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

    internal LayerEditorDialog(Window owner, PdfOptionalContentInfo layers)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_EditLayers");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        // The column names, then the layers in a well.
        var header = CreateGrid();
        header.Margin = new Thickness(11, 0, 7, 2);
        AddLabel(header, L("Str_DocumentTools_LayerName"), 0);
        AddLabel(header, L("Str_DocumentTools_LayerVisible"), 1);
        AddLabel(header, L("Str_DocumentTools_LayerLocked"), 2);
        body.Children.Add(header);

        var rows = new StackPanel { Margin = new Thickness(8, 5, 4, 5) };
        foreach (PdfOptionalContentGroupInfo group in layers.Groups)
        {
            var grid = CreateGrid();
            var name = UiKit.Field();
            name.Text = group.Name;
            name.Margin = new Thickness(0, 3, 10, 3);
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);
            var visible = UiKit.CheckBox(string.Empty);
            visible.IsChecked = layers.ViewVisibleGroupObjectNumbers.Contains(group.ObjectNumber);
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
        var scroll = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 390,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        body.Children.Add(DialogParts.Well(scroll));

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        body.Children.Add(DialogParts.Footer([cancel, save]));

        Content = DialogFrame.Frame(this, owner, Title,
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

        Edits = [.. _rows.Select((row, index) => new PdfEngineIntegration.LayerEdit(
            row.ObjectNumber, names[index], row.Visible.IsChecked == true,
            row.Locked.IsChecked == true))];
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
