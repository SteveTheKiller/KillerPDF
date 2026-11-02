using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF.Controls;

/// <summary>The body of a page job's progress overlay: its count and a button that stops it.</summary>
internal static class BusyProgressPanel
{
    internal static StackPanel Build(string text, string progressFormat, string cancelLabel, Action? cancel, Style? buttonStyle)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 14,
            Tag = progressFormat
        });
        if (cancel is null) return panel;

        var button = new Button
        {
            Content = cancelLabel,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(16, 4, 16, 4),
            MinWidth = 90
        };
        if (buttonStyle is not null) button.Style = buttonStyle;
        button.Click += (_, _) =>
        {
            button.IsEnabled = false;   // the job stops at its next page, and one press is enough
            cancel();
        };
        panel.Children.Add(button);
        return panel;
    }
}
