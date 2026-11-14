using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

// Divide Spreads (#382). The page is shown as it looks in the viewer with a split line across it.
// Dragging or clicking moves the line onto the gutter; each page divided becomes two pages in
// reading order, left then right or top then bottom.
internal sealed class SpreadDivideDialog : Window
{
    internal enum DivideScope { CurrentPage, SelectedPages, AllPages }

    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly Line _line = new() { StrokeThickness = 2, StrokeDashArray = [4, 2], IsHitTestVisible = false };
    private readonly TextBlock _positionLabel;
    private readonly RadioButton _leftRight;
    private readonly RadioButton _topBottom;
    private readonly RadioButton _thisPage;
    private readonly RadioButton? _selected;
    private readonly RadioButton _all;
    private double _position = 0.5;

    internal bool Confirmed { get; private set; }
    internal PdfSpreadDivisionDirection Direction { get; private set; } = PdfSpreadDivisionDirection.Vertical;
    internal double Position => _position;
    internal DivideScope Scope { get; private set; } = DivideScope.CurrentPage;

    internal SpreadDivideDialog(Window owner, BitmapSource? preview, int selectedCount, int pageCount)
    {
        Title = "KillerPDF - " + L("Str_Divide_Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        // The page on the left, the options and the footer on the right, like Transform.
        var options = new StackPanel();
        var hint = new TextBlock
        {
            Text = L("Str_Divide_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        options.Children.Add(hint);

        // The preview keeps the page's shape inside a fixed box; the overlay sits exactly on the
        // image so the line's position is a fraction of the page itself.
        double boxWidth = 460, boxHeight = 440;
        double imageWidth = boxWidth, imageHeight = boxHeight;
        if (preview is not null && preview.PixelWidth > 0 && preview.PixelHeight > 0)
        {
            double fit = Math.Min(boxWidth / preview.PixelWidth, boxHeight / preview.PixelHeight);
            imageWidth = preview.PixelWidth * fit;
            imageHeight = preview.PixelHeight * fit;
        }
        var page = new Grid { Width = imageWidth, Height = imageHeight };
        page.Children.Add(new Image { Source = preview, Stretch = Stretch.Fill });
        _line.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
        _overlay.Children.Add(_line);
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
        FrameworkElement pagePane = PagePane(owner, frame, boxWidth, boxHeight);

        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            _overlay.CaptureMouse();
            MoveLine(e.GetPosition(_overlay));
            e.Handled = true;
        };
        _overlay.MouseMove += (_, e) =>
        {
            if (_overlay.IsMouseCaptured) MoveLine(e.GetPosition(_overlay));
        };
        _overlay.MouseLeftButtonUp += (_, _) => _overlay.ReleaseMouseCapture();
        _overlay.SizeChanged += (_, _) => DrawLine();

        var positionRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var center = UiKit.Make(L("Str_Divide_Center"), accent: false);
        center.Click += (_, _) => { _position = 0.5; DrawLine(); };
        DockPanel.SetDock(center, Dock.Right);
        positionRow.Children.Add(center);
        _positionLabel = new TextBlock { FontFamily = UiKit.UiFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _positionLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        positionRow.Children.Add(_positionLabel);
        options.Children.Add(positionRow);

        _leftRight = UiKit.Radio(L("Str_Divide_LeftRight"));
        _topBottom = UiKit.Radio(L("Str_Divide_TopBottom"));
        _leftRight.GroupName = _topBottom.GroupName = "DivideDirection";
        _leftRight.IsChecked = true;
        _leftRight.Checked += (_, _) => { Direction = PdfSpreadDivisionDirection.Vertical; DrawLine(); };
        _topBottom.Checked += (_, _) => { Direction = PdfSpreadDivisionDirection.Horizontal; DrawLine(); };
        var directions = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _leftRight.Margin = new Thickness(0, 0, 0, 4);
        _topBottom.Margin = new Thickness(0, 0, 0, 4);
        directions.Children.Add(_leftRight);
        directions.Children.Add(_topBottom);
        options.Children.Add(directions);

        _thisPage = UiKit.Radio(L("Str_Divide_ThisPage"));
        _all = UiKit.Radio(string.Format(L("Str_Divide_All"), pageCount));
        _thisPage.GroupName = _all.GroupName = "DivideScope";
        var scopes = new StackPanel();
        _thisPage.Margin = new Thickness(0, 0, 0, 4);
        scopes.Children.Add(_thisPage);
        if (selectedCount >= 2)
        {
            _selected = UiKit.Radio(string.Format(L("Str_Divide_Selected"), selectedCount));
            _selected.GroupName = "DivideScope";
            _selected.Margin = new Thickness(0, 0, 0, 4);
            _selected.IsChecked = true;
            scopes.Children.Add(_selected);
        }
        else _thisPage.IsChecked = true;
        _all.Margin = new Thickness(0, 0, 0, 4);
        scopes.Children.Add(_all);
        options.Children.Add(scopes);

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var divide = UiKit.Make(L("Str_Divide_Apply"), accent: true);
        divide.IsDefault = true;
        divide.Click += (_, _) =>
        {
            Scope = _all.IsChecked == true ? DivideScope.AllPages
                : _selected?.IsChecked == true ? DivideScope.SelectedPages : DivideScope.CurrentPage;
            Confirmed = true;
            Close();
        };
        Content = DialogFrame.Frame(this, owner, Title, Close,
            PageLeftBody(pagePane, options, DialogParts.Footer([cancel, divide])));
        DrawLine();
    }

    /// <summary>The page on the left; on the right the options over the footer, 280 wide.</summary>
    internal static Grid PageLeftBody(FrameworkElement pagePane, FrameworkElement options, FrameworkElement footer)
    {
        var side = new DockPanel { Width = 280, Margin = new Thickness(16, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        side.Children.Add(footer);
        side.Children.Add(options);
        var body = new Grid { Margin = new Thickness(12, 0, 20, 16) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(pagePane);
        Grid.SetColumn(side, 1);
        body.Children.Add(side);
        return body;
    }

    /// <summary>
    /// The page window's preview pane (Transform's look): the canvas color with the window's grain and a 1px frame,
    /// the family shadow under it, and <paramref name="page"/> centered in a box of the given size plus the page margin.
    /// </summary>
    internal static FrameworkElement PagePane(Window owner, FrameworkElement page, double boxWidth, double boxHeight)
    {
        var pane = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = UiKit.RadCard,
            Margin = new Thickness(8),
            ClipToBounds = true,
            // The box, the page margin on each side, the page's 1px frame and the pane's own.
            Width = boxWidth + 2 * KillerPDF.Controls.PageWindowLayout.PageMargin + 4,
            Height = boxHeight + 2 * KillerPDF.Controls.PageWindowLayout.PageMargin + 4
        };
        pane.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
        pane.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        var host = new Grid();
        if ((owner as KillerPDF.Shell.MainWindow)?.GrainTextureSource is { } grain)
        {
            double opacity = Application.Current.Resources["GrainOpacity"] is double value ? value : 0.05;
            host.Children.Add(new Border
            {
                IsHitTestVisible = false, Opacity = opacity,
                Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
            });
        }
        host.Children.Add(page);
        pane.Child = host;
        return UiKit.PaneWithShadow(pane);
    }

    // Keeps both halves at least a twentieth of the page, and snaps to the middle when close.
    private void MoveLine(Point point)
    {
        bool vertical = Direction == PdfSpreadDivisionDirection.Vertical;
        double extent = vertical ? _overlay.ActualWidth : _overlay.ActualHeight;
        if (extent <= 0) return;
        double position = Math.Clamp((vertical ? point.X : point.Y) / extent, 0.05, 0.95);
        if (Math.Abs(position - 0.5) < 0.01) position = 0.5;
        _position = position;
        DrawLine();
    }

    private void DrawLine()
    {
        double width = _overlay.ActualWidth, height = _overlay.ActualHeight;
        if (Direction == PdfSpreadDivisionDirection.Vertical)
        {
            _line.X1 = _line.X2 = width * _position;
            _line.Y1 = 0;
            _line.Y2 = height;
        }
        else
        {
            _line.Y1 = _line.Y2 = height * _position;
            _line.X1 = 0;
            _line.X2 = width;
        }
        _positionLabel.Text = string.Format(L("Str_Divide_Position"), Math.Round(_position * 100, 1));
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
