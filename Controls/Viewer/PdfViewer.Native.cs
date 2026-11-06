using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using System.Windows;
using System.Windows.Controls;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private NativeWorkingDocument? _nativeDoc;
    private long _nativeNavigationRequest;

    internal NativeWorkingDocument? NativeDocumentRef { get => _nativeDoc; set => _nativeDoc = value; }
    private bool HasReadDocument => _doc is not null || _nativeDoc is not null;
    internal bool HasReadDocumentExt => HasReadDocument;
    private int ReadPageCount => _nativeDoc?.PageCount ?? _doc?.PageCount ?? 0;

    internal async Task RevealNativeNavigationExt(NativeWorkingDocument owner, PdfReadNavigationDestination destination)
    {
        long request = ++_nativeNavigationRequest;
        var session = _active;
        var mode = _viewMode;
        MarkViewportIntent();
        long intent = session?.ViewportIntentRevision ?? 0;
        bool Current() => request == _nativeNavigationRequest && IsLoaded
            && ReferenceEquals(owner, _nativeDoc) && ReferenceEquals(session, _active)
            && _currentPage == destination.PageIndex && _viewMode == mode
            && (session?.ViewportIntentRevision ?? 0) == intent && Host?.IsViewerFocused(this) != false;
        try
        {
            await PrimaryRenderTask;
            Canvas? canvas = null;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (Current())
            {
                await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                if (!Current()) return;
                canvas = VisibleCanvasForPage(destination.PageIndex);
                if (canvas is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }) break;
                if (deadline.Elapsed > TimeSpan.FromSeconds(3))
                    throw new InvalidOperationException("The native navigation page did not become available.");
                await Task.Delay(25);
            }
            if (!Current() || canvas is null) return;
            var geometry = owner.GetNativePageGeometry(destination.PageIndex);
            double width = geometry.Width, height = geometry.Height;
            if (geometry.IntrinsicRotation is 90 or 270) (width, height) = (height, width);
            if (!double.IsFinite(destination.Left) || !double.IsFinite(destination.Top))
                throw new InvalidOperationException("The native navigation position is invalid.");
            var local = new Point(Math.Clamp(destination.Left, 0, width) * canvas.ActualWidth / width,
                Math.Clamp(destination.Top, 0, height) * canvas.ActualHeight / height);
            Point visible = PagePreviewPanel.PointFromScreen(canvas.PointToScreen(local));
            double horizontal = PagePreviewPanel.HorizontalOffset;
            if (visible.X < 0) horizontal += visible.X;
            else if (visible.X > PagePreviewPanel.ViewportWidth)
                horizontal += visible.X - PagePreviewPanel.ViewportWidth;
            _sidebarSelectionPinned = destination.PageIndex;
            PagePreviewPanel.ScrollToHorizontalOffset(horizontal);
            PagePreviewPanel.ScrollToVerticalOffset(PagePreviewPanel.VerticalOffset + visible.Y);
            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.Loaded);
            if (Current()) CaptureActiveIfAny();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (Current()) SetStatus(string.Format(Loc("Str_Pages_Failed"), error.Message));
        }
    }

    internal static KillerPdf.Engine.Rendering.PdfRenderedFrame RenderNativeFrame(
        PdfCapturedReadRevision revision, int page, int maximumWidth, int maximumHeight,
        CancellationToken cancellationToken, KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
    {
        var geometry = revision.GetPageGeometry(page);
        double width = geometry.Width, height = geometry.Height;
        if (geometry.Rotation is 90 or 270) (width, height) = (height, width);
        double scale = Math.Min(maximumWidth / width, maximumHeight / height);
        int pixelsWide = Math.Clamp((int)Math.Round(width * scale), 1, maximumWidth);
        int pixelsHigh = Math.Clamp((int)Math.Round(height * scale), 1, maximumHeight);
        using var lease = revision.Rent(cancellationToken);
        return lease.RenderFrame(page, new KillerPdf.Engine.Rendering.PdfRenderOptions(
            pixelsWide, pixelsHigh, maximumPixelBytes: (proof?.Enabled == true ? 256L : 128L) << 20) { Proof = proof });
    }

    private PdfPageInformation ReadPageInformation(int page)
        => _nativeDoc is not null ? _nativeDoc.CaptureReadRevision().GetPageGeometry(page)
            : EnsureEngineDocumentSession().Pages[page];

    private (double Width, double Height) ReadVisualPageSize(int page)
    {
        if (_nativeDoc is null) return EnsureEngineDocumentSession().VisualPageSize(page, _pageRotations);
        PdfPageInformation geometry = ReadPageInformation(page);
        int extra = _pageRotations.TryGetValue(page, out int rotation) ? rotation : 0;
        int total = ((geometry.Rotation + extra) % 360 + 360) % 360;
        return total is 90 or 270
            ? (geometry.Height, geometry.Width) : (geometry.Width, geometry.Height);
    }

    private (double Width, double Height) ReadPhysicalPageSize(int page)
    {
        if (_nativeDoc is null) return EnsureEngineDocumentSession().PhysicalPageSize(page, _pageRotations);
        var (width, height) = ReadVisualPageSize(page);
        double unit = ReadPageInformation(page).UserUnit;
        return (width * unit, height * unit);
    }
}
