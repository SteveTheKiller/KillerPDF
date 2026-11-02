using System;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KillerPDF.Shell
{
    // Configurable sidebar placement: left, right, top or bottom. MainContentGrid has three
    // columns (a sized sidebar column, an empty zero column, a star document column) and two rows.
    // A left or right sidebar swaps which outer column is the sized one; a top or bottom sidebar
    // spans the columns and swaps which row is. Either way _sidebarCol is repointed at the sized
    // track, so the collapse / resize logic keeps working unchanged.
    public partial class MainWindow
    {
        private void ApplySidebarSide()
        {
            // ContentPane, NOT Viewer. Since the split landed, the element that sits in
            // MainContentGrid is the SPLIT HOST - Viewer is one of its children. Setting
            // Grid.Column on Viewer therefore moved pane A into ContentPane's column 2, which is
            // PaneBCol and is ZERO WIDTH while unsplit, so the document rendered into a pane with
            // no width and the whole content area went blank. Same for the margin below: the 8px
            // gutter to the window edge belongs to the host, not to one pane.
            //
            // Do NOT look up TabStripBorder / TabScroll here: those elements live inside PdfViewer,
            // so FindName cannot see them (a UserControl is its own namescope and FindName returns
            // null SILENTLY). They would only re-span the band across the splitter column, which is
            // meaningless when each strip lives inside its own pane and spans it exactly.
            if (FindName("SidebarCol") is not ColumnDefinition sidebarColDef ||
                FindName("DocCol") is not ColumnDefinition docColDef ||
                FindName("SidebarOuterGrid") is not Grid sbOuter ||
                FindName("SidebarBorder") is not Border sbContent ||
                FindName("SidebarToggleStrip") is not Border sbToggle ||
                ContentPane is not FrameworkElement docPane ||
                FindName("SbContentCol") is not ColumnDefinition sbContentCol ||
                FindName("SbToggleCol") is not ColumnDefinition sbToggleCol ||
                FindName("SbContentRow") is not RowDefinition sbContentRow ||
                FindName("SbToggleRow") is not RowDefinition sbToggleRow)
                return;
            // Every rail flyout, the kit's theme and language menus included, opens on the rail's
            // side from the first open of the session (Shell/FlyoutPlacement.cs wiring).
            FlyoutPlacement.UsePane(ContentPane, _sidebarSide);
            RowDefinition firstRow = MainContentGrid.RowDefinitions[0];
            RowDefinition secondRow = MainContentGrid.RowDefinitions[1];

            // Carry the sized track's current length across a flip (24px when collapsed, else the
            // user's size). A star length means it isn't the sized track yet, so fall back.
            GridLength sized = _sidebarCollapsed
                ? new GridLength(SbPx(SidebarRailSize))
                : (_sidebarCol != null && _sidebarCol.Width.GridUnitType == GridUnitType.Pixel)
                    ? _sidebarCol.Width
                    : new GridLength(SbPx(180));
            double maxW = SbPx(SidebarMaxFor(_sidebarPanel));
            double minW = SbPx(_sidebarCollapsed ? SidebarRailSize : SidebarMinOpen);
            // A glide still running belongs to the track being given up; let it go so it cannot
            // keep writing a length into a track that is about to be the document's.
            _sidebarCol?.BeginAnimation(null);

            // Every track starts neutral; the side below sizes the one it uses.
            foreach (ColumnDefinition col in new[] { sidebarColDef, docColDef })
            { col.MinWidth = 0; col.MaxWidth = double.PositiveInfinity; }
            foreach (RowDefinition row in new[] { firstRow, secondRow })
            { row.MinHeight = 0; row.MaxHeight = double.PositiveInfinity; }
            sidebarColDef.Width = new GridLength(1, GridUnitType.Star);
            docColDef.Width = new GridLength(1, GridUnitType.Star);
            firstRow.Height = GridLength.Auto;
            secondRow.Height = new GridLength(1, GridUnitType.Star);

            bool strip = _sidebarSide.IsStrip();
            switch (_sidebarSide)
            {
                case SidebarSide.Left:
                    // Sidebar in column 0; document fills column 2.
                    sidebarColDef.MinWidth = minW; sidebarColDef.MaxWidth = maxW; sidebarColDef.Width = sized;
                    _sidebarCol = new SidebarTrack(sidebarColDef);
                    break;
                case SidebarSide.Right:
                    // Sidebar in column 2; document fills column 0.
                    docColDef.MinWidth = minW; docColDef.MaxWidth = maxW; docColDef.Width = sized;
                    _sidebarCol = new SidebarTrack(docColDef);
                    break;
                case SidebarSide.Top:
                    // Both span the columns; the sidebar takes row 0 and the document row 1. Column 0
                    // goes to zero so the star document column is the whole width.
                    sidebarColDef.Width = new GridLength(0);
                    firstRow.MinHeight = minW; firstRow.MaxHeight = maxW; firstRow.Height = sized;
                    _sidebarCol = new SidebarTrack(firstRow);
                    break;
                default:
                    // Bottom: the document takes row 0 and the sidebar row 1.
                    sidebarColDef.Width = new GridLength(0);
                    firstRow.Height = new GridLength(1, GridUnitType.Star);
                    secondRow.MinHeight = minW; secondRow.MaxHeight = maxW; secondRow.Height = sized;
                    _sidebarCol = new SidebarTrack(secondRow);
                    break;
            }
            Grid.SetColumn(sbOuter, _sidebarSide == SidebarSide.Right ? 2 : 0);
            Grid.SetColumnSpan(sbOuter, strip ? 3 : 1);
            Grid.SetRow(sbOuter, _sidebarSide == SidebarSide.Bottom ? 1 : 0);
            Grid.SetRowSpan(sbOuter, strip ? 1 : 2);
            Grid.SetColumn(docPane, _sidebarSide == SidebarSide.Left ? 2 : 0);
            Grid.SetColumnSpan(docPane, strip ? 3 : 1);
            Grid.SetRow(docPane, _sidebarSide == SidebarSide.Top ? 1 : 0);
            Grid.SetRowSpan(docPane, strip ? 1 : 2);

            // The toggle strip faces the document. The inner column and row defs are fixed in
            // position, so they are sized by position, not by name: the first def holds whichever
            // part comes first.
            bool railFirst = _sidebarSide.IsTrailing();
            // A strip's rail also carries the panel's header and a larger chevron, so it is a little
            // taller than a column's rail is wide.
            GridLength rail = new(strip ? StripRailHeight : 24, GridUnitType.Pixel), fill = new(1, GridUnitType.Star);
            if (strip)
            {
                sbContentCol.Width = fill;
                sbToggleCol.Width = new GridLength(0);
                sbContentRow.Height = railFirst ? rail : fill;
                sbToggleRow.Height = railFirst ? fill : rail;
            }
            else
            {
                sbContentCol.Width = railFirst ? rail : fill;
                sbToggleCol.Width = railFirst ? fill : rail;
                sbContentRow.Height = fill;
                sbToggleRow.Height = new GridLength(0);
            }
            Grid.SetColumn(sbContent, !strip && railFirst ? 1 : 0);
            Grid.SetColumn(sbToggle, !strip && !railFirst ? 1 : 0);
            Grid.SetRow(sbContent, strip && railFirst ? 1 : 0);
            Grid.SetRow(sbToggle, strip && !railFirst ? 1 : 0);
            ApplySidebarOrientation(strip);
            ApplyRailIconsEnd();
            Dock? railEdge = _sidebarSide == SidebarSide.Top ? Dock.Top
                : _sidebarSide == SidebarSide.Bottom ? Dock.Bottom : null;
            Viewer.SetRailShadowEdge(railEdge);
            ViewerB.SetRailShadowEdge(railEdge);

            // The splitter's edge line follows its alignment at the content/rail boundary.
            // The old SidebarShadow gradient is gone; DocPaneBorder casts a real PaneShadow
            // on all four sides.

            // The DocTopAccent / DocBottomAccent repositioning was here. Those two 1px rules are
            // gone with the squared layout - the card carries its own border on all four sides now,
            // so there is nothing left to bridge to the toolbar and footer. (2026-07-31.)

            // The document card's 8px inset always sits on its OUTER edge - the window side, away
            // from the splitter - so the gap reads as a margin off the window rather than a gutter
            // between the pane and the sidebar. Full screen clears the margin, so leave it alone
            // there; ApplyFullScreen restores it from this same helper on exit.
            // The grip rides the CONTENT part and faces the rail, so it always sits on the list's
            // inner lip: right edge with the sidebar on the left, left edge with it on the right,
            // bottom edge at the top and top edge at the bottom.
            if (FindName("SidebarSplitter") is Thumb grip)
            {
                Grid.SetColumn(grip, Grid.GetColumn(sbContent));
                Grid.SetRow(grip, Grid.GetRow(sbContent));
                grip.Width = strip ? double.NaN : 5;
                grip.Height = strip ? 5 : double.NaN;
                grip.Cursor = strip ? System.Windows.Input.Cursors.SizeNS : System.Windows.Input.Cursors.SizeWE;
                grip.HorizontalAlignment = _sidebarSide switch
                {
                    SidebarSide.Left => HorizontalAlignment.Right,
                    SidebarSide.Right => HorizontalAlignment.Left,
                    _ => HorizontalAlignment.Stretch,
                };
                grip.VerticalAlignment = _sidebarSide switch
                {
                    SidebarSide.Top => VerticalAlignment.Bottom,
                    SidebarSide.Bottom => VerticalAlignment.Top,
                    _ => VerticalAlignment.Stretch,
                };
            }

            // The shadow caster is inside the PdfViewer control, so moving the control moves both -
            // one less thing that can drift out of alignment than a separate grid child that has to
            // be moved in step with the pane.
            if (!_fullScreen)
            {
                // docPane is ContentPane, so this one margin now insets BOTH panes from the window
                // edge together, which is what the 8px gutter always meant.
                docPane.Margin = DocPaneInsetMargin();
                // No separate tab-band margins here. Each strip is inside its own pane, so the band
                // and the card share an edge by construction.
            }

            UpdateSidebarToggleGlyph();
            UpdateTabStripFade();
            // The column swap repositions the document pane; re-anchor the footer shadow once layout
            // settles (TransformToVisual needs the final positions).
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)UpdateFooterFade);
        }

        // Document on the right (sidebar left) -> 8px inset on the right; mirrored when the sidebar
        // moves. With the sidebar at the top or bottom both sides are window edges, so both get the
        // inset. Top and bottom stay 0: PaneShadow is Direction 270 and draws outside the element's
        // bounds without taking layout space, so a bottom gap would be real padding lifting the
        // card off the footer, not room for the shadow (KillerShell's ResultsPane comment).
        //
        // The sidebar side is 0, not a pull-back. That 6px column is a plain gap - KillerShell's
        // TreeGapCol - because the grip lives inside the sidebar, so nothing sits there and the
        // grain layer paints it like the rest of the surface.
        // Top is -1, KillerShell's ResultsPane margin verbatim: the card's own top border tucks
        // UNDER the tab band, which is opaque, so the active tab and the pane read as one surface
        // instead of being split by a hairline under the active tab. With no tabs open the band is
        // collapsed and the -1 just eats a pixel against the toolbar. Below a top sidebar the
        // card meets the rail instead, which it must not tuck under, so the top is 0 there.
        private Thickness DocPaneInsetMargin()
        {
            // This is layout geometry, not a palette value. Keep the zero-inset exception scoped
            // to 98SE so it cannot leak into the rounded themes after a live theme switch.
            double inset = Services.ThemeManager.Current == Services.Theme.SE98 ? 0 : 8;
            return _sidebarSide switch
            {
                SidebarSide.Right => new Thickness(inset, -1, 0, 0),
                SidebarSide.Top => new Thickness(inset, 0, inset, 0),
                SidebarSide.Bottom => new Thickness(inset, -1, inset, 0),
                _ => new Thickness(0, -1, inset, 0),
            };
        }

        // The grip drives the sized track directly (see the XAML comment). Dragging toward the
        // document grows the sidebar when it is on the left or top and shrinks it when on the
        // right or bottom, so the delta is signed by side. Clamped to the track's own Min/Max,
        // which the collapse and outline/pages modes already maintain, so this cannot drag past a
        // readable minimum.
        private void SidebarGrip_DragStarted(object sender, DragStartedEventArgs e)
            => OnSidebarSplitterPress();

        private void SidebarGrip_DragCompleted(object sender, DragCompletedEventArgs e)
            => OnSidebarResized();

        private void SidebarGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_sidebarCol == null) return;
            double change = _sidebarSide.IsStrip() ? e.VerticalChange : e.HorizontalChange;
            double w = _sidebarCol.ActualWidth + (_sidebarSide.IsTrailing() ? -change : change);
            double min = _sidebarCol.MinWidth > 0 ? _sidebarCol.MinWidth : SbPx(SidebarRailSize);
            double max = double.IsPositiveInfinity(_sidebarCol.MaxWidth) ? double.MaxValue : _sidebarCol.MaxWidth;
            _sidebarCol.Width = new GridLength(Math.Max(min, Math.Min(max, w)));
            OnSidebarSplitterMove(sender, new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = System.Windows.Input.Mouse.MouseMoveEvent });
        }

        // A Border with a CornerRadius does not clip its child, so the canvas, the grain and the
        // page itself all square the card's corners straight back off. Each corner of the clip is
        // the card's own less its 1px border, which is where the inner edge of the curve actually
        // falls; the card's corners at the tab band differ from the rest (PdfViewer.ClipToCard).
        // internal: PdfViewer's XAML binds this and forwards to it.
        internal void DocPane_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement el) Controls.PdfViewer.ClipToCard(el);
        }


        // Clip the tab-strip shadow gradient to the document column so it never falls over the
        // sidebar (on whichever side the sidebar sits).
        private void UpdateTabStripFade()
        {
            // The tab-strip gradient band spans the splitter column + document column. BOTH ends are
            // feathered. The document-facing end used to keep a hard stop on the reasoning that it
            // was the window edge and should be crisp; with the split it is not the window edge at
            // all, it is a seam in the middle of the window, and it read as a solid vertical line
            // rising out of the pane. A shadow with a visible end is not a shadow.
            if (TabStripFade != null)
            {
                TabStripFade.Margin = new Thickness(0);
                double w = TabStripFade.ActualWidth;
                if (w > 0)
                {
                    double f = Math.Min(0.5, 32.0 / w);   // wider (~32px) feather than the footer - the top
                                                          // shadow is darker, so a 15px fade still read as a
                                                          // hard vertical edge near the sidebar corner
                    var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                    // Same ramp at both ends. The sidebar-facing side keeps the wider feather it
                    // already had; the document-facing side gets a shorter one, enough to kill the
                    // hard line without eating into the strip.
                    double fDoc = Math.Min(0.25, 14.0 / w);
                    bool right = _sidebarSide == SidebarSide.Right;
                    double fNear = right ? fDoc  : f;
                    double fFar  = right ? f     : fDoc;
                    mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                    mask.GradientStops.Add(new GradientStop(Colors.White, fNear));
                    mask.GradientStops.Add(new GradientStop(Colors.White, 1 - fFar));
                    mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                    TabStripFade.OpacityMask = mask;
                }
                else TabStripFade.OpacityMask = null;
            }
            UpdateFooterFade();
        }

        // Portable follows the document area: centred under the pane in single view, centred on
        // the pane divider in split view. When that spot would run into the page-size readout it
        // anchors immediately left of it instead. The status text trims before the badge.
        private double _footerMinimumWidth;
        private bool _footerMinimumWidthQueued;

        private void UpdateFooterFade()
        {
            if (StatusText is null || PortableBadge is null || FooterBorder is null
                || FooterDocumentControls is null || FooterVersionCell is null) return;
            if (Viewer is null || PortableBadge.Parent is not FrameworkElement host || host.ActualWidth <= 0) return;

            double badgeReserve = PortableBadge.Visibility == Visibility.Visible ? PortableBadge.ActualWidth + 16 : 0;
            double fixedWidth = FooterVersionCell.DesiredSize.Width + badgeReserve + 48;
            double margins = FooterDocumentControls.Margin.Left + FooterDocumentControls.Margin.Right;
            double widestTool = Math.Max(PageSizeLabel.DesiredSize.Width, ZoomBox.DesiredSize.Width);
            double minimumTools = widestTool + 48 + margins;
            double minimumWindow = ActualWidth - host.ActualWidth + fixedWidth + minimumTools * _appScale;
            if (Math.Abs(_footerMinimumWidth - minimumWindow) > .5)
            {
                _footerMinimumWidth = minimumWindow;
                if (!_footerMinimumWidthQueued)
                {
                    _footerMinimumWidthQueued = true;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        _footerMinimumWidthQueued = false;
                        SyncSplitMinWidth();
                    }));
                }
            }
            double available = Math.Max(minimumTools, (host.ActualWidth - fixedWidth) / _appScale - margins);
            if (Math.Abs(FooterDocumentControls.MaxWidth - available) > .5)
                FooterDocumentControls.MaxWidth = available;
            UpdateFooterToolsOverflow();

            Grid.SetColumn(PortableBadge, 0);
            Grid.SetColumnSpan(PortableBadge, 4);
            PortableBadge.HorizontalAlignment = HorizontalAlignment.Left;

            double badgeWidth = PortableBadge.ActualWidth;
            if (PortableBadge.Visibility != Visibility.Visible || badgeWidth <= 0)
            {
                PortableBadge.Margin = new Thickness(0);
                StatusText.MaxWidth = double.PositiveInfinity;
                return;
            }

            double X(UIElement e, double x) => e.TranslatePoint(new Point(x, 0), host).X;
            bool split = ViewerB is not null && ViewerB.Visibility == Visibility.Visible && ViewerB.ActualWidth > 0;
            double target = split
                ? (X(Viewer, Viewer.ActualWidth) + X(ViewerB!, 0)) / 2   // the divider between the panes
                : X(Viewer, Viewer.ActualWidth / 2);                     // the middle of the pane

            double anchoredLeft = X(FooterDocumentControls, 0) - 8 - badgeWidth;   // just left of the page size
            double left = Math.Min(target - badgeWidth / 2, anchoredLeft);
            double statusLeft = X(StatusText, 0);
            left = Math.Max(left, statusLeft);

            PortableBadge.Margin = new Thickness(left, 0, 0, 0);
            StatusText.MaxWidth = Math.Max(0, left - statusLeft - 16);
        }

        private void FooterAnchor_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFooterFade();

        private void FooterToolsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateFooterFade();

        private void UpdateFooterToolsOverflow()
        {
            if (FooterToolsViewport is null || FooterToolsPanel is null || FooterToolsScroll is null
                || FooterToolsPrevious is null || FooterToolsNext is null || FooterToolsViewport.ActualWidth <= 0) return;
            bool overflow = FooterToolsPanel.DesiredSize.Width > FooterToolsViewport.ActualWidth + .5;
            FooterToolsPrevious.Visibility = FooterToolsNext.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            FooterToolsPrevious.IsEnabled = FooterToolsScroll.HorizontalOffset > .5;
            FooterToolsNext.IsEnabled = FooterToolsScroll.HorizontalOffset < FooterToolsScroll.ScrollableWidth - .5;
            if (!overflow && FooterToolsScroll.HorizontalOffset > 0) FooterToolsScroll.ScrollToLeftEnd();
        }

        private void FooterToolsPrevious_Click(object sender, RoutedEventArgs e) => ScrollFooterTools(-1);
        private void FooterToolsNext_Click(object sender, RoutedEventArgs e) => ScrollFooterTools(1);

        private void ScrollFooterTools(int direction) => FooterToolsScroll.ScrollToHorizontalOffset(
            FooterToolsScroll.HorizontalOffset + direction * Math.Max(24, FooterToolsScroll.ViewportWidth - 24));

        // The collapse arrow points toward where the page-list content goes when toggled: toward
        // the sidebar's own edge while open, away from it while collapsed.
        private void UpdateSidebarToggleGlyph()
        {
            if (_sidebarToggleBtn == null) return;
            // A strip's header rides in the rail, so it hides with the panel it names.
            SidebarHeaderStack.Visibility = _sidebarStripLayout && _sidebarCollapsed
                ? Visibility.Collapsed : Visibility.Visible;
            SidebarSide toward = _sidebarCollapsed
                ? _sidebarSide switch
                {
                    SidebarSide.Left => SidebarSide.Right,
                    SidebarSide.Right => SidebarSide.Left,
                    SidebarSide.Top => SidebarSide.Bottom,
                    _ => SidebarSide.Top,
                }
                : _sidebarSide;
            _sidebarToggleBtn.Content = toward switch
            {
                SidebarSide.Left => "\uE76B",    // ChevronLeft
                SidebarSide.Right => "\uE76C",   // ChevronRight
                SidebarSide.Top => "\uE70E",     // ChevronUp
                _ => "\uE70D",                   // ChevronDown
            };
            SyncRailPanelButtons();   // the open panel's rail button is lit only while the sidebar is open
        }

        private void SelectSidebarSide(SidebarSide side)
        {
            if (side == _sidebarSide) return;   // no change (e.g. picking the side it's already on)
            _sidebarSide = side;
            App.SetSetting(SidebarSides.SettingName, side.ToString());
            ApplySidebarSide();
        }

        // Shift+F9 (pairs with F9, the sidebar collapse toggle): the next side in the menu's order.
        private void ToggleSidebarSide() => SelectSidebarSide(_sidebarSide.Next());

        // ── Top and bottom strips: the rail, the header and the page list turn horizontal ──

        private bool _sidebarStripLayout;             // what the rail and page list are laid out for now
        private Panel? _sidebarHeaderHome;            // where the panel header lives while the rail is a column
        private int _sidebarHeaderHomeIndex;
        private Grid? _stripRailHost;
        private DockPanel? _stripRailContent;
        private ScrollViewer? _stripRailScroll;
        private Button? _stripRailPrevious;
        private Button? _stripRailNext;

        private const double StripRailHeight = 28;

        // The rail's own thickness, which is also the sidebar track's size while collapsed: a top
        // or bottom strip collapses to its taller rail row, so the rail's glyphs keep the same
        // space to the window edge as to the document whether the strip is open or shut.
        private double SidebarRailSize => _sidebarSide.IsStrip() ? StripRailHeight : 24;
        // With the header at the left end, ahead of the icons, it keeps at least this width so the
        // icons stay put when a panel with a shorter name opens.
        private const double StripHeaderMinWidth = 170;
        private DataTemplate? _pageColumnTemplate;    // the page list's own (column) item template
        private ItemsPanelTemplate? _pageColumnPanel;
        private ItemsPanelTemplate? _pageStripPanel;

        /// <summary>
        /// Turns the rail, the panel header and the page list between a column (left or right
        /// sidebar) and a strip (top or bottom). Only acts on a change, because the rail's spacing
        /// is turned by transposing each margin, which a second pass would turn straight back.
        /// </summary>
        private void ApplySidebarOrientation(bool strip)
        {
            if (strip == _sidebarStripLayout) return;
            _sidebarStripLayout = strip;

            LayOutRail(strip);

            // The page list scrolls sideways, its thumbnails sized by the strip's height.
            _pageColumnTemplate ??= PageList.ItemTemplate;
            _pageColumnPanel ??= PageList.ItemsPanel;
            if (_pageStripPanel is null)
            {
                var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
                panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
                _pageStripPanel = new ItemsPanelTemplate(panel);
            }
            PageList.ItemTemplate = strip ? (DataTemplate)FindResource("PageThumbStripTemplate") : _pageColumnTemplate;
            PageList.ItemsPanel = strip ? _pageStripPanel : _pageColumnPanel;
            ScrollViewer.SetVerticalScrollBarVisibility(PageList, strip ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(PageList, strip ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            if (PageListFadeHost.OpacityMask is LinearGradientBrush mask)
                mask.EndPoint = strip ? new Point(1, 0) : new Point(0, 1);

            // The drop marker becomes an upright bar between thumbnails.
            PageDropIndicator.Width = strip ? 3 : double.NaN;
            PageDropIndicator.Height = strip ? double.NaN : 3;
            PageDropIndicator.HorizontalAlignment = strip ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            PageDropIndicator.VerticalAlignment = strip ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            PageDropIndicator.Margin = strip ? new Thickness(0, 4, 0, 14) : new Thickness(8, 0, 20, 0);
            if (PageDropIndicator.RenderTransform is TranslateTransform move) { move.X = 0; move.Y = 0; }

            // The tree panels flow their rows into columns and scroll sideways too.
            ApplyTreeStripLayout(OutlineTree, OutlineScrollViewer, strip);
            if (_sidebarPanelTree is { Parent: ScrollViewer listScroll } listTree)
                ApplyTreeStripLayout(listTree, listScroll, strip);

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)SyncPageListEdgeFades);
        }

        /// <summary>
        /// Lays the rail out for a strip or a column. In a strip it is one row with two ends: the
        /// icons at one, and at the other the chevron with the open panel's header lifted up out of
        /// the panel beside it (so the thumbnails get its height). For a column each part goes
        /// back where it came from. Only called on a change, because the icons' gaps turn by
        /// transposing each margin, which a second pass
        /// would turn straight back.
        /// </summary>
        private void LayOutRail(bool strip)
        {
            SidebarRailStack.Orientation = strip ? Orientation.Horizontal : Orientation.Vertical;
            foreach (FrameworkElement button in SidebarRailStack.Children.OfType<FrameworkElement>())
                button.Margin = SidebarSides.Transpose(button.Margin);
            var titleLine = (FrameworkElement)SidebarHeaderStack.Children[0];

            if (strip)
            {
                _sidebarHeaderHome ??= (Panel)SidebarHeaderStack.Parent;
                _sidebarHeaderHomeIndex = _sidebarHeaderHome.Children.IndexOf(SidebarHeaderStack);
                SidebarRailGrid.Children.Remove(_sidebarToggleBtn);
                SidebarRailGrid.Children.Remove(SidebarRailStack);
                _sidebarHeaderHome.Children.Remove(SidebarHeaderStack);
                DockPanel.SetDock(_sidebarToggleBtn, Dock.Left);
                DockPanel.SetDock(SidebarHeaderStack, Dock.Left);
                SidebarStripRow.Children.Add(_sidebarToggleBtn);
                _stripRailHost ??= CreateStripRailHost();
                _stripRailContent!.Children.Add(SidebarHeaderStack);
                _stripRailContent.Children.Add(SidebarRailStack);
                SidebarStripRow.LastChildFill = true;
                SidebarStripRow.Children.Add(_stripRailHost);
                SidebarStripRow.Visibility = Visibility.Visible;

                _sidebarToggleBtn.VerticalAlignment = VerticalAlignment.Center;
                _sidebarToggleBtn.Width = 30;
                _sidebarToggleBtn.Height = 26;
                _sidebarToggleBtn.FontSize = 16;
                SidebarHeaderStack.Orientation = Orientation.Horizontal;
                SidebarHeaderStack.VerticalAlignment = VerticalAlignment.Center;
                titleLine.Margin = new Thickness(6, 0, 8, 0);
                PageControlsRow.Margin = new Thickness(0, 0, 8, 0);
                PageControlsRow.VerticalAlignment = VerticalAlignment.Center;
                SidebarRailStack.VerticalAlignment = VerticalAlignment.Center;
                SidebarRailStack.HorizontalAlignment = HorizontalAlignment.Right;
                ApplyRailIconsEnd();
            }
            else if (_sidebarHeaderHome is not null)
            {
                _stripRailContent?.Children.Clear();
                SidebarStripRow.Children.Clear();
                SidebarStripRow.Visibility = Visibility.Collapsed;
                SidebarRailGrid.Children.Insert(0, _sidebarToggleBtn);
                SidebarRailGrid.Children.Add(SidebarRailStack);
                DockPanel.SetDock(SidebarHeaderStack, Dock.Top);
                _sidebarHeaderHome.Children.Insert(
                    Math.Min(_sidebarHeaderHomeIndex, _sidebarHeaderHome.Children.Count), SidebarHeaderStack);

                _sidebarToggleBtn.Margin = new Thickness(0, 6, 0, 0);
                _sidebarToggleBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                _sidebarToggleBtn.VerticalAlignment = VerticalAlignment.Top;
                _sidebarToggleBtn.ClearValue(WidthProperty);
                _sidebarToggleBtn.ClearValue(HeightProperty);
                _sidebarToggleBtn.ClearValue(FontSizeProperty);
                SidebarHeaderStack.Orientation = Orientation.Vertical;
                SidebarHeaderStack.ClearValue(VerticalAlignmentProperty);
                SidebarHeaderStack.ClearValue(MinWidthProperty);
                SidebarHeaderStack.ClearValue(MaxWidthProperty);
                SidebarHeaderStack.ClearValue(ClipToBoundsProperty);
                SidebarHeaderStack.Visibility = Visibility.Visible;
                titleLine.Margin = new Thickness(10, 6, 8, 0);
                PageControlsRow.Margin = new Thickness(12, 2, 8, 4);
                PageControlsRow.ClearValue(VerticalAlignmentProperty);
                SidebarRailStack.HorizontalAlignment = HorizontalAlignment.Stretch;
                SidebarRailStack.VerticalAlignment = VerticalAlignment.Bottom;
                SidebarRailStack.Margin = new Thickness(0, 0, 0, 6);
                ApplyRailIconsEnd();
            }
        }

        // A column's icons stack up from the bottom, and at a large app size they would run up over
        // the chevron, so they shrink to the room below it. A strip lays them out in a row instead.
        private bool _railFitHooked;

        private void FitRailToColumn()
        {
            if (!_railFitHooked)
            {
                SidebarRailGrid.SizeChanged += (_, _) => FitRailToColumn();
                _railFitHooked = true;
            }
            if (_sidebarStripLayout || _sidebarToggleBtn is null)
            {
                SidebarRailStack.LayoutTransform = Transform.Identity;
                return;
            }
            double natural = SidebarRailStack.Margin.Top + SidebarRailStack.Margin.Bottom
                + SidebarRailStack.Children.OfType<FrameworkElement>()
                    .Where(icon => icon.Visibility != Visibility.Collapsed).Sum(icon => icon.DesiredSize.Height);
            double below = SidebarRailGrid.ActualHeight
                - (_sidebarToggleBtn.Margin.Top + _sidebarToggleBtn.ActualHeight + 4);
            double scale = ScaledDialogFit.RailScale(below, natural);
            SidebarRailStack.LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        }

        // A column keeps the chevron at the top and icons at the bottom. A strip keeps the
        // chevron and header at the left and icons at the right, with the chevron outermost.
        private void ApplyRailIconsEnd()
        {
            if (_sidebarToggleBtn == null) return;
            FitRailToColumn();
            if (!_sidebarStripLayout)
            {
                _sidebarToggleBtn.VerticalAlignment = VerticalAlignment.Top;
                _sidebarToggleBtn.Margin = new Thickness(0, 6, 0, 0);
                SidebarRailStack.VerticalAlignment = VerticalAlignment.Bottom;
                SidebarRailStack.Margin = new Thickness(0, 0, 0, 6);
                return;
            }
            DockPanel.SetDock(_sidebarToggleBtn, Dock.Left);
            DockPanel.SetDock(SidebarHeaderStack, Dock.Left);
            DockPanel.SetDock(SidebarRailStack, Dock.Right);
            _sidebarToggleBtn.Margin = new Thickness(4, 0, 0, 0);
            SidebarHeaderStack.MinWidth = StripHeaderMinWidth;
            SidebarRailStack.Margin = new Thickness(0, 0, 4, 0);
            UpdateStripRailBounds();
        }

        private Grid CreateStripRailHost()
        {
            var host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _stripRailContent = new DockPanel { LastChildFill = false };
            _stripRailScroll = new ScrollViewer
            {
                Content = _stripRailContent,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Right,
                CanContentScroll = false,
                Focusable = false,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
            };
            Grid.SetColumn(_stripRailScroll, 1);
            host.Children.Add(_stripRailScroll);
            _stripRailPrevious = OverflowButton("RailOverflowPrevious", "\uE76B", "Str_Scroll_Left", -1);
            _stripRailNext = OverflowButton("RailOverflowNext", "\uE76C", "Str_Scroll_Right", 1);
            Grid.SetColumn(_stripRailNext, 2);
            host.Children.Add(_stripRailPrevious);
            host.Children.Add(_stripRailNext);
            host.SizeChanged += (_, _) => UpdateStripRailOverflow();
            _stripRailScroll.ScrollChanged += (_, _) => UpdateStripRailOverflow();
            MainContentGrid.SizeChanged += (_, _) => ConstrainStripHeight();
            SidebarOuterGrid.SizeChanged += (_, _) => ConstrainStripHeight();
            return host;
        }

        private Button OverflowButton(string name, string glyph, string label, int direction)
        {
            var button = new Button
            {
                Name = name, Content = glyph,
                Style = (Style)FindResource("RailChevronBtn"),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            button.SetResourceReference(ToolTipProperty, label);
            button.Click += (_, _) =>
            {
                if (_stripRailScroll is not { } scroll) return;
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset
                    + direction * Math.Max(30, scroll.ViewportWidth - 30));
            };
            return button;
        }

        private void UpdateStripRailBounds()
        {
            ConstrainStripHeight();
            UpdateStripRailOverflow();
        }

        private bool _constrainingStripHeight;

        private void ConstrainStripHeight()
        {
            if (_fullScreen || !_sidebarStripLayout || _sidebarCol is null || _constrainingStripHeight
                || MainContentGrid.ActualHeight <= 0) return;
            double rail = SbPx(StripRailHeight);
            double maximum = Math.Max(rail, MainContentGrid.ActualHeight - rail);
            double minimum = Math.Min(SbPx(_sidebarCollapsed ? SidebarRailSize : SidebarMinOpen), maximum);
            _constrainingStripHeight = true;
            try
            {
                // Keep the requested size, but leave a rail's height of document visible.
                // A large accessibility scale cannot force the readable minimum offscreen.
                _sidebarCol.MinWidth = minimum;
                _sidebarCol.MaxWidth = Math.Max(minimum, Math.Min(SbPx(SidebarMaxFor(_sidebarPanel)), maximum));
            }
            finally { _constrainingStripHeight = false; }
        }

        private void UpdateStripRailOverflow()
        {
            if (!_sidebarStripLayout || _stripRailHost is null || _stripRailScroll is null
                || _stripRailContent is null || _stripRailPrevious is null || _stripRailNext is null) return;
            // A full-width content row keeps the header and icons at their established ends.
            // When it no longer fits, the same viewport reveals every original header action
            // and rail button instead of clipping the title's neighboring controls.
            _stripRailContent.MinWidth = Math.Max(0, _stripRailScroll.ViewportWidth);
            double contentWidth = SidebarHeaderStack.DesiredSize.Width + SidebarRailStack.DesiredSize.Width;
            bool overflow = contentWidth > _stripRailHost.ActualWidth + 0.5;
            _stripRailPrevious.Visibility = _stripRailNext.Visibility = overflow
                ? Visibility.Visible : Visibility.Collapsed;
            _stripRailPrevious.IsEnabled = _stripRailScroll.HorizontalOffset > 0.5;
            _stripRailNext.IsEnabled = _stripRailScroll.HorizontalOffset < _stripRailScroll.ScrollableWidth - 0.5;
        }

        private const string RailOrderSetting = "SidebarRailOrder";

        // The family rail order (RailOrder): theme, language and shortcuts hold the rail's edge,
        // and the panel and window buttons can be dragged into any order above them.
        private void InitRailOrder()
        {
            foreach (var (button, _) in RailPanelButtons())
                button.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(IsEnabled))
                {
                    Source = button,
                    Converter = new BooleanToVisibilityConverter()
                });
            RailOrder.Attach(SidebarRailStack,
                new FrameworkElement[] { ThemeButton, LangButton, ShortcutHelpRailBtn },
                new FrameworkElement[]
                {
                    SplitPaneRailBtn, SidebarPagesTab, SidebarOutlinesTab, SidebarCommentsTab, SidebarAttachmentsTab,
                    SidebarLayersTab, SidebarSignaturesTab, DocInfoRailBtn,
                },
                App.GetSetting(RailOrderSetting), value => App.SetSetting(RailOrderSetting, value));
        }

        /// <summary>
        /// Lays a sidebar tree out for a strip or a column. In a strip the tree is tagged so
        /// OutlineTreeStyle flows its rows into columns (ColumnFlowPanel), and its scroller goes
        /// sideways with the height fixed, which is what gives the columns a bottom to wrap at.
        /// </summary>
        private static void ApplyTreeStripLayout(TreeView tree, ScrollViewer scroll, bool strip)
        {
            tree.Tag = strip ? "strip" : null;
            scroll.HorizontalScrollBarVisibility = strip ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled;
            scroll.VerticalScrollBarVisibility = strip ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }

        /// <summary>The wheel on a sidebar tree: down the column, or along a strip's columns.</summary>
        private void ScrollSidebarTree(ScrollViewer scroll, double delta)
        {
            if (_sidebarStripLayout) scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - delta);
            else scroll.ScrollToVerticalOffset(scroll.VerticalOffset - delta);
        }

        // A mouse wheel has no sideways motion to give a strip, so the wheel scrolls it sideways.
        private void PageList_StripWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (!_sidebarStripLayout || FindSidebarDescendant<ScrollViewer>(PageList) is not { } sv) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        // ── Page-list edge fades - KillerShell's transparent-content mask ──
        // Each edge fades only while there is a row past it. PageList has no horizontal scrollbar,
        // so KillerShell's narrow scrollbar-restoration stops are not needed here.

        /// <summary>Called once from the window ctor, beside InitSplitPanes.</summary>
        private void WirePageListEdgeFades()
        {
            // ScrollChanged bubbles, so the ListBox's inner ScrollViewer is reached without
            // having to dig it out of the template first. Loaded and SizeChanged cover the
            // passes where nothing scrolled but the extent moved (reseat, thumbnail load).
            PageList.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => SyncPageListEdgeFades()));
            PageList.SizeChanged += (_, _) => SyncPageListEdgeFades();
            PageList.Loaded      += (_, _) => SyncPageListEdgeFades();
            PageList.PreviewMouseWheel += PageList_StripWheel;
        }

        private const double PageListTopFadePx = 18;
        private const double PageListBottomFadePx = 22;
        private LinearGradientBrush? _pageListEdgeMask;

        private void SyncPageListEdgeFades()
        {
            var sv = FindSidebarDescendant<ScrollViewer>(PageList);
            if (sv == null || PageListFadeHost == null) return;
            if (sv.Template.FindName("PART_ScrollContentPresenter", sv)
                    is not ScrollContentPresenter viewport) return;
            _pageListEdgeMask ??= PageListFadeHost.OpacityMask as LinearGradientBrush;
            PageListFadeHost.OpacityMask = null;
            if (_pageListEdgeMask == null) return;

            // A top or bottom strip scrolls sideways, so its fades run along the left and right
            // ends. Both scrollbars stay outside the masked content viewport.
            bool strip = _sidebarStripLayout;
            double height = strip ? viewport.ActualWidth : viewport.ActualHeight;
            if (height <= 1) return;
            _pageListEdgeMask.MappingMode = BrushMappingMode.Absolute;
            _pageListEdgeMask.EndPoint = strip ? new Point(height, 0) : new Point(0, height);

            // Reveal the actual sidebar underneath; never paint a theme-colored strip over it.
            // EdgeFadeOpacity is zero only in 98SE and one in every other theme.
            double fade = TryFindResource("EdgeFadeOpacity") is double value ? value : 1.0;
            double offset = strip ? sv.HorizontalOffset : sv.VerticalOffset;
            double hidden = strip ? sv.ExtentWidth - sv.ViewportWidth : sv.ExtentHeight - sv.ViewportHeight;
            double top = EdgeFadeRamp(offset, PageListTopFadePx) * fade;
            double bottom = EdgeFadeRamp(hidden - offset, PageListBottomFadePx) * fade;
            viewport.OpacityMask = top > 0 || bottom > 0 ? _pageListEdgeMask : null;

            PageListFadeTopOuter.Color = FadeMaskAlpha(1 - top);
            PageListFadeBottomOuter.Color = FadeMaskAlpha(1 - bottom);
            PageListFadeTopInner.Offset = Math.Min(0.45, PageListTopFadePx / height);
            PageListFadeBottomInner.Offset = Math.Max(0.5, 1 - PageListBottomFadePx / height);
        }

        private static double EdgeFadeRamp(double distance, double depth) =>
            Math.Min(1, Math.Max(0, distance) / depth);

        private static Color FadeMaskAlpha(double opacity) =>
            Color.FromArgb(
                (byte)Math.Round(Math.Min(1, Math.Max(0, opacity)) * 255),
                0, 0, 0);

        private static T? FindSidebarDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T t) return t;
                var deeper = FindSidebarDescendant<T>(c);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }
}
