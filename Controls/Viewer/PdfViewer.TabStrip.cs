using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    // This pane's tab band: which tabs are in the strip, which of them owns an edge, what the card's
    // top corners do, where the focus ring runs, and the drag that reorders them or hands one to the
    // other pane.
    //
    // Ported from KillerShell (FilePane.xaml + Tabs.cs + DualPane.cs + PaneDrag.cs). The strip is an
    // ItemsControl bound to _sessions over a UniformGrid, and every visual decision below is a
    // NOTIFYING FLAG ON THE SESSION that a template trigger reads - not a property written onto a
    // code-built Border. That is the whole point: there is one place each rule is expressed, so a
    // fix to one edge case cannot break the next one.
    //
    // Two consequences worth knowing before changing anything here:
    //   * UniformGrid divides the band equally, so the last visible tab ALWAYS reaches the strip's
    //     right edge. Edge ownership is decided, never measured. The old strip measured it after
    //     every reflow, which is why the halo came and went with the pane width.
    //   * A collapsed child is not counted when UniformGrid divides the band, so windowing tabs out
    //     into the chevron needs no width arithmetic at all - the survivors fill the band on their own.
    public partial class PdfViewer
    {
        /// <summary>Bind the strip to this pane's sessions. Called once, from InitSplitPanes.</summary>
        private void InitTabStrip()
        {
            if (TabStrip != null) TabStrip.ItemsSource = _sessions;
        }

        /// <summary>
        /// The one funnel. Every add, close, switch, drag-reorder and resize ends here, and it is the
        /// only thing that writes the strip's state.
        /// </summary>
        /// <remarks>
        /// Still called RebuildTabStrip because ~20 call sites and RebuildTabStripExt already say so,
        /// but it rebuilds nothing: the ItemsControl repaints itself off the collection and the
        /// notifying flags.
        /// </remarks>
        private void RebuildTabStrip()
        {
            if (TabStrip == null || TabStripBorder == null) return;

            foreach (var t in _sessions) t.RefreshTabLabel();

            int docTabs = _sessions.Count(t => t.Doc != null || t.DeferredPath != null);
            // Only show the strip once THIS pane has more than one document - a single open PDF
            // doesn't need tabs, split or not. (KillerShell forces the band on in both panes whenever
            // either one has 2+ tabs, so the two card tops always line up; Steve asked for the
            // simpler per-pane rule instead, so a lone tab never shows a bar even while split.)
            bool show = docTabs > 1;
            TabStripBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // The -1 tucks the card's top border a pixel into the band, so the active tab and the card
            // read as one surface. ONLY while there IS a band: a collapsed element contributes no
            // height, so with no strip the -1 lifts this pane a pixel above the other one instead of
            // tucking under anything. The bandless card gets 3px of top air instead of 0 - at 0 it
            // opened 3px too high against the chrome (Steve, 2026-08-01).
            CardRow.Margin = new Thickness(0, show ? -1 : 3, 0, 0);

            // Which tabs fit at this width, before anything below asks which is on an edge.
            ApplyTabWindow();

            // First and last VISIBLE, not first and last in the list. Both are about the strip's own
            // edges: IsLast drops the divider that would otherwise land on the right edge as a stray
            // rule, and IsFirst/IsLast keep the tab from drawing the outer ring side that the band
            // already draws. With tabs windowed out, the tab sitting on an edge is not the one at the
            // end of the collection.
            //
            // And NOT the last visible tab while the chevron is showing: the chevron is what sits on
            // the band's right edge then, so the tab is a middle tab in every way that matters. Told
            // otherwise it dropped the divider that separates it from the chevron AND handed its right
            // side to the band, which drew that side at the band's edge - past the chevron, as an
            // accent stripe up the far right with nothing under it.
            bool chevron = TabOverflowBtn.Visibility == Visibility.Visible;

            var strip = _sessions.Where(t => t.IsStripVisible).ToList();
            foreach (var t in _sessions) { t.IsFirst = false; t.IsLast = false; }
            if (strip.Count > 0)
            {
                strip[0].IsFirst              = true;
                strip[strip.Count - 1].IsLast = !chevron;
            }

            SyncPaneLeadingCorner();
            UpdatePaneFocusRing();
            UpdateTabStripFade();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)UpdateFooterFade);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  OVERFLOW
        // ════════════════════════════════════════════════════════════════════════════════════════
        // The strip is a UniformGrid, so every tab takes an equal share of the band whatever the
        // count - right up to a point, and then off a cliff. Eight tabs in a half-width pane came out
        // around forty pixels each, which is not a label, it is a shape. A tab you cannot read is a
        // tab you have to click to identify, and at that point the strip has stopped being navigation.
        //
        // So the COUNT is capped rather than the width. As many tabs as fit at TabFloorWidth stay in
        // the strip and the rest are collapsed. The chevron at the right end lists every tab, so
        // nothing is unreachable.
        //
        // Scrolling was the other option and is what a browser does. It lost because the band is a
        // bordered surface the pane's focus ring runs along, and a scrolled band cannot be edge to
        // edge - the ring would have to stop somewhere that is not a corner.

        /// <summary>Narrowest a tab may get before the strip stops taking more.</summary>
        /// <remarks>
        /// Picked from what it has to hold rather than off a grid: 120px is about sixteen characters
        /// at this size once the close x and the padding are paid for - "Quarterly-Repo...", enough to
        /// tell two documents apart. Much below a hundred and the ellipsis starts eating the part that
        /// distinguishes them, which is the whole job.
        /// </remarks>
        private const double TabFloorWidth = 120;

        /// <summary>What the chevron takes out of the band while it is showing.</summary>
        private const double TabChevronWidth = 26;

        /// <summary>Index of the leftmost tab currently in the strip. 0 whenever they all fit.</summary>
        /// <remarks>
        /// Per pane, like the sessions themselves: the two strips are different widths and hold
        /// different numbers of tabs, so one shared index would have each pane scrolling the other.
        /// </remarks>
        private int _tabWindow;

        /// <summary>
        /// Decide which of this pane's tabs are in the strip at its current width, and show or hide
        /// the chevron. Called from RebuildTabStrip, before anything reads which tab is on an edge.
        /// </summary>
        /// <remarks>
        /// The window is a contiguous RUN, not a set: tabs keep their order and their neighbours, so a
        /// strip that has moved still reads like the tab bar it was. It shifts the least it can to
        /// keep the active tab on screen, which is the one invariant that matters - a tab you just
        /// switched to and cannot see is worse than no strip at all.
        /// </remarks>
        private void ApplyTabWindow()
        {
            int n = _sessions.Count;
            if (n == 0) { TabOverflowBtn.Visibility = Visibility.Collapsed; return; }

            // ActualWidth is 0 until the band has been measured once - on the first pass, and on any
            // pass that runs while the pane is hidden. Falling back to the pane's own width keeps the
            // answer sane instead of capping the strip at one tab and having to be undone by the
            // SizeChanged that follows.
            double avail = TabStripBorder.ActualWidth > 0 ? TabStripBorder.ActualWidth : ActualWidth;

            // Two passes, because the chevron's width changes the answer that decides whether there is
            // a chevron. Asked without it first: if everything fits there is none, and the whole band
            // belongs to the strip.
            int cap = (int)(avail / TabFloorWidth);
            bool overflow = cap < n;
            if (overflow)
            {
                cap = Math.Max(1, (int)((avail - TabChevronWidth) / TabFloorWidth));
                if (cap >= n) overflow = false;
            }

            TabOverflowBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

            int start = 0;
            if (overflow)
            {
                // Clamped before the active tab is considered, so a window left pointing past the end
                // by a close does not survive as a scroll nobody asked for.
                start = Math.Max(0, Math.Min(_tabWindow, n - cap));

                int active = _active == null ? -1 : _sessions.IndexOf(_active);
                if      (active >= 0 && active < start)           start = active;
                else if (active >= 0 && active > start + cap - 1) start = active - cap + 1;

                _tabWindow = start;
            }
            else
            {
                _tabWindow = 0;
                cap = n;
            }

            for (int i = 0; i < n; i++)
                _sessions[i].IsStripVisible = i >= start && i < start + cap;
        }

        /// <summary>The band was resized, so the strip may hold a different number of tabs.</summary>
        /// <remarks>
        /// Goes through RebuildTabStrip rather than calling ApplyTabWindow alone: a different set of
        /// visible tabs is a different first and last tab, and those are the card's corner rounding
        /// and the focus ring's outer verticals as much as they are the strip.
        /// </remarks>
        private void TabBarResized()
        {
            if (_sessions.Count == 0 || _inTabResize) return;

            // Reentrancy guard, not an optimization. RebuildTabStrip writes CardRow.Margin and flips
            // the band's own Visibility, either of which can raise SizeChanged again from inside this
            // call - and a layout loop in WPF is not a slow app, it is a hung one. The pass that
            // follows would compute the same answer anyway.
            _inTabResize = true;
            try     { RebuildTabStrip(); }
            finally { _inTabResize = false; }
        }

        private bool _inTabResize;

        private void TabStripBorder_SizeChanged(object sender, SizeChangedEventArgs e) => TabBarResized();

        /// <summary>The chevron: every tab in this pane, hidden ones included, in strip order.</summary>
        /// <remarks>
        /// EVERY tab, not only the overflowed ones. A list that shows just what is off screen makes
        /// you work out which those are before you can use it, and the visible ones cost nothing to
        /// include. Built on each open rather than kept: titles change on every save and load.
        /// </remarks>
        private void TabOverflow_Click(object sender, RoutedEventArgs e)
        {
            var menu = MakeThemedMenu();
            foreach (var t in _sessions)
            {
                var sess = t;
                // Doubled, because a lone underscore in a MenuItem header is an access-key marker:
                // "Q3_Report" would draw as "Q3Report" with an R underlined, and file names carry
                // underscores all the time.
                var item = MakeMenuItem(sess.TabLabel.Replace("_", "__"), (_, _) => SwitchToTab(sess));
                // Bold rather than a check mark: the menu has no icon column to put one in.
                if (sess.IsActive) item.FontWeight = FontWeights.Bold;
                menu.Items.Add(item);
            }
            menu.PlacementTarget = TabOverflowBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  CARD CORNERS + FOCUS RING
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Square off the card's top corners under a flush active tab.
        /// </summary>
        /// <remarks>
        /// Each top corner squares only when the tab sitting on it is the ACTIVE one: the first tab
        /// owns the top-left, the last owns the top-right. That tab's outer edge is flat and flush
        /// with the card's, so a curve underneath cuts a notch out from under a square tab. An
        /// inactive tab is window-colored and so a different surface anyway, and the card keeps its
        /// rounding under it; no strip at all and the card takes its full radius back.
        ///
        /// Read off the MODEL, never re-measured from the visual tree. Skipped in full screen, where
        /// ApplyFullScreen owns the radius and squares all four.
        /// </remarks>
        private void SyncPaneLeadingCorner()
        {
            // PaneBorder / PaneShadow, not DocPaneBorder / DocPaneShadow: those names are the
            // window's forwards to the FOCUSED pane, and this runs for whichever pane's strip
            // changed. Using them here squares pane A's corners when pane B's tabs move.
            if (PaneBorder == null || _fullScreen) return;
            double r = TryFindResource("RadCard") is CornerRadius rc ? rc.TopLeft : 6;

            bool strip = TabStripBorder != null && TabStripBorder.Visibility == Visibility.Visible;
            bool firstActive = strip && _active?.IsFirst == true;
            bool lastActive  = strip && _active?.IsLast  == true;

            var cr = new CornerRadius(firstActive ? 0 : r, lastActive ? 0 : r, r, r);
            PaneBorder.CornerRadius = cr;
            if (PaneShadow != null) PaneShadow.CornerRadius = cr;
            // Keep the ring's top radii in step with the card's, so its curved sides land exactly on
            // the card's own left/right border rather than beside them.
            if (TabBarRing != null)
            {
                TabBarRing.CornerRadius = new CornerRadius(cr.TopLeft, cr.TopRight, 0, 0);
                // Sides ONLY where the card's corner is square. There the ring has to carry the border
                // up the flush tab edge. Against a rounded corner the card already draws that curve
                // itself, and the ring's own end stands 7px above it as a stray accent stub rising out
                // of the card.
                TabBarRing.BorderThickness = new Thickness(firstActive ? 1 : 0, 1, lastActive ? 1 : 0, 0);
            }
        }

        /// <summary>
        /// Mark this pane's focus state on its tabs and draw the ring's outer verticals.
        /// </summary>
        /// <remarks>
        /// The ring has to continue UP and AROUND the active tab, or it stops dead at the band and the
        /// tab and card read as two surfaces. The tab's own share of that is a template trigger on
        /// PaneFocused; all this does is set the flag.
        ///
        /// PaneDimmed is the other half - the active tab of the pane that does NOT have focus drops
        /// its lip to the card's border color, because two lips at full accent both claim to be the
        /// live pane. Deliberately NOT !PaneFocused: with one pane open both are false and that pane's
        /// lip stays bright.
        ///
        /// The outermost verticals come from the BAND, not from the tab: a first or last tab's own
        /// outer border sits on the ScrollViewer's clip edge and gets cut, so whether it survived
        /// depended on how the UniformGrid divided a fractional band width. TabEdgeLeft/Right are
        /// anchored to the band's own edges, which are the card's edges, so there is no arithmetic to
        /// land wrong.
        /// </remarks>
        private void UpdatePaneFocusRing()
        {
            bool split = Owner?.IsSplit == true;
            bool lit   = PaneHasFocus && split;

            foreach (var t in _sessions)
            {
                t.PaneFocused = lit && t.IsActive;
                t.PaneDimmed  = split && !lit && t.IsActive;
            }

            // Same ownership rule the card's corner rounding uses, read off the tab rather than
            // recomputed: with the strip windowed the tab on an edge is not the one at the end of the
            // list, and two places working that out separately is two places to get it wrong.
            bool firstActive = _active?.IsFirst == true;
            bool lastActive  = _active?.IsLast  == true;
            // The XAML declares these with NO Background - unlike KillerShell's copy, which paints
            // them PrimaryBrush directly in markup, KillerPDF's accent key is only known at runtime
            // (SelectionAccent, resolved the same way SetFocusHalo resolves the card border). Without
            // this they toggle Visible and still draw nothing: a transparent Border is invisible
            // whatever its Visibility says.
            if (TabEdgeLeft != null)
            {
                TabEdgeLeft.Visibility = lit && firstActive ? Visibility.Visible : Visibility.Collapsed;
                TabEdgeLeft.SetResourceReference(Border.BackgroundProperty, "SelectionAccent");
            }
            if (TabEdgeRight != null)
            {
                TabEdgeRight.Visibility = lit && lastActive ? Visibility.Visible : Visibility.Collapsed;
                TabEdgeRight.SetResourceReference(Border.BackgroundProperty, "SelectionAccent");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  TAB GESTURES
        // ════════════════════════════════════════════════════════════════════════════════════════
        // Left-click switches on mouse-UP, so a press can begin a drag without switching first.

        private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not DocumentSession s) return;
            if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; CloseTab(s); }
        }

        private void Tab_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not DocumentSession s) return;
            var menu = MakeThemedMenu();
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CloseTab"), (_, _) => CloseTab(s), "Ctrl+W"));
            var others = MakeMenuItem(Loc("Str_Ctx_CloseOthers"), (_, _) => CloseOtherTabs(s), "Ctrl+Shift+W");
            others.IsEnabled = _sessions.Count(z => z.Doc != null || z.DeferredPath != null) > 1;
            menu.Items.Add(others);
            menu.PlacementTarget = fe;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is DocumentSession s) CloseTab(s);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  DRAG: reorder within this pane, or hand the tab to the other one
        // ════════════════════════════════════════════════════════════════════════════════════════
        // Arm on press; past the threshold the grabbed tab glues to the cursor and its neighbours
        // glide aside as it crosses their layout-slot midpoints. A plain click still switches on
        // release.
        //
        // Over the OTHER pane the real tab cannot follow the hand - it is still parked in the strip it
        // came from - so a ghost takes over and the reorder stands down (Shell/PaneDrag.cs). Coming
        // back into this pane hands control straight back.

        private DocumentSession? _tabDragSession;
        private Point  _tabDragStart;
        private double _tabGrabDX;
        private bool   _tabDragging;

        /// <summary>Cursor offset inside the grabbed tab, so the window's ghost can sit exactly where
        /// the tab did when it was picked up.</summary>
        internal double TabGrabOffsetX => _tabGrabDX;

        private FrameworkElement? TabContainer(DocumentSession s)
            => TabStrip?.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement;

        /// <summary>Did the press land on a button (the close x) rather than on the tab itself?</summary>
        private static bool InsideButton(object src)
        {
            var d = src as System.Windows.DependencyObject;
            while (d != null && d is not Button && d is not Window)
                d = VisualTreeHelper.GetParent(d);
            return d is Button;
        }

        /// <summary>Midpoint X of a tab's LAYOUT slot (ignores any in-flight slide transform).</summary>
        private static double LayoutMidX(FrameworkElement fe)
        {
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
            return slot.X + slot.Width / 2;
        }

        /// <summary>Set a tab's horizontal offset immediately - glues the grabbed tab to the cursor.</summary>
        private static void SetTabOffsetX(FrameworkElement tab, double x)
        {
            if (tab.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.XProperty, null);   // drop any prior animation so the set sticks
            tt.X = x;
        }

        /// <summary>Glide a just-reordered neighbour from where it was into its new slot, so a swap
        /// reads as a movement instead of an instant jump.</summary>
        private static void AnimateTabSlide(FrameworkElement? tab, double fromX)
        {
            if (tab == null) return;
            if (tab.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            var anim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0,
                new Duration(TimeSpan.FromMilliseconds(140)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            tt.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        internal void CleanupTabTransforms()
        {
            foreach (var s in _sessions)
                if (TabContainer(s) is { } c)
                {
                    c.RenderTransform = null;
                    Panel.SetZIndex(c, 0);
                }
        }

        private void Tab_DragDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement bd || bd.DataContext is not DocumentSession s) return;
            if (InsideButton(e.OriginalSource)) return;   // the close x handles its own click
            _tabDragSession = s;
            _tabDragStart   = e.GetPosition(TabStrip);
            _tabGrabDX      = e.GetPosition(bd).X;
            _tabDragging    = false;
            bd.CaptureMouse();
            // Own the press entirely so it cannot bubble to the title bar's window-drag handler, and
            // so the mouse capture rather than the caption hit-test drives the drag - which is what
            // makes it Y-independent.
            e.Handled = true;
        }

        private void Tab_DragMove(object sender, MouseEventArgs e)
        {
            if (sender is not FrameworkElement bd || !bd.IsMouseCaptured || _tabDragSession is null) return;
            var cont = TabContainer(_tabDragSession);
            if (cont == null) return;

            double x = e.GetPosition(TabStrip).X;
            if (!_tabDragging && Math.Abs(x - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            _tabDragging = true;
            Panel.SetZIndex(cont, 3);   // the grabbed tab rides above its neighbours

            var over = Owner?.TabDropTargetPane(this, e);
            Owner?.UpdateTabDragFeedback(this, _tabDragSession, e, over);
            if (over != null) return;   // the ghost has it; no reorder while the pointer is away

            int cur = _sessions.IndexOf(_tabDragSession);
            double slide   = cont.ActualWidth;
            double rawLeft = x - _tabGrabDX;
            double leftEdge  = rawLeft;
            double rightEdge = rawLeft + cont.ActualWidth;
            double maxLeft = Math.Max(0, TabStrip.ActualWidth - slide);
            double renderLeft = Math.Min(Math.Max(0, rawLeft), maxLeft);

            // Swap when the ADVANCING edge crosses a neighbour's layout-slot midpoint. Edge against
            // midpoint gives natural hysteresis, so a tab parked on a boundary does not bounce.
            bool swapped = false;
            if (cur + 1 < _sessions.Count && TabContainer(_sessions[cur + 1]) is { } right && rightEdge > LayoutMidX(right))
            {
                _sessions.Move(cur + 1, cur);
                AnimateTabSlide(TabContainer(_sessions[cur]), slide);    // it jumped left; glide it in from the right
                swapped = true;
            }
            else if (cur - 1 >= 0 && TabContainer(_sessions[cur - 1]) is { } left && leftEdge < LayoutMidX(left))
            {
                _sessions.Move(cur - 1, cur);
                AnimateTabSlide(TabContainer(_sessions[cur]), -slide);   // it jumped right; glide it in from the left
                swapped = true;
            }

            // After a swap the grabbed tab's slot has moved by a neighbour's width; refresh layout so
            // the new slot is current, then offset it back under the cursor.
            if (swapped) TabStrip.UpdateLayout();
            var dragged = TabContainer(_tabDragSession);
            if (dragged == null) return;
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(dragged);
            SetTabOffsetX(dragged, renderLeft - slot.X);
        }

        private void Tab_DragUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement bd || !bd.IsMouseCaptured) return;
            bd.ReleaseMouseCapture();
            bool wasDragging = _tabDragging;
            var  s = _tabDragSession;
            _tabDragSession = null;
            _tabDragging    = false;
            Owner?.HideTabDragFeedback();   // the ghost goes whatever the drop turns out to be

            if (!wasDragging)
            {
                if (s != null) SwitchToTab(s);
                return;
            }

            // Dropped over the OTHER pane? Then this was a move, not a reorder. Checked on RELEASE
            // rather than mid-drag on purpose: moving a tab between panes re-creates its container,
            // which would pull the mouse capture out from under the drag that is still running.
            if (s != null && Owner?.TabDropTargetPane(this, e) is { } target)
            {
                Owner.MoveTabToPane(this, target, s, e);
                return;
            }

            RebuildTabStrip();   // a reorder may have moved the active tab on or off an edge

            // Settle the grabbed tab from its dragged offset into its final slot.
            var cont = s != null ? TabContainer(s) : null;
            if (cont?.RenderTransform is TranslateTransform tt && Math.Abs(tt.X) > 0.5)
            {
                var settle = new System.Windows.Media.Animation.DoubleAnimation(0,
                    new Duration(TimeSpan.FromMilliseconds(120)))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                };
                settle.Completed += (_, _) => CleanupTabTransforms();
                tt.BeginAnimation(TranslateTransform.XProperty, settle);
            }
            else CleanupTabTransforms();
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  CROSS-PANE MOVE (the window drives this - Shell/PaneDrag.cs)
        // ════════════════════════════════════════════════════════════════════════════════════════
        // A session is already self-contained - it owns its document, annotations, undo stack, render
        // cache and view state - so a move between panes is a move: out of one collection, into the
        // other. Nothing is reloaded, which is what lets a large document cross without a re-render.

        /// <summary>This pane's band, for the window's drop hit-test and caret math.</summary>
        internal Border TabBandCtl => TabStripBorder;

        /// <summary>This pane's strip, for the window's caret math.</summary>
        internal ItemsControl TabStripCtl => TabStrip;

        /// <summary>How many tabs this pane holds. The caret divides the band by this.</summary>
        internal int TabCount => _sessions.Count;

        /// <summary>Take <paramref name="s"/> out of this pane and pick whatever should be active in
        /// its place. Pure bookkeeping - the caller re-renders, because which pane's fields are live
        /// at that moment is its decision, not this one's.</summary>
        internal void DetachSessionExt(DocumentSession s)
        {
            int idx = _sessions.IndexOf(s);
            if (idx < 0) return;

            _sessions.Remove(s);
            // Its bitmaps travel with it: leaving the session in this pane's LRU would have this pane
            // clearing a cache the other pane is now serving from.
            _renderLru.Remove(s);

            if (ReferenceEquals(_active, s))
                SetActiveSession(_sessions.Count > 0 ? _sessions[Math.Min(idx, _sessions.Count - 1)] : null);

            RebuildTabStrip();
        }

        /// <summary>Put <paramref name="s"/> into this pane at <paramref name="index"/> and make it
        /// the front tab - the tab you just dragged is the one you are looking at.</summary>
        internal void AdoptSessionExt(DocumentSession s, int index)
        {
            _sessions.Insert(Math.Min(Math.Max(0, index), _sessions.Count), s);
            SetActiveSession(s);
            RebuildTabStrip();
        }
    }
}
