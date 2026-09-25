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
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        var header = CreateGrid();
        AddLabel(header, L("Str_Attachment_Name"), 0);
        AddLabel(header, L("Str_Attachment_Description"), 1);
        AddLabel(header, L("Str_Attachment_MimeType"), 2);
        AddLabel(header, L("Str_Attachment_Relationship"), 3);
        AddLabel(header, L("Str_Attachment_Remove"), 4);
        body.Children.Add(header);

        var rows = new StackPanel();
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
            CheckBox remove = UiKit.CheckBox(string.Empty);
            remove.HorizontalAlignment = HorizontalAlignment.Center;
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.Checked += (_, _) => SetEnabled(false, name, description, mimeType, relationship);
            remove.Unchecked += (_, _) => SetEnabled(true, name, description, mimeType, relationship);

            Add(grid, name, 0);
            Add(grid, description, 1);
            Add(grid, mimeType, 2);
            Add(grid, relationship, 3);
            Add(grid, remove, 4);
            rows.Children.Add(grid);
            _rows.Add(new AttachmentRow(attachment, name, description, mimeType,
                relationship, remove));
        }
        body.Children.Add(new ScrollViewer
        {
            Content = rows,
            MaxHeight = 420,
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
        PdfEngineIntegration.AttachmentEditorItem[] edits = [.. _rows.Select(row =>
            row.Original with
            {
                FileName = row.Name.Text.Trim(),
                Description = EmptyToNull(row.Description.Text),
                MimeType = row.MimeType.Text.Trim(),
                Relationship = (PdfAssociatedFileRelationship)row.Relationship.SelectedItem,
                Remove = row.Remove.IsChecked == true
            })];
        PdfEngineIntegration.AttachmentEditorItem[] remaining =
            [.. edits.Where(edit => !edit.Remove)];
        if (remaining.Any(edit => string.IsNullOrWhiteSpace(edit.FileName)
                || string.IsNullOrWhiteSpace(edit.MimeType))
            || remaining.Select(edit => edit.FileName)
                .Distinct(System.StringComparer.OrdinalIgnoreCase).Count() != remaining.Length)
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
        Style = Application.Current?.TryFindResource("DarkComboBox") as Style
    };

    private static Grid CreateGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
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

    private static void SetEnabled(bool enabled, params Control[] controls)
    {
        foreach (Control control in controls) control.IsEnabled = enabled;
    }

    private sealed record AttachmentRow(
        PdfEngineIntegration.AttachmentEditorItem Original,
        TextBox Name,
        TextBox Description,
        TextBox MimeType,
        ComboBox Relationship,
        CheckBox Remove);

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
