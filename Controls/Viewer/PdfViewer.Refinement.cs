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
        // Hidden, not torn down: the next refinement reuses the detail window and its presenter, so each settle after
        // a scroll or zoom does not create a new device and swap chain. A display change tears it down.
        _primaryViewport?.HideDetail();
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
        var proof = session?.ProofView.CaptureProof();
        string file = _currentFile;
        long revision = session?.RenderRevision ?? 0;
        bool invert = DocInvert;
        var mode = _viewMode;
        double zoom = _zoomLevel;
        var display = _presentationDisplay;
        // Every refined tile gets the same monitor conversion as its base page, in every mode.
        var monitor = !invert && display is not null
            && KillerPdf.Engine.Rendering.PdfDisplayPresentationPolicy.Resolve(display).TransformOwner
                == KillerPdf.Engine.Rendering.PdfMonitorTransformOwner.Application ? _sdrDisplayConverter : null;
        var request = _backgroundRenderCache.Capture(file, revision, _protection);
        bool Current() => !token.IsCancellationRequested && ReferenceEquals(session, _active)
            && file == _currentFile && revision == (_active?.RenderRevision ?? 0) && invert == DocInvert
            && mode == _viewMode && zoom == _zoomLevel && ReferenceEquals(display, _presentationDisplay)
            && proof?.Stamp == _active?.ProofView.CaptureProof().Stamp;
        var images = new List<(int Page, Image Image)>();
        if (_viewMode.IsContinuous())
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
                if (ReferenceEquals(image, PageImage) && _primaryPresentationState is not null
                    && !invert && (!_pageRotations.TryGetValue(page, out int nativeRotation) || nativeRotation == 0))
                {
                    await RefinePrimaryFrameAsync(request, page, image, source, proof?.Settings, Current, token);
                    continue;
                }
                // The retained native overlay and scroll viewport can have different visual roots.
                Point viewportStart = image.PointFromScreen(PagePreviewPanel.PointToScreen(default));
                Point viewportEnd = image.PointFromScreen(PagePreviewPanel.PointToScreen(new Point(
                    PagePreviewPanel.ViewportWidth, PagePreviewPanel.ViewportHeight)));
                Rect visible = new(viewportStart, viewportEnd);
                visible.Intersect(new Rect(0, 0, image.ActualWidth, image.ActualHeight));
                if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) continue;
                int rotation = _pageRotations.TryGetValue(page, out int rot) ? rot : 0;
                // Conversion happens before rotation, so rotated pages are converted like upright ones.
                var regionMonitor = monitor;
                Point imageStart = image.PointToScreen(default);
                Point imageEnd = image.PointToScreen(new Point(image.ActualWidth, image.ActualHeight));
                double deviceWidth = Math.Abs(imageEnd.X - imageStart.X);
                double deviceHeight = Math.Abs(imageEnd.Y - imageStart.Y);
                int maximum = Math.Clamp((int)Math.Ceiling(Math.Max(deviceWidth, deviceHeight)), 1, 6144);
                // Same 5% hysteresis as the 1.8 re-sharpen: a base render within 5% of the drawn
                // size shows no visible softness, and re-rendering it only delays the settled view.
                if (maximum <= Math.Max(source.PixelWidth, source.PixelHeight) * 1.05) continue;
                double l = visible.Left / image.ActualWidth, t = visible.Top / image.ActualHeight;
                double r = visible.Right / image.ActualWidth, b = visible.Bottom / image.ActualHeight;
                // Reserve at most one bounded region result; its raster is never cached as a full page.
                var result = await _viewportRenderQueue.Enqueue((request, "visible", page), 20,
                    Math.Min((proof?.Settings.Enabled == true ? 64L : regionMonitor is null ? 8L : 40L)
                        * maximum * maximum, 512L << 20), workToken =>
                    {
                        using var lease = request.Rent(workToken);
                        var dimensions = lease.Dimensions(page, maximum, maximum);
                        var region = VisibleRenderRegion.Create(dimensions.Width, dimensions.Height, rotation, l, t, r, b)
                            ?? throw new OperationCanceledException();
                        var viewing = KillerPDF.Presentation.PdfViewingFrameConversion.Render(
                            requested => lease.RenderFrameRegion(page, maximum, maximum, region, workToken, requested),
                            () => lease.RenderRegion(page, maximum, maximum,
                                region.Left, region.Top, region.Width, region.Height, workToken),
                            proof?.Settings, regionMonitor, workToken);
                        byte[] pixels = viewing.Pixels;
                        int w = region.Width, h = region.Height;
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
                                BitmapHelpers.InvertBgraInPlaceExcept(pixels, w, h, keep);
                            }
                            finally { content?.Dispose(); }
                        }
                        if (rotation != 0) (pixels, w, h) = BitmapHelpers.RotateBitmap(pixels, w, h, rotation);
                        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                        bitmap.Freeze();
                        RememberRenderWarning(bitmap, viewing);
                        return (bitmap, region);
                    }, token);
                if (!Current() || !ReferenceEquals(image.Source, source)) return;
                PresentRenderWarning(image, RenderWarnings.TryGetValue(result.bitmap, out _)
                    ? result.bitmap : source, page);
                var detail = new Image
                {
                    Source = result.bitmap, IsHitTestVisible = false, Stretch = Stretch.Fill,
                    UseLayoutRounding = false,
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
