using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;

namespace KillerPDF.Controls;

internal sealed class ImageAssetPickerDialog : Window
{
    private readonly ImageAssetStore _store = new();
    private readonly bool _allowVector;
    private readonly ListBox _assets = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _emptyHint;
    private readonly TextBox _name = UiKit.Field();
    private readonly Button _delete;
    private readonly Button _use;
    // The row whose name the Name box is editing, so a rename lands on the right image even when
    // the box loses focus because another row was clicked.
    private ListBoxItem? _editingItem;

    internal string? SelectedPath { get; private set; }
    internal string? SelectedName { get; private set; }

    // allowVector lets the import also pick an SVG file (#398). An SVG is handed straight back
    // rather than kept in the image library, which holds bitmaps.
    internal ImageAssetPickerDialog(Window owner, string? title = null, bool allowVector = false)
    {
        _allowVector = allowVector;
        Title = "KillerPDF - " + (title ?? L("Str_Dlg_InsertImage"));
        Width = 680;
        Height = 480;
        MinWidth = 560;
        MinHeight = 400;
        DialogFrame.Configure(this, owner, resizable: true);

        _assets.SelectionChanged += (_, _) => SelectionChanged();
        _assets.MouseDoubleClick += (_, e) =>
        {
            if (_assets.SelectedItem is ListBoxItem) UseSelected();
            e.Handled = true;
        };
        // Import and Delete are icons on the library's heading, the way the Macros window lists its macros.
        Button import = DialogParts.IconAction("Str_Sig_Import", 0xE710, (_, _) => Import());
        _delete = DialogParts.IconAction("Str_Ctx_BmDelete", 0xE74D, (_, _) => Delete());
        DockPanel libraryHeader = DialogParts.Header(L("Str_Filter_Images"), import, _delete);

        // An empty library says what it is for, rather than showing a blank box.
        _emptyHint = new TextBlock
        {
            Text = L("Str_ImageAsset_Empty"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16),
            IsHitTestVisible = false
        };
        _emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(libraryHeader, Dock.Top);
        left.Children.Add(libraryHeader);
        left.Children.Add(DialogParts.Well(_assets, _emptyHint));

        var previewFrame = new Border
        {
            Padding = new Thickness(12),
            Child = _preview
        };
        previewFrame.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
        previewFrame.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        previewFrame.BorderThickness = new Thickness(1);

        var right = new DockPanel();
        var namePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        namePanel.Children.Add(UiKit.GroupLabel(L("Str_Attachment_Name")));
        namePanel.Children.Add(_name);
        // The name saves on Enter or when the box loses focus; Enter here renames rather than
        // pressing Open.
        _name.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitName();
            e.Handled = true;
        };
        _name.LostKeyboardFocus += (_, _) => CommitName();
        DockPanel.SetDock(namePanel, Dock.Bottom);
        right.Children.Add(namePanel);
        right.Children.Add(UiKit.PaneWithShadow(previewFrame));

        var content = new Grid { Margin = new Thickness(20, 6, 20, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        content.Children.Add(left);
        content.Children.Add(right);

        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => CloseCanceled();
        _use = UiKit.Make(L("Str_Btn_Open"), accent: true);
        _use.IsDefault = true;
        _use.Click += (_, _) => UseSelected();
        var actions = UiKit.ButtonRow(cancel, _use);
        actions.Margin = new Thickness(20, 14, 20, 16);

        var body = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        body.Children.Add(actions);
        body.Children.Add(content);
        Content = DialogFrame.Frame(this, owner, Title, CloseCanceled, body);

        Reload(null);
    }

    private ImageAsset? SelectedAsset => (_assets.SelectedItem as ListBoxItem)?.Tag as ImageAsset;

    private void Reload(string? selectedId)
    {
        _assets.Items.Clear();
        foreach (ImageAsset asset in _store.Load())
        {
            string path = _store.GetPath(asset);
            var thumbnail = new Image
            {
                Source = LoadBitmap(path),
                Width = 44,
                Height = 44,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(thumbnail);
            row.Children.Add(new TextBlock
            {
                Text = asset.Name,
                VerticalAlignment = VerticalAlignment.Center
            });
            var item = new ListBoxItem
            {
                Content = row,
                Tag = asset,
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = Cursors.Hand
            };
            item.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            _assets.Items.Add(item);
            if (string.Equals(asset.Id, selectedId, StringComparison.Ordinal))
                _assets.SelectedItem = item;
        }
        if (_assets.SelectedIndex < 0 && _assets.Items.Count > 0)
            _assets.SelectedIndex = 0;
        _emptyHint.Visibility = _assets.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        CommitName();
        _editingItem = _assets.SelectedItem as ListBoxItem;
        ImageAsset? asset = SelectedAsset;
        BitmapImage? bitmap = asset is null ? null : LoadBitmap(_store.GetPath(asset));
        _delete.IsEnabled = asset is not null;
        _use.IsEnabled = bitmap is not null;
        _name.IsEnabled = asset is not null;
        _name.Text = asset?.Name ?? string.Empty;
        _preview.Source = bitmap;
    }

    private void Import()
    {
        var dialog = new FileDialog(FileDialogMode.Open)
        {
            Title = L("Str_Dlg_InsertImage"),
            Filter = L("Str_Filter_Images") +
                "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif" + (_allowVector ? ";*.svg|" : "|") +
                L("Str_Filter_AllFiles") + "|*.*",
            ShowImagePreview = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_allowVector && string.Equals(Path.GetExtension(dialog.FileName), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            SelectedPath = dialog.FileName;
            SelectedName = Path.GetFileNameWithoutExtension(dialog.FileName);
            DialogResult = true;
            Close();
            return;
        }

        try
        {
            ImageAsset asset = _store.Import(dialog.FileName);
            Reload(asset.Id);
        }
        catch (InvalidDataException)
        {
            SelectedPath = dialog.FileName;
            SelectedName = Path.GetFileNameWithoutExtension(dialog.FileName);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, L("Str_Err_ImportImageFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Saves the Name box onto the row it was editing. The row is updated in place instead of
    // reloading the list, so a click on another row is never lost to a rebuilt list.
    private void CommitName()
    {
        if (_editingItem?.Tag is not ImageAsset asset) return;
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            if (ReferenceEquals(_editingItem, _assets.SelectedItem)) _name.Text = asset.Name;
            return;
        }
        if (string.Equals(name, asset.Name, StringComparison.Ordinal)) return;
        try
        {
            _store.Rename(asset.Id, name);
            _editingItem.Tag = asset with { Name = name };
            if (_editingItem.Content is StackPanel row && row.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
                label.Text = name;
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, ex.Message, "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Delete()
    {
        ImageAsset? asset = SelectedAsset;
        if (asset is null) return;
        if (KillerDialog.Show(this,
                string.Format(L("Str_ImageAsset_DeleteConfirm"), asset.Name), "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            _store.Remove(asset.Id);
            Reload(null);
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, ex.Message, "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UseSelected()
    {
        CommitName();
        ImageAsset? asset = SelectedAsset;
        if (asset is null) return;
        SelectedPath = _store.GetPath(asset);
        SelectedName = asset.Name;
        DialogResult = true;
        Close();
    }

    private void CloseCanceled()
    {
        DialogResult = false;
        Close();
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
