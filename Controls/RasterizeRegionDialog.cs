using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace KillerPDF;

// Rasterize Region (#369). The page is shown as it looks in the viewer; the user drags a box
// around the part to turn into an image and sees what inside it will change before applying.
internal sealed class RasterizeRegionDialog : Window
{
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly Rectangle _box = new() { StrokeThickness = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _summary;
    private readonly TextBox _dpi;
    private readonly Button _apply;
    private readonly Func<Rect, string> _describe;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private Point _start;

    internal bool Confirmed { get; private set; }
    /// <summary>The chosen box in the preview bitmap's pixels.</summary>
    internal Rect Region { get; private set; } = Rect.Empty;
    internal double Dpi { get; private set; } = 300;

    internal RasterizeRegionDialog(Window owner, BitmapSource preview, Func<Rect, string> describe)
    {
        _describe = describe;
        _pixelWidth = preview.PixelWidth;
        _pixelHeight = preview.PixelHeight;
        Title = "KillerPDF - " + L("Str_Raster_Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        // The page on the left, the options and the footer on the right, like Transform.
        var options = new StackPanel();
        var hint = new TextBlock
        {
            Text = L("Str_Raster_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        options.Children.Add(hint);

        const double boxWidth = 460, boxHeight = 440;
        double fit = Math.Min(boxWidth / Math.Max(1, _pixelWidth), boxHeight / Math.Max(1, _pixelHeight));
        var page = new Grid { Width = _pixelWidth * fit, Height = _pixelHeight * fit };
        page.Children.Add(new Image { Source = preview, Stretch = Stretch.Fill });
        _box.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
        _box.Fill = new SolidColorBrush(Color.FromArgb(40, 0, 120, 215));
        _overlay.Children.Add(_box);
        page.Children.Add(_overlay);
        var frame = new Border
        {
            Child = page,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(KillerPDF.Controls.PageWindowLayout.PageMargin)
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        FrameworkElement pagePane = SpreadDivideDialog.PagePane(owner, frame, boxWidth, boxHeight);

        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            _start = Clamp(e.GetPosition(_overlay));
            _overlay.CaptureMouse();
            DrawBox(_start);
            e.Handled = true;
        };
        _overlay.MouseMove += (_, e) =>
        {
            if (_overlay.IsMouseCaptured) DrawBox(Clamp(e.GetPosition(_overlay)));
        };
        _overlay.MouseLeftButtonUp += (_, e) =>
        {
            if (!_overlay.IsMouseCaptured) return;
            _overlay.ReleaseMouseCapture();
            FinishBox(Clamp(e.GetPosition(_overlay)));
        };

        _summary = new TextBlock
        {
            Text = L("Str_Raster_None"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10),
            MinHeight = 32
        };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        options.Children.Add(_summary);

        options.Children.Add(UiKit.GroupLabel(L("Str_ExportImg_Dpi")));
        _dpi = UiKit.Field();
        _dpi.Text = "300";
        _dpi.Width = 96;
        _dpi.HorizontalAlignment = HorizontalAlignment.Left;
        _dpi.Margin = new Thickness(0, 3, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_dpi, L("Str_ExportImg_Dpi"));
        options.Children.Add(_dpi);

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _apply = UiKit.Make(L("Str_Raster_Apply"), accent: true);
        _apply.IsDefault = true;
        _apply.IsEnabled = false;
        _apply.Click += (_, _) =>
        {
            Dpi = double.TryParse(_dpi.Text.Trim(), out double dpi) && dpi >= 36 && dpi <= 1200 ? dpi : 300;
            Confirmed = true;
            Close();
        };
        Content = DialogFrame.Frame(this, owner, Title, Close,
            SpreadDivideDialog.PageLeftBody(pagePane, options, DialogParts.Footer([cancel, _apply])));
    }

    private Point Clamp(Point point) => new(
        Math.Clamp(point.X, 0, _overlay.ActualWidth), Math.Clamp(point.Y, 0, _overlay.ActualHeight));

    private void DrawBox(Point end)
    {
        var rect = new Rect(_start, end);
        Canvas.SetLeft(_box, rect.X);
        Canvas.SetTop(_box, rect.Y);
        _box.Width = rect.Width;
        _box.Height = rect.Height;
        _box.Visibility = Visibility.Visible;
    }

    private void FinishBox(Point end)
    {
        var rect = new Rect(_start, end);
        if (rect.Width < 4 || rect.Height < 4 || _overlay.ActualWidth <= 0 || _overlay.ActualHeight <= 0)
        {
            _box.Visibility = Visibility.Collapsed;
            Region = Rect.Empty;
            _summary.Text = L("Str_Raster_None");
            _apply.IsEnabled = false;
            return;
        }
        double sx = _pixelWidth / _overlay.ActualWidth, sy = _pixelHeight / _overlay.ActualHeight;
        Region = new Rect(rect.X * sx, rect.Y * sy, rect.Width * sx, rect.Height * sy);
        try
        {
            _summary.Text = _describe(Region);
            _apply.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _summary.Text = ex.Message;
            _apply.IsEnabled = false;
        }
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
