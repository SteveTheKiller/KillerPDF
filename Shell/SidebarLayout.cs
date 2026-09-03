using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KillerPDF
{
    // Configurable sidebar placement (left or right). The layout uses three columns in
    // MainContentGrid: one sized sidebar column, a 6px splitter, and a star document column.
    // ApplySidebarSide swaps which outer column is the sidebar and repoints _sidebarCol so the
    // existing collapse / resize logic keeps working unchanged.
    public partial class MainWindow
    {
        private void ApplySidebarSide()
        {
            // SplitHost, NOT Viewer. Since the split landed, the element that sits in
            // MainContentGrid is the SPLIT HOST - Viewer is one of its children. Setting
            // Grid.Column on Viewer therefore moved pane A into SplitHost's column 2, which is
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
                SplitHost is not FrameworkElement docPane ||
                FindName("SbContentCol") is not ColumnDefinition sbContentCol ||
                FindName("SbToggleCol") is not ColumnDefinition sbToggleCol)
                return;

            // Carry the sized column's current width across a flip (24px when collapsed, else the
            // user's width). A star length means it isn't the sized column yet, so fall back.
            GridLength sized = _sidebarCollapsed
                ? new GridLength(SbPx(24))
                : (_sidebarCol != null && _sidebarCol.Width.GridUnitType == GridUnitType.Pixel)
                    ? _sidebarCol.Width
                    : new GridLength(SbPx(180));
            double maxW = SbPx(_sidebarShowingOutlines ? SidebarMaxOutlines : SidebarMaxPages);

            if (!_sidebarRight)
            {
                // Sidebar on the LEFT (column 0); document fills column 2.
                sidebarColDef.MinWidth = SbPx(_sidebarCollapsed ? 24 : SidebarMinOpen); sidebarColDef.MaxWidth = maxW; sidebarColDef.Width = sized;
                docColDef.MinWidth = 0; docColDef.MaxWidth = double.PositiveInfinity;
                docColDef.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(sbOuter, 0);
                Grid.SetColumn(docPane, 2);
                _sidebarCol = sidebarColDef;
                // Toggle strip faces the document: right edge of the sidebar.
                sbContentCol.Width = new GridLength(1, GridUnitType.Star);
                sbToggleCol.Width  = new GridLength(24, GridUnitType.Pixel);
                Grid.SetColumn(sbContent, 0);
                Grid.SetColumn(sbToggle, 1);
            }
            else
            {
                // Sidebar on the RIGHT (column 2); document fills column 0.
                docColDef.MinWidth = SbPx(_sidebarCollapsed ? 24 : SidebarMinOpen); docColDef.MaxWidth = maxW; docColDef.Width = sized;
                sidebarColDef.MinWidth = 0; sidebarColDef.MaxWidth = double.PositiveInfinity;
                sidebarColDef.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(sbOuter, 2);
                Grid.SetColumn(docPane, 0);
                _sidebarCol = docColDef;
                // Toggle strip faces the document: left edge of the sidebar (inner column 0). The
                // inner column defs are fixed in position, so size them by position, not by name.
                sbContentCol.Width = new GridLength(24, GridUnitType.Pixel);   // inner col 0 -> toggle
                sbToggleCol.Width  = new GridLength(1, GridUnitType.Star);     // inner col 1 -> content
                Grid.SetColumn(sbToggle, 0);
                Grid.SetColumn(sbContent, 1);
            }

            // The splitter's edge-line and the SidebarShadow gradient were both handled here. The
            // splitter draws a single centered line now, which is symmetric and so needs no side
            // handling, and the fake elevation gradient is gone - DocPaneBorder casts a real
            // PaneShadow on all four sides. (2026-07-31.)

            // The DocTopAccent / DocBottomAccent repositioning was here. Those two 1px rules are
            // gone with the squared layout - the card carries its own border on all four sides now,
            // so there is nothing left to bridge to the toolbar and footer. (2026-07-31.)

            // The document card's 8px inset always sits on its OUTER edge - the window side, away
            // from the splitter - so the gap reads as a margin off the window rather than a gutter
            // between the pane and the sidebar. Full screen clears the margin, so leave it alone
            // there; ApplyFullScreen restores it from this same helper on exit.
            // The grip rides the CONTENT column and faces the rail, so it always sits on the list's
            // inner lip: right edge with the sidebar on the left, left edge with it on the right.
            if (FindName("SidebarSplitter") is Thumb grip)
            {
                Grid.SetColumn(grip, _sidebarRight ? 1 : 0);
                grip.HorizontalAlignment = _sidebarRight ? HorizontalAlignment.Left
                                                         : HorizontalAlignment.Right;
            }

            // The shadow caster is inside the PdfViewer control, so moving the control moves both -
            // one less thing that can drift out of alignment than a separate grid child that has to
            // be moved in step with the pane.
            if (!_fullScreen)
            {
                // docPane is SplitHost, so this one margin now insets BOTH panes from the window
                // edge together, which is what the 8px gutter always meant.
                docPane.Margin = DocPaneInsetMargin();
                // No tab-band or TabBarRing margins here. Those exist only to stop a window-level
                // band overhanging the card's rounded outer corner, where the band runs the full
                // width while the card is inset 8px. Each strip is inside its own pane and spans
                // exactly that pane, so the band and the card share an edge by construction.
            }

            UpdateSidebarToggleGlyph();
            SyncThemeFlyoutSide();
            UpdateTabStripFade();
            // The column swap repositions the document pane; re-anchor the footer shadow once layout
            // settles (TransformToVisual needs the final positions).
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)UpdateFooterFade);
        }

        // Document on the right (sidebar left) -> 8px inset on the right; mirrored when the sidebar
        // moves. Top and bottom stay 0: PaneShadow is Direction 270 and draws outside the element's
        // bounds without taking layout space, so a bottom gap would be real padding lifting the
        // card off the footer, not room for the shadow (KillerShell's ResultsPane comment).
        //
        // The sidebar side is 0, not a pull-back. That 6px column is a plain gap - KillerShell's
        // TreeGapCol - because the grip lives inside the sidebar, so nothing sits there and the
        // grain layer paints it like the rest of the surface.
        // Top is -1, KillerShell's ResultsPane margin verbatim: the card's own top border tucks
        // UNDER the tab band, which is opaque, so the active tab and the pane read as one surface
        // instead of being split by a hairline under the active tab. With no tabs open the band is
        // collapsed and the -1 just eats a pixel against the toolbar.
        private Thickness DocPaneInsetMargin()
        {
            // This is layout geometry, not a palette value. Keep the zero-inset exception scoped
            // to 98SE so it cannot leak into the rounded themes after a live theme switch.
            double inset = Services.ThemeManager.Current == Services.Theme.SE98 ? 0 : 8;
            return _sidebarRight ? new Thickness(inset, -1, 0, 0)
                                 : new Thickness(0, -1, inset, 0);
        }

        // The grip drives SidebarCol's width directly (see the XAML comment). Dragging toward the
        // document grows the sidebar when it is on the left and shrinks it when on the right, so
        // the delta is signed by side. Clamped to the column's own Min/MaxWidth, which the collapse
        // and outline/pages modes already maintain, so this cannot drag past a readable minimum.
        private void SidebarGrip_DragStarted(object sender, DragStartedEventArgs e)
            => OnSidebarSplitterPress();

        private void SidebarGrip_DragCompleted(object sender, DragCompletedEventArgs e)
            => OnSidebarResized();

        private void SidebarGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_sidebarCol == null) return;
            double w = _sidebarCol.ActualWidth + (_sidebarRight ? -e.HorizontalChange : e.HorizontalChange);
            double min = _sidebarCol.MinWidth > 0 ? _sidebarCol.MinWidth : SbPx(24);
            double max = double.IsPositiveInfinity(_sidebarCol.MaxWidth) ? double.MaxValue : _sidebarCol.MaxWidth;
            _sidebarCol.Width = new GridLength(Math.Max(min, Math.Min(max, w)));
            OnSidebarSplitterMove(sender, new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = System.Windows.Input.Mouse.MouseMoveEvent });
        }

        // A Border with a CornerRadius does not clip its child, so the canvas, the grain and the
        // page itself all square the card's corners straight back off. Radius 5 = the card's 6
        // less its 1px border, which is where the inner edge of the curve actually falls.
        // internal: PdfViewer's XAML binds this and forwards to it.
        internal void DocPane_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            double outer = TryFindResource("PanelCornerRadius") is CornerRadius cr ? cr.TopLeft : 6;
            double inner = Math.Max(0, outer - 1);
            el.Clip = new RectangleGeometry(new Rect(0, 0, el.ActualWidth, el.ActualHeight), inner, inner);
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
                    double fNear = _sidebarRight ? fDoc  : f;
                    double fFar  = _sidebarRight ? f     : fDoc;
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

        // Center Portable while there is a clear gap before the right-side controls. At narrower
        // widths it returns to its compact slot immediately before those controls.
        private void UpdateFooterFade()
        {
            if (StatusText is null || PortableBadge is null || FooterBorder is null
                || FooterDocumentControls is null || FooterVersionCell is null) return;

            double footerWidth = FooterBorder.ActualWidth;
            double badgeWidth = PortableBadge.ActualWidth;
            double rightWidth = FooterDocumentControls.ActualWidth * _appScale + FooterVersionCell.ActualWidth;
            bool centerBadge = PortableBadge.Visibility == Visibility.Visible
                && footerWidth > 0 && badgeWidth > 0
                && footerWidth / 2 + badgeWidth / 2 + 16 <= footerWidth - rightWidth;

            Grid.SetColumn(PortableBadge, centerBadge ? 0 : 1);
            Grid.SetColumnSpan(PortableBadge, centerBadge ? 4 : 1);
            PortableBadge.HorizontalAlignment = centerBadge
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Right;
            PortableBadge.Margin = centerBadge
                ? new Thickness(0)
                : new Thickness(6, 0, 8, 0);
            StatusText.MaxWidth = centerBadge
                ? Math.Max(0, footerWidth / 2 - badgeWidth / 2 - 24)
                : double.PositiveInfinity;
        }

        // The collapse arrow points toward where the page-list content goes when toggled, which
        // depends on both the side and the collapsed state.
        private void UpdateSidebarToggleGlyph()
        {
            if (_sidebarToggleBtn == null) return;
            bool pointLeft = _sidebarRight ? _sidebarCollapsed : !_sidebarCollapsed;
            _sidebarToggleBtn.Content = pointLeft ? "" : "";   // ChevronLeft / ChevronRight
        }

        private void SelectSidebarSide(bool right)
        {
            if (right == _sidebarRight) return;   // no change (e.g. picking the side it's already on)
            _sidebarRight = right;
            App.SetSetting("SidebarSide", right ? "Right" : "Left");
            ApplySidebarSide();
        }

        // Shift+F9 (pairs with F9, the sidebar collapse toggle).
        private void ToggleSidebarSide() => SelectSidebarSide(!_sidebarRight);

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
        }

        private const double PageListTopFadePx = 18;
        private const double PageListBottomFadePx = 22;

        private void SyncPageListEdgeFades()
        {
            var sv = FindSidebarDescendant<ScrollViewer>(PageList);
            if (sv == null || PageListFadeHost == null) return;

            double height = PageListFadeHost.ActualHeight;
            if (height <= 1) return;

            // Reveal the actual sidebar underneath; never paint a theme-colored strip over it.
            // EdgeFadeOpacity is zero only in 98SE and one in every other theme.
            double fade = TryFindResource("EdgeFadeOpacity") is double value ? value : 1.0;
            double top = EdgeFadeRamp(sv.VerticalOffset, PageListTopFadePx) * fade;
            double bottom = EdgeFadeRamp(
                sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset,
                PageListBottomFadePx) * fade;

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
