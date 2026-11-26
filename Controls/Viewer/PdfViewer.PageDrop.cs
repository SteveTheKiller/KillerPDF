using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    public partial class PdfViewer
    {
        private Border? _pageImportDropIndicator;

        /// <summary>
        /// Resolves a cross-document drop against the page under the pointer and draws the same
        /// accent rule used by the sidebar reorder surface. The upper half inserts before the page;
        /// the lower half inserts after it.
        /// </summary>
        internal bool ShowPageImportDropIndicator(DragEventArgs e, out int insertionIndex)
        {
            insertionIndex = -1;
            if (_doc is null || PagePreviewPanel.Visibility != Visibility.Visible) return false;

            FrameworkElement? pageElement = FindTaggedPageElement(e.OriginalSource as DependencyObject);
            if (pageElement?.Tag is not int page || page < 0 || page >= _doc.PageCount) return false;

            Point pointer = e.GetPosition(pageElement);
            bool after = pointer.Y >= pageElement.ActualHeight / 2;
            insertionIndex = Math.Clamp(page + (after ? 1 : 0), 0, _doc.PageCount);

            try
            {
                GeneralTransform transform = pageElement.TransformToVisual(DocPaneContent);
                Point topLeft = transform.Transform(new Point(0, 0));
                Point topRight = transform.Transform(new Point(pageElement.ActualWidth, 0));
                Point bottomLeft = transform.Transform(new Point(0, pageElement.ActualHeight));
                double left = Math.Min(topLeft.X, topRight.X);
                double width = Math.Max(24, Math.Abs(topRight.X - topLeft.X));
                double y = after ? bottomLeft.Y : topLeft.Y;

                _pageImportDropIndicator ??= BuildPageImportDropIndicator();
                _pageImportDropIndicator.Width = width;
                _pageImportDropIndicator.Margin = new Thickness(left, y - 2, 0, 0);
                _pageImportDropIndicator.Visibility = Visibility.Visible;
                return true;
            }
            catch (InvalidOperationException)
            {
                HidePageImportDropIndicator();
                insertionIndex = -1;
                return false;
            }
        }

        internal void HidePageImportDropIndicator()
        {
            _pageImportDropIndicator?.Visibility = Visibility.Collapsed;
        }

        private Border BuildPageImportDropIndicator()
        {
            var indicator = new Border
            {
                Height = 3,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(1.5),
                Visibility = Visibility.Collapsed
            };
            indicator.SetResourceReference(Border.BackgroundProperty, "SelectionAccent");
            Panel.SetZIndex(indicator, 600);
            KillerPDF.Presentation.ViewportOcclusion.SetOccludes(indicator, true);
            DocPaneContent.Children.Add(indicator);
            return indicator;
        }

        private static FrameworkElement? FindTaggedPageElement(DependencyObject? source)
        {
            for (DependencyObject? node = source; node is not null; node = ParentOf(node))
            {
                if (node is Canvas { Tag: int } canvas) return canvas;
                if (node is Border { Tag: int } border) return border;
            }
            return null;
        }

        private static DependencyObject? ParentOf(DependencyObject node)
            => node is Visual
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
    }
}
