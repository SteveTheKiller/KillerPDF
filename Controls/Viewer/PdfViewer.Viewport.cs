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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

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

        /// <summary>The page's image boxes for the inversion carve-out, cached per (file, page).
        /// On a miss, opens PdfPig into the caller's ref (so a worker loop pays ONE open however
        /// many pages it fills) - the caller disposes it; a held handle on the temp file would
        /// block the save-time file swap. Encrypted or unparsable pages cache an empty set, which
        /// falls back to inverting everything - the pre-carve-out behavior.</summary>
        private BitmapHelpers.FracRect[] ImageRectsFor(string file, int page, ref PdfPigDoc? pig)
        {
            // Opt-in full inversion (moon right-click, "Invert images too"): no carve-out, and
            // no PdfPig open paid for rects nobody will use.
            if (BitmapHelpers.DocInvertImages) return [];
            string key = page + "|" + file;
            if (_pageImageRects.TryGetValue(key, out var hit)) return hit;
            BitmapHelpers.FracRect[] rects;
            try
            {
                pig ??= PdfPigDoc.Open(file);
                rects = PdfImages.GetFracRects(pig, page);
            }
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

        private void PagePreviewPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // The vertical scrollbar can appear/disappear without a window resize (zoom, page count
            // changes). When it does, re-anchor the annotate bars so a right-docked bar tracks the
            // scrollbar's edge instead of getting covered (or stranded once it's gone).
            bool vis = PagePreviewPanel.ComputedVerticalScrollBarVisibility == Visibility.Visible;
            if (vis != _vScrollVisible)
            {
                _vScrollVisible = vis;
                RepositionAnnotationBars();
            }

            if (_viewMode == ViewMode.Continuous && _continuousTops.Count > 0)
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
                double viewCenter = PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight * 0.5;
                int nearest = -1;
                double minDist = double.MaxValue;
                for (int i = 0; i < _pageContentPanel.Children.Count; i++)
                {
                    if (_pageContentPanel.Children[i] is not FrameworkElement tile || tile.ActualHeight <= 0)
                        continue;
                    double cy = tile.TranslatePoint(new Point(0, tile.ActualHeight * 0.5), gridRoot).Y;
                    double dist = Math.Abs(cy - viewCenter);
                    if (dist < minDist) { minDist = dist; nearest = i; }
                }
                if (nearest >= 0) SyncCurrentPageTo(nearest);
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
            if (Owner != null && !ReferenceEquals(Owner.ActiveViewer, this)) return;
            if (PageList.SelectedIndex == nearest) return;
            _pageJumpBox.Text = (nearest + 1).ToString();
            // Reentrancy FLAG, not a detach/attach pair. The PageList's real subscription is the
            // WINDOW's XAML-bound stub, so `-=` of this pane's own delegate removed NOTHING and
            // the `+=` stacked this pane's handler onto the shared list as an EXTRA direct
            // subscription - one more per scroll-sync, forever (found via zoomtrace, 2026-08-01,
            // alongside the identical ZoomBox pattern).
            _syncingPageList = true;
            try { PageList.SelectedIndex = nearest; }
            finally { _syncingPageList = false; }
            // Keep the selected thumbnail in view. ScrollIntoView lives in
            // PageList_SelectionChanged, which is detached above - so the scroll-driven path moved
            // the highlight but never scrolled the sidebar, and the selection walked off the end
            // of the visible list as the document scrolled.
            PageList.ScrollIntoView(PageList.SelectedItem);
            // This is the one write that detaches the handler (to avoid re-entering the render path
            // from a scroll sync), so it is also the one that would slip past the mirror in
            // PageList_SelectionChanged. Set it by hand.
            State.CurrentPage = nearest;
            if (_doc is not null)
                SetStatus(string.Format(Loc("Str_PageOf"), nearest + 1, _doc.PageCount) + $" - {DisplayZoomPct():F0}%");
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
                if (_viewMode == ViewMode.Grid) PageList.SelectedIndex = page;
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
        }

        // Builds a page's annotation overlay. Size/transform differ by mode (continuous = render-dim + scale;
        // grid/two-page = DIP 1:1); everything else - background, clip, tag, input handler - is identical.
        private Canvas BuildPageOverlay(int page, double width, double height, System.Windows.Media.Transform? layoutTransform)
        {
            var overlay = new Canvas
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
            // #151: the page-number tooltip only existed on the secondary tiles, so whether it
            // showed depended on the view mode (none in Single/Continuous, even pages only in
            // Two-Page, from page 2 in Grid). Set here it covers every code-built overlay; the
            // XAML primary tile gets the same line in RenderPage, where its page changes.
            overlay.ToolTip = string.Format(Loc("Str_PageLabel"), page + 1);
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

            var overlay = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
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

        internal void SetupContinuousView(int initialPage, bool fitDefault = true)
        {
            if (_doc is null) return;
            // #130: a malformed page tree can parse to zero pages - Pages[0] below would throw
            // ArgumentOutOfRangeException. Bail out instead of crashing; the view just stays empty.
            if (_doc.PageCount == 0) return;
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
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _continuousCanvases.Clear();
            _continuousLinks.Clear();
            _pages.Clear();

            // Fresh slot set: any hi-res re-sharpen bookkeeping from the previous layout is stale.
            // (This reset used to live in the render pass, back when it repainted every slot.)
            _continuousSharpenCts?.Cancel();
            _continuousSharpPages.Clear();
            _continuousSharpW = 0;

            // Use the PDF's natural page width in WPF DIPs (96 DIP/inch, 72 pt/inch).
            // This is zoom-independent, which is critical: FitToWidth computes
            //   zoom = viewportW / _continuousPageW
            // and if _continuousPageW were derived from the current zoom level the two
            // would cancel and FitToWidth would always return approximately the old zoom.
            var refPage = _doc.Pages[0];
            _continuousPageW = Math.Max(200.0, refPage.Width.Point * (96.0 / 72.0));

            double y = 0;
            for (int i = 0; i < _doc.PageCount; i++)
            {
                _continuousTops.Add(y);
                var pdfPage = _doc.Pages[i];
                double pw = pdfPage.Width.Point, ph = pdfPage.Height.Point;
                if (_pageRotations.TryGetValue(i, out int prot) && (prot == 90 || prot == 270))
                    (pw, ph) = (ph, pw);
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
                double slotH = _continuousPageW * rdH / (double)rdW;
                double slotScale = _continuousPageW / rdW;
                var overlay = BuildPageOverlay(i, rdW, rdH, new System.Windows.Media.ScaleTransform(slotScale, slotScale));

                var pageImg = new Image { Stretch = Stretch.None, Width = _continuousPageW, Height = slotH };
                RenderOptions.SetBitmapScalingMode(pageImg, BitmapScalingMode.HighQuality);

                var slotGrid = new Grid();
                slotGrid.Children.Add(pageImg);
                slotGrid.Children.Add(overlay);
                // Record this page's links so they're clickable in continuous view (resolved by the
                // bounds-check in Canvas_MouseLeftButtonDown). Without this, links never work in scrolling view.
                AddSecondaryPageLinks(i, rdW, rdH);

                var placeholder = new Border
                {
                    Width      = _continuousPageW,
                    Height     = slotH,
                    Margin     = new Thickness(0, 0, 0, 12),
                    Background = Brushes.White,   // empty-page scaffold while the bitmap streams in (not a dark box)
                    Tag = i,
                    Child = slotGrid
                };
                // #128: no click-to-select in Continuous. The current page follows the viewport via
                // scroll-sync (like Acrobat/Sumatra), so a click must not scroll or move the counter.
                _continuousPanel.Children.Add(placeholder);
                y += slotH + 12;
            }

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
            if (_fitMode == FitMode.Width) FitToWidth();
            else if (_fitMode == FitMode.Page) FitToPage();
            else if (fitDefault) FitToPage();
            else SetZoom(_zoomLevel);

            _continuousScrollTarget = initialPage;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ScrollContinuousToPage(initialPage));

            _ = RenderContinuousPages(initialPage);
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

        internal async System.Threading.Tasks.Task RenderContinuousPages(int centerPage)
        {
            if (_doc is null || _currentFile is null) return;
            _continuousRenderCts?.Cancel();
            _continuousRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _continuousRenderCts;

            string currentFile = _currentFile;
            int pageCount      = _doc.PageCount;
            double targetW     = _continuousPageW;
            int renderW        = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));

            // Window of pages to materialize. Slots that already hold a base bitmap are skipped;
            // slots holding a hi-res re-sharpened bitmap belong to the resharpen pass, skip too.
            int lo = Math.Max(0, centerPage - ContinuousKeepPages);
            int hi = Math.Min(pageCount - 1, centerPage + ContinuousKeepPages);
            var todo = new List<int>();
            for (int i = lo; i <= hi; i++)
            {
                if (i >= _continuousPanel.Children.Count) break;
                if (_continuousPanel.Children[i] is Border b && b.Child is Grid g
                    && g.Children.Count > 0 && g.Children[0] is Image img && img.Source == null)
                    todo.Add(i);
            }
            if (todo.Count == 0) return;

            // Capture per-page rotations on the UI thread before going async
            var rotations = new Dictionary<int, int>(_pageRotations);

            var session = _active;
            await System.Threading.Tasks.Task.Run(() =>
            {
                Docnet.Core.Readers.IDocReader? docReader = null;
                PdfPigDoc? pig = null;   // opened lazily by ImageRectsFor on the first uncached page
                try
                {
                    foreach (int i in todo)
                    {
                        if (cts.IsCancellationRequested) return;
                        int rot = rotations.TryGetValue(i, out int rr) ? rr : 0;

                        // Cache hit: skip pdfium, just attach the cached bitmap to its slot.
                        var cb = TryGetCachedRender(session, i, renderW, rot);
                        if (cb != null)
                        {
                            int fic = i;
                            Dispatcher.Invoke(() =>
                            {
                                if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                                SetContinuousSlot(fic, cb);
                            });
                            continue;
                        }

                        docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(renderW, renderW * 2));
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        var raw = pr.GetImage(PdfRender.WithAnnotations);   // #141: draw the file's own markup
                        if (w <= 0 || h <= 0 || raw is null) continue;
                        // #135: display-only dark mode, pictures excluded. Invert BEFORE the
                        // pixel-buffer rotation (the ops commute for the full page) so the image
                        // carve-out rects stay in unrotated page space.
                        if (BitmapHelpers.DocInvert)
                            BitmapHelpers.InvertBgraInPlaceExcept(raw, w, h, ImageRectsFor(currentFile, i, ref pig));
                        if (rot != 0)
                            (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);

                        int fi = i, fw = w, fh = h, prot = rot;
                        byte[] bytes = raw;
                        if (cts.IsCancellationRequested) return;
                        // Use the window's own dispatcher, not Application.Current.Dispatcher: during app
                        // shutdown Application.Current goes null and this background render would NRE.
                        Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            if (fi >= _continuousPanel.Children.Count) return;
                            // Render at the natural continuous width (zoom is a LayoutTransform, so the bitmap
                            // is zoom-independent and reusable). Square pixels via the matching dipH.
                            int ddw = Math.Max(1, (int)Math.Round(targetW));
                            int ddh = Math.Max(1, (int)Math.Round(targetW * fh / fw));
                            var bmp = BitmapHelpers.BuildScaledBitmap(fw, fh, bytes, ddw, ddh);
                            CacheRender(session, fi, renderW, prot, bmp);
                            SetContinuousSlot(fi, bmp);
                        });
                    }
                }
                catch { /* render cancelled or doc closed */ }
                finally { docReader?.Dispose(); pig?.Dispose(); }
            }, cts.Token);
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
            if (slot.Child is not Grid slotGrid || slotGrid.Children.Count == 0 || slotGrid.Children[0] is not Image pageImg)
                return;

            pageImg.Source  = bmp;
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

            // Slot heights are now exact; recompute scroll offsets from them.
            double yy = 0;
            for (int k = 0; k < _continuousPanel.Children.Count && k < _continuousTops.Count; k++)
            {
                _continuousTops[k] = yy;
                double hk = ((FrameworkElement)_continuousPanel.Children[k]).Height;
                if (double.IsNaN(hk)) hk = 0;
                yy += hk + 12;
            }

            // Pages render in order, so when the target page is reached every page above it has its final
            // height; re-scroll so a crop lands you back on the same page instead of drifting.
            if (_continuousScrollTarget >= 0 && fi >= _continuousScrollTarget)
            {
                int tgt = _continuousScrollTarget;
                _continuousScrollTarget = -1;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => ScrollContinuousToPage(tgt)));
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
        }

        // Scroll-settle window maintenance for virtualized Continuous view (#122): releases the
        // bitmaps of slots far outside the viewport (heights are kept, so nothing moves) and
        // kicks a render pass when unrendered slots have come near. Runs on the UI thread from
        // the same debounced timer as the re-sharpen pass.
        private void VirtualizeContinuousSlots()
        {
            if (_viewMode != ViewMode.Continuous || _doc is null) return;
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
            if (missing) _ = RenderContinuousPages((first + last) / 2);
        }

        // ── Continuous zoom re-sharpen (#85) ─────────────────────────────────────────────────────────
        // The continuous base pass renders every page at a fixed fit-width budget, so on high-DPI
        // displays or deep zoom the upscaled bitmap goes soft (and, unlike Single mode, nothing ever
        // re-rendered it - RenderPage is guarded off in Continuous). This re-renders ONLY the pages
        // near the viewport at a DPI- and zoom-aware budget and swaps them into their slots. Slots that
        // scroll away are restored to the cached base render, so hi-res bitmaps never accumulate beyond
        // the visible window; the hi-res bitmaps are deliberately NOT put in the render cache for the
        // same reason. Debounced via _rerenderTimer (zoom settle + scroll settle).
        private void ResharpenContinuousVisible()
        {
            if (_viewMode != ViewMode.Continuous || _doc is null || _currentFile is null) return;
            if (_continuousTops.Count == 0 || _continuousPanel.Children.Count == 0) return;

            double targetW = _continuousPageW;
            int baseW = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));
            var dpiInfo = VisualTreeHelper.GetDpi(this);
            double dpiScale = Math.Max(dpiInfo.DpiScaleX, dpiInfo.DpiScaleY);
            int hiW = (int)Math.Min(4096, targetW * 2 * dpiScale * Math.Max(1.0, _zoomLevel));

            // Visible slot range. Slot space is zoom-independent (the LayoutTransform supplies the
            // zoom), so divide the scroll offsets back down - same mapping ScrollChanged uses.
            double viewTop = PagePreviewPanel.VerticalOffset / Math.Max(0.01, _zoomLevel);
            double viewBot = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight) / Math.Max(0.01, _zoomLevel);
            var visible = new List<int>();
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double top = _continuousTops[i];
                double bot = top + ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (bot >= viewTop && top <= viewBot) visible.Add(i);
            }
            if (visible.Count > 0)
            {
                // One page of margin either side so a small scroll stays sharp.
                if (visible[0] > 0) visible.Insert(0, visible[0] - 1);
                if (visible[^1] < _continuousTops.Count - 1) visible.Add(visible[^1] + 1);
            }

            // Below ~1.25x the base budget the re-raster isn't visibly sharper; restore-only pass.
            bool wantHi = hiW >= (int)(baseW * 1.25);

            _continuousSharpenCts?.Cancel();
            _continuousSharpenCts = new System.Threading.CancellationTokenSource();
            var cts = _continuousSharpenCts;

            // Pages sharpened earlier that scrolled away (or aren't wanted at this zoom): swap the
            // cached base render back in so their hi-res bitmaps get collected. Cache miss = leave it.
            var session = _active;
            foreach (int p in _continuousSharpPages.ToList())
            {
                if (wantHi && visible.Contains(p)) continue;
                int rot = _pageRotations.TryGetValue(p, out int rr) ? rr : 0;
                var baseBmp = TryGetCachedRender(session, p, baseW, rot);
                if (baseBmp != null) SetContinuousSlot(p, baseBmp);
                _continuousSharpPages.Remove(p);
            }
            if (!wantHi) return;

            // Zoom changed since the last pass: every sharpened slot is at the wrong budget, redo them.
            bool budgetChanged = hiW != _continuousSharpW;
            _continuousSharpW = hiW;
            var work = visible.Where(p => budgetChanged || !_continuousSharpPages.Contains(p)).ToList();
            if (work.Count == 0) return;

            string currentFile = _currentFile;
            var rotations = new Dictionary<int, int>(_pageRotations);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                Docnet.Core.Readers.IDocReader? docReader = null;
                PdfPigDoc? pig = null;   // opened lazily by ImageRectsFor on the first uncached page
                try
                {
                    foreach (int p in work)
                    {
                        if (cts.IsCancellationRequested) return;
                        docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(hiW, hiW * 2));
                        using var pr = docReader.GetPageReader(p);
                        int w = pr.GetPageWidth(), h = pr.GetPageHeight();
                        var raw = pr.GetImage(PdfRender.WithAnnotations);   // #141: draw the file's own markup
                        if (w <= 0 || h <= 0 || raw is null) continue;
                        int rot = rotations.TryGetValue(p, out int rr) ? rr : 0;
                        // #135: dark mode with pictures excluded; invert before the rotation so
                        // the carve-out rects stay in unrotated page space.
                        if (BitmapHelpers.DocInvert)
                            BitmapHelpers.InvertBgraInPlaceExcept(raw, w, h, ImageRectsFor(currentFile, p, ref pig));
                        if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);

                        int fp = p, fw = w, fh = h;
                        byte[] bytes = raw;
                        if (cts.IsCancellationRequested) return;
                        Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            int ddw = Math.Max(1, (int)Math.Round(targetW));
                            int ddh = Math.Max(1, (int)Math.Round(targetW * fh / (double)fw));
                            SetContinuousSlot(fp, BitmapHelpers.BuildScaledBitmap(fw, fh, bytes, ddw, ddh));
                            _continuousSharpPages.Add(fp);
                        });
                    }
                }
                catch { /* cancelled or doc closed */ }
                finally { docReader?.Dispose(); pig?.Dispose(); }
            }, cts.Token);
        }

        // keepTiles: leave the existing secondary tiles in place instead of clearing them first.
        // For a pixels-only repaint (invert toggle) the tile set is unchanged, and clearing it
        // made the whole grid flash empty and re-stream - RenderAdditionalPages swaps bitmaps
        // into existing tiles in place, so keeping them repaints without any layout jitter.
        // Navigation keeps the default (clear first) since the tile set actually changes.
        internal void RenderPage(int pageIndex, bool keepTiles = false)
        {
            if (_currentFile is null || _doc is null) return;
            // Continuous has its own pipeline (SetupContinuousView + RenderContinuousPages into
            // _continuousPanel) and owns the _pages map for every page. RenderPage targets the hidden
            // single/grid primary (_annotationCanvas in the collapsed _pageContentPanel) and calls
            // ClearSecondaryPages, which would WIPE the continuous _pages map and repoint the current
            // page at the invisible primary - so any annotation added afterwards renders off-screen
            // until a mode switch rebuilds the overlays. Stray callers (the zoom re-sharpen timer, the
            // DPI-change handler) can fire RenderPage while continuous is active; ignore them. The mode
            // switch sets _viewMode to the new (non-continuous) mode BEFORE calling RenderPage, so this
            // guard never blocks a legitimate switch-into-single/grid render.
            if (_viewMode == ViewMode.Continuous) return;
            // Two-page spreads pair (0,1),(2,3),...; render the pair's left (even) page as primary so
            // selecting the right page of a pair still shows the whole spread, not a lone page.
            if (_viewMode == ViewMode.TwoPage) pageIndex -= pageIndex % 2;
            try
            {
                // Scale render resolution to match display DPI AND current zoom so the
                // bitmap stays sharp when zoomed in.  Base 2048 means Fit Width on a
                // wide monitor stays crisp; zoom factor ensures 1:1 pixels at 2× zoom.
                // Capped at 6144 to keep memory manageable.
                var dpiInfo = VisualTreeHelper.GetDpi(this);
                double dpiScaleX = dpiInfo.DpiScaleX;
                double dpiScaleY = dpiInfo.DpiScaleY;
                int scaledMax = (int)Math.Min(6144,
                    2048 * Math.Max(dpiScaleX, dpiScaleY) * Math.Max(1.0, _zoomLevel));
                _lastRenderZoom = _zoomLevel;

                int pgRot = _pageRotations.TryGetValue(pageIndex, out int pr0) ? pr0 : 0;

                // #151: keep the primary tile's page tooltip in step with what it shows, matching
                // the code-built overlays (BuildPageOverlay).
                _annotationCanvas.ToolTip = string.Format(Loc("Str_PageLabel"), pageIndex + 1);

                // Reuse this tab's cached bitmap for (page, resolution, rotation) if present; otherwise
                // rasterize once and cache it. On a switch back to a recent tab this skips pdfium entirely.
                int width, height;
                System.Windows.Media.Imaging.BitmapSource bitmap;
                var cached = TryGetCachedRender(_active, pageIndex, scaledMax, pgRot);
                if (cached != null)
                {
                    bitmap = cached;
                    width  = cached.PixelWidth;
                    height = cached.PixelHeight;
                }
                else
                {
                    using var docReader = DocLib.Instance.GetDocReader(_currentFile, new PageDimensions(scaledMax, scaledMax));
                    using var pageReader = docReader.GetPageReader(pageIndex);
                    width  = pageReader.GetPageWidth();
                    height = pageReader.GetPageHeight();
                    var rawBytes = pageReader.GetImage(PdfRender.WithAnnotations);   // #141
                    // Bail on an unusable render BEFORE touching the buffer. This check used to sit
                    // after the rotate; moving the invert ahead of the rotate (#135) meant the
                    // buffer was read while still only maybe-non-null, so validate first instead.
                    if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                    {
                        PageImage.Source = null;
                        SetStatus(string.Format(Loc("Str_PageRenderError"), pageIndex + 1));
                        return;
                    }
                    // #135: display-only dark mode, pictures excluded. Before the rotation so the
                    // carve-out rects stay in unrotated page space; the one-shot PdfPig open is
                    // paid only on this page's first inverted render (the rects cache after).
                    if (BitmapHelpers.DocInvert)
                    {
                        PdfPigDoc? pig = null;
                        try { BitmapHelpers.InvertBgraInPlaceExcept(rawBytes, width, height, ImageRectsFor(_currentFile, pageIndex, ref pig)); }
                        finally { pig?.Dispose(); }
                    }
                    // The temp file has /Rotate stripped so Docnet renders unrotated (no clipping); rotate
                    // the pixel buffer to match the visual.
                    if (pgRot != 0)
                        (rawBytes, width, height) = BitmapHelpers.RotateBitmap(rawBytes, width, height, pgRot);
                    // Bake the bitmap DPI from the canonical render-dim size (longest side -> 2048 DIP, the
                    // SAME zoom-independent basis continuous and the secondary tiles use) so the extra pixels
                    // display within a fixed DIP area regardless of zoom. LayoutTransform supplies the zoom.
                    double bLongest = Math.Max(1, Math.Max(width, height));
                    int bw = Math.Max(1, (int)Math.Round(2048.0 * width  / bLongest));
                    int bh = Math.Max(1, (int)Math.Round(2048.0 * height / bLongest));
                    var wb = new WriteableBitmap(width, height, 96.0 * width / bw, 96.0 * height / bh, PixelFormats.Bgra32, null);
                    wb.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);
                    wb.Freeze();
                    bitmap = wb;
                    CacheRender(_active, pageIndex, scaledMax, pgRot, bitmap);
                }

                // Canonical render-dim space: longest side -> 2048 DIP, identical to the continuous and
                // secondary-tile paths. This is zoom-independent (aspect only), so the canvas/overlay size
                // is byte-for-byte stable across every zoom re-render - no left-shift when re-sharpening,
                // and annotation coordinates match across all view modes. LayoutTransform handles the zoom.
                double longest = Math.Max(1, Math.Max(width, height));
                int dipW = Math.Max(1, (int)Math.Round(2048.0 * width  / longest));
                int dipH = Math.Max(1, (int)Math.Round(2048.0 * height / longest));
                _renderDims[pageIndex] = (dipW, dipH);

                PageImage.Source = bitmap;
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
                SetStatus(string.Format(Loc("Str_PageOf"), pageIndex + 1, _doc!.PageCount));
                // Defer additional pages until layout has settled so ActualWidth is valid.
                // RenderPageLinks runs AFTER RenderAdditionalPages so ClearSecondaryPages
                // inside RenderAdditionalPages doesn't wipe the overlays we just added.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    // Only Grid/Two-Page lay out neighbour tiles. In Single mode RenderAdditionalPages would
                    // snap the panel width to pageW + 12 (the inter-tile gap), nudging the lone page ~6px left
                    // of centre - the shift seen a beat after a zoom-in, when the re-sharpen timer re-renders.
                    // Match RefreshPageView's single-mode handling instead: no extra tiles, auto panel width.
                    if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
                        RenderAdditionalPages(pageIndex);
                    else
                    {
                        ClearSecondaryPages();
                        if (_pageContentPanel is not null) _pageContentPanel.Width = double.NaN;
                    }
                    RenderPageLinks(pageIndex, dipW, dipH);
                });
                _renderedPrimaryPage = pageIndex;
            }
            catch (Exception ex)
            {
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
            // Unified map: keep only the CURRENT primary entry (key == _annotationCanvas.Tag) and drop
            // everything else - the secondary overlays and any stale primary entry from a prior page.
            int primPage = _annotationCanvas.Tag is int tp ? tp : -1;
            foreach (var pg in _pages.Keys.Where(k => k != primPage).ToList())
                _pages.Remove(pg);
        }

        /// <summary>
        /// Renders secondary pages as a grid. Panel-width setup is synchronous so layout
        /// is correct immediately; Docnet pixel rendering runs on a background thread so
        /// the UI stays responsive. WPF element creation returns to the UI thread.
        /// </summary>
        private async void RenderAdditionalPages(int primaryPageIdx)
        {
            if (_currentFile is null || _doc is null) return;
            // Grid is a stable overview anchored at page 0 (independent of the selected page), so it
            // always shows the whole document instead of only the selected page onward.
            if (_viewMode == ViewMode.Grid) primaryPageIdx = 0;

            double viewportW = PagePreviewPanel.ActualWidth;
            if (viewportW <= 0 || _doc.PageCount <= 1)
            {
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                return;
            }

            // Snap the WrapPanel width to a whole number of page-width slots.
            // Grid slots carry a constant GridGapPx on-screen gap (divided by zoom because tile
            // margins scale with the view transform); other tiled modes keep the 12px gap.
            double primaryPageW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;
            double pageSlotW = primaryPageW + (_viewMode == ViewMode.Grid
                ? GridGapPx / Math.Max(0.01, _zoomLevel) : 12);
            double availablePreZoom = (viewportW - 24) / _zoomLevel;
            // +1e-6: same floating-point underflow guard as GridZoomStep, so a zoom set for n columns
            // actually lays out n (not n-1) when the division lands a hair under the integer.
            // Grid lays out its AUTHORITATIVE column count (set on grid zoom, restored per tab); it no longer
            // derives columns from the zoom here - that zoom->columns->zoom round-trip lost the grid zoom on
            // tab switches. Other modes still fit to the current zoom.
            int pagesPerRow = _viewMode == ViewMode.TwoPage ? 2
                            : _viewMode == ViewMode.Grid    ? Math.Max(1, _gridColumns)
                            : Math.Max(1, (int)(availablePreZoom / pageSlotW + 1e-6));
            // +0.5/slot: secondary tiles round their DIP width to a whole pixel (AddSecondaryTile),
            // so a row can measure up to half a pixel per tile wider than n exact slots. Without
            // this slack the WrapPanel wraps the row's last tile early and the grid shows n-1
            // columns with a dead column of background on the right. The slack is far below one
            // slot width, so it can never admit an extra column.
            double panelW = pagesPerRow * (pageSlotW + 0.5);
            if (panelW > 0) _pageContentPanel.Width = panelW;

            // Cancel any previously running secondary render.
            _secondaryRenderCts?.Cancel();
            _secondaryRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _secondaryRenderCts;

            // Secondary pages: 1536 px base, scaled up for high-DPI displays so grid / two-page text
            // stays crisp on 150%/200% screens (capped at 3072 to keep memory in check). Stays 1536
            // at 100% DPI, so standard displays are unaffected.
            int SecondaryMax = (int)Math.Min(3072, 1536 * Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX));
            // Grid shows the whole document; Two-Page shows one secondary; other modes peek ahead.
            int limit = _viewMode == ViewMode.Grid
                ? _doc.PageCount
                : Math.Min(_doc.PageCount, primaryPageIdx + 1 + (_viewMode == ViewMode.TwoPage ? 1 : 25));
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
            double primaryDipW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;

            // Render pixels on a background thread and attach each page tile to the UI as soon
            // as it is ready, so large documents fill in progressively instead of blocking
            // until every page has been rendered.
            try
            {
                var session = _active;
                int tileBucket = (int)Math.Round(primaryDipW);   // tiles are sized to the primary width; key the cache by it
                await System.Threading.Tasks.Task.Run(() =>
                {
                    Docnet.Core.Readers.IDocReader? docReader = null;
                    PdfPigDoc? pig = null;   // opened lazily by ImageRectsFor on the first uncached page
                    try
                    {
                        for (int i = primaryPageIdx + 1; i < limit; i++)
                        {
                            if (cts.IsCancellationRequested) break;
                            int rot = secRotations.TryGetValue(i, out int rr) ? rr : 0;

                            // Cache hit: skip pdfium entirely, just attach the cached tile bitmap.
                            var cachedTile = TryGetCachedRender(session, i, tileBucket, rot);
                            if (cachedTile != null)
                            {
                                int pic = i;
                                try
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        if (cts.IsCancellationRequested || _doc is null) return;
                                        if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.TwoPage) return;
                                        AddSecondaryTile(pic, cachedTile, primaryDipW);
                                    });
                                }
                                catch (System.Threading.Tasks.TaskCanceledException) { break; }
                                catch (OperationCanceledException) { break; }
                                continue;
                            }

                            docReader ??= DocLib.Instance.GetDocReader(currentFile, new PageDimensions(SecondaryMax, SecondaryMax));
                            using var pageReader = docReader.GetPageReader(i);
                            int w = pageReader.GetPageWidth();
                            int h = pageReader.GetPageHeight();
                            var rawBytes = pageReader.GetImage(PdfRender.WithAnnotations);   // #141
                            if (w <= 0 || h <= 0 || rawBytes is null) continue;
                            // #135: dark mode with pictures excluded; invert before the rotation
                            // so the carve-out rects stay in unrotated page space.
                            if (BitmapHelpers.DocInvert)
                                BitmapHelpers.InvertBgraInPlaceExcept(rawBytes, w, h, ImageRectsFor(currentFile, i, ref pig));
                            if (rot != 0)
                                (rawBytes, w, h) = BitmapHelpers.RotateBitmap(rawBytes, w, h, rot);

                            int pi = i, pw = w, ph = h, prot = rot;
                            byte[] bytes = rawBytes;
                            try
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (cts.IsCancellationRequested || _doc is null) return;
                                    if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.TwoPage) return;
                                    int ddw = Math.Max(1, (int)Math.Round(primaryDipW));
                                    int ddh = Math.Max(1, (int)Math.Round(primaryDipW * ph / pw));
                                    var bmp = BitmapHelpers.BuildScaledBitmap(pw, ph, bytes, ddw, ddh);
                                    CacheRender(session, pi, tileBucket, prot, bmp);
                                    AddSecondaryTile(pi, bmp, primaryDipW);
                                });
                            }
                            // Dispatcher.Invoke throws when the dispatcher is shutting down (app closing) or
                            // the render was cancelled; stop rendering cleanly instead of crashing.
                            catch (System.Threading.Tasks.TaskCanceledException) { break; }
                            catch (OperationCanceledException) { break; }
                        }
                    }
                    finally { docReader?.Dispose(); pig?.Dispose(); }
                }, cts.Token);
            }
            catch { return; }
        }

        /// <summary>
        /// Builds one secondary-page tile (image + annotation overlay + links) and appends it
        /// to the page content panel. Must run on the UI thread.
        /// </summary>
        private void AddSecondaryTile(int pi, System.Windows.Media.Imaging.BitmapSource bitmap, double primaryDipW)
        {
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            int pageDipW = (int)Math.Round(primaryDipW);
            int pageDipH = (int)Math.Round(primaryDipW * h / w);

            // This page already has a tile: swap just the bitmap (same logical size, crisper pixels).
            // No clear, no reflow - so the grid/spread never jumps or blinks.
            if (_continuousCanvases.TryGetValue(pi, out var exOverlay)
                && exOverlay.Parent is Grid exGrid && exGrid.Children.Count > 0 && exGrid.Children[0] is Image exImg)
            {
                exImg.Source = bitmap;
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
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var overlay = BuildPageOverlay(pi, pageDipW, pageDipH, null);
            overlay.Cursor = CursorForTool(_currentTool);

            var pageGrid = new Grid();
            pageGrid.Children.Add(img);
            pageGrid.Children.Add(overlay);
            AddSecondaryPageLinks(pi, pageDipW, pageDipH);

            var tile = new Border
            {
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
                // Grid: constant GridGapPx on-screen gap between tiles (the document pane
                // background shows through). Two-Page: no margin on the right page - the spread
                // gap is the primary's right margin, and a bottom/trailing margin would make the
                // fit-page margins uneven.
                Margin = _viewMode == ViewMode.Grid
                    ? new Thickness(0, 0, GridGapPx / Math.Max(0.01, _zoomLevel),
                                          GridGapPx / Math.Max(0.01, _zoomLevel))
                    : new Thickness(0),
                Child = pageGrid
            };
            _pageContentPanel.Children.Add(tile);
            RenderAllAnnotations(pi);

            // Grid tiles render asynchronously, so a "scroll to page N" requested when entering grid
            // can't run until page N's tile exists. Do it the moment that tile streams in.
            if (pi == _gridScrollToPage)
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
        }

        internal void BootstrapDocumentView(int initialPage, bool autoFit, bool restoreFitMode = false)
        {
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
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
            _pageJumpBox.IsEnabled = true;
            _pageTotalLabel.Text = $"/ {_doc!.PageCount}";
            if (_doc!.PageCount > 0)
            {
                int page = Math.Max(0, Math.Min(initialPage, _doc.PageCount - 1));
                // Show the panel that matches THIS tab's view mode. This must run for every mode, not
                // only Continuous: switching from a Continuous tab to a Single/Two-Page/Grid tab has to
                // collapse the continuous panel, otherwise the previous tab's continuous render stays on
                // screen over the new document.
                bool isContinuous = _viewMode == ViewMode.Continuous;
                _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
                _continuousPanel.Visibility  = isContinuous ? Visibility.Visible   : Visibility.Collapsed;
                PageList.SelectedIndex = page;
                // Continuous's SelectionChanged returns early (no RenderPage call), so build its panel here.
                if (isContinuous)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () => SetupContinuousView(page, fitDefault: autoFit));
                // Fit / zoom once the first page has rendered and layout has settled.
                // DispatcherPriority.Background is lower than Loaded, so this fires after
                // all pending RenderPage / RefreshPageView callbacks have completed.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() =>
                    {
                        if (autoFit)
                        {
                            // Grid opens to its 3-across default; other modes fit to width.
                            if (_viewMode == ViewMode.Grid)
                            {
                                _gridColumns = Math.Min(_doc?.PageCount ?? 1, 3);
                                SetZoom(GridZoomForN(_gridColumns));
                            }
                            else
                                FitToPage();
                        }
                        else if (restoreFitMode)
                        {
                            // Reopened document: re-fit to the current window if it was in a fit mode,
                            // else apply its exact saved zoom. (Grid's zoom encodes its column count.)
                            if (_viewMode == ViewMode.Grid)       SetZoom(_zoomLevel);
                            else if (_fitMode == FitMode.Width)   FitToWidth();
                            else if (_fitMode == FitMode.Page)    FitToPage();
                            else                                  SetZoom(_zoomLevel);
                        }
                        else
                        {
                            // Tab restore: keep the document's saved zoom. Grid's zoom is really "how many
                            // columns" for the CURRENT window width, and its SizeChanged/settle handlers
                            // recompute it as GridZoomForN(_gridColumns); replay the saved column count the
                            // same way so the two agree instead of fighting (a raw saved zoom from a different
                            // width loses). _gridColumns is restored per tab in ApplySessionState.
                            if (_viewMode == ViewMode.Grid) SetZoom(GridZoomForN(_gridColumns));
                            else SetZoom(_zoomLevel);
                        }
                    }));
            }
        }

        internal void RefreshPageView(int pageIndex)
        {
            if (_viewMode == ViewMode.Continuous)
                return; // continuous mode manages its own rendering
            if (_viewMode == ViewMode.TwoPage) pageIndex -= pageIndex % 2;   // snap to the spread's left page

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
                primaryBorder.Margin = _viewMode == ViewMode.Grid    ? new Thickness(0, 0, gapDip, gapDip)
                                     : _viewMode == ViewMode.TwoPage ? new Thickness(0, 0, 12, 0)
                                     : new Thickness(0);
            }
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
                RenderAdditionalPages(pageIndex);
            else
            {
                ClearSecondaryPages();
                if (_pageContentPanel is not null)
                    _pageContentPanel.Width = double.NaN;
            }
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderPageLinks(pageIndex, dims.w, dims.h);
        }

        internal void ApplyZoom(bool lite = false)
        {
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                st.ScaleX = _zoomLevel;
                st.ScaleY = _zoomLevel;
            }
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
            // zoom to the clamp - "pane A zooms in like crazy when a file opens in pane B"
            // (Steve, 2026-08-01, repeatedly). For the focused pane the two values are identical
            // by the stage-3a sync, so this changes nothing single-pane.
            int applyIdx = State.CurrentPage;
            if (applyIdx >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(applyIdx));

            // If the user has zoomed in past ~10% of the last render, queue a deferred re-render at
            // higher resolution so text re-sharpens quickly (especially on high-DPI displays, where
            // the upscaled bitmap shows blur sooner). The timer debounces rapid Ctrl+scroll.
            // Skipped in Grid: this re-renders via the selected page (not page 0) and, once the render
            // hits its pixel cap when zoomed in, shifts page 0's render width - which is the basis for
            // the grid's column math. That desync locks Ctrl+scroll to a 1<->2 column toggle. The grid
            // is an overview and doesn't need the re-sharpen.
            // Continuous re-sharpens on ANY zoom change (zoom-in sharpens the visible pages, zoom-out
            // restores base bitmaps so hi-res memory is released); the other modes only on a >10% zoom-in.
            if (applyIdx >= 0 && _doc is not null && _viewMode != ViewMode.Grid
                && (_viewMode == ViewMode.Continuous || _zoomLevel > _lastRenderZoom * 1.10))
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
                    // A tick orphaned by a focus switch must not run: _doc, _renderDims and
                    // PageList are shared fields describing the FOCUSED pane, so an unfocused
                    // pane's re-sharpen rendered the OTHER pane's document into its own tiles at
                    // the other document's dimensions - which is what "opening a file in pane B
                    // zooms pane A in like crazy" was (Steve, 2026-08-01). The re-sharpen is a
                    // crispness optimization for the pane being zoomed; a pane that lost focus
                    // mid-debounce keeps its current render, same as the SizeChanged guard above
                    // ("an unfocused pane simply sits the fit out").
                    if (Owner != null && !ReferenceEquals(Owner.ActiveViewer, this)) return;
                    if (_doc is null) return;
                    if (_viewMode == ViewMode.Continuous)
                    {
                        VirtualizeContinuousSlots();   // #122: release far bitmaps / render approaching ones
                        ResharpenContinuousVisible();
                        return;
                    }
                    // Never re-render the primary in Grid (it would shift page 0's width basis and
                    // desync the column math); guards a timer started just before a switch into grid.
                    if (_viewMode != ViewMode.Grid && State.CurrentPage >= 0)
                        RenderPage(State.CurrentPage);
                };
            }
            _rerenderTimer.Stop();
            _rerenderTimer.Start();
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
            return (vw - n * GridGapPx) / (n * rdW);
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
        private double DisplayZoomFactor()
        {
            if (_viewMode == ViewMode.Continuous || _doc is null) return 1.0;
            int idx = _viewMode == ViewMode.Grid ? 0 : Math.Max(0, State.CurrentPage);   // never the shared sidebar's index (see ApplyZoom)
            if (idx < 0 || idx >= _doc.PageCount) return 1.0;
            if (!_renderDims.TryGetValue(idx, out var d) || d.w <= 0) return 1.0;
            double wpt = _doc.Pages[idx].Width.Point, hpt = _doc.Pages[idx].Height.Point;
            if (_pageRotations.TryGetValue(idx, out int r) && (r == 90 || r == 270)) wpt = hpt;
            double naturalW = wpt * 96.0 / 72.0;
            if (naturalW <= 0) return 1.0;
            return d.w / naturalW;
        }
        internal double DisplayZoomPct() => _zoomLevel * DisplayZoomFactor() * 100.0;

        internal void SetZoom(double level)
        {
            _fitMode   = FitMode.None;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, level));
            ApplyZoom();
            SyncZoomBox();
            if (_doc != null && PageList.SelectedIndex >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), PageList.SelectedIndex + 1, _doc.PageCount) + $" - {DisplayZoomPct():F0}%");
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
            double zf = DisplayZoomFactor();
            if (zf <= 0) zf = 1.0;
            SetZoom(trueZoom / zf);
        }

        internal void ZoomIn_Click(object sender, RoutedEventArgs e)  { if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep); }
        internal void ZoomOut_Click(object sender, RoutedEventArgs e) { if (_viewMode == ViewMode.Grid) GridZoomStep(true);  else SetZoom(_zoomLevel - ZoomStep); }

        /// <summary>Set by SyncZoomBox around its programmatic writes. Same story as
        /// _syncingPageList: the box's real subscription is the WINDOW's XAML-bound stub, so the
        /// old detach/attach of this pane's own delegate removed nothing and ADDED a direct
        /// subscription per sync - which is how pane A's handler kept running while pane B was
        /// focused, fitting A against B's document (the zoomtrace smoking gun, 2026-08-01).</summary>
        private bool _syncingZoomBox;

        internal void SyncZoomBox()
        {
            if (_zoomBox is null) return;
            _syncingZoomBox = true;
            try
            {
                // When a fit mode is active, show the "Fit Width"/"Fit Page" entry rather than a raw
                // percentage so the box matches the status bar.
                string? fitTag = _fitMode == FitMode.Width ? "fitwidth"
                               : _fitMode == FitMode.Page  ? "fitpage"
                               : null;
                if (fitTag != null)
                {
                    foreach (ComboBoxItem item in _zoomBox.Items)
                    {
                        if (item.Tag?.ToString() == fitTag)
                        {
                            _zoomBox.SelectedItem = item;
                            return;
                        }
                    }
                }

                string target = $"{DisplayZoomPct():F0}%";
                foreach (ComboBoxItem item in _zoomBox.Items)
                {
                    if (item.Content?.ToString() == target)
                    {
                        _zoomBox.SelectedItem = item;
                        return;
                    }
                }
                // No preset match - clear dropdown selection and show free-form percentage
                _zoomBox.SelectedItem = null;
                _zoomBox.Text = target;
            }
            finally { _syncingZoomBox = false; }
        }

        internal void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingZoomBox) return;   // programmatic sync, not a user pick
            // Belt and braces beside the routing fix in MainWindowViewerBridge: this pane's zoom
            // box actions only ever apply to the focused pane.
            if (Owner != null && !ReferenceEquals(Owner.ActiveViewer, this)) return;
            if (_zoomBox?.SelectedItem is not ComboBoxItem item) return;
            // Editable combos highlight the shown value after a pick (looks like selected text);
            // collapse that selection to just the caret once the value settles.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
            {
                if (_zoomBox.Template?.FindName("PART_EditableTextBox", _zoomBox) is TextBox etb)
                    etb.Select(etb.Text.Length, 0);
            }));
            string? tag = item.Tag?.ToString();
            if (tag is null) return;

            if (tag == "fitwidth") { FitToWidth(); return; }
            if (tag == "fitpage")  { FitToPage();  return; }

            if (double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double z))
            {
                _fitMode = FitMode.None;
                // Preset tags are true zoom (1.0 = 100%); convert to the internal render-dim scale.
                double zf = DisplayZoomFactor(); if (zf <= 0) zf = 1.0;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, z / zf));
                ApplyZoom();
                if (PageList.SelectedIndex >= 0 && _doc != null)
                    SetStatus(string.Format(Loc("Str_PageOf"), PageList.SelectedIndex + 1, _doc.PageCount) + $" - {DisplayZoomPct():F0}%");
            }
        }

        // The selected page's DIP size for fit/zoom math (Single + Two-Page). Prefer _renderDims - it's set
        // synchronously in RenderPage so it always matches the current page and is zoom-stable (scaledMax
        // scales with zoom while RenderPage divides it back out, so the two cancel). Fall back to PageImage's
        // live layout size only when _renderDims has no entry yet, and to 1 to avoid divide-by-zero. Single
        // source so FitToWidth/FitToPage don't each re-derive it. (Continuous/Grid use their own page metrics.)
        private (double w, double h) GetPageDipSize(int idx)
        {
            if (idx >= 0 && _renderDims.TryGetValue(idx, out var d))
                return (d.w, d.h);
            return (PageImage.ActualWidth  > 0 ? PageImage.ActualWidth  : 1,
                    PageImage.ActualHeight > 0 ? PageImage.ActualHeight : 1);
        }

        internal void FitToWidth(bool lite = false)
        {
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;

            // Continuous mode: pages are laid out at _continuousPageW (natural DIPs width)
            // and scaled by the ScaleTransform on PageContentGrid. PageImage is hidden, so
            // we cannot use its Source as a guard; use _continuousPageW directly instead.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0) return;
                _fitMode   = FitMode.Width;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, viewW / _continuousPageW));
                ApplyZoom(lite);
                int ci = State.CurrentPage;   // this pane's page, never the shared sidebar's (see ApplyZoom)
                if (ci >= 0 && _doc != null)
                    SetStatus(string.Format(Loc("Str_FitWidth"), ci + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
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
            if (idx >= 0 && _doc != null)
                SetStatus(string.Format(Loc("Str_FitWidth"), idx + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
        }

        internal void FitToPage(bool lite = false)
        {
            double viewW = PagePreviewPanel.ActualWidth  - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;

            // Continuous mode: derive the current page's natural height from its PDF aspect
            // ratio and _continuousPageW, then fit both axes.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0 || _doc is null) return;
                int ci = State.CurrentPage;   // this pane's page, never the shared sidebar's (see ApplyZoom)
                if (ci < 0 || ci >= _doc.PageCount) return;
                var pdfPage = _doc.Pages[ci];
                double ratio = Math.Max(0.1, pdfPage.Height.Point / Math.Max(1.0, pdfPage.Width.Point));
                double dipH  = _continuousPageW * ratio;
                _fitMode   = FitMode.Page;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                    Math.Min(viewW / _continuousPageW, viewH / dipH)));
                ApplyZoom(lite);
                SetStatus(string.Format(Loc("Str_FitPage"), ci + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
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
            SetStatus(string.Format(Loc("Str_FitPage"), idx + 1, _doc!.PageCount, $"{DisplayZoomPct():F0}"));
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
            else FitToWidth();
        }

        internal void NavigatePageByWheel(int delta)
            => NavigatePageStep(delta > 0 ? -1 : 1);

        // Moves the selection one page - or one two-page SPREAD in Two-Page mode (#120), landing on
        // the spread's left page so a press always shows the NEXT spread instead of re-showing the
        // current one from its right page. direction: -1 = back, +1 = forward. Returns true when
        // the selection moved. Shared by the wheel, the Up/Down edge-flip, and the Left/Right keys.
        internal bool NavigatePageStep(int direction)
        {
            if (_doc is null) return false;
            int cur = PageList.SelectedIndex;
            if (_viewMode == ViewMode.TwoPage)
            {
                int baseIdx = Math.Max(0, cur - cur % 2);   // left page of the current spread
                int target = baseIdx + direction * 2;
                if (target < 0 || target >= _doc.PageCount) return false;
                PageList.SelectedIndex = target;
                return true;
            }
            int t = cur + direction;
            if (t < 0 || t >= _doc.PageCount) return false;
            PageList.SelectedIndex = t;
            return true;
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
            if (Owner != null && !ReferenceEquals(Owner.ActiveViewer, this))
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
            RepositionAnnotationBars();   // cheap; keep the draw/text bar tracking its anchored edge
            if (_cropPreviewRect is not null || _cropConfirmBar is not null) return;

            if (_viewMode == ViewMode.Grid)
            {
                // Grid columns depend only on width, so a height-only resize (e.g. dragging the bottom
                // edge) changes nothing - skip it so it doesn't needlessly re-render/blink.
                if (!e.WidthChanged) return;
                // Hold the column count through a non-modal resize: scale the already-laid-out tiles via
                // the transform so the same number of columns fills the new width (lite, no re-render).
                if (_doc is null || _gridColumns < 1) return;
                double rdWg = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
                if (PagePreviewPanel.ActualWidth <= 0 || rdWg <= 0) return;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(_gridColumns)));
                ApplyZoom(lite: true);
                StartResizeSettleTimer();
                return;
            }

            // Non-modal resize (maximize/restore, splitter, programmatic): rescale lite + settle.
            if (_fitMode == FitMode.Width) FitToWidth(lite: true);
            else if (_fitMode == FitMode.Page) FitToPage(lite: true);
            StartResizeSettleTimer();
        }

        // Coalesces resize ticks: the crisp re-render runs once, a beat after the last size change.
        private void StartResizeSettleTimer()
        {
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
                    if (Owner != null && !ReferenceEquals(Owner.ActiveViewer, this)) ReapplyGridOrFit();
                    else WithOwnSession(OnResizeSettled);
                };
            }
            _resizeRefitTimer.Stop();
            _resizeRefitTimer.Start();
        }

        private void OnResizeSettled()
        {
            if (_viewMode == ViewMode.Grid)
            {
                // Crisp re-render at the held column count for the final size (the drag only transform-
                // scaled the tiles). The grid's width is stable (vertical scrollbar reserved), so this
                // settles in one pass instead of looping.
                if (_doc is not null && _gridColumns >= 1)
                    SetZoom(Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(_gridColumns))));
                RepositionAnnotationBars();   // settle the bar against the final pane size
                return;
            }
            if (_fitMode == FitMode.Width) FitToWidth();
            else if (_fitMode == FitMode.Page) FitToPage();
            RepositionAnnotationBars();   // settle the bar against the final pane size (scrollbar may have toggled)
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

        internal void ApplyViewMode(ViewMode mode)
        {
            if (_viewMode == mode) return;
            _viewMode = mode;
            _renderedPrimaryPage = -1;   // spread/layout changes with the mode; force the next render
            _gridScrollToPage = -1;
            App.SetSetting("ViewMode", mode.ToString());

            bool isContinuous = mode == ViewMode.Continuous;
            _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            _continuousPanel.Visibility  = isContinuous ? Visibility.Visible   : Visibility.Collapsed;

            if (!isContinuous)
            {
                _continuousRenderCts?.Cancel();
                _continuousPanel.Children.Clear();
                _continuousTops.Clear();
                _continuousCanvases.Clear();
                _pages.Clear();
            }

            if (_doc is null) return;
            int idx = PageList.SelectedIndex;
            if (mode == ViewMode.Continuous)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => SetupContinuousView(idx));
            }
            else
            {
                _secondaryRenderCts?.Cancel();
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                // Drop any scroll offset carried over from the previous mode (especially Continuous,
                // whose large vertical offset would otherwise land the grid mid-document).
                PagePreviewPanel.ScrollToVerticalOffset(0);
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    RenderPage(mode == ViewMode.Grid ? 0 : idx);
                    // Grid: apply a clean column-fit zoom (continuous's zoom is far too large for a
                    // grid, and a non-column zoom leaves a gap). SetZoom -> ApplyZoom defers the
                    // single tile render, so return here instead of calling RefreshPageView again
                    // (a second render would duplicate tiles).
                    if (mode == ViewMode.Grid)
                    {
                        _gridColumns = Math.Min(_doc!.PageCount, 3);
                        SetZoom(GridZoomForN(_gridColumns));
                        // The first fit can run before the viewport width has settled (leaving the
                        // grid off-center / at the wrong zoom); re-fit once more after layout settles,
                        // and pin to the top so nothing carries over from the previous mode.
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                            (Action)(() =>
                            {
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
                    // awkward carried-over zoom from another mode.
                    if      (mode == ViewMode.Single || mode == ViewMode.TwoPage) FitToPage();
                    else if (_fitMode == FitMode.Width) FitToWidth();
                    else if (_fitMode == FitMode.Page)  FitToPage();
                    else                                ApplyZoom();
                    RefreshPageView(idx);
                });
            }
        }
    }
}
