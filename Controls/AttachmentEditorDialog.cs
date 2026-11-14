using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class AttachmentEditorDialog : Window
{
    private readonly List<AttachmentRow> _rows = [];

    internal IReadOnlyList<PdfEngineIntegration.AttachmentEditorItem> Edits { get; private set; } = [];

    internal AttachmentEditorDialog(
        Window owner, IReadOnlyList<PdfEngineIntegration.AttachmentEditorItem> attachments)
    {
        Title = "KillerPDF - " + L("Str_DocumentTools_EditAttachments");
        Width = 940;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 680;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        // The column names over the well; the remove column needs none, its icons name themselves.
        var header = CreateGrid();
        header.Margin = new Thickness(11, 0, 7, 2);
        AddLabel(header, L("Str_Attachment_Name"), 0);
        AddLabel(header, L("Str_Attachment_Description"), 1);
        AddLabel(header, L("Str_Attachment_MimeType"), 2);
        AddLabel(header, L("Str_Attachment_Relationship"), 3);
        body.Children.Add(header);

        var rows = new StackPanel { Margin = new Thickness(8, 5, 4, 5) };
        TextBlock empty = DialogParts.EmptyHint("Str_Attachment_None");
        empty.Visibility = Visibility.Collapsed;
        foreach (PdfEngineIntegration.AttachmentEditorItem attachment in attachments)
        {
            var grid = CreateGrid();
            TextBox name = Field(attachment.FileName, new Thickness(0, 3, 8, 3));
            TextBox description = Field(attachment.Description ?? string.Empty,
                new Thickness(0, 3, 8, 3));
            TextBox mimeType = Field(attachment.MimeType, new Thickness(0, 3, 8, 3));
            ComboBox relationship = Combo();
            relationship.ItemsSource = System.Enum.GetValues<PdfAssociatedFileRelationship>();
            relationship.SelectedItem = attachment.Relationship;
            relationship.Margin = new Thickness(0, 3, 8, 3);
            var row = new AttachmentRow(attachment, name, description, mimeType, relationship);
            // The remove icon takes the row out of the list; Save still reports it as removed, as the
            // checked Remove box did, and Cancel keeps every attachment.
            Button remove = DialogParts.IconAction("Str_Attachment_Remove", 0xE74D, (_, _) =>
            {
                row.Remove = true;
                grid.Visibility = Visibility.Collapsed;
                if (_rows.All(item => item.Remove)) empty.Visibility = Visibility.Visible;
            });
            remove.HorizontalAlignment = HorizontalAlignment.Center;

            Add(grid, name, 0);
            Add(grid, description, 1);
            Add(grid, mimeType, 2);
            Add(grid, relationship, 3);
            Add(grid, remove, 4);
            rows.Children.Add(grid);
            _rows.Add(row);
        }
        body.Children.Add(DialogParts.Well(new ScrollViewer
        {
            Content = rows,
            MaxHeight = 420,
            MinHeight = 44,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        }, empty));

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
        PdfEngineIntegration.AttachmentEditorItem[] edits = [.. _rows.Select(row =>
            row.Original with
            {
                FileName = row.Name.Text.Trim(),
                Description = EmptyToNull(row.Description.Text),
                MimeType = row.MimeType.Text.Trim(),
                Relationship = (PdfAssociatedFileRelationship)row.Relationship.SelectedItem,
                Remove = row.Remove
            })];
        PdfEngineIntegration.AttachmentEditorItem[] remaining =
            [.. edits.Where(edit => !edit.Remove)];
        if (remaining.Any(edit => string.IsNullOrWhiteSpace(edit.FileName)
                || string.IsNullOrWhiteSpace(edit.MimeType))
            // A name is checked only where it changed, so a file that shares a name with another can
            // still have its description or type edited.
            || remaining.Any(edit => !string.Equals(edit.FileName, edit.OriginalFileName, System.StringComparison.Ordinal)
                && remaining.Any(other => !ReferenceEquals(other, edit)
                    && string.Equals(other.FileName, edit.FileName, System.StringComparison.OrdinalIgnoreCase))))
        {
            KillerDialog.Show(this, L("Str_Attachment_Invalid"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Edits = edits;
        DialogResult = true;
        Close();
    }

    private static string? EmptyToNull(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static TextBox Field(string value, Thickness margin)
    {
        TextBox field = UiKit.Field();
        field.Text = value;
        field.Margin = margin;
        return field;
    }

    private static ComboBox Combo() => new()
    {
        Height = 28,
        Style = UiKit.DarkComboStyle
    };

    private static Grid CreateGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        return grid;
    }

    private static void AddLabel(Grid grid, string text, int column)
    {
        TextBlock label = UiKit.GroupLabel(text);
        label.HorizontalAlignment = column < 4
            ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        Add(grid, label, column);
    }

    private static void Add(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private sealed class AttachmentRow(
        PdfEngineIntegration.AttachmentEditorItem original,
        TextBox name,
        TextBox description,
        TextBox mimeType,
        ComboBox relationship)
    {
        public PdfEngineIntegration.AttachmentEditorItem Original { get; } = original;
        public TextBox Name { get; } = name;
        public TextBox Description { get; } = description;
        public TextBox MimeType { get; } = mimeType;
        public ComboBox Relationship { get; } = relationship;
        public bool Remove { get; set; }
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
