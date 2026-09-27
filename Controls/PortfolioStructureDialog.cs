using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class PortfolioStructureDialog : Window
{
    private static readonly string[] Subtypes =
        ["S", "D", "N", "F", "Desc", "ModDate", "CreationDate", "Size"];

    private readonly List<FieldRow> _fieldRows = [];
    private readonly List<SortRow> _sortRows = [];
    private readonly List<FolderRow> _folderRows = [];
    private readonly List<ValueRow> _valueRows = [];
    private readonly StackPanel _fieldsPanel = new();
    private readonly StackPanel _sortPanel = new();
    private readonly StackPanel _foldersPanel = new();
    private readonly StackPanel _valuesPanel = new();
    private readonly string[] _attachmentNames;

    internal IReadOnlyList<PdfCollectionFieldInfo> Fields { get; private set; } = [];
    internal IReadOnlyList<PdfCollectionSortInfo> Sort { get; private set; } = [];
    internal IReadOnlyList<PdfCollectionFolder> Folders { get; private set; } = [];
    internal IReadOnlyDictionary<string, IReadOnlyList<PdfCollectionItemValue>> ItemValues
        { get; private set; } = new Dictionary<string, IReadOnlyList<PdfCollectionItemValue>>();

    internal PortfolioStructureDialog(
        Window owner, PdfCollectionInfo? collection,
        IReadOnlyList<PdfEngineIntegration.PortfolioAttachmentItem> attachments)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_EditPortfolioStructure");
        Width = 1040;
        Height = 720;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);
        _attachmentNames = [.. attachments.Select(item => item.FileName)];

        var content = new StackPanel();
        AddSection(content, "Str_Portfolio_Fields", FieldHeader(), _fieldsPanel,
            () => AddField(null));
        foreach (PdfCollectionFieldInfo field in collection?.Fields ?? []) AddField(field);

        AddSection(content, "Str_Portfolio_Sort", SortHeader(), _sortPanel,
            () => AddSort(null));
        foreach (PdfCollectionSortInfo rule in collection?.Sort ?? []) AddSort(rule);

        AddSection(content, "Str_Portfolio_Folders", FolderHeader(), _foldersPanel,
            () => AddFolder(null));
        foreach (PdfCollectionFolderInfo folder in collection?.Folders ?? []) AddFolder(folder);

        AddSection(content, "Str_Portfolio_ItemValues", ValueHeader(), _valuesPanel,
            () => AddValue(null, null));
        foreach (PdfEngineIntegration.PortfolioAttachmentItem attachment in attachments)
            foreach (PdfCollectionItemValue value in attachment.Values)
                AddValue(attachment.FileName, value);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var buttons = new StackPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = UiKit.Make(L("Str_DocInfo_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        var row = UiKit.ButtonRow(cancel, save);
        row.Margin = new Thickness(0, 14, 0, 0);
        buttons.Children.Add(row);
        body.Children.Add(buttons);
        body.Children.Add(new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        Content = DialogChrome.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
    }

    private void AddField(PdfCollectionFieldInfo? field)
    {
        var grid = GridWith([150, 220, 120, 80, 74, 74, 74]);
        TextBox key = Field(field?.Key ?? $"field{_fieldRows.Count + 1}");
        TextBox name = Field(field?.DisplayName ?? L("Str_Portfolio_CustomField"));
        ComboBox subtype = Combo(Subtypes);
        subtype.SelectedItem = field?.Subtype is string value && Subtypes.Contains(value)
            ? value : "S";
        TextBox order = Field(field?.Order?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
        CheckBox visible = Check(field?.IsVisible ?? true);
        CheckBox editable = Check(field?.IsEditable ?? true);
        CheckBox remove = Check(false);
        Add(grid, key, 0); Add(grid, name, 1); Add(grid, subtype, 2); Add(grid, order, 3);
        Add(grid, visible, 4); Add(grid, editable, 5); Add(grid, remove, 6);
        _fieldsPanel.Children.Add(grid);
        _fieldRows.Add(new(key, name, subtype, order, visible, editable, remove));
    }

    private void AddSort(PdfCollectionSortInfo? rule)
    {
        var grid = GridWith([300, 120, 74]);
        TextBox key = Field(rule?.Key ?? string.Empty);
        CheckBox ascending = Check(rule?.Ascending ?? true);
        CheckBox remove = Check(false);
        Add(grid, key, 0); Add(grid, ascending, 1); Add(grid, remove, 2);
        _sortPanel.Children.Add(grid);
        _sortRows.Add(new(key, ascending, remove));
    }

    private void AddFolder(PdfCollectionFolderInfo? folder)
    {
        var grid = GridWith([90, 220, 100, 0, 74]);
        long nextId = _folderRows.Count == 0 ? 1 : _folderRows
            .Select(row => long.TryParse(row.Id.Text, out long id) ? id : 0).Max() + 1;
        TextBox id = Field((folder?.Id ?? nextId).ToString(CultureInfo.CurrentCulture));
        TextBox name = Field(folder?.Name ?? L("Str_Portfolio_NewFolder"));
        TextBox parent = Field(folder?.ParentId?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
        TextBox description = Field(folder?.Description ?? string.Empty);
        CheckBox remove = Check(false);
        Add(grid, id, 0); Add(grid, name, 1); Add(grid, parent, 2);
        Add(grid, description, 3); Add(grid, remove, 4);
        _foldersPanel.Children.Add(grid);
        _folderRows.Add(new(id, name, parent, description, remove));
    }

    private void AddValue(string? attachmentName, PdfCollectionItemValue? value)
    {
        if (_attachmentNames.Length == 0) return;
        var grid = GridWith([190, 150, 0, 80, 130, 74]);
        ComboBox attachment = Combo(_attachmentNames);
        attachment.SelectedItem = attachmentName is not null
            && _attachmentNames.Contains(attachmentName, StringComparer.Ordinal)
                ? attachmentName : _attachmentNames[0];
        TextBox key = Field(value?.Key ?? string.Empty);
        bool isNumber = value?.Number.HasValue == true;
        TextBox data = Field(isNumber
            ? value!.Number!.Value.ToString("R", CultureInfo.CurrentCulture)
            : value?.Text ?? string.Empty);
        CheckBox number = Check(isNumber);
        TextBox prefix = Field(value?.Prefix ?? string.Empty);
        CheckBox remove = Check(false);
        Add(grid, attachment, 0); Add(grid, key, 1); Add(grid, data, 2);
        Add(grid, number, 3); Add(grid, prefix, 4); Add(grid, remove, 5);
        _valuesPanel.Children.Add(grid);
        _valueRows.Add(new(attachment, key, data, number, prefix, remove));
    }

    private void Commit()
    {
        try
        {
            PdfCollectionFieldInfo[] fields = [.. _fieldRows
                .Where(row => row.Remove.IsChecked != true)
                .Select(row => new PdfCollectionFieldInfo
                {
                    Key = Required(row.Key.Text),
                    DisplayName = Required(row.Name.Text),
                    Subtype = (string)row.Subtype.SelectedItem,
                    Order = OptionalInt(row.Order.Text),
                    IsVisible = row.Visible.IsChecked == true,
                    IsEditable = row.Editable.IsChecked == true
                })];
            if (fields.Length == 0
                || fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count()
                    != fields.Length)
                throw new FormatException();
            HashSet<string> fieldKeys = fields.Select(field => field.Key)
                .ToHashSet(StringComparer.Ordinal);

            PdfCollectionSortInfo[] sort = [.. _sortRows
                .Where(row => row.Remove.IsChecked != true)
                .Select(row => new PdfCollectionSortInfo(
                    Required(row.Key.Text), row.Ascending.IsChecked == true))];
            if (sort.Any(rule => !fieldKeys.Contains(rule.Key))
                || sort.Select(rule => rule.Key).Distinct(StringComparer.Ordinal).Count()
                    != sort.Length)
                throw new FormatException();

            PdfCollectionFolder[] folders = [.. _folderRows
                .Where(row => row.Remove.IsChecked != true)
                .Select(row => new PdfCollectionFolder(
                    RequiredLong(row.Id.Text), Required(row.Name.Text),
                    OptionalLong(row.Parent.Text), EmptyToNull(row.Description.Text)))];
            if (folders.Select(folder => folder.Id).Distinct().Count() != folders.Length)
                throw new FormatException();
            var seenFolders = new HashSet<long>();
            foreach (PdfCollectionFolder folder in folders)
            {
                if (folder.Id < 0 || folder.ParentId.HasValue
                    && !seenFolders.Contains(folder.ParentId.Value))
                    throw new FormatException();
                seenFolders.Add(folder.Id);
            }

            var values = _attachmentNames.ToDictionary(
                name => name,
                name => (IReadOnlyList<PdfCollectionItemValue>)[.. _valueRows
                    .Where(row => row.Remove.IsChecked != true
                        && string.Equals(row.Attachment.SelectedItem as string, name,
                            StringComparison.Ordinal))
                    .Select(row => BuildValue(row, fieldKeys))],
                StringComparer.Ordinal);
            if (values.Values.Any(items => items.Select(item => item.Key)
                    .Distinct(StringComparer.Ordinal).Count() != items.Count))
                throw new FormatException();

            Fields = fields;
            Sort = sort;
            Folders = folders;
            ItemValues = values;
            DialogResult = true;
            Close();
        }
        catch (FormatException)
        {
            KillerDialog.Show(this, L("Str_Portfolio_StructureInvalid"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static PdfCollectionItemValue BuildValue(ValueRow row, HashSet<string> fieldKeys)
    {
        string key = Required(row.Key.Text);
        if (!fieldKeys.Contains(key)) throw new FormatException();
        string data = Required(row.Value.Text);
        string? prefix = EmptyToNull(row.Prefix.Text);
        if (row.Number.IsChecked != true)
            return new PdfCollectionItemValue(key, data, null, prefix);
        if (!double.TryParse(data, NumberStyles.Float, CultureInfo.CurrentCulture,
                out double number) || !double.IsFinite(number))
            throw new FormatException();
        return new PdfCollectionItemValue(key, null, number, prefix);
    }

    private static void AddSection(
        StackPanel body, string titleKey, Grid header, StackPanel rows, Action addRow)
    {
        TextBlock title = UiKit.GroupLabel(L(titleKey));
        title.Margin = new Thickness(0, 12, 0, 4);
        body.Children.Add(title);
        body.Children.Add(header);
        body.Children.Add(rows);
        Button add = UiKit.Make(L("Str_Portfolio_AddRow"), accent: false);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 4, 0, 2);
        add.Click += (_, _) => addRow();
        body.Children.Add(add);
    }

    private static Grid FieldHeader() => Header(
        [150, 220, 120, 80, 74, 74, 74],
        ["Str_Portfolio_Key", "Str_Portfolio_DisplayName", "Str_Portfolio_Type",
         "Str_Portfolio_Order", "Str_Portfolio_Visible", "Str_Portfolio_Editable",
         "Str_Portfolio_Remove"]);

    private static Grid SortHeader() => Header([300, 120, 74],
        ["Str_Portfolio_Key", "Str_Portfolio_Ascending", "Str_Portfolio_Remove"]);

    private static Grid FolderHeader() => Header([90, 220, 100, 0, 74],
        ["Str_Portfolio_Id", "Str_Attachment_Name", "Str_Portfolio_Parent",
         "Str_Attachment_Description", "Str_Portfolio_Remove"]);

    private static Grid ValueHeader() => Header([190, 150, 0, 80, 130, 74],
        ["Str_Portfolio_Attachment", "Str_Portfolio_Key", "Str_Portfolio_Value",
         "Str_Portfolio_Number", "Str_Portfolio_Prefix", "Str_Portfolio_Remove"]);

    private static Grid Header(double[] widths, string[] labels)
    {
        Grid grid = GridWith(widths);
        for (int index = 0; index < labels.Length; index++)
        {
            TextBlock label = UiKit.GroupLabel(L(labels[index]));
            label.HorizontalAlignment = index == labels.Length - 1
                ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            Add(grid, label, index);
        }
        return grid;
    }

    private static Grid GridWith(double[] widths)
    {
        var grid = new Grid();
        foreach (double width in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = width == 0 ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(width)
            });
        return grid;
    }

    private static TextBox Field(string value)
    {
        TextBox field = UiKit.Field();
        field.Text = value;
        field.Margin = new Thickness(0, 2, 8, 2);
        return field;
    }

    private static ComboBox Combo(System.Collections.IEnumerable values) => new()
    {
        Height = 28,
        Margin = new Thickness(0, 2, 8, 2),
        ItemsSource = values,
        Style = Application.Current?.TryFindResource("DarkComboBox") as Style
    };

    private static CheckBox Check(bool value) => new()
    {
        IsChecked = value,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static void Add(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static string Required(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? throw new FormatException() : trimmed;
    }

    private static string? EmptyToNull(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static int? OptionalInt(string value) => string.IsNullOrWhiteSpace(value)
        ? null : int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture,
            out int number) ? number : throw new FormatException();

    private static long RequiredLong(string value) => long.TryParse(
        value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long number)
            ? number : throw new FormatException();

    private static long? OptionalLong(string value) => string.IsNullOrWhiteSpace(value)
        ? null : RequiredLong(value);

    private sealed record FieldRow(TextBox Key, TextBox Name, ComboBox Subtype,
        TextBox Order, CheckBox Visible, CheckBox Editable, CheckBox Remove);
    private sealed record SortRow(TextBox Key, CheckBox Ascending, CheckBox Remove);
    private sealed record FolderRow(TextBox Id, TextBox Name, TextBox Parent,
        TextBox Description, CheckBox Remove);
    private sealed record ValueRow(ComboBox Attachment, TextBox Key, TextBox Value,
        CheckBox Number, TextBox Prefix, CheckBox Remove);

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
