using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Where a notice centered at the top of the document pane sits when a floating bar shares the pane. The find bar
    /// stays where the user put it; a notice that would sit under it moves just below it instead, so its message and
    /// buttons stay readable and clickable. Anywhere else the notice keeps its place at the top.
    /// </summary>
    internal static class NoticePlacement
    {
        /// <summary>The space left between the floating bar and a notice moved below it.</summary>
        internal const double Gap = 6;

        /// <summary>
        /// The notice's top margin in pane pixels. Both rectangles are taken as drawn, so the notice's app-scale
        /// transform counts, and the notice is tested at its natural place at the top whatever margin it has now, so
        /// applying the answer and asking again gives the same answer.
        /// </summary>
        internal static double TopClearOf(FrameworkElement notice, FrameworkElement? floating)
        {
            if (floating is not { Visibility: Visibility.Visible } || notice.Visibility != Visibility.Visible
                || VisualTreeHelper.GetParent(notice) is not Visual pane
                || !ReferenceEquals(VisualTreeHelper.GetParent(floating), pane))
                return 0;
            Rect bar = floating.TransformToAncestor(pane).TransformBounds(new Rect(floating.RenderSize));
            Rect natural = notice.TransformToAncestor(pane).TransformBounds(new Rect(notice.RenderSize));
            natural.Offset(0, -notice.Margin.Top);
            bool overlaps = natural.Left < bar.Right && bar.Left < natural.Right
                && natural.Top < bar.Bottom && bar.Top < natural.Bottom;
            return overlaps ? bar.Bottom + Gap : 0;
        }
    }
}
