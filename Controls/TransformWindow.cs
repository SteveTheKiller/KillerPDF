using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using KillerPDF.Services;

namespace KillerPDF
{
    /// <summary>
    /// Modal "Transform" window. Renders the current page on its own canvas (so the main view's mode is
    /// irrelevant) and lets the user rotate (quarter turns + fine deskew) and scale it, with the controls in
    /// a right-hand sidebar (the mirror of Print Preview). Apply hands the chosen angle / scale / page-mode
    /// back to the caller, which rasterizes at full resolution. Draggable corner handles are the next step.
    /// </summary>
    internal sealed class TransformWindow : Window
    {
        internal sealed record PagePreview(BitmapSource Source, double WidthPoints,
            double HeightPoints, int DocumentPageNumber);

        public bool Applied { get; private set; }
        public double Angle { get; private set; }     // total = quarter turns + fine
        public double Scale { get; private set; } = 1.0;
        public bool FixedPage { get; private set; }    // true = keep page size (margins); false = resize page
        public bool FlipH { get; private set; }
        public bool FlipV { get; private set; }
        // #174: source levels (black point, white point, midtone gamma). 0/255/1.0 = untouched.
        public int    LevelBlack { get; private set; }
        public int    LevelWhite { get; private set; } = 255;
        public double LevelGamma { get; private set; } = 1.0;
        public PageColorMode ColorMode { get; private set; }
        public int BlackWhiteThreshold { get; private set; } = 160;
        public int OutputDpi { get; private set; }
        public bool UseJpegCompression { get; private set; }
        public int JpegQuality { get; private set; } = 85;

        public Point[] PerspectiveCorners { get; private set; } =
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

        private readonly IReadOnlyList<PagePreview> _pages;
        private BitmapSource _src;
        private readonly Image _preview = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 }
        };
        private readonly Border _previewArea = null!;
        private double _srcW;
        private double _srcH;
        private double _pageWpt;
        private double _pageHpt;
        private int _previewPageIndex;
        private readonly TextBlock _pageCounter = null!;
        private readonly Button _previousPage = null!;
        private readonly Button _nextPage = null!;
        private readonly TextBlock _sizeReadout = null!;
        private readonly TextBlock _dpiReadout = null!;
        private int _quarter;        // 0..3 quarter turns clockwise
        private double _fine;        // fine deskew, degrees
        private double _scale = 1.0;
        private bool _fixedPage;
        private readonly TextBlock _rotReadout = null!;
        private readonly TextBlock _scaleReadout = null!;
        private readonly Slider _rotSlider = null!;
        private readonly Slider _scaleSlider = null!;
        private readonly Slider _lvlBlack = null!, _lvlWhite = null!, _lvlGamma = null!;   // #174
        private readonly ComboBox _colorMode = null!;
        private readonly Slider _bwThreshold = null!, _dpiSlider = null!, _jpegSlider = null!;
        private readonly CheckBox _setDpi = null!, _useJpeg = null!;
        private readonly RadioButton _resizeRadio = null!;
        private bool _flipH;
        private bool _flipV;
        private readonly CheckBox _flipHCheck = null!;
        private readonly CheckBox _flipVCheck = null!;
        private readonly Canvas _lineCanvas = null!;
        private readonly Line _alignLine = null!;
        private readonly CheckBox _deskewCheck = null!;
        private readonly TextBlock _lineCoords = null!;
        private bool _drawingLine;
        private Point _lineStart;
        private Point _startPagePt;
        private readonly DispatcherTimer _previewTimer = null!;
        private readonly Canvas _perspectiveCanvas = null!;
        private readonly Polygon _perspectiveOutline = null!;
        private readonly Ellipse[] _perspectiveHandles = new Ellipse[4];
        private readonly CheckBox _perspectiveCheck = null!;
        private int _dragPerspective = -1;

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];
        private static string S(string key) => Application.Current.TryFindResource(key) as string ?? key;

        public TransformWindow(Window owner, IReadOnlyList<PagePreview> pages)
        {
            ArgumentNullException.ThrowIfNull(pages);
            if (pages.Count == 0) throw new ArgumentException("At least one page preview is required.", nameof(pages));
            _pages = pages;
            _src = pages[0].Source;
            _srcW = _src.PixelWidth;
            _srcH = _src.PixelHeight;
            _pageWpt = pages[0].WidthPoints;
            _pageHpt = pages[0].HeightPoints;
            Title = "KillerPDF - " + S("Str_Tf_Suffix");
            Width = 980;
            Height = 720;
            MinWidth = 640;
            MinHeight = 460;
            DialogChrome.Configure(this, owner, resizable: true);

            Style? darkSlider = owner?.TryFindResource("DarkSlider") is Style sliderStyle
                ? sliderStyle : null;
            Style? darkCombo = owner?.TryFindResource("DarkComboBox") is Style comboStyle
                ? comboStyle : null;
            Style? themeRadio = owner?.TryFindResource("ThemeRadio") is Style radioStyle
                ? radioStyle : null;

            // Coalesce rapid slider changes: the heavy compose (especially scaling a page up, which makes a
            // big bitmap) only runs ~25x/sec on the latest value, so dragging stays smooth instead of queuing
            // a backlog of full re-renders.
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _previewTimer.Tick += (_, _2) => { _previewTimer.Stop(); UpdatePreview(); };

            var root = new DockPanel();

            // ---- Right sidebar (transparent so it blends with the dark title bar, like Print Preview) ----
            var sidebar = new Border
            {
                Width = 288,
                Background = Brushes.Transparent,
                Padding = new Thickness(16, 8, 4, 14)
            };
            DockPanel.SetDock(sidebar, Dock.Right);

            var side = new DockPanel();

            // Bottom: a "Reset all" text link on its own line (translations like "Tout reinitialiser" are
            // long), with Cancel / Apply right-aligned beneath it - so nothing crowds or clips.
            var bottom = new StackPanel { Margin = new Thickness(0, 10, 12, 0) };
            var resetAll = new TextBlock
            {
                Text = S("Str_Tf_ResetAll"), FontFamily = UiKit.UiFont, FontSize = 12,
                Foreground = R("MutedTextBrush"), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left
            };
            resetAll.MouseEnter += (_, _2) => resetAll.Foreground = R("PrimaryBrush");
            resetAll.MouseLeave += (_, _2) => resetAll.Foreground = R("MutedTextBrush");
            resetAll.MouseLeftButtonUp += (_, _2) =>
            {
                _quarter = 0; _rotSlider.Value = 0; _scaleSlider.Value = 100;
                _resizeRadio.IsChecked = true; _flipHCheck.IsChecked = false; _flipVCheck.IsChecked = false;
                ResetPerspective();
                ResetLevels();   // #174
                ResetQuality();
            };
            bottom.Children.Add(resetAll);
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var cancelBtn = UiKit.Make(S("Str_Tf_Cancel"), false);
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.Click += (_, _2) => { Applied = false; Close(); };
            cancelBtn.IsCancel = true;   // Esc
            actionRow.Children.Add(cancelBtn);
            var applyBtn = UiKit.Make(pages.Count == 1 ? S("Str_Tf_Apply") : $"{S("Str_Tf_Apply")} ({pages.Count})", true);
            applyBtn.Click += (_, _2) => CommitAndClose();
            applyBtn.IsDefault = true;   // Enter
            actionRow.Children.Add(applyBtn);
            bottom.Children.Add(actionRow);
            DockPanel.SetDock(bottom, Dock.Bottom);
            side.Children.Add(bottom);

            var stack = new StackPanel();

            int rotateStart = stack.Children.Count;
            // Quarter-turn buttons.
            var turnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
            var turnL = UiKit.Make("↺ 90°", false);
            turnL.Margin = new Thickness(0, 0, 6, 0);
            turnL.Click += (_, _2) => { _quarter = (_quarter + 3) % 4; UpdatePreview(); };
            var turnR = UiKit.Make("90° ↻", false);
            turnR.Click += (_, _2) => { _quarter = (_quarter + 1) % 4; UpdatePreview(); };
            turnRow.Children.Add(turnL);
            turnRow.Children.Add(turnR);
            stack.Children.Add(turnRow);

            _rotSlider = new Slider { Minimum = -45, Maximum = 45, Value = 0, TickFrequency = 1, SmallChange = 0.1, LargeChange = 1, Margin = new Thickness(0, 2, 0, 2) };
            if (darkSlider != null) _rotSlider.Style = darkSlider;
            _rotSlider.ValueChanged += (_, ev) => { _fine = Math.Round(ev.NewValue, 1); _rotReadout?.Text = $"{Total:0.0}°"; SchedulePreview(); };
            stack.Children.Add(_rotSlider);
            stack.Children.Add(ValueRow(S("Str_Tf_Angle"), "0.0°", out _rotReadout, out var rotReset));
            rotReset.Click += (_, _2) => { _quarter = 0; _rotSlider.Value = 0; UpdatePreview(); };

            WrapSection(stack, rotateStart, S("Str_Tf_Rotate"), expanded: true);
            stack.Children.Add(Divider());

            int scaleStart = stack.Children.Count;
            _scaleSlider = new Slider { Minimum = 25, Maximum = 200, Value = 100, TickFrequency = 5, SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, 2, 0, 2) };
            if (darkSlider != null) _scaleSlider.Style = darkSlider;
            _scaleSlider.ValueChanged += (_, ev) => { _scale = Math.Round(ev.NewValue) / 100.0; _scaleReadout.Text = $"{ev.NewValue:0}%"; SchedulePreview(); };
            stack.Children.Add(_scaleSlider);
            stack.Children.Add(ValueRow(S("Str_Tf_Size"), "100%", out _scaleReadout, out var scaleReset));
            scaleReset.Click += (_, _2) => _scaleSlider.Value = 100;

            stack.Children.Add(new TextBlock { Text = S("Str_Tf_WhenScaling"), Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont, FontSize = 11, Margin = new Thickness(0, 10, 0, 4) });
            _resizeRadio = MakeRadio(S("Str_Tf_ResizePage"), true, themeRadio);
            var fixedRadio = MakeRadio(S("Str_Tf_KeepSize"), false, themeRadio);
            _resizeRadio.Checked += (_, _2) => { _fixedPage = false; UpdatePreview(); };
            fixedRadio.Checked += (_, _2) => { _fixedPage = true; UpdatePreview(); };
            stack.Children.Add(_resizeRadio);
            stack.Children.Add(fixedRadio);

            // Live output dimensions, so scale changes (including above 100%, where the preview clamps to
            // fit) are always legible as a number even when the page can't grow on screen.
            _sizeReadout = new TextBlock { Foreground = R("MutedTextBrush"), FontFamily = UiKit.MonoFont, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
            stack.Children.Add(_sizeReadout);

            WrapSection(stack, scaleStart, S("Str_Tf_Scale"), expanded: false);
            stack.Children.Add(Divider());
            int flipStart = stack.Children.Count;
            _flipHCheck = MakeCheck(S("Str_Tf_FlipH"));
            _flipHCheck.Checked   += (_, _2) => { _flipH = true;  UpdatePreview(); };
            _flipHCheck.Unchecked += (_, _2) => { _flipH = false; UpdatePreview(); };
            stack.Children.Add(_flipHCheck);
            _flipVCheck = MakeCheck(S("Str_Tf_FlipV"));
            _flipVCheck.Checked   += (_, _2) => { _flipV = true;  UpdatePreview(); };
            _flipVCheck.Unchecked += (_, _2) => { _flipV = false; UpdatePreview(); };
            stack.Children.Add(_flipVCheck);

            WrapSection(stack, flipStart, S("Str_Tf_Flip"), expanded: false);
            stack.Children.Add(Divider());
            int skewStart = stack.Children.Count;
            _deskewCheck = MakeCheck(S("Str_Tf_LevelLine"));
            _deskewCheck.Checked   += (_, _2) => { _lineCanvas.IsHitTestVisible = true; };
            _deskewCheck.Unchecked += (_, _2) => { _lineCanvas.IsHitTestVisible = false; _alignLine.Visibility = Visibility.Collapsed; _lineCoords.Text = ""; };
            stack.Children.Add(_deskewCheck);
            stack.Children.Add(new TextBlock
            {
                Text = S("Str_Tf_SkewHint"),
                Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont, FontSize = 10,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            });
            // Live cursor coordinates (page points), so the user can place the line precisely on the small
            // preview. Start point on press, end point as they drag.
            _lineCoords = new TextBlock
            {
                Text = "", Foreground = R("MutedTextBrush"), FontFamily = UiKit.MonoFont,
                FontSize = 11, LineHeight = 16, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(3)
            };
            stack.Children.Add(_lineCoords);

            WrapSection(stack, skewStart, S("Str_Tf_Skew"), expanded: false);
            stack.Children.Add(Divider());
            int perspectiveStart = stack.Children.Count;
            _perspectiveCheck = MakeCheck(S("Str_Tf_CorrectPerspective"));
            _perspectiveCheck.Checked += (_, _2) =>
            {
                _deskewCheck.IsChecked = false;
                _perspectiveCanvas.Visibility = Visibility.Visible;
                _perspectiveCanvas.IsHitTestVisible = true;
                UpdatePerspectiveOverlay();
            };
            _perspectiveCheck.Unchecked += (_, _2) =>
            {
                _perspectiveCanvas.Visibility = Visibility.Collapsed;
                _perspectiveCanvas.IsHitTestVisible = false;
            };
            stack.Children.Add(_perspectiveCheck);
            stack.Children.Add(new TextBlock
            {
                Text = S("Str_Tf_PerspectiveHint"),
                Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont, FontSize = 10,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            });
            var resetPerspective = UiKit.Make(S("Str_Tf_ResetCorners"), false);
            resetPerspective.Margin = new Thickness(0, 7, 0, 0);
            resetPerspective.HorizontalAlignment = HorizontalAlignment.Left;
            resetPerspective.Click += (_, _2) => ResetPerspective();
            stack.Children.Add(resetPerspective);
            WrapSection(stack, perspectiveStart, S("Str_Tf_Perspective"), expanded: false);

            // #174: LEVELS - FineReader-style source levels for rescuing pale scans. Black point,
            // white point, and a midtone gamma; live in the preview, baked on Apply like every
            // other correction here.
            stack.Children.Add(Divider());
            int levelsStart = stack.Children.Count;
            stack.Children.Add(SliderLabel(S("Str_Tf_LevelsBlack")));
            _lvlBlack = new Slider { Minimum = 0, Maximum = 200, Value = 0, TickFrequency = 5, SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, 2, 0, 2) };
            if (darkSlider != null) _lvlBlack.Style = darkSlider;
            _lvlBlack.ValueChanged += (_, ev) => { LevelBlack = (int)Math.Round(ev.NewValue); SchedulePreview(); };
            stack.Children.Add(_lvlBlack);
            stack.Children.Add(SliderLabel(S("Str_Tf_LevelsWhite")));
            _lvlWhite = new Slider { Minimum = 55, Maximum = 255, Value = 255, TickFrequency = 5, SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, 2, 0, 2) };
            if (darkSlider != null) _lvlWhite.Style = darkSlider;
            _lvlWhite.ValueChanged += (_, ev) => { LevelWhite = (int)Math.Round(ev.NewValue); SchedulePreview(); };
            stack.Children.Add(_lvlWhite);
            stack.Children.Add(SliderLabel(S("Str_Tf_LevelsGamma")));
            _lvlGamma = new Slider { Minimum = 0.2, Maximum = 2.5, Value = 1.0, TickFrequency = 0.05, SmallChange = 0.05, LargeChange = 0.2, Margin = new Thickness(0, 2, 0, 2) };
            if (darkSlider != null) _lvlGamma.Style = darkSlider;
            _lvlGamma.ValueChanged += (_, ev) => { LevelGamma = Math.Round(ev.NewValue, 2); SchedulePreview(); };
            stack.Children.Add(_lvlGamma);
            var levelsReset = UiKit.Make(S("Str_Tf_Reset"), false);
            levelsReset.Margin = new Thickness(0, 7, 0, 0);
            levelsReset.HorizontalAlignment = HorizontalAlignment.Left;
            levelsReset.Click += (_, _2) => ResetLevels();
            stack.Children.Add(levelsReset);
            WrapSection(stack, levelsStart, S("Str_Tf_Levels"), expanded: false);

            // #173: output quality applies after geometry and levels. Defaults preserve the
            // existing lossless, automatic-resolution Transform behavior.
            stack.Children.Add(Divider());
            int qualityStart = stack.Children.Count;
            stack.Children.Add(SliderLabel(S("Str_Tf_ColorMode")));
            _colorMode = new ComboBox
            {
                ItemsSource = new[]
                {
                    S("Str_Print_Color"), S("Str_Tf_Grayscale"), S("Str_Tf_BlackWhite")
                },
                SelectedIndex = 0,
                Margin = new Thickness(0, 3, 0, 3)
            };
            if (darkCombo != null) _colorMode.Style = darkCombo;
            _colorMode.SelectionChanged += (_, _) =>
            {
                ColorMode = (PageColorMode)Math.Max(0, _colorMode.SelectedIndex);
                _bwThreshold.IsEnabled = ColorMode == PageColorMode.BlackAndWhite;
                SchedulePreview();
            };
            stack.Children.Add(_colorMode);
            stack.Children.Add(SliderLabel(S("Str_Tf_Threshold")));
            _bwThreshold = new Slider
            {
                Minimum = 0, Maximum = 255, Value = 160, IsEnabled = false,
                TickFrequency = 5, SmallChange = 1, LargeChange = 10,
                Margin = new Thickness(0, 2, 0, 2)
            };
            if (darkSlider != null) _bwThreshold.Style = darkSlider;
            var thresholdValue = SliderLabel("160");
            thresholdValue.HorizontalAlignment = HorizontalAlignment.Right;
            _bwThreshold.ValueChanged += (_, ev) =>
            {
                BlackWhiteThreshold = (int)Math.Round(ev.NewValue);
                thresholdValue.Text = BlackWhiteThreshold.ToString();
                SchedulePreview();
            };
            stack.Children.Add(_bwThreshold);
            stack.Children.Add(thresholdValue);

            _setDpi = MakeCheck(S("Str_Tf_SetDpi"));
            _setDpi.Checked += (_, _) => { OutputDpi = (int)Math.Round(_dpiSlider.Value); _dpiSlider.IsEnabled = true; };
            _setDpi.Unchecked += (_, _) => { OutputDpi = 0; _dpiSlider.IsEnabled = false; };
            stack.Children.Add(_setDpi);
            _dpiReadout = SliderLabel("300 DPI");
            _dpiReadout.HorizontalAlignment = HorizontalAlignment.Right;
            _dpiSlider = new Slider
            {
                Minimum = 72, Maximum = 600, Value = 300, IsEnabled = false,
                TickFrequency = 25, SmallChange = 1, LargeChange = 25,
                Margin = new Thickness(0, 2, 0, 2)
            };
            if (darkSlider != null) _dpiSlider.Style = darkSlider;
            _dpiSlider.ValueChanged += (_, ev) =>
            {
                if (_setDpi.IsChecked == true) OutputDpi = (int)Math.Round(ev.NewValue);
                UpdateDpiReadout();
            };
            stack.Children.Add(_dpiSlider);
            stack.Children.Add(_dpiReadout);

            _useJpeg = MakeCheck(S("Str_Tf_UseJpeg"));
            _useJpeg.Checked += (_, _) => { UseJpegCompression = true; _jpegSlider.IsEnabled = true; };
            _useJpeg.Unchecked += (_, _) => { UseJpegCompression = false; _jpegSlider.IsEnabled = false; };
            stack.Children.Add(_useJpeg);
            var jpegValue = SliderLabel("85%");
            jpegValue.HorizontalAlignment = HorizontalAlignment.Right;
            _jpegSlider = new Slider
            {
                Minimum = 25, Maximum = 100, Value = 85, IsEnabled = false,
                TickFrequency = 5, SmallChange = 1, LargeChange = 5,
                Margin = new Thickness(0, 2, 0, 2)
            };
            if (darkSlider != null) _jpegSlider.Style = darkSlider;
            _jpegSlider.ValueChanged += (_, ev) =>
            {
                JpegQuality = (int)Math.Round(ev.NewValue);
                jpegValue.Text = $"{ev.NewValue:0}%";
            };
            stack.Children.Add(_jpegSlider);
            stack.Children.Add(jpegValue);
            var qualityReset = UiKit.Make(S("Str_Tf_Reset"), false);
            qualityReset.Margin = new Thickness(0, 7, 0, 0);
            qualityReset.HorizontalAlignment = HorizontalAlignment.Left;
            qualityReset.Click += (_, _) => ResetQuality();
            stack.Children.Add(qualityReset);
            WrapSection(stack, qualityStart, S("Str_Tf_Quality"), expanded: false);

            // The classic scrollbar sits near the window edge to return width to translated
            // controls, while this small inner gap keeps those controls from touching it.
            if (ThemeManager.Current == Theme.SE98)
                stack.Margin = new Thickness(0, 0, 6, 0);

            side.Children.Add(new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            sidebar.Child = side;
            root.Children.Add(sidebar);

            // ---- Preview area: a documentbg box (1px frame, margin, rounded) with grain in the margins and
            //      the page (sized to its true relative scale, with a drop shadow) centered on top. ----
            var previewWrap = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = UiKit.RadCard,
                Margin = new Thickness(8, 4, 8, 12),
                ClipToBounds = true
            };
            previewWrap.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
            previewWrap.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");

            var previewGrid = new Grid();
            var pgGrain = (owner as MainWindow)?.GrainTexture;
            if (pgGrain != null)
            {
                double pop = Application.Current.Resources["GrainOpacity"] is double pgo ? pgo : 0.05;
                previewGrid.Children.Add(new Border
                {
                    IsHitTestVisible = false, Opacity = pop,
                    Background = new ImageBrush(pgGrain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
                });
            }
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
            _preview.Source = _src;
            previewGrid.Children.Add(_preview);

            // Alignment-line overlay: when "Draw a level line" is on, the user drags a reference line across
            // the page and the page rotates so that line becomes level. Hit-testing is off until enabled, so
            // it never interferes with the rest of the preview.
            _lineCanvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false, Cursor = Cursors.Cross };
            _alignLine = new Line
            {
                Stroke = R("PrimaryBrush"), StrokeThickness = 2, StrokeDashArray = [4, 3],
                Visibility = Visibility.Collapsed,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.White, BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 }
            };
            _lineCanvas.Children.Add(_alignLine);
            _lineCanvas.MouseLeftButtonDown += LineCanvas_Down;
            _lineCanvas.MouseMove += LineCanvas_Move;
            _lineCanvas.MouseLeftButtonUp += LineCanvas_Up;
            previewGrid.Children.Add(_lineCanvas);

            _perspectiveCanvas = new Canvas
            {
                Background = Brushes.Transparent,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Cursor = Cursors.Cross,
            };
            _perspectiveOutline = new Polygon
            {
                Stroke = R("PrimaryBrush"), StrokeThickness = 2, StrokeDashArray = [5, 3],
                Fill = new SolidColorBrush(Color.FromArgb(24, 30, 165, 76)), IsHitTestVisible = false,
            };
            _perspectiveCanvas.Children.Add(_perspectiveOutline);
            for (int i = 0; i < 4; i++)
            {
                var handle = new Ellipse
                {
                    Width = 18, Height = 18, Fill = R("PrimaryBrush"), Stroke = Brushes.White,
                    StrokeThickness = 2, Cursor = DragCursors.Open, Tag = i,
                };
                handle.PreviewMouseLeftButtonDown += PerspectiveHandle_Down;
                _perspectiveHandles[i] = handle;
                _perspectiveCanvas.Children.Add(handle);
            }
            _perspectiveCanvas.AddHandler(Mouse.PreviewMouseMoveEvent,
                new MouseEventHandler(PerspectiveCanvas_Move), true);
            _perspectiveCanvas.AddHandler(Mouse.PreviewMouseUpEvent,
                new MouseButtonEventHandler(PerspectiveCanvas_Up), true);
            // A capture lost to an alt-tab or a dialog never reaches the up handler, which would
            // strand the closed hand on screen for the rest of the session.
            _perspectiveCanvas.LostMouseCapture += (_, _2) => { _dragPerspective = -1; DragCursors.EndDrag(); };
            previewGrid.Children.Add(_perspectiveCanvas);

            previewWrap.Child = previewGrid;
            _previewArea = previewWrap;
            previewWrap.SizeChanged += (_, _2) =>
            {
                double radius = previewWrap.CornerRadius.TopLeft;
                previewGrid.Clip = new RectangleGeometry(
                    new Rect(0, 0, previewGrid.ActualWidth, previewGrid.ActualHeight), radius, radius);
                SizePreviewImage();
            };

            var previewColumn = new DockPanel();
            if (pages.Count > 1)
            {
                var navigation = new Grid { Height = 38, Margin = new Thickness(8, 0, 8, 8) };
                navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                navigation.ColumnDefinitions.Add(new ColumnDefinition());
                navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _previousPage = UiKit.Make("‹", false);
                _previousPage.Width = 44;
                _previousPage.ToolTip = S("Str_Kb_PrevPage");
                _previousPage.Click += (_, _2) => ShowPreviewPage(_previewPageIndex - 1);
                Grid.SetColumn(_previousPage, 0);
                navigation.Children.Add(_previousPage);
                _pageCounter = new TextBlock
                {
                    FontFamily = UiKit.MonoFont,
                    FontSize = 12,
                    Foreground = R("TextBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(_pageCounter, 1);
                navigation.Children.Add(_pageCounter);
                _nextPage = UiKit.Make("›", false);
                _nextPage.Width = 44;
                _nextPage.ToolTip = S("Str_Kb_NextPage");
                _nextPage.Click += (_, _2) => ShowPreviewPage(_previewPageIndex + 1);
                Grid.SetColumn(_nextPage, 2);
                navigation.Children.Add(_nextPage);
                DockPanel.SetDock(navigation, Dock.Bottom);
                previewColumn.Children.Add(navigation);
            }
            // Family shadow under the content pane, like the main window (flat on 98SE).
            previewColumn.Children.Add(UiKit.PaneWithShadow(previewWrap));
            root.Children.Add(previewColumn);

            Content = DialogChrome.Frame(this, Owner, "KillerPDF - " + S("Str_Tf_Suffix"), () => { Applied = false; Close(); }, root);
            UpdatePreview();   // populate the output-size readout at the original dimensions
            UpdatePageNavigation();

            // Esc-to-close is wired by DialogChrome.Frame; Enter commits. Arrow keys flip through
            // batch previews unless a slider currently owns the key press.
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) CommitAndClose();
                else if (_pages.Count > 1 && e.OriginalSource is not Slider && e.Key == Key.Left)
                { ShowPreviewPage(_previewPageIndex - 1); e.Handled = true; }
                else if (_pages.Count > 1 && e.OriginalSource is not Slider && e.Key == Key.Right)
                { ShowPreviewPage(_previewPageIndex + 1); e.Handled = true; }
            };
        }

        private double Total => _quarter * 90 + _fine;

        private void ShowPreviewPage(int index)
        {
            if (index < 0 || index >= _pages.Count || index == _previewPageIndex) return;
            _previewPageIndex = index;
            PagePreview page = _pages[index];
            _src = page.Source;
            _srcW = _src.PixelWidth;
            _srcH = _src.PixelHeight;
            _pageWpt = page.WidthPoints;
            _pageHpt = page.HeightPoints;
            _alignLine.Visibility = Visibility.Collapsed;
            _lineCoords.Text = "";
            UpdatePreview();
            UpdatePageNavigation();
        }

        private void UpdatePageNavigation()
        {
            if (_pages.Count <= 1 || _pageCounter is null) return;
            PagePreview page = _pages[_previewPageIndex];
            _pageCounter.Text = $"{_previewPageIndex + 1} / {_pages.Count}    #{page.DocumentPageNumber}";
            _previousPage.IsEnabled = _previewPageIndex > 0;
            _nextPage.IsEnabled = _previewPageIndex + 1 < _pages.Count;
        }

        private void CommitAndClose()
        {
            Applied = true;
            Angle = Total;
            Scale = _scale;
            FixedPage = _fixedPage;
            FlipH = _flipH;
            FlipV = _flipV;
            PerspectiveCorners = [.. PerspectiveCorners];
            Close();
        }

        private void ResetPerspective()
        {
            PerspectiveCorners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            UpdatePerspectiveOverlay();
        }

        private Rect PreviewBoundsOnPerspectiveCanvas()
        {
            if (_preview.ActualWidth <= 0 || _preview.ActualHeight <= 0) return Rect.Empty;
            Point origin = _preview.TranslatePoint(new Point(0, 0), _perspectiveCanvas);
            return new Rect(origin.X, origin.Y, _preview.ActualWidth, _preview.ActualHeight);
        }

        private void UpdatePerspectiveOverlay()
        {
            if (_perspectiveCanvas == null || _perspectiveOutline == null) return;
            Rect bounds = PreviewBoundsOnPerspectiveCanvas();
            if (bounds.IsEmpty) return;
            var points = new PointCollection();
            for (int i = 0; i < 4; i++)
            {
                Point p = new(bounds.Left + PerspectiveCorners[i].X * bounds.Width,
                              bounds.Top + PerspectiveCorners[i].Y * bounds.Height);
                points.Add(p);
                Canvas.SetLeft(_perspectiveHandles[i], p.X - 9);
                Canvas.SetTop(_perspectiveHandles[i], p.Y - 9);
            }
            _perspectiveOutline.Points = points;
        }

        private void PerspectiveHandle_Down(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Ellipse { Tag: int index }) return;
            _dragPerspective = index;
            Mouse.Capture(_perspectiveCanvas, CaptureMode.SubTree);
            DragCursors.BeginDrag();
            e.Handled = true;
        }

        private void PerspectiveCanvas_Move(object sender, MouseEventArgs e)
        {
            if (_dragPerspective < 0 || e.LeftButton != MouseButtonState.Pressed) return;
            Rect bounds = PreviewBoundsOnPerspectiveCanvas();
            if (bounds.IsEmpty) return;
            Point p = e.GetPosition(_perspectiveCanvas);
            PerspectiveCorners[_dragPerspective] = new Point(
                Math.Max(0, Math.Min(1, (p.X - bounds.Left) / bounds.Width)),
                Math.Max(0, Math.Min(1, (p.Y - bounds.Top) / bounds.Height)));
            UpdatePerspectiveOverlay();
        }

        private void PerspectiveCanvas_Up(object sender, MouseButtonEventArgs e)
        {
            _dragPerspective = -1;
            if (ReferenceEquals(Mouse.Captured, _perspectiveCanvas)) Mouse.Capture(null);
            DragCursors.EndDrag();
            e.Handled = true;
        }

        // ---- Alignment-line deskew: drag a line, release, and the page rotates to make that line level. ----
        // Maps a point in the preview image to page coordinates in points (clamped to the page).
        private Point PreviewToPagePts(Point pInPreview)
        {
            double w = _preview.ActualWidth, h = _preview.ActualHeight;
            double fx = w > 0 ? Math.Max(0, Math.Min(1, pInPreview.X / w)) : 0;
            double fy = h > 0 ? Math.Max(0, Math.Min(1, pInPreview.Y / h)) : 0;
            return new Point(fx * _pageWpt, fy * _pageHpt);
        }

        private void ShowLineCoords(Point endPage)
            => _lineCoords.Text = $"Start  {_startPagePt.X:0}, {_startPagePt.Y:0} pt\nEnd    {endPage.X:0}, {endPage.Y:0} pt";

        private void LineCanvas_Down(object sender, MouseButtonEventArgs e)
        {
            _drawingLine = true;
            _lineStart = e.GetPosition(_lineCanvas);
            _alignLine.X1 = _alignLine.X2 = _lineStart.X;
            _alignLine.Y1 = _alignLine.Y2 = _lineStart.Y;
            _alignLine.Visibility = Visibility.Visible;
            _startPagePt = PreviewToPagePts(e.GetPosition(_preview));
            ShowLineCoords(_startPagePt);
            _lineCanvas.CaptureMouse();
        }

        private void LineCanvas_Move(object sender, MouseEventArgs e)
        {
            if (!_drawingLine) return;
            var p = e.GetPosition(_lineCanvas);
            _alignLine.X2 = p.X;
            _alignLine.Y2 = p.Y;
            ShowLineCoords(PreviewToPagePts(e.GetPosition(_preview)));
        }

        private void LineCanvas_Up(object sender, MouseButtonEventArgs e)
        {
            if (!_drawingLine) return;
            _drawingLine = false;
            _lineCanvas.ReleaseMouseCapture();

            double dx = _alignLine.X2 - _alignLine.X1;
            double dy = _alignLine.Y2 - _alignLine.Y1;
            _alignLine.Visibility = Visibility.Collapsed;
            if (dx * dx + dy * dy < 100) return;   // ignore an accidental tap

            // Screen angle of the line (clockwise positive, since Y is down). Normalize to an undirected
            // (-90, 90], then snap to the nearest axis so a near-vertical drag deskews to vertical.
            double a = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            a %= 180.0;
            if (a > 90.0) a -= 180.0; else if (a < -90.0) a += 180.0;
            if (a > 45.0) a -= 90.0; else if (a < -45.0) a += 90.0;

            // Rotate by -a (on top of the current fine angle) to level the line; the slider drives _fine.
            double newFine = Math.Max(-45.0, Math.Min(45.0, _fine - a));
            _rotSlider.Value = Math.Round(newFine, 1);
        }

        // Throttles the heavy preview compose so slider dragging stays smooth (see the timer in the ctor).
        private void SchedulePreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        // #174 helpers, shared with the full-resolution Apply in Rotate.cs.
        internal static bool LevelsIdentity(int black, int white, double gamma)
            => black <= 0 && white >= 255 && Math.Abs(gamma - 1.0) < 0.01;

        /// <summary>Levels pass: remaps [black..white] to [0..255] through a midtone gamma,
        /// per RGB channel, alpha untouched. Identity settings return the source unchanged.</summary>
        internal static BitmapSource ApplyLevels(BitmapSource src, int black, int white, double gamma)
        {
            if (LevelsIdentity(black, white, gamma)) return src;
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);
            var lut = new byte[256];
            double lo = black, hi = Math.Max(black + 1, white), invG = 1.0 / Math.Max(0.05, gamma);
            for (int i = 0; i < 256; i++)
            {
                double t = (i - lo) / (hi - lo);
                t = t < 0 ? 0 : t > 1 ? 1 : t;
                lut[i] = (byte)Math.Round(Math.Pow(t, invG) * 255);
            }
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i]     = lut[px[i]];
                px[i + 1] = lut[px[i + 1]];
                px[i + 2] = lut[px[i + 2]];
            }
            var bmp = BitmapSource.Create(w, h, conv.DpiX, conv.DpiY, PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }

        private void ResetLevels()
        {
            _lvlBlack.Value = 0; _lvlWhite.Value = 255; _lvlGamma.Value = 1.0;
        }

        private void ResetQuality()
        {
            _colorMode.SelectedIndex = 0;
            _bwThreshold.Value = 160;
            _setDpi.IsChecked = false;
            _dpiSlider.Value = 300;
            _useJpeg.IsChecked = false;
            _jpegSlider.Value = 85;
            ColorMode = PageColorMode.Color;
            BlackWhiteThreshold = 160;
            OutputDpi = 0;
            UseJpegCompression = false;
            JpegQuality = 85;
            SchedulePreview();
        }

        private static TextBlock SliderLabel(string text) => new()
        {
            Text = text, Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont,
            FontSize = 10, Margin = new Thickness(0, 6, 0, 0),
        };

        private void UpdatePreview()
        {
            double total = Total;
            _rotReadout?.Text = $"{total:0.0}°";
            _preview.Source = (total == 0 && _scale == 1.0 && !_flipH && !_flipV)
                ? _src
                : MainWindow.ComposeTransform(_src, total, _scale, _fixedPage, _flipH, _flipV);
            // #174: levels ride on top of whatever geometry the preview shows.
            if (_preview.Source is BitmapSource lvlSrc && !LevelsIdentity(LevelBlack, LevelWhite, LevelGamma))
                _preview.Source = ApplyLevels(lvlSrc, LevelBlack, LevelWhite, LevelGamma);
            if (_preview.Source is BitmapSource colorSrc && ColorMode != PageColorMode.Color)
                _preview.Source = PageQualityConverter.ApplyColorMode(
                    colorSrc, ColorMode, BlackWhiteThreshold);

            if (_sizeReadout != null && _preview.Source is BitmapSource b && _srcW > 0 && _pageWpt > 0)
            {
                double outWin = b.PixelWidth * (_pageWpt / _srcW) / 72.0;
                double outHin = b.PixelHeight * (_pageHpt / _srcH) / 72.0;
                _sizeReadout.Text = string.Format(
                    S("Str_Tf_Output"), outWin.ToString("0.0"), outHin.ToString("0.0"));
            }
            UpdateDpiReadout();
            SizePreviewImage();
        }

        private void UpdateDpiReadout()
        {
            if (_dpiReadout is null || _preview.Source is not BitmapSource b
                || _srcW <= 0 || _srcH <= 0 || _pageWpt <= 0 || _pageHpt <= 0)
                return;

            double widthPoints = b.PixelWidth * (_pageWpt / _srcW);
            double heightPoints = b.PixelHeight * (_pageHpt / _srcH);
            double dpi = _dpiSlider?.Value ?? 300;
            var (width, height) = OutputPixelDimensions.FromPoints(
                widthPoints, heightPoints, dpi);
            _dpiReadout.Text = $"{dpi:0} DPI  |  " +
                string.Format(S("Str_OutputPixels"), width, height) + "  |  " +
                OutputPixelDimensions.ScaleLabel(width, height, b.PixelWidth, b.PixelHeight);
        }

        // Sizes the page to its TRUE relative scale within the preview box, so "Resize the whole page" makes
        // the page visibly shrink (rather than refit to the same size), and rotation visibly grows it.
        // Clamps so the page never overflows the box.
        private void SizePreviewImage()
        {
            if (_previewArea is null || _preview.Source is not BitmapSource bmp || _srcW <= 0 || _srcH <= 0) return;
            const double m = 36;   // breathing room inside the box
            double areaW = Math.Max(1, _previewArea.ActualWidth - m);
            double areaH = Math.Max(1, _previewArea.ActualHeight - m);
            double baseFit = Math.Min(areaW / _srcW, areaH / _srcH);   // scale that fits the original page
            double dispW = bmp.PixelWidth * baseFit;
            double dispH = bmp.PixelHeight * baseFit;
            double clamp = Math.Min(1.0, Math.Min(areaW / dispW, areaH / dispH));   // never overflow the box
            _preview.Width = dispW * clamp;
            _preview.Height = dispH * clamp;
            Dispatcher.BeginInvoke(new Action(UpdatePerspectiveOverlay), DispatcherPriority.Loaded);
        }

        private static TextBlock SectionHeader(string text) => new()
        {
            Text = text, Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont,
            FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4)
        };

        private static void WrapSection(StackPanel host, int start, string title, bool expanded)
        {
            var children = host.Children.Cast<UIElement>().Skip(start).ToList();
            while (host.Children.Count > start) host.Children.RemoveAt(start);
            var body = new StackPanel { Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
            foreach (var child in children) body.Children.Add(child);
            var chevron = new TextBlock
            {
                Text = expanded ? "▾" : "▸", Width = 16, FontSize = 12,
                Foreground = R("MutedTextBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            var label = SectionHeader(title);
            label.Margin = new Thickness(0);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(chevron);
            row.Children.Add(label);
            var header = new Border
            {
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Padding = new Thickness(0, 5, 0, 5), Child = row,
            };
            header.MouseLeftButtonUp += (_, _2) =>
            {
                bool open = body.Visibility != Visibility.Visible;
                body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = open ? "▾" : "▸";
            };
            host.Children.Add(header);
            host.Children.Add(body);
        }

        private static Border Divider()
        {
            var b = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 12) };
            b.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            return b;
        }

        private static DockPanel ValueRow(string label, string value, out TextBlock valueBlock, out Button reset)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            reset = UiKit.Make(S("Str_Tf_Reset"), false);
            reset.Padding = new Thickness(8, 1, 8, 1);
            reset.FontSize = 11;
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(reset);
            valueBlock = new TextBlock
            {
                Text = value, Foreground = R("TextBrush"), FontFamily = UiKit.MonoFont,
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0)
            };
            DockPanel.SetDock(valueBlock, Dock.Right);
            row.Children.Add(valueBlock);
            row.Children.Add(new TextBlock
            {
                Text = label, Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont,
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center
            });
            return row;
        }

        private static RadioButton MakeRadio(string text, bool isChecked, Style? style)
        {
            var rb = new RadioButton
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                IsChecked = isChecked, Foreground = R("TextBrush"),
                FontFamily = UiKit.UiFont, FontSize = 12, Margin = new Thickness(0, 3, 0, 0)
            };
            if (style != null) rb.Style = style;
            return rb;
        }

        private static CheckBox MakeCheck(string text)
        {
            var cb = UiKit.CheckBox(text);
            cb.Margin = new Thickness(0, 3, 0, 0);
            return cb;
        }

    }
}
