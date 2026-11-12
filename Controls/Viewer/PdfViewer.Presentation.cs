using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.ColorManagement;
using KillerPDF.Presentation;
using KillerPDF.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    /// <summary>Gets or sets an optional platform presenter factory, configured before opening a document.</summary>
    public Func<nint, IPdfFramePresenter>? FramePresenterFactory { get; set; }

    /// <summary>Gets or sets optional platform display observation, configured before opening a document.</summary>
    public Func<nint, PdfDisplayDiscoverySnapshot>? DisplayCapture { get; set; }

    /// <summary>Gets or sets an optional platform profile reader, configured before opening a document.</summary>
    public Func<string, CancellationToken, PdfColorProfile>? DisplayProfileReader { get; set; }

    // The native child window hides everything the main window floats over the page, so 1.9.3 never creates it. The
    // presenter code stays for 1.9.4, which hosts those bars in popups first. A field rather than a constant so the
    // code behind it stays compiled.
    internal static readonly bool NativePrimaryViewportEnabled = false;

    private WindowsPdfDisplayDiscovery? _presentationDisplayObserver;
    private PdfDisplayDiscoverySnapshot? _presentationDisplay;
    private PdfSdrDisplayConverter? _sdrDisplayConverter;
    private WindowsPdfViewportHost? _primaryViewport;
    private (nint Handle, int Width, int Height) _primarySurface;
    private long _primarySurfaceGeneration, _presentationRequestSequence;
    private PdfDocumentProofViewStamp? _primaryProofStamp;
    private long _primaryProofGeneration;
    private PdfPresentationState? _primaryPresentationState;

    // One immutable observation stands for "no window yet", so renders started before the
    // window exists all see the same display and do not invalidate each other.
    private static readonly PdfDisplayDiscoverySnapshot NotAttachedDisplay = new(0, PdfDisplayDiscoveryStatus.Unavailable,
        null, null, "ViewportWindowNotAttached", null);
    private bool _presentationRerenderPending, _presentationVisibilityObserved;

    private PdfDisplayDiscoverySnapshot CapturePrimaryDisplay()
    {
        nint window = Window.GetWindow(this) is { } owner ? new WindowInteropHelper(owner).Handle : 0;
        if (window == 0)
            return ReferenceEquals(_presentationDisplay, NotAttachedDisplay)
                ? NotAttachedDisplay : ObservePresentationDisplay(NotAttachedDisplay);
        if (_presentationDisplayObserver is null)
        {
            _presentationDisplayObserver = new WindowsPdfDisplayDiscovery(window);
            _presentationDisplayObserver.Changed += (_, _) =>
            {
                if (_presentationDisplayObserver is not { } observer) return;
                var observed = DisplayCapture is { } capture ? capture(window) : observer.Capture();
                if (ReferenceEquals(observed, _presentationDisplay)) return;
                // Windows broadcasts settings changes for many reasons besides color. When pages would look the same on
                // the new observation, the one they were drawn for stays current: they do not go blank and draw again,
                // and renders still in progress for it keep going.
                if (_presentationDisplay is { } shown && _sdrDisplayConverter is { } converter
                    && converter.PresentsAlike(shown, observed)) return;
                ObservePresentationDisplay(observed);
                if (!IsLoaded || !IsVisible || !HasReadDocument)
                {
                    // Pages converted for the old display are redrawn when the viewer is shown again.
                    _presentationRerenderPending = true;
                    return;
                }
                RerenderForDisplayChange();
            };
            _presentationDisplayObserver.ObserveChanges();
            _presentationDisplayObserving = true;
            if (!_presentationVisibilityObserved)
            {
                _presentationVisibilityObserved = true;
                IsVisibleChanged += (_, _) =>
                {
                    if (!_presentationRerenderPending || !IsLoaded || !IsVisible || !HasReadDocument) return;
                    RerenderForDisplayChange();
                };
            }
        }
        if (DisplayCapture is { } captureDisplay) return ObservePresentationDisplay(captureDisplay(window));
        // A full capture queries the display configuration and the monitor profile, about 11 ms at the median on a
        // two-monitor desktop, on the UI thread for every render pass. The observer captures again on activation,
        // movement, showing and settings messages, and its Changed handler above takes in an observation that changes
        // how pages look, so a render uses the observation taken in.
        if (!_presentationDisplayObserving) return ObservePresentationDisplay(_presentationDisplayObserver.Capture());
        return _presentationDisplay is { } current && !ReferenceEquals(current, NotAttachedDisplay)
            ? current : ObservePresentationDisplay(_presentationDisplayObserver.Status);
    }

    private bool _presentationDisplayObserving;

    private void RerenderForDisplayChange()
    {
        _presentationRerenderPending = false;
        CancelVisibleRefinement();
        // The detail presenter was made for the old display.
        _primaryViewport?.ClearDetail();
        if (_viewMode.IsContinuous())
        {
            CancelAndRelease(_continuousRenderCts);
            _continuousRenderCts = null;
            foreach (var slot in _continuousPanel.Children.OfType<Border>())
                if (slot.Child is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Image image)
                {
                    image.Source = null;
                    image.ToolTip = null;
                }
            _continuousSharpPages.Clear();
            _ = RenderContinuousPages(State.CurrentPage);
            StartRerenderTimer();
        }
        else RenderPage(_viewMode == ViewMode.Grid ? 0 : _currentPage);
    }

    private PdfDisplayDiscoverySnapshot ObservePresentationDisplay(PdfDisplayDiscoverySnapshot display)
    {
        _sdrDisplayConverter ??= new((name, token) => DisplayProfileReader is { } reader
            ? reader(name, token) : WindowsPdfColorProfileReader.Load(name, token));
        _sdrDisplayConverter.UpdateDisplay(display);
        return _presentationDisplay = display;
    }

    private void ShowPrimaryCompatibility()
    {
        _primaryPresentationState = null;
        _primaryViewport?.ClearDetail();
        if (State.PageImage is { } image) image.Visibility = Visibility.Visible;
        if (_primaryViewport?.OverlayRoot is Panel panel) panel.Background = Brushes.White;
        // Highlighter marks show the page image through them; with it shown again they can.
        HighlighterLook.Refresh(_annotationCanvas);
    }

    private long CapturePrimaryProofGeneration(PdfDocumentProofViewSnapshot? proof)
    {
        if (_primaryProofStamp != proof?.Stamp)
        {
            _primaryProofGeneration = checked(_primaryProofGeneration + 1);
            _primaryProofStamp = proof?.Stamp;
        }
        return _primaryProofGeneration;
    }

    private async Task PresentPrimaryFrameAsync(PdfRenderedFrame frame, PdfDisplayDiscoverySnapshot display,
        PdfDisplayPresentationDecision policy, int page, long revision, long renderGeneration, long proofGeneration,
        Func<bool> current, CancellationToken token)
    {
        if (!current()) return;
        if (PageImage.Source is not { } source) return;
        if (_nativeViewportUnavailable)
        {
            ShowPresentationFallback(page);
            return;
        }
        try
        {
            await PresentPrimaryFrameCoreAsync(frame, source, display, policy, page, revision, renderGeneration,
                proofGeneration, current, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The sRGB bitmap is already rendered. If the native viewport cannot be built or attached,
            // give the page back to its tile instead of leaving an empty native host on screen.
            if (!current()) return;
            if (_primaryViewport is { Viewport: 0 } host && host.Parent is Border border)
            {
                _primaryViewport = null;
                border.Child = null;
                border.Child = host.Abandon();
                _nativeViewportUnavailable = true;
            }
            ShowPrimaryCompatibility();
            ShowPresentationFallback(page);
        }
    }

    private bool _nativeViewportUnavailable;

    private void ShowPresentationFallback(int page)
    {
        string notice = string.Format(Loc("Str_PageOf"), page + 1, ReadPageTotal) + ". " + Loc("Str_CM_DisplayFallback");
        PageImage.Visibility = Visibility.Visible;
        PageImage.ToolTip = notice;
        HighlighterLook.Refresh(_annotationCanvas);
        if (page == _currentPage && (Host is null || Host.IsViewerFocused(this))) SetStatus(notice);
    }

    private async Task PresentPrimaryFrameCoreAsync(PdfRenderedFrame frame, ImageSource source,
        PdfDisplayDiscoverySnapshot display, PdfDisplayPresentationDecision policy, int page, long revision,
        long renderGeneration, long proofGeneration, Func<bool> current, CancellationToken token)
    {
        if (_primaryViewport is null)
        {
            if (PageImage.Parent is not Grid grid || grid.Parent is not Border border)
                throw new InvalidOperationException("The primary page does not have its owning tile.");
            border.Child = null;
            grid.Background = Brushes.White;
            _primaryViewport = new WindowsPdfViewportHost(grid,
                window => FramePresenterFactory?.Invoke(window) ?? new WindowsPdfFramePresenter(window));
            border.Child = _primaryViewport;
        }
        _primaryViewport.Width = source.Width;
        _primaryViewport.Height = source.Height;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded);
        token.ThrowIfCancellationRequested();
        if (!current()) return;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_primaryViewport.Viewport == 0 && DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (!current()) return;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded);
            await Task.Delay(10, token);
        }
        if (!current()) return;
        if (_primaryViewport.Viewport == 0) throw new InvalidOperationException("The primary viewport did not attach.");
        var size = _primaryViewport.PixelSize;
        var surface = (_primaryViewport.Viewport, size.Width, size.Height);
        if (surface != _primarySurface)
        {
            _primarySurface = surface;
            _primarySurfaceGeneration = checked(_primarySurfaceGeneration + 1);
        }
        Guid document = _nativeDoc?.CaptureReadRevision().Id ?? _doc!.CaptureReadRevision().Id;
        var stamp = new PdfPresentationStamp(document, revision, renderGeneration, proofGeneration, display.Generation,
            _primarySurfaceGeneration, checked(++_presentationRequestSequence), page, 1);
        var presenter = _primaryViewport.GetPresenter();
        var state = new PdfPresentationState(stamp, frame.Width, frame.Height, policy.ReferenceWhiteScale, policy.TransformOwner);
        presenter.UpdateState(state);
        var result = await presenter.SubmitAsync(new(frame, stamp), token);
        if (!current()) return;
        if (result.Outcome == PdfFrameSubmissionOutcome.Submitted)
        {
            _primaryPresentationState = state;
            PageImage.Visibility = Visibility.Hidden;
            ((Panel)_primaryViewport.OverlayRoot).Background = Brushes.Transparent;
            // The presenter draws the page now and highlighter marks cannot show it through them; until the presenter
            // blends them itself, they fall back to the translucent look instead of an opaque bar.
            HighlighterLook.Refresh(_annotationCanvas);
        }
        else if (result.Outcome == PdfFrameSubmissionOutcome.Unavailable)
        {
            ShowPrimaryCompatibility();
            ShowPresentationFallback(page);
        }
    }

    private async Task RefinePrimaryFrameAsync(IVisibleRegionSource request, int page,
        Image image, System.Windows.Media.Imaging.BitmapSource source, PdfDocumentProofSettings? proof,
        Func<bool> current, CancellationToken token)
    {
        if (_primaryViewport is not { } host || _primaryPresentationState is not { } baseline
            || baseline.Stamp.PageIndex != page) return;
        Point origin = image.PointToScreen(default);
        Point far = image.PointToScreen(new Point(image.ActualWidth, image.ActualHeight));
        var drawn = new Rect(origin, far);
        var visible = new Rect(PagePreviewPanel.PointToScreen(default), PagePreviewPanel.PointToScreen(
            new Point(PagePreviewPanel.ViewportWidth, PagePreviewPanel.ViewportHeight)));
        visible.Intersect(drawn);
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return;
        int maximum = Math.Clamp((int)Math.Ceiling(Math.Max(drawn.Width, drawn.Height)), 1, 6144);
        if (maximum <= Math.Max(source.PixelWidth, source.PixelHeight) * 1.05) return;
        double left = (visible.Left - drawn.Left) / drawn.Width, top = (visible.Top - drawn.Top) / drawn.Height;
        double right = (visible.Right - drawn.Left) / drawn.Width, bottom = (visible.Bottom - drawn.Top) / drawn.Height;
        var result = await _viewportRenderQueue.Enqueue((request, "visible-float", page), 20,
            Math.Min((proof?.Enabled == true ? 64L : 40L) * (long)Math.Ceiling(visible.Width + 2)
                * (long)Math.Ceiling(visible.Height + 2) + request.RegionWorkingBytes(page), 512L << 20), workToken =>
            {
                using var lease = request.RentRegion(workToken);
                var dimensions = lease.Dimensions(page, maximum, maximum);
                var region = VisibleRenderRegion.Create(dimensions.Width, dimensions.Height, 0, left, top, right, bottom)
                    ?? throw new OperationCanceledException();
                return (Frame: lease.RenderFrameRegion(page, maximum, maximum, region, workToken, proof), Region: region);
            }, token);
        bool Current() => current() && ReferenceEquals(host, _primaryViewport)
            && ReferenceEquals(baseline, _primaryPresentationState) && ReferenceEquals(image.Source, source)
            && baseline.Stamp.DisplayGeneration == _presentationDisplay?.Generation;
        if (!Current()) return;
        var regionBounds = new Rect((double)result.Region.DisplayLeft / result.Region.DisplayWidth,
            (double)result.Region.DisplayTop / result.Region.DisplayHeight,
            (double)result.Frame.Width / result.Region.DisplayWidth, (double)result.Frame.Height / result.Region.DisplayHeight);
        var presenter = host.PrepareDetail(regionBounds);
        var stamp = baseline.Stamp with { SurfaceGeneration = checked(++_primarySurfaceGeneration),
            RequestSequence = checked(++_presentationRequestSequence), RegionId = 2 };
        presenter.UpdateState(new(stamp, result.Frame.Width, result.Frame.Height,
            baseline.ReferenceWhiteScale, baseline.TransformOwner));
        PdfFrameSubmissionResult submitted;
        try { submitted = await presenter.SubmitAsync(new(result.Frame, stamp), token); }
        catch (Exception error) when (error is not OperationCanceledException && ReferenceEquals(host, _primaryViewport))
        {
            // A failed submission releases the reused detail window and presenter at once.
            host.ClearDetail();
            throw;
        }
        if (!Current()) return;
        if (submitted.Outcome == PdfFrameSubmissionOutcome.Submitted) host.ShowDetail();
        else if (submitted.Outcome == PdfFrameSubmissionOutcome.Unavailable) host.ClearDetail();
        else host.HideDetail();
    }

    private void ReleasePresentationResources()
    {
        _presentationDisplayObserver?.Dispose();
        _presentationDisplayObserver = null;
        _primaryViewport?.Dispose();
        _primaryViewport = null;
        _nativeViewportUnavailable = false;
        _presentationRerenderPending = false;
        _presentationDisplay = null;
        _sdrDisplayConverter = null;
        _primaryPresentationState = null;
    }
}
