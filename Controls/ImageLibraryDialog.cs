using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;

namespace KillerPDF;

// #326: the shared image-asset picker, also the library manager. Callers (signature
// picker, stamp watermark, future comments) pick an asset and consume it their own way;
// import and delete live here so there is one place to manage. Thumbnails decode lazily
// per refresh; a corrupt entry is skipped, never fatal.
internal sealed class ImageLibraryDialog : Window
{
    private readonly ImageAssetLibrary _library;

    public ImageAsset? Selected { get; private set; }

    private readonly StackPanel _list = new();

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;

    internal ImageLibraryDialog(Window owner, ImageAssetLibrary library)
    {
        _library = library;
        Owner = owner;
        Title = "KillerPDF - " + L("Str_Lib_Title");
        Width = 500;
        MinWidth = 400;
        MaxHeight = 620;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(new TextBlock
        {
            Text = L("Str_Lib_Hint"),
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var scroll = new ScrollViewer
        {
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
            Margin = new Thickness(0, 0, 0, 8)
        };
        body.Children.Add(scroll);

        var importBtn = UiKit.Make(L("Str_Lib_Import"), accent: true);
        importBtn.Click += (_, _) => ImportImage();
        var closeBtn = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        closeBtn.IsCancel = true;
        closeBtn.Click += (_, _) => { DialogResult = false; Close(); };
        body.Children.Add(UiKit.ButtonRow(closeBtn, importBtn));

        Content = DialogChrome.Frame(this, owner, Title,
            () => { DialogResult = false; Close(); }, body);
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        var assets = _library.Assets;
        if (assets.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = L("Str_Lib_Empty"),
                Foreground = UiKit.Brush("MutedTextBrush"),
                FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 16)
            });
            return;
        }
        foreach (var asset in assets)
        {
            var captured = asset;
            BitmapImage? thumb = null;
            try
            {
                var bytes = Convert.FromBase64String(captured.ImageData);
                thumb = new BitmapImage();
                thumb.BeginInit();
                thumb.CacheOption = BitmapCacheOption.OnLoad;
                thumb.StreamSource = new MemoryStream(bytes);
                thumb.EndInit();
                thumb.Freeze();
            }
            catch { /* corrupt entry: row still shows with its name */ }

            var row = new Grid { Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var img = new Image
            {
                Width = 64, Height = 64, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = captured.Name,
            };
            if (thumb is not null) img.Source = thumb;
            Grid.SetColumn(img, 0);
            row.Children.Add(img);

            var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            namePanel.Children.Add(new TextBlock
            {
                Text = captured.Name,
                Foreground = UiKit.Brush("TextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            namePanel.Children.Add(new TextBlock
            {
                Text = $"{captured.Width} x {captured.Height}",
                Foreground = UiKit.Brush("MutedTextBrush"),
                FontSize = 11,
            });
            Grid.SetColumn(namePanel, 1);
            row.Children.Add(namePanel);

            var useBtn = UiKit.Make(L("Str_Lib_Use"), accent: false);
            useBtn.Padding = new Thickness(14, 4, 14, 4);
            useBtn.VerticalAlignment = VerticalAlignment.Center;
            useBtn.Click += (_, _) => Pick(captured);
            Grid.SetColumn(useBtn, 2);
            row.Children.Add(useBtn);

            var del = new TextBlock
            {
                Text = "×",
                FontSize = 16,
                Foreground = UiKit.Brush("MutedTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 2, 0),
                Cursor = Cursors.Hand,
            };
            del.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _library.Remove(captured);
                _library.Persist();
                Refresh();
            };
            Grid.SetColumn(del, 3);
            row.Children.Add(del);

            row.MouseLeftButtonDown += (_, _) => Pick(captured);
            _list.Children.Add(row);
        }
    }

    private void Pick(ImageAsset asset)
    {
        Selected = asset;
        DialogResult = true;
        Close();
    }

    private void ImportImage()
    {
        var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Filter = L("Str_Filter_Images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif|" + L("Str_Filter_AllFiles") + "|*.*",
            Title = L("Str_Sign_ImportImage"),
            ShowImagePreview = true
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bmp = new BitmapImage(new Uri(dlg.FileName));
            byte[] pngBytes;
            using (var ms = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                encoder.Save(ms);
                pngBytes = ms.ToArray();
            }
            _library.Add(
                System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                Convert.ToBase64String(pngBytes),
                bmp.PixelWidth, bmp.PixelHeight);
            _library.Persist();
            Refresh();
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, L("Str_Err_ImportImageFailed") + "\n" + ex.Message, "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
