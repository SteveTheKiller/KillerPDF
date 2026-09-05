using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // Wheel zoom, wheel scroll, and the pointer gestures that start on the page surface.
    //
    // Moved from Shell/Zoom.cs; this namespace and class line are the only changes. It lives with
    // the render pipeline because the two share the zoom state and the gesture routing
    // (_activeCanvas / _gestureCanvas) that decides which page a press landed on.
    //
    // Window members referenced bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        private readonly WheelPageFlipGate _wheelPageFlipGate = new();
        private bool _pinchZooming;

        // ============================================================
        // Zoom
        // ============================================================

        // internal: PdfViewer's XAML binds this and forwards to it.
        internal void PagePreview_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // A form field list keeps the wheel while it still has rows to reach. Its overlay
            // sits inside PagePreviewPanel, so this tunnelling handler would otherwise scroll
            // the page out from under a list the user is reading, and a field taller than its
            // box could never be scrolled at all.
            if (FieldListWantsWheel(e.OriginalSource as DependencyObject, e.Delta)) return;

            // #209: match the standard Windows/browser gesture and reuse the same path as a
            // physical tilt wheel. Wheel-down moves right; wheel-up moves left.
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                e.Handled = true;
                ScrollHorizontalExt(-e.Delta);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (_viewMode == ViewMode.Grid) { GridZoomStep(e.Delta < 0); return; }

                // Capture cursor position and scroll offsets BEFORE zoom changes so we can
                // compute the new offsets that keep the point under the cursor stationary.
                Point cursorInViewport = e.GetPosition(PagePreviewPanel);
                double oldZoom = _zoomLevel;
                double oldHOff = PagePreviewPanel.HorizontalOffset;
                double oldVOff = PagePreviewPanel.VerticalOffset;

                // Smooth wheel zoom. Two parts:
                // 1) A multiplicative step - every notch changes the zoom by the same RATIO. The
                //    old additive ZoomStep was a ~50% jump when zoomed out and barely visible when
                //    zoomed in. The exponent scales with e.Delta, so a precision touchpad's small
                //    frequent deltas produce proportionally small ratios (a continuous glide).
                // 2) A lite apply - only the ScaleTransform moves during the gesture (instant,
                //    flicker-free, same path as live window-resize); the expensive tile/link
                //    refresh and hi-res re-sharpen run ONCE when the wheel rests (settle timer)
                //    instead of on every notch, which is what made zooming feel steppy.
                _fitMode   = FitMode.None;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                    _zoomLevel * Math.Pow(WheelZoomFactor, e.Delta / 120.0)));
                ApplyZoom(lite: true);
                StartZoomSettleTimer();

                // After layout settles, reposition the scroll so the cursor point stays fixed.
                // Formula: newOffset = (oldOffset + cursorPos) * (newZoom / oldZoom) - cursorPos
                double ratio = _zoomLevel / oldZoom;
                double newHOff = (oldHOff + cursorInViewport.X) * ratio - cursorInViewport.X;
                double newVOff = (oldVOff + cursorInViewport.Y) * ratio - cursorInViewport.Y;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                {
                    PagePreviewPanel.ScrollToHorizontalOffset(Math.Max(0, newHOff));
                    PagePreviewPanel.ScrollToVerticalOffset(Math.Max(0, newVOff));
                }));
                return;
            }

            // Regular scroll. Grid and Continuous are a single scroll over the WHOLE document, so the
            // wheel must never be hijacked for page navigation there - it always scrolls. (Page-nav
            // hijacking here was the old grid-refuses-to-scroll bug: right after a zoom/column change
            // the extent can momentarily measure as zero and the nav fallback fired instead.)
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.Continuous)
            {
                ScrollWheel(e);
                return;
            }

            // Single / Two-Page: a page often fits the viewport, so at the scroll boundary fall
            // through to page navigation so the user can reach adjacent pages without the sidebar.
            if (PagePreviewPanel.ScrollableHeight <= 0)
            {
                e.Handled = true;
                if (_wheelPageFlipGate.TryConfirm(e.Delta, DateTime.UtcNow))
                    NavigatePageByWheel(e.Delta);
                return;
            }

            bool atTop = PagePreviewPanel.VerticalOffset <= 0;
            bool atBottom = PagePreviewPanel.VerticalOffset >= PagePreviewPanel.ScrollableHeight - 1;
            if ((atTop && e.Delta > 0) || (atBottom && e.Delta < 0))
            {
                e.Handled = true;
                if (_wheelPageFlipGate.TryConfirm(e.Delta, DateTime.UtcNow))
                    NavigatePageByWheel(e.Delta);
                return;
            }
            _wheelPageFlipGate.NoteContentScroll(DateTime.UtcNow);
            ScrollWheel(e);
        }

        // True when the wheel sits over a scrollable control inside the page overlay that still
        // has somewhere to go in this direction. The walk stops at PagePreviewPanel, which is
        // itself a ScrollViewer and owns the wheel from that point up.
        private bool FieldListWantsWheel(DependencyObject? source, int delta)
        {
            for (DependencyObject? node = source;
                 node is not null && !ReferenceEquals(node, PagePreviewPanel);
                 node = node is Visual or System.Windows.Media.Media3D.Visual3D
                     ? VisualTreeHelper.GetParent(node) : null)
            {
                if (node is not ScrollViewer inner
                    || inner.ScrollableHeight <= 0
                    || !BelongsToFormFieldList(inner)) continue;
                return delta > 0
                    ? inner.VerticalOffset > 0
                    : inner.VerticalOffset < inner.ScrollableHeight;
            }
            return false;
        }

        // A TextBox, ComboBox, or another templated control can also contain an internal
        // ScrollViewer. Only the one owned by our multi-select form ListBox should intercept
        // the document wheel. The original broad check treated every nested ScrollViewer as
        // a field list and could leave an otherwise scrollable page unable to take the wheel.
        private bool BelongsToFormFieldList(DependencyObject scrollViewer)
        {
            for (DependencyObject? node = VisualTreeHelper.GetParent(scrollViewer);
                 node is not null && !ReferenceEquals(node, PagePreviewPanel);
                 node = node is Visual or System.Windows.Media.Media3D.Visual3D
                     ? VisualTreeHelper.GetParent(node) : null)
            {
                if (node is ListBox list && Equals(list.Tag, FormOverlayTag)) return true;
            }
            return false;
        }

        // Zoom ratio per full wheel notch (e.Delta = 120) for Ctrl+scroll. 1.1 lands close to the
        // old additive step at 100% zoom but stays a constant 10% everywhere on the range.
        private const double WheelZoomFactor = 1.1;

        private void PagePreview_ManipulationStarting(object? sender, ManipulationStartingEventArgs e)
        {
            _pinchZooming = false;
            e.ManipulationContainer = PagePreviewPanel;
            e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        }

        private void PagePreview_ManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
        {
            double scale = (e.DeltaManipulation.Scale.X + e.DeltaManipulation.Scale.Y) / 2.0;
            if (!double.IsFinite(scale) || scale <= 0) return;

            // A one-finger gesture reports a scale of 1 and belongs to the ScrollViewer's normal
            // panning path. Once a second finger changes the scale, own the rest of this gesture so
            // ScrollViewer does not translate the document underneath the pinch focal point (#271).
            if (!_pinchZooming && Math.Abs(scale - 1.0) < 0.002) return;
            _pinchZooming = true;
            e.Handled = true;
            if (_doc is null) return;

            Point origin = e.ManipulationOrigin;
            PinchZoomResult result = PinchZoomMath.Apply(
                _zoomLevel, scale, ZoomMin, ZoomMax,
                PagePreviewPanel.HorizontalOffset, PagePreviewPanel.VerticalOffset,
                origin.X, origin.Y);
            if (Math.Abs(result.Zoom - _zoomLevel) < 0.000001) return;

            _fitMode = FitMode.None;
            _zoomLevel = result.Zoom;
            ApplyZoom(lite: true);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                PagePreviewPanel.ScrollToHorizontalOffset(result.HorizontalOffset);
                PagePreviewPanel.ScrollToVerticalOffset(result.VerticalOffset);
            }));
        }

        private void PagePreview_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
        {
            if (!_pinchZooming) return;
            e.Handled = true;
            _pinchZooming = false;
            if (_doc is null) return;
            _zoomSettleTimer?.Stop();
            ApplyZoom();
            if (_currentPage >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), _currentPage + 1, _doc.PageCount) + $" - {DisplayZoomPct():F0}%");
        }

        // Wheel over the toolbar zoom dropdown: same multiplicative step as Ctrl+scroll, without
        // the cursor anchoring (the cursor is on the toolbar, not the page). Handled is set so the
        // ComboBox does not cycle its preset items under the wheel.
        internal void ZoomBoxWheel(MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (_doc is null) return;
            if (_viewMode == ViewMode.Grid) { GridZoomStep(e.Delta < 0); return; }
            _fitMode   = FitMode.None;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                _zoomLevel * Math.Pow(WheelZoomFactor, e.Delta / 120.0)));
            ApplyZoom(lite: true);   // SyncZoomBox inside keeps the shown % live per notch
            StartZoomSettleTimer();
        }

        // Debounced full zoom apply, shared by every Ctrl+scroll notch: while the wheel is moving
        // only the lite ScaleTransform runs; once it rests for a beat, do the one full ApplyZoom
        // (tile/link refresh, and the hi-res re-sharpen it queues) plus the status-bar update that
        // SetZoom would have shown per notch.
        private System.Windows.Threading.DispatcherTimer? _zoomSettleTimer;

        private void StartZoomSettleTimer()
        {
            if (_zoomSettleTimer is null)
            {
                _zoomSettleTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(200) };
                _zoomSettleTimer.Tick += (_, _) =>
                {
                    _zoomSettleTimer!.Stop();
                    if (_doc is null) return;
                    ApplyZoom();
                    if (_currentPage >= 0)
                        SetStatus(string.Format(Loc("Str_PageOf"), _currentPage + 1, _doc.PageCount) + $" - {DisplayZoomPct():F0}%");
                };
            }
            _zoomSettleTimer.Stop();
            _zoomSettleTimer.Start();
        }

        // Horizontal scrolling and the page sidebar retain the established speed multiplier.
        // Document wheel scrolling follows the Windows mouse setting below.
        internal const double WheelScrollFactor = 3.0;

        private void ScrollWheel(MouseWheelEventArgs e)
        {
            _sidebarSelectionPinned = -1;
            e.Handled = true;

            // Honor the Wheel tab in Windows Mouse Properties. A value of -1 means one screen
            // at a time. Scaling by the raw delta preserves smooth precision-touchpad movement.
            int lines = SystemParameters.WheelScrollLines;
            double distance = lines < 0
                ? PagePreviewPanel.ViewportHeight
                : lines * 16.0;
            PagePreviewPanel.ScrollToVerticalOffset(
                PagePreviewPanel.VerticalOffset - e.Delta * (distance / 120.0));
        }

        // #196: horizontal scroll fed from the window's WM_MOUSEHWHEEL hook (WPF surfaces no
        // event for it). Same per-delta distance as the vertical wheel; positive = right.
        internal void ScrollHorizontalExt(int delta)
        {
            if (_doc is null || PagePreviewPanel.Visibility != Visibility.Visible) return;
            PagePreviewPanel.ScrollToHorizontalOffset(
                PagePreviewPanel.HorizontalOffset + delta * (48.0 / 120.0) * WheelScrollFactor);
        }

        // Walks up the visual tree from the press's hit element to see if it landed on the scrollbar
        // (thumb, track, or repeat buttons). Used to exempt scrollbar presses from pane pan/marquee/crop.
        private static bool PressIsOnScrollBar(MouseButtonEventArgs e)
        {
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d is not null)
            {
                if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        internal void PagePreviewPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _sidebarSelectionPinned = -1;
            // A press that lands on the document scrollbar must reach the scrollbar itself (thumb drag,
            // track paging). The pan/crop/marquee handling below otherwise claims the press first and sets
            // e.Handled, so the thumb could never be grabbed. Let scrollbar presses fall through untouched.
            if (PressIsOnScrollBar(e)) return;

            bool spaceDown = Keyboard.IsKeyDown(Key.Space);
            if (e.ChangedButton == MouseButton.Middle ||
                (e.ChangedButton == MouseButton.Left && spaceDown))
            {
                _isPanning = true;
                _panStart = e.GetPosition(PagePreviewPanel);
                _panScrollH = PagePreviewPanel.HorizontalOffset;
                _panScrollV = PagePreviewPanel.VerticalOffset;
                PagePreviewPanel.CaptureMouse();
                PagePreviewPanel.Cursor = DragCursors.Closed;
                e.Handled = true;
            }
            // Crop: allow starting the selection OUTSIDE the page - catch margin clicks, route them to the
            // nearest page overlay, and clamp the start to the page edge so the crop rect stays on the page.
            else if (e.ChangedButton == MouseButton.Left && !spaceDown
                     && _currentTool == EditTool.Crop && _doc is not null)
            {
                Canvas? target = ResolveMarginOverlay(e);
                if (target is not null && target.Width > 0 && target.Height > 0)
                {
                    _activeCanvas = target;
                    // Pin the gesture surface/page so mouse-move/up resolve against this overlay
                    // (a margin crop start doesn't go through Canvas_MouseLeftButtonDown).
                    _gestureCanvas = target;
                    _gesturePage = target.Tag is int gt ? gt : _currentPage;
                    var p = e.GetPosition(target);
                    p.X = Math.Max(0, Math.Min(target.Width, p.X));
                    p.Y = Math.Max(0, Math.Min(target.Height, p.Y));
                    StartCropDraw(p);
                    e.Handled = true;
                }
            }
            // Marquee select: start a selection rectangle in the margin so it can span onto the pages. Same
            // routing as crop, but the start point is NOT clamped, so the box can begin off-page.
            else if (e.ChangedButton == MouseButton.Left && !spaceDown
                     && _currentTool == EditTool.Select && _doc is not null)
            {
                Canvas? target = ResolveMarginOverlay(e);
                if (target is not null && target.Width > 0 && target.Height > 0)
                {
                    StartMarqueeDraw(target, e.GetPosition(target));
                    e.Handled = true;
                }
            }
        }

        // Resolves which page overlay a margin (off-page) click attaches to, or null when the click is
        // actually on a page (left to that page's own surface). Shared by off-page crop and marquee starts.
        private Canvas? ResolveMarginOverlay(MouseButtonEventArgs e)
        {
            // A press inside a form field is the field's own interaction, never a margin gesture.
            // This includes a choice field's dropdown items: the popup floats outside every page
            // overlay's visual tree, so without this check the press reads as a margin click, the
            // marquee (or crop) starts, and taking the mouse capture closes the dropdown and
            // discards the click.
            if (e.OriginalSource is DependencyObject fieldSrc && IsFormFieldElement(fieldSrc))
                return null;
            if (_viewMode == ViewMode.Continuous)
            {
                if (e.OriginalSource is DependencyObject osc && IsWithinPageOverlay(osc)) return null;
                int pg = _currentPage;
                if (pg < 0 || !_continuousCanvases.ContainsKey(pg))
                    pg = NearestContinuousPage(e.GetPosition(_continuousPanel).Y);
                return pg >= 0 && _continuousCanvases.TryGetValue(pg, out var c) ? c : null;
            }
            bool onPrimary = e.OriginalSource is DependencyObject oss && IsDescendantOf(oss, _annotationCanvas);
            bool onTile = e.OriginalSource is DependencyObject ost && IsWithinPageOverlay(ost);
            return (!onPrimary && !onTile) ? _annotationCanvas : null;
        }

        // Begins a marquee anchored to refCanvas at posInRef (that page's coords, possibly off-page and
        // un-clamped). The box draws on the cross-page MarqueeLayer; the existing move/up handlers finish it.
        private void StartMarqueeDraw(Canvas refCanvas, Point posInRef)
        {
            _activeCanvas = refCanvas;
            _gestureCanvas = refCanvas;
            _gesturePage = refCanvas.Tag is int gt ? gt : _currentPage;
            ClearSelection();
            ClearTextSelection();
            _isSelecting = true;
            _selectStart = posInRef;
            _selectRect = new Rectangle
            {
                Fill = AccentBrush(40),
                Stroke = AccentBrush(150),
                StrokeThickness = 1,
                Width = 0, Height = 0,
                IsHitTestVisible = false
            };
            MarqueeLayer.Children.Add(_selectRect);
            UpdateMarquee(posInRef, posInRef);
            refCanvas.CaptureMouse();
        }

        // Begin a crop selection on the active overlay at pos (render-dim coords).
        private void StartCropDraw(Point pos)
        {
            _cropPageIndex = _activeCanvas.Tag is int cpi ? cpi : (_viewMode == ViewMode.Grid ? 0 : _currentPage);
            ClearSelection();
            _isDrawing = true;
            _drawStart = pos;
            // Draw the NEW box as a separate rect; the existing box, handles, and bar stay put until this
            // draw is committed on mouse-up (so a mouse-down never wipes the current box or bar).
            var cropDrawRect = new Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                StrokeDashArray = [5, 3],
                Fill = AccentBrush(55),
                Width = 0,
                Height = 0,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 },
            };
            Canvas.SetLeft(cropDrawRect, pos.X);
            Canvas.SetTop(cropDrawRect, pos.Y);
            Panel.SetZIndex(cropDrawRect, 2);
            _activeCanvas.Children.Add(cropDrawRect);
            _activePreview = cropDrawRect;
            _activeCanvas.CaptureMouse();
        }

        private bool IsWithinPageOverlay(DependencyObject node)
        {
            var cur = node;
            while (cur != null)
            {
                if (cur is Canvas c && _continuousCanvases.ContainsValue(c)) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        internal void PagePreviewPanel_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(PagePreviewPanel);
            PagePreviewPanel.ScrollToHorizontalOffset(_panScrollH - (pos.X - _panStart.X));
            PagePreviewPanel.ScrollToVerticalOffset(_panScrollV - (pos.Y - _panStart.Y));
            e.Handled = true;
        }

        internal void PagePreviewPanel_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            _isPanning = false;
            PagePreviewPanel.ReleaseMouseCapture();
            // Still holding space means still armed to pan, so it drops back to the open hand
            // rather than the arrow - the fingers release, the hand stays.
            PagePreviewPanel.Cursor = _spaceHeld ? DragCursors.Open : Cursors.Arrow;
            e.Handled = true;
        }
    }
}
