using System;
using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class DocumentReportDialog : Window
{
    internal DocumentReportDialog(Window owner, string title, string report)
    {
        Title = "KillerPDF - " + title;
        Width = 720;
        Height = 620;
        MinWidth = 460;
        MinHeight = 360;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new Grid { Margin = new Thickness(20, 6, 20, 16) };
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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
        Grid.SetRow(reportBox, 0);
        body.Children.Add(reportBox);

        var copy = UiKit.Make(L("Str_DocumentTools_Copy"), accent: false);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(report);
                copy.Content = L("Str_DocumentTools_Copied");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: true);
        close.IsDefault = true;
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var buttons = UiKit.ButtonRow(copy, close);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(buttons, 1);
        body.Children.Add(buttons);

        Content = DialogChrome.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => reportBox.Focus();
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
