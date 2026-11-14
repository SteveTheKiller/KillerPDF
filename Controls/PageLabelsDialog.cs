using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal sealed class PageLabelsDialog : Window
{
    private readonly int _pageCount;
    private readonly List<LabelRow> _rows = [];
    private readonly StackPanel _rowsPanel = new();

    internal IReadOnlyList<PdfPageLabelMacroRange> Ranges { get; private set; } = [];

    internal PageLabelsDialog(Window owner, int pageCount, IReadOnlyList<PdfPageLabelMacroRange>? ranges = null)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_PageLabels");
        // As tall as the ranges need, up to most of the screen, where they start scrolling.
        Width = 720;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.85);
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        _pageCount = pageCount;

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        DockPanel footer = DialogParts.Footer([cancel, save]);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        var help = new TextBlock
        {
            Text = L("Str_PageLabels_Help"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(help, Dock.Top);
        body.Children.Add(help);

        // Add range is an icon over the ranges; each range has its own remove icon.
        Button addRange = DialogParts.IconAction("Str_PageLabels_AddRange", 0xE710, (_, _) => AddRow(null));
        DockPanel header = DialogParts.Header(string.Empty, addRange);
        header.Children.Remove(header.Children.OfType<TextBlock>().First());
        header.Children.Add(ColumnLabels());
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);

        _rowsPanel.Margin = new Thickness(8, 6, 4, 6);
        foreach (PdfPageLabelMacroRange range in ranges ?? [new(0, PdfPageLabelStyle.Decimal)])
            AddRow(range);
        body.Children.Add(DialogParts.Well(new ScrollViewer
        {
            Content = _rowsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        }));

        Content = DialogFrame.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
    }

    private void AddRow(PdfPageLabelMacroRange? range)
    {
        var grid = CreateGrid();
        TextBox page = Field(((range?.PageIndex ?? 0) + 1).ToString(CultureInfo.CurrentCulture));
        ComboBox style = Combo();
        Choice[] choices =
        [
            new(L("Str_PageLabels_Decimal"), PdfPageLabelStyle.Decimal),
            new(L("Str_PageLabels_UpperRoman"), PdfPageLabelStyle.UpperRoman),
            new(L("Str_PageLabels_LowerRoman"), PdfPageLabelStyle.LowerRoman),
            new(L("Str_PageLabels_UpperLetters"), PdfPageLabelStyle.UpperLetters),
            new(L("Str_PageLabels_LowerLetters"), PdfPageLabelStyle.LowerLetters),
            new(L("Str_PageLabels_PrefixOnly"), PdfPageLabelStyle.None)
        ];
        style.ItemsSource = choices;
        style.SelectedItem = choices.First(item => item.Value ==
            (range?.Style ?? PdfPageLabelStyle.Decimal));
        TextBox prefix = Field(range?.Prefix ?? string.Empty);
        TextBox start = Field((range?.StartNumber ?? 1).ToString(CultureInfo.CurrentCulture));
        LabelRow? row = null;
        // Removing a range drops it from what Save writes, as the old Remove check box did.
        Button remove = DialogParts.IconAction("Str_Portfolio_Remove", 0xE711, (_, _) =>
        {
            _rows.Remove(row!);
            _rowsPanel.Children.Remove(grid);
        });
        remove.HorizontalAlignment = HorizontalAlignment.Center;
        Add(grid, page, 0); Add(grid, style, 1); Add(grid, prefix, 2);
        Add(grid, start, 3); Add(grid, remove, 4);
        _rowsPanel.Children.Add(grid);
        row = new(page, style, prefix, start);
        _rows.Add(row);
    }

    private void Commit()
    {
        var ranges = new List<PdfPageLabelMacroRange>();
        foreach (LabelRow row in _rows)
        {
            if (!int.TryParse(row.Page.Text, NumberStyles.Integer, CultureInfo.CurrentCulture,
                    out int page) || page < 1 || page > _pageCount
                || !int.TryParse(row.Start.Text, NumberStyles.Integer, CultureInfo.CurrentCulture,
                    out int start) || start < 1)
            {
                ShowInvalid();
                return;
            }
            PdfPageLabelStyle style = ((Choice)row.Style.SelectedItem).Value;
            string? prefix = EmptyToNull(row.Prefix.Text);
            if (style == PdfPageLabelStyle.None && prefix is null)
            {
                ShowInvalid();
                return;
            }
            ranges.Add(new PdfPageLabelMacroRange(page - 1, style, prefix, start));
        }
        if (ranges.Count == 0
            || ranges.Select(range => range.PageIndex).Distinct().Count() != ranges.Count)
        {
            ShowInvalid();
            return;
        }
        Ranges = [.. ranges.OrderBy(range => range.PageIndex)];
        DialogResult = true;
        Close();
    }

    private void ShowInvalid() => KillerDialog.Show(this,
        string.Format(L("Str_PageLabels_Invalid"), _pageCount),
        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);

    // The column names over the ranges; the Add icon takes the remove column's place on the right.
    private static Grid ColumnLabels()
    {
        var grid = new Grid { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Bottom };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        string[] labels =
        [
            "Str_PageLabels_FirstPage", "Str_PageLabels_Style", "Str_PageLabels_Prefix",
            "Str_PageLabels_Start"
        ];
        for (int index = 0; index < labels.Length; index++)
            Add(grid, UiKit.GroupLabel(L(labels[index])), index);
        return grid;
    }

    private static Grid CreateGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        return grid;
    }

    private static TextBox Field(string value)
    {
        TextBox field = UiKit.Field();
        field.Text = value;
        field.Margin = new Thickness(0, 2, 8, 2);
        return field;
    }

    private static ComboBox Combo() => new()
    {
        Height = 28,
        Margin = new Thickness(0, 2, 8, 2),
        Style = UiKit.DarkComboStyle
    };

    private static void Add(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static string? EmptyToNull(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private sealed record LabelRow(TextBox Page, ComboBox Style,
        TextBox Prefix, TextBox Start);
    private sealed record Choice(string Label, PdfPageLabelStyle Value)
    {
        public override string ToString() => Label;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
