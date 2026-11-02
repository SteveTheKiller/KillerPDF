using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private CancellationTokenSource? _visibleRefineCts;
    private readonly Dictionary<Image, Image> _visibleDetails = [];
    internal Task VisibleRefinementTask { get; private set; } = Task.CompletedTask;
    internal string LastVisibleRefinementError { get; private set; } = string.Empty;

    private void CancelVisibleRefinement()
    {
        CancelAndRelease(_visibleRefineCts);
        _visibleRefineCts = null;
        foreach (var detail in _visibleDetails.Values)
        {
            detail.Source = null;
            if (detail.Parent is Grid grid) grid.Children.Remove(detail);
        }
        _visibleDetails.Clear();
    }

    private void RemoveVisibleDetail(Image image)
    {
        if (!_visibleDetails.Remove(image, out var detail)) return;
        detail.Source = null;
        if (detail.Parent is Grid grid) grid.Children.Remove(detail);
    }

    private void RefineVisibleRegions() => VisibleRefinementTask = RefineVisibleAsync();

    private async Task RefineVisibleAsync()
    {
        CancelVisibleRefinement();
        LastVisibleRefinementError = string.Empty;
        if (_currentFile is null || _doc is null) return;
        var cts = _visibleRefineCts = new CancellationTokenSource();
        var token = cts.Token;
        var session = _active;
        string file = _currentFile;
        long revision = session?.RenderRevision ?? 0;
        bool invert = DocInvert;
        var mode = _viewMode;
        double zoom = _zoomLevel;
        var request = _backgroundRenderCache.Capture(file, revision, _protection);
        var dpi = VisualTreeHelper.GetDpi(this);
        bool Current() => !token.IsCancellationRequested && ReferenceEquals(session, _active)
            && file == _currentFile && revision == (_active?.RenderRevision ?? 0) && invert == DocInvert
            && mode == _viewMode && zoom == _zoomLevel;
        var images = new List<(int Page, Image Image)>();
        if (_viewMode == ViewMode.Continuous)
        {
            for (int p = 0; p < _continuousPanel.Children.Count; p++)
                if (_continuousPanel.Children[p] is Border { Child: Grid g }
                    && g.Children.Count > 0 && g.Children[0] is Image image) images.Add((p, image));
        }
        else if (_viewMode is ViewMode.Single or ViewMode.TwoPage)
        {
            if (_annotationCanvas.Tag is int page) images.Add((page, PageImage));
            foreach (var pair in _continuousCanvases)
                if (pair.Value.Parent is Grid g && g.Children.Count > 0 && g.Children[0] is Image image)
                    images.Add((pair.Key, image));
        }
        try
        {
            foreach (var (page, image) in images)
            {
                if (!Current()) return;
                if (image.Source is not BitmapSource source || image.Parent is not Grid grid
                    || image.ActualWidth <= 0 || image.ActualHeight <= 0) continue;
                var transform = image.TransformToAncestor(PagePreviewPanel);
                var inverse = transform.Inverse;
                if (inverse is null) continue;
                Rect visible = inverse.TransformBounds(new Rect(0, 0,
                    PagePreviewPanel.ViewportWidth, PagePreviewPanel.ViewportHeight));
                visible.Intersect(new Rect(0, 0, image.ActualWidth, image.ActualHeight));
                if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) continue;
                int rotation = _pageRotations.TryGetValue(page, out int rot) ? rot : 0;
                double deviceWidth = transform.TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight)).Width * dpi.DpiScaleX;
                double deviceHeight = transform.TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight)).Height * dpi.DpiScaleY;
                int maximum = Math.Clamp((int)Math.Ceiling(Math.Max(deviceWidth, deviceHeight)), 1, 6144);
                // Same 5% hysteresis as the 1.8 re-sharpen: a base render within 5% of the drawn
                // size shows no visible softness, and re-rendering it only delays the settled view.
                if (maximum <= Math.Max(source.PixelWidth, source.PixelHeight) * 1.05) continue;
                double l = visible.Left / image.ActualWidth, t = visible.Top / image.ActualHeight;
                double r = visible.Right / image.ActualWidth, b = visible.Bottom / image.ActualHeight;
                // Reserve at most one bounded region result; its raster is never cached as a full page.
                var result = await _viewportRenderQueue.Enqueue((request, "visible", page), 20,
                    Math.Min(8L * maximum * maximum, 512L << 20), workToken =>
                    {
                        using var lease = request.Rent(workToken);
                        var dimensions = lease.Dimensions(page, maximum, maximum);
                        var region = VisibleRenderRegion.Create(dimensions.Width, dimensions.Height, rotation, l, t, r, b)
                            ?? throw new OperationCanceledException();
                        var rendered = lease.RenderRegion(page, maximum, maximum,
                            region.Left, region.Top, region.Width, region.Height, workToken);
                        byte[] pixels = rendered.Pixels;
                        if (invert)
                        {
                            PdfContentDocument? content = null;
                            try
                            {
                                var keep = ImageRectsFor(file, request.Protection, page, ref content).Select(box => new BitmapHelpers.FracRect(
                                    (box.L * dimensions.Width - region.Left) / region.Width,
                                    (box.T * dimensions.Height - region.Top) / region.Height,
                                    (box.R * dimensions.Width - region.Left) / region.Width,
                                    (box.B * dimensions.Height - region.Top) / region.Height)).ToArray();
                                BitmapHelpers.InvertBgraInPlaceExcept(pixels, rendered.Width, rendered.Height, keep);
                            }
                            finally { content?.Dispose(); }
                        }
                        int w = rendered.Width, h = rendered.Height;
                        if (rotation != 0) (pixels, w, h) = BitmapHelpers.RotateBitmap(pixels, w, h, rotation);
                        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                        bitmap.Freeze();
                        RememberRenderWarning(bitmap, rendered);
                        return (bitmap, region);
                    }, token);
                if (!Current() || !ReferenceEquals(image.Source, source)) return;
                PresentRenderWarning(image, result.bitmap, page);
                var detail = new Image
                {
                    Source = result.bitmap, IsHitTestVisible = false, Stretch = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Width = image.ActualWidth * result.bitmap.PixelWidth / result.region.DisplayWidth,
                    Height = image.ActualHeight * result.bitmap.PixelHeight / result.region.DisplayHeight,
                    Margin = new Thickness(image.ActualWidth * result.region.DisplayLeft / result.region.DisplayWidth,
                        image.ActualHeight * result.region.DisplayTop / result.region.DisplayHeight, 0, 0)
                };
                // Keep the annotation canvas at index 1; its input and coordinate mapping stay intact.
                Panel.SetZIndex(detail, -1);
                Panel.SetZIndex(image, -2);
                grid.Children.Add(detail);
                _visibleDetails[image] = detail;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastVisibleRefinementError = ex.ToString(); }
    }
}
