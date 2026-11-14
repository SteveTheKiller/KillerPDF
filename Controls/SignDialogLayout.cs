using System.Windows;
using System.Windows.Controls;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The Digital Signature window's two compact columns: the certificate, signer details and
    /// timestamp on the left, the visible signature and the signed copy's path on the right. Built
    /// without resource lookups so the layout can be checked headless.
    /// </summary>
    internal static class SignDialogLayout
    {
        public const double ColumnWidth = 352;

        /// <summary>Padding for the 26 px Browse buttons: the kit button's own padding is for taller buttons and
        /// pushed the bottom of the caption out of view at this height.</summary>
        public static System.Windows.Thickness CompactButtonPadding => new(12, 0, 12, 0);
        public const double ColumnGap = 24;
        /// <summary>Space between the card's edges and the columns, the footer's 16 plus 4.</summary>
        public const double Inset = 20;
        /// <summary>The transparent shadow halo on each side of the dialog card.</summary>
        public const double Halo = 12;

        public static double ContentWidth => 2 * ColumnWidth + ColumnGap;

        /// <summary>The window's width at 100%: the columns, their insets and the halo.</summary>
        public static double OpeningWidth => ContentWidth + 2 * Inset + 2 * Halo;

        /// <summary>
        /// The two columns side by side. They share the width equally, so a wider window widens
        /// both, and the taller one sets the height. The left column is added first so Tab reaches
        /// it before the right one.
        /// </summary>
        public static Grid Columns(FrameworkElement left, FrameworkElement right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnGap) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            left.VerticalAlignment = VerticalAlignment.Top;
            right.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            return grid;
        }
    }
}
