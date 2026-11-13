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
            if (detail.Parent is Grid grid)
            {
                grid.Children.Remove(detail);
                RefreshHighlighterMarks(grid);
            }
        }
        _visibleDetails.Clear();
    }

    private void RemoveVisibleDetail(Image image)
    {
        if (!_visibleDetails.Remove(image, out var detail)) return;
        detail.Source = null;
        if (detail.Parent is Grid grid)
        {
            grid.Children.Remove(detail);
            RefreshHighlighterMarks(grid);
        }
    }

    // Highlighter marks show the page and its zoom detail through them, so a detail that arrives or leaves rebuilds the
    // marks on that page.
    private static void RefreshHighlighterMarks(Grid grid)
    {
        foreach (var canvas in grid.Children.OfType<Canvas>()) HighlighterLook.Refresh(canvas);
    }

    /// <summary>Rebuilds every highlighter mark in this viewer for the current dark-mode setting.</summary>
    internal void RefreshHighlighterMarks()
    {
        // The dark look is asked only for a canvas that holds a mark: it can read the document for kept pictures.
        // One open of the document serves every marked page, and it is released before this returns.
        Services.PdfContentDocument? content = null;
        try
        {
            if (_annotationCanvas.Tag is int primary) HighlighterLook.Refresh(_annotationCanvas, () => HighlighterDarkLook(primary, ref content));
            foreach (var (page, canvas) in _continuousCanvases) HighlighterLook.Refresh(canvas, () => HighlighterDarkLook(page, ref content));
        }
        finally { content?.Dispose(); }
    }

    /// <summary>
    /// How dark mode shows <paramref name="page"/> under a highlighter mark. A PDF page is inverted except its pictures,
    /// unless images invert too; a book page is inverted only when images invert too. Pictures come from the same cached
    /// boxes the page render keeps, turned with the page.
    /// </summary>
    private HighlighterLook.Dark HighlighterDarkLook(int page, ref Services.PdfContentDocument? content)
    {
        if (!DocInvert) return HighlighterLook.Dark.Off;
        if (_nativeDoc is not null) return BitmapHelpers.DocInvertImages ? new(true, []) : HighlighterLook.Dark.Off;
        if (BitmapHelpers.DocInvertImages || _currentFile is null) return new(true, []);
        var boxes = ImageRectsFor(_currentFile, _protection, page, ref content);
        int rotation = _pageRotations.TryGetValue(page, out int turned) ? turned : 0;
        return new(true, [.. boxes.Select(box => HighlighterLook.KeptOnPage(box.L, box.T, box.R, box.B, rotation))]);
    }

    private HighlighterLook.Dark HighlighterDarkLook(int page)
    {
        Services.PdfContentDocument? content = null;
        try { return HighlighterDarkLook(page, ref content); }
        finally { content?.Dispose(); }
    }

    private void RefineVisibleRegions() => VisibleRefinementTask = RefineVisibleAsync();

    // Refines once the pages being drawn again have landed, while the view is still the one they were drawn for.
    private async Task RefineAfterAsync(Task drawing)
    {
        var mode = _viewMode;
        var session = _active;
        // A failed redraw reports itself; the pages it left are still refined, and this task never faults.
        try { await drawing; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        if (_viewMode != mode || !ReferenceEquals(session, _active) || !HasReadDocument
            || Host?.IsViewerFocused(this) == false) return;
        RefineVisibleRegions();
        await VisibleRefinementTask;
    }

    private async Task RefineVisibleAsync()
    {
        CancelVisibleRefinement();
        LastVisibleRefinementError = string.Empty;
        if (_currentFile is null || !HasReadDocument) return;
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
        // A book draws its tiles from its captured revision, a PDF from the background render cache.
        var nativeOwner = _nativeDoc;
        var pdfRequest = nativeOwner is null ? _backgroundRenderCache.Capture(file, revision, _protection) : null;
        IVisibleRegionSource regionSource = (IVisibleRegionSource?)pdfRequest ?? nativeOwner!.CaptureReadRevision();
        bool invertImages = BitmapHelpers.DocInvertImages;
        bool Current() => !token.IsCancellationRequested && ReferenceEquals(session, _active)
            && ReferenceEquals(nativeOwner, _nativeDoc)
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
                    await RefinePrimaryFrameAsync(regionSource, page, image, source, proof?.Settings, Current, token);
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
                if (VisibleDetail.DetailMaximum(deviceWidth, deviceHeight, source.PixelWidth, source.PixelHeight)
                    is not { } maximum) continue;
                double l = visible.Left / image.ActualWidth, t = visible.Top / image.ActualHeight;
                double r = visible.Right / image.ActualWidth, b = visible.Bottom / image.ActualHeight;
                // Dark mode inverts a tile as its base page: a PDF keeps its images unless images invert too, and a
                // book page, all image to the viewer, inverts only when images do.
                Func<(int Width, int Height), VisibleRenderRegion, BitmapHelpers.FracRect[]>? invertKeep = !invert ? null
                    : pdfRequest is null ? invertImages ? (_, _) => [] : null
                    : (dimensions, region) =>
                    {
                        PdfContentDocument? content = null;
                        try
                        {
                            return ImageRectsFor(file, pdfRequest.Protection, page, ref content).Select(box => new BitmapHelpers.FracRect(
                                (box.L * dimensions.Width - region.Left) / region.Width,
                                (box.T * dimensions.Height - region.Top) / region.Height,
                                (box.R * dimensions.Width - region.Left) / region.Width,
                                (box.B * dimensions.Height - region.Top) / region.Height)).ToArray();
                        }
                        finally { content?.Dispose(); }
                    };
                // Reserve at most one bounded region result; its raster is never cached as a full page.
                var result = await _viewportRenderQueue.Enqueue((regionSource, "visible", page), 20,
                    regionSource.RegionReservation(page, maximum, proof?.Settings.Enabled == true, regionMonitor is not null),
                    workToken =>
                    {
                        var tile = VisibleDetail.Render(regionSource, page, maximum, rotation, l, t, r, b,
                            proof?.Settings, regionMonitor, invertKeep, workToken);
                        var bitmap = BitmapSource.Create(tile.Width, tile.Height, 96, 96, PixelFormats.Bgra32, null,
                            tile.Pixels, tile.Width * 4);
                        bitmap.Freeze();
                        RememberRenderWarning(bitmap, tile.Viewing);
                        return (bitmap, region: tile.Region);
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
                RefreshHighlighterMarks(grid);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastVisibleRefinementError = ex.ToString(); }
    }
}
