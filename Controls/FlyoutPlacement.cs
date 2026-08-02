using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerPDF
{
    /// <summary>
    /// Where every flyout opens: the BOTTOM-LEFT CORNER OF THE CONTENT PANE.
    /// (From KillerUI/Shell/FlyoutPlacement.cs - the family flyout standard.)
    ///
    /// That corner is the answer because of what bounds it, and all three matter:
    ///   - it is INSIDE the window, so a flyout never hangs over the desktop;
    ///   - it is ABOVE the footer, so the status bar is never covered;
    ///   - it is clear of the icon rail, so the rail buttons are never covered.
    /// The content pane is the one element bounded by all three at once, so flyouts are positioned
    /// against IT - not against the button, and not by any built-in placement mode.
    ///
    /// WHY NOT PlacementMode.Right / Top / etc: a Popup is its own top-level window, and WPF's
    /// built-in modes only ever avoid the SCREEN edge. They do not know the app window exists, let
    /// alone the footer or the rail. "Right of the button" opened flyouts over the desktop when the
    /// rail sat near the window's right edge; "Top" opened them over the status bar. Hours went
    /// into re-tuning offsets before it was clear no built-in mode can express the requirement.
    /// (Steve, 2026-07-30: "DONT OBSCURE ICONS, DONT OBSCURE THE STATUSBAR, PUT IT AGAINST THE
    /// CORNER OF THE CONTENT PANE.")
    ///
    /// WIRING (once, before the flyouts open):
    ///     FlyoutPlacement.UsePane(pane);               // the element the document content sits on
    /// then, each time a flyout opens:
    ///     FlyoutPlacement.Attach(themeMenu, themeButton);
    ///     themeMenu.IsOpen = true;
    ///
    /// The flyout's own card carries a 6px margin for its drop shadow (FlyoutCard in
    /// MainWindow.xaml), so pinning flush to the corner leaves the VISIBLE card sitting neatly just
    /// inside it. Do not add an inset here.
    /// </summary>
    internal static class FlyoutPlacement
    {
        /// <summary>The content pane. Set once; every flyout positions against it.</summary>
        private static FrameworkElement? _pane;

        internal static void UsePane(FrameworkElement pane) => _pane = pane;

        internal static void Attach(Popup popup, UIElement _)
        {
            popup.PlacementTarget = _pane;
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => BottomLeftOfPane(popupSize, targetSize);
        }

        internal static void Attach(ContextMenu menu, UIElement _)
        {
            menu.PlacementTarget = _pane;
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => BottomLeftOfPane(popupSize, targetSize);
        }

        /// <summary>
        /// Coordinates are relative to the placement target's top-left - the pane's top-left. So
        /// x = 0 is the pane's left edge (clear of the rail) and y = pane height - flyout height
        /// puts the flyout's bottom on the pane's bottom (clear of the footer).
        /// </summary>
        private static CustomPopupPlacement[] BottomLeftOfPane(Size popupSize, Size targetSize)
        {
            double y = targetSize.Height - popupSize.Height;

            // A flyout taller than the pane would otherwise start above it and run over the
            // toolbar; pin it to the pane's top instead and let it use the height it has.
            if (y < 0) y = 0;

            return new[] { new CustomPopupPlacement(new Point(0, y), PopupPrimaryAxis.None) };
        }
    }
}
