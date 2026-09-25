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
    private readonly ListBox _assets = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _name = UiKit.Field();
    private readonly Button _rename;
    private readonly Button _delete;
    private readonly Button _use;

    internal string? SelectedPath { get; private set; }
    internal string? SelectedName { get; private set; }

    internal ImageAssetPickerDialog(Window owner)
    {
        Title = "KillerPDF - " + L("Str_Dlg_InsertImage");
        Width = 680;
        Height = 480;
        MinWidth = 560;
        MinHeight = 400;
        DialogChrome.Configure(this, owner, resizable: true);

        _assets.SelectionChanged += (_, _) => SelectionChanged();
        _assets.MouseDoubleClick += (_, e) =>
        {
            if (_assets.SelectedItem is ListBoxItem) UseSelected();
            e.Handled = true;
        };
        _assets.SetResourceReference(Control.BackgroundProperty, "PaneBrush");
        _assets.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _assets.SetResourceReference(Control.BorderBrushProperty, "CardBorderBrush");

        var import = UiKit.Make(L("Str_Sig_Import"), accent: false);
        import.Click += (_, _) => Import();
        _rename = UiKit.Make(L("Str_Ctx_BmRename"), accent: false);
        _rename.Click += (_, _) => Rename();
        _delete = UiKit.Make(L("Str_Ctx_BmDelete"), accent: false);
        _delete.Click += (_, _) => Delete();

        var libraryButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        libraryButtons.Children.Add(import);
        _rename.Margin = new Thickness(8, 0, 0, 0);
        libraryButtons.Children.Add(_rename);
        _delete.Margin = new Thickness(8, 0, 0, 0);
        libraryButtons.Children.Add(_delete);

        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(libraryButtons, Dock.Bottom);
        left.Children.Add(libraryButtons);
        left.Children.Add(_assets);

        var previewFrame = new Border
        {
            Padding = new Thickness(12),
            Child = _preview
        };
        previewFrame.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
        previewFrame.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        previewFrame.SetResourceReference(Border.BorderThicknessProperty, "PaneBorderThickness");

        var right = new DockPanel();
        var namePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        namePanel.Children.Add(UiKit.GroupLabel(L("Str_DocInfo_Title")));
        namePanel.Children.Add(_name);
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
        Content = DialogChrome.Frame(this, owner, Title, CloseCanceled, body);

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
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        ImageAsset? asset = SelectedAsset;
        BitmapImage? bitmap = asset is null ? null : LoadBitmap(_store.GetPath(asset));
        _rename.IsEnabled = asset is not null;
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
                "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|" +
                L("Str_Filter_AllFiles") + "|*.*",
            ShowImagePreview = true
        };
        if (dialog.ShowDialog(this) != true) return;

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

    private void Rename()
    {
        ImageAsset? asset = SelectedAsset;
        string name = _name.Text.Trim();
        if (asset is null || name.Length == 0) return;
        try
        {
            _store.Rename(asset.Id, name);
            Reload(asset.Id);
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
