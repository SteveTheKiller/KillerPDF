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

        /// <summary>The narrowest the notice's message may wrap to, at scale 1.</summary>
        internal const double MessageMinimum = 120;

        /// <summary>
        /// The scale the notice is drawn at: the app scale, unless the pane cannot hold the notice's minimum width at
        /// that scale (the narrowest window at a large app size), when it is drawn just small enough to fit.
        /// </summary>
        internal static double FitScale(double appScale, double paneWidth, double minimumWidth)
            => paneWidth > 0 && minimumWidth > 0 && minimumWidth * appScale > paneWidth ? paneWidth / minimumWidth : appScale;

        /// <summary>
        /// The notice's minimum width at scale 1: its edge, padding and controls at their natural widths, plus
        /// <see cref="MessageMinimum"/> for the wrapping message. Measures the notice's content unconstrained, so
        /// call it outside a layout pass.
        /// </summary>
        internal static double MinimumWidth(System.Windows.Controls.Border notice, FrameworkElement message)
        {
            if (notice.Child is not UIElement content) return 0;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Thickness edge = notice.BorderThickness, padding = notice.Padding;
            return content.DesiredSize.Width - message.DesiredSize.Width + MessageMinimum
                + edge.Left + edge.Right + padding.Left + padding.Right;
        }

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
