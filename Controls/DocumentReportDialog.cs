using System;
using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

// A read-only report (print production, signatures, layers, portfolio and the rest): the report's name with a
// copy icon above the text, the text in a well, and Close on the footer. The window fits the report, up to most
// of the screen, after which the text scrolls.
internal sealed class DocumentReportDialog : Window
{
    internal DocumentReportDialog(Window owner, string title, string report)
    {
        Title = "KillerPDF - " + title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 460;
        MinHeight = 240;
        MaxWidth = Math.Max(MinWidth, Math.Min(1000, SystemParameters.WorkArea.Width * 0.85));
        MaxHeight = Math.Max(MinHeight, Math.Min(760, SystemParameters.WorkArea.Height * 0.85));
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };

        var reportBox = UiKit.Field();
        reportBox.Text = report;
        reportBox.IsReadOnly = true;
        reportBox.AcceptsReturn = true;
        reportBox.AcceptsTab = true;
        reportBox.TextWrapping = TextWrapping.NoWrap;
        reportBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        reportBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        reportBox.FontFamily = UiKit.MonoFont;
        reportBox.FontSize = 11;
        System.Windows.Automation.AutomationProperties.SetName(reportBox, title);

        // Copying confirms on the footer's left, where the copy icon's tooltip alone would go unseen.
        var copied = new TextBlock { Text = L("Str_DocumentTools_Copied"), Visibility = Visibility.Hidden };
        copied.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        Button copy = DialogParts.IconAction("Str_DocumentTools_Copy", 0xE8C8, (_, _) =>
        {
            try
            {
                Clipboard.SetText(report);
                copied.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        DockPanel footer = DialogParts.Footer([close], copied);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        DockPanel header = DialogParts.Header(title, copy);
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(DialogParts.Well(reportBox));

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => reportBox.Focus();
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
