using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace KillerPDF.Shell
{
    /// <summary>The edge of the content area a sidebar docks to.</summary>
    internal enum SidebarSide { Left, Right, Top, Bottom }

    /// <summary>
    /// Geometry for a sidebar that docks to any edge of the content area.
    ///
    /// Left and right keep the sidebar a column with a vertical rail. Top and bottom turn it into
    /// a strip with a horizontal rail. Either way the rail sits on the sidebar's INNER edge, the
    /// one facing the content, so it never moves when the sidebar collapses (the locked rail rule).
    /// A horizontal rail is one row with two ends: the button stack at one, and at the other the
    /// collapse chevron with the open panel's header lifted up out of the panel beside it, so the
    /// content keeps the strip's height. The ends can swap; the buttons then run in reverse, so
    /// they always read outward from the middle. Each button's spacing turns with it (Transpose).
    ///
    /// The side is saved under "SidebarSide" as Left, Right, Top or Bottom, so an app that only
    /// ever wrote Left or Right reads its old setting unchanged.
    /// </summary>
    internal static class SidebarSides
    {
        internal const string SettingName = "SidebarSide";

        /// <summary>True for a top or bottom sidebar, which lays out as a horizontal strip.</summary>
        internal static bool IsStrip(this SidebarSide side)
            => side == SidebarSide.Top || side == SidebarSide.Bottom;

        /// <summary>True when the sidebar comes after the content (right or bottom).</summary>
        internal static bool IsTrailing(this SidebarSide side)
            => side == SidebarSide.Right || side == SidebarSide.Bottom;

        /// <summary>
        /// The next side in menu order: left, right, top, bottom, then back to left. One press
        /// from the default still moves a left sidebar to the right, as it did when those were
        /// the only two sides.
        /// </summary>
        internal static SidebarSide Next(this SidebarSide side)
        {
            switch (side)
            {
                case SidebarSide.Left: return SidebarSide.Right;
                case SidebarSide.Right: return SidebarSide.Top;
                case SidebarSide.Top: return SidebarSide.Bottom;
                default: return SidebarSide.Left;
            }
        }

        /// <summary>Reads a saved side. Anything unknown or missing is the default, left.</summary>
        internal static SidebarSide Parse(string? value)
        {
            SidebarSide side;
            return Enum.TryParse(value, true, out side) && Enum.IsDefined(typeof(SidebarSide), side)
                ? side : SidebarSide.Left;
        }

        /// <summary>The string key that names the side in a menu: Str_Sidebar_Left and so on.</summary>
        internal static string StringKey(this SidebarSide side) => "Str_Sidebar_" + side;

        /// <summary>
        /// Swaps a thickness across the diagonal, so a vertical rail's spacing becomes a
        /// horizontal rail's: a gap below a button becomes a gap to its right. Applying it twice
        /// gives back the original, so the same call turns the rail either way.
        /// </summary>
        internal static Thickness Transpose(Thickness t) => new Thickness(t.Top, t.Left, t.Bottom, t.Right);

        /// <summary>The side's card tile glyph (Segoe MDL2 DockLeft, DockRight, DockBottom). The font has no
        /// DockTop, so Top uses DockBottom and its tile turns it upside down (FlipsTileGlyph).</summary>
        internal static string TileGlyph(this SidebarSide side) => side switch
        {
            SidebarSide.Left => "\uE90C",
            SidebarSide.Right => "\uE90D",
            _ => "\uE90E",
        };

        internal static bool FlipsTileGlyph(this SidebarSide side) => side == SidebarSide.Top;

        /// <summary>The side menu's icon for this side (PaneSideIcon).</summary>
        internal static FrameworkElement MenuIcon(this SidebarSide side)
            => PaneSideIcon(side == SidebarSide.Left ? Dock.Left : side == SidebarSide.Right ? Dock.Right
                          : side == SidebarSide.Top ? Dock.Top : Dock.Bottom);

        /// <summary>
        /// A small window with its sidebar shaded on the given side, the icon for a side menu's
        /// items. Drawn in the item's text color, read the way a glyph icon reads it, so it
        /// follows the highlight; a checked item shows its checkmark in this slot instead.
        /// </summary>
        internal static FrameworkElement PaneSideIcon(Dock side)
        {
            var frame = new Border { BorderThickness = new Thickness(1) };
            frame.SetBinding(Border.BorderBrushProperty, InheritedForeground());
            bool across = side == Dock.Top || side == Dock.Bottom;
            var bar = new Border
            {
                Width = across ? double.NaN : 3,
                Height = across ? 3 : double.NaN,
                HorizontalAlignment = side == Dock.Right ? HorizontalAlignment.Right
                                    : side == Dock.Left ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                VerticalAlignment = side == Dock.Bottom ? VerticalAlignment.Bottom
                                  : side == Dock.Top ? VerticalAlignment.Top : VerticalAlignment.Stretch,
            };
            bar.SetBinding(Border.BackgroundProperty, InheritedForeground());
            var icon = new Grid { Width = 12, Height = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            icon.Children.Add(frame);
            icon.Children.Add(bar);
            return icon;
        }

        private static System.Windows.Data.Binding InheritedForeground() => new System.Windows.Data.Binding
        {
            Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty),
            RelativeSource = System.Windows.Data.RelativeSource.Self,
        };
    }

    /// <summary>
    /// The grid track that sizes the sidebar: a column for a left or right sidebar, a row for a
    /// top or bottom one. Width is the sidebar's thickness away from its edge in either case, so
    /// collapse, resize, scaling and full-screen code reads the same whichever edge it is on.
    /// </summary>
    internal sealed class SidebarTrack
    {
        private readonly ColumnDefinition? _column;
        private readonly RowDefinition? _row;

        internal SidebarTrack(ColumnDefinition column) { _column = column; }
        internal SidebarTrack(RowDefinition row) { _row = row; }

        /// <summary>True when the track is a row (a top or bottom sidebar).</summary>
        internal bool IsRow => _row != null;

        internal GridLength Width
        {
            get => _row != null ? _row.Height : _column!.Width;
            set { if (_row != null) _row.Height = value; else _column!.Width = value; }
        }

        internal double MinWidth
        {
            get => _row != null ? _row.MinHeight : _column!.MinWidth;
            set { if (_row != null) _row.MinHeight = value; else _column!.MinWidth = value; }
        }

        internal double MaxWidth
        {
            get => _row != null ? _row.MaxHeight : _column!.MaxWidth;
            set { if (_row != null) _row.MaxHeight = value; else _column!.MaxWidth = value; }
        }

        internal double ActualWidth => _row != null ? _row.ActualHeight : _column!.ActualWidth;

        /// <summary>Animates the track's length; null clears a running animation.</summary>
        internal void BeginAnimation(AnimationTimeline? animation)
        {
            if (_row != null) _row.BeginAnimation(RowDefinition.HeightProperty, animation);
            else _column!.BeginAnimation(ColumnDefinition.WidthProperty, animation);
        }
    }
}
