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
        // As tall as the sections need, up to most of the screen, where they start scrolling.
        Width = Math.Min(1100, SystemParameters.WorkArea.Width - 40);
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.85);
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        _attachmentNames = [.. attachments.Select(item => item.FileName)];

        // Fields and Sort rules side by side, Folders and Attachment values full width under them, each a well
        // with its column names and an Add icon in its heading.
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(63, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(37, GridUnitType.Star) });
        UIElement fields = Section("Str_Portfolio_Fields", FieldHeader(), _fieldsPanel, () => AddField(null));
        foreach (PdfCollectionFieldInfo field in collection?.Fields ?? []) AddField(field);
        top.Children.Add(fields);
        UIElement sort = Section("Str_Portfolio_Sort", SortHeader(), _sortPanel, () => AddSort(null));
        foreach (PdfCollectionSortInfo rule in collection?.Sort ?? []) AddSort(rule);
        Grid.SetColumn(sort, 2);
        top.Children.Add(sort);

        var content = new StackPanel();
        content.Children.Add(top);
        content.Children.Add(Section("Str_Portfolio_Folders", FolderHeader(), _foldersPanel,
            () => AddFolder(null)));
        foreach (PdfCollectionFolderInfo folder in collection?.Folders ?? []) AddFolder(folder);

        content.Children.Add(Section("Str_Portfolio_ItemValues", ValueHeader(), _valuesPanel,
            () => AddValue(null, null), canAdd: _attachmentNames.Length > 0));
        foreach (PdfEngineIntegration.PortfolioAttachmentItem attachment in attachments)
            foreach (PdfCollectionItemValue value in attachment.Values)
                AddValue(attachment.FileName, value);

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
        body.Children.Add(new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        Content = DialogFrame.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
    }

    private static readonly double[] FieldWidths = [130, 0, 110, 60, 64, 68, 36];
    private static readonly double[] SortWidths = [0, 90, 36];
    private static readonly double[] FolderWidths = [80, 220, 100, 0, 36];
    private static readonly double[] ValueWidths = [220, 150, 0, 80, 130, 36];

    // New rows take names and IDs no earlier row used, removed ones included, as when removed rows stayed
    // in the list with their Remove box checked.
    private int _fieldsAdded;
    private readonly List<long> _removedFolderIds = [];

    private void AddField(PdfCollectionFieldInfo? field)
    {
        _fieldsAdded++;
        var grid = GridWith(FieldWidths);
        TextBox key = Field(field?.Key ?? $"field{_fieldsAdded}");
        TextBox name = Field(field?.DisplayName ?? L("Str_Portfolio_CustomField"));
        ComboBox subtype = Combo(Subtypes);
        subtype.SelectedItem = field?.Subtype is string value && Subtypes.Contains(value)
            ? value : "S";
        TextBox order = Field(field?.Order?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
        CheckBox visible = Check(field?.IsVisible ?? true);
        CheckBox editable = Check(field?.IsEditable ?? true);
        FieldRow row = new(key, name, subtype, order, visible, editable);
        Button remove = RemoveIcon(() => { _fieldRows.Remove(row); _fieldsPanel.Children.Remove(grid); });
        Add(grid, key, 0); Add(grid, name, 1); Add(grid, subtype, 2); Add(grid, order, 3);
        Add(grid, visible, 4); Add(grid, editable, 5); Add(grid, remove, 6);
        _fieldsPanel.Children.Add(grid);
        _fieldRows.Add(row);
    }

    private void AddSort(PdfCollectionSortInfo? rule)
    {
        var grid = GridWith(SortWidths);
        TextBox key = Field(rule?.Key ?? string.Empty);
        CheckBox ascending = Check(rule?.Ascending ?? true);
        SortRow row = new(key, ascending);
        Button remove = RemoveIcon(() => { _sortRows.Remove(row); _sortPanel.Children.Remove(grid); });
        Add(grid, key, 0); Add(grid, ascending, 1); Add(grid, remove, 2);
        _sortPanel.Children.Add(grid);
        _sortRows.Add(row);
    }

    private void AddFolder(PdfCollectionFolderInfo? folder)
    {
        var grid = GridWith(FolderWidths);
        long nextId = _folderRows.Count == 0 && _removedFolderIds.Count == 0 ? 1 : _folderRows
            .Select(row => long.TryParse(row.Id.Text, out long id) ? id : 0)
            .Concat(_removedFolderIds).Max() + 1;
        TextBox id = Field((folder?.Id ?? nextId).ToString(CultureInfo.CurrentCulture));
        TextBox name = Field(folder?.Name ?? L("Str_Portfolio_NewFolder"));
        TextBox parent = Field(folder?.ParentId?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
        TextBox description = Field(folder?.Description ?? string.Empty);
        FolderRow row = new(id, name, parent, description);
        Button remove = RemoveIcon(() =>
        {
            _removedFolderIds.Add(long.TryParse(id.Text, out long removed) ? removed : 0);
            _folderRows.Remove(row);
            _foldersPanel.Children.Remove(grid);
        });
        Add(grid, id, 0); Add(grid, name, 1); Add(grid, parent, 2);
        Add(grid, description, 3); Add(grid, remove, 4);
        _foldersPanel.Children.Add(grid);
        _folderRows.Add(row);
    }

    private void AddValue(string? attachmentName, PdfCollectionItemValue? value)
    {
        if (_attachmentNames.Length == 0) return;
        var grid = GridWith(ValueWidths);
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
        ValueRow row = new(attachment, key, data, number, prefix);
        Button remove = RemoveIcon(() => { _valueRows.Remove(row); _valuesPanel.Children.Remove(grid); });
        Add(grid, attachment, 0); Add(grid, key, 1); Add(grid, data, 2);
        Add(grid, number, 3); Add(grid, prefix, 4); Add(grid, remove, 5);
        _valuesPanel.Children.Add(grid);
        _valueRows.Add(row);
    }

    // A row's remove icon drops the row from what Save writes, as its Remove check box did.
    private static Button RemoveIcon(Action remove)
    {
        Button button = DialogParts.IconAction("Str_Portfolio_Remove", 0xE711, (_, _) => remove());
        button.HorizontalAlignment = HorizontalAlignment.Center;
        return button;
    }

    private void Commit()
    {
        try
        {
            PdfCollectionFieldInfo[] fields = [.. _fieldRows
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
                .Select(row => new PdfCollectionSortInfo(
                    Required(row.Key.Text), row.Ascending.IsChecked == true))];
            if (sort.Any(rule => !fieldKeys.Contains(rule.Key))
                || sort.Select(rule => rule.Key).Distinct(StringComparer.Ordinal).Count()
                    != sort.Length)
                throw new FormatException();

            PdfCollectionFolder[] folders = [.. _folderRows
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
                    .Where(row => string.Equals(row.Attachment.SelectedItem as string, name,
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

    // A section: its title with an Add icon, then its column names and rows in a well.
    private static DockPanel Section(string titleKey, Grid header, StackPanel rows, Action addRow,
        bool canAdd = true)
    {
        Button add = DialogParts.IconAction("Str_Portfolio_AddRow", 0xE710, (_, _) => addRow());
        add.IsEnabled = canAdd;
        var section = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel heading = DialogParts.Header(L(titleKey), add);
        DockPanel.SetDock(heading, Dock.Top);
        section.Children.Add(heading);
        var inside = new StackPanel { Margin = new Thickness(8, 6, 4, 6) };
        inside.Children.Add(header);
        inside.Children.Add(rows);
        section.Children.Add(DialogParts.Well(inside));
        return section;
    }

    private static Grid FieldHeader() => Header(FieldWidths,
        ["Str_Portfolio_Key", "Str_Portfolio_DisplayName", "Str_Portfolio_Type",
         "Str_Portfolio_Order", "Str_Portfolio_Visible", "Str_Portfolio_Editable"]);

    private static Grid SortHeader() => Header(SortWidths,
        ["Str_Portfolio_Key", "Str_Portfolio_Ascending"]);

    private static Grid FolderHeader() => Header(FolderWidths,
        ["Str_Portfolio_Id", "Str_Attachment_Name", "Str_Portfolio_Parent",
         "Str_Attachment_Description"]);

    private static Grid ValueHeader() => Header(ValueWidths,
        ["Str_Portfolio_Attachment", "Str_Portfolio_Key", "Str_Portfolio_Value",
         "Str_Portfolio_Number", "Str_Portfolio_Prefix"]);

    // Column names; check box columns are centered over their boxes. The remove column has no name, since
    // each row's remove icon says what it does.
    private static Grid Header(double[] widths, string[] labels)
    {
        Grid grid = GridWith(widths);
        for (int index = 0; index < labels.Length; index++)
        {
            TextBlock label = UiKit.GroupLabel(L(labels[index]));
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.HorizontalAlignment = labels[index] is "Str_Portfolio_Visible" or "Str_Portfolio_Editable"
                or "Str_Portfolio_Ascending" or "Str_Portfolio_Number"
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
        Style = UiKit.DarkComboStyle
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
        TextBox Order, CheckBox Visible, CheckBox Editable);
    private sealed record SortRow(TextBox Key, CheckBox Ascending);
    private sealed record FolderRow(TextBox Id, TextBox Name, TextBox Parent,
        TextBox Description);
    private sealed record ValueRow(ComboBox Attachment, TextBox Key, TextBox Value,
        CheckBox Number, TextBox Prefix);

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
