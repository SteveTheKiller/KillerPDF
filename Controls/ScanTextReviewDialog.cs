using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Features;

namespace KillerPDF.Controls;

// Shows each word Convert Scan to Text is about to set as text beside the same word as scanned,
// least certain first, so a word can be kept as scanned before the file is written.
internal sealed class ScanTextReviewDialog : Window
{
    private readonly List<Row> _rows;
    private readonly TextBlock _count;

    internal ScanTextReviewDialog(Window owner, IReadOnlyList<OcrController.ScanTextReviewWord> words)
    {
        Title = "KillerPDF - " + L("Str_ScanReview_Title");
        Width = 620;
        Height = 560;
        MinWidth = 480;
        MinHeight = 360;
        DialogFrame.Configure(this, owner, resizable: true);

        _rows = [.. words.OrderBy(word => double.IsNaN(word.Word.Match) ? 1 : word.Word.Match)
            .ThenBy(word => word.PageIndex).Select(word => new Row(word))];
        foreach (Row row in _rows) row.PropertyChanged += (_, _) => UpdateCount();

        var hint = new TextBlock
        {
            Text = L("Str_ScanReview_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        // The headings line up over the scan and text columns: the well pads the list by 3 DIP where its
        // old border was 1, so they sit 2 DIP further in than before.
        var header = new Grid { Margin = new Thickness(36, 0, 8, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock scanHeader = UiKit.GroupLabel(L("Str_ScanReview_Scan"));
        TextBlock textHeader = UiKit.GroupLabel(L("Str_ScanReview_Text"));
        Grid.SetColumn(textHeader, 1);
        header.Children.Add(scanHeader);
        header.Children.Add(textHeader);

        var list = new ListBox
        {
            ItemsSource = _rows,
            ItemTemplate = RowTemplate(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            SelectionMode = SelectionMode.Single
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);

        _count = UiKit.GroupLabel("");
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(0, 0, 12, 0);
        var all = UiKit.LinkLabel(L("Str_Ctx_SelectAll"), () => SetAll(true));
        all.Margin = new Thickness(0, 0, 12, 0);
        var none = UiKit.LinkLabel(L("Str_Lbl_Clear"), () => SetAll(false));
        none.Margin = new Thickness(0, 0, 12, 0);

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; };
        var save = UiKit.Make(L("Str_Btn_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) =>
        {
            foreach (Row row in _rows) row.Word.UseText = row.UseText;
            DialogResult = true;
        };
        // One footer row: the count and Select all / Clear on the left, Cancel and Save on the right.
        var footer = DialogParts.Footer([cancel, save], _count, all, none);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(hint);
        body.Children.Add(header);
        body.Children.Add(footer);
        body.Children.Add(DialogParts.Well(list));

        Content = DialogFrame.Frame(this, owner, Title, () => DialogResult = false, body);
        UpdateCount();
    }

    private void SetAll(bool useText)
    {
        foreach (Row row in _rows) row.UseText = useText;
    }

    private void UpdateCount() =>
        _count.Text = string.Format(L("Str_ScanReview_Count"), _rows.Count(row => row.UseText), _rows.Count);

    // A row: the check box, the word as scanned, and the word as text, each image at its scanned
    // size or smaller, with the recognized word underneath for words too faint to read.
    private static DataTemplate RowTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        var check = new FrameworkElementFactory(typeof(ColumnDefinition));
        check.SetValue(ColumnDefinition.WidthProperty, new GridLength(28));
        var scan = new FrameworkElementFactory(typeof(ColumnDefinition));
        scan.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var text = new FrameworkElementFactory(typeof(ColumnDefinition));
        text.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        grid.AppendChild(check);
        grid.AppendChild(scan);
        grid.AppendChild(text);

        var box = new FrameworkElementFactory(typeof(CheckBox));
        box.SetValue(Control.TemplateProperty, UiKit.CheckBox("").Template);
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.SetValue(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand);
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Row.UseText)) { Mode = BindingMode.TwoWay });
        grid.AppendChild(box);

        grid.AppendChild(WordImage(nameof(Row.ScanImage), 1));
        grid.AppendChild(WordImage(nameof(Row.TextImage), 2));

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(Grid.ColumnProperty, 1);
        label.SetValue(Grid.ColumnSpanProperty, 2);
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
        label.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        label.SetValue(TextBlock.FontSizeProperty, 10.0);
        label.SetValue(TextBlock.FontFamilyProperty, UiKit.MonoFont);
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Label)));
        grid.AppendChild(label);
        return new DataTemplate { VisualTree = grid };
    }

    private static FrameworkElementFactory WordImage(string property, int column)
    {
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Grid.ColumnProperty, column);
        frame.SetValue(Border.BackgroundProperty, Brushes.White);
        frame.SetValue(Border.PaddingProperty, new Thickness(4, 2, 4, 2));
        frame.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 12));
        frame.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.SetValue(Image.StretchDirectionProperty, StretchDirection.DownOnly);
        image.SetValue(FrameworkElement.MaxHeightProperty, 44.0);
        image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        image.SetBinding(Image.SourceProperty, new Binding(property));
        frame.AppendChild(image);
        return frame;
    }

    private sealed class Row(OcrController.ScanTextReviewWord word) : INotifyPropertyChanged
    {
        private bool _useText = word.UseText;
        private BitmapSource? _scan;
        private BitmapSource? _text;

        public event PropertyChangedEventHandler? PropertyChanged;

        internal OcrController.ScanTextReviewWord Word { get; } = word;

        public bool UseText
        {
            get => _useText;
            set
            {
                if (_useText == value) return;
                _useText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseText)));
            }
        }

        // Made when the row first scrolls into view, so a long document opens at once.
        public BitmapSource ScanImage => _scan ??= Gray(Word.Scan, Word.Width, Word.Height);
        public BitmapSource TextImage => _text ??= Gray(Word.Typeset, Word.Width, Word.Height);
        public string Label => $"{Word.Word.Word.Text}   p. {Word.PageIndex + 1}";

        private static BitmapSource Gray(byte[] pixels, int width, int height)
        {
            if (pixels.Length != width * height) pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
