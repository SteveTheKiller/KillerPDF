using KillerPDF.Shell;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal sealed class ExportDocumentDialog : Window
{
    private readonly ComboBox _format;
    private readonly TextBox _range;
    private readonly ComboBox _mode;

    internal bool Confirmed { get; private set; }
    internal PdfStructuredExportFormat Format { get; private set; }
    internal PdfOfficeExportMode OfficeMode { get; private set; }
    internal string Range { get; private set; } = string.Empty;

    internal ExportDocumentDialog(Window owner) : this(owner, PdfStructuredExportFormat.WordDocument, PdfOfficeExportMode.Editable, true) { }

    internal ExportDocumentDialog(Window owner, PdfStructuredExportFormat format, PdfOfficeExportMode mode, bool showRange)
    {
        Title = "KillerPDF - " + L("Str_ExportDoc_Suffix");
        MinWidth = 320;
        MaxWidth = Math.Max(320, SystemParameters.WorkArea.Width - 40);
        MaxHeight = Math.Max(260, SystemParameters.WorkArea.Height - 40);
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        // A short form that sizes to its content: dragged larger on SN01, its buttons floated
        // mid-window instead of staying at the bottom, so it keeps its fitted size.
        DialogFrame.Configure(this, owner, resizable: false);
        AutomationProperties.SetAutomationId(this, "ExportDocumentDialog");

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        Window? scaleOwner = owner;
        while (scaleOwner is not null && scaleOwner is not MainWindow) scaleOwner = scaleOwner.Owner;
        double scale = scaleOwner is MainWindow main ? main.AppScale : 1;
        body.LayoutTransform = new ScaleTransform(scale, scale);
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
        _format.SelectedItem = _format.Items.Cast<FormatChoice>().First(choice => choice.Format == format);
        AutomationProperties.SetAutomationId(_format, "ExportDocumentFormat");
        AutomationProperties.SetName(_format, L("Str_ExportImg_Format"));
        body.Children.Add(_format);

        var layout = new StackPanel();
        layout.Children.Add(UiKit.GroupLabel(L("Str_ExportDoc_Layout")));
        _mode = new ComboBox { ItemsSource = new[] { L("Str_ExportDoc_Editable"), L("Str_ExportDoc_PageImages") },
            SelectedIndex = mode == PdfOfficeExportMode.PageImages ? 1 : 0, Style = UiKit.DarkComboStyle,
            Margin = new Thickness(0, 3, 0, 12) };
        AutomationProperties.SetAutomationId(_mode, "ExportDocumentMode");
        AutomationProperties.SetName(_mode, L("Str_ExportDoc_Layout"));
        layout.Children.Add(_mode); body.Children.Add(layout);

        if (showRange) body.Children.Add(UiKit.GroupLabel(L("Str_Stamp_Pages")));
        _range = UiKit.Field();
        _range.ToolTip = L("Str_Crop_RangeTip");
        _range.Margin = new Thickness(0, 0, 0, 8);
        if (showRange) body.Children.Add(_range);

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
        void UpdateMode()
        {
            bool office = ((FormatChoice)_format.SelectedItem).Format is PdfStructuredExportFormat.WordDocument or PdfStructuredExportFormat.Presentation;
            layout.Visibility = office ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = L(office && _mode.SelectedIndex == 1 ? "Str_ExportDoc_PageImagesHint" : "Str_ExportDoc_Hint");
        }
        _format.SelectionChanged += (_, _) => UpdateMode();
        _mode.SelectionChanged += (_, _) => UpdateMode();
        UpdateMode();

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), false);
        cancel.IsCancel = true;
        AutomationProperties.SetAutomationId(cancel, "ExportDocumentCancel");
        cancel.Click += (_, _) => Close();
        var export = UiKit.Make(L("Str_ExportImg_Export"), true);
        export.IsDefault = true;
        AutomationProperties.SetAutomationId(export, "ExportDocumentSave");
        export.Click += (_, _) => Commit();
        body.Children.Add(UiKit.ButtonRow(cancel, export));

        Content = DialogFrame.Frame(this, owner, Title, Close, new ScrollViewer
            { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Loaded += (_, _) => _format.Focus();
    }

    private void Commit()
    {
        Format = ((FormatChoice)_format.SelectedItem).Format;
        OfficeMode = Format is PdfStructuredExportFormat.WordDocument or PdfStructuredExportFormat.Presentation && _mode.SelectedIndex == 1
            ? PdfOfficeExportMode.PageImages : PdfOfficeExportMode.Editable;
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
