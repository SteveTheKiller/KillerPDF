using System;
using KillerPDF.Controls;
using KillerPDF.Shell;
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
    internal enum TransformWindowMode
    {
        Transform,
        ColorCorrection
    }

    /// <summary>
    /// Modal "Transform" window. Renders the current page on its own canvas (so the main view's mode is
    /// irrelevant) and lets the user rotate (quarter turns + fine deskew) and scale it, with the controls in
    /// a right-hand sidebar (the mirror of Print Preview). Apply hands the chosen angle / scale / page-mode
    /// back to the caller, which rasterizes at full resolution. Draggable corner handles are the next step.
    /// </summary>
    internal sealed class TransformWindow : Window
    {
        internal sealed record PagePreview(BitmapSource Source, double WidthPoints,
            double HeightPoints, int DocumentPageNumber, (int Width, int Height)? ImagePixels = null);

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
        public ColorCorrectionSettings ColorAdjustments { get; private set; } = new();
        private readonly List<Slider> _colorAdjustmentSliders = [];
        private readonly CheckBox _autoExposure = null!;
        private readonly CheckBox _autoWhiteBalance = null!;
        public PageColorMode ColorMode { get; private set; }
        public int BlackWhiteThreshold { get; private set; } = 160;
        public int OutputDpi { get; private set; }
        public bool UseJpegCompression { get; private set; }
        public int JpegQuality { get; private set; } = 85;

        public Point[] PerspectiveCorners { get; private set; } =
            [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

        // Every page of the document can be previewed and changed from the window (Steve, 2026-11-12: navigate and
        // modify pages from within the tool). Pages render on demand and stay cached until a change replaces them.
        private readonly int _pageCount;
        private readonly IReadOnlyList<int> _selection;
        private readonly Func<int, PagePreview?> _renderPage;
        private readonly Func<TransformWindow, IReadOnlyList<int>, Task<bool>> _apply;
        private readonly Dictionary<int, PagePreview> _previews = [];
        private CheckBox? _applyToSelection;
        private Button _applyButton = null!;
        private Action _resetAll = () => { };
        private bool _busy;
        private BitmapSource _src;
        private readonly Image _preview = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 }
        };
        private readonly Border _previewArea = null!;
        private readonly FrameworkElement _previewColumn = null!;
        private readonly MatrixTransform _previewTransform = new();
        private Point? _previewPanStart;
        private double _srcW;
        private double _srcH;
        private double _pageWpt;
        private double _pageHpt;
        private (int Width, int Height)? _imagePixels;
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

        // Brush, never SolidColorBrush: any theme may state a surface as a gradient.
        private static Brush R(string key) => PageWindowLayout.ThemeBrush(Application.Current?.TryFindResource(key));
        private static string S(string key) => Application.Current.TryFindResource(key) as string ?? key;

        /// <param name="pageCount">Pages in the document; the flipper walks all of them.</param>
        /// <param name="selection">The pages selected when the window opened (zero-based); the first is shown.</param>
        /// <param name="first">The preview of the selection's first page.</param>
        /// <param name="renderPage">Renders any page's preview, or null when it cannot.</param>
        /// <param name="apply">Changes the given pages with this window's settings; true once they are changed.</param>
        public TransformWindow(Window owner, int pageCount, IReadOnlyList<int> selection, PagePreview first,
            Func<int, PagePreview?> renderPage, Func<TransformWindow, IReadOnlyList<int>, Task<bool>> apply,
            TransformWindowMode mode = TransformWindowMode.Transform)
        {
            ArgumentNullException.ThrowIfNull(selection);
            ArgumentNullException.ThrowIfNull(first);
            if (selection.Count == 0) throw new ArgumentException("At least one page is required.", nameof(selection));
            _pageCount = Math.Max(1, pageCount);
            _selection = selection;
            _renderPage = renderPage;
            _apply = apply;
            _previewPageIndex = selection[0];
            _previews[selection[0]] = first;
            _src = first.Source;
            _srcW = _src.PixelWidth;
            _srcH = _src.PixelHeight;
            _pageWpt = first.WidthPoints;
            _pageHpt = first.HeightPoints;
            _imagePixels = first.ImagePixels;
            string toolName = S(mode == TransformWindowMode.Transform
                ? "Str_Tf_Suffix" : "Str_ColorCorrection");
            Title = "KillerPDF - " + toolName;
            DialogFrame.Configure(this, owner, resizable: true);

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

            // Borrow the main window's themed scrollbar so the options scroller isn't the OS default.
            if (owner?.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style sbStyle)
                Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sbStyle;

            // Footer: "Reset all" on the left, then Cancel and Apply, on one row.
            _resetAll = () =>
            {
                _quarter = 0; _rotSlider.Value = 0; _scaleSlider.Value = 100;
                _resizeRadio.IsChecked = true; _flipHCheck.IsChecked = false; _flipVCheck.IsChecked = false;
                ResetPerspective();
                ResetLevels();   // #174
                ResetColorAdjustments();
                ResetQuality();
            };
            var resetAll = PageWindowLayout.LinkButton(S("Str_Tf_ResetAll"), () => _resetAll());
            // Apply changes the page on screen and keeps the window open, so Cancel becomes Close.
            var cancelBtn = UiKit.Make(S("Str_Lbl_Close"), false);
            cancelBtn.Click += (_, _2) => Close();
            cancelBtn.IsCancel = true;   // Esc
            var applyBtn = _applyButton = UiKit.Make(S("Str_Tf_Apply"), true);
            applyBtn.Click += async (_, _2) => await ApplyCurrentAsync();
            applyBtn.IsDefault = true;   // Enter
            var footer = PageWindowLayout.Footer(resetAll, cancelBtn, applyBtn);

            // ---- ROTATE: quarter turns | angle readout and Reset, then the fine angle across both ----
            var turnRow = new StackPanel { Orientation = Orientation.Horizontal };
            var turnL = SmallButton("↺ 90°");
            turnL.Margin = new Thickness(0, 0, 6, 0);
            turnL.Click += (_, _2) => { _quarter = (_quarter + 3) % 4; UpdatePreview(); };
            var turnR = SmallButton("90° ↻");
            turnR.Click += (_, _2) => { _quarter = (_quarter + 1) % 4; UpdatePreview(); };
            turnRow.Children.Add(turnL);
            turnRow.Children.Add(turnR);
            _rotReadout = new TextBlock
            {
                Text = "0.0°", Foreground = R("TextBrush"), FontFamily = UiKit.MonoFont, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
            };
            var rotReset = SmallButton(S("Str_Tf_Reset"));
            rotReset.Click += (_, _2) => { _quarter = 0; _rotSlider.Value = 0; UpdatePreview(); };
            var angleRow = new DockPanel { Height = PageWindowLayout.ControlHeight, LastChildFill = false };
            DockPanel.SetDock(rotReset, Dock.Right);
            DockPanel.SetDock(_rotReadout, Dock.Right);
            angleRow.Children.Add(rotReset);
            angleRow.Children.Add(_rotReadout);
            _rotSlider = new Slider { Minimum = -45, Maximum = 45, Value = 0, TickFrequency = 1, SmallChange = 0.1, LargeChange = 1 };
            if (darkSlider != null) _rotSlider.Style = darkSlider;
            System.Windows.Automation.AutomationProperties.SetName(_rotSlider, S("Str_Tf_Angle"));
            _rotSlider.ValueChanged += (_, ev) => { _fine = Math.Round(ev.NewValue, 1); _rotReadout?.Text = $"{Total:0.0}°"; SchedulePreview(); };
            var rotate = Grid2(
                PageWindowLayout.Labeled(" ", turnRow),
                PageWindowLayout.Labeled(S("Str_Tf_Angle"), angleRow),
                Span(Cell(Row(_rotSlider))));

            // ---- FLIP ----
            _flipHCheck = MakeCheck(S("Str_Tf_FlipH"));
            _flipHCheck.Checked   += (_, _2) => { _flipH = true;  UpdatePreview(); };
            _flipHCheck.Unchecked += (_, _2) => { _flipH = false; UpdatePreview(); };
            _flipVCheck = MakeCheck(S("Str_Tf_FlipV"));
            _flipVCheck.Checked   += (_, _2) => { _flipV = true;  UpdatePreview(); };
            _flipVCheck.Unchecked += (_, _2) => { _flipV = false; UpdatePreview(); };
            var flip = Grid2(Cell(Row(_flipHCheck)), Cell(Row(_flipVCheck)));

            // ---- SCALE: size slider | when scaling, then the output size across both ----
            _scaleReadout = PageWindowLayout.Readout("100%");
            _scaleSlider = new Slider { Minimum = 25, Maximum = 200, Value = 100, TickFrequency = 5, SmallChange = 1, LargeChange = 10 };
            if (darkSlider != null) _scaleSlider.Style = darkSlider;
            System.Windows.Automation.AutomationProperties.SetName(_scaleSlider, S("Str_Tf_Size"));
            _scaleSlider.ValueChanged += (_, ev) => { _scale = Math.Round(ev.NewValue) / 100.0; _scaleReadout.Text = $"{ev.NewValue:0}%"; SchedulePreview(); };
            var scaleReset = SmallButton(S("Str_Tf_Reset"));
            scaleReset.Margin = new Thickness(6, 0, 0, 0);
            scaleReset.Click += (_, _2) => _scaleSlider.Value = 100;
            var sizeRow = new DockPanel();
            DockPanel.SetDock(scaleReset, Dock.Right);
            sizeRow.Children.Add(scaleReset);
            sizeRow.Children.Add(Row(_scaleSlider));
            _resizeRadio = MakeRadio(S("Str_Tf_ResizePage"), true, themeRadio);
            var fixedRadio = MakeRadio(S("Str_Tf_KeepSize"), false, themeRadio);
            _resizeRadio.Checked += (_, _2) => { _fixedPage = false; UpdatePreview(); };
            fixedRadio.Checked += (_, _2) => { _fixedPage = true; UpdatePreview(); };
            var scalingModes = new StackPanel();
            scalingModes.Children.Add(_resizeRadio);
            scalingModes.Children.Add(fixedRadio);
            // Live output dimensions, so scale changes (including above 100%, where the preview clamps to
            // fit) are always legible as a number even when the page can't grow on screen.
            _sizeReadout = PageWindowLayout.StatusLine();
            var scale = Grid2(
                PageWindowLayout.Labeled(S("Str_Tf_Size"), sizeRow, _scaleReadout),
                PageWindowLayout.Labeled(S("Str_Tf_WhenScaling"), scalingModes),
                Span(Cell(_sizeReadout)));

            // ---- SKEW: level line | live coordinates, then the hint across both ----
            _deskewCheck = MakeCheck(S("Str_Tf_LevelLine"));
            _deskewCheck.Checked   += (_, _2) => { _lineCanvas.IsHitTestVisible = true; };
            _deskewCheck.Unchecked += (_, _2) => { _lineCanvas.IsHitTestVisible = false; _alignLine.Visibility = Visibility.Collapsed; _lineCoords.Text = ""; };
            // Live cursor coordinates (page points), so the user can place the line precisely on the small
            // preview. Start point on press, end point as they drag. Two fixed lines, so the window
            // never grows while dragging.
            _lineCoords = PageWindowLayout.StatusLine();
            _lineCoords.LineHeight = 14;
            _lineCoords.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            _lineCoords.Height = 28;
            var skew = Grid2(
                Cell(Row(_deskewCheck)),
                Cell(_lineCoords),
                Span(PageWindowLayout.Hint(S("Str_Tf_SkewHint"))));

            // ---- PERSPECTIVE: correct | Reset corners, then the hint across both ----
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
            var resetPerspective = SmallButton(S("Str_Tf_ResetCorners"));
            resetPerspective.HorizontalAlignment = HorizontalAlignment.Left;
            resetPerspective.Click += (_, _2) => ResetPerspective();
            var perspective = Grid2(
                Cell(Row(_perspectiveCheck)),
                Cell(resetPerspective),
                Span(PageWindowLayout.Hint(S("Str_Tf_PerspectiveHint"))));

            // ---- COLOR (Color correction): exposure | saturation, auto exposure, white balance ----
            var exposure = AddColorSlider(darkSlider, "Str_Cc_Exposure", -3, 3, 0, 0.1,
                value => ColorAdjustments = ColorAdjustments with { Exposure = value },
                value => value.ToString("+0.00;-0.00;0.00"), continuous: true);
            var saturation = AddColorSlider(darkSlider, "Str_Cc_Saturation", 0, 200, 100, 1,
                value => ColorAdjustments = ColorAdjustments with { Saturation = value / 100 },
                value => value.ToString("0") + "%");
            _autoExposure = MakeCheck(S("Str_Cc_AutoExposure"));
            _autoExposure.Checked += (_, _) => SetAutoExposure(true);
            _autoExposure.Unchecked += (_, _) => SetAutoExposure(false);
            var whiteBalance = new TextBlock
            {
                Text = S("Str_Cc_WhiteBalance"), Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont,
                FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 6)
            };
            var warmth = AddColorSlider(darkSlider, "Str_Cc_Warmth", -100, 100, 0, 1,
                value => ColorAdjustments = ColorAdjustments with { Warmth = value },
                value => value.ToString("+0;-0;0"), "Str_Cc_WarmthHint");
            var tint = AddColorSlider(darkSlider, "Str_Cc_Tint", -100, 100, 0, 1,
                value => ColorAdjustments = ColorAdjustments with { Tint = value },
                value => value.ToString("+0;-0;0"), "Str_Cc_TintHint");
            _autoWhiteBalance = MakeCheck(S("Str_Cc_AutoWhiteBalance"));
            _autoWhiteBalance.Checked += (_, _) => SetAutoWhiteBalance(true);
            _autoWhiteBalance.Unchecked += (_, _) => SetAutoWhiteBalance(false);
            var colorReset = SmallButton(S("Str_Tf_Reset"));
            colorReset.HorizontalAlignment = HorizontalAlignment.Right;
            colorReset.Click += (_, _) => ResetColorAdjustments();
            var color = Grid2(
                exposure, saturation,
                Cell(Row(_autoExposure)), Empty(),
                Span(whiteBalance),
                warmth, tint,
                Cell(Row(_autoWhiteBalance)), Cell(colorReset));

            // #174: LEVELS - FineReader-style source levels for rescuing pale scans. Black point,
            // white point, and a midtone gamma; live in the preview, baked on Apply like every
            // other correction here.
            var blackValue = PageWindowLayout.Readout("0");
            _lvlBlack = new Slider { Minimum = 0, Maximum = 200, Value = 0, TickFrequency = 5, SmallChange = 1, LargeChange = 10 };
            if (darkSlider != null) _lvlBlack.Style = darkSlider;
            System.Windows.Automation.AutomationProperties.SetName(_lvlBlack, S("Str_Tf_LevelsBlack"));
            _lvlBlack.ValueChanged += (_, ev) => { LevelBlack = (int)Math.Round(ev.NewValue); blackValue.Text = LevelBlack.ToString(); SchedulePreview(); };
            var whiteValue = PageWindowLayout.Readout("255");
            _lvlWhite = new Slider { Minimum = 55, Maximum = 255, Value = 255, TickFrequency = 5, SmallChange = 1, LargeChange = 10 };
            if (darkSlider != null) _lvlWhite.Style = darkSlider;
            System.Windows.Automation.AutomationProperties.SetName(_lvlWhite, S("Str_Tf_LevelsWhite"));
            _lvlWhite.ValueChanged += (_, ev) => { LevelWhite = (int)Math.Round(ev.NewValue); whiteValue.Text = LevelWhite.ToString(); SchedulePreview(); };
            var gammaValue = PageWindowLayout.Readout("1.00");
            _lvlGamma = new Slider { Minimum = 0.2, Maximum = 2.5, Value = 1.0, TickFrequency = 0.05, SmallChange = 0.05, LargeChange = 0.2 };
            if (darkSlider != null) _lvlGamma.Style = darkSlider;
            System.Windows.Automation.AutomationProperties.SetName(_lvlGamma, S("Str_Tf_LevelsGamma"));
            _lvlGamma.ValueChanged += (_, ev) => { LevelGamma = Math.Round(ev.NewValue, 2); gammaValue.Text = LevelGamma.ToString("0.00"); SchedulePreview(); };
            var levelsReset = SmallButton(S("Str_Tf_Reset"));
            levelsReset.HorizontalAlignment = HorizontalAlignment.Right;
            levelsReset.Click += (_, _2) => ResetLevels();
            var levels = Grid2(
                PageWindowLayout.Labeled(S("Str_Tf_LevelsBlack"), Row(_lvlBlack), blackValue),
                PageWindowLayout.Labeled(S("Str_Tf_LevelsWhite"), Row(_lvlWhite), whiteValue),
                PageWindowLayout.Labeled(S("Str_Tf_LevelsGamma"), Row(_lvlGamma), gammaValue),
                PageWindowLayout.Labeled(" ", levelsReset));

            // #173: output quality applies after geometry and levels. Defaults preserve the
            // existing lossless, automatic-resolution Transform behavior.
            _colorMode = new ComboBox
            {
                ItemsSource = new[]
                {
                    S("Str_Print_Color"), S("Str_Tf_Grayscale"), S("Str_Tf_BlackWhite")
                },
                SelectedIndex = 0,
                Height = PageWindowLayout.ControlHeight
            };
            if (darkCombo != null) _colorMode.Style = darkCombo;
            System.Windows.Automation.AutomationProperties.SetName(_colorMode, S("Str_Tf_ColorMode"));
            _colorMode.SelectionChanged += (_, _) =>
            {
                ColorMode = (PageColorMode)Math.Max(0, _colorMode.SelectedIndex);
                _bwThreshold.IsEnabled = ColorMode == PageColorMode.BlackAndWhite;
                SchedulePreview();
            };
            _bwThreshold = new Slider
            {
                Minimum = 0, Maximum = 255, Value = 160, IsEnabled = false,
                TickFrequency = 5, SmallChange = 1, LargeChange = 10,
            };
            if (darkSlider != null) _bwThreshold.Style = darkSlider;
            System.Windows.Automation.AutomationProperties.SetName(_bwThreshold, S("Str_Tf_Threshold"));
            var thresholdValue = PageWindowLayout.Readout("160");
            _bwThreshold.ValueChanged += (_, ev) =>
            {
                BlackWhiteThreshold = (int)Math.Round(ev.NewValue);
                thresholdValue.Text = BlackWhiteThreshold.ToString();
                SchedulePreview();
            };

            _setDpi = MakeCheck(S("Str_Tf_SetDpi"));
            _setDpi.Checked += (_, _) => { OutputDpi = (int)Math.Round(_dpiSlider.Value); _dpiSlider.IsEnabled = true; };
            _setDpi.Unchecked += (_, _) => { OutputDpi = 0; _dpiSlider.IsEnabled = false; };
            // DPI, pixel size and scale: one line across both columns, the full text in its tooltip.
            _dpiReadout = PageWindowLayout.StatusLine();
            _dpiSlider = new Slider
            {
                Minimum = 72, Maximum = 600, Value = OneTimesDpi(), IsEnabled = false,
                TickFrequency = 25, SmallChange = 1, LargeChange = 25,
            };
            if (darkSlider != null) _dpiSlider.Style = darkSlider;
            System.Windows.Automation.AutomationProperties.SetName(_dpiSlider, S("Str_Tf_SetDpi"));
            _dpiSlider.ValueChanged += (_, ev) =>
            {
                if (_setDpi.IsChecked == true) OutputDpi = (int)Math.Round(ev.NewValue);
                UpdateDpiReadout();
            };

            _useJpeg = MakeCheck(S("Str_Tf_UseJpeg"));
            _useJpeg.Checked += (_, _) => { UseJpegCompression = true; _jpegSlider.IsEnabled = true; };
            _useJpeg.Unchecked += (_, _) => { UseJpegCompression = false; _jpegSlider.IsEnabled = false; };
            var jpegValue = PageWindowLayout.Readout("85%");
            _jpegSlider = new Slider
            {
                Minimum = 25, Maximum = 100, Value = 85, IsEnabled = false,
                TickFrequency = 5, SmallChange = 1, LargeChange = 5,
            };
            if (darkSlider != null) _jpegSlider.Style = darkSlider;
            System.Windows.Automation.AutomationProperties.SetName(_jpegSlider, S("Str_Tf_UseJpeg"));
            _jpegSlider.ValueChanged += (_, ev) =>
            {
                JpegQuality = (int)Math.Round(ev.NewValue);
                jpegValue.Text = $"{ev.NewValue:0}%";
            };
            var qualityReset = SmallButton(S("Str_Tf_Reset"));
            qualityReset.HorizontalAlignment = HorizontalAlignment.Left;
            qualityReset.Click += (_, _) => ResetQuality();
            var quality = Grid2(
                PageWindowLayout.Labeled(S("Str_Tf_ColorMode"), _colorMode),
                PageWindowLayout.Labeled(S("Str_Tf_Threshold"), Row(_bwThreshold), thresholdValue),
                Cell(CheckOverSlider(_setDpi, null, _dpiSlider)),
                Cell(CheckOverSlider(_useJpeg, jpegValue, _jpegSlider)),
                Span(Cell(_dpiReadout)),
                Cell(qualityReset), Empty());

            // Transform shows the geometry sections and Color correction its own three, COLOR first.
            // Every section starts open; the controls of the other mode are built but never shown.
            var options = new StackPanel();
            var sections = mode == TransformWindowMode.Transform
                ? new (string Title, UIElement Body)[]
                {
                    (S("Str_Tf_Rotate"), rotate), (S("Str_Tf_Flip"), flip), (S("Str_Tf_Scale"), scale),
                    (S("Str_Tf_Skew"), skew), (S("Str_Tf_Perspective"), perspective)
                }
                : new (string Title, UIElement Body)[]
                {
                    (S("Str_Cc_Color"), color), (S("Str_Tf_Levels"), levels), (S("Str_Tf_Quality"), quality)
                };
            for (int i = 0; i < sections.Length; i++)
            {
                if (i > 0) options.Children.Add(PageWindowLayout.Divider());
                options.Children.Add(PageWindowLayout.Section(sections[i].Title, sections[i].Body, out _));
            }

            // ---- Preview area: a documentbg box (1px frame, margin, rounded) with grain in the margins and
            //      the page (sized to its true relative scale, with a drop shadow) centered on top. ----
            var previewWrap = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = UiKit.RadCard,
                Margin = new Thickness(8, 8, 8, _pageCount > 1 ? 0 : 8),
                ClipToBounds = true
            };
            previewWrap.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
            previewWrap.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");

            var previewGrid = new Grid();
            var pgGrain = (owner as MainWindow)?.GrainTextureSource;
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
            // A capture lost to an alt-tab or a dialog never reaches the button-up handler, which
            // would leave the line following the pointer with no button held.
            _lineCanvas.LostMouseCapture += (_, _) => { _drawingLine = false; _alignLine.Visibility = Visibility.Collapsed; };
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
            if (mode == TransformWindowMode.ColorCorrection)
            {
                _preview.RenderTransform = _previewTransform;
                previewGrid.PreviewMouseWheel += (_, e) =>
                {
                    ZoomPreview(e.Delta, e.GetPosition(_preview));
                    e.Handled = true;
                };
                previewGrid.MouseLeftButtonDown += (_, e) =>
                {
                    if (_previewTransform.Matrix.M11 <= 1) return;
                    _previewPanStart = e.GetPosition(previewGrid);
                    previewGrid.CaptureMouse();
                    previewGrid.Cursor = Cursors.SizeAll;
                    e.Handled = true;
                };
                previewGrid.MouseMove += (_, e) =>
                {
                    if (_previewPanStart is not Point start) return;
                    Point current = e.GetPosition(previewGrid);
                    Matrix matrix = _previewTransform.Matrix;
                    matrix.Translate(current.X - start.X, current.Y - start.Y);
                    _previewTransform.Matrix = matrix;
                    _previewPanStart = current;
                    ClampPreviewPan();
                };
                previewGrid.MouseLeftButtonUp += (_, e) =>
                {
                    if (_previewPanStart is null) return;
                    previewGrid.ReleaseMouseCapture();
                    e.Handled = true;
                };
                previewGrid.LostMouseCapture += (_, _) =>
                {
                    _previewPanStart = null;
                    previewGrid.Cursor = Cursors.Arrow;
                };
            }
            previewWrap.SizeChanged += (_, _2) =>
            {
                double radius = previewWrap.CornerRadius.TopLeft;
                previewGrid.Clip = new RectangleGeometry(
                    new Rect(0, 0, previewGrid.ActualWidth, previewGrid.ActualHeight), radius, radius);
                SizePreviewImage();
            };

            var previewColumn = new DockPanel();
            if (_selection.Count > 1)
            {
                // Opened on several pages: Apply changes all of them at once while this is ticked, as before.
                _applyToSelection = new CheckBox
                {
                    Content = string.Format(S("Str_Divide_Selected"), _selection.Count), IsChecked = true,
                    Margin = new Thickness(10, 0, 8, 6), Foreground = R("TextBrush")
                };
                DockPanel.SetDock(_applyToSelection, Dock.Bottom);
                previewColumn.Children.Add(_applyToSelection);
            }
            if (_pageCount > 1)
            {
                var navigation = new Grid { Height = 36, Margin = new Thickness(8, 2, 8, 6) };
                navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                navigation.ColumnDefinitions.Add(new ColumnDefinition());
                navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _previousPage = PageNavButtons.Previous(() => ShowPreviewPage(_previewPageIndex - 1));
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
                _nextPage = PageNavButtons.Next(() => ShowPreviewPage(_previewPageIndex + 1));
                Grid.SetColumn(_nextPage, 2);
                navigation.Children.Add(_nextPage);
                DockPanel.SetDock(navigation, Dock.Bottom);
                previewColumn.Children.Add(navigation);
            }
            // Family shadow under the content pane, like the main window (flat on 98SE).
            previewColumn.Children.Add(UiKit.PaneWithShadow(previewWrap));
            _previewColumn = previewColumn;

            // A work area narrower than the minimum window (456 options + 24 halo + 282 preview)
            // falls back to one 288 wide column of options.
            int columns = SystemParameters.WorkArea.Width < PageWindowLayout.OptionsWidth + 24 + 282 ? 1 : 2;
            var root = PageWindowLayout.Body(options, footer, previewColumn, out _, columns);

            Content = DialogFrame.Frame(this, Owner, "KillerPDF - " + toolName, Close, root);
            // Color correction opens with focus on its first setting, Exposure, so Tab and the arrow keys
            // start there; a slider leaves Enter to Apply. Transform keeps today's open focus: its first
            // control is the 90-degree button, which would take Enter away from Apply.
            if (mode == TransformWindowMode.ColorCorrection)
                Loaded += (_, _2) => _colorAdjustmentSliders[0].Focus();
            // As tall as the open options and the footer, resizable from every edge, remembered per
            // mode; the first open widens until the page fills the preview's height.
            DialogFrame.EnablePageSizing(this, mode == TransformWindowMode.Transform ? "Transform" : "ColorCorrection",
                _previewColumn, fittedExtraWidth: () => PageWindowLayout.FittedExtraWidth(
                    new Size(_previewArea.ActualWidth - 2, _previewArea.ActualHeight - 2), new Size(_srcW, _srcH)));
            UpdatePreview();   // populate the output-size readout at the original dimensions
            UpdatePageNavigation();

            // Esc-to-close is wired by DialogFrame.Frame; Enter commits. Arrow keys flip through
            // batch previews unless a slider currently owns the key press.
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { _ = ApplyCurrentAsync(); e.Handled = true; }
                else if (_pageCount > 1 && e.OriginalSource is not Slider && e.Key == Key.Left)
                { ShowPreviewPage(_previewPageIndex - 1); e.Handled = true; }
                else if (_pageCount > 1 && e.OriginalSource is not Slider && e.Key == Key.Right)
                { ShowPreviewPage(_previewPageIndex + 1); e.Handled = true; }
            };
        }

        private double Total => _quarter * 90 + _fine;

        private void ZoomPreview(int delta, Point imagePoint)
        {
            Matrix matrix = _previewTransform.Matrix;
            double zoom = Math.Clamp(matrix.M11 * Math.Pow(1.2, delta / 120.0), 1, 8);
            if (zoom == 1) matrix = Matrix.Identity;
            else
            {
                Point anchor = matrix.Transform(imagePoint);
                double factor = zoom / matrix.M11;
                matrix.ScaleAt(factor, factor, anchor.X, anchor.Y);
            }
            _previewTransform.Matrix = matrix;
            ClampPreviewPan();
        }

        private void ClampPreviewPan()
        {
            if (_previewArea.Child is not FrameworkElement viewport) return;
            Matrix matrix = _previewTransform.Matrix;
            static double Offset(double offset, double image, double viewport, double zoom)
            {
                if (image <= 0 || viewport <= 0) return offset;
                double origin = (viewport - image) / 2;
                return image * zoom <= viewport ? -image * (zoom - 1) / 2
                    : Math.Clamp(offset, viewport - origin - image * zoom, -origin);
            }
            matrix.OffsetX = Offset(matrix.OffsetX, _preview.ActualWidth, viewport.ActualWidth, matrix.M11);
            matrix.OffsetY = Offset(matrix.OffsetY, _preview.ActualHeight, viewport.ActualHeight, matrix.M22);
            _previewTransform.Matrix = matrix;
        }

        private PagePreview? PreviewFor(int index)
        {
            if (_previews.TryGetValue(index, out var cached)) return cached;
            if (_renderPage(index) is not { } rendered) return null;
            _previews[index] = rendered;
            return rendered;
        }

        private void ShowPreviewPage(int index, bool force = false)
        {
            if (_busy || index < 0 || index >= _pageCount || (index == _previewPageIndex && !force)) return;
            if (PreviewFor(index) is not { } page) return;
            _previewPageIndex = index;
            _src = page.Source;
            _srcW = _src.PixelWidth;
            _srcH = _src.PixelHeight;
            _pageWpt = page.WidthPoints;
            _pageHpt = page.HeightPoints;
            _imagePixels = page.ImagePixels;
            _alignLine.Visibility = Visibility.Collapsed;
            _lineCoords.Text = "";
            UpdatePreview();
            UpdatePageNavigation();
        }

        private void UpdatePageNavigation()
        {
            if (_pageCount <= 1 || _pageCounter is null) return;
            _pageCounter.Text = $"{_previewPageIndex + 1} / {_pageCount}";
            _previousPage.IsEnabled = !_busy && _previewPageIndex > 0;
            _nextPage.IsEnabled = !_busy && _previewPageIndex + 1 < _pageCount;
        }

        // The live settings, copied into the properties the main window applies.
        private void CaptureSettings()
        {
            Angle = Total;
            Scale = _scale;
            FixedPage = _fixedPage;
            FlipH = _flipH;
            FlipV = _flipV;
            PerspectiveCorners = [.. PerspectiveCorners];
        }

        /// <summary>True when the captured settings would change a page.</summary>
        internal bool HasChanges => Math.Abs(Angle) > 0.01 || Math.Abs(Scale - 1.0) > 0.001 || FlipH || FlipV
            || !PerspectiveWarp.IsIdentity(PerspectiveCorners) || !LevelsIdentity(LevelBlack, LevelWhite, LevelGamma)
            || !ColorAdjustments.IsIdentity || ColorMode != PageColorMode.Color || OutputDpi > 0 || UseJpegCompression;

        // Apply changes the page on screen (or the opening selection while its box is ticked) and keeps the window
        // open, so another page can be flipped to and changed; each Apply is its own undo step. The settings reset
        // afterwards so a second Apply never doubles a change, and the page reloads from the changed document.
        private async Task ApplyCurrentAsync()
        {
            if (_busy) return;
            CaptureSettings();
            if (!HasChanges) return;
            IReadOnlyList<int> targets = _applyToSelection?.IsChecked == true ? _selection : [_previewPageIndex];
            _busy = true;
            _applyButton.IsEnabled = false;
            UpdatePageNavigation();
            bool changed;
            try { changed = await _apply(this, targets); }
            finally
            {
                _busy = false;
                _applyButton.IsEnabled = true;
            }
            if (changed)
            {
                Applied = true;
                foreach (int page in targets) _previews.Remove(page);
                if (_applyToSelection is not null) _applyToSelection.IsChecked = false;   // done; next Applies go page by page
                _resetAll();
                ShowPreviewPage(_previewPageIndex, force: true);
            }
            UpdatePageNavigation();
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
            => _lineCoords.Text = string.Format(S("Str_Tf_LineCoords"), _startPagePt.X.ToString("0"), _startPagePt.Y.ToString("0"), endPage.X.ToString("0"), endPage.Y.ToString("0"));

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

        // A labelled color slider cell (label and mono readout over the slider). The sliders are
        // listed in creation order: exposure, saturation, warmth, tint.
        private FrameworkElement AddColorSlider(Style? style, string labelKey,
            double minimum, double maximum, double initial, double step,
            Action<double> changed, Func<double, string> format, string? hintKey = null,
            bool continuous = false)
        {
            var readout = PageWindowLayout.Readout(format(initial));
            var slider = new Slider
            {
                Minimum = minimum, Maximum = maximum, Value = initial,
                SmallChange = step, LargeChange = step * 10, TickFrequency = step,
                IsSnapToTickEnabled = false,
                Tag = initial,
                ToolTip = hintKey is null ? S(labelKey) : S(hintKey)
            };
            if (style is not null) slider.Style = style;
            System.Windows.Automation.AutomationProperties.SetName(slider, S(labelKey));
            slider.ValueChanged += (_, e) =>
            {
                double value = continuous ? e.NewValue : Math.Round(e.NewValue / step) * step;
                changed(value);
                readout.Text = format(value);
                if (continuous)
                {
                    // Keep rendering during a drag instead of postponing every frame.
                    if (!_previewTimer.IsEnabled) _previewTimer.Start();
                }
                else SchedulePreview();
            };
            _colorAdjustmentSliders.Add(slider);
            return PageWindowLayout.Labeled(S(labelKey), Row(slider), readout);
        }

        private void ResetColorAdjustments()
        {
            _autoExposure.IsChecked = false;
            _autoWhiteBalance.IsChecked = false;
            foreach (var slider in _colorAdjustmentSliders) slider.Value = (double)slider.Tag;
            ColorAdjustments = new();
            SchedulePreview();
        }

        private void SetAutoExposure(bool enabled)
        {
            ColorAdjustments = ColorAdjustments with { AutoExposure = enabled };
            _colorAdjustmentSliders[0].IsEnabled = !enabled;
            SchedulePreview();
        }

        private void SetAutoWhiteBalance(bool enabled)
        {
            ColorAdjustments = ColorAdjustments with { AutoWhiteBalance = enabled };
            _colorAdjustmentSliders[2].IsEnabled = !enabled;
            _colorAdjustmentSliders[3].IsEnabled = !enabled;
            SchedulePreview();
        }

        // The slider starts at x1 for a page that is one image, and at 300 otherwise.
        private double OneTimesDpi() => Math.Clamp(
            OutputPixelDimensions.NativeDpi(_imagePixels, _pageWpt, _pageHpt, 300), 72, 600);

        private void ResetQuality()
        {
            _colorMode.SelectedIndex = 0;
            _bwThreshold.Value = 160;
            _setDpi.IsChecked = false;
            _dpiSlider.Value = OneTimesDpi();
            _useJpeg.IsChecked = false;
            _jpegSlider.Value = 85;
            ColorMode = PageColorMode.Color;
            BlackWhiteThreshold = 160;
            OutputDpi = 0;
            UseJpegCompression = false;
            JpegQuality = 85;
            SchedulePreview();
        }

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
            if (_preview.Source is BitmapSource adjustmentSource)
                _preview.Source = ColorCorrection.Apply(adjustmentSource, ColorAdjustments);
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
            var (baseWidth, baseHeight) = OutputPixelDimensions.ScaleBase(
                b.PixelWidth, b.PixelHeight, _srcW, _srcH, _imagePixels);
            _dpiReadout.Text = $"{dpi:0} DPI  |  " +
                string.Format(S("Str_OutputPixels"), width, height) + "  |  " +
                OutputPixelDimensions.ScaleLabel(width, height, baseWidth, baseHeight);
        }

        // Sizes the page to its TRUE relative scale within the preview box, so "Resize the whole page" makes
        // the page visibly shrink (rather than refit to the same size), and rotation visibly grows it.
        // Clamps so the page never overflows the box.
        private void SizePreviewImage()
        {
            if (_previewArea is null || _preview.Source is not BitmapSource bmp || _srcW <= 0 || _srcH <= 0) return;
            // Inside the 1px frame border, less the 16 page margin on every side.
            var frame = new Size(Math.Max(0, _previewArea.ActualWidth - 2), Math.Max(0, _previewArea.ActualHeight - 2));
            Size page = PageWindowLayout.FitPage(frame, new Size(_srcW, _srcH), new Size(bmp.PixelWidth, bmp.PixelHeight));
            _preview.Width = page.Width;
            _preview.Height = page.Height;
            Dispatcher.BeginInvoke(new Action(ClampPreviewPan), DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(UpdatePerspectiveOverlay), DispatcherPriority.Loaded);
        }

        // ---- options layout helpers (spec 2.2: 26 high controls, 8 under each cell) ----
        private static TwoColumnPanel Grid2(params UIElement[] cells)
        {
            var grid = new TwoColumnPanel();
            foreach (var cell in cells) grid.Children.Add(cell);
            return grid;
        }

        private static UIElement Span(UIElement cell)
        {
            TwoColumnPanel.SetSpan(cell, true);
            return cell;
        }

        private static FrameworkElement Cell(UIElement content) => new Border { Margin = new Thickness(0, 0, 0, 8), Child = content };

        private static FrameworkElement Empty() => new Border();

        // A control centered in a 26 high row that may grow when its label wraps.
        private static FrameworkElement Row(FrameworkElement control)
        {
            // The app's DarkSlider style takes sliders out of Tab; here every slider is a Tab stop and
            // its arrow keys move it (the page-flip keys already step aside for a focused slider).
            if (control is Slider slider) slider.Focusable = true;
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(0);
            return new Border { MinHeight = PageWindowLayout.ControlHeight, Child = control };
        }

        // A checkbox with an optional readout on its row, and its slider under it.
        private static FrameworkElement CheckOverSlider(CheckBox check, TextBlock? readout, Slider slider)
        {
            var head = new DockPanel();
            if (readout != null)
            {
                readout.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(readout, Dock.Right);
                head.Children.Add(readout);
            }
            head.Children.Add(Row(check));
            var cell = new StackPanel();
            cell.Children.Add(head);
            cell.Children.Add(Row(slider));
            return cell;
        }

        private static Button SmallButton(string text)
        {
            var button = UiKit.Make(text, false);
            button.Height = PageWindowLayout.ControlHeight;
            button.Padding = new Thickness(10, 0, 10, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            return button;
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
            cb.Margin = new Thickness(0);
            return cb;
        }

    }
}
