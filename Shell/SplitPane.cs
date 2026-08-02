using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Controls;

namespace KillerPDF
{
    /// <summary>
    /// Two document panes side by side in one window.
    ///
    /// One toolbar, sidebar, status line and set of document fields serve both panes, so every
    /// window -> viewer call resolves through <see cref="ActiveViewer"/> rather than naming a pane.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Neither pane may go below this. Both handles clamp against both minimums, so
        /// dragging either one stops at whichever pane would go under first.</summary>
        private const double MinPaneWidth = 320;

        /// <summary>Inset a pane keeps from the window edge.</summary>
        private const double PaneEdge = 8;

        /// <summary>Channel between the two cards, sized so the gap either side of a pane reads the
        /// same whether its neighbor is the other pane or the window edge. Zero when unsplit.</summary>
        private const double SplitGutter = PaneEdge;

        /// <summary>The pane every window control acts on.</summary>
        internal PdfViewer ActiveViewer { get; private set; } = null!;

        private bool _isSplit;

        /// <summary>Read by each pane's RebuildTabStrip: while split, both panes show their tab band
        /// even with one document, or their card tops sit at different heights.</summary>
        internal bool IsSplit => _isSplit;

        private bool _draggingSplit;
        private double _splitDragStartX;
        private double _splitDragStartAWidth;

        /// <summary>Pane A's chosen width. A is the FIXED column and B takes the remainder, so
        /// narrowing the window eats into pane B first and pane A keeps the size you gave it. Only
        /// once B is down to <see cref="MinPaneWidth"/> does A start giving ground, and it stops at
        /// the same minimum. It was the other way round - A star-sized - which meant dragging the
        /// window's right edge shrank the pane at the far LEFT of the window.</summary>
        private double _paneAWidth;

        /// <summary>Wire both panes up. Called from the constructor.</summary>
        private void InitSplitPanes()
        {
            Viewer.Owner  = this;
            ViewerB.Owner = this;
            ActiveViewer  = Viewer;

            // Each pane builds its own tile tree. Routing this through ActiveViewer would build
            // pane A twice and leave pane B with a null annotation canvas.
            Viewer.InitTiles();
            ViewerB.InitTiles();

            // Each pane's strip binds to its OWN session collection. Routing this through
            // ActiveViewer would bind pane A's strip twice and leave pane B's showing nothing.
            Viewer.InitTabStripExt();
            ViewerB.InitTabStripExt();

            // PreviewMouseDown, not MouseDown: the page overlays and annotation tools handle the
            // bubbling event and would swallow it.
            Viewer.PreviewMouseDown  += (_, _) => FocusPane(Viewer);
            ViewerB.PreviewMouseDown += (_, _) => FocusPane(ViewerB);

            // The wheel focuses too. The zoom toolbar, the zoom box and Ctrl+wheel all act on the
            // FOCUSED pane, so wheeling over a pane you had not clicked was zooming and scrolling
            // the other one - which reads as "zooming pane B zoomed pane A". Preview, so focus has
            // moved before the pane's own wheel handler runs.
            Viewer.PreviewMouseWheel  += (_, _) => FocusPane(Viewer);
            ViewerB.PreviewMouseWheel += (_, _) => FocusPane(ViewerB);

            // Re-lay the columns whenever the host resizes, so the window's own edge takes width
            // out of pane B first. Without this A, being the fixed column, would simply keep its
            // width and B would be clipped to nothing.
            //
            // SyncSplitMinWidth must NOT be called from here. Setting MinWidth can resize the
            // window, which fires this handler again, which sets MinWidth again - an unbounded
            // layout loop that hard-froze the app. The floor only changes when the split opens or
            // closes, so that is the only place it is recomputed.
            //
            // Queued, never run inline. ApplyPaneWidths writes the column widths, which is itself a
            // layout change - assigning straight from the handler re-enters the layout pass it was
            // raised by. At Background priority it runs after that pass has finished.
            SplitHost.SizeChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(ApplyPaneWidths),
                                       System.Windows.Threading.DispatcherPriority.Background);

            ApplyFocusHalo();
        }

        /// <summary>Point the window's element accessors at a pane, with NONE of FocusPane's side
        /// effects, and hand back the previous one. WithOwnSession needs this: it swaps the document
        /// FIELDS to a pane, but every element the render path reaches for - PageGrid, PageHost,
        /// ContinuousHost, PreviewScroller - still resolves through ActiveViewer, so an unfocused
        /// pane's re-fit measured and painted into the OTHER pane's tiles. That is what zoomed a
        /// pane wildly, and what put one pane's pages into the other.</summary>
        internal PdfViewer SwapActiveViewer(PdfViewer pane)
        {
            var prev = ActiveViewer;
            ActiveViewer = pane;
            return prev;
        }

        /// <summary>F10 toggles the split. Bound from KeyboardShortcuts.</summary>
        internal void ToggleSplit()
        {
            if (_isSplit) CloseSplit();
            else OpenSplit();
        }

        /// <summary>Reopen the split and pane B's tabs from the last session. Runs at the tail of
        /// the startup restore, after pane A is populated.
        ///
        /// Pane B's tabs are restored the same lazy way pane A's are - placeholders, with only its
        /// active tab loaded - but loading it has to happen with B focused, because the load path
        /// writes through the window's shared document fields. Focus always returns to A; which
        /// pane had focus at exit is not saved yet.</summary>
        private bool _restoringSplit;   // set only during that restore - see OpenSplit

        private void RestorePaneB()
        {
            try { RestorePaneBCore(); }
            catch (Exception ex)
            {
                // A failed restore must never take the window down with it. Whatever went wrong,
                // the app still has to come up - worst case with one pane and no tabs in B.
                _restoringSplit = false;
                SetStatus($"Could not restore the second pane: {ex.Message}");
            }
        }

        private void RestorePaneBCore()
        {
            if (App.GetSetting("SplitOpen") != "1") return;

            // Seed pane A's fixed width from the saved setting BEFORE opening the split.
            // OpenSplit's own fallback (Viewer.ActualWidth) is always 0 at this point - the window
            // is still inside its own Loaded handler and has not laid out yet - so without this,
            // pane A opened pinned to MinPaneWidth every launch no matter what width it was left
            // at, which is what "the pane is always small when the app loads" was (#161).
            var savedA = App.GetSetting("SplitPaneAWidth");
            if (!string.IsNullOrEmpty(savedA)
                && double.TryParse(savedA, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double aw)
                && aw >= MinPaneWidth)
                _paneAWidth = aw;

            _restoringSplit = true;
            try { OpenSplit(); }
            finally { _restoringSplit = false; }

            var saved = App.GetSetting("OpenTabsB");
            if (string.IsNullOrEmpty(saved)) return;

            var restored = new System.Collections.Generic.List<PdfViewer.DocumentSession>();
            foreach (var f in saved!.Split('|'))
                if (!string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                    restored.Add(PdfViewer.MakeDeferredSession(f));
            if (restored.Count == 0) return;

            var wantActive = App.GetSetting("ActiveTabB");
            var target = (!string.IsNullOrEmpty(wantActive)
                    ? restored.FirstOrDefault(s => string.Equals(s.OriginalFile, wantActive,
                                                                 StringComparison.OrdinalIgnoreCase))
                    : null)
                ?? restored[0];

            ViewerB.SetSessionsExt(restored, target);
            FocusPane(ViewerB);
            ViewerB.ApplySessionStateExt(target);
            ViewerB.MaterializeDeferredExt(target);
            ViewerB.RebuildTabStripExt();
            FocusPane(Viewer);

            // B's document was loaded against whatever width the column had at that instant, which
            // during startup is not yet its final one. Re-fit both once the layout settles - same
            // reason as the queued fit in OpenSplit, one pass later.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Viewer.ReapplyGridOrFit();
                ViewerB.ReapplyGridOrFit();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Sidebar rail button, same action as F10.</summary>
        private void SplitPaneRailBtn_Click(object sender, RoutedEventArgs e) => ToggleSplit();

        /// <summary>Light the rail button while the split is open, the same tell the night-mode
        /// moon beside it uses.</summary>
        private void SyncSplitRailButton()
        {
            if (SplitPaneRailBtn != null) SplitPaneRailBtn.Tag = _isSplit ? "on" : null;
        }

        private void OpenSplit()
        {
            if (_isSplit) return;
            _isSplit = true;

            // Pane A keeps the width it already has and the WINDOW grows to make room for pane B,
            // which opens matching it. Halving pane A instead would resize the document you are
            // reading just to put a second one beside it - and it is the counterpart of the close,
            // which shrinks the window back by exactly the same amount, so F10 round-trips to where
            // it started. If there is no room left on the work area the window grows as far as it
            // can and ApplyPaneWidths takes the shortfall out of pane A, down to the minimum.
            // During the startup restore, pane A's width was already seeded from the saved setting
            // by RestorePaneBCore, above, before this ran - Viewer.ActualWidth is not usable there,
            // see the comment on that seed. Everywhere else, pane A keeps the width it already has.
            double aNow = _restoringSplit && _paneAWidth > 0
                ? _paneAWidth
                : Viewer.ActualWidth > 0 ? Viewer.ActualWidth : MinPaneWidth;
            _paneAWidth = aNow;

            // No window resize during the startup restore. The window is still inside its Loaded
            // handler and has not rendered yet; changing Width there produced a blank, unpainted
            // window. On restore the saved size already accounts for both panes anyway, so there is
            // nothing to grow by - ApplyPaneWidths just divides what is there.
            double grow = 0;
            if (WindowState == WindowState.Normal && !_restoringSplit)
            {
                double room = SystemParameters.WorkArea.Right - Left;
                grow = Math.Min(aNow + SplitGutter, Math.Max(0, room - Width));
            }
            // Pane B's final width is whatever the window could actually give it.
            double bTarget = Math.Max(MinPaneWidth, grow - SplitGutter);

            // Maximized, snapped, or already hard against the work area edge: there is no room to
            // grow the window by pane A's full width, so keeping pane A at its size would squeeze
            // pane B down to the bare minimum - a lopsided split nobody asked for on a window that
            // is plainly big enough for two. When the window cannot grow that far, split what is
            // already there evenly instead (Steve, 2026-08-01). The exact round-trip (pane A keeps
            // its size, the window grows to match) stays the behavior whenever there IS room.
            bool evenSplit = !_restoringSplit && grow + 0.5 < aNow + SplitGutter;
            if (evenSplit)
            {
                _paneAWidth = Math.Max(MinPaneWidth, (aNow - SplitGutter) / 2);
                bTarget     = Math.Max(MinPaneWidth, aNow - SplitGutter - _paneAWidth);
            }

            // Start closed and slide open. During the restore there is nothing to slide - the panes
            // are simply there when the window paints - so the columns go straight to their places.
            PaneACol.Width      = new GridLength(aNow, GridUnitType.Pixel);
            PaneBCol.Width      = new GridLength(0, GridUnitType.Pixel);
            PaneGutterCol.Width = new GridLength(0);
            if (_restoringSplit)
            {
                PaneGutterCol.Width = new GridLength(SplitGutter);
                // NOT called synchronously here. RestoreWindowSettings ran moments ago in the same
                // Loaded handler, but WPF layout is asynchronous - SplitHost.ActualWidth at this
                // point still reflects the window's PRE-restore size (or 0), not the size just
                // assigned. Calling ApplyPaneWidths against that stale/zero width is what clamped
                // pane A down and left pane B with whatever tiny remainder fell out - the saved
                // split came back on every launch no matter what it was saved at (#161). Deferred to
                // Loaded priority, same as the re-fit below and in RestorePaneBCore's tail, so it
                // runs once the restored window size has actually been laid out. The window is still
                // hidden behind RootClipGrid's opacity hold at this point (see ContentRendered),
                // so there is nothing to see between the temporary zero-width column set two lines
                // up and this correcting it.
                // SyncSplitMinWidth reads SplitHost.ActualWidth too (via its "chrome" measurement),
                // so it rides along in the same deferred call rather than running synchronously
                // below against the same stale width.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyPaneWidths(); SyncSplitMinWidth();
                    // Same width-gate re-run as OpenSplit's done callback: on restore the panes
                    // reach their real widths only after this deferred layout pass.
                    Viewer.SyncRecentBoxWidth(); ViewerB.SyncRecentBoxWidth();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            ViewerB.Visibility       = Visibility.Visible;
            SplitHandleA.Visibility  = Visibility.Visible;
            SplitHandleB.Visibility  = Visibility.Visible;

            // Both bands, so both card tops line up - the band is a two-pane decision now.
            Viewer.RebuildTabStripExt();
            ViewerB.RebuildTabStripExt();

            // Pane B's start screen has never been filled: its recent list is only populated when a
            // pane shows its empty state, and B goes straight from hidden to visible without one.
            // Without this its Recent box stayed blank until B first took focus.
            PopulateRecentFilesList();

            ApplyFocusHalo();
            SyncSplitRailButton();
            SetStatus(Loc("Str_St_SplitOn"));

            if (_restoringSplit)
            {
                // Already in place, and no animation during startup: a timer-driven slide running
                // while the window is still being brought up left it painting nothing at all.
                // (ApplyPaneWidths/SyncSplitMinWidth already queued above, deferred past this point.)
                return;
            }

            // Pane A only tweens when the even-split branch above actually moved its target; the
            // exact-round-trip path leaves it null and AnimateSplitWidth skips it, unchanged from
            // before.
            AnimateSplitWidth(opening: true, bTarget, grow,
                aFrom: evenSplit ? aNow : (double?)null, aTo: evenSplit ? _paneAWidth : (double?)null,
                done: () =>
            {
                ApplyPaneWidths();     // hand pane B back to the star column
                SyncSplitMinWidth();
                // Both panes just changed width, so both have to re-fit. Queued at Loaded so it
                // runs once the columns are real - on the startup restore the split opens before
                // the first layout pass has settled, and pane A kept the zoom it was fitted at
                // full width and opened clipped, with a horizontal scrollbar.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Viewer.ReapplyGridOrFit();
                    if (_isSplit) ViewerB.ReapplyGridOrFit();
                    // Re-gate the start screens' Recent boxes against the SETTLED widths. The
                    // populate above ran while pane B's column was still 0 (the slide had not
                    // started), so SyncRecentBoxWidth's width gate collapsed B's box and nothing
                    // re-ran it - pane B showed an empty start screen until the next open action
                    // repopulated the list (Steve, 2026-08-01).
                    Viewer.SyncRecentBoxWidth();
                    ViewerB.SyncRecentBoxWidth();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            });
        }

        private System.Windows.Threading.DispatcherTimer? _splitAnim;

        /// <summary>True while the open/close slide is running. ApplyPaneWidths sits it out - it is
        /// driven from SplitHost's SizeChanged, which fires on every frame of the slide, and it
        /// would put pane B straight back on the star column and undo the animation.</summary>
        private bool _splitAnimating;

        /// <summary>Slide the split open or shut. Pane B's column and the gutter animate between 0
        /// and <paramref name="bTarget"/>; the window's width changes by <paramref name="widthDelta"/>
        /// in one step. Pane A normally never changes size - the window absorbs the whole
        /// difference - but the even-split open (OpenSplit, when the window cannot grow) also has to
        /// shrink pane A down to its half-share, and <paramref name="aFrom"/>/<paramref name="aTo"/>
        /// tween it alongside B so the two panes move together instead of A sitting frozen at full
        /// width until ApplyPaneWidths snaps it down on the final frame - which is what "the panes
        /// slid all weird before settling" was (Steve, 2026-08-01).
        /// GridLength has no built-in animation, so the columns are stepped off a timer.</summary>
        private void AnimateSplitWidth(bool opening, double bTarget, double widthDelta, Action done,
                                       double? aFrom = null, double? aTo = null)
        {
            _splitAnim?.Stop();
            _splitAnimating = false;

            // The WINDOW's width changes in ONE step, never animated. Stepping Width frame by frame
            // off a timer put the app in an unusable state: it fights whatever the window manager
            // is doing, and a tick landing while Windows was in its own modal resize loop could
            // stall the animation - leaving _splitAnimating latched, the columns frozen part-open
            // with the drag handles still live over the pane, the mouse captured and the window
            // unresizable. Only the columns slide.
            if (WindowState == WindowState.Normal && Math.Abs(widthDelta) > 0.5)
            {
                double w = Width + (opening ? widthDelta : -widthDelta);
                Width = Math.Max(MinWidth, w);
            }

            double bStart = opening ? 0 : ViewerB.ActualWidth;
            double bEnd   = opening ? bTarget : 0;
            double gStart = opening ? 0 : SplitGutter;
            double gEnd   = opening ? SplitGutter : 0;
            // Only set on the even-split open; every other call leaves both null, and the tick below
            // then never touches PaneACol at all - identical to the pre-existing behavior.
            bool   tweenA  = aFrom.HasValue && aTo.HasValue && Math.Abs(aFrom.Value - aTo.Value) > 0.5;
            double aStart  = aFrom ?? 0;
            double aEnd    = aTo   ?? 0;

            if (bStart <= 0 && bEnd <= 0 && !tweenA) { done(); return; }   // nothing to slide

            _splitAnimating = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const double durationMs = 160;
            _splitAnim = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(15) };
            _splitAnim.Tick += (_, _) =>
            {
                // Belt and braces: finish on elapsed time, so a dropped or delayed tick can never
                // strand the split half-open with its state latched.
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / durationMs);
                double e = 1 - Math.Pow(1 - t, 3);   // ease out, so it settles rather than stopping dead
                PaneGutterCol.Width = new GridLength(gStart + (gEnd - gStart) * e);
                PaneBCol.Width      = new GridLength(bStart + (bEnd - bStart) * e, GridUnitType.Pixel);
                if (tweenA) PaneACol.Width = new GridLength(aStart + (aEnd - aStart) * e, GridUnitType.Pixel);
                if (t >= 1)
                {
                    _splitAnim!.Stop();
                    _splitAnimating = false;
                    done();
                }
            };
            _splitAnim.Start();
        }

        private void CloseSplit()
        {
            if (!_isSplit) return;
            _isSplit = false;

            // Focus returns to A before B is hidden: leaving ActiveViewer pointing at a collapsed
            // pane would leave the toolbar driving something invisible.
            FocusPane(Viewer);

            double aStart = Viewer.ActualWidth;
            double bStart = ViewerB.ActualWidth;

            // The WINDOW shrinks back by pane B plus the gutter; pane A keeps the width it has.
            // Expanding pane A to swallow both panes was the wrong half of the trade - closing the
            // second pane should put the window back where opening it found it, not resize the
            // document you were reading. Drop the floor first, with _isSplit already false, or the
            // two-pane minimum blocks the shrink.
            SyncSplitMinWidth();

            // Handles go first, not in FinishCloseSplit. They sit in the gutter column and stay
            // hit-testable while it is closing, so a click during the slide could grab a divider
            // that is on its way out and capture the mouse with nothing left to drag.
            SplitHandleA.Visibility = Visibility.Collapsed;
            SplitHandleB.Visibility = Visibility.Collapsed;
            EndSplitDrag(SplitHandleA);
            EndSplitDrag(SplitHandleB);

            if (aStart <= 0 || bStart <= 0) { FinishCloseSplit(); return; }   // never laid out

            PaneACol.Width = new GridLength(aStart, GridUnitType.Pixel);
            PaneBCol.Width = new GridLength(bStart, GridUnitType.Pixel);

            // THE RULE IS THE CORNERS (Steve, 2026-08-01, after several rounds of narrower
            // conditions each missing a case):
            //  - SQUARED corners (_chromeSquared: maximized OR snapped) - the window is pinned to
            //    screen edges and must not move, so pane A expands to fill the space pane B gives
            //    up. WindowState alone is NOT this test: a snapped window stays WindowState.Normal
            //    (see OnWindowLocationChanged), which is exactly the case every earlier version of
            //    this condition got wrong.
            //  - ROUNDED corners (floating) - closing the second pane closes the second pane: the
            //    window shrinks by pane B plus the gutter and pane A keeps the size it had.
            // The widthDelta is 0 in the squared case so AnimateSplitWidth cannot shrink a snapped
            // window (it skips only MAXIMIZED ones on its own, since they are not Normal).
            bool fillA = _chromeSquared;
            double aTarget = fillA ? aStart + bStart + SplitGutter : aStart;
            _paneAWidth = aTarget;

            AnimateSplitWidth(opening: false, 0, fillA ? 0 : bStart + SplitGutter, FinishCloseSplit,
                aFrom: fillA ? aStart : (double?)null, aTo: fillA ? aTarget : (double?)null);
        }

        /// <summary>Tail of CloseSplit: everything that must be true once the slide has finished.
        /// Split apart so the animation and the never-laid-out shortcut share it.</summary>
        private void FinishCloseSplit()
        {
            PaneGutterCol.Width = new GridLength(0);
            PaneBCol.Width      = new GridLength(0);
            PaneACol.Width      = new GridLength(1, GridUnitType.Star);   // one pane takes it all
            SyncSplitMinWidth();                                          // release the split floor

            ViewerB.Visibility      = Visibility.Collapsed;
            SplitHandleA.Visibility = Visibility.Collapsed;
            SplitHandleB.Visibility = Visibility.Collapsed;

            Viewer.RebuildTabStripExt();   // back to the single-pane rule: hide the band under two tabs
            Viewer.SyncRecentBoxWidth();   // pane A may have just widened past the Recent box's gate

            ApplyFocusHalo();
            SyncSplitRailButton();
            SetStatus(Loc("Str_St_SplitOff"));
        }

        /// <summary>Point the window's chrome at a pane and move the halo. Cheap and idempotent, so
        /// it is safe to call from every mouse-down. Internal (not private): PdfViewer's own
        /// SwitchToTab calls this too, to re-assert ownership of the shared fields before a tab
        /// switch inside a pane that is not (yet) ActiveViewer - see the comment there.</summary>
        internal void FocusPane(PdfViewer pane)
        {
            if (ReferenceEquals(ActiveViewer, pane)) return;

            // _doc, _currentFile, _annotations and the rest are window fields that both panes bridge
            // to, so they describe one pane at a time. A pane's documents live in its session list
            // and swap into those fields when it takes focus - the same handshake tab switching
            // uses. Without it the sidebar, page count and status line keep describing the pane you
            // just left. Capture before the swap, apply after, or the outgoing pane's scroll and
            // zoom land in the incoming pane's session.
            ActiveViewer.CaptureActiveIfAny();
            ActiveViewer = pane;
            pane.ApplyActiveSessionIfAny();

            ApplyFocusHalo();

            // Restore rather than refresh: RefreshPageList re-decodes every page, which on a large
            // document costs seconds on every click between panes.
            RestorePageListForActivePane();
            LoadOutlines();
            SyncZoomBox();

            // No render here. Each pane keeps its own tile tree, so its document stays painted
            // whether or not it has focus - focus moves the chrome, not the pixels. A
            // RenderActiveSession() call here also fires on any mouse-down that reaches a pane,
            // including while the file dialog is open, painting a document into it before the user
            // has picked one.
        }

        /// <summary>Accent border on the focused pane, normal border on the other. Only while split:
        /// with one pane there is nothing to disambiguate.</summary>
        private void ApplyFocusHalo()
        {
            if (!_isSplit)
            {
                Viewer.SetFocusHalo(false);
                ViewerB.SetFocusHalo(false);
                return;
            }
            Viewer.SetFocusHalo(ReferenceEquals(ActiveViewer, Viewer));
            ViewerB.SetFocusHalo(ReferenceEquals(ActiveViewer, ViewerB));
        }

        /// <summary>Lay the two columns out from <see cref="_paneAWidth"/>: A is the fixed column,
        /// B is the star that takes what is left. Run on every split-host resize as well as on the
        /// gutter drag, so narrowing the window comes out of B until B is at the minimum, then out
        /// of A until A is too. Neither pane can be squeezed below MinPaneWidth from any direction.</summary>
        private void ApplyPaneWidths()
        {
            if (!_isSplit || _splitAnimating) return;   // the slide owns the columns while it runs
            double avail = SplitHost.ActualWidth - SplitGutter;
            if (avail <= 0) return;

            double aW = _paneAWidth > 0 ? _paneAWidth : avail / 2;
            // Give B its minimum first, then let A have what it asked for out of the rest.
            double maxA = avail - MinPaneWidth;
            aW = Math.Min(aW, maxA);
            aW = Math.Max(aW, MinPaneWidth);
            // Window too narrow to honor both: A keeps the minimum and B takes the remainder. The
            // window's own MinWidth (SyncSplitMinWidth) is what normally stops this happening.
            if (aW > avail) aW = Math.Max(0, avail);

            // Only write when it actually moves, or every layout pass has a fresh value to react to.
            if (PaneACol.Width.IsStar || Math.Abs(PaneACol.Width.Value - aW) > 0.5)
                PaneACol.Width = new GridLength(aW, GridUnitType.Pixel);
            if (!PaneBCol.Width.IsStar)
                PaneBCol.Width = new GridLength(1, GridUnitType.Star);
        }

        /// <summary>The window has no minimum of its own. The only floor is the PANE minimum, which
        /// is the same number in both modes - one pane's worth unsplit, two plus the gutter when
        /// split. Everything outside the panes (the sidebar, the margins) is MEASURED rather than
        /// assumed, so collapsing or moving the sidebar lowers the floor by exactly its width
        /// instead of the pane having to give the space up.</summary>
        private void SyncSplitMinWidth()
        {
            double chrome = Math.Max(0, ActualWidth - SplitHost.ActualWidth);
            double floor = _isSplit
                ? chrome + SplitGutter + MinPaneWidth * 2
                : chrome + MinPaneWidth;
            // Only when it actually moves. An unconditional assignment can resize the window, and
            // anything that re-enters this from a layout event then has a loop to run round.
            if (Math.Abs(MinWidth - floor) > 0.5) MinWidth = floor;
        }

        // One boundary, two handles: whichever you grab is the pane you are sizing. Both resolve to
        // pane A's width - B is the star and takes the remainder - so they cannot disagree.

        private void SplitHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isSplit || sender is not Border h) return;
            _draggingSplit        = true;
            _splitDragStartX      = e.GetPosition(SplitHost).X;
            _splitDragStartAWidth = Viewer.ActualWidth;
            h.CaptureMouse();
            e.Handled = true;
        }

        private void SplitHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingSplit || sender is not Border h || !h.IsMouseCaptured) return;

            // Self-heal a lost button-up. If capture survives past the release - which happens when
            // the up lands on the window's own resize border - the handle keeps the mouse, the
            // resize cursor stays on screen and nothing else can be clicked until something else
            // steals capture. Checking the real button state on every move ends the drag anyway.
            if (e.LeftButton != MouseButtonState.Pressed) { EndSplitDrag(h); return; }

            double dx    = e.GetPosition(SplitHost).X - _splitDragStartX;
            double total = SplitHost.ActualWidth;
            double aimA  = _splitDragStartAWidth + dx;

            double maxA = total - SplitGutter - MinPaneWidth;

            // Out of slack: pane B is already at its minimum and cannot give up any more. Rather
            // than the divider going dead, GROW THE WINDOW by what pane A still wants - pane A
            // gets bigger, pane B keeps exactly its width and simply travels right with the
            // window's edge. Stops at the edge of the work area, and does nothing while maximized.
            if (aimA > maxA && WindowState == WindowState.Normal)
            {
                double room = SystemParameters.WorkArea.Right - Left;
                double grow = Math.Min(aimA - maxA, Math.Max(0, room - Width));
                if (grow > 0)
                {
                    Width += grow;
                    maxA  += grow;
                }
            }

            if (maxA < MinPaneWidth) return;              // window too narrow to split meaningfully

            _paneAWidth = Math.Max(MinPaneWidth, Math.Min(maxA, aimA));
            ApplyPaneWidths();
            e.Handled = true;
        }

        /// <summary>Release the drag and the mouse together. Both the normal button-up and the
        /// self-heal in MouseMove come through here, so capture cannot be left behind.</summary>
        private void EndSplitDrag(Border? h)
        {
            if (h is { IsMouseCaptured: true }) h.ReleaseMouseCapture();
            _draggingSplit = false;
        }

        /// <summary>Anything at all that takes capture away mid-drag - an alt-tab, a system move,
        /// another control grabbing it - has to leave the drag state consistent, or the next click
        /// is swallowed by a drag that thinks it is still running.</summary>
        private void SplitHandle_LostMouseCapture(object sender, MouseEventArgs e)
            => _draggingSplit = false;

        private void SplitHandle_MouseUp(object sender, MouseButtonEventArgs e)
        {
            EndSplitDrag(sender as Border);
            e.Handled = true;

            // Re-fit once the drag settles, not per mouse-move: a re-render every frame stutters
            // on a large document.
            Viewer.ReapplyGridOrFit();
            if (_isSplit) ViewerB.ReapplyGridOrFit();
            // The drag changed both panes' widths; re-gate their Recent boxes (an empty pane
            // dragged wide enough should gain the list, one squeezed narrow should shed it).
            Viewer.SyncRecentBoxWidth();
            if (_isSplit) ViewerB.SyncRecentBoxWidth();
        }
    }
}
