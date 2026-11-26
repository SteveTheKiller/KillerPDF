using System.Windows;
using System.Windows.Controls;

namespace KillerPDF.Controls
{
    public partial class PdfViewer
    {
        /// <summary>
        /// A floating tool bar hangs from the card's top edge. With the tabs on top, the active tab
        /// paints the card's top border row in its own face color so it opens into the card, and a
        /// bar slid under it merged into the tab with no edge between them. The dock rounds the bar's
        /// top corners and reports the span; this draws the pane's border line back along it, on the
        /// border's own row, above the strip. Over an inactive tab it lies on the border already there.
        /// </summary>
        private void InitializeBarSeal()
        {
            ToolOptionsDock.TopSealChanged += (_, _) => UpdateBarSeal();
            // The card moves under the line when the strip shows, hides or changes side, and the
            // sidebar gutter flips; one check per layout pass costs a TranslatePoint.
            LayoutUpdated += (_, _) => UpdateBarSeal();
        }

        private void UpdateBarSeal()
        {
            bool tabsOnTop = TabStripBorder.Visibility == Visibility.Visible && !TabsAtBottom;
            ToolOptionsDock.SealsTop = tabsOnTop;
            ToolOptionsDock.OpenSpan = tabsOnTop && ActiveTabFace() is FrameworkElement face && face.IsVisible
                ? Span(face.TranslatePoint(new Point(0, 0), ToolOptionsDock).X, face.ActualWidth)
                : null;
            if (!tabsOnTop || ToolOptionsDock.TopSeal is not var (from, to) || to <= from
                || BarSealLine.Parent is not Canvas layer || !ToolOptionsDock.IsVisible)
            {
                if (BarSealLine.Visibility != Visibility.Collapsed) BarSealLine.Visibility = Visibility.Collapsed;
                return;
            }
            // A bar against the card's side runs under the card's top corner. The line stops where that
            // corner starts to curve, so the card's own rounded border draws there and the bar sits
            // behind it; a straight line across the corner squared it off on top of the curve. The
            // card's corners change with the active tab (SyncPaneLeadingCorner), so read them live.
            CornerRadius card = PaneBorder?.CornerRadius ?? default;
            from = Math.Max(from, Math.Ceiling(card.TopLeft) - 1);
            to = Math.Min(to, ToolOptionsDock.ActualWidth - Math.Ceiling(card.TopRight) + 1);
            if (to <= from)
            {
                if (BarSealLine.Visibility != Visibility.Collapsed) BarSealLine.Visibility = Visibility.Collapsed;
                return;
            }
            // The dock's first row is the one below the card's top border.
            Point at = ToolOptionsDock.TranslatePoint(new Point(from, -1), layer);
            if (Canvas.GetLeft(BarSealLine) != at.X) Canvas.SetLeft(BarSealLine, at.X);
            if (Canvas.GetTop(BarSealLine) != at.Y) Canvas.SetTop(BarSealLine, at.Y);
            if (BarSealLine.Width != to - from) BarSealLine.Width = to - from;
            if (BarSealLine.Visibility != Visibility.Visible) BarSealLine.Visibility = Visibility.Visible;
        }

        private static (double, double) Span(double left, double width) => (Math.Round(left), Math.Round(left + width));

        // The active tab's face, the element that paints over the card's top border row.
        private FrameworkElement? ActiveTabFace()
        {
            if (_active is null || TabContainer(_active) is not FrameworkElement container) return null;
            return FindNamed(container, "tabBd");
        }

        private static FrameworkElement? FindNamed(DependencyObject root, string name)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement { Name: var n } element && n == name) return element;
                if (FindNamed(child, name) is { } found) return found;
            }
            return null;
        }
    }
}
