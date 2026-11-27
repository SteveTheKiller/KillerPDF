using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;

namespace KillerPDF.Controls;

// Fillable text field presets (#340). Lists the saved presets for renaming, reordering and
// deleting. Opened with a preset to save, the Name box names that new preset instead. Nothing is
// written until Save.
internal sealed class TextFieldPresetsDialog : Window
{
    private readonly PdfTextFieldPreset? _pending;
    private readonly ListBox _list = new();
    private readonly TextBox _name = UiKit.Field();
    private readonly TextBlock _empty;
    private readonly Button _rename;
    private readonly Button _up;
    private readonly Button _down;
    private readonly Button _delete;
    private PdfTextFieldPresetCollection _presets;

    internal bool Saved { get; private set; }
    internal PdfTextFieldPresetCollection Result => _presets;
    /// <summary>The name the new preset was saved under, in save mode.</summary>
    internal string? SavedName { get; private set; }

    internal TextFieldPresetsDialog(Window owner, PdfTextFieldPresetCollection presets,
        PdfTextFieldPreset? pending = null)
    {
        _presets = presets;
        _pending = pending;
        Title = "KillerPDF - " + L(pending is null ? "Str_FieldPreset_Title" : "Str_FieldPreset_SaveTitle");
        Width = 480;
        Height = 420;
        MinWidth = 400;
        MinHeight = 320;
        DialogFrame.Configure(this, owner, resizable: true);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16), LastChildFill = true };

        var hint = new TextBlock
        {
            Text = L("Str_FieldPreset_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var save = UiKit.Make(L("Str_Btn_Save"), accent: true);
        save.IsDefault = pending is not null;
        save.Click += (_, _) => Save();
        DockPanel footer = DialogParts.Footer([cancel, save]);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        var nameRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var nameLabel = new TextBlock
        {
            Text = L("Str_FieldPreset_Name"),
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        DockPanel.SetDock(nameLabel, Dock.Left);
        nameRow.Children.Add(nameLabel);
        _rename = DialogParts.IconAction("Str_Ctx_BmRename", 0xE8AC, (_, _) => RenameSelected());
        _rename.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(_rename, Dock.Right);
        if (pending is null) nameRow.Children.Add(_rename);
        System.Windows.Automation.AutomationProperties.SetName(_name, L("Str_FieldPreset_Name"));
        _name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || pending is not null) return;
            RenameSelected();
            e.Handled = true;
        };
        nameRow.Children.Add(_name);
        DockPanel.SetDock(nameRow, Dock.Bottom);
        body.Children.Add(nameRow);

        // The list's actions are icons on its heading, the way the Macros window lists its macros.
        _up = DialogParts.IconAction("Str_Ctx_BmMoveUp", 0xE74A, (_, _) => MoveSelected(-1));
        _down = DialogParts.IconAction("Str_Ctx_BmMoveDown", 0xE74B, (_, _) => MoveSelected(+1));
        _delete = DialogParts.IconAction("Str_Ctx_BmDelete", 0xE74D, (_, _) => DeleteSelected());
        DockPanel header = DialogParts.Header(L("Str_FieldPreset_Title"), _up, _down, _delete);
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);

        _list.SelectionChanged += (_, _) => SelectionChanged();
        _empty = new TextBlock
        {
            Text = L("Str_FieldPreset_Empty"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(12),
            IsHitTestVisible = false
        };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        body.Children.Add(DialogParts.Well(_list, _empty));

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        if (pending is not null)
            _name.Text = TextFieldPresetCapture.NextName(L("Str_FieldPreset_DefaultName"),
                presets.Presets.Select(preset => preset.Name));
        Reload(null);
        Loaded += (_, _) =>
        {
            if (pending is null) return;
            _name.Focus();
            _name.SelectAll();
        };
    }

    private PdfTextFieldPreset? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as PdfTextFieldPreset;

    private void Reload(string? select)
    {
        _list.Items.Clear();
        foreach (PdfTextFieldPreset preset in _presets.Presets)
        {
            var name = new TextBlock { Text = preset.Name, FontFamily = UiKit.UiFont, FontSize = 12 };
            var detail = new TextBlock
            {
                Text = Detail(preset),
                FontFamily = UiKit.UiFont,
                FontSize = 11
            };
            var detailStyle = new Style(typeof(TextBlock));
            detailStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new DynamicResourceExtension("MutedTextBrush")));
            var selectedDetail = new DataTrigger
            {
                Binding = new Binding(nameof(ListBoxItem.IsSelected))
                { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) },
                Value = true
            };
            // The selected row's own text color, which the dialog's row style picks to suit its fill.
            selectedDetail.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                new Binding(nameof(ListBoxItem.Foreground))
                { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) }));
            detailStyle.Triggers.Add(selectedDetail);
            detail.Style = detailStyle;
            var row = new StackPanel();
            row.Children.Add(name);
            row.Children.Add(detail);
            var item = new ListBoxItem { Content = row, Tag = preset, Padding = new Thickness(8, 5, 8, 5) };
            _list.Items.Add(item);
            if (string.Equals(preset.Name, select, StringComparison.OrdinalIgnoreCase)) _list.SelectedItem = item;
        }
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0 && _pending is null) _list.SelectedIndex = 0;
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionChanged();
    }

    /// <summary>"240 × 24 pt, 12 pt text", in the user's number format.</summary>
    internal static string Detail(PdfTextFieldPreset preset) => string.Format(CultureInfo.CurrentCulture,
        L("Str_FieldPreset_Detail"), Points(preset.Width), Points(preset.Height), Points(preset.FontSize));

    private static string Points(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    private void SelectionChanged()
    {
        PdfTextFieldPreset? selected = Selected;
        int index = _list.SelectedIndex;
        _up.IsEnabled = selected is not null && index > 0;
        _down.IsEnabled = selected is not null && index < _list.Items.Count - 1;
        _delete.IsEnabled = selected is not null;
        _rename.IsEnabled = selected is not null;
        if (_pending is null) _name.Text = selected?.Name ?? "";
        _name.IsEnabled = _pending is not null || selected is not null;
    }

    // False when the name is already used by another preset, which is left as it was.
    private bool RenameSelected()
    {
        if (Selected is not PdfTextFieldPreset selected) return true;
        string name = _name.Text.Trim();
        if (name.Length == 0 || name == selected.Name) return true;
        if (!string.Equals(name, selected.Name, StringComparison.OrdinalIgnoreCase)
            && _presets.Presets.Any(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            KillerDialog.Show(this, string.Format(L("Str_FieldPreset_NameTaken"), name), "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        _presets = _presets.Rename(selected.Name, name);
        Reload(name);
        return true;
    }

    private void MoveSelected(int step)
    {
        int index = _list.SelectedIndex;
        if (Selected is not PdfTextFieldPreset selected || index + step < 0 || index + step >= _list.Items.Count) return;
        _presets = _presets.Move(index, index + step);
        Reload(selected.Name);
    }

    private void DeleteSelected()
    {
        if (Selected is not PdfTextFieldPreset selected) return;
        int index = _list.SelectedIndex;
        _presets = _presets.Remove(selected.Name);
        Reload(null);
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
    }

    private void Save()
    {
        if (_pending is not null)
        {
            string name = _name.Text.Trim();
            if (name.Length == 0)
            {
                _name.Focus();
                return;
            }
            var preset = new PdfTextFieldPreset(name, _pending.Width, _pending.Height,
                _pending.FontSize, _pending.AppearanceStyle);
            PdfTextFieldPreset? existing = _presets.Presets.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (KillerDialog.Show(this, string.Format(L("Str_FieldPreset_Replace"), existing.Name),
                        "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                _presets = _presets.Replace(existing.Name, preset);
            }
            else _presets = _presets.Add(preset);
            SavedName = name;
        }
        else if (!RenameSelected()) return;
        Saved = true;
        Close();
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
