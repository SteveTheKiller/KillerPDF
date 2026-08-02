using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace KillerPDF
{
    /// <summary>
    /// A small, themed RGB color picker: saturation/value square + hue strip, RGB and HTML-hex inputs,
    /// a desktop-wide crosshair eyedropper, and a row of 9 fixed swatches that double as the annotate-bar
    /// palette (shared "UserSwatches" setting). Replace overwrites one slot with the current color;
    /// Reset restores defaults. Opacity is left to the annotate bar's slider, so this is opaque-RGB only.
    /// </summary>
    internal sealed class ColorPickerDialog : Window
    {
        public Color SelectedColor { get; private set; }

        /// <summary>True once OK committed. Callers must read THIS, not ShowDialog's return: the
        /// eyedropper opens a nested modal (the capture window, owned by this dialog), and a
        /// nested modal closing inside an outer one can corrupt the outer frame's result - OK set
        /// DialogResult = true and ShowDialog still returned false, silently discarding the pick.
        /// Proven by trace 2026-08-01: Accept -> #FFFEFEFE, PickerClosed(result=False), and every
        /// "shapes draw the wrong color" report back to the purple era was this one drop.</summary>
        public bool Accepted { get; private set; }
        private double _h, _s = 1, _v = 1;     // HSV state (h 0..360, s/v 0..1)
        private bool _updating;                // guards the field<->thumb<->preview sync from feedback loops
        private Border _svArea = null!;
        private Canvas _svThumb = null!;
        private Border _hueThumb = null!;
        private Rectangle _svHue = null!;
        private TextBox _rBox = null!, _gBox = null!, _bBox = null!, _hexBox = null!;
        private Border _newSwatch = null!;
        private WrapPanel _savedRow = null!;
        private Border _replaceBtn = null!;
        private bool _replaceArmed;            // when on, the next swatch click is overwritten, not selected
        public event Action? SwatchesChanged;  // raised when the shared palette is edited, so the bar can live-update
        private const int SvW = 220, SvH = 170, HueW = 18;
        private const int SwatchCell = 24, SwatchCols = 9, SwatchMax = 9;   // one clean row of 9 fixed slots
        // Shared with the annotate-bar palette: editing these swatches reconfigures the toolbar colors.
        private const string SavedKey = "UserSwatches";
        // First-run / Reset palette: 9 fixed slots, last one white.
        private static readonly Color[] DefaultSwatches = UiKit.DefaultSwatches;
        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];
        public ColorPickerDialog(Window? owner, Color initial)
        {
            Title = "KillerPDF - Color";
            Width = 300;
            SizeToContent = SizeToContent.Height;
            DialogChrome.Configure(this, owner);
            UseLayoutRounding = true;
            SelectedColor = initial;
            (_h, _s, _v) = RgbToHsv(initial);
            BuildUi();
            SyncFromHsv();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } else if (e.Key == Key.Enter) Accept(); };
        }
        // ── UI ──────────────────────────────────────────────────────────────────
        private void BuildUi()
        {
            var card = new Border
            {
                Background = R("MenuBackgroundBrush"),
                BorderBrush = R("MenuBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.55 }
            };
            var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
            // Film-grain overlay so the dialog carries the same texture as the rest of the app - dimmed
            // by the shared GrainOpacity so it stays subtle (was rendering at full strength before).
            var root = new Grid();
            if (Owner?.TryFindResource("GrainBrushShared") is Brush grain)
            {
                double grainOp = Owner?.TryFindResource("GrainOpacity") is double go ? go : 0.12;
                root.Children.Add(new Border { Background = grain, Opacity = grainOp, CornerRadius = new CornerRadius(6), IsHitTestVisible = false });
            }
            root.Children.Add(panel);
            card.Child = root;
            Content = card;
            // Accent heading with a 1px drop shadow - the shared style for these secondary-window titles.
            var title = new TextBlock
            {
                Text = "Pick a color", Foreground = R("PrimaryBrush"),
                FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 1, Direction = 270, Opacity = 0.7 },
                Cursor = Cursors.SizeAll
            };
            title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            panel.Children.Add(title);
            // SV square + hue strip
            var pickRow = new StackPanel { Orientation = Orientation.Horizontal };
            _svHue = new Rectangle { Width = SvW, Height = SvH };
            var svWhite = new Rectangle { Width = SvW, Height = SvH, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0) };
            var svBlack = new Rectangle { Width = SvW, Height = SvH, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0), 90) };
            _svThumb = new Canvas { Width = SvW, Height = SvH, IsHitTestVisible = false };
            var svDot = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 0, Opacity = 0.8 } };
            _svThumb.Children.Add(svDot);
            var svGrid = new Grid { Width = SvW, Height = SvH };
            svGrid.Children.Add(_svHue); svGrid.Children.Add(svWhite); svGrid.Children.Add(svBlack); svGrid.Children.Add(_svThumb);
            // ClipToBounds off so the indicator dot shows fully when it sits at an edge/corner.
            _svArea = new Border { Width = SvW, Height = SvH, CornerRadius = new CornerRadius(3), ClipToBounds = false,
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1), Child = svGrid, Cursor = Cursors.Cross };
            _svArea.MouseLeftButtonDown += (s, e) => { _svArea.CaptureMouse(); SvPick(e.GetPosition(svGrid)); };
            _svArea.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) SvPick(e.GetPosition(svGrid)); };
            _svArea.MouseLeftButtonUp += (s, e) => _svArea.ReleaseMouseCapture();
            pickRow.Children.Add(_svArea);
            var hueRect = new Rectangle { Width = HueW, Height = SvH, Fill = HueStripBrush() };
            // Themed handle, matching the annotate-bar slider thumbs (accent fill, light outline).
            _hueThumb = new Border { Width = HueW + 6, Height = 6, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
                Background = R("PrimaryBrush"), CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
            var hueCanvas = new Canvas { Width = HueW + 6, Height = SvH };
            Canvas.SetLeft(_hueThumb, -3);
            hueCanvas.Children.Add(_hueThumb);
            var hueGrid = new Grid { Margin = new Thickness(8, 0, 0, 0) };
            hueGrid.Children.Add(hueRect); hueGrid.Children.Add(hueCanvas);
            var hueArea = new Border { Child = hueGrid, Cursor = Cursors.SizeNS, CornerRadius = new CornerRadius(3),
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1) };
            hueArea.MouseLeftButtonDown += (s, e) => { hueArea.CaptureMouse(); HuePick(e.GetPosition(hueRect)); };
            hueArea.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) HuePick(e.GetPosition(hueRect)); };
            hueArea.MouseLeftButtonUp += (s, e) => hueArea.ReleaseMouseCapture();
            pickRow.Children.Add(hueArea);
            panel.Children.Add(pickRow);
            // RGB + hex + preview + eyedropper
            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            _newSwatch = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(3),
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
            inputRow.Children.Add(_newSwatch);
            _rBox = NumBox(); _gBox = NumBox(); _bBox = NumBox();
            inputRow.Children.Add(FieldGroup("R", _rBox));
            inputRow.Children.Add(FieldGroup("G", _gBox));
            inputRow.Children.Add(FieldGroup("B", _bBox));
            _eyedropBtn = new Button
            {
                Width = 28, Height = 22, Margin = new Thickness(8, 14, 0, 0),
                Background = R("BgCanvas"), BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1),
                // No Cursor here: the crosshair belongs to the CAPTURE window that opens on click.
                // On the button it appeared on hover, before the pick had started (Steve, 2026-08-01).
                Content = CrosshairIcon(), ToolTip = "Pick a color from anywhere on screen",
                Template = MakeBtnTemplate()
            };
            // Same hover treatment as the dialog's chips (greyer fill), and RunEyedropper holds the
            // armed tint + accent border for as long as the capture is live (Steve, 2026-08-01).
            _eyedropBtn.MouseEnter += (_, _) => { if (!_eyedropArmed) _eyedropBtn.Background = R("CardBorderBrush"); };
            _eyedropBtn.MouseLeave += (_, _) => { if (!_eyedropArmed) _eyedropBtn.Background = R("BgCanvas"); };
            _eyedropBtn.Click += (_, _) => RunEyedropper();
            inputRow.Children.Add(_eyedropBtn);
            panel.Children.Add(inputRow);
            var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            hexRow.Children.Add(new TextBlock { Text = "Hex", Foreground = R("MutedTextBrush"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _hexBox = MakeTextBox(96);
            _hexBox.MaxLength = 7;
            _hexBox.LostFocus += (_, _) => CommitHex();
            _hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
            hexRow.Children.Add(_hexBox);
            panel.Children.Add(hexRow);
            // Swatch header: Replace (assign current color to a slot) on the left, Reset on the far right.
            var swHeader = new Grid { Margin = new Thickness(0, 12, 0, 5), Width = SwatchCols * SwatchCell };
            _replaceBtn = Chip("Replace", "Click, then click a swatch to set it to the current color");
            _replaceBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _replaceBtn.MouseLeftButtonUp += (_, _) => { _replaceArmed = !_replaceArmed; UpdateReplaceChip(); RebuildSavedRow(); };
            var resetBtn = Chip("Reset", "Reset swatches to defaults");
            resetBtn.HorizontalAlignment = HorizontalAlignment.Right;
            resetBtn.MouseLeftButtonUp += (_, _) => { StoreSaved([.. DefaultSwatches]); _replaceArmed = false; UpdateReplaceChip(); RebuildSavedRow(); SwatchesChanged?.Invoke(); };
            swHeader.Children.Add(_replaceBtn);
            swHeader.Children.Add(resetBtn);
            panel.Children.Add(swHeader);
            _savedRow = new WrapPanel { Width = SwatchCols * SwatchCell };
            panel.Children.Add(_savedRow);
            UpdateReplaceChip();
            RebuildSavedRow();
            // OK / Cancel
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = MakeButton("Cancel", false); cancel.Click += (_, _) => { DialogResult = false; Close(); }; cancel.IsCancel = true;
            var ok = MakeButton("OK", true); ok.Margin = new Thickness(8, 0, 0, 0); ok.Click += (_, _) => Accept(); ok.IsDefault = true;
            btnRow.Children.Add(cancel); btnRow.Children.Add(ok);
            panel.Children.Add(btnRow);
        }
        private void Accept()
        {
            SelectedColor = HsvToRgb(_h, _s, _v);
            Accepted = true;
            // Best-effort only - see Accepted. Setting DialogResult can also throw once the
            // nested capture modal has run, and the commit must not die with it.
            try { DialogResult = true; } catch (InvalidOperationException) { }
            Close();
        }
        // ── Interaction ─────────────────────────────────────────────────────────
        private void SvPick(Point p) { _s = Clamp01(p.X / SvW); _v = Clamp01(1 - p.Y / SvH); SyncFromHsv(); }
        private void HuePick(Point p) { _h = Clamp01(p.Y / SvH) * 360; SyncFromHsv(); }
        private void CommitHex() { if (TryParseHex(_hexBox.Text, out Color c)) SetFromColor(c); else SyncFromHsv(); }
        private void CommitRgb()
        {
            if (byte.TryParse(_rBox.Text, out byte r) && byte.TryParse(_gBox.Text, out byte g) && byte.TryParse(_bBox.Text, out byte b))
                SetFromColor(Color.FromRgb(r, g, b));
            else SyncFromHsv();
        }
        private void SetFromColor(Color c) { (_h, _s, _v) = RgbToHsv(c); SyncFromHsv(); }
        // Push current HSV out to every control (hue background, thumbs, RGB, hex, preview).
        private void SyncFromHsv()
        {
            if (_updating) return;
            _updating = true;
            var c = HsvToRgb(_h, _s, _v);
            _svHue.Fill = new SolidColorBrush(HsvToRgb(_h, 1, 1));
            Canvas.SetLeft((UIElement)_svThumb.Children[0], _s * SvW - 6);
            Canvas.SetTop((UIElement)_svThumb.Children[0], (1 - _v) * SvH - 6);
            Canvas.SetTop(_hueThumb, Math.Max(0, Math.Min(SvH - 6, _h / 360.0 * SvH - 3)));   // keep the handle inside the strip
            _rBox.Text = c.R.ToString(); _gBox.Text = c.G.ToString(); _bBox.Text = c.B.ToString();
            _hexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _newSwatch.Background = new SolidColorBrush(c);
            _updating = false;
        }
        // ── Eyedropper (desktop-wide) ───────────────────────────────────────────
        private Button? _eyedropBtn;
        private bool _eyedropArmed;

        private void RunEyedropper()
        {
            // Armed look while the capture is live: accent border + selected-row tint, so the
            // active state is visible even with the crosshair off in another corner of the screen.
            _eyedropArmed = true;
            if (_eyedropBtn != null)
            {
                _eyedropBtn.SetResourceReference(Button.BackgroundProperty,  "RowSelectedBrush");
                _eyedropBtn.SetResourceReference(Button.BorderBrushProperty, "PrimaryBrush");
            }
            try { RunEyedropperCore(); }
            finally
            {
                _eyedropArmed = false;
                if (_eyedropBtn != null)
                {
                    _eyedropBtn.Background  = R("BgCanvas");
                    _eyedropBtn.BorderBrush = R("CardBorderBrush");
                }
            }
        }

        private void RunEyedropperCore()
        {
            var capture = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Cursor = Cursors.Cross,
                Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth, Height = SystemParameters.VirtualScreenHeight, Owner = this
            };
            capture.MouseLeftButtonDown += (_, _) =>
            {
                // GetCursorPos returns physical screen pixels; the desktop DC's GetPixel uses the same
                // space, so this is correct regardless of per-monitor DPI scaling.
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr dc = GetDC(IntPtr.Zero);
                    uint cref = GetPixel(dc, pt.X, pt.Y);
                    ReleaseDC(IntPtr.Zero, dc);
                    capture.DialogResult = true; capture.Close();
                    SetFromColor(Color.FromRgb((byte)(cref & 0xFF), (byte)((cref >> 8) & 0xFF), (byte)((cref >> 16) & 0xFF)));
                    return;
                }
                capture.DialogResult = false; capture.Close();
            };
            capture.KeyDown += (_, e) => { if (e.Key == Key.Escape) { capture.DialogResult = false; capture.Close(); } };
            capture.ShowDialog();
        }
        // ── Saved swatches ──────────────────────────────────────────────────────
        private List<Color> LoadSaved()
        {
            var raw = App.GetSetting(SavedKey);
            if (string.IsNullOrWhiteSpace(raw)) return [.. DefaultSwatches];   // first run = defaults
            var list = new List<Color>();
            foreach (var part in raw!.Split(','))
                if (TryParseHex(part.Trim(), out Color c)) list.Add(c);
            return list.Count > 0 ? list : [.. DefaultSwatches];
        }
        private void StoreSaved(List<Color> list) =>
            App.SetSetting(SavedKey, string.Join(",", list.Take(SwatchMax).Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}")));
        private void UpdateReplaceChip()
        {
            if (_replaceBtn is null) return;
            _replaceBtn.Background = _replaceArmed ? R("RowSelectedBrush") : R("PaneBrush");
            _replaceBtn.SetResourceReference(Border.BorderBrushProperty, _replaceArmed ? "PrimaryBrush" : "CardBorderBrush");
        }
        private void RebuildSavedRow()
        {
            _savedRow.Children.Clear();
            var saved = LoadSaved().Take(SwatchMax).ToList();
            for (int i = 0; i < saved.Count; i++)
            {
                var c = saved[i];
                int idx = i;
                var sw = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 4),
                    Background = new SolidColorBrush(c), BorderThickness = new Thickness(_replaceArmed ? 2 : 1), Cursor = Cursors.Hand,
                    ToolTip = _replaceArmed ? "Click to set this swatch to the current color" : "Click to use this color" };
                if (_replaceArmed) sw.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush"); else sw.BorderBrush = R("CardBorderBrush");
                sw.MouseLeftButtonUp += (_, _) =>
                {
                    if (_replaceArmed)
                    {
                        var list = LoadSaved();
                        if (idx < list.Count) { list[idx] = HsvToRgb(_h, _s, _v); StoreSaved(list); }
                        _replaceArmed = false; UpdateReplaceChip(); RebuildSavedRow(); SwatchesChanged?.Invoke();
                    }
                    else SetFromColor(c);
                };
                _savedRow.Children.Add(sw);
            }
        }
        // ── Small themed control builders ───────────────────────────────────────
        private StackPanel FieldGroup(string label, TextBox box)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            sp.Children.Add(new TextBlock { Text = label, Foreground = R("MutedTextBrush"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(box);
            return sp;
        }
        private TextBox NumBox()
        {
            var b = MakeTextBox(34);
            b.MaxLength = 3;
            b.TextAlignment = TextAlignment.Center;
            b.LostFocus += (_, _) => CommitRgb();
            b.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitRgb(); };
            return b;
        }
        private TextBox MakeTextBox(double width) => new()
        {
            Width = width, Height = 22, VerticalContentAlignment = VerticalAlignment.Center,
            Background = R("BgCanvas"), Foreground = R("TextBrush"),
            BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1),
            CaretBrush = R("TextBrush"), SelectionBrush = R("RowSelectedBrush"),
            Padding = new Thickness(4, 0, 4, 0), Template = MakeTextBoxTemplate()
        };
        // A crosshair/target glyph drawn in vectors, to match the KillerPDF look.
        private UIElement CrosshairIcon()
        {
            var g = new Grid { Width = 14, Height = 14 };
            var fg = R("TextBrush");
            g.Children.Add(new Rectangle { Width = 1.4, Fill = fg, HorizontalAlignment = HorizontalAlignment.Center });
            g.Children.Add(new Rectangle { Height = 1.4, Fill = fg, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(new Ellipse { Width = 8, Height = 8, Stroke = fg, StrokeThickness = 1.4,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Fill = Brushes.Transparent });
            return g;
        }
        private Border Chip(string text, string tip)
        {
            var b = new Border { Height = 20, MinWidth = 22, CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand,
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1), Background = R("PaneBrush"),
                Padding = new Thickness(6, 0, 6, 0), ToolTip = tip,
                Child = new TextBlock { Text = text, Foreground = R("TextBrush"), FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            // Unified hover with the Cancel button (greyer fill), respecting Replace's armed highlight.
            b.MouseEnter += (_, _) => { if (b != _replaceBtn || !_replaceArmed) b.Background = R("CardBorderBrush"); };
            b.MouseLeave += (_, _) => { b.Background = (b == _replaceBtn && _replaceArmed) ? R("RowSelectedBrush") : R("PaneBrush"); };
            return b;
        }
        private static ControlTemplate MakeTextBoxTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            foreach (var (dp, prop) in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.BorderThicknessProperty, "BorderThickness") })
                b.SetBinding(dp, new Binding(prop) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            sv.SetValue(ScrollViewer.VerticalAlignmentProperty, VerticalAlignment.Center);
            b.AppendChild(sv);
            return new ControlTemplate(typeof(TextBox)) { VisualTree = b };
        }
        private Button MakeButton(string text, bool primary)
        {
            var btn = new Button { Content = text, Height = 28, MinWidth = 74, Padding = new Thickness(12, 0, 12, 0),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.TemplateProperty, MakeBtnTemplate()));
            style.Setters.Add(new Setter(Control.ForegroundProperty, primary ? R("PrimaryBrush") : R("TextBrush")));
            style.Setters.Add(new Setter(Control.BackgroundProperty, primary ? R("RowSelectedBrush") : R("PaneBrush")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, primary ? R("PrimaryBrush") : R("CardBorderBrush")));
            // Hover: OK fills solid accent (white text for contrast); Cancel goes a shade greyer.
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            if (primary)
            {
                hover.Setters.Add(new Setter(Control.BackgroundProperty, R("PrimaryBrush")));
                hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            }
            else
            {
                hover.Setters.Add(new Setter(Control.BackgroundProperty, R("CardBorderBrush")));
            }
            style.Triggers.Add(hover);
            btn.Style = style;
            return btn;
        }
        private static ControlTemplate MakeBtnTemplate()
        {
            var bf = new FrameworkElementFactory(typeof(Border));
            foreach (var (dp, prop) in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.BorderThicknessProperty, "BorderThickness") })
                bf.SetBinding(dp, new Binding(prop) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = bf };
        }
        private static LinearGradientBrush HueStripBrush()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            for (int i = 0; i <= 6; i++) g.GradientStops.Add(new GradientStop(HsvToRgb(i * 60, 1, 1), i / 6.0));
            return g;
        }
        // ── Color math / parsing ───────────────────────────────────────────────
        private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));
        private static bool TryParseHex(string? s, out Color c)
        {
            c = Colors.Black;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s!.Trim().TrimStart('#');
            if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
            if (s.Length != 6) return false;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            c = Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            return true;
        }
        private static (double h, double s, double v) RgbToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double h = 0;
            if (d > 0.00001)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;
            double s = max <= 0 ? 0 : d / max;
            return (h, s, max);
        }
        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((h / 60.0 % 2) - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);
    }
}
