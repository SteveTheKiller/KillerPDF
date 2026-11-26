using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerPDF.Shell;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Where every flyout opens: in the content pane, against the edge beside the icon rail, at
    /// the height of the rail button that opened it.
    ///
    /// The bottom buttons' menus sit in the pane's corner, eight pixels in from the rail and from
    /// the pane's foot. A menu opened from a button higher up rises with it: its visible bottom
    /// edge lines up with the button's bottom, so a one-item menu sits beside its button instead
    /// of stranded down in the corner. It never drops below the corner and never rises past the
    /// pane's top, except a menu taller than the pane: that one keeps its foot in the corner and
    /// rises as high as it needs, bounded only by the window. With the sidebar on the right the same rule mirrors; on
    /// the top or bottom the menu takes the pane's near right corner, because the strip's rail
    /// buttons sit at its right end (2026-11-25).
    ///
    /// Why the pane and not the button or a built-in mode: a Popup is its own top-level window and
    /// WPF's modes only avoid the SCREEN edge. They do not know the app window, the footer or the
    /// rail exist, so "right of the button" hung over the desktop and "Top" covered the status bar
    /// (2026-07-30). The pane is the one element bounded by all three.
    ///
    /// Placement is computed against an UNSCALED root, not the zoomed pane. With the pane as
    /// PlacementTarget, WPF multiplied a Popup's coordinates by the app size while a ContextMenu
    /// took a different path, so the two kinds of flyout landed in different places at any size
    /// but 100% (KillerScan).
    ///
    /// WIRING (at startup, and again when the sidebar moves to another edge):
    ///     FlyoutPlacement.UsePane(ContentPane, side);           // or UsePane(pane, root, side)
    /// then, each time a flyout opens:
    ///     FlyoutPlacement.Attach(historyMenu, historyButton);   // rises with its button
    ///     FlyoutPlacement.Attach(themeMenu, null);              // large menus: always the corner
    ///     themeMenu.IsOpen = true;
    ///
    /// The big menus (theme, language) always open in the corner whatever button opens them:
    /// raised to a button they would cover the pane they are meant to sit beside.
    ///
    /// The flyout's card carries a transparent margin for its drop shadow (FlyoutCard, or the
    /// ContextMenu template's MenuRoot); that halo is read from the card itself and taken off, so
    /// the VISIBLE card sits the eight pixels in. Do not add an inset of your own.
    ///
    /// UsePane without a side keeps the side (and root) already set, so a picker that re-points
    /// the pane on every open does not move a right-docked rail's menus back to the left.
    ///
    /// A pane that carries a strip along its foot which is not pane (a row of tabs below the
    /// list) says where its visible foot is with SetVisibleFoot. The corner then sits eight pixels
    /// above that strip instead of over it. It is read each time a flyout opens, so it follows
    /// the strip as it comes and goes. An app that never sets it keeps the pane's own foot.
    ///
    /// Fixed placements for menus that are not rail flyouts, measured against the pane itself:
    ///     AttachTopLeft(popup)                  the pane's top-left corner
    ///     AttachTopRight(menu)                  8px in from the pane's top-right (title bar menus)
    ///     AttachBelowRight(menu, button)        under a toolbar button, right edges aligned
    ///     AttachBesideButton(menu, button)      in from the rail, centered on its button
    ///     AttachInsidePane(menu or popup, button, alignRight)
    ///                                           8px inside the pane's top corner, for a flyout
    ///                                           anchored to a button inside the pane
    /// </summary>
    internal static class FlyoutPlacement
    {
        private const double Inset = 8;
        private static readonly Thickness DefaultCardHalo = new(22, 18, 22, 26);

        // Held weakly: every window points these at itself, and a strong static reference kept the
        // last window that did so alive after it closed (KillerPDF's window collection check).
        private static WeakReference<FrameworkElement>? _paneRef, _rootRef;
        private static FrameworkElement? _pane
        {
            get => _paneRef != null && _paneRef.TryGetTarget(out var pane) ? pane : null;
            set => _paneRef = value == null ? null : new WeakReference<FrameworkElement>(value);
        }
        private static FrameworkElement? _root
        {
            get => _rootRef != null && _rootRef.TryGetTarget(out var root) ? root : null;
            set => _rootRef = value == null ? null : new WeakReference<FrameworkElement>(value);
        }
        private static SidebarSide _side;

        /// <summary>The content pane. Set once at startup; every flyout positions against it.
        /// Without a side the current side is kept (Left until one is given), and the root set
        /// earlier is kept while the pane is the same.</summary>
        internal static void UsePane(FrameworkElement pane, SidebarSide? side = null)
        {
            if (!ReferenceEquals(pane, _pane)) _root = null;
            _pane = pane;
            if (side is { } s) _side = s;
        }

        /// <summary>The content pane and the unscaled element placement is measured in (the
        /// window's root grid). Without a root, the window's content is used. Without a side the
        /// current side is kept.</summary>
        internal static void UsePane(FrameworkElement pane, FrameworkElement? root, SidebarSide? side = null)
        {
            _pane = pane;
            _root = root;
            if (side is { } s) _side = s;
        }

        private static FrameworkElement? Root =>
            _root ?? (_pane == null ? null : Window.GetWindow(_pane)?.Content as FrameworkElement) ?? _pane;

        /// <summary>The pane's visible foot: given the root placement measures in, the Y of the
        /// strip's top in that root's DIPs, or NaN while no strip covers the foot. Stored on the
        /// pane itself, so it goes away with the pane's window.</summary>
        internal static readonly DependencyProperty VisibleFootProperty = DependencyProperty.RegisterAttached(
            "VisibleFoot", typeof(Func<FrameworkElement, double>), typeof(FlyoutPlacement), new PropertyMetadata(null));

        internal static void SetVisibleFoot(FrameworkElement pane, Func<FrameworkElement, double>? foot)
            => pane.SetValue(VisibleFootProperty, foot);

        /// <summary>The pane in the root's DIPs, its foot raised to the visible foot when the pane
        /// gives one. A top sidebar's corner is at the pane's top, so its foot is left alone.</summary>
        private static Rect PaneIn(FrameworkElement pane, FrameworkElement root)
        {
            Rect bounds = pane.TransformToAncestor(root).TransformBounds(new Rect(pane.RenderSize));
            if (_side != SidebarSide.Top && pane.GetValue(VisibleFootProperty) is Func<FrameworkElement, double> foot)
            {
                double y = foot(root);
                if (!double.IsNaN(y) && y > bounds.Top && y < bounds.Bottom) bounds.Height = y - bounds.Top;
            }
            return bounds;
        }

        internal static void Attach(Popup popup, UIElement? anchor)
        {
            popup.PlacementTarget = Root;
            popup.Placement = PlacementMode.Custom;
            popup.HorizontalOffset = popup.VerticalOffset = 0;
            Thickness halo = popup.Child is FrameworkElement card ? card.Margin : DefaultCardHalo;
            popup.CustomPopupPlacementCallback = (popupSize, _, _) => Place(popup, popupSize, halo, anchor);
        }

        internal static void Attach(ContextMenu menu, UIElement? anchor)
        {
            menu.PlacementTarget = Root;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = menu.VerticalOffset = 0;
            menu.ApplyTemplate();
            Thickness halo = menu.Template?.FindName("MenuRoot", menu) is FrameworkElement card ? card.Margin : DefaultCardHalo;
            FitWindow(menu, halo);
            menu.CustomPopupPlacementCallback = (popupSize, _, _) => Place(menu, popupSize, halo, anchor);
        }

        /// <summary>
        /// Only a window too short for the whole menu makes it scroll. The menu's room runs from
        /// eight pixels inside the window's top down to the pane's corner, eight pixels above the
        /// pane's foot, so its foot stays in the corner; the template's ScrollViewer scrolls the
        /// rest. The pane's top never limits it, since a tall menu rises above the pane.
        /// Measured in the unscaled root's DIPs, the units the menu itself lays out in.
        /// </summary>
        private static void FitWindow(ContextMenu menu, Thickness halo)
        {
            var root = Root;
            if (root == null || root.ActualHeight <= 0)
            {
                menu.ClearValue(FrameworkElement.MaxHeightProperty);
                return;
            }
            double foot = root.ActualHeight;
            if (_side != SidebarSide.Top && _pane != null && _pane.IsDescendantOf(root) && _pane.ActualHeight > 0)
                foot = Math.Min(foot, PaneIn(_pane, root).Bottom);
            menu.MaxHeight = Math.Max(0, foot - 2 * Inset) + halo.Top + halo.Bottom;
        }

        /// <summary>
        /// The callback's sizes are device pixels, so every DIP length is scaled by the root's own
        /// device ratio before it is combined with them.
        /// </summary>
        private static CustomPopupPlacement[] Place(FrameworkElement flyout, Size popupSize, Thickness halo, UIElement? anchor)
        {
            var root = Root;
            if (_pane == null || root == null || !_pane.IsDescendantOf(root))
                return [new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None)];

            var dpi = VisualTreeHelper.GetDpi(root);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
            Rect pane = PaneIn(_pane, root);
            Point Device(Point p) => new(p.X * sx, p.Y * sy);
            var topLeft = Device(pane.TopLeft);
            var bottomRight = Device(pane.BottomRight);
            double inX = Inset * sx, inY = Inset * sy;
            // The halo is in the flyout's own DIPs; the flyout can be scaled differently from the
            // root (app size), so measure its own ratio from the size WPF reports.
            double fx = flyout.ActualWidth > 0 ? popupSize.Width / flyout.ActualWidth : sx;
            double fy = flyout.ActualHeight > 0 ? popupSize.Height / flyout.ActualHeight : sy;

            // A top or bottom strip keeps its rail buttons at its right end, so its menus take
            // the pane's right corner, under or over the buttons that open them.
            double x = _side == SidebarSide.Left
                ? topLeft.X + inX - halo.Left * fx
                : bottomRight.X - popupSize.Width + halo.Right * fx - inX;

            // The corner: visible card's foot eight pixels above the pane's foot (or its top below
            // the pane's top for a top sidebar).
            double cornerY = _side == SidebarSide.Top
                ? topLeft.Y + inY - halo.Top * fy
                : bottomRight.Y - popupSize.Height + halo.Bottom * fy - inY;
            double y = cornerY;

            // Beside a left or right rail, rise with the button: the visible card's foot level with
            // the button's foot, never below the corner and never above the pane's top.
            if (anchor is FrameworkElement button && button.IsDescendantOf(root) && _side is SidebarSide.Left or SidebarSide.Right)
            {
                double buttonFoot = Device(button.TransformToAncestor(root).Transform(new Point(0, button.RenderSize.Height))).Y;
                double level = buttonFoot - popupSize.Height + halo.Bottom * fy;
                double highest = topLeft.Y + inY - halo.Top * fy;
                y = System.Math.Max(highest, System.Math.Min(cornerY, level));
            }
            // A flyout taller than the pane keeps its foot in the corner and rises above the pane as
            // far as it needs. Only the window bounds it: never below the corner, and never above
            // eight pixels inside the window's top (2026-11-21: pinning it to the pane's top ran
            // the theme menu off the window's bottom, and holding it to the pane cut it off).
            if (_side != SidebarSide.Top)
            {
                var rootSize = Device(new Point(root.ActualWidth, root.ActualHeight));
                double windowTop = inY - halo.Top * fy;
                double windowFoot = rootSize.Y - inY - popupSize.Height + halo.Bottom * fy;
                y = System.Math.Max(windowTop, System.Math.Min(y, System.Math.Min(cornerY, windowFoot)));
            }
            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
        }

        // The FlyoutCard halo for the fixed placements below, which measure against the pane.
        private static Thickness FlyoutCardHalo() => new Border
        {
            Style = Application.Current?.TryFindResource("FlyoutCard") as Style
        }.Margin;

        /// <summary>The pane's top-left corner, with no inset (Killendar's export scope popup).</summary>
        internal static void AttachTopLeft(Popup popup)
        {
            popup.PlacementTarget = _pane;
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback =
                (_, _, _) => [new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None)];
        }

        /// <summary>The title bar's menus (a lock button) open in the pane's top-right corner, 8px
        /// in from the top and the right: the mirror of the rail flyouts' bottom corner.</summary>
        internal static void AttachTopRight(ContextMenu menu)
        {
            menu.PlacementTarget = _pane;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                var halo = FlyoutCardHalo();
                return [new CustomPopupPlacement(new Point(
                    targetSize.Width - popupSize.Width + halo.Right - Inset,
                    Inset - halo.Top), PopupPrimaryAxis.None)];
            };
        }

        /// <summary>A toolbar dropdown: under its button, the visible card's right edge on the
        /// button's right edge, 4px below it.</summary>
        internal static void AttachBelowRight(ContextMenu menu, FrameworkElement button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                var halo = FlyoutCardHalo();
                double x = targetSize.Width - popupSize.Width + halo.Right;
                double y = targetSize.Height - halo.Top + 4;
                return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
            };
        }

        /// <summary>In from the rail like the corner menus, but centered on its button vertically
        /// and kept inside the pane (KillerNotes' dictation menu).</summary>
        internal static void AttachBesideButton(ContextMenu menu, FrameworkElement button)
        {
            var pane = _pane;
            if (pane is null) { Attach(menu, button); return; }
            menu.PlacementTarget = pane;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                var halo = FlyoutCardHalo();
                double x = _side != SidebarSide.Left
                    ? targetSize.Width - popupSize.Width + halo.Right - Inset
                    : Inset - halo.Left;
                // Popup target sizes include the app's layout scale. TranslatePoint returns
                // pane-local coordinates, so its height is converted to the callback's units.
                double scaleY = pane.ActualHeight > 0 ? targetSize.Height / pane.ActualHeight : 1;
                double center = button.TranslatePoint(new Point(0, button.ActualHeight / 2), pane).Y * scaleY;
                double visibleHeight = popupSize.Height - halo.Top - halo.Bottom;
                double y = center - visibleHeight / 2 - halo.Top;
                double top = Inset - halo.Top;
                double bottom = targetSize.Height - popupSize.Height + halo.Bottom - Inset;
                return [new CustomPopupPlacement(new Point(x, Math.Max(top, Math.Min(bottom, y))), PopupPrimaryAxis.None)];
            };
        }

        /// <summary>A flyout anchored to a button inside the pane: the visible card 8px inside the
        /// pane's top-left corner, or its top-right with <paramref name="alignRight"/>.</summary>
        internal static void AttachInsidePane(ContextMenu menu, UIElement button, bool alignRight = false)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = menu.VerticalOffset = 0;
            menu.CustomPopupPlacementCallback = PanePlacement(button, alignRight,
                () => menu.Template?.FindName("MenuRoot", menu) as FrameworkElement);
        }

        /// <inheritdoc cref="AttachInsidePane(ContextMenu, UIElement, bool)"/>
        internal static void AttachInsidePane(Popup popup, UIElement button, bool alignRight = false)
        {
            popup.PlacementTarget = button;
            popup.Placement = PlacementMode.Custom;
            popup.HorizontalOffset = popup.VerticalOffset = 0;
            popup.CustomPopupPlacementCallback = PanePlacement(button, alignRight,
                () => popup.Child as FrameworkElement);
        }

        private static CustomPopupPlacementCallback PanePlacement(UIElement button, bool alignRight, Func<FrameworkElement?> card)
            => (popupSize, targetSize, offset) =>
            {
                if (_pane == null || button is not FrameworkElement element)
                    return [new CustomPopupPlacement(new Point(0, targetSize.Height + 8), PopupPrimaryAxis.None)];
                FrameworkElement? face = card();
                Vector faceOffset = new();
                double faceWidth = popupSize.Width;
                if (face != null && PresentationSource.FromVisual(face) is { RootVisual: Visual root, CompositionTarget: { } target })
                {
                    var transform = face.TransformToAncestor(root);
                    Point left = target.TransformToDevice.Transform(transform.Transform(new Point()));
                    Point right = target.TransformToDevice.Transform(transform.Transform(new Point(face.ActualWidth, 0)));
                    faceOffset = (Vector)left;
                    faceWidth = right.X - left.X;
                }
                Point desired = _pane.PointToScreen(new Point(alignRight ? _pane.ActualWidth - Inset : Inset, Inset));
                Vector position = desired - element.PointToScreen(new Point()) - faceOffset;
                if (alignRight) position.X -= faceWidth;
                return [new CustomPopupPlacement((Point)position, PopupPrimaryAxis.None)];
            };
    }
}
