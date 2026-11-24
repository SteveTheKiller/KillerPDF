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
using KillerPdf.Engine.Rendering;
using PdfRenderedPage = KillerPDF.Services.PdfRenderedPage;
using ContentDoc = KillerPDF.Services.PdfContentDocument;

namespace KillerPDF.Controls
{
    // Page viewport: builds the page tiles and annotation overlays for all four view modes (single,
    // continuous, two-page, grid) and handles preview scrolling.
    //
    // Moved from Shell/Viewport.cs; this namespace and class line are the only changes. RenderPage /
    // SetupContinuousView / AddSecondaryTile / WirePageOverlay and the _pages / _continuousCanvases
    // invariant have to stay together - CLAUDE.md documents why splitting them repoints every page
    // at the hidden primary tile.
    //
    // The body spells window things bare (PageList, _doc, Loc, RenderAllAnnotations, ...). Those
    // resolve through PdfViewer.Bridge.cs, which forwards each one to Owner. That is what keeps this
    // file byte-identical with its pre-move form instead of rewriting ~700 references.
    public partial class PdfViewer
    {
        // ── Dark mode: image regions excluded from the inversion (#135 follow-up) ──────────────
        // Cache keyed by "page|file" so tab switches and temp reloads (which change the temp file
        // path) can never serve another document's rects; flushed with the render caches. Thread-
        // safe because the continuous / secondary-tile workers fill it off the UI thread.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapHelpers.FracRect[]>
            _pageImageRects = new();

        // ── Adjacent page prefetch (Single-page view) ──────────────────────────────────────
        // In Single mode the user often steps to the next or previous page, and each step used
        // to pay the full render before the new bitmap appeared. After a primary render lands
        // we fire a background render for pageIndex + 1 and pageIndex - 1 through the same
        // per-tab render cache the viewer already uses, so navigation to either neighbour hits
        // TryGetCachedRender and skips the engine entirely. Continuous and Grid modes stream
        // their own tiles through _backgroundRenderCache and do not need this. Two-Page shows
        // the whole spread already, so its adjacents are the next/previous spread; prefetching
        // them the same way still helps but keeps the same cancellation contract.
        private CancellationTokenSource? _prefetchCts;
        private void CancelAdjacentPrefetch()
        {
            var cts = System.Threading.Interlocked.Exchange(ref _prefetchCts, null);
            if (cts is null) return;
            try { cts.Cancel(); } catch { }
        }
        private void KickOffAdjacentPrefetch(int pageIndex, int scaledMax, int pgRot, bool primaryReadsCache,
            KillerPDF.Presentation.PdfSdrDisplayConverter? monitor = null, string? monitorIdentity = null)
        {
            if (_viewMode != ViewMode.Single && _viewMode != ViewMode.TwoPage) return;
            if (_currentFile is null || _doc is null) return;
            CancelAdjacentPrefetch();
            var session = _active;
            // The primary render looks pages up under the proof stamp and, under a monitor profile, the monitor identity.
            // These renders carry no proofing, and carry monitor conversion only when given the converter, so they are
            // only worth drawing when proofing is off and the primary reads the cache.
            var proof = session?.ProofView.CaptureProof();
            if (!primaryReadsCache || proof is null || proof.Settings.Enabled) return;
            // Pixels converted for one display observation are stored only if it is still the one shown when they land.
            var display = _presentationDisplay;
            var cts = new CancellationTokenSource();
            CancellationToken cancellationToken = cts.Token;
            _prefetchCts = cts;
            string currentFile = _currentFile;
            bool invert = DocInvert;
            long revision = session?.RenderRevision ?? 0;
            long prefetchCmyk = KillerPDF.Services.WorkingCmykProfiles.Generation;
            int total = _doc.PageCount;
            int[] targets = PreviewSpreads.NeighborStarts(pageIndex, total,
                PreviewSpreads.FromViewer(_viewMode == ViewMode.TwoPage, BookMode));
            var rotations = new Dictionary<int, int>(_pageRotations);
            var renderRequest = CaptureRenderRequest(currentFile, revision, floatFrames: monitor is not null);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                ContentDoc? contentDoc = null;
                try
                {
                    foreach (int target in targets)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        if (target < 0 || target >= total) continue;
                        int rot = rotations.TryGetValue(target, out int r) ? r : pgRot;
                        var cached = TryGetCachedRender(session, target, scaledMax, rot, proofStamp: proof.Stamp, monitorIdentity: monitorIdentity);
                        if (cached is not null) continue;
                        BitmapSource bitmap;
                        if (monitor is not null)
                        {
                            // The page is drawn the way the primary draws it, converted for the monitor, so the next page
                            // step finds the same pixels under the same identity.
                            bitmap = (await _viewportRenderQueue.Enqueue(
                                (renderRequest, "prefetch", target), 1000, 40L * scaledMax * scaledMax,
                                token => PreparePrimaryBitmap(renderRequest, target, scaledMax, rot, false, token, null, monitor),
                                cancellationToken)).Bitmap;
                        }
                        else
                        {
                            PdfRenderedPage rendered = await _viewportRenderQueue.Enqueue(
                                (renderRequest, "prefetch", target), 1000, 8L * scaledMax * scaledMax, token =>
                                {
                                    using var lease = renderRequest.Rent(token);
                                    return lease.Render(target, scaledMax, scaledMax, token);
                                }, cancellationToken);
                            int w = rendered.Width, h = rendered.Height;
                            byte[] raw = rendered.Pixels;
                            if (w <= 0 || h <= 0 || raw is null) continue;
                            if (invert)
                                BitmapHelpers.InvertBgraInPlaceExcept(raw, w, h,
                                    ImageRectsFor(renderRequest, target, ref contentDoc));
                            if (rot != 0)
                                (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                            if (cancellationToken.IsCancellationRequested) return;
                            double longest = Math.Max(1, Math.Max(w, h));
                            int bw = Math.Max(1, (int)Math.Round(2048.0 * w / longest));
                            int bh = Math.Max(1, (int)Math.Round(2048.0 * h / longest));
                            var wb = new WriteableBitmap(w, h,
                                96.0 * w / bw, 96.0 * h / bh, PixelFormats.Bgra32, null);
                            wb.WritePixels(new Int32Rect(0, 0, w, h), raw, w * 4, 0);
                            wb.Freeze();
                            RememberRenderWarning(wb, rendered);
                            NoteStampedRender(renderRequest, target, wb);
                            bitmap = wb;
                        }
                        if (cancellationToken.IsCancellationRequested) return;
                        int localTarget = target, localRot = rot;
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (cancellationToken.IsCancellationRequested) return;
                            // A page drawn for an older revision is not stored under the new one.
                            if (session is not null && session.RenderRevision != revision) return;
                            if (monitor is not null && !ReferenceEquals(display, _presentationDisplay)) return;
                            if (prefetchCmyk != KillerPDF.Services.WorkingCmykProfiles.Generation) return;
                            CacheRender(session, localTarget, scaledMax, localRot, bitmap, proofStamp: proof.Stamp, monitorIdentity: monitorIdentity);
                        }));
                    }
                }
                catch { /* prefetch is best-effort */ }
                finally
                {
                    contentDoc?.Dispose();
                    cts.Dispose();
                }
            });
        }

        /// <summary>The page's image boxes for the inversion carve-out, cached per (file, page).
        /// On a miss, opens engine into the caller's ref (so a worker loop pays ONE open however
        /// many pages it fills) - the caller disposes it; a held handle on the temp file would
        /// block the save-time file swap. Encrypted or unparsable pages cache an empty set, which
        /// falls back to inverting everything - the pre-carve-out behavior.</summary>
        private BitmapHelpers.FracRect[] ImageRectsFor(string file, Services.OpenedProtection? protection, int page, ref ContentDoc? contentDoc)
        {
            // Opt-in full inversion (moon right-click, "Invert images too"): no carve-out, and
            // no engine open paid for rects nobody will use.
            if (BitmapHelpers.DocInvertImages) return [];
            string key = page + "|" + file;
            if (_pageImageRects.TryGetValue(key, out var hit)) return hit;
            BitmapHelpers.FracRect[] rects;
            try
            {
                contentDoc ??= ContentDoc.Open(file, protection);
                rects = PdfImages.GetFracRects(contentDoc, page);
            }
            catch { rects = []; }
            _pageImageRects[key] = rects;
            return rects;
        }

        /// <summary>The image boxes of <paramref name="page"/> as <paramref name="request"/> renders it: a page drawn from
        /// the stamped copy (#478) keeps that copy's pictures, an image watermark among them, as Apply's output would.</summary>
        private BitmapHelpers.FracRect[] ImageRectsFor(PdfBackgroundRenderCache.Request request, int page, ref ContentDoc? contentDoc)
        {
            if (!request.StampsInPage(page)) return ImageRectsFor(request.Path, request.Protection, page, ref contentDoc);
            if (BitmapHelpers.DocInvertImages) return [];
            string key = page + "|" + request.Path + "|stamped " + request.Stamps!.Id.ToString("N");
            if (_pageImageRects.TryGetValue(key, out var hit)) return hit;
            BitmapHelpers.FracRect[] rects;
            try { rects = request.Stamps!.ReadContent(stamped => PdfImages.GetFracRects(stamped, page)); }
            catch { rects = []; }
            _pageImageRects[key] = rects;
            return rects;
        }

        /// <summary>Attach the scroll handler. The window used to wire this in its constructor, but
        /// PagePreviewPanel is inside this control's namescope now, so the control does it - called
        /// from the MainWindow ctor at the point the += used to sit.</summary>
        internal void WireScrollChanged()
            => PagePreviewPanel.ScrollChanged += PagePreviewPanel_ScrollChanged;

        /// <summary>Drop the per-page image-rect cache (the night-mode carve-out). Called by the
        /// window's FlushAllRenderCaches when the invert state flips; it re-fills lazily.</summary>
        internal void FlushImageRectCache() => _pageImageRects.Clear();

        internal void ScrollContinuousToPage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _continuousTops.Count) return;
            double target = _continuousTops[pageIndex] * _zoomLevel;
            PagePreviewPanel.ScrollToVerticalOffset(target);
        }

        // Sidebar/page-jump navigation must temporarily outrank viewport-driven page tracking.
        // WPF can raise ScrollChanged while the requested offset is still clamped to the old layout;
        // without this target, that interim event immediately writes Page 1 back into the sidebar.
        private int _continuousNavigationTarget = -1;
        // An explicit sidebar selection owns the current page until the user interacts with the
        // document viewport. Passive layout, fitting, rendering, and BringIntoView scrolls must not
        // replace it with whichever page happens to be nearest the viewport center.
        private int _sidebarSelectionPinned = -1;

        private void NavigateContinuousToPage(int pageIndex)
        {
            if (!HasReadDocument || pageIndex < 0 || pageIndex >= ReadPageCount) return;
            _continuousNavigationTarget = pageIndex;
            // A page the open is still waiting to render scrolls to itself when its slot arrives.
            // A jump made before that replaces it, or the render would pull the view back.
            if (_continuousScrollTarget >= 0) _continuousScrollTarget = pageIndex;
            ScrollContinuousToPage(pageIndex);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                if (_continuousNavigationTarget != pageIndex) return;
                ScrollContinuousToPage(pageIndex);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
                {
                    if (_continuousNavigationTarget != pageIndex) return;
                    _continuousNavigationTarget = -1;
                    SyncCurrentPageTo(pageIndex);
                }));
            }));
        }

        // ── Current-page badge (#197, thanks Ryokoxx) ─────────────────────────────────────────
        // One viewport-corner badge showing "page / total", replacing the per-tile tooltips that
        // trailed the cursor. Slides up on scroll or page change, slides back down after idle.
        private System.Windows.Threading.DispatcherTimer? _pageBadgeTimer;

        private void ShowPageBadge(int page)
        {
            if (ReadPageCount < 2) return;
            ShowPageBadgeText($"{page + 1} / {ReadPageTotal}");
        }

        // #197 follow-up: one number says the least in Grid, where several tiles share the screen -
        // the badge names the visible span there instead ("2-5 / 50").
        private void ShowPageBadgeSpan(int first, int last)
        {
            if (ReadPageCount < 2) return;
            ShowPageBadgeText(first == last
                ? $"{first + 1} / {ReadPageTotal}"
                : $"{first + 1}-{last + 1} / {ReadPageTotal}");
        }

        private void ShowPageBadgeText(string text)
        {
            PageBadgeText.Text = text;
            PageBadgeSlide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            PageBadge.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            if (_pageBadgeTimer is null)
            {
                _pageBadgeTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(900) };
                _pageBadgeTimer.Tick += (_, _) =>
                {
                    _pageBadgeTimer!.Stop();
                    PageBadgeSlide.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(46, TimeSpan.FromMilliseconds(220))
                            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
                    PageBadge.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
                };
            }
            _pageBadgeTimer.Stop();
            _pageBadgeTimer.Start();
        }

        private void PagePreviewPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // ScrollChanged bubbles from nested form controls. Only movement of the document
            // viewport is page navigation or a reason to rebuild document chrome.
            if (!ReferenceEquals(e.OriginalSource, PagePreviewPanel)) return;
            if (e.HorizontalChange != 0 || e.VerticalChange != 0)
            {
                CancelVisibleRefinement();
                StartRerenderTimer();
            }
            // A layout that brings other pages into view without a scroll, such as a Grid rebuilt at another zoom,
            // gets their search highlights read once it settles.
            else if ((e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0 || e.ViewportHeightChange != 0)
                && _searchBar is { Visibility: Visibility.Visible } && Search.WaitsForPagesOnScreen)
                StartRerenderTimer();

            if (_active is not null)
            {
                _active.ScrollH = PagePreviewPanel.HorizontalOffset;
                _active.ScrollV = PagePreviewPanel.VerticalOffset;
            }

            if (e.HorizontalChange != 0 || e.VerticalChange != 0)
                Host?.ViewerScrolled(this,
                    PagePreviewPanel.ScrollableWidth <= 0 ? 0 : PagePreviewPanel.HorizontalOffset / PagePreviewPanel.ScrollableWidth,
                    PagePreviewPanel.ScrollableHeight <= 0 ? 0 : PagePreviewPanel.VerticalOffset / PagePreviewPanel.ScrollableHeight);

            if (_viewMode.IsContinuous() && _continuousTops.Count > 0)
            {
                double viewportCenter = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight * 0.5)
                                        / Math.Max(0.01, _zoomLevel);
                int nearest = 0;
                double minDist = double.MaxValue;
                for (int i = 0; i < _continuousTops.Count; i++)
                {
                    if (i >= _continuousPanel.Children.Count) break;
                    var slot = (FrameworkElement)_continuousPanel.Children[i];
                    double center = _continuousTops[i] + slot.Height * 0.5;
                    double dist   = Math.Abs(center - viewportCenter);
                    if (dist < minDist) { minDist = dist; nearest = i; }
                }

                // #197: surface the position badge on real scrolling, focused pane or not - the
                // badge is per-pane furniture, unlike the page-jump box synced below.
                if (e.VerticalChange != 0) ShowPageBadge(nearest);

                // Rebuilding Continuous temporarily returns the ScrollViewer to the top while its
                // slots are replaced. That layout scroll is not navigation: keep the requested page
                // selected until the target slot has its final geometry and the deferred scroll lands.
                if (_continuousScrollTarget < 0 && _continuousNavigationTarget < 0
                    && _sidebarSelectionPinned < 0)
                    SyncCurrentPageTo(nearest);

                // Once the scroll settles, sharpen the pages now in view (and release the ones that left).
                // Cheap when there's nothing to do: below the hi-res threshold it's a restore-only pass over
                // an (almost always empty) set (#85).
                StartRerenderTimer();
                return;
            }

            // Grid scrolls through this same ScrollViewer but never tracked the current page, so the
            // statusbar counter, the jump box, and anything reading PageList.SelectedIndex (e.g. the
            // page a new bookmark targets, #133) went stale the moment the user scrolled. Track the
            // tile nearest the viewport center, exactly like Continuous. TranslatePoint to the scroll
            // content root already includes the zoom LayoutTransform, so no manual scaling is needed.
            if (_viewMode == ViewMode.Grid && _pageContentPanel.Children.Count > 1
                && PagePreviewPanel.Content is FrameworkElement gridRoot)
            {
                double viewTop    = PagePreviewPanel.VerticalOffset;
                double viewBottom = viewTop + PagePreviewPanel.ViewportHeight;
                double viewCenter = viewTop + PagePreviewPanel.ViewportHeight * 0.5;
                int nearest = -1, firstVis = -1, lastVis = -1;
                double minDist = double.MaxValue;
                for (int i = 0; i < _pageContentPanel.Children.Count; i++)
                {
                    if (_pageContentPanel.Children[i] is not FrameworkElement tile || tile.ActualHeight <= 0)
                        continue;
                    double top    = tile.TranslatePoint(new Point(0, 0), gridRoot).Y;
                    double bottom = tile.TranslatePoint(new Point(0, tile.ActualHeight), gridRoot).Y;
                    double dist = Math.Abs((top + bottom) * 0.5 - viewCenter);
                    if (dist < minDist) { minDist = dist; nearest = i; }
                    if (bottom > viewTop && top < viewBottom)
                    {
                        if (firstVis < 0) firstVis = i;
                        if (i > lastVis) lastVis = i;
                    }
                }
                // #197 follow-up: grid scrolling never raised the badge (it only fired on a page
                // change), and one page number can't say which tiles are on screen - show the span.
                if (e.VerticalChange != 0 && firstVis >= 0) ShowPageBadgeSpan(firstVis, lastVis);
                if (nearest >= 0 && _sidebarSelectionPinned < 0) SyncCurrentPageTo(nearest);
            }
        }

        // Reflects a scroll-derived current page in the jump box, the sidebar selection, and the
        // statusbar page counter - without re-rendering (the selection handler is detached while
        // the index is set, matching the original Continuous sync).
        private void SyncCurrentPageTo(int nearest)
        {
            // Everything below is window chrome that describes the FOCUSED pane - the one sidebar
            // list, the one jump box, the one status line. An unfocused pane scrolling must not
            // overwrite them with its own page number.
            if (Host != null && !Host.IsViewerFocused(this))
            {
                State.CurrentPage = nearest;
                _active?.PageIndex = nearest;
                return;
            }
            if (_currentPage == nearest)
            {
                Host?.ViewerPageChanged(this, nearest);
                Host?.PageJumpText = (nearest + 1).ToString();
                Host?.EnsureSidebarPageVisible(this, nearest);
                // Explicit navigation already assigned the page before the scroll settles.
                // Its status still needs refreshing even when the page index now agrees.
                PresentCurrentPageStatus();
                return;
            }
            Host?.PageJumpText = (nearest + 1).ToString();
            // Reentrancy FLAG, not a detach/attach pair. The PageList's real subscription is the
            // WINDOW's XAML-bound stub, so `-=` of this pane's own delegate removed NOTHING and
            // the `+=` stacked this pane's handler onto the shared list as an EXTRA direct
            // subscription - one more per scroll-sync, forever (found via zoomtrace, 2026-08-01,
            // alongside the identical ZoomBox pattern).
            _syncingPageList = true;
            try { _currentPage = nearest; }
            finally { _syncingPageList = false; }
            // Keep the selected thumbnail in view. ScrollIntoView lives in
            // PageList_SelectionChanged, which is detached above - so the scroll-driven path moved
            // the highlight but never scrolled the sidebar, and the selection walked off the end
            // of the visible list as the document scrolled.
            Host?.EnsureSidebarPageVisible(this, _currentPage);
            // This is the one write that detaches the handler (to avoid re-entering the render path
            // from a scroll sync), so it is also the one that would slip past the mirror in
            // PageList_SelectionChanged. Set it by hand.
            State.CurrentPage = nearest;
            PresentCurrentPageStatus();
        }

        // Shows an edit's result now and again once a reload's scroll has settled. The settle
        // reports the page position, which would otherwise replace the result before it is read.
        internal void ReportResult(string text)
        {
            SetStatus(text);
            var report = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            report.Tick += (_, _) => { report.Stop(); SetStatus(text); };
            report.Start();
        }

        // Common overlay wiring shared by the continuous and secondary-tile builders: the move/up
        // gesture handlers, the shared right-click context menu (per-page overlays don't inherit the
        // primary's ContextMenu), and registration in both page maps. The mouse-DOWN handler and the
        // overlay's size/layout are caller-specific, so those stay in the callers.
        private void WirePageOverlay(Canvas overlay, int page)
        {
            overlay.MouseMove                += Canvas_MouseMove;
            overlay.MouseLeave               += Canvas_MouseLeave;
            overlay.PreviewMouseLeftButtonUp += Canvas_MouseLeftButtonUp;
            overlay.PreviewMouseRightButtonUp += (s, ev) =>
            {
                // #128: Continuous never click-selects (current page is viewport-driven); the menu
                // below is populated for the clicked page directly. Grid keeps the click highlight.
                if (_viewMode == ViewMode.Grid) _currentPage = page;
                if (_annotationCanvas.ContextMenu is ContextMenu cm)
                {
                    // Selection chrome draws on _activeCanvas, so point it at this tile before populating.
                    _activeCanvas = (Canvas)s;
                    PopulateContextMenu(ev.GetPosition((Canvas)s), page);
                    cm.PlacementTarget = (UIElement)s;
                    cm.IsOpen = true;
                    ev.Handled = true;
                }
            };
            _continuousCanvases[page] = overlay;
            _pages[page] = overlay;
            NotifyReadingPageReady(page);
        }

        // Builds a page's annotation overlay. Size/transform differ by mode (continuous = render-dim + scale;
        // grid/two-page = DIP 1:1); everything else - background, clip, tag, input handler - is identical.
        private SafeCanvas BuildPageOverlay(int page, double width, double height, System.Windows.Media.Transform? layoutTransform)
        {
            var overlay = new SafeCanvas
            {
                Width = width,
                Height = height,
                Background = Brushes.Transparent,
                ClipToBounds = true,
                Tag = page
            };
            if (layoutTransform != null) overlay.LayoutTransform = layoutTransform;
            overlay.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            WirePageOverlay(overlay, page);
            // #197: no per-tile page tooltip anymore - it trailed the cursor and read as noise.
            // The viewport-corner badge (ShowPageBadge) is the page indicator now.
            return overlay;
        }

        // Build tile-0 (the primary page) and insert it at the head of the page panel. Wiring:
        // left-down/move/leave/left-up, plus the attached ContextMenu set later in BuildContextMenu.
        // It deliberately does NOT use WirePageOverlay - the primary must stay OUT of _continuousCanvases, and
        // RenderPage remains the sole registrar of _pages[primary], preserving ClearSecondaryPages' "keep the
        // index-0 tile" contract. Runs once from the constructor after _pageContentPanel is resolved.
        internal void BuildPrimaryTile()
        {
            var img = new Image { Stretch = Stretch.None };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var overlay = new SafeCanvas { Background = Brushes.Transparent, ClipToBounds = true };
            overlay.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            overlay.MouseMove                  += Canvas_MouseMove;
            overlay.MouseLeave                 += Canvas_MouseLeave;
            overlay.PreviewMouseLeftButtonUp   += Canvas_MouseLeftButtonUp;

            var grid = new Grid();
            grid.Children.Add(img);
            grid.Children.Add(overlay);

            var tile = new Border
            {
                Background        = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
                Margin            = new Thickness(0, 0, 12, 12),
                Child             = grid,
            };
            _pageContentPanel.Children.Insert(0, tile);

            PageImage         = img;
            _annotationCanvas = overlay;
        }

        private double ContinuousLayoutWidth => _viewMode == ViewMode.TwoPageContinuous
            ? _continuousPageW * 2 + 12 : _continuousPageW;

        private void LayoutContinuousSlots()
        {
            double[] heights = [.. _continuousPanel.Children.Cast<FrameworkElement>()
                .Select(slot => double.IsFinite(slot.Height) ? slot.Height : 0)];
            var layout = ContinuousPageLayout.Create(heights, _continuousPageW,
                _viewMode == ViewMode.TwoPageContinuous, BookMode);
            _continuousTops.Clear();
            _continuousPanel.Width = ContinuousLayoutWidth;
            _continuousPanel.Height = layout.Count == 0 ? 0 : layout.Max(slot => slot.Top + slot.Height) + 12;
            for (int page = 0; page < layout.Count; page++)
            {
                _continuousTops.Add(layout[page].Top);
                Canvas.SetLeft(_continuousPanel.Children[page], layout[page].Left);
                Canvas.SetTop(_continuousPanel.Children[page], layout[page].Top);
            }
        }

        internal void SetupContinuousView(int initialPage, bool fitDefault = true,
            double? restoreVerticalOffset = null)
        {
            if (!HasReadDocument) return;
            // #130: a malformed page tree can parse to zero pages - Pages[0] below would throw
            // ArgumentOutOfRangeException. Bail out instead of crashing; the view just stays empty.
            if (ReadPageCount == 0) return;
            initialPage = Math.Clamp(initialPage, 0, ReadPageCount - 1);
            _continuousScrollTarget = restoreVerticalOffset.HasValue ? -1 : initialPage;
            SyncPageListSelection(initialPage);
            // Coming from Grid, the shared ScrollViewer still carries the grid's overrides
            // (horizontal bar Disabled, vertical Visible). Continuous never passes through
            // RefreshPageView, where the other modes restore them - so restore here, or a
            // zoomed-in continuous view gets clamped to the viewport width: the page renders
            // clipped with dead side margins and no horizontal scrollbar to reach the rest.
            PagePreviewPanel.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            PagePreviewPanel.VerticalScrollBarVisibility   = ScrollBarVisibility.Auto;
            // Grid zeroes the document-surface padding and Two-Page centers it vertically;
            // restore both here for the same reason as the scrollbar overrides above.
            DocSurfacePad.Padding = new Thickness(12);
            DocSurfacePad.VerticalAlignment = VerticalAlignment.Top;
            CancelAndRelease(_continuousRenderCts);
            _continuousRenderCts = null;
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _continuousCanvases.Clear();
            _continuousLinks.Clear();
            _continuousDrawnWidths.Clear();
            _pages.Clear();

            // Fresh slot set: any hi-res re-sharpen bookkeeping from the previous layout is stale.
            // (This reset used to live in the render pass, back when it repainted every slot.)
            CancelAndRelease(_continuousSharpenCts);
            _continuousSharpenCts = null;
            _continuousSharpPages.Clear();
            _continuousSharpW = 0;

            // Use the document's natural page width in WPF DIPs (96 DIP/inch, 72 pt/inch).
            // This is zoom-independent, which is critical: FitToWidth computes
            //   zoom = viewportW / _continuousPageW
            // and if _continuousPageW were derived from the current zoom level the two
            // would cancel and FitToWidth would always return approximately the old zoom.
            var (referenceWidth, _) = ReadPhysicalPageSize(0);
            _continuousPageW = Math.Max(200.0, referenceWidth * (96.0 / 72.0));

            for (int i = 0; i < ReadPageCount; i++) AddContinuousSlot(i);
            LayoutContinuousSlots();

            // Paint existing annotations onto the freshly built per-page overlays so they show
            // immediately. Without this they stayed invisible until the next tool/page change
            // happened to trigger a render for that page.
            foreach (var annotPage in _annotations.Keys.ToList())
                if (_continuousCanvases.ContainsKey(annotPage))
                    RenderAllAnnotations(annotPage);

            // Re-apply the view's zoom now that _continuousPageW is known. Honor the saved fit mode; for a
            // custom (None) zoom, keep the exact level on a tab restore (fitDefault=false) instead of snapping
            // to fit-page - otherwise switching tabs in continuous mode loses the user's zoom. Fresh opens and
            // view-mode switches pass fitDefault=true and still default to fit-page.
            if (_active?.PendingZoom != null) RestoreManualZoom();
            else if (_fitMode == FitMode.Width) FitToWidth();
            else if (_fitMode == FitMode.Page) FitToPage();
            else if (fitDefault) FitToPage();
            else RestoreManualZoom();

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (restoreVerticalOffset.HasValue)
                    PagePreviewPanel.ScrollToVerticalOffset(restoreVerticalOffset.Value);
                else
                    ScrollContinuousToPage(initialPage);
            });

            _ = RenderContinuousPages(initialPage);
        }

        // One continuous-view slot at the end: a white placeholder at the page's size. Its page image and annotation
        // overlay are built when the page is first drawn (EnsureContinuousSlotContent), so a long document does not
        // build and lay out thousands of them before its first page shows. A paginating book adds its new pages here too.
        private void AddContinuousSlot(int i)
        {
            var (pw, ph) = ReadVisualPageSize(i);
            // Scaffold: reuse this tab's cached render dimensions (from a prior render of this page) so
            // the frame is built at its REAL size up front. On a tab switch the page slots are already the
            // right shape - no dark estimate-sized box that resizes when the bitmap finally streams in.
            // Fall back to the page-box estimate only the first time a page is laid out. Both are the same
            // canonical render-dim space (longest side -> 2048), so annotation coordinates stay identical.
            int rdW, rdH;
            if (_renderDims.TryGetValue(i, out var cachedDims) && cachedDims.w > 0 && cachedDims.h > 0)
            {
                rdW = cachedDims.w;
                rdH = cachedDims.h;
            }
            else
            {
                double maxDim = Math.Max(pw, ph);
                rdW = Math.Max(1, (int)Math.Round(2048.0 * pw / maxDim));
                rdH = Math.Max(1, (int)Math.Round(2048.0 * ph / maxDim));
                _renderDims[i] = (rdW, rdH);
            }
            // #128: no click-to-select in Continuous. The current page follows the viewport via
            // scroll-sync (like Acrobat/Sumatra), so a click must not scroll or move the counter.
            _continuousPanel.Children.Add(new Border
            {
                Width      = _continuousPageW,
                Height     = _continuousPageW * rdH / (double)rdW,
                Margin     = new Thickness(0, 0, 0, 12),
                Background = Brushes.White,   // empty-page scaffold while the bitmap streams in (not a dark box)
                Tag = i,
            });
        }

        // A slot's page image and annotation overlay, built the first time the page is drawn. A page's links are read
        // then too (SetContinuousSlot): reading every page's up front laid a whole book out, and read every link of a
        // long PDF, on the UI thread before the first page showed.
        private Grid EnsureContinuousSlotContent(int i)
        {
            var slot = (Border)_continuousPanel.Children[i];
            if (slot.Child is Grid built) return built;
            var (rdW, rdH) = _renderDims.TryGetValue(i, out var dims) && dims.w > 0 && dims.h > 0
                ? dims : (2048, Math.Max(1, (int)Math.Round(2048 * slot.Height / slot.Width)));
            double slotScale = _continuousPageW / rdW;
            var overlay = BuildPageOverlay(i, rdW, rdH, new System.Windows.Media.ScaleTransform(slotScale, slotScale));
            // The overlay misses the tool change that set every other page's cursor, so it takes the current one.
            overlay.Cursor = _annotationCanvas.Cursor;
            var pageImg = new Image { Stretch = Stretch.None, Width = _continuousPageW, Height = slot.Height };
            RenderOptions.SetBitmapScalingMode(pageImg, BitmapScalingMode.HighQuality);
            var slotGrid = new Grid();
            slotGrid.Children.Add(pageImg);
            slotGrid.Children.Add(overlay);
            slot.Child = slotGrid;
            return slotGrid;
        }

        // ── Continuous view virtualization (#122) ──────────────────────────────────────────────
        // Continuous used to rasterize EVERY page at open and keep every slot's bitmap alive for
        // the life of the document - a 243-page image PDF pinned gigabytes. Now only a window of
        // pages around the viewport holds real bitmaps: the render pass below fills the window,
        // and VirtualizeContinuousSlots (scroll settle) releases bitmaps that scrolled far away
        // and requests the ones approaching. Released slots keep their exact height, so scroll
        // geometry never changes; they show the white scaffold until re-rendered (render cache
        // hits re-attach instantly).
        private const int ContinuousKeepPages    = 10;   // pages each side of the viewport that get bitmaps
        private const int ContinuousReleasePages = 15;   // release only beyond this (hysteresis, no edge churn)
        private System.Threading.Tasks.Task? _continuousRenderTask;
        private int _continuousRenderCenterPage = -1;
        // The raster width each continuous page was last drawn at (ViewerRenderResolution.ContinuousWidth).
        private readonly Dictionary<int, int> _continuousDrawnWidths = [];

        /// <summary>True when a page near the viewport was drawn narrower than the current zoom and monitor need.</summary>
        private bool ContinuousNeedsSharperPages()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int now = ViewerRenderResolution.ContinuousWidth(_continuousPageW, dpi.DpiScaleX, dpi.DpiScaleY, _zoomLevel);
            int lo = Math.Max(0, _currentPage - ContinuousKeepPages), hi = _currentPage + ContinuousKeepPages;
            return _continuousDrawnWidths.Any(pair => pair.Key >= lo && pair.Key <= hi && pair.Value < now);
        }

        internal async System.Threading.Tasks.Task RenderContinuousPages(int centerPage)
        {
            if (!HasReadDocument || _currentFile is null) return;
            CancelAndRelease(_continuousRenderCts);
            _continuousRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _continuousRenderCts;

            string currentFile = _currentFile;
            int pageCount      = ReadPageCount;
            double targetW     = _continuousPageW;
            // Drawn at twice the width the page shows, at most twice its width in DIPs: a long document opened at a
            // fit-page zoom no longer draws its pages several times larger than they show. A settled zoom-in redraws
            // the pages near the viewport at the new width (ContinuousNeedsSharperPages).
            var renderDpi      = VisualTreeHelper.GetDpi(this);
            int renderW        = ViewerRenderResolution.ContinuousWidth(targetW, renderDpi.DpiScaleX, renderDpi.DpiScaleY, _zoomLevel);

            // Window of pages to materialize. Slots that already hold a base bitmap are skipped;
            // slots holding a hi-res re-sharpened bitmap belong to the resharpen pass, skip too.
            int lo = Math.Max(0, centerPage - ContinuousKeepPages);
            int hi = Math.Min(pageCount - 1, centerPage + ContinuousKeepPages);
            var todo = new List<int>();
            foreach (int i in ContinuousRenderOrder.Around(centerPage, lo, hi))
            {
                if (i >= _continuousPanel.Children.Count) continue;
                if (_continuousPanel.Children[i] is Border b && (b.Child is not Grid g
                    || g.Children.Count > 0 && g.Children[0] is Image { Source: null }
                    || _continuousDrawnWidths.TryGetValue(i, out int drawn) && drawn < renderW))
                    todo.Add(i);
            }
            if (todo.Count == 0)
            {
                _continuousRenderTask = null;
                _continuousRenderCenterPage = -1;
                return;
            }

            // Capture per-page rotations on the UI thread before going async
            var rotations = new Dictionary<int, int>(_pageRotations);

            var session = _active;
            var proof = session?.ProofView.CaptureProof();
            long revision = session?.RenderRevision ?? 0;
            var mode = _viewMode;
            var nativeOwner = _nativeDoc;
            var nativeRevision = nativeOwner?.CaptureReadRevision();
            bool invert = DocInvert;
            bool invertImages = BitmapHelpers.DocInvertImages;
            var continuousDisplay = CapturePrimaryDisplay();
            var continuousMonitor = !invert
                && PdfDisplayPresentationPolicy.Resolve(continuousDisplay).TransformOwner == PdfMonitorTransformOwner.Application
                ? _sdrDisplayConverter : null;
            var renderRequest = nativeRevision is null ? CaptureRenderRequest(currentFile, revision,
                floatFrames: proof?.Settings.Enabled == true || continuousMonitor is not null) : null;
            // Pages converted for a monitor are cached under its profile's identity; with none established they are not cached.
            bool continuousCacheable = TryMonitorCacheIdentity(continuousMonitor, continuousDisplay, out string? continuousMonitorIdentity);
            var renderToken = cts.Token;
            long continuousCmyk = KillerPDF.Services.WorkingCmykProfiles.Generation;
            bool Current() => continuousCmyk == KillerPDF.Services.WorkingCmykProfiles.Generation
                && !renderToken.IsCancellationRequested && mode == _viewMode && _viewMode.IsContinuous()
                && ReferenceEquals(_active, session) && ReferenceEquals(_nativeDoc, nativeOwner)
                && _currentFile == currentFile && ReferenceEquals(continuousDisplay, _presentationDisplay)
                && revision == (_active?.RenderRevision ?? 0) && invert == DocInvert
                && proof?.Stamp == _active?.ProofView.CaptureProof().Stamp;
            var renderTask = System.Threading.Tasks.Task.Run(async () =>
            {
                ContentDoc? contentDoc = null;   // opened lazily by ImageRectsFor on the first uncached page
                try
                {
                    foreach (int i in todo)
                    {
                        if (cts.IsCancellationRequested) return;
                        int rot = rotations.TryGetValue(i, out int rr) ? rr : 0;

                        // Cache hit: attach the cached bitmap to its slot.
                        var cb = continuousCacheable ? TryGetCachedRender(session, i, renderW, rot,
                            continuous: true, proofStamp: proof?.Stamp, monitorIdentity: continuousMonitorIdentity) : null;
                        if (cb != null)
                        {
                            int fic = i;
                            Dispatcher.Invoke(() =>
                            {
                                if (!Current()) return;
                                _continuousDrawnWidths[fic] = renderW;
                                SetContinuousSlot(fic, cb);
                            });
                            continue;
                        }

                        PdfRenderedPage? rendered = null;
                        string? monitorWarning = null;
                        string? displayFallback = null;
                        int w, h;
                        byte[] raw;
                        if (nativeRevision is not null)
                        {
                            var nativeRender = await _viewportRenderQueue.Enqueue(
                                (nativeRevision, "continuous", i), 10 + Math.Abs(i - centerPage),
                                Math.Min((proof?.Settings.Enabled == true ? 128L : 80L) * renderW * renderW, 512L << 20),
                                token =>
                                {
                                    try
                                    {
                                        return (Frame: RenderNativeFrame(nativeRevision, i, renderW, renderW * 2, token,
                                            proof?.Settings), Warning: (string?)null);
                                    }
                                    catch (NotSupportedException) when (proof?.Settings.Enabled == true)
                                    {
                                        return (Frame: RenderNativeFrame(nativeRevision, i, renderW, renderW * 2, token),
                                            Warning: "Str_CM_ProofUnavailablePage");
                                    }
                                }, renderToken);
                            var frame = nativeRender.Frame;
                            monitorWarning = nativeRender.Warning;
                            w = frame.Width;
                            h = frame.Height;
                            if (continuousMonitor is not null)
                            {
                                var converted = continuousMonitor.Convert(frame, renderToken);
                                if (converted.Outcome == KillerPDF.Presentation.PdfSdrConversionOutcome.Stale) return;
                                displayFallback = converted.FailureOperation;
                                raw = converted.Outcome == KillerPDF.Presentation.PdfSdrConversionOutcome.Converted
                                    ? converted.BgraPixels.ToArray() : frame.ToSrgbBgra32(renderToken);
                            }
                            else raw = frame.ToSrgbBgra32(renderToken);
                            if (invert && invertImages)
                                BitmapHelpers.InvertBgraInPlaceExcept(raw, w, h, []);
                        }
                        else
                        {
                            if (renderRequest is null) return;
                            // A refused proof keeps monitor conversion, and an undrawable float page keeps
                            // a visible fallback; neither request is dropped.
                            var viewing = await _viewportRenderQueue.Enqueue(
                                (renderRequest, continuousMonitor is null ? "continuous" : "continuous-float", i),
                                10 + Math.Abs(i - centerPage),
                                Math.Min((proof?.Settings.Enabled == true ? 128L : continuousMonitor is null ? 16L : 80L)
                                    * renderW * renderW, 512L << 20), token =>
                            {
                                using var lease = renderRequest.Rent(token);
                                return RenderViewingPage(lease, i, renderW, token, proof?.Settings,
                                    maximumHeight: renderW * 2, monitor: continuousMonitor);
                            }, renderToken);
                            rendered = viewing.Compatibility;
                            if (viewing.ProofUnavailable) monitorWarning = "Str_CM_ProofUnavailablePage";
                            displayFallback = viewing.DisplayFallbackOperation;
                            w = viewing.Width;
                            h = viewing.Height;
                            raw = viewing.Pixels;
                            if (w <= 0 || h <= 0 || raw is null) continue;
                            if (invert)
                                BitmapHelpers.InvertBgraInPlaceExcept(raw, w, h,
                                    ImageRectsFor(renderRequest, i, ref contentDoc));
                        }
                        if (w <= 0 || h <= 0 || raw is null) continue;
                        if (rot != 0)
                            (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);

                        PublishContinuousBitmap(i, w, h, rot, raw, rendered, monitorWarning, displayFallback);
                    }

                    void PublishContinuousBitmap(int fi, int fw, int fh, int prot, byte[] bytes,
                        PdfRenderedPage? rendered, string? monitorWarning, string? displayFallback)
                    {
                        if (cts.IsCancellationRequested) return;
                        // Use the window's own dispatcher, not Application.Current.Dispatcher: during app
                        // shutdown Application.Current goes null and this background render would NRE.
                        Dispatcher.Invoke(() =>
                        {
                            if (!Current()) return;
                            if (fi >= _continuousPanel.Children.Count) return;
                            // Render at the natural continuous width (zoom is a LayoutTransform, so the bitmap
                            // is zoom-independent and reusable). Square pixels via the matching dipH.
                            int ddw = Math.Max(1, (int)Math.Round(targetW));
                            int ddh = Math.Max(1, (int)Math.Round(targetW * fh / fw));
                            var bmp = BitmapHelpers.BuildScaledBitmap(fw, fh, bytes, ddw, ddh);
                            if (rendered is { } pdfPage) RememberRenderWarning(bmp, pdfPage);
                            NoteStampedRender(renderRequest, fi, bmp);
                            // monitorWarning carries the proof notice key; the text is localized when shown.
                            if (monitorWarning is not null || displayFallback is not null)
                            {
                                var existing = RenderWarnings.TryGetValue(bmp, out var recorded) ? recorded : new RenderWarning("");
                                RenderWarnings.AddOrUpdate(bmp, existing with
                                {
                                    DisplayFallbackOperation = displayFallback ?? existing.DisplayFallbackOperation,
                                    ProofUnavailable = existing.ProofUnavailable || monitorWarning is not null
                                });
                            }
                            _continuousDrawnWidths[fi] = renderW;
                            if (continuousCacheable) CacheRender(session, fi, renderW, prot, bmp,
                                continuous: true, proofStamp: proof?.Stamp, monitorIdentity: continuousMonitorIdentity);
                            SetContinuousSlot(fi, bmp);
                        });
                    }
                }
                catch { /* render canceled or doc closed */ }
                finally { contentDoc?.Dispose(); }
            }, renderToken);
            _continuousRenderTask = renderTask;
            _continuousRenderCenterPage = centerPage;
            await renderTask;
        }

        // Attaches a (cached or freshly built) page bitmap to its continuous slot and finalizes the slot /
        // overlay sizes + scroll offsets. UI thread. Shared by the cache-hit and freshly-rendered paths.
        private void SetContinuousSlot(int fi, System.Windows.Media.Imaging.BitmapSource bmp)
        {
            if (fi < 0 || fi >= _continuousPanel.Children.Count) return;
            var slot = (Border)_continuousPanel.Children[fi];
            int fw = bmp.PixelWidth, fh = bmp.PixelHeight;
            double dipW = slot.Width;
            double dipH = dipW * fh / fw;
            var slotGrid = EnsureContinuousSlotContent(fi);
            if (slotGrid.Children.Count == 0 || slotGrid.Children[0] is not Image pageImg)
                return;

            RemoveVisibleDetail(pageImg);
            pageImg.Source  = bmp;
            ShowStampElementsFor(slotGrid);   // #478: in the same step as the bitmap that holds the stamps, or not
            PresentRenderWarning(pageImg, bmp, fi);
            pageImg.Width   = dipW;
            pageImg.Height  = dipH;
            slot.Background = Brushes.White;
            // A base attach over a re-sharpened slot supersedes the hi-res bitmap; the resharpen
            // pass re-adds the page right after ITS SetContinuousSlot call, so this stays correct.
            _continuousSharpPages.Remove(fi);

            // Size the slot and overlay from the ACTUAL rendered page so a cropped page (which renders
            // shorter than its MediaBox estimate) fills its slot with no white bars.
            double oldH = double.IsNaN(slot.Height) ? 0 : slot.Height;
            slot.Height = dipH;
            double maxF = Math.Max(fw, fh);
            int rdW = Math.Max(1, (int)Math.Round(2048.0 * fw / maxF));
            int rdH = Math.Max(1, (int)Math.Round(2048.0 * fh / maxF));
            _renderDims[fi] = (rdW, rdH);
            if (slotGrid.Children.Count > 1 && slotGrid.Children[1] is Canvas ov)
            {
                ov.Width  = rdW;
                ov.Height = rdH;
                ov.LayoutTransform = new System.Windows.Media.ScaleTransform(dipW / rdW, dipW / rdW);
            }
            // A page's links, read once the page itself is drawn, so they're clickable in continuous view (resolved by
            // the bounds-check in Canvas_MouseLeftButtonDown). A PDF page records only their areas, as it did when they
            // were read at setup. A book page records its links, and gets its keyboard link overlays, once its
            // annotations are drawn below.
            if (_nativeDoc is null && !_continuousLinks.ContainsKey(fi))
                _continuousLinks[fi] = GetPageLinks(fi, rdW, rdH);

            // Slot heights are now exact; recompute scroll offsets from them. The offsets depend only on the heights, so
            // a page whose slot kept its height leaves them as they are.
            if (Math.Abs(dipH - oldH) > 0.5) LayoutContinuousSlots();

            // Pages render in order, so when the target page is reached every page above it has its final
            // height; re-scroll so a crop lands you back on the same page instead of drifting.
            if (_continuousScrollTarget >= 0 && fi >= _continuousScrollTarget)
            {
                int tgt = _continuousScrollTarget;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() =>
                    {
                        ScrollContinuousToPage(tgt);
                        _continuousScrollTarget = -1;
                        SyncCurrentPageTo(tgt);
                    }));
            }
            // Virtualized slots render on approach, so a page ABOVE the viewport can refine its
            // estimated height mid-scroll (e.g. scrolling upward into a cropped page). Compensate
            // the scroll offset by the height delta so the content on screen doesn't jump.
            else if (Math.Abs(dipH - oldH) > 0.5 && fi < _continuousTops.Count)
            {
                double viewTopSlots = PagePreviewPanel.VerticalOffset / Math.Max(0.01, _zoomLevel);
                if (_continuousTops[fi] + dipH <= viewTopSlots + 1)
                    PagePreviewPanel.ScrollToVerticalOffset(
                        PagePreviewPanel.VerticalOffset + (dipH - oldH) * _zoomLevel);
            }

            RenderAllAnnotations(fi);
            if (_nativeDoc is not null) AddSecondaryPageLinks(fi, rdW, rdH);
        }

        /// <summary>True when part of the page is in the document viewport now. Continuous and Grid keep an
        /// overlay for pages well outside the viewport, so having an overlay does not mean being on screen.</summary>
        internal bool IsPageOnScreen(int pageIndex)
        {
            if (!HasReadDocument || pageIndex < 0) return false;
            if (_viewMode.IsContinuous())
            {
                if (pageIndex >= _continuousTops.Count || pageIndex >= _continuousPanel.Children.Count) return false;
                double viewTop = PagePreviewPanel.VerticalOffset / Math.Max(0.01, _zoomLevel);
                double viewBot = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight) / Math.Max(0.01, _zoomLevel);
                double top = _continuousTops[pageIndex];
                double bot = top + ((FrameworkElement)_continuousPanel.Children[pageIndex]).Height;
                return bot >= viewTop && top <= viewBot;
            }
            if (_viewMode == ViewMode.Grid && _pageContentPanel.Children.Count > 1
                && PagePreviewPanel.Content is FrameworkElement gridRoot)
            {
                if (pageIndex >= _pageContentPanel.Children.Count
                    || _pageContentPanel.Children[pageIndex] is not FrameworkElement tile || tile.ActualHeight <= 0) return false;
                double viewTop = PagePreviewPanel.VerticalOffset;
                double viewBottom = viewTop + PagePreviewPanel.ViewportHeight;
                double top = tile.TranslatePoint(new Point(0, 0), gridRoot).Y;
                double bottom = tile.TranslatePoint(new Point(0, tile.ActualHeight), gridRoot).Y;
                return bottom > viewTop && top < viewBottom;
            }
            // Single Page and Two-Page give an overlay only to the pages they show.
            return VisibleCanvasForPage(pageIndex) is not null;
        }

        // Scroll-settle window maintenance for virtualized Continuous view (#122): releases the
        // bitmaps of slots far outside the viewport (heights are kept, so nothing moves) and
        // kicks a render pass when unrendered slots have come near. Runs on the UI thread from
        // the same debounced timer as the re-sharpen pass.
        private void VirtualizeContinuousSlots()
        {
            if (!_viewMode.IsContinuous() || !HasReadDocument) return;
            if (_continuousTops.Count == 0 || _continuousPanel.Children.Count == 0) return;

            // Visible slot range - same zoom-independent mapping the re-sharpen pass uses.
            double viewTop = PagePreviewPanel.VerticalOffset / Math.Max(0.01, _zoomLevel);
            double viewBot = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight) / Math.Max(0.01, _zoomLevel);
            int first = int.MaxValue, last = int.MinValue;
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double top = _continuousTops[i];
                double bot = top + ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (bot >= viewTop && top <= viewBot) { first = Math.Min(first, i); last = Math.Max(last, i); }
            }
            if (first > last) return;

            bool missing = false;
            for (int i = 0; i < _continuousPanel.Children.Count; i++)
            {
                bool near = i >= first - ContinuousKeepPages && i <= last + ContinuousKeepPages;
                // A slot whose page was never drawn is only a placeholder; one near the viewport needs drawing.
                if (_continuousPanel.Children[i] is Border { Child: null }) { missing |= near; continue; }
                if (_continuousPanel.Children[i] is not Border b || b.Child is not Grid g
                    || g.Children.Count == 0 || g.Children[0] is not Image img) continue;
                bool keep = i >= first - ContinuousReleasePages && i <= last + ContinuousReleasePages;
                if (!keep && img.Source != null)
                {
                    img.Source = null;   // slot keeps its height; white scaffold shows until re-rendered
                    _continuousSharpPages.Remove(i);
                }
                else if (i >= first - ContinuousKeepPages && i <= last + ContinuousKeepPages && img.Source == null)
                {
                    missing = true;
                }
            }
            if (missing)
            {
                int centerPage = (first + last) / 2;
                if (_continuousRenderCts is { IsCancellationRequested: false }
                    && _continuousRenderCenterPage == centerPage
                    && _continuousRenderTask is { IsCompleted: false }) return;
                _ = RenderContinuousPages(centerPage);
            }
        }

        // ── Continuous zoom re-sharpen (#85) ─────────────────────────────────────────────────────────
        // The settle timer refines visible regions above the fixed base budget.

        // keepTiles: leave the existing secondary tiles in place instead of clearing them first.
        // For a pixels-only repaint (invert toggle) the tile set is unchanged, and clearing it
        // made the whole grid flash empty and re-stream - RenderAdditionalPages swaps bitmaps
        // into existing tiles in place, so keeping them repaints without any layout jitter.
        // Navigation keeps the default (clear first) since the tile set actually changes.
        private readonly ViewportRenderQueue _viewportRenderQueue = new();
        private CancellationTokenSource? _primaryRenderCts;
        private long _primaryRenderGeneration;
        internal Task PrimaryRenderTask { get; private set; } = Task.CompletedTask;

        private void CancelPrimaryRender()
        {
            CancelVisibleRefinement();
            ++_primaryRenderGeneration;
            CancelAndRelease(_primaryRenderCts);
            _primaryRenderCts = null;
        }

        internal void RenderPage(int pageIndex, bool keepTiles = false)
            => PrimaryRenderTask = RenderPrimaryAsync(pageIndex, keepTiles);

        private async Task RenderPrimaryAsync(int pageIndex, bool keepTiles)
        {
            CancelPrimaryRender();
            ShowPrimaryCompatibility();
            if (_currentFile is null || !HasReadDocument) return;
            // Cancel any adjacent-page prefetch from the previous page; a new prefetch is
            // scheduled at the tail of this render.
            CancelAdjacentPrefetch();
            CancelAndRelease(_secondaryRenderCts);
            _secondaryRenderCts = null;
            // Continuous has its own pipeline (SetupContinuousView + RenderContinuousPages into
            // _continuousPanel) and owns the _pages map for every page. RenderPage targets the hidden
            // single/grid primary (_annotationCanvas in the collapsed _pageContentPanel) and calls
            // ClearSecondaryPages, which would WIPE the continuous _pages map and repoint the current
            // page at the invisible primary - so any annotation added afterwards renders off-screen
            // until a mode switch rebuilds the overlays. Stray callers (the zoom re-sharpen timer, the
            // DPI-change handler) can fire RenderPage while continuous is active; ignore them. The mode
            // switch sets _viewMode to the new (non-continuous) mode BEFORE calling RenderPage, so this
            // guard never blocks a legitimate switch-into-single/grid render.
            if (_viewMode.IsContinuous()) return;
            // Two-page spreads pair (0,1),(2,3),...; render the pair's left (even) page as primary so
            // selecting the right page of a pair still shows the whole spread, not a lone page.
            if (_viewMode == ViewMode.TwoPage) pageIndex = SpreadStart(pageIndex);   // #193: book-aware
            var primaryCts = _primaryRenderCts = new CancellationTokenSource();
            var primaryToken = primaryCts.Token;
            long generation = _primaryRenderGeneration;
            var primarySession = _active;
            var primaryProof = primarySession?.ProofView.CaptureProof();
            long primaryProofGeneration = CapturePrimaryProofGeneration(primaryProof);
            var primaryNative = _nativeDoc;
            string primaryFile = _currentFile;
            long primaryRevision = primarySession?.RenderRevision ?? 0;
            var primaryMode = _viewMode;
            bool primaryInvert = DocInvert;
            PdfDisplayDiscoverySnapshot? primaryDisplay = null;
            if (_annotationCanvas.Tag is not int displayedPage || displayedPage != pageIndex)
            {
                PageImage.Source = null;
                _annotationCanvas.Children.Clear();
                _annotationCanvas.IsHitTestVisible = false;
                ClearSecondaryPages();
            }
            long primaryCmyk = KillerPDF.Services.WorkingCmykProfiles.Generation;
            bool Current() => primaryCmyk == KillerPDF.Services.WorkingCmykProfiles.Generation
                && !primaryToken.IsCancellationRequested && generation == _primaryRenderGeneration
                && ReferenceEquals(primarySession, _active) && primaryFile == _currentFile
                && ReferenceEquals(primaryNative, _nativeDoc)
                && primaryRevision == (_active?.RenderRevision ?? 0) && primaryMode == _viewMode
                && primaryProof?.Stamp == _active?.ProofView.CaptureProof().Stamp
                && primaryInvert == DocInvert
                && (primaryDisplay is null || ReferenceEquals(primaryDisplay, _presentationDisplay));
            try
            {
                // Present a bounded base raster first. Settled visible regions receive
                // the additional pixels needed by display DPI and zoom.
                var dpiInfo = VisualTreeHelper.GetDpi(this);
                double dpiScaleX = dpiInfo.DpiScaleX;
                double dpiScaleY = dpiInfo.DpiScaleY;
                int scaledMax = Math.Min(2048, ViewerRenderResolution.Primary(dpiScaleX, dpiScaleY, _zoomLevel));
                _lastRenderZoom = _zoomLevel;

                int pgRot = _pageRotations.TryGetValue(pageIndex, out int pr0) ? pr0 : 0;
                primaryDisplay = CapturePrimaryDisplay();
                var presentationPolicy = PdfDisplayPresentationPolicy.Resolve(primaryDisplay);
                var primaryMonitor = !primaryInvert
                    && presentationPolicy.TransformOwner == PdfMonitorTransformOwner.Application
                    ? _sdrDisplayConverter : null;
                // Advanced Color and HDR displays take the plain path until the native child window can share the page
                // with the find bar, form size bar, badge and notices (1.9.4); a profiled SDR display still converts here.
                bool linear = primaryMode == ViewMode.Single && !primaryInvert && pgRot == 0
                    && ((NativePrimaryViewportEnabled && presentationPolicy.Mode == PdfPresentationMode.LinearFloat)
                        || presentationPolicy.TransformOwner == PdfMonitorTransformOwner.Application);

                // #197: the cursor-trailing tooltip is gone; the corner badge announces the page.
                ShowPageBadge(pageIndex);

                // Reuse this tab's cached bitmap for (page, resolution, rotation) if present; otherwise
                // rasterize once and cache it. On a switch back to a recent tab this skips pdfium entirely.
                int width, height;
                System.Windows.Media.Imaging.BitmapSource bitmap;
                PdfRenderedFrame? primaryFrame = null;
                // A page drawn under a monitor profile is cached under that profile's identity. Only the float frame the
                // linear path hands to the compositor (linear without a monitor conversion) has no bitmap to cache; one
                // whose monitor identity cannot be established is left uncached, as before.
                bool primaryCacheable = TryMonitorCacheIdentity(primaryMonitor, primaryDisplay, out string? primaryMonitorIdentity);
                bool primaryUsesCache = primaryCacheable && !(linear && primaryMonitor is null);
                var cached = !primaryUsesCache ? null
                    : TryGetCachedRender(_active, pageIndex, scaledMax, pgRot, proofStamp: primaryProof?.Stamp, monitorIdentity: primaryMonitorIdentity);
                if (cached != null)
                {
                    LastPrimaryRenderMeasurement = null;
                    bitmap = cached;
                    width  = cached.PixelWidth;
                    height = cached.PixelHeight;
                }
                else
                {
                    Task<(BitmapSource Bitmap, RenderMeasurement Measurement, PdfRenderedFrame? Frame)> render;
                    if (linear)
                    {
                        var revision = primaryNative?.CaptureReadRevision();
                        var request = primaryNative is null
                            ? CaptureRenderRequest(primaryFile, primaryRevision, floatFrames: true) : null;
                        render = _viewportRenderQueue.Enqueue(("primary", primarySession), 0,
                            (primaryProof?.Settings.Enabled == true ? 64L : 40L) * scaledMax * scaledMax,
                            token => PrepareLinearPrimaryBitmap(request, revision, pageIndex, scaledMax, token,
                                presentationPolicy.TransformOwner == PdfMonitorTransformOwner.Application ? _sdrDisplayConverter : null,
                                primaryProof?.Settings),
                            primaryToken);
                    }
                    else if (primaryNative is not null)
                    {
                        var revision = primaryNative.CaptureReadRevision();
                        render = _viewportRenderQueue.Enqueue(("primary", primarySession), 0,
                            (primaryProof?.Settings.Enabled == true ? 64L : 40L) * scaledMax * scaledMax,
                            token =>
                            {
                                var prepared = PrepareNativePrimaryBitmap(revision, pageIndex, scaledMax, pgRot, primaryInvert, token,
                                    primaryProof?.Settings, primaryMonitor);
                                return (prepared.Bitmap, prepared.Measurement, (PdfRenderedFrame?)null);
                            },
                            primaryToken);
                    }
                    else
                    {
                        var request = CaptureRenderRequest(primaryFile, primaryRevision,
                            floatFrames: primaryProof?.Settings.Enabled == true || primaryMonitor is not null);
                        render = _viewportRenderQueue.Enqueue(("primary", primarySession), 0,
                            (primaryProof?.Settings.Enabled == true ? 64L : primaryMonitor is not null ? 40L : 8L) * scaledMax * scaledMax,
                            token =>
                            {
                                var prepared = PreparePrimaryBitmap(request, pageIndex, scaledMax, pgRot, primaryInvert, token,
                                    primaryProof?.Settings, primaryMonitor);
                                return (prepared.Bitmap, prepared.Measurement, (PdfRenderedFrame?)null);
                            },
                            primaryToken);
                    }
                    var (preparedBitmap, preparedMeasurement, preparedFrame) = await render;
                    if (!Current()) return;
                    bitmap = preparedBitmap;
                    LastPrimaryRenderMeasurement = preparedMeasurement;
                    primaryFrame = preparedFrame;
                    width = bitmap.PixelWidth;
                    height = bitmap.PixelHeight;
                    if (primaryFrame is null && primaryUsesCache) CacheRender(primarySession, pageIndex, scaledMax, pgRot, bitmap,
                        proofStamp: primaryProof?.Stamp, monitorIdentity: primaryMonitorIdentity);
                }

                // Canonical render-dim space: longest side -> 2048 DIP, identical to the continuous and
                // secondary-tile paths. This is zoom-independent (aspect only), so the canvas/overlay size
                // is byte-for-byte stable across every zoom re-render - no left-shift when re-sharpening,
                // and annotation coordinates match across all view modes. LayoutTransform handles the zoom.
                double longest = Math.Max(1, Math.Max(width, height));
                int dipW = Math.Max(1, (int)Math.Round(2048.0 * width  / longest));
                int dipH = Math.Max(1, (int)Math.Round(2048.0 * height / longest));
                _renderDims[pageIndex] = (dipW, dipH);

                var presentationTimer = Stopwatch.StartNew();
                if (_primaryViewport is { } viewport)
                {
                    viewport.Width = dipW;
                    viewport.Height = dipH;
                }
                PageImage.Source = bitmap;
                PresentRenderWarning(PageImage, bitmap, pageIndex);
                _annotationCanvas.IsHitTestVisible = true;
                _annotationCanvas.Width  = dipW;
                _annotationCanvas.Height = dipH;
                _annotationCanvas.Tag    = pageIndex;   // so clicks on the primary page resolve to the
                                                        // page actually shown (page 0 in grid), not the
                                                        // selected index - otherwise annotations on it
                                                        // are unhittable and clicks "do nothing".
                ClearSelection();
                if (!keepTiles) ClearSecondaryPages();
                _pages[pageIndex] = _annotationCanvas;   // the primary is a normal entry in the unified map
                RenderAllAnnotations(pageIndex);
                PresentCurrentPageStatus();
                // Defer additional pages until layout has settled so ActualWidth is valid.
                // RenderPageLinks runs AFTER RenderAdditionalPages so ClearSecondaryPages
                // inside RenderAdditionalPages doesn't wipe the overlays we just added.
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (!Current()) return;
                    // Only Grid/Two-Page lay out neighbor tiles. In Single mode RenderAdditionalPages would
                    // snap the panel width to pageW + 12 (the inter-tile gap), nudging the lone page ~6px left
                    // of center - the shift seen a beat after a zoom-in, when the re-sharpen timer re-renders.
                    // Match RefreshPageView's single-mode handling instead: no extra tiles, auto panel width.
                    if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
                        RenderAdditionalPages(pageIndex);
                    else
                    {
                        ClearSecondaryPages();
                        _pageContentPanel?.Width = double.NaN;
                    }
                    RenderPageLinks(pageIndex, dipW, dipH);
                });
                _renderedPrimaryPage = pageIndex;
                if (primaryFrame is not null)
                {
                    await PresentPrimaryFrameAsync(primaryFrame, primaryDisplay, presentationPolicy,
                        pageIndex, primaryRevision, generation, primaryProofGeneration, Current, primaryToken);
                    if (!Current()) return;
                }
                StartRerenderTimer();
                if (LastPrimaryRenderMeasurement is { } measurement)
                    LastPrimaryRenderMeasurement = measurement with { PresentationMilliseconds = presentationTimer.Elapsed.TotalMilliseconds };
                // Fire background renders for the pages the user is most likely to reach next.
                // Cached bitmaps land in the same per-tab render cache TryGetCachedRender reads,
                // so the next PageDown/Left/Right skips the engine entirely.
                KickOffAdjacentPrefetch(pageIndex, scaledMax, pgRot, primaryUsesCache, primaryMonitor, primaryMonitorIdentity);
                // Navigation can supersede bootstrap while a remembered zoom is still waiting
                // for dimensions. The current owning render must finish that conversion too.
                if (Current() && _active?.PendingZoom != null)
                {
                    if (_active.PendingFitRestore != null)
                    {
                        await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        if (!Current()) return;
                    }
                    // Layout may already have applied the request. Do not turn its fit into a manual zoom.
                    TryApplyPendingViewport();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!Current()) return;
                PageImage.Source = null;
                SetStatus(string.Format(Loc("Str_RenderError"), ex.Message));
            }
        }

        /// <summary>
        /// Clears all dynamically-added secondary page borders from the panel,
        /// leaving only the first child (the primary page border).
        /// </summary>
        // Removes secondary tiles whose page is no longer shown (keeps the primary at index 0 and any
        // tile still in range so it can be reused in place). Keeps the tile map in sync.
        private void RemoveSecondaryTilesNotIn(HashSet<int> keep)
        {
            if (_pageContentPanel is null) return;
            var stale = new List<int>();
            foreach (var k in _continuousCanvases.Keys)
                if (!keep.Contains(k)) stale.Add(k);
            foreach (var pg in stale)
            {
                if (_continuousCanvases.TryGetValue(pg, out var ov) && ov.Parent is Grid g && g.Parent is Border tile)
                {
                    foreach (var gc in g.Children) if (gc is Image im) im.Source = null;
                    _pageContentPanel.Children.Remove(tile);
                }
                _continuousCanvases.Remove(pg);
                _pages.Remove(pg);
                _gridFresh.Remove(pg);
            }
        }

        private void ClearSecondaryPages()
        {
            if (_pageContentPanel is null) return;
            // Explicitly null out Image sources before removing so the GC can
            // reclaim the WriteableBitmap backing arrays promptly.
            while (_pageContentPanel.Children.Count > 1)
            {
                var child = _pageContentPanel.Children[^1];
                if (child is Border b && b.Child is Grid g)
                {
                    foreach (var gc in g.Children)
                        if (gc is Image img) img.Source = null;
                }
                _pageContentPanel.Children.RemoveAt(_pageContentPanel.Children.Count - 1);
            }
            // NOTE: do NOT reset _pageContentPanel.Width here.  Width is managed exclusively
            // by RenderAdditionalPages (which runs only via Dispatcher) so that no synchronous
            // call to ClearSecondaryPages triggers an intermediate layout pass that would cause
            // the primary page to flash centered and then jerk back to left-aligned.
            // Clear any link overlays from the annotation canvas.
            foreach (var lo in _linkOverlays)
                _annotationCanvas.Children.Remove(lo);
            _linkOverlays.Clear();
            _continuousCanvases.Clear();   // keep the page->tile map in sync with the visible tiles
            _gridFresh.Clear();
            _gridTileBudget = 0;
            // Unified map: keep only the CURRENT primary entry (key == _annotationCanvas.Tag) and drop
            // everything else - the secondary overlays and any stale primary entry from a prior page.
            int primPage = _annotationCanvas.Tag is int tp ? tp : -1;
            foreach (var pg in _pages.Keys.Where(k => k != primPage).ToList())
                _pages.Remove(pg);
        }

        /// <summary>
        /// Renders secondary pages as a grid. Panel-width setup is synchronous so layout
        /// is correct immediately; engine pixel rendering runs on a background thread so
        /// the UI stays responsive. WPF element creation returns to the UI thread.
        /// </summary>
        private async void RenderAdditionalPages(int primaryPageIdx)
        {
            // A fill pass (FillGridAroundCurrentPage) draws only Grid pages without a current bitmap.
            bool fillOnly = _gridFillPass;
            _gridFillPass = false;
            if (_currentFile is null || !HasReadDocument) return;
            // Grid is a stable overview anchored at page 0 (independent of the selected page), so it
            // always shows the whole document instead of only the selected page onward.
            if (_viewMode == ViewMode.Grid) primaryPageIdx = 0;

            // Re-assert the grid's ScrollViewer overrides on every tile layout. RefreshPageView is
            // where they normally land, but an unfocused pane's ApplyZoom SKIPS RefreshPageView (the
            // focus check guarding the tile-stream churn), so a grid entered in an unfocused pane
            // kept the 12px surround padding and Auto scrollbars: the panel overflowed by the
            // padding + vertical scrollbar width and a horizontal scrollbar appeared under the grid.
            if (_viewMode == ViewMode.Grid)
            {
                PagePreviewPanel.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                PagePreviewPanel.VerticalScrollBarVisibility   = ScrollBarVisibility.Visible;
                DocSurfacePad.Padding = new Thickness(0);
            }

            double viewportW = PagePreviewPanel.ActualWidth;
            // A transient zero width (a split drag rewriting the pane columns mid-layout) must NOT
            // wipe the tiles: clearing here is what blanked the grid on a two-pane resize and forced
            // the full one-at-a-time rebuild. Skip instead - the existing tiles stay put (transform-
            // stretched) and the resize-settle refresh swaps crisp bitmaps into them in place.
            if (viewportW <= 0) return;
            if (ReadPageCount <= 1)
            {
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                return;
            }
            // Partner tiles take the primary page's width, which RenderPage sets once that page is
            // on screen. A zoom or fit refresh while a document was still opening reached this with
            // no width yet (or the previous page's), built every partner at a stand-in width and
            // cached it: page 2 of the first spread showed at about a third of page 1's size.
            // RenderPage runs this pass again right after it sets the width, so waiting loses nothing.
            if (_annotationCanvas.Tag is not int shownPage || shownPage != primaryPageIdx
                || !(_annotationCanvas.Width > 0)) return;

            // Snap the WrapPanel width to a whole number of page-width slots.
            // Grid slots carry a constant GridGapPx on-screen gap (divided by zoom because tile
            // margins scale with the view transform); other tiled modes keep the 12px gap.
            double primaryPageW = _annotationCanvas.Width;
            bool bookCover = _viewMode == ViewMode.TwoPage && BookMode && primaryPageIdx == 0;
            double pageSlotW = primaryPageW + (_viewMode == ViewMode.Grid
                ? GridGapPx / Math.Max(0.01, _zoomLevel) : bookCover ? 0 : 12);
            double availablePreZoom = (viewportW - 24) / _zoomLevel;
            // +1e-6: same floating-point underflow guard as GridZoomStep, so a zoom set for n columns
            // actually lays out n (not n-1) when the division lands a hair under the integer.
            // Grid lays out its AUTHORITATIVE column count (set on grid zoom, restored per tab); it no longer
            // derives columns from the zoom here - that zoom->columns->zoom round-trip lost the grid zoom on
            // tab switches. Other modes still fit to the current zoom.
            // #193: a book-layout cover is a ONE-page row - sizing the panel for two slots parked
            // the cover in the left half of a centered two-slot panel, which read as left-aligned.
            int pagesPerRow = _viewMode == ViewMode.TwoPage ? (bookCover ? 1 : 2)
                            : _viewMode == ViewMode.Grid    ? Math.Max(1, _gridColumns)
                            : Math.Max(1, (int)(availablePreZoom / pageSlotW + 1e-6));
            // +0.5/slot: secondary tiles round their DIP width to a whole pixel (AddSecondaryTile),
            // so a row can measure up to half a pixel per tile wider than n exact slots. Without
            // this slack the WrapPanel wraps the row's last tile early and the grid shows n-1
            // columns with a dead column of background on the right. The slack is far below one
            // slot width, so it can never admit an extra column.
            double panelW = pagesPerRow * (pageSlotW + 0.5);
            if (panelW > 0) _pageContentPanel.Width = panelW;

            // A grid zoom changes the pre-transform margin that keeps the visible seam at GridGapPx.
            // Update every existing tile in one layout pass, before the async bitmap stream starts.
            // Doing this from AddSecondaryTile made each seam move only when that page's render callback
            // arrived, so the already-zoomed grid visibly settled one page at a time.
            if (_viewMode == ViewMode.Grid)
            {
                double gapDip = GridGapPx / Math.Max(0.01, _zoomLevel);
                var margin = new Thickness(0, 0, gapDip, gapDip);
                foreach (var child in _pageContentPanel.Children)
                    if (child is Border tile) tile.Margin = margin;
            }

            // Cancel any previously running secondary render.
            CancelAndRelease(_secondaryRenderCts);
            _secondaryRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _secondaryRenderCts;

            // A two-page spread displays both pages at the same size, so both must be rasterized at
            // the same effective pixel density. The old secondary budget was only 1536 pixels and
            // did not follow zoom, while the primary used a 2048-pixel zoom-aware budget. WPF then
            // enlarged the right page from fewer pixels, making it visibly softer than the left.
            // Grid draws each tile at the size it shows in device pixels (GridTileBudget), so a small tile is not drawn
            // large and a zoomed-in one is not enlarged from too few pixels.
            var secondaryDpi = VisualTreeHelper.GetDpi(this);
            int secondaryMax = _viewMode == ViewMode.Grid ? GridTileBudget() : Math.Min(2048, ViewerRenderResolution.Secondary(
                _viewMode == ViewMode.TwoPage,
                secondaryDpi.DpiScaleX,
                secondaryDpi.DpiScaleY,
                _zoomLevel));
            // Grid shows the whole document; Two-Page shows one secondary; other modes peek ahead.
            int limit = _viewMode == ViewMode.Grid
                ? ReadPageCount
                : Math.Min(ReadPageCount, primaryPageIdx + 1 + (_viewMode == ViewMode.TwoPage
                    ? (BookMode && primaryPageIdx == 0 ? 0 : 1)   // #193: the cover has no partner
                    : 25));
            if (limit <= primaryPageIdx + 1) { ClearSecondaryPages(); return; }

            // Per-tile reuse: drop tiles for pages that left the view, keep the rest. Pages that already
            // have a tile get their bitmap swapped in place (AddSecondaryTile); only genuinely new pages
            // are built. Stays smooth even mid-stream on a large doc, where the tile set is only partly
            // built. (Navigation clears everything via RenderPage first, so it rebuilds.)
            var keepPages = new HashSet<int>();
            for (int i = primaryPageIdx + 1; i < limit; i++) keepPages.Add(i);
            RemoveSecondaryTilesNotIn(keepPages);

            string currentFile = _currentFile;

            // Collect rotations on the UI thread before the background task.
            var secRotations = new Dictionary<int, int>();
            for (int i = primaryPageIdx + 1; i < limit; i++)
                if (_pageRotations.TryGetValue(i, out int r) && r != 0)
                    secRotations[i] = r;

            // Capture the primary page width and reset the tile map on the UI thread before
            // streaming tiles in from the background render.
            double primaryDipW = _annotationCanvas.Width;

            // Grid lays out a slot for every page before drawing any, in page order, so a jump can scroll to any page at
            // once; the pages are then drawn outward from the page being looked at. Building tiles only as their bitmaps
            // arrived, in page order, dropped a jump past the pages drawn so far.
            int renderCenter = primaryPageIdx + 1;
            int renderFirst = primaryPageIdx + 1, renderLast = limit - 1;
            if (_viewMode == ViewMode.Grid)
            {
                renderCenter = Math.Clamp(State.CurrentPage, primaryPageIdx + 1, limit - 1);
                // Grid draws the pages within two screens of the page being looked at and lets go of the bitmaps of
                // pages further off, so its memory and its drawing follow the viewport, not the length of the document.
                int reach = GridPagesPerScreen() * 2;
                renderFirst = Math.Max(primaryPageIdx + 1, renderCenter - reach);
                renderLast = Math.Min(limit - 1, renderCenter + reach);
                // The slots through the pages drawn now are laid out at once; the rest follow in batches, since all
                // 5,000 slots of a long PDF took about half a second in one frame. A jump past them lays them out first.
                AddGridPlaceholders(primaryPageIdx + 1, renderLast + 1, primaryDipW);
                QueueGridPlaceholders();
                ReleaseGridTilesOutside(renderCenter - reach * 2, renderCenter + reach * 2);
                // A zoom or monitor change makes the tiles' size on screen differ from the one they were drawn at. The
                // tiles on show stretch until the change settles, then the scroll-settle timer draws them again once.
                if (secondaryMax != _gridTileBudget && _gridTileBudget > 0 && !_gridBudgetSettled)
                {
                    StartRerenderTimer();
                    return;
                }
                _gridBudgetSettled = false;
                _gridTileBudget = secondaryMax;
                if (!fillOnly) _gridFresh.Clear();
            }
            _gridRenderCenter = renderCenter;
            int[] renderOrder = [.. ContinuousRenderOrder.Around(renderCenter, renderFirst, renderLast)
                .Where(page => !fillOnly || !_gridFresh.Contains(page))];

            // Render pixels on a background thread and attach each page tile to the UI as soon
            // as it is ready, so large documents fill in progressively instead of blocking
            // until every page has been rendered.
            try
            {
                var session = _active;
                var nativeOwner = _nativeDoc;
                var nativeRevision = nativeOwner?.CaptureReadRevision();
                var proof = session?.ProofView.CaptureProof();
                long revision = session?.RenderRevision ?? 0;
                var mode = _viewMode;
                bool invert = DocInvert;
                var secondaryDisplay = CapturePrimaryDisplay();
                var secondaryMonitor = !invert
                    && PdfDisplayPresentationPolicy.Resolve(secondaryDisplay).TransformOwner == PdfMonitorTransformOwner.Application
                    ? _sdrDisplayConverter : null;
                bool secondaryCacheable = TryMonitorCacheIdentity(secondaryMonitor, secondaryDisplay, out string? secondaryMonitorIdentity);
                long secondaryCmyk = KillerPDF.Services.WorkingCmykProfiles.Generation;
                bool Current() => secondaryCmyk == KillerPDF.Services.WorkingCmykProfiles.Generation
                    && !cts.IsCancellationRequested && HasReadDocument
                    && ReferenceEquals(nativeOwner, _nativeDoc)
                    && ReferenceEquals(secondaryDisplay, _presentationDisplay)
                    && ReferenceEquals(session, _active) && currentFile == _currentFile
                    && revision == (_active?.RenderRevision ?? 0) && mode == _viewMode && invert == DocInvert
                    && proof?.Stamp == _active?.ProofView.CaptureProof().Stamp;
                // Cache by the actual raster budget, not the tile's fixed logical width. Two-page
                // rendering follows zoom, while primaryDipW remains constant; using that width as
                // the key let a lower-resolution bitmap from an earlier zoom replace the sharp
                // partner page when the delayed re-render settled. Keep tile keys negative so a
                // tile bitmap whose baked logical geometry follows the primary page cannot collide
                // with a primary bitmap rendered at the same pixel budget.
                int tileBucket = -secondaryMax;
                var renderRequest = nativeRevision is null ? CaptureRenderRequest(currentFile, session?.RenderRevision ?? 0,
                    floatFrames: proof?.Settings.Enabled == true || secondaryMonitor is not null) : null;
                var secondaryToken = cts.Token;
                await System.Threading.Tasks.Task.Run(async () =>
                {
                    ContentDoc? contentDoc = null;   // opened lazily by ImageRectsFor on the first uncached page
                    try
                    {
                        foreach (int i in renderOrder)
                        {
                            if (cts.IsCancellationRequested) break;
                            int rot = secRotations.TryGetValue(i, out int rr) ? rr : 0;

                            // Cache hit: skip pdfium entirely, just attach the cached tile bitmap.
                            var cachedTile = secondaryCacheable
                                ? TryGetCachedRender(session, i, tileBucket, rot, proofStamp: proof?.Stamp,
                                    monitorIdentity: secondaryMonitorIdentity) : null;
                            // The cache key has no width: a tile built beside a primary page of
                            // another width (book layout pairs pages differently, Grid anchors at
                            // page 0) would show at the wrong size, so render it again instead.
                            if (cachedTile != null && Math.Abs(cachedTile.Width - Math.Round(primaryDipW)) > 0.5)
                                cachedTile = null;
                            if (cachedTile != null)
                            {
                                int pic = i;
                                try
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (!Current()) return;
                                        if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.TwoPage) return;
                                        AddSecondaryTile(pic, cachedTile, primaryDipW);
                                    });
                                }
                                catch (System.Threading.Tasks.TaskCanceledException) { break; }
                                catch (OperationCanceledException) { break; }
                                continue;
                            }

                            // One bad page (a pdfium hiccup, a malformed object) must not kill the
                            // rest of the stream - it silently stranded every page after it with no
                            // tile, which read as "the grid's last column never refreshed".
                            try
                            {
                                if (nativeRevision is not null)
                                {
                                    var prepared = await _viewportRenderQueue.Enqueue(
                                        (nativeRevision, "secondary", i), 10 + Math.Abs(i - renderCenter),
                                        Math.Min((proof?.Settings.Enabled == true ? 64L : 40L) * secondaryMax * secondaryMax, 512L << 20),
                                        token => PrepareNativePrimaryBitmap(nativeRevision, i, secondaryMax, rot, invert, token,
                                            proof?.Settings, secondaryMonitor), secondaryToken);
                                    int nativePage = i;
                                    if (cts.IsCancellationRequested) break;
                                    // The prepared bitmap is frozen: copy and scale it here, on the render thread, and
                                    // leave only the attach to the UI thread.
                                    var source = prepared.Bitmap;
                                    byte[] pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
                                    source.CopyPixels(pixels, source.PixelWidth * 4, 0);
                                    int dipWidth = Math.Max(1, (int)Math.Round(primaryDipW));
                                    int dipHeight = Math.Max(1, (int)Math.Round(primaryDipW * source.PixelHeight / source.PixelWidth));
                                    var bitmap = BitmapHelpers.BuildScaledBitmap(source.PixelWidth, source.PixelHeight,
                                        pixels, dipWidth, dipHeight);
                                    if (RenderWarnings.TryGetValue(source, out var warning))
                                        RenderWarnings.GetValue(bitmap, _ => warning);
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (!Current()) return;
                                        if (_viewMode is not (ViewMode.Grid or ViewMode.TwoPage)) return;
                                        if (secondaryCacheable)
                                            CacheRender(session, nativePage, tileBucket, rot, bitmap, proofStamp: proof?.Stamp,
                                                monitorIdentity: secondaryMonitorIdentity);
                                        AddSecondaryTile(nativePage, bitmap, primaryDipW);
                                    });
                                    continue;
                                }
                                var rendered = await _viewportRenderQueue.Enqueue(
                                    (renderRequest, "secondary", i), 10 + Math.Abs(i - renderCenter),
                                    Math.Min((proof?.Settings.Enabled == true ? 64L : secondaryMonitor is not null ? 40L : 8L)
                                        * secondaryMax * secondaryMax, 512L << 20), token =>
                                    {
                                        using var lease = renderRequest!.Rent(token);
                                        return RenderViewingPage(lease, i, secondaryMax, token, proof?.Settings,
                                            monitor: secondaryMonitor);
                                    }, secondaryToken);
                                int w = rendered.Width;
                                int h = rendered.Height;
                                byte[] rawBytes = rendered.Pixels;
                                if (w <= 0 || h <= 0 || rawBytes is null) continue;
                                // #135: dark mode with pictures excluded; invert before the rotation
                                // so the carve-out rects stay in unrotated page space.
                                if (invert)
                                    BitmapHelpers.InvertBgraInPlaceExcept(rawBytes, w, h, ImageRectsFor(renderRequest!, i, ref contentDoc));
                                if (rot != 0)
                                    (rawBytes, w, h) = BitmapHelpers.RotateBitmap(rawBytes, w, h, rot);

                                int pi = i, pw = w, ph = h, prot = rot;
                                byte[] bytes = rawBytes;
                                Dispatcher.Invoke(() =>
                                {
                                    if (!Current()) return;
                                    if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.TwoPage) return;
                                    int ddw = Math.Max(1, (int)Math.Round(primaryDipW));
                                    int ddh = Math.Max(1, (int)Math.Round(primaryDipW * ph / pw));
                                    var bmp = BitmapHelpers.BuildScaledBitmap(pw, ph, bytes, ddw, ddh);
                                    RememberRenderWarning(bmp, rendered);
                                    NoteStampedRender(renderRequest, pi, bmp);
                                    if (secondaryCacheable)
                                        CacheRender(session, pi, tileBucket, prot, bmp, proofStamp: proof?.Stamp,
                                            monitorIdentity: secondaryMonitorIdentity);
                                    AddSecondaryTile(pi, bmp, primaryDipW);
                                });
                            }
                            // Dispatcher.Invoke throws when the dispatcher is shutting down (app closing) or
                            // the render was canceled; stop rendering cleanly instead of crashing.
                            catch (System.Threading.Tasks.TaskCanceledException) { break; }
                            catch (OperationCanceledException) { break; }
                            catch { continue; }   // skip this page, keep streaming the rest
                        }
                    }
                    finally { contentDoc?.Dispose(); }
                }, secondaryToken);
            }
            catch (OperationCanceledException) { return; }   // superseded - the newer render owns the tiles now
            catch
            {
                // A transient failure mid-stream must not strand the tail of the grid unrendered
                // (the "last column never refreshed" symptom). Retry once - the flag stops a
                // persistently broken document from looping, and cache hits make the retry cheap.
                if (!_secondaryRenderRetried && !cts.IsCancellationRequested)
                {
                    _secondaryRenderRetried = true;
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        (Action)(() =>
                        {
                            if (_viewMode is ViewMode.Grid or ViewMode.TwoPage) RenderAdditionalPages(primaryPageIdx);
                        }));
                }
                return;
            }
            _secondaryRenderRetried = false;   // a complete pass clears the retry budget
        }

        // One-shot retry budget for RenderAdditionalPages - set when a failed stream schedules its
        // retry, cleared by any pass that completes. Keeps a broken document from retry-looping.
        private bool _secondaryRenderRetried;

        /// <summary>
        /// Builds one secondary-page tile (image + annotation overlay + links) and appends it
        /// to the page content panel. Must run on the UI thread.
        /// </summary>
        // The page the current Grid pass draws outward from.
        private int _gridRenderCenter = -1;
        // Set for one pass that draws only the Grid pages missing a current bitmap.
        private bool _gridFillPass;
        // The tile size the Grid's bitmaps were drawn at, and whether a changed size has settled and may be drawn.
        private int _gridTileBudget;
        private bool _gridBudgetSettled;

        /// <summary>The longest side, in device pixels, a Grid tile shows at now.</summary>
        private int GridTileBudget()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double longest = Math.Max(_annotationCanvas.Width, _annotationCanvas.Height);
            return ViewerRenderResolution.GridTile(double.IsFinite(longest) && longest > 0 ? longest : 2048,
                dpi.DpiScaleX, dpi.DpiScaleY, _zoomLevel);
        }
        // Grid pages whose bitmap the latest full pass (or a fill pass after it) drew.
        private readonly HashSet<int> _gridFresh = [];

        /// <summary>Draws the Grid pages around the current page that have no current bitmap: after a jump, or when a
        /// scroll settles on pages let go of or not drawn yet. Pages already drawn are left as they are.</summary>
        private void FillGridAroundCurrentPage()
        {
            if (_viewMode != ViewMode.Grid) return;
            _gridFillPass = true;
            RenderAdditionalPages(0);
        }

        /// <summary>True when a page within a screen of the current page has no current Grid bitmap.</summary>
        private bool GridNeedsFillAroundCurrentPage()
        {
            if (_viewMode != ViewMode.Grid || State.CurrentPage < 0) return false;
            int span = GridPagesPerScreen();
            int last = Math.Min(ReadPageCount - 1, State.CurrentPage + span);
            for (int page = Math.Max(1, State.CurrentPage - span); page <= last; page++)
                if (GridTileIsBlank(page)) return true;
            return false;
        }

        /// <summary>About how many Grid pages one screen shows: the columns times the rows that fit, plus a row.</summary>
        private int GridPagesPerScreen()
        {
            int columns = Math.Max(1, _gridColumns);
            double tileHeight = Math.Max(1, _annotationCanvas.Height) * Math.Max(0.01, _zoomLevel);
            double viewport = PagePreviewPanel.ViewportHeight > 0 ? PagePreviewPanel.ViewportHeight : PagePreviewPanel.ActualHeight;
            int rows = Math.Max(1, (int)Math.Ceiling(Math.Max(1, viewport) / tileHeight)) + 1;
            return Math.Max(6, columns * rows);
        }

        /// <summary>Lets go of the bitmaps of Grid pages outside the range; their slots keep their size.</summary>
        private void ReleaseGridTilesOutside(int first, int last)
        {
            foreach (var (page, overlay) in _continuousCanvases)
            {
                if (page >= first && page <= last) continue;
                if (overlay.Parent is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Image { Source: not null } image)
                {
                    image.Source = null;
                    _gridFresh.Remove(page);
                }
            }
        }

        /// <summary>Appends an empty Grid slot, sized from the page's own proportions, for each page from
        /// <paramref name="first"/> up to <paramref name="end"/> that has none, in page order, so the panel's child
        /// <c>i</c> is page <c>i</c>. Links and annotations wait for the page's bitmap.</summary>
        private void AddGridPlaceholders(int first, int end, double primaryDipW)
        {
            int pageDipW = (int)Math.Round(primaryDipW);
            if (pageDipW <= 0) return;
            // An empty slot is a sized white Border with no content, as in Continuous. Thousands of full slots (an
            // image, an overlay and their grid each) made every later frame of a long document slower, and a jump
            // far ahead built them all at once. AddSecondaryTile fills a slot in place when its bitmap arrives.
            for (int pi = Math.Max(first, _pageContentPanel.Children.Count); pi < end; pi++)
            {
                var (width, height) = ReadVisualPageSize(pi);
                int pageDipH = Math.Max(1, (int)Math.Round(primaryDipW * height / Math.Max(1e-6, width)));
                _pageContentPanel.Children.Add(new Border
                {
                    Background = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Top,
                    Width = pageDipW,
                    Height = pageDipH,
                    Margin = new Thickness(0, 0, GridGapPx / Math.Max(0.01, _zoomLevel), GridGapPx / Math.Max(0.01, _zoomLevel))
                });
            }
        }

        // Grid slots are always a prefix of the pages (child i is page i), so the next one to lay out is the child count.
        private const int GridSlotBatch = 100;
        private bool _gridSlotsQueued;

        /// <summary>Lays out the Grid slots not laid out yet in small batches when the window is idle, so entering Grid
        /// on a long document does not hold the window, and the Grid's own fit and first pages are never kept waiting
        /// behind the batches (at background priority they held the fit back by about three seconds).</summary>
        private void QueueGridPlaceholders()
        {
            if (_gridSlotsQueued || _viewMode != ViewMode.Grid || _pageContentPanel.Children.Count >= ReadPageCount) return;
            _gridSlotsQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, (Action)(() =>
            {
                _gridSlotsQueued = false;
                // Grid left, the document closed, or the panel holds no primary page yet: nothing to extend.
                if (_viewMode != ViewMode.Grid || !HasReadDocument || _pageContentPanel.Children.Count == 0) return;
                int next = _pageContentPanel.Children.Count;
                AddGridPlaceholders(next, Math.Min(ReadPageCount, next + GridSlotBatch), _annotationCanvas.Width);
                QueueGridPlaceholders();
            }));
        }

        /// <summary>Lays out the Grid slots through the page now, for a jump to a page whose slot is still queued.</summary>
        private void EnsureGridSlotsThrough(int page)
        {
            if (_viewMode != ViewMode.Grid || _pageContentPanel.Children.Count == 0 || page < _pageContentPanel.Children.Count) return;
            AddGridPlaceholders(_pageContentPanel.Children.Count, Math.Min(ReadPageCount, page + 1), _annotationCanvas.Width);
        }

        /// <summary>True when the page has a Grid slot whose bitmap has not arrived yet.</summary>
        private bool GridTileIsBlank(int page) =>
            _viewMode == ViewMode.Grid && (_continuousCanvases.TryGetValue(page, out var overlay)
                ? overlay.Parent is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Image { Source: null }
                : page > 0 && page < _pageContentPanel.Children.Count && _pageContentPanel.Children[page] is Border { Child: null });

        private void AddSecondaryTile(int pi, System.Windows.Media.Imaging.BitmapSource bitmap, double primaryDipW)
        {
            if (_viewMode == ViewMode.Grid) _gridFresh.Add(pi);
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            int pageDipW = (int)Math.Round(primaryDipW);
            int pageDipH = (int)Math.Round(primaryDipW * h / w);

            // This page already has a tile: swap just the bitmap (same logical size, crisper pixels).
            // No clear, no reflow - so the grid/spread never jumps or blinks.
            if (_continuousCanvases.TryGetValue(pi, out var exOverlay)
                && exOverlay.Parent is Grid exGrid && exGrid.Children.Count > 0 && exGrid.Children[0] is Image exImg)
            {
                // A Grid slot laid out ahead of its bitmap takes the page's real size, links and annotations now.
                if (exImg.Source is null)
                {
                    exImg.Width = pageDipW;
                    exImg.Height = pageDipH;
                    exOverlay.Width = pageDipW;
                    exOverlay.Height = pageDipH;
                    if (!_renderDims.ContainsKey(pi)) _renderDims[pi] = (pageDipW, pageDipH);
                    exImg.Source = bitmap;
                    RenderAllAnnotations(pi);
                    AddSecondaryPageLinks(pi, pageDipW, pageDipH);   // after: drawing annotations clears the canvas
                }
                exImg.Source = bitmap;
                ShowStampElementsFor(exGrid);   // #478: in the same step as the bitmap that holds the stamps, or not
                PresentRenderWarning(exImg, bitmap, pi);
                // Keep the grid's constant on-screen gap exact across zoom/column changes: the
                // reused tile's margin was computed at the old zoom, so refresh it here.
                if (_viewMode == ViewMode.Grid && exGrid.Parent is Border exTile)
                {
                    double exGap = GridGapPx / Math.Max(0.01, _zoomLevel);
                    exTile.Margin = new Thickness(0, 0, exGap, exGap);
                }
                return;
            }

            // Do NOT overwrite _renderDims if the page was already rendered as primary -
            // its annotation coordinate mapping must stay intact.
            if (!_renderDims.ContainsKey(pi))
                _renderDims[pi] = (pageDipW, pageDipH);

            var img = new Image { Source = bitmap, Stretch = Stretch.None };
            PresentRenderWarning(img, bitmap, pi);
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var overlay = BuildPageOverlay(pi, pageDipW, pageDipH, null);
            overlay.Cursor = CursorForTool(_currentTool);

            var pageGrid = new Grid();
            pageGrid.Children.Add(img);
            pageGrid.Children.Add(overlay);

            // Grid: constant GridGapPx on-screen gap between tiles (the document pane
            // background shows through). Two-Page: no margin on the right page - the spread
            // gap is the primary's right margin, and a bottom/trailing margin would make the
            // fit-page margins uneven.
            var margin = _viewMode == ViewMode.Grid
                ? new Thickness(0, 0, GridGapPx / Math.Max(0.01, _zoomLevel), GridGapPx / Math.Max(0.01, _zoomLevel))
                : new Thickness(0);
            // A Grid page laid out ahead of its bitmap has an empty slot at its index: fill that slot in place.
            if (_viewMode == ViewMode.Grid && pi < _pageContentPanel.Children.Count
                && _pageContentPanel.Children[pi] is Border { Child: null } slot)
            {
                slot.Width = double.NaN;
                slot.Height = double.NaN;
                slot.Margin = margin;
                slot.Child = pageGrid;
                RenderAllAnnotations(pi);
                AddSecondaryPageLinks(pi, pageDipW, pageDipH);
                if (pi == _gridScrollToPage) ScrollToGridTile(slot);
                return;
            }
            var tile = new Border
            {
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = margin,
                Child = pageGrid
            };
            _pageContentPanel.Children.Add(tile);
            RenderAllAnnotations(pi);
            AddSecondaryPageLinks(pi, pageDipW, pageDipH);
            if (_viewMode == ViewMode.TwoPage && pi == State.CurrentPage)
            {
                if (_active?.PendingZoom is null) SyncZoomBox();
                else if (_active.PendingFitRestore != null)
                {
                    var expectedSession = _active;
                    string? expectedFile = _currentFile;
                    long expectedRevision = expectedSession.RenderRevision;
                    long expectedGeneration = _primaryRenderGeneration;
                    bool CurrentFit() => ReferenceEquals(_active, expectedSession) && _currentFile == expectedFile
                        && expectedSession.RenderRevision == expectedRevision && _primaryRenderGeneration == expectedGeneration
                        && _viewMode == ViewMode.TwoPage && State.CurrentPage == pi;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
                    {
                        if (!CurrentFit()) return;
                        UpdateLayout();
                        if (CurrentFit()) TryApplyPendingViewport();
                    }));
                }
                else TryApplyPendingViewport();
            }

            // Grid tiles render asynchronously, so a "scroll to page N" requested when entering grid
            // can't run until page N's tile exists. Do it the moment that tile streams in.
            if (pi == _gridScrollToPage)
                ScrollToGridTile(tile);
        }

        private void ScrollToGridTile(Border tile)
        {
            _gridScrollToPage = -1;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                (Action)(() =>
                {
                    if (_viewMode != ViewMode.Grid) return;
                    try
                    {
                        // Top-align the page's row in the viewport (accounts for the zoom transform).
                        if (PagePreviewPanel.Content is FrameworkElement content)
                            PagePreviewPanel.ScrollToVerticalOffset(
                                tile.TransformToVisual(content).Transform(new Point(0, 0)).Y);
                        else
                            tile.BringIntoView();
                    }
                    catch { tile.BringIntoView(); }
                }));
        }

        internal void BootstrapDocumentView(int initialPage, bool autoFit, bool restoreFitMode = false,
            double? restoreHorizontalOffset = null, double? restoreVerticalOffset = null, double? restoreTrueZoom = null)
        {
            DocumentSession? expectedSession = _active;
            if (expectedSession != null && (restoreFitMode || autoFit))
            {
                ClearPendingTrueZoom();
                if (_viewMode != ViewMode.Grid)
                {
                    if (autoFit || _fitMode is FitMode.Width or FitMode.Page)
                    {
                        expectedSession.PendingFitRestore = autoFit ? FitMode.Page : _fitMode;
                        expectedSession.PendingZoom = new DeferredManualZoom(1.0);
                    }
                    else if (restoreTrueZoom is > 0 && double.IsFinite(restoreTrueZoom.Value))
                        expectedSession.PendingZoom = new DeferredManualZoom(restoreTrueZoom.Value);
                }
            }
            long initialIntent = expectedSession?.ViewportIntentRevision ?? 0;
            bool InitialIntentCurrent() => (expectedSession?.ViewportIntentRevision ?? 0) == initialIntent;
            // The document is (re)displaying - usually a different one (tab switch/close/open). The
            // skip-render guard in PageList_SelectionChanged compares the target page to the last
            // rasterised page (_renderedPrimaryPage) but not to WHICH document, so a switch to another
            // doc at the same page index + zoom would skip the render and leave the previous doc on
            // screen. Invalidate it here so the new document always renders.
            _renderedPrimaryPage = -1;
            ClearSecondaryPages();
            ClearSelection();
            RefreshPageList();
            LoadOutlines();
            DropZone.Visibility = Visibility.Collapsed;
            PagePreviewPanel.Visibility = Visibility.Visible;
            if (Host != null)
            {
                Host.CloseFileEnabled = true;
                Host.PageJumpEnabled = true;
                Host.PageTotalText = $"/ {ReadPageCount}";
            }
            if (ReadPageCount > 0)
            {
                int page = Math.Max(0, Math.Min(initialPage, ReadPageCount - 1));
                // Show the panel that matches THIS tab's view mode. This must run for every mode, not
                // only Continuous: switching from a Continuous tab to a Single/Two-Page/Grid tab has to
                // collapse the continuous panel, otherwise the previous tab's continuous render stays on
                // screen over the new document.
                bool isContinuous = _viewMode.IsContinuous();
                _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
                _continuousPanel.Visibility  = isContinuous ? Visibility.Visible   : Visibility.Collapsed;
                _currentPage = page;
                // Continuous's SelectionChanged returns early (no RenderPage call), so build its panel here.
                if (isContinuous)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () =>
                        {
                            if (expectedSession is not null && !ReferenceEquals(_active, expectedSession)) return;
                            SetupContinuousView(page, fitDefault: autoFit, restoreVerticalOffset);
                        });
                // RefreshPageList re-seats the sidebar under _syncingPageList, so the selection
                // handler never renders the primary for a new document. Render it here, or the
                // previous tab's page stays on screen in Single, Two-Page and Grid.
                else
                    RenderPage(_viewMode == ViewMode.Grid ? 0 : page);
                var initialRender = isContinuous ? Task.CompletedTask : PrimaryRenderTask;
                IInputElement? focusBeforeInitialRender = Keyboard.FocusedElement;
                long initialGeneration = _primaryRenderGeneration;
                var initialMode = _viewMode;
                bool InitialCurrent() => ReferenceEquals(_active, expectedSession)
                    && initialMode == _viewMode && initialGeneration == _primaryRenderGeneration;
                // Raster completion is asynchronous; dispatcher priority alone cannot order fit after it.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(async () =>
                    {
                        await initialRender;
                        if (!InitialCurrent()) return;
                        await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        if (!InitialCurrent()) return;
                        if (autoFit && InitialIntentCurrent())
                        {
                            // Grid opens to its 3-across default; other modes fit to width.
                            if (_viewMode == ViewMode.Grid)
                            {
                                _gridColumns = Math.Min(ReadPageCount, 3);
                                SetZoom(GridZoomForN(_gridColumns));
                            }
                            else
                                FitToPage();
                        }
                        else if (restoreFitMode && InitialIntentCurrent())
                        {
                            // Fit modes use the current window. Manual zoom uses a mode-independent
                            // percentage when available; old records retain their internal-scale fallback.
                            if (_viewMode == ViewMode.Grid)
                            {
                                _gridColumns = Math.Min(ReadPageCount, 3);
                                SetZoom(GridZoomForN(_gridColumns));
                            }
                            else if (_fitMode == FitMode.Width)   FitToWidth();
                            else if (_fitMode == FitMode.Page)    FitToPage();
                            else                                  RestoreManualZoom();
                        }
                        else if (InitialIntentCurrent())
                        {
                            // Tab restore: keep the document's saved zoom. Grid's zoom is really "how many
                            // columns" for the CURRENT window width, and its SizeChanged/settle handlers
                            // recompute it as GridZoomForN(_gridColumns); replay the saved column count the
                            // same way so the two agree instead of fighting (a raw saved zoom from a different
                            // width loses). _gridColumns is restored per tab in ApplySessionState.
                            if (_viewMode == ViewMode.Grid) SetZoom(GridZoomForN(_gridColumns));
                            else RestoreManualZoom();
                        }
                        // #196: opening a file from Explorer left keyboard focus outside the document
                        // view (WPF's initial focus can land in the page-jump box, whose editable-
                        // TextBox guard swallows every shortcut), so arrows and PageUp/Down did
                        // nothing until a click. Do not take focus back from a control the user
                        // reached while the initial render was completing, such as a signature picker.
                        if (Window.GetWindow(this) is { IsActive: true }
                            && ReferenceEquals(Keyboard.FocusedElement, focusBeforeInitialRender)
                            && Keyboard.FocusedElement is not TextBox { IsReadOnly: false })
                            FocusDocumentView();
                        if (restoreHorizontalOffset.HasValue || restoreVerticalOffset.HasValue)
                        {
                            await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                            if (!InitialCurrent()) return;
                            if (restoreHorizontalOffset.HasValue)
                                PagePreviewPanel.ScrollToHorizontalOffset(restoreHorizontalOffset.Value);
                            if (restoreVerticalOffset.HasValue)
                                PagePreviewPanel.ScrollToVerticalOffset(restoreVerticalOffset.Value);
                        }
                    }));
            }
        }

        internal void RefreshPageView(int pageIndex)
        {
            if (_viewMode.IsContinuous())
                return; // continuous mode manages its own rendering
            if (_viewMode == ViewMode.TwoPage) pageIndex = SpreadStart(pageIndex);   // snap to the spread's left page (#193: book-aware)

            // Grid fits its columns to the viewport, so it never needs a horizontal scrollbar.
            // Leaving it on Auto shows a stray (green) thumb across the bottom when the tile panel
            // overflows by the vertical scrollbar's width. Disable it for grid, Auto elsewhere.
            PagePreviewPanel.HorizontalScrollBarVisibility =
                _viewMode == ViewMode.Grid ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            // Reserve the vertical scrollbar in Grid so its appearing/disappearing can't change the
            // viewport width mid-resize and feed a width change back into the layout (the loop the grid
            // used to guard against). A stable width lets the column-holding resize stay stable too.
            PagePreviewPanel.VerticalScrollBarVisibility =
                _viewMode == ViewMode.Grid ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto;
            // Grid is edge-to-edge: zero the document-surface padding so tiles reach the pane
            // edges (other modes keep the 12px surround; Continuous restores it in
            // SetupContinuousView, which never passes through here).
            DocSurfacePad.Padding = new Thickness(_viewMode == ViewMode.Grid ? 0 : 12);
            // Two-Page centers the document surface vertically so Fit Page leaves EVEN top and
            // bottom margins (top-anchored, all the leftover height landed at the bottom). The
            // other modes stay top-anchored; Continuous restores this in SetupContinuousView.
            DocSurfacePad.VerticalAlignment = _viewMode == ViewMode.TwoPage
                ? VerticalAlignment.Center : VerticalAlignment.Top;
            // Primary-tile margin per mode: Single is centered with no gap; Grid gets the constant
            // GridGapPx on-screen tile gap; Two-Page keeps only the 12px spread gap on the right
            // (a bottom margin would make the fit-page margins vertically uneven).
            if (_pageContentPanel is not null && _pageContentPanel.Children.Count > 0
                && _pageContentPanel.Children[0] is Border primaryBorder)
            {
                double gapDip = GridGapPx / Math.Max(0.01, _zoomLevel);
                // #193: a book-layout cover has no facing page, so it drops the 12px spread gap
                // and centers like a Single page instead of hanging left of an empty slot.
                bool coverAlone = _viewMode == ViewMode.TwoPage && BookMode && pageIndex == 0;
                primaryBorder.Margin = _viewMode == ViewMode.Grid    ? new Thickness(0, 0, gapDip, gapDip)
                                     : _viewMode == ViewMode.TwoPage ? (coverAlone ? new Thickness(0) : new Thickness(0, 0, 12, 0))
                                     : new Thickness(0);
            }
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
                RenderAdditionalPages(pageIndex);
            else
            {
                ClearSecondaryPages();
                _pageContentPanel?.Width = double.NaN;
            }
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderPageLinks(pageIndex, dims.w, dims.h);
        }

        internal void ApplyZoom(bool lite = false)
        {
            if (!_applyingPendingViewport && _active?.PendingFitRestore != null && TryApplyPendingViewport()) return;
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                st.ScaleX = _zoomLevel;
                st.ScaleY = _zoomLevel;
            }
            UpdateMissingPageLabelScale();
            SyncZoomBox();   // keep the toolbar box in step (FitToWidth/FitToPage don't call SetZoom)
            // Live-resize path: the ScaleTransform above already grew/shrank the existing render to
            // match the new size - smooth and flicker-free. Skip the bitmap re-render and tile rebuild;
            // PagePreviewPanel_SizeChanged debounces one crisp re-render once the drag settles, instead
            // of thrashing it on every size tick (which is what made the page blink during a resize).
            if (lite) return;
            // Recalculate how many pages fit after zoom changes.
            // Use RefreshPageView so link overlays are re-added after RenderAdditionalPages
            // calls ClearSecondaryPages (which wipes them).
            // State.CurrentPage, NEVER PageList.SelectedIndex, in every fit/zoom/render path in
            // this file: the sidebar is a window singleton that follows the FOCUSED pane, so an
            // unfocused pane's re-fit (settle timer -> ReapplyGridOrFit under WithOwnSession) read
            // the OTHER pane's page number here. That index is usually absent from this pane's
            // _renderDims, GetPageDipSize then returns a degenerate size, and the fit slams the
            // zoom to the clamp - which is what made pane A zoom in wildly whenever a file was
            // opened in pane B (2026-08-01, repeatedly). For the focused pane the two values are
            // identical by the stage-3a sync, so this changes nothing single-pane.
            int applyIdx = State.CurrentPage;
            if (applyIdx >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () =>
                    {
                        // Focus check at EXECUTION time, not queue time: an unfocused pane's resize
                        // settle queues this from inside WithOwnSession, but by the time Loaded runs
                        // the session swap is undone - the shared fields and element accessors are
                        // back on the FOCUSED pane, so this refresh landed on the OTHER pane,
                        // cancelling and restarting its tile stream (the churn behind the grid's
                        // sequential rebuild on a two-pane resize). The unfocused pane keeps its
                        // transform-scaled tiles; its view math already ran inside the session.
                        if (Host == null || Host.IsViewerFocused(this)) RefreshPageView(applyIdx);
                    });

            // After a zoom, queue a deferred re-render at
            // higher resolution so text re-sharpens quickly (especially on high-DPI displays, where
            // the upscaled bitmap shows blur sooner). The timer debounces rapid Ctrl+scroll.
            // Skipped in Grid: this re-renders via the selected page (not page 0) and, once the render
            // hits its pixel cap when zoomed in, shifts page 0's render width - which is the basis for
            // the grid's column math. That desync locks Ctrl+scroll to a 1<->2 column toggle. The grid
            // is an overview and doesn't need the re-sharpen.
            // Continuous re-sharpens on ANY zoom change (zoom-in sharpens the visible pages, zoom-out
            // restores base bitmaps so hi-res memory is released).
            // Every other mode re-sharpens on ANY zoom change too. Single and Two-Page only refine
            // the visible regions, which decide per page whether more pixels are needed; gating on
            // a 10% zoom-in over the last full render left pages blurry after zooming out and back
            // in (the last render zoom stays at the earlier, higher value).
            if (applyIdx >= 0 && HasReadDocument && _viewMode != ViewMode.Grid)
            {
                StartRerenderTimer();
            }
        }

        // Debounced high-resolution re-render, shared by zoom settle (all modes) and continuous scroll
        // settle. Continuous gets the targeted visible-page re-sharpen (#85); Single/Two-Page re-render
        // the primary via RenderPage (which is guarded off in Continuous).
        internal void StartRerenderTimer()
        {
            if (_rerenderTimer is null)
            {
                _rerenderTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _rerenderTimer.Tick += (_, _) =>
                {
                    _rerenderTimer!.Stop();
                    if (!HasReadDocument) return;
                    // Synchronized scrolling also exposes new pages in the unfocused pane.
                    // Its page images must load even while optional sharpening waits for focus.
                    if (_viewMode.IsContinuous()) VirtualizeContinuousSlots();
                    // A settled zoom or monitor change draws the Grid again at its new tile size; a scroll onto Grid pages
                    // not drawn yet, or let go of, draws them next. Grid pages are drawn only from here, so this runs in
                    // the unfocused pane too, like the continuous page images above.
                    if (_viewMode == ViewMode.Grid && _gridTileBudget > 0 && GridTileBudget() != _gridTileBudget)
                    {
                        _gridBudgetSettled = true;
                        RenderAdditionalPages(0);
                    }
                    else if (GridNeedsFillAroundCurrentPage()) FillGridAroundCurrentPage();
                    if (Host != null && !Host.IsViewerFocused(this)) return;
                    // An EPUB search reads a page's highlight areas once the page is on screen, so the pages a
                    // scroll brought into view get theirs now.
                    if (_searchBar is { Visibility: Visibility.Visible } && Search.WaitsForPagesOnScreen)
                        HighlightSearchResultsOnCurrentPage();
                    if (_viewMode.IsContinuous())
                    {
                        // A settled zoom-in draws the pages near the viewport again at the width they now need. The
                        // redrawn pages replace the bitmaps a detail tile was drawn over, so the tiles wait for them.
                        if (ContinuousNeedsSharperPages())
                            VisibleRefinementTask = RefineAfterAsync(RenderContinuousPages(Math.Max(0, _currentPage)));
                        else RefineVisibleRegions();
                        return;
                    }
                    // Never re-render the primary in Grid (it would shift page 0's width basis and
                    // desync the column math); guards a timer started just before a switch into grid.
                    if (_viewMode != ViewMode.Grid && State.CurrentPage >= 0)
                        RefineVisibleRegions();
                };
            }
            _rerenderTimer.Stop();
            _rerenderTimer.Start();
        }

        // #189: the re-sharpen budget is measured in DEVICE pixels, so moving the window to a
        // monitor at a different scale factor changes how many pixels the page needs without
        // changing its size in DIPs. In a fit mode the monitor move also changes the window's DIP
        // size, so the resize path re-fits and re-renders; at a manual zoom (FitMode.None) nothing
        // else fires, and the page would sit upscaled from the old monitor's render. Queue the same
        // debounced pass the zoom and resize settles use.
        // This mattered less while hiW carried a 2x supersample, which happened to cover a
        // 100% -> 200% jump; at a device-native budget there is no such headroom.
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (HasReadDocument) StartRerenderTimer();
        }

        private void ResetZoom() => SetTrueZoom(1.0);

        // On-screen pixel gap between grid tiles, so adjacent pages don't visually merge - the
        // document pane background shows through it. Tile margins live inside the zoom transform,
        // so every use divides by _zoomLevel to keep the gap a constant GridGapPx on screen.
        // 2px, not 1: tile edges land on fractional device pixels (page width x fractional zoom),
        // and a 1px gap straddling a pixel boundary anti-aliases away on some seams.
        private const double GridGapPx = 2.0;

        // Grid zoom snaps to "fit N pages across the viewport", so zooming steps through clean
        // columns (1, 2, 3, ... per row) instead of arbitrary percentages. N rises as you zoom out
        // and keeps going for larger documents until the page size hits the zoom floor.
        internal double GridZoomForN(int n)
        {
            if (n < 1) n = 1;
            double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
            // Grid is edge-to-edge except a GridGapPx on-screen gap per column: fit n rdW slots
            // plus n gaps (matching RenderAdditionalPages) to the CONTENT viewport, which excludes
            // the reserved vertical scrollbar - the surround padding is 0 in grid. ViewportWidth
            // can be 0 before the first layout pass; fall back to ActualWidth, and the Background
            // re-fit that follows every grid entry corrects it.
            double vw  = PagePreviewPanel.ViewportWidth > 0
                ? PagePreviewPanel.ViewportWidth : PagePreviewPanel.ActualWidth;
            if (vw <= 0 || rdW <= 0) return _zoomLevel;
            // 2px slack: the zoom used to fit n columns EXACTLY, so a tile width pixel-snapping up
            // a fraction (UseLayoutRounding) overflowed the wrap panel and the last column dropped
            // to the next row - a 3-column grid showing 2 columns and a column-wide gap, at some
            // pane widths and not others.
            return (vw - n * GridGapPx - 2) / (n * rdW);
        }

        internal void GridZoomStep(bool zoomOut)
        {
            double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
            double vw  = PagePreviewPanel.ActualWidth;
            if (vw <= 0 || rdW <= 0) { SetZoom(zoomOut ? _zoomLevel - ZoomStep : _zoomLevel + ZoomStep); return; }
            // _gridColumns is the authoritative current column count (set on every grid zoom and restored
            // per tab); step from it rather than re-deriving it from the zoom + geometry.
            int curN = Math.Max(1, _gridColumns);
            int newN = Math.Max(1, zoomOut ? curN + 1 : curN - 1);
            // If the column count is already at the limit the clamped zoom is unchanged, so
            // skip the re-render entirely - otherwise every Ctrl+Scroll reloads all tiles
            // without changing anything.
            double target = Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(newN)));
            if (Math.Abs(target - _zoomLevel) < 1e-4) return;
            _gridColumns = newN;
            SetZoom(target);   // already clamped to [ZoomMin, ZoomMax]
        }

        /// <summary>
        /// Central zoom-change entry point for buttons, keyboard shortcuts, and the dropdown.
        /// Clamps to [ZoomMin, ZoomMax], applies the scale, syncs the combo box, and updates
        /// the status bar. Does NOT apply a fit mode - call FitToWidth / FitToPage for that.
        /// </summary>
        // The internal _zoomLevel scales each page's layout box. In Continuous mode that box is
        // the page's natural DIP width, so _zoomLevel already reads as true zoom (1.0 = 100%).
        // In Single/Two-Page/Grid the box is the render-dimension bitmap (~2x natural width), so
        // the raw _zoomLevel reads about half the real size. DisplayZoomFactor converts to true
        // zoom for everything shown to (or typed by) the user; the internal value is unchanged.
        private double DisplayZoomFactor() => TryDisplayZoomFactor(out double factor) ? factor : 1.0;

        private bool TryDisplayZoomFactor(out double factor)
        {
            factor = 1.0;
            if (!HasReadDocument) return false;
            if (_viewMode.IsContinuous()) return true;
            int idx = _viewMode == ViewMode.Grid ? 0 : Math.Max(0, State.CurrentPage);   // never the shared sidebar's index (see ApplyZoom)
            if (idx < 0 || idx >= ReadPageCount) return false;
            if (!_renderDims.TryGetValue(idx, out var d) || d.w <= 0) return false;
            double displayedWidth = d.w;
            if (_viewMode == ViewMode.TwoPage && idx != SpreadStart(idx))
            {
                if (!TryGetSecondaryPageSize(idx, out var size)) return false;
                displayedWidth = size.w;
            }
            var (wpt, _) = ReadPhysicalPageSize(idx);
            double naturalW = wpt * 96.0 / 72.0;
            if (!double.IsFinite(naturalW) || naturalW <= 0) return false;
            factor = displayedWidth / naturalW;
            return double.IsFinite(factor) && factor > 0;
        }

        // Preserve the previous completed viewport until ordered inputs have usable geometry.
        internal bool CanPersistViewportExt => _active?.PendingZoom?.HasOperations != true
            || (_active.PendingFitRestore is null && TryDisplayZoomFactor(out _));

        internal double? PersistableTrueZoomExt
        {
            get
            {
                if (_fitMode != FitMode.None || _active?.PendingFitRestore != null) return null;
                if (_active?.PendingZoom is { } pending)
                    return TryDisplayZoomFactor(out double pendingFactor)
                        ? pending.Resolve(pendingFactor, ZoomMin, ZoomMax) * pendingFactor
                        : pending.TrueZoom;
                return TryDisplayZoomFactor(out double factor) && double.IsFinite(_zoomLevel * factor)
                    && _zoomLevel * factor > 0 ? _zoomLevel * factor : null;
            }
        }

        private void RestoreManualZoom()
        {
            if (_active?.PendingZoom != null) TryApplyPendingViewport();
            else SetZoom(_zoomLevel);
        }

        private bool _applyingPendingViewport;

        private bool TryApplyPendingViewport()
        {
            if (_active?.PendingZoom is not { } request || !TryDisplayZoomFactor(out double factor)) return false;
            var session = _active;
            var fit = session.PendingFitRestore;
            // Fit is the base for subsequent manual input, so it needs the final host geometry
            // even before the first step has been queued.
            if (fit is not null && !IsFitLayoutReady(fit.Value))
            {
                StartResizeSettleTimer(restart: false);
                return false;
            }
            bool wasApplying = _applyingPendingViewport;
            _applyingPendingViewport = true;
            try
            {
                ClearPendingTrueZoom();
                if (fit is FitMode.Width or FitMode.Page)
                {
                    if (fit == FitMode.Width) FitToWidth(); else FitToPage();
                    if (!ReferenceEquals(_active, session)) return false;
                    if (session.PendingFitRestore != null)
                    {
                        session.PendingZoom = request;
                        if (request.HasOperations) _fitMode = FitMode.None;
                        return false;
                    }
                    if (request.HasOperations) SetZoom(request.ResolveFromInternal(_zoomLevel, ZoomMin, ZoomMax));
                }
                else SetZoom(request.Resolve(factor, ZoomMin, ZoomMax));
                return true;
            }
            finally { _applyingPendingViewport = wasApplying; }
        }

        private void MarkViewportIntent()
        {
            if (!_applyingPendingViewport && _active != null) _active.ViewportIntentRevision++;
        }

        private void ClearPendingTrueZoom()
        {
            if (_active != null)
            {
                _active.PendingZoom = null;
                _active.PendingFitRestore = null;
            }
        }

        private bool DeferTrueZoom(double requested)
        {
            if (_active is null || !HasReadDocument || TryDisplayZoomFactor(out _) || double.IsNaN(requested)) return false;
            _fitMode = FitMode.None;
            _active.PendingFitRestore = null;
            _active.PendingZoom = new DeferredManualZoom(double.IsPositiveInfinity(requested) ? double.MaxValue
                : requested > 0 ? requested : double.Epsilon);
            return true;
        }

        private bool DeferFit(FitMode fit)
        {
            if (_active is null || !HasReadDocument) return false;
            bool ready = TryDisplayZoomFactor(out _)
                && IsFitLayoutReady(fit)
                && (_viewMode.IsContinuous() ? _continuousPageW > 0 : PageImage.Source != null);
            if (ready) return false;
            ClearPendingTrueZoom();
            _fitMode = fit;
            _active.PendingFitRestore = fit;
            _active.PendingZoom = new DeferredManualZoom(1.0);
            StartResizeSettleTimer(restart: false);
            return true;
        }

        private bool IsFitLayoutReady(FitMode fit)
            => _resizeRefitTimer?.IsEnabled != true
                && Host?.IsViewportLayoutReady != false
                && PagePreviewPanel.IsMeasureValid && PagePreviewPanel.IsArrangeValid
                && PagePreviewPanel.ActualWidth > 40
                && (fit != FitMode.Page || PagePreviewPanel.ActualHeight > 40);

        private bool AdjustPendingTrueZoom(double scale)
        {
            if (_active?.PendingZoom is not { } pending) return false;
            if (TryApplyPendingViewport()) return false;
            pending.QueueScale(scale);
            _fitMode = FitMode.None;
            return true;
        }

        private bool AdjustPendingZoomStep(double step)
        {
            if (_active?.PendingZoom is not { } pending) return false;
            if (TryApplyPendingViewport()) return false;
            pending.QueueStep(step);
            _fitMode = FitMode.None;
            return true;
        }
        internal double DisplayZoomPct() => _zoomLevel * DisplayZoomFactor() * 100.0;

        internal void SetZoom(double level)
        {
            ClearPendingTrueZoom();
            _fitMode   = FitMode.None;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, level));
            ApplyZoom();
            SyncZoomBox();
            if (HasReadDocument && _currentPage >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), _currentPage + 1, ReadPageTotal) + $" - {DisplayZoomPct():F0}%");
        }

        /// <summary>
        /// Zoom entry point for callers that work in TRUE zoom (1.0 = the 100% the user sees),
        /// as opposed to <see cref="SetZoom"/>, which takes the internal render-dim scale.
        /// Converts through <see cref="DisplayZoomFactor"/> so an absolute zoom lands on the same
        /// percentage in every view mode instead of ~182% outside Continuous. The zoom dropdown
        /// already does this same conversion inline for its presets.
        /// </summary>
        internal void SetTrueZoom(double trueZoom)
        {
            MarkViewportIntent();
            if (DeferTrueZoom(trueZoom)) return;
            double zf = DisplayZoomFactor();
            if (zf <= 0) zf = 1.0;
            if (_viewMode == ViewMode.Grid)
            {
                if (_currentPage >= 0) _gridScrollToPage = _currentPage;
                SetZoom(trueZoom / zf);
            }
            else
            {
                ZoomAnchoredToCenter(trueZoom / zf);
            }
        }

        internal void ZoomAnchoredToCenter(double level, bool lite = false)
        {
            ClearPendingTrueZoom();
            _fitMode = FitMode.None;
            double oldZoom = _zoomLevel;
            double targetZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, level));
            if (Math.Abs(targetZoom - oldZoom) < 1e-5) return;

            bool canAnchor = HasReadDocument && oldZoom > 0
                && PagePreviewPanel.Visibility == Visibility.Visible
                && PagePreviewPanel.ActualWidth > 0 && PagePreviewPanel.ActualHeight > 0
                && _viewMode != ViewMode.Grid;

            PinchZoomResult anchored = default;
            if (canAnchor)
            {
                double vw = PagePreviewPanel.ViewportWidth;
                double vh = PagePreviewPanel.ViewportHeight;
                double originX = PagePreviewPanel.ScrollableWidth > 0 && vw > 0 ? vw * 0.5 : 0;
                double originY = PagePreviewPanel.VerticalOffset > 0 && vh > 0 ? vh * 0.5 : 0;

                if (!_toolbarZoomAnchorActive)
                {
                    (_toolbarZoomAnchorX, _toolbarZoomAnchorY) = PinchZoomMath.CaptureAnchor(
                        oldZoom, PagePreviewPanel.HorizontalOffset,
                        PagePreviewPanel.VerticalOffset,
                        originX, originY);
                    _toolbarZoomAnchorActive = true;
                }

                anchored = PinchZoomMath.ApplyAtAnchor(
                    targetZoom, _toolbarZoomAnchorX, _toolbarZoomAnchorY,
                    originX, originY);
            }

            _zoomLevel = targetZoom;
            ApplyZoom(lite);
            SyncZoomBox();

            if (canAnchor)
            {
                PagePreviewPanel.UpdateLayout();
                PagePreviewPanel.ScrollToHorizontalOffset(anchored.HorizontalOffset);
                PagePreviewPanel.ScrollToVerticalOffset(anchored.VerticalOffset);
            }

            StartZoomSettleTimer();

            if (HasReadDocument && _currentPage >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), _currentPage + 1, ReadPageTotal) + $" - {DisplayZoomPct():F0}%");
        }

        internal void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoomByUser(zoomIn: true);
        internal void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoomByUser(zoomIn: false);

        /// <summary>
        /// One zoom step the user asked for (toolbar, menu or keyboard). It marks viewport intent,
        /// so a fit still pending from a view switch or document open does not replace it.
        /// <see cref="SetZoom"/> stays unmarked for programmatic fits and restores.
        /// </summary>
        internal void StepZoomByUser(bool zoomIn)
        {
            MarkViewportIntent();
            double step = zoomIn ? ZoomStep : -ZoomStep;
            if (_viewMode == ViewMode.Grid) GridZoomStep(!zoomIn);
            else if (!AdjustPendingZoomStep(step)) ZoomAnchoredToCenter(_zoomLevel + step);
        }

        /// <summary>Set by SyncZoomBox around its programmatic writes. Same story as
        /// _syncingPageList: the box's real subscription is the WINDOW's XAML-bound stub, so the
        /// old detach/attach of this pane's own delegate removed nothing and ADDED a direct
        /// subscription per sync - which is how pane A's handler kept running while pane B was
        /// focused, fitting A against B's document (the zoomtrace smoking gun, 2026-08-01).</summary>
        private bool _syncingZoomBox;

        internal void SyncZoomBox()
        {
            _syncingZoomBox = true;
            try
            {
                // When a fit mode is active, show the "Fit Width"/"Fit Page" entry rather than a raw
                // percentage so the box matches the status bar.
                string? fitTag = _fitMode == FitMode.Width ? "fitwidth"
                               : _fitMode == FitMode.Page  ? "fitpage"
                               : null;
                string target = $"{DisplayZoomPct():F0}%";
                if (Host == null || Host.IsViewerFocused(this))
                    Host?.SyncZoomDisplay(fitTag, target);
                Host?.ViewerZoomChanged(this, _zoomLevel);
            }
            finally { _syncingZoomBox = false; }
        }

        internal void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingZoomBox) return;   // programmatic sync, not a user pick
            // Belt and braces beside the routing fix in MainWindowViewerBridge: this pane's zoom
            // box actions only ever apply to the focused pane.
            if (Host != null && !Host.IsViewerFocused(this)) return;
            string? tag = Host?.SelectedZoomTag;
            if (tag is null) return;
            // Editable combos highlight the shown value after a pick (looks like selected text);
            // collapse that selection to just the caret once the value settles.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
            {
                Host?.CollapseZoomTextSelection();
            }));

            if (tag == "fitwidth")
            {
                App.SetSetting("DefaultFitMode", FitMode.Width.ToString());
                FitToWidth();
                return;
            }
            if (tag == "fitpage")
            {
                App.SetSetting("DefaultFitMode", FitMode.Page.ToString());
                FitToPage();
                return;
            }

            if (double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double z))
            {
                SetTrueZoom(z);
                if (_currentPage >= 0 && HasReadDocument)
                    SetStatus(string.Format(Loc("Str_PageOf"), _currentPage + 1, ReadPageTotal) + $" - {DisplayZoomPct():F0}%");
            }
        }

        // A secondary tile shares the primary's displayed width. Its retained annotation dimensions
        // can differ after a view-mode switch, so fit and display zoom use the actual tile geometry.
        private bool TryGetSecondaryPageSize(int idx, out (double w, double h) size)
        {
            size = default;
            if (_viewMode != ViewMode.TwoPage || idx < 0 || idx == SpreadStart(idx)
                || !_renderDims.ContainsKey(idx) || !_continuousCanvases.TryGetValue(idx, out var canvas)
                || !double.IsFinite(canvas.Width) || !double.IsFinite(canvas.Height)
                || canvas.Width <= 0 || canvas.Height <= 0
                || !canvas.LayoutTransform.Value.IsIdentity
                || canvas.Parent is not Grid tile || !tile.Children.OfType<Image>().Any(image => image.Source != null))
                return false;
            size = (canvas.Width, canvas.Height);
            return true;
        }

        // Primary pages use their canonical dimensions; Continuous and Grid have their own fit math.
        private (double w, double h) GetPageDipSize(int idx)
        {
            if (TryGetSecondaryPageSize(idx, out var size)) return size;
            if (idx >= 0 && _renderDims.TryGetValue(idx, out var d))
                return (d.w, d.h);
            return (PageImage.ActualWidth  > 0 ? PageImage.ActualWidth  : 1,
                    PageImage.ActualHeight > 0 ? PageImage.ActualHeight : 1);
        }

        // Where a Continuous fit leaves the scroll position. A user fit (Ctrl+2, Ctrl+3, view-mode
        // change) seats the current page at the top. A resize keeps what was under the viewport,
        // during the drag and on the settle re-fit, because navigating on every tick is what made
        // the document and the thumbnails jump.
        internal enum FitScroll { PageTop, KeepOffset }

        // Slot tops are unscaled DIPs and scroll targets are top * _zoomLevel, so scaling the
        // offset by the zoom ratio keeps the same content under the viewport top.
        private void CarryContinuousOffset(double previousZoom)
        {
            double offset = PagePreviewPanel.VerticalOffset;
            if (offset <= 0) return;
            PagePreviewPanel.ScrollToVerticalOffset(offset * _zoomLevel / Math.Max(0.01, previousZoom));
        }

        // A fresh Continuous fit must not inherit a horizontal position from the previous view.
        // Repeat after layout because WPF can briefly retain the old extent while the scaled strip
        // is being rebuilt. Tab restores bypass this path and keep their saved manual position.
        private void ResetContinuousFitHorizontalOffset()
        {
            PagePreviewPanel.ScrollToHorizontalOffset(0);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                if (_viewMode.IsContinuous())
                    PagePreviewPanel.ScrollToHorizontalOffset(0);
            }));
        }

        internal void FitToWidth(bool lite = false, FitScroll scroll = FitScroll.PageTop)
        {
            MarkViewportIntent();
            if (DeferFit(FitMode.Width)) return;
            ClearPendingTrueZoom();
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;

            // Continuous mode: pages are laid out at _continuousPageW (natural DIPs width)
            // and scaled by the ScaleTransform on PageContentGrid. PageImage is hidden, so
            // we cannot use its Source as a guard; use _continuousPageW directly instead.
            if (_viewMode.IsContinuous())
            {
                if (_continuousPageW <= 0) return;
                double previousZoom = _zoomLevel;
                _fitMode   = FitMode.Width;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, viewW / ContinuousLayoutWidth));
                ApplyZoom(lite);
                int ci = State.CurrentPage;   // this pane's page, never the shared sidebar's (see ApplyZoom)
                if (lite || scroll == FitScroll.KeepOffset) CarryContinuousOffset(previousZoom);
                else
                {
                    ResetContinuousFitHorizontalOffset();
                    if (ci >= 0) NavigateContinuousToPage(ci);
                }
                if (ci >= 0 && HasReadDocument)
                    PresentCurrentPageStatus(string.Format(Loc("Str_FitWidth"), ci + 1, ReadPageTotal, $"{DisplayZoomPct():F0}"));
                return;
            }

            if (PageImage.Source is null) return;
            int idx = State.CurrentPage;   // this pane's page, never the shared sidebar's (see ApplyZoom)
            double dipW = GetPageDipSize(idx).w;
            if (dipW <= 0) return;
            // Two Page mode shows two pages side by side - each page gets roughly half
            // the viewport width (minus a small gap between pages).
            double slotW = _viewMode == ViewMode.TwoPage ? (viewW - 12) / 2 : viewW;
            _fitMode = FitMode.Width;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, slotW / dipW));
            ApplyZoom(lite);
            if (idx >= 0 && HasReadDocument)
                PresentCurrentPageStatus(string.Format(Loc("Str_FitWidth"), idx + 1, ReadPageTotal, $"{DisplayZoomPct():F0}"));
        }

        internal void FitToPage(bool lite = false, FitScroll scroll = FitScroll.PageTop)
        {
            MarkViewportIntent();
            if (DeferFit(FitMode.Page)) return;
            ClearPendingTrueZoom();
            double viewW = PagePreviewPanel.ActualWidth  - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;

            // Continuous mode: derive the current page's natural height from its PDF aspect
            // ratio and _continuousPageW, then fit both axes.
            if (_viewMode.IsContinuous())
            {
                if (_continuousPageW <= 0 || !HasReadDocument) return;
                int ci = State.CurrentPage;   // this pane's page, never the shared sidebar's (see ApplyZoom)
                if (ci < 0 || ci >= ReadPageCount) return;
                var (pageWidth, pageHeight) = ReadVisualPageSize(ci);
                double ratio = Math.Max(0.1, pageHeight / Math.Max(1.0, pageWidth));
                double dipH  = _continuousPageW * ratio;
                if (_viewMode == ViewMode.TwoPageContinuous)
                {
                    int first = SpreadStart(ci);
                    int last = BookMode && first == 0 ? first : Math.Min(first + 1, ReadPageCount - 1);
                    for (int page = first; page <= last; page++)
                    {
                        var (width, height) = ReadVisualPageSize(page);
                        dipH = Math.Max(dipH, _continuousPageW * height / Math.Max(1, width));
                    }
                }
                double previousZoom = _zoomLevel;
                _fitMode   = FitMode.Page;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                    Math.Min(viewW / ContinuousLayoutWidth, viewH / dipH)));
                ApplyZoom(lite);
                if (lite || scroll == FitScroll.KeepOffset) CarryContinuousOffset(previousZoom);
                else
                {
                    ResetContinuousFitHorizontalOffset();
                    NavigateContinuousToPage(ci);
                }
                PresentCurrentPageStatus(string.Format(Loc("Str_FitPage"), ci + 1, ReadPageTotal, $"{DisplayZoomPct():F0}"));
                return;
            }

            if (PageImage.Source is null) return;
            int idx = State.CurrentPage;   // this pane's page, never the shared sidebar's (see ApplyZoom)
            var (dipW, dipH2) = GetPageDipSize(idx);
            if (dipW <= 0 || dipH2 <= 0) return;
            double slotW2 = _viewMode == ViewMode.TwoPage ? (viewW - 12) / 2 : viewW;
            _fitMode = FitMode.Page;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                Math.Min(slotW2 / dipW, viewH / dipH2)));
            ApplyZoom(lite);
            PresentCurrentPageStatus(string.Format(Loc("Str_FitPage"), idx + 1, ReadPageTotal, $"{DisplayZoomPct():F0}"));
        }

        // Re-fit the main view after a reload. Grid keeps its column-fit (FitToWidth alone would
        // yank it out into a single-page Fit Width view); other modes honor the fit mode.
        // Called for BOTH panes after a splitter drag, so like the viewport's SizeChanged it has to
        // run against the pane it is named on rather than the focused pane. See WithOwnSession.
        internal void ReapplyGridOrFit() => WithOwnSession(ReapplyGridOrFitCore);

        private void ReapplyGridOrFitCore()
        {
            if (_viewMode == ViewMode.Grid)
            {
                double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
                double vw  = PagePreviewPanel.ActualWidth;
                if (vw > 0 && rdW > 0)
                    SetZoom(GridZoomForN(Math.Max(1, _gridColumns)));   // authoritative column count
                else ApplyZoom();
                return;
            }
            if (_fitMode == FitMode.Page) FitToPage();
            else if (_fitMode == FitMode.Width) FitToWidth();
            else ApplyZoom();
        }

        internal void NavigatePageByWheel(int delta)
            => NavigatePageStep(delta > 0 ? -1 : 1);

        // Moves the selection one page - or one two-page SPREAD in Two-Page mode (#120), landing on
        // the spread's left page so a press always shows the NEXT spread instead of re-showing the
        // current one from its right page. direction: -1 = back, +1 = forward. Returns true when
        // the selection moved. Shared by the wheel, the Up/Down edge-flip, and the Left/Right keys.
        // #193: book layout for Two-Page - the cover displays alone, so facing pairs run
        // (1,2), (3,4)... like a physical book instead of (0,1), (2,3). Global preference.
        internal static bool BookMode => App.GetSetting("TwoPageBook") == "1";

        // Left page of the spread containing <paramref name="page"/> under the active layout. One
        // formula for the viewer and Print's spread preview, so they never pair pages differently.
        private static int SpreadStart(int page) => PreviewSpreads.SpreadStart(page, BookMode);

        // #193: re-run the current mode's layout in place (the book toggle re-pairs spreads).
        // This must bypass ApplyViewMode's same-mode early return: the mode stays TwoPage while its
        // pairing rule changes, so treating it as a no-op leaves the old cover/spread tiles visible.
        internal void ReapplyViewMode()
        {
            if (_viewMode is ViewMode.TwoPage or ViewMode.TwoPageContinuous)
                ApplyViewMode(_viewMode, force: true);
        }

        internal bool NavigatePageStep(int direction)
        {
            int target = PageStepTarget(direction);
            if (target < 0) return false;
            _currentPage = target;
            return true;
        }

        internal bool CanNavigatePageStep(int direction) => PageStepTarget(direction) >= 0;

        private int PageStepTarget(int direction)
        {
            if (!HasReadDocument) return -1;
            int cur = _currentPage;
            if (_viewMode == ViewMode.TwoPage)
            {
                int baseIdx = SpreadStart(Math.Max(0, cur));   // left page of the current spread
                int target;
                if (BookMode)
                    target = direction > 0
                        ? (baseIdx == 0 ? 1 : baseIdx + 2)
                        : (baseIdx <= 1 ? 0 : baseIdx - 2);
                else
                    target = baseIdx + direction * 2;
                return target == baseIdx || target < 0 || target >= ReadPageCount ? -1 : target;
            }
            int t = cur + direction;
            return t < 0 || t >= ReadPageCount ? -1 : t;
        }

        private System.Windows.Threading.DispatcherTimer? _resizeRefitTimer;
        private int _gridColumns = 1;   // columns the grid is currently laid out in; held across resizes

        // internal: PdfViewer's XAML binds this and forwards to it.
        // Every pane raises this from its OWN ScrollViewer. It must NOT go through WithOwnSession:
        // that swaps the shared fields mid-handler, and the fit below changes the scroll content's
        // size, which raises this again - with the zoom being swapped underneath it the fit never
        // converges and the app locks up before it can paint. An unfocused pane simply sits the
        // fit out; it re-fits from ReapplyGridOrFit when the drag or the split settles.
        internal void PagePreviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Host != null && !Host.IsViewerFocused(this))
            {
                // Unfocused pane: it still has to re-fit, or it keeps the zoom from its old width
                // and the page ends up cut off - which is what opening a file in the other pane
                // did to this one. Just not INLINE: the shared fields describe the other pane right
                // now, and fitting from inside a layout event is what looped. The settle timer runs
                // ReapplyGridOrFit through WithOwnSession, after the pass, against this pane's own
                // document.
                StartResizeSettleTimer();
                return;
            }
            PagePreviewPanelSizeChangedCore(e);
        }

        private void PagePreviewPanelSizeChangedCore(SizeChangedEventArgs e)
        {
            if (_cropPreviewRect is not null || _cropConfirmBar is not null) return;

            if (_viewMode == ViewMode.Grid)
            {
                // Grid columns depend only on width, so a height-only resize (e.g. dragging the bottom
                // edge) changes nothing - skip it so it doesn't needlessly re-render/blink.
                if (!e.WidthChanged) return;
                // Hold the column count through a non-modal resize: scale the already-laid-out tiles via
                // the transform so the same number of columns fills the new width (lite, no re-render).
                if (!HasReadDocument || _gridColumns < 1) return;
                double rdWg = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
                if (PagePreviewPanel.ActualWidth <= 0 || rdWg <= 0) return;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(_gridColumns)));
                ApplyZoom(lite: true);
                StartResizeSettleTimer();
                return;
            }

            // Non-modal resize (maximize/restore, splitter, programmatic): rescale lite + settle.
            if (_active?.PendingFitRestore is not null)
            {
                StartResizeSettleTimer();
                return;
            }
            if (_fitMode == FitMode.Width) FitToWidth(lite: true);
            else if (_fitMode == FitMode.Page) FitToPage(lite: true);
            StartResizeSettleTimer();
        }

        // Coalesces resize ticks: the crisp re-render runs once, a beat after the last size change.
        private void StartResizeSettleTimer(bool restart = true)
        {
            if (!IsLoaded || !IsVisible || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (!restart && _resizeRefitTimer?.IsEnabled == true) return;
            if (_resizeRefitTimer is null)
            {
                _resizeRefitTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(110) };
                // This pane's own timer, so the work runs against this pane's document. An
                // unfocused pane wants the full re-fit (it skipped the inline one); the focused
                // pane already fitted live and only needs the crisp re-render.
                _resizeRefitTimer.Tick += (_, _) =>
                {
                    _resizeRefitTimer!.Stop();
                    if (!IsLoaded || !IsVisible || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    // Complete host layout before temporarily swapping an unfocused pane's
                    // session. SizeChanged can rearm the timer and keep the fit pending.
                    if (_active?.PendingFitRestore is not null)
                    {
                        Window.GetWindow(this)?.UpdateLayout();
                        UpdateLayout();
                    }
                    if (Host != null && !Host.IsViewerFocused(this)) ReapplyGridOrFit();
                    else WithOwnSession(OnResizeSettled);
                };
                Unloaded += (_, _) => _resizeRefitTimer?.Stop();
            }
            _resizeRefitTimer.Stop();
            _resizeRefitTimer.Start();
        }

        private void OnResizeSettled()
        {
            if (_active?.PendingFitRestore is not null && _active.PendingZoom is not null)
            {
                TryApplyPendingViewport();
                return;
            }
            if (_viewMode == ViewMode.Grid)
            {
                // Crisp re-render at the held column count for the final size (the drag only transform-
                // scaled the tiles). The grid's width is stable (vertical scrollbar reserved), so this
                // settles in one pass instead of looping.
                if (HasReadDocument && _gridColumns >= 1)
                    SetZoom(Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(_gridColumns))));
                return;
            }
            if (_fitMode == FitMode.Width) FitToWidth(scroll: FitScroll.KeepOffset);
            else if (_fitMode == FitMode.Page) FitToPage(scroll: FitScroll.KeepOffset);
        }

        private int NearestContinuousPage(double yInPanel)
        {
            int best = -1; double bestDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double top = _continuousTops[i];
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) h = 0;
                double bottom = top + h;
                double dist = yInPanel < top ? top - yInPanel : (yInPanel > bottom ? yInPanel - bottom : 0);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }

        internal void SelectViewMode(ViewMode mode)
        {
            SetViewMode(mode);
            // Leave the flyout open (PanelMenuItem) so the user can try view modes back to back.
        }

        // Fade timings for a view-mode switch: quick dip to black-out the relayout, calmer reveal.
        private const int ViewFadeOutMs = 90;
        private const int ViewFadeInMs  = 140;

        // Target mode while a fade-out is in flight. Non-null means a switch is mid-fade; a new
        // SetViewMode during that window just retargets it (rapid F5-F8 presses land on the last).
        // Forwarding property onto the per-view state - it belongs to a view, not to the window,
        // since two panes fade independently. _pendingViewMode is defined in PdfViewer.Bridge.cs
        // with the rest of the state accessors; PendingViewMode below is how the window's flyout
        // code reaches it.
        internal ViewMode? PendingViewMode { get => _pendingViewMode; set => _pendingViewMode = value; }
        internal int GridColumns { get => _gridColumns; set => _gridColumns = value; }

        // Fade wrapper around the actual mode switch (ApplyViewMode). The switch itself swaps panel
        // visibility instantly and defers its layout/scroll setup through the dispatcher, so doing
        // it live flashed intermediate frames - most visibly page 1 at the top of the continuous
        // strip before the deferred ScrollContinuousToPage ran. Fading the viewport out, switching
        // while it's invisible, and fading back in AFTER the setup queue drains hides all of that
        // and gives every mode switch the same soft transition.
        internal void SetViewMode(ViewMode mode)
        {
            if (_pendingViewMode is not null)   // mid-fade: retarget the switch already underway
            {
                _pendingViewMode = mode;
                return;
            }
            if (_viewMode == mode) return;
            ClearPendingTrueZoom();
            if (_doc is null) { ApplyViewMode(mode); return; }   // start screen: nothing visible to fade

            _pendingViewMode = mode;
            var fadeOut = new DoubleAnimation(PagePreviewPanel.Opacity, 0,
                new Duration(TimeSpan.FromMilliseconds(ViewFadeOutMs)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            fadeOut.Completed += (_, _) =>
            {
                var target = _pendingViewMode ?? mode;
                _pendingViewMode = null;
                ApplyViewMode(target);
                // The mode's setup work is queued at Loaded priority (continuous nests one more
                // Loaded dispatch for its scroll-to-page; grid one Background dispatch for its
                // re-fit). ContextIdle is below all of those, so the fade-in only starts once the
                // new mode is laid out and scrolled to the right page - no intermediate frames.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
                {
                    var fadeIn = new DoubleAnimation(0, 1,
                        new Duration(TimeSpan.FromMilliseconds(ViewFadeInMs)))
                        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    PagePreviewPanel.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }));
            };
            PagePreviewPanel.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // Removing every Continuous slot of a long document in one go held the window for about 200 ms on the way
        // into another mode. The collapsed panel drops its slots' bitmaps at once and lets the slots themselves go a
        // step at a time once the new mode has settled. Setting Continuous up again clears whatever is left.
        private const int ContinuousSlotReleaseStep = 100;

        private void ReleaseContinuousSlotsInSteps()
        {
            foreach (var child in _continuousPanel.Children)
                if (child is Border { Child: Grid g } && g.Children.Count > 0 && g.Children[0] is Image image) image.Source = null;
            if (_continuousPanel.Children.Count <= ContinuousSlotReleaseStep) { _continuousPanel.Children.Clear(); return; }
            var panel = _continuousPanel;
            void Step()
            {
                if (!ReferenceEquals(panel, _continuousPanel) || _viewMode.IsContinuous() || panel.Children.Count == 0) return;
                int keep = Math.Max(0, panel.Children.Count - ContinuousSlotReleaseStep);
                panel.Children.RemoveRange(keep, panel.Children.Count - keep);
                if (keep > 0) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.SystemIdle, (Action)Step);
            }
            var start = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.SystemIdle, Dispatcher)
                { Interval = TimeSpan.FromSeconds(1) };
            start.Tick += (_, _) => { start.Stop(); Step(); };
            start.Start();
        }

        internal void ApplyViewMode(ViewMode mode, bool force = false)
        {
            if (_viewMode == mode && !force) return;
            // A view mode keeps the document and its page list, so the list's thumbnail window is left running.
            CancelRenderWork(includeThumbnails: false);
            _primaryRenderSession.Clear();
            // The background render cache stays: a view mode does not change the document, and opening a long
            // document's page tree again took about 225 ms before the new mode's first page could draw.
            _viewMode = mode;
            Host?.ViewerNavigationChanged(this);
            _renderedPrimaryPage = -1;   // spread/layout changes with the mode; force the next render
            _gridScrollToPage = -1;
            App.SetSetting("ViewMode", mode.ToString());

            bool isContinuous = mode.IsContinuous();
            _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            _continuousPanel.Visibility  = isContinuous ? Visibility.Visible   : Visibility.Collapsed;

            if (!isContinuous)
            {
                CancelAndRelease(_continuousRenderCts);
                _continuousRenderCts = null;
                ReleaseContinuousSlotsInSteps();
                _continuousTops.Clear();
                _continuousCanvases.Clear();
                _pages.Clear();
            }

            if (!HasReadDocument) return;
            int idx = _currentPage;
            var modeSession = _active;
            bool ModeCurrent() => _viewMode == mode && ReferenceEquals(_active, modeSession);
            // A zoom the user picks while the new mode's first page renders outranks the switch's fit.
            long modeIntent = modeSession?.ViewportIntentRevision ?? 0;
            bool ModeIntentCurrent() => (modeSession?.ViewportIntentRevision ?? 0) == modeIntent;
            if (mode.IsContinuous())
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => { if (ModeCurrent()) SetupContinuousView(idx); });
            }
            else
            {
                CancelAndRelease(_secondaryRenderCts);
                _secondaryRenderCts = null;
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                // Drop any scroll offset carried over from the previous mode (especially Continuous,
                // whose large vertical offset would otherwise land the grid mid-document).
                PagePreviewPanel.ScrollToVerticalOffset(0);
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(async () =>
                {
                    if (!ModeCurrent()) return;
                    RenderPage(mode == ViewMode.Grid ? 0 : idx);
                    long modeGeneration = _primaryRenderGeneration;
                    await PrimaryRenderTask;
                    if (!ModeCurrent() || modeGeneration != _primaryRenderGeneration) return;
                    await Dispatcher.InvokeAsync(UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    if (!ModeCurrent() || modeGeneration != _primaryRenderGeneration) return;
                    // Grid: apply a clean column-fit zoom (continuous's zoom is far too large for a
                    // grid, and a non-column zoom leaves a gap). SetZoom -> ApplyZoom defers the
                    // single tile render, so return here instead of calling RefreshPageView again
                    // (a second render would duplicate tiles).
                    if (mode == ViewMode.Grid)
                    {
                        _gridColumns = Math.Min(ReadPageCount, 3);
                        SetZoom(GridZoomForN(_gridColumns));
                        // The first fit can run before the viewport width has settled (leaving the
                        // grid off-center / at the wrong zoom); re-fit once more after layout settles,
                        // and pin to the top so nothing carries over from the previous mode.
                        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                            (Action)(() =>
                            {
                                if (!ModeCurrent() || modeGeneration != _primaryRenderGeneration) return;
                                ReapplyGridOrFit();
                                // Selection is preserved across the switch; scroll to that page once
                                // its tile streams in (grid tiles render async). Page 0 stays at top.
                                if (idx > 0) _gridScrollToPage = idx;
                                else
                                {
                                    PagePreviewPanel.ScrollToVerticalOffset(0);
                                    PagePreviewPanel.ScrollToHorizontalOffset(0);
                                }
                            }));
                        return;
                    }
                    // Switching into Single or Two-Page fits the whole page so it isn't left at an
                    // awkward carried-over zoom from another mode, unless the user has zoomed since.
                    if (!ModeIntentCurrent())               ApplyZoom();
                    else if (mode == ViewMode.Single || mode == ViewMode.TwoPage) FitToPage();
                    else if (_fitMode == FitMode.Width) FitToWidth();
                    else if (_fitMode == FitMode.Page)  FitToPage();
                    else                                ApplyZoom();
                    RefreshPageView(idx);
                }));
            }
        }
    }
}
