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

    internal PageLabelsDialog(Window owner, int pageCount)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_PageLabels");
        Width = 720;
        Height = 520;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);
        _pageCount = pageCount;

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var buttonsPanel = new StackPanel();
        DockPanel.SetDock(buttonsPanel, Dock.Bottom);
        Button addRange = UiKit.Make(L("Str_PageLabels_AddRange"), accent: false);
        addRange.HorizontalAlignment = HorizontalAlignment.Left;
        addRange.Margin = new Thickness(0, 10, 0, 6);
        addRange.Click += (_, _) => AddRow(null);
        buttonsPanel.Children.Add(addRange);
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        buttonsPanel.Children.Add(UiKit.ButtonRow(cancel, save));
        body.Children.Add(buttonsPanel);

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = L("Str_PageLabels_Help"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        content.Children.Add(Header());
        content.Children.Add(_rowsPanel);
        AddRow(new PdfPageLabelMacroRange(0, PdfPageLabelStyle.Decimal));
        body.Children.Add(new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        Content = DialogChrome.Frame(this, owner, Title,
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
        CheckBox remove = UiKit.CheckBox(string.Empty);
        remove.HorizontalAlignment = HorizontalAlignment.Center;
        Add(grid, page, 0); Add(grid, style, 1); Add(grid, prefix, 2);
        Add(grid, start, 3); Add(grid, remove, 4);
        _rowsPanel.Children.Add(grid);
        _rows.Add(new(page, style, prefix, start, remove));
    }

    private void Commit()
    {
        var ranges = new List<PdfPageLabelMacroRange>();
        foreach (LabelRow row in _rows.Where(row => row.Remove.IsChecked != true))
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

    private static Grid Header()
    {
        Grid grid = CreateGrid();
        string[] labels =
        [
            "Str_PageLabels_FirstPage", "Str_PageLabels_Style", "Str_PageLabels_Prefix",
            "Str_PageLabels_Start", "Str_Portfolio_Remove"
        ];
        for (int index = 0; index < labels.Length; index++)
        {
            TextBlock label = UiKit.GroupLabel(L(labels[index]));
            if (index == labels.Length - 1)
                label.HorizontalAlignment = HorizontalAlignment.Center;
            Add(grid, label, index);
        }
        return grid;
    }

    private static Grid CreateGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
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
        Style = Application.Current?.TryFindResource("DarkComboBox") as Style
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
        TextBox Prefix, TextBox Start, CheckBox Remove);
    private sealed record Choice(string Label, PdfPageLabelStyle Value)
    {
        public override string ToString() => Label;
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
