using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace KillerPDF.Shell
{
    /// <summary>
    /// The icons for the Docked and Floating Tool Menu rows (#485), drawn like the sidebar's side icons
    /// (<see cref="SidebarSides.PaneSideIcon"/>): a small window with the tool menu docked as a strip across
    /// its top, or hanging from the top edge as a short tab. Drawn in the row's text color.
    /// </summary>
    internal static class ToolMenuIcons
    {
        internal static FrameworkElement For(bool floating)
        {
            if (!floating) return SidebarSides.PaneSideIcon(Dock.Top);
            var frame = new Border { BorderThickness = new Thickness(1) };
            frame.SetBinding(Border.BorderBrushProperty, InheritedForeground());
            var tab = new Border
            {
                Width = 5,
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 2, 0),
            };
            tab.SetBinding(Border.BackgroundProperty, InheritedForeground());
            var icon = new Grid { Width = 12, Height = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            icon.Children.Add(frame);
            icon.Children.Add(tab);
            return icon;
        }

        private static Binding InheritedForeground() => new()
        {
            Path = new PropertyPath(TextElement.ForegroundProperty),
            RelativeSource = RelativeSource.Self,
        };
    }
}
