using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal sealed class ExportDocumentDialog : Window
{
    private readonly ComboBox _format;
    private readonly TextBox _range;

    internal bool Confirmed { get; private set; }
    internal PdfStructuredExportFormat Format { get; private set; }
    internal string Range { get; private set; } = string.Empty;

    internal ExportDocumentDialog(Window owner)
    {
        Title = "KillerPDF - " + L("Str_ExportDoc_Suffix");
        MinWidth = 420;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(UiKit.GroupLabel(L("Str_ExportImg_Format")));
        _format = new ComboBox
        {
            ItemsSource = new[]
            {
                new FormatChoice("Word (.docx)", PdfStructuredExportFormat.WordDocument),
                new FormatChoice("Excel (.xlsx)", PdfStructuredExportFormat.Spreadsheet),
                new FormatChoice("PowerPoint (.pptx)", PdfStructuredExportFormat.Presentation),
                new FormatChoice("HTML (.html)", PdfStructuredExportFormat.Html),
                new FormatChoice("Markdown (.md)", PdfStructuredExportFormat.Markdown),
                new FormatChoice("Plain text (.txt)", PdfStructuredExportFormat.PlainText),
                new FormatChoice("JSON (.json)", PdfStructuredExportFormat.Json)
            },
            SelectedIndex = 0,
            Margin = new Thickness(0, 3, 0, 12)
        };
        if (owner.TryFindResource("DarkComboBox") is Style comboStyle) _format.Style = comboStyle;
        body.Children.Add(_format);

        body.Children.Add(UiKit.GroupLabel(L("Str_Stamp_Pages")));
        _range = UiKit.Field();
        _range.ToolTip = L("Str_Crop_RangeTip");
        _range.Margin = new Thickness(0, 0, 0, 8);
        body.Children.Add(_range);

        var hint = new TextBlock
        {
            Text = L("Str_ExportDoc_Hint"),
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 430,
            Margin = new Thickness(0, 2, 0, 8)
        };
        body.Children.Add(hint);

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var export = UiKit.Make(L("Str_ExportImg_Export"), true);
        export.IsDefault = true;
        export.Click += (_, _) => Commit();
        body.Children.Add(UiKit.ButtonRow(cancel, export));

        Content = DialogChrome.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => _format.Focus();
    }

    private void Commit()
    {
        Format = ((FormatChoice)_format.SelectedItem).Format;
        Range = _range.Text.Trim();
        Confirmed = true;
        Close();
    }

    private static string L(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    private sealed record FormatChoice(string Label, PdfStructuredExportFormat Format)
    {
        public override string ToString() => Label;
    }
}
