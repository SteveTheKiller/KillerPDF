using System.Windows;

namespace KillerPDF.Shell
{
    /// <summary>
    /// The page list's scrollbar is the gap between the selected thumbnail and the grip, so it is
    /// sized to the fat hovered thumb plus 1px on either side (Steve 2026-11-25). The thumb grows to
    /// its fat margin on hover; a fat margin over 1 narrows the bar by the difference on each side,
    /// and the resting thumb keeps its width in both orientations. A theme whose resting thumb
    /// already fills the bar (98SE) keeps its own bar untouched.
    /// </summary>
    internal static class PageListScrollBar
    {
        public static (double Thickness, Thickness Resting, Thickness RestingH, Thickness Fat)? Size(
            double thickness, Thickness resting, Thickness restingH, Thickness fat)
        {
            double trim = fat.Left - 1;
            if (trim <= 0 || resting.Left < trim || resting.Right < trim
                || restingH.Top < trim || restingH.Bottom < trim || thickness <= 2 * trim) return null;
            return (thickness - 2 * trim,
                new Thickness(resting.Left - trim, resting.Top, resting.Right - trim, resting.Bottom),
                new Thickness(restingH.Left, restingH.Top - trim, restingH.Right, restingH.Bottom - trim),
                new Thickness(1));
        }
    }
}
