using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class LayerEditorDialog : Window
{
    private readonly List<LayerRow> _rows = [];

    internal IReadOnlyList<PdfEngineIntegration.LayerEdit> Edits { get; private set; } = [];

    internal LayerEditorDialog(
        Window owner, IReadOnlyList<PdfOptionalContentGroupInfo> groups)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_EditLayers");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        var header = CreateGrid();
        AddLabel(header, L("Str_DocumentTools_LayerName"), 0);
        AddLabel(header, L("Str_DocumentTools_LayerVisible"), 1);
        AddLabel(header, L("Str_DocumentTools_LayerLocked"), 2);
        body.Children.Add(header);

        var rows = new StackPanel();
        foreach (PdfOptionalContentGroupInfo group in groups)
        {
            var grid = CreateGrid();
            var name = UiKit.Field();
            name.Text = group.Name;
            name.Margin = new Thickness(0, 3, 10, 3);
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);
            var visible = UiKit.CheckBox(string.Empty);
            visible.IsChecked = group.IsInitiallyVisible;
            visible.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(visible, 1);
            grid.Children.Add(visible);
            var locked = UiKit.CheckBox(string.Empty);
            locked.IsChecked = group.IsLocked;
            locked.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(locked, 2);
            grid.Children.Add(locked);
            rows.Children.Add(grid);
            _rows.Add(new LayerRow(group.ObjectNumber, name, visible, locked));
        }
        body.Children.Add(new ScrollViewer
        {
            Content = rows,
            MaxHeight = 390,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        var buttons = UiKit.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(buttons);

        Content = DialogChrome.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        Loaded += (_, _) => _rows[0].Name.Focus();
    }

    private void Commit()
    {
        string[] names = [.. _rows.Select(row => row.Name.Text.Trim())];
        if (names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(System.StringComparer.Ordinal).Count() != names.Length)
        {
            KillerDialog.Show(this, L("Str_DocumentTools_LayerInvalid"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Edits = _rows.Select((row, index) => new PdfEngineIntegration.LayerEdit(
            row.ObjectNumber, names[index], row.Visible.IsChecked == true,
            row.Locked.IsChecked == true)).ToArray();
        DialogResult = true;
        Close();
    }

    private static Grid CreateGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        return grid;
    }

    private static void AddLabel(Grid grid, string text, int column)
    {
        TextBlock label = UiKit.GroupLabel(text);
        label.HorizontalAlignment = column == 0
            ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private sealed record LayerRow(
        int ObjectNumber, TextBox Name, CheckBox Visible, CheckBox Locked);

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
