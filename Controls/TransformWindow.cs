using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

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
        public bool Applied { get; private set; }
        public double Angle { get; private set; }     // total = quarter turns + fine
        public double Scale { get; private set; } = 1.0;
        public bool FixedPage { get; private set; }    // true = keep page size (margins); false = resize page
        public bool FlipH { get; private set; }
        public bool FlipV { get; private set; }

        private readonly BitmapSource _src;
        private readonly Image _preview = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 }
        };
        private readonly Border _previewArea = null!;
        private readonly double _srcW;
        private readonly double _srcH;
        private readonly double _pageWpt;
        private readonly double _pageHpt;
        private readonly TextBlock _sizeReadout = null!;
        private int _quarter;        // 0..3 quarter turns clockwise
        private double _fine;        // fine deskew, degrees
        private double _scale = 1.0;
        private bool _fixedPage;
        private readonly TextBlock _rotReadout = null!;
        private readonly TextBlock _scaleReadout = null!;
        private readonly Slider _rotSlider = null!;
        private readonly Slider _scaleSlider = null!;
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

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];
        private static string S(string key) => Application.Current.TryFindResource(key) as string ?? key;

        public TransformWindow(Window owner, BitmapSource src, double pageWpt, double pageHpt)
        {
            _src = src;
            _srcW = src.PixelWidth;
            _srcH = src.PixelHeight;
            _pageWpt = pageWpt;
            _pageHpt = pageHpt;
            Title = "KillerPDF - " + S("Str_Tf_Suffix");
            Width = 980;
            Height = 720;
            MinWidth = 640;
            MinHeight = 460;
            DialogChrome.Configure(this, owner, resizable: true);

            var darkSlider = owner?.TryFindResource("DarkSlider") as Style;
            var themeRadio = owner?.TryFindResource("ThemeRadio") as Style;

            // Coalesce rapid slider changes: the heavy compose (especially scaling a page up, which makes a
            // big bitmap) only runs ~25x/sec on the latest value, so dragging stays smooth instead of queuing
            // a backlog of full re-renders.
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _previewTimer.Tick += (_, _2) => { _previewTimer.Stop(); UpdatePreview(); };

            var root = new DockPanel();

            // ---- Right sidebar (transparent so it blends with the dark title bar, like Print Preview) ----
            var sidebar = new Border { Width = 288, Background = Brushes.Transparent, Padding = new Thickness(16, 8, 16, 14) };
            DockPanel.SetDock(sidebar, Dock.Right);

            var side = new DockPanel();

            // Bottom: a "Reset all" text link on its own line (translations like "Tout reinitialiser" are
            // long), with Cancel / Apply right-aligned beneath it - so nothing crowds or clips.
            var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var resetAll = new TextBlock
            {
                Text = S("Str_Tf_ResetAll"), FontFamily = UiKit.UiFont, FontSize = 12,
                Foreground = R("MutedTextBrush"), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left
            };
            resetAll.MouseEnter += (_, _2) => resetAll.Foreground = R("PrimaryBrush");
            resetAll.MouseLeave += (_, _2) => resetAll.Foreground = R("MutedTextBrush");
            resetAll.MouseLeftButtonUp += (_, _2) => { _quarter = 0; _rotSlider.Value = 0; _scaleSlider.Value = 100; _resizeRadio.IsChecked = true; _flipHCheck.IsChecked = false; _flipVCheck.IsChecked = false; };
            bottom.Children.Add(resetAll);
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var cancelBtn = UiKit.Make(S("Str_Tf_Cancel"), false);
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.Click += (_, _2) => { Applied = false; Close(); };
            cancelBtn.IsCancel = true;   // Esc
            actionRow.Children.Add(cancelBtn);
            var applyBtn = UiKit.Make(S("Str_Tf_Apply"), true);
            applyBtn.Click += (_, _2) => CommitAndClose();
            applyBtn.IsDefault = true;   // Enter
            actionRow.Children.Add(applyBtn);
            bottom.Children.Add(actionRow);
            DockPanel.SetDock(bottom, Dock.Bottom);
            side.Children.Add(bottom);

            var stack = new StackPanel();

            stack.Children.Add(SectionHeader(S("Str_Tf_Rotate")));
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
            _rotSlider.ValueChanged += (_, ev) => { _fine = Math.Round(ev.NewValue, 1); if (_rotReadout != null) _rotReadout.Text = $"{Total:0.0}°"; SchedulePreview(); };
            stack.Children.Add(_rotSlider);
            stack.Children.Add(ValueRow(S("Str_Tf_Angle"), "0.0°", out _rotReadout, out var rotReset));
            rotReset.Click += (_, _2) => { _quarter = 0; _rotSlider.Value = 0; UpdatePreview(); };

            stack.Children.Add(Divider());

            stack.Children.Add(SectionHeader(S("Str_Tf_Scale")));
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

            stack.Children.Add(Divider());
            stack.Children.Add(SectionHeader(S("Str_Tf_Flip")));
            _flipHCheck = MakeCheck(S("Str_Tf_FlipH"));
            _flipHCheck.Checked   += (_, _2) => { _flipH = true;  UpdatePreview(); };
            _flipHCheck.Unchecked += (_, _2) => { _flipH = false; UpdatePreview(); };
            stack.Children.Add(_flipHCheck);
            _flipVCheck = MakeCheck(S("Str_Tf_FlipV"));
            _flipVCheck.Checked   += (_, _2) => { _flipV = true;  UpdatePreview(); };
            _flipVCheck.Unchecked += (_, _2) => { _flipV = false; UpdatePreview(); };
            stack.Children.Add(_flipVCheck);

            stack.Children.Add(Divider());
            stack.Children.Add(SectionHeader(S("Str_Tf_Skew")));
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

            side.Children.Add(stack);
            sidebar.Child = side;
            root.Children.Add(sidebar);

            // ---- Preview area: a documentbg box (1px frame, margin, rounded) with grain in the margins and
            //      the page (sized to its true relative scale, with a drop shadow) centred on top. ----
            var previewWrap = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
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

            previewWrap.Child = previewGrid;
            _previewArea = previewWrap;
            previewWrap.SizeChanged += (_, _2) => SizePreviewImage();
            root.Children.Add(previewWrap);

            Content = DialogChrome.Frame(this, Owner, "KillerPDF - " + S("Str_Tf_Suffix"), () => { Applied = false; Close(); }, root);
            UpdatePreview();   // populate the output-size readout at the original dimensions

            // Esc-to-close is wired by DialogChrome.Frame; Enter commits.
            KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitAndClose(); };
        }

        private double Total => _quarter * 90 + _fine;

        private void CommitAndClose()
        {
            Applied = true;
            Angle = Total;
            Scale = _scale;
            FixedPage = _fixedPage;
            FlipH = _flipH;
            FlipV = _flipV;
            Close();
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

            // Screen angle of the line (clockwise positive, since Y is down). Normalise to an undirected
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

        private void UpdatePreview()
        {
            double total = Total;
            if (_rotReadout != null) _rotReadout.Text = $"{total:0.0}°";
            _preview.Source = (total == 0 && _scale == 1.0 && !_flipH && !_flipV)
                ? _src
                : MainWindow.ComposeTransform(_src, total, _scale, _fixedPage, _flipH, _flipV);

            if (_sizeReadout != null && _preview.Source is BitmapSource b && _srcW > 0 && _pageWpt > 0)
            {
                double outWin = b.PixelWidth * (_pageWpt / _srcW) / 72.0;
                double outHin = b.PixelHeight * (_pageHpt / _srcH) / 72.0;
                _sizeReadout.Text = string.Format(S("Str_Tf_Output"), outWin.ToString("0.0"), outHin.ToString("0.0"));
            }
            SizePreviewImage();
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
        }

        private TextBlock SectionHeader(string text) => new()
        {
            Text = text, Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont,
            FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4)
        };

        private Border Divider()
        {
            var b = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 12) };
            b.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            return b;
        }

        private DockPanel ValueRow(string label, string value, out TextBlock valueBlock, out Button reset)
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

        private RadioButton MakeRadio(string text, bool isChecked, Style? style)
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

        private CheckBox MakeCheck(string text)
        {
            var cb = UiKit.CheckBox(text);
            cb.Margin = new Thickness(0, 3, 0, 0);
            return cb;
        }

    }
}
