using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;

namespace KillerPDF;

/// <summary>
/// Extract Image. The page is on the left, drawn from the engine's own sRGB render (never the monitor
/// or proof view); the options run down the right like Transform. A box or a color area chooses the
/// pixels, each new choice replacing, adding to or removing from the selection, and the page shows the
/// selection, or for Cut the fill, before anything changes. Cancel leaves the document untouched.
/// </summary>
internal sealed class ExtractImageDialog : Window
{
    private readonly PdfRenderedFrame _frame;
    private readonly int _width, _height;
    private readonly WriteableBitmap _overlayBitmap;
    private readonly Image _overlay = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
    private readonly Canvas _marks = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross, Focusable = true };
    private readonly Rectangle _box = new() { StrokeThickness = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Path _pointer = new() { StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly RadioButton _rectangleMode, _colorMode, _replace, _add, _subtract, _copy, _cut;
    private readonly Slider _tolerance, _featherRadius;
    private readonly CheckBox _feather;
    private readonly TextBlock _summary, _toleranceValue;
    private readonly Border _swatch;
    private readonly StackPanel _fillSection;
    private readonly Button _apply;
    private PdfRasterSelection? _selection, _base;
    private (int X, int Y)? _seed;
    private Point? _dragStart;
    private Point _keyPointer;
    private Point? _keyAnchor;
    private bool _manualFill;
    // The selection's box is found once per selection (selections never change after they are built),
    // and the overlay's pixels and the swatch's brush are reused from one refresh to the next.
    private PdfRasterSelection? _boundsOf;
    private (int Left, int Top, int Width, int Height)? _bounds;
    private byte[]? _overlayPixels;
    private Color? _swatchColor;
    // A slider drag asks for many changes a second; the work runs once the slider has been still for a moment.
    private readonly System.Windows.Threading.DispatcherTimer _sliderPause = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _pendingReseed, _pendingRefresh;

    internal bool Confirmed { get; private set; }
    internal PdfRasterSelection? Selection => _selection;
    internal bool Cut { get; private set; }
    internal Color FillColor { get; private set; } = Colors.White;
    internal int FeatherRadius { get; private set; }

    internal ExtractImageDialog(Window owner, PdfRenderedFrame frame, BitmapSource preview)
    {
        _frame = frame;
        _width = frame.Width;
        _height = frame.Height;
        _keyPointer = new Point(_width / 2.0, _height / 2.0);
        Title = "KillerPDF - " + L("Str_ExtractImage_Title");
        Width = 980;
        Height = 720;
        MinWidth = 640;
        MinHeight = 460;
        DialogFrame.Configure(this, owner, resizable: true);
        Style? darkSlider = owner.TryFindResource("DarkSlider") as Style;
        Style? themeRadio = owner.TryFindResource("ThemeRadio") as Style;

        var root = new DockPanel();
        var sidebar = new Border { Width = 288, Background = Brushes.Transparent, Padding = new Thickness(16, 8, 0, 14) };
        DockPanel.SetDock(sidebar, Dock.Right);
        var side = new DockPanel();

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { Confirmed = false; Close(); };
        _apply = UiKit.Make(L("Str_ExtractImage_Apply"), true);
        _apply.IsDefault = true;
        _apply.Click += (_, _) => Commit();
        // One footer row under the options, as in Transform.
        var actions = DialogParts.Footer([cancel, _apply]);
        actions.Margin = new Thickness(0, 10, 16, 0);
        DockPanel.SetDock(actions, Dock.Bottom);
        side.Children.Add(actions);

        var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        stack.Children.Add(Wrapped(L("Str_ExtractImage_Hint"), "MutedTextBrush", 11));

        stack.Children.Add(UiKit.GroupLabel(L("Str_ExtractImage_Method")));
        _rectangleMode = Radio(L("Str_ExtractImage_Rectangle"), true, "method", themeRadio);
        _colorMode = Radio(L("Str_ExtractImage_ColorArea"), false, "method", themeRadio);
        stack.Children.Add(_rectangleMode);
        stack.Children.Add(_colorMode);

        _toleranceValue = new TextBlock { Foreground = R("TextBrush"), FontFamily = UiKit.MonoFont, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var toleranceRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_toleranceValue, Dock.Right);
        toleranceRow.Children.Add(_toleranceValue);
        toleranceRow.Children.Add(Wrapped(L("Str_ExtractImage_Tolerance"), "MutedTextBrush", 11));
        stack.Children.Add(toleranceRow);
        _tolerance = new Slider { Minimum = 0, Maximum = 100, Value = 12, SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, TickFrequency = 1 };
        if (darkSlider is not null) _tolerance.Style = darkSlider;
        System.Windows.Automation.AutomationProperties.SetName(_tolerance, L("Str_ExtractImage_Tolerance"));
        _tolerance.ValueChanged += (_, _) => { _toleranceValue.Text = $"{_tolerance.Value:0}%"; AfterSliderPause(reseed: true); };
        _toleranceValue.Text = $"{_tolerance.Value:0}%";
        stack.Children.Add(_tolerance);

        stack.Children.Add(UiKit.GroupLabel(L("Str_ExtractImage_Combine")));
        _replace = Radio(L("Str_ExtractImage_Replace"), true, "combine", themeRadio);
        _add = Radio(L("Str_ExtractImage_Add"), false, "combine", themeRadio);
        _subtract = Radio(L("Str_ExtractImage_Subtract"), false, "combine", themeRadio);
        foreach (var radio in new[] { _replace, _add, _subtract }) stack.Children.Add(radio);
        var clear = UiKit.Make(L("Str_ExtractImage_Clear"), false);
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        clear.Margin = new Thickness(0, 6, 0, 0);
        clear.Click += (_, _) => { _selection = _base = null; _seed = null; Refresh(); };
        stack.Children.Add(clear);

        _feather = UiKit.CheckBox(L("Str_ExtractImage_Feather"));
        _feather.Margin = new Thickness(0, 12, 0, 0);
        _feather.IsChecked = false;
        stack.Children.Add(_feather);
        _featherRadius = new Slider { Minimum = 1, Maximum = 8, Value = 2, SmallChange = 1, LargeChange = 1, IsSnapToTickEnabled = true, TickFrequency = 1, IsEnabled = false };
        if (darkSlider is not null) _featherRadius.Style = darkSlider;
        System.Windows.Automation.AutomationProperties.SetName(_featherRadius, L("Str_ExtractImage_FeatherRadius"));
        _feather.Checked += (_, _) => { _featherRadius.IsEnabled = true; Refresh(); };
        _feather.Unchecked += (_, _) => { _featherRadius.IsEnabled = false; Refresh(); };
        _featherRadius.ValueChanged += (_, _) => AfterSliderPause(reseed: false);
        _sliderPause.Tick += (_, _) => FlushSliderWork();
        Closed += (_, _) => _sliderPause.Stop();
        stack.Children.Add(_featherRadius);

        stack.Children.Add(UiKit.GroupLabel(L("Str_ExtractImage_Result")));
        _copy = Radio(L("Str_ExtractImage_Copy"), true, "result", themeRadio);
        _cut = Radio(L("Str_ExtractImage_Cut"), false, "result", themeRadio);
        stack.Children.Add(_copy);
        stack.Children.Add(_cut);

        _fillSection = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        _fillSection.Children.Add(Wrapped(L("Str_ExtractImage_Fill"), "MutedTextBrush", 11));
        var fillRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _swatch = new Border { Width = 28, Height = 22, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(FillColor) };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        fillRow.Children.Add(_swatch);
        var pick = UiKit.Make(L("Str_ExtractImage_PickColor"), false);
        pick.Click += (_, _) =>
        {
            var picker = new ColorPickerDialog(this, FillColor);
            picker.ShowDialog();
            // A picker opened from a modal window can report false even on OK; Cancel keeps the color it opened with.
            if (picker.SelectedColor != FillColor) { FillColor = picker.SelectedColor; _manualFill = true; Refresh(); }
        };
        fillRow.Children.Add(pick);
        _fillSection.Children.Add(fillRow);
        var sampled = UiKit.Make(L("Str_ExtractImage_UseSampled"), false);
        sampled.HorizontalAlignment = HorizontalAlignment.Left;
        sampled.Margin = new Thickness(0, 6, 0, 0);
        sampled.Click += (_, _) => { _manualFill = false; Refresh(); };
        _fillSection.Children.Add(sampled);
        _fillSection.Children.Add(Wrapped(L("Str_ExtractImage_CutNote"), "MutedTextBrush", 11));
        stack.Children.Add(_fillSection);
        _copy.Checked += (_, _) => Refresh();
        _cut.Checked += (_, _) => Refresh();

        _summary = Wrapped(L("Str_ExtractImage_None"), "TextBrush", 12);
        _summary.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(_summary);

        side.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        sidebar.Child = side;
        root.Children.Add(sidebar);

        // The page: a framed pane with grain like Transform, holding the page at its own pixel size
        // inside a Viewbox, so pointer positions on the overlay canvas are page pixels.
        var previewWrap = new Border { BorderThickness = new Thickness(1), CornerRadius = UiKit.RadCard, Margin = new Thickness(8, 4, 8, 12), ClipToBounds = true };
        previewWrap.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
        previewWrap.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        var previewGrid = new Grid();
        if ((owner as KillerPDF.Shell.MainWindow)?.GrainTextureSource is { } grain)
        {
            double opacity = Application.Current.Resources["GrainOpacity"] is double value ? value : 0.05;
            previewGrid.Children.Add(new Border
            {
                IsHitTestVisible = false, Opacity = opacity,
                Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
            });
        }
        var page = new Grid { Width = _width, Height = _height };
        var image = new Image { Source = preview, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        page.Children.Add(image);
        _overlayBitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        _overlay.Source = _overlayBitmap;
        RenderOptions.SetBitmapScalingMode(_overlay, BitmapScalingMode.NearestNeighbor);
        page.Children.Add(_overlay);
        _box.Stroke = R("PrimaryBrush");
        _pointer.Stroke = R("PrimaryBrush");
        _marks.Children.Add(_box);
        _marks.Children.Add(_pointer);
        System.Windows.Automation.AutomationProperties.SetName(_marks, L("Str_ExtractImage_Hint"));
        page.Children.Add(_marks);
        var viewbox = new Viewbox
        {
            Child = page, Stretch = Stretch.Uniform, Margin = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 }
        };
        previewGrid.Children.Add(viewbox);
        previewWrap.Child = previewGrid;
        root.Children.Add(UiKit.PaneWithShadow(previewWrap));

        _marks.MouseLeftButtonDown += (_, e) =>
        {
            _marks.Focus();
            Point point = Clamp(e.GetPosition(_marks));
            if (_colorMode.IsChecked == true) { SeedAt(point); e.Handled = true; return; }
            _dragStart = point;
            _marks.CaptureMouse();
            ShowBox(point, point);
            e.Handled = true;
        };
        _marks.MouseMove += (_, e) => { if (_dragStart is { } start) ShowBox(start, Clamp(e.GetPosition(_marks))); };
        _marks.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragStart is not { } start) return;
            _dragStart = null;
            _marks.ReleaseMouseCapture();
            _box.Visibility = Visibility.Collapsed;
            BoxBetween(start, Clamp(e.GetPosition(_marks)));
        };
        _marks.LostMouseCapture += (_, _) => { _dragStart = null; _box.Visibility = Visibility.Collapsed; };
        // Keyboard path: the arrows move a pointer over the page (Shift for larger steps) and Space
        // seeds a color area there, or marks one corner of a box and then the other.
        _marks.GotKeyboardFocus += (_, _) => ShowPointer();
        _marks.LostKeyboardFocus += (_, _) => _pointer.Visibility = Visibility.Collapsed;
        _marks.KeyDown += (_, e) =>
        {
            double step = Math.Max(1, Math.Round(Math.Min(_width, _height) * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .1 : .01)));
            switch (e.Key)
            {
                case Key.Left: _keyPointer.X -= step; break;
                case Key.Right: _keyPointer.X += step; break;
                case Key.Up: _keyPointer.Y -= step; break;
                case Key.Down: _keyPointer.Y += step; break;
                case Key.Space:
                    if (_colorMode.IsChecked == true) SeedAt(_keyPointer);
                    else if (_keyAnchor is { } anchor) { _keyAnchor = null; _box.Visibility = Visibility.Collapsed; BoxBetween(anchor, _keyPointer); }
                    else { _keyAnchor = _keyPointer; ShowBox(_keyPointer, _keyPointer); }
                    e.Handled = true;
                    return;
                default: return;
            }
            _keyPointer = Clamp(_keyPointer);
            if (_keyAnchor is { } corner) ShowBox(corner, _keyPointer);
            ShowPointer();
            e.Handled = true;
        };

        Content = DialogFrame.Frame(this, owner, Title, () => { Confirmed = false; Close(); }, root);
        Loaded += (_, _) => _marks.Focus();
        Refresh();
    }

    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, _width - 1), Math.Clamp(point.Y, 0, _height - 1));

    private void ShowBox(Point a, Point b)
    {
        Canvas.SetLeft(_box, Math.Min(a.X, b.X));
        Canvas.SetTop(_box, Math.Min(a.Y, b.Y));
        _box.Width = Math.Abs(a.X - b.X) + 1;
        _box.Height = Math.Abs(a.Y - b.Y) + 1;
        _box.StrokeThickness = Math.Max(1, Math.Min(_width, _height) / 300.0);
        _box.Visibility = Visibility.Visible;
    }

    private void ShowPointer()
    {
        double arm = Math.Max(6, Math.Min(_width, _height) / 40.0);
        _pointer.StrokeThickness = Math.Max(1, Math.Min(_width, _height) / 300.0);
        _pointer.Data = new GeometryGroup
        {
            Children =
            {
                new LineGeometry(new Point(_keyPointer.X - arm, _keyPointer.Y), new Point(_keyPointer.X + arm, _keyPointer.Y)),
                new LineGeometry(new Point(_keyPointer.X, _keyPointer.Y - arm), new Point(_keyPointer.X, _keyPointer.Y + arm))
            }
        };
        _pointer.Visibility = Visibility.Visible;
    }

    private void BoxBetween(Point a, Point b)
    {
        int left = (int)Math.Floor(Math.Min(a.X, b.X)), top = (int)Math.Floor(Math.Min(a.Y, b.Y));
        int right = (int)Math.Floor(Math.Max(a.X, b.X)), bottom = (int)Math.Floor(Math.Max(a.Y, b.Y));
        _seed = null;
        Combine(PdfRasterSelection.SelectRectangle(_width, _height, left, top, right - left + 1, bottom - top + 1));
    }

    private void SeedAt(Point point)
    {
        _seed = ((int)point.X, (int)point.Y);
        _base = _selection;
        Combine(PdfRasterSelection.SelectConnected(_frame, _seed.Value.X, _seed.Value.Y, Tolerance), keepBase: true);
    }

    // A tolerance change re-runs the latest color-area choice against the selection it refined.
    private void Reseed()
    {
        if (_seed is not { } seed || _summary is null) return;
        Combine(PdfRasterSelection.SelectConnected(_frame, seed.X, seed.Y, Tolerance), keepBase: true);
    }

    private void AfterSliderPause(bool reseed)
    {
        _pendingReseed |= reseed;
        _pendingRefresh = true;
        _sliderPause.Stop();
        _sliderPause.Start();
    }

    // Runs the work a slider asked for. Apply calls it first, so it never acts on a stale choice.
    private void FlushSliderWork()
    {
        _sliderPause.Stop();
        bool reseed = _pendingReseed, refresh = _pendingRefresh;
        _pendingReseed = _pendingRefresh = false;
        if (reseed) Reseed();
        else if (refresh) Refresh();
    }

    private float Tolerance => (float)(_tolerance.Value / 100);

    private void Combine(PdfRasterSelection next, bool keepBase = false)
    {
        PdfRasterSelection? previous = keepBase ? _base : _selection;
        if (!keepBase) _base = _selection;
        _selection = _add.IsChecked == true && previous is not null ? previous.Add(next)
            : _subtract.IsChecked == true ? previous?.Subtract(next)
            : next;
        Refresh();
    }

    private (int Left, int Top, int Width, int Height)? Bounds()
    {
        if (_selection is null) return null;
        if (!ReferenceEquals(_boundsOf, _selection))
        {
            _bounds = FindBounds();
            _boundsOf = _selection;
        }
        return _bounds;
    }

    // The size of the image Apply makes: the selection's box grown by the feather radius on each side,
    // kept inside the page, as the engine's extraction does.
    private (int Left, int Top, int Width, int Height)? ExtractedBounds()
    {
        if (Bounds() is not { } box) return null;
        int radius = _feather.IsChecked == true ? (int)_featherRadius.Value : 0;
        int left = Math.Max(0, box.Left - radius), top = Math.Max(0, box.Top - radius);
        int right = Math.Min(_width - 1, box.Left + box.Width - 1 + radius), bottom = Math.Min(_height - 1, box.Top + box.Height - 1 + radius);
        return (left, top, right - left + 1, bottom - top + 1);
    }

    private (int Left, int Top, int Width, int Height)? FindBounds()
    {
        ReadOnlySpan<byte> coverage = _selection!.Coverage;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < _height; y++)
            for (int x = 0; x < _width; x++)
                if (coverage[y * _width + x] != 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    maxY = y;
                }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private void Refresh()
    {
        if (_summary is null) return;
        bool cut = _cut.IsChecked == true;
        _fillSection.Visibility = cut ? Visibility.Visible : Visibility.Collapsed;
        if (cut && !_manualFill && _selection is not null && Bounds() is not null)
        {
            var (r, g, b) = PdfRasterExtraction.SampleFillColor(_frame, _selection);
            FillColor = Color.FromRgb(r, g, b);
        }
        if (_swatchColor != FillColor)
        {
            _swatch.Background = new SolidColorBrush(FillColor);
            _swatchColor = FillColor;
        }
        // Copy tints the selection; Cut shows the page as it will look, with the fill in place.
        Color accent = KillerPDF.Controls.PageWindowLayout.ThemeColor(Application.Current?.TryFindResource("PrimaryBrush"), Color.FromRgb(0x22, 0xBB, 0x56));
        byte[] pixels = _overlayPixels ??= new byte[_width * _height * 4];
        Array.Clear(pixels);
        if (_selection is not null)
        {
            ReadOnlySpan<byte> coverage = _selection.Coverage;
            for (int i = 0; i < coverage.Length; i++)
            {
                if (coverage[i] == 0) continue;
                Color c = cut ? FillColor : accent;
                pixels[i * 4] = c.B; pixels[i * 4 + 1] = c.G; pixels[i * 4 + 2] = c.R; pixels[i * 4 + 3] = cut ? (byte)255 : (byte)110;
            }
        }
        _overlayBitmap.WritePixels(new Int32Rect(0, 0, _width, _height), pixels, _width * 4, 0);
        var bounds = ExtractedBounds();
        bool tooLarge = bounds is { } size && (long)size.Width * size.Height > PdfRasterExtraction.MaximumPixels;
        _summary.Text = bounds is not { } shown ? L("Str_ExtractImage_None")
            : string.Format(L(tooLarge ? "Str_ExtractImage_TooLarge" : "Str_ExtractImage_Size"), shown.Width, shown.Height);
        _apply.IsEnabled = bounds is not null && !tooLarge;
    }

    private void Commit()
    {
        FlushSliderWork();
        if (!_apply.IsEnabled) return;
        Cut = _cut.IsChecked == true;
        FeatherRadius = _feather.IsChecked == true ? (int)_featherRadius.Value : 0;
        Confirmed = true;
        Close();
    }

    private RadioButton Radio(string text, bool isChecked, string group, Style? style)
    {
        var radio = new RadioButton
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
            IsChecked = isChecked, GroupName = group + GetHashCode(), Foreground = R("TextBrush"),
            FontFamily = UiKit.UiFont, FontSize = 12, Margin = new Thickness(0, 3, 0, 0)
        };
        if (style is not null) radio.Style = style;
        return radio;
    }

    private static TextBlock Wrapped(string text, string brush, double size) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = R(brush), FontFamily = UiKit.UiFont,
        FontSize = size, Margin = new Thickness(0, 4, 0, 0)
    };

    // Brush, never SolidColorBrush: any theme may state a surface as a gradient.
    private static Brush R(string key) => KillerPDF.Controls.PageWindowLayout.ThemeBrush(Application.Current?.TryFindResource(key));
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
