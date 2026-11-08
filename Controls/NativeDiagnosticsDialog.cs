using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using KillerPDF.Services;

namespace KillerPDF;

// Lists, per chapter, what a native book left out, with Copy and Close like the document reports.
internal sealed class NativeDiagnosticsDialog : Window
{
    internal NativeDiagnosticsDialog(Window owner, IReadOnlyList<NativeDiagnosticRow> rows)
    {
        Title = "KillerPDF - " + L("Str_Epub_Diagnostics_Title");
        Width = 640;
        Height = 520;
        MinWidth = 420;
        MinHeight = 320;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new Grid { Margin = new Thickness(20, 6, 20, 16) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = Text(L(rows.Count > 0 ? "Str_Epub_Diagnostics_Intro" : "Str_Epub_Diagnostics_None"), muted: true);
        intro.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(intro);

        var list = new StackPanel { Name = "NativeDiagnosticsList" };
        AutomationProperties.SetName(list, L("Str_Epub_Diagnostics_Title"));
        foreach (var row in rows)
        {
            var heading = Text(row.Heading, muted: false);
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 4);
            list.Children.Add(heading);
            foreach (string line in row.Lines)
            {
                var item = Text(line, muted: true);
                item.Margin = new Thickness(12, 0, 0, 2);
                list.Children.Add(item);
            }
        }
        var scroller = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true
        };
        Grid.SetRow(scroller, 1);
        body.Children.Add(scroller);

        // The copied report keeps the engine's English diagnosis after each localized reason.
        string text = NativeDocumentDiagnostics.Report(rows);
        var copy = UiKit.Make(L("Str_DocumentTools_Copy"), accent: false);
        copy.IsEnabled = rows.Count > 0;
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text);
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
        Grid.SetRow(buttons, 2);
        body.Children.Add(buttons);

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => scroller.Focus();
    }

    private static TextBlock Text(string text, bool muted)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedTextBrush" : "TextBrush");
        return block;
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
