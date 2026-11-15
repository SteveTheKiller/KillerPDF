using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KillerPDF.Controls
{
    // In its own file so an app whose picker has no FolderTree.cs can still build Controls.xaml,
    // which declares it.
    // True when a TreeViewItem is the last child of its parent - which is the one thing the
    // folder tree's connecting lines need to know. Every node draws a vertical line down its own
    // left edge; for the LAST child that line has to stop at the elbow instead of running past
    // the bottom of the node into empty space. There is no "IsLastItem" property in WPF and no
    // way to ask in pure XAML, hence this. Bound with the item itself as the source, so it
    // re-evaluates when the container is recycled onto a different node.
    //
    // As a multi-value converter its second input is the parent's Items.Count, which does nothing
    // but make the binding re-evaluate when an item is inserted after the one that was last.
    public sealed class LastChildConverter : IValueConverter, IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            Convert(values != null && values.Length > 0 ? values[0] : null!, targetType, parameter, culture);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not DependencyObject d) return false;

            var parent = ItemsControl.ItemsControlFromItemContainer(d);
            if (parent == null) return false;

            int index = parent.ItemContainerGenerator.IndexFromContainer(d);
            return index >= 0 && index == parent.Items.Count - 1;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
