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
    internal sealed partial class ColorPickerDialog : Window
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
        private System.Windows.Controls.Primitives.Thumb _hueThumb = null!;
        private const double HueThumbH = 12;
        private Rectangle _svHue = null!;
        private TextBox _rBox = null!, _gBox = null!, _bBox = null!, _hexBox = null!;
        private Border _newSwatch = null!;
        private System.Windows.Controls.Primitives.UniformGrid _savedRow = null!;
        private Border _replaceBtn = null!;
        private System.Windows.Controls.Primitives.ToggleButton _replaceToggle = null!;
        private bool _replaceArmed;            // when on, the next swatch click is overwritten, not selected
        public event Action? SwatchesChanged;  // raised when the shared palette is edited, so the bar can live-update
        // SvW is only the fallback before layout; the square takes whatever width the strip leaves.
        private const int SvW = 220, SvH = 170, HueW = 18;
        private Grid _svGrid = null!;
        private const int SwatchCols = 9, SwatchMax = 9;   // one clean row of 9 fixed slots
        // The values block: two 24px field rows with a 6px gap, framed by a preview and an eyedropper
        // that are each one square of the block's full height.
        private const double FieldHeight = 24, InputGap = 6, SwatchRowHeight = 44, SwatchGap = 6;
        // Shared with the annotate-bar palette: editing these swatches reconfigures the toolbar colors.
        private const string SavedKey = "UserSwatches";
        // First-run / Reset palette: 9 fixed slots, last one white.
        private static readonly Color[] DefaultSwatches = UiKit.DefaultSwatches;
        private static Brush R(string key) => (Brush)Application.Current.Resources[key];
        private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
        internal const string ColorPdfTitle = "ColorPDF";
        public ColorPickerDialog(Window? owner, Color initial)
        {
            Title = ColorPdfTitle;
            System.Windows.Automation.AutomationProperties.SetAutomationId(this, "ColorPickerDialog");
            // The caption is a joke wordmark, so a screen reader still hears what the window is for.
            System.Windows.Automation.AutomationProperties.SetName(this, L("Str_Color_Pick"));
            Width = 300;
            SizeToContent = SizeToContent.Height;
            DialogFrame.Configure(this, owner);
            UseLayoutRounding = true;
            SelectedColor = initial;
            (_h, _s, _v) = RgbToHsv(initial);
            BuildUi();
            SyncFromHsv();
            // The frame already closes on Escape and marks the key handled; closing twice would throw.
            KeyDown += (_, e) => { if (e.Handled) return; if (e.Key == Key.Escape) { DialogResult = false; Close(); } else if (e.Key == Key.Enter) Accept(); };
        }
        // ── UI ──────────────────────────────────────────────────────────────────
        private void BuildUi()
        {
            // Top -2: the square sits 8px closer to the title than the shared 6px gap (Steve, 2026-11-25).
            var panel = new StackPanel { Margin = new Thickness(18, -2, 18, 16) };
            // The picker wears the shared dialog frame on every theme, with its own play on the name:
            // the ColorPDF wordmark and the color wheel icon. The PDF half takes the theme's accent as
            // KillerPDF's does. 98SE shows the plain caption, which reads ColorPDF too.
            Content = DialogFrame.Frame(this, Owner, ColorPdfTitle,
                () => { DialogResult = false; Close(); }, panel, lead: "Color", icon: "color-icon.ico");
            // SV square + hue strip
            // The strip docks right and the square fills the rest, so the strip always meets the
            // right margin whatever width the frame leaves (a fixed square overflowed or fell short).
            var pickRow = new DockPanel { LastChildFill = true };
            _svHue = new Rectangle { Height = SvH };
            var svWhite = new Rectangle { Height = SvH, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0) };
            var svBlack = new Rectangle { Height = SvH, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0), 90) };
            _svThumb = new Canvas { Height = SvH, IsHitTestVisible = false };
            var svDot = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 0, Opacity = 0.8 } };
            _svThumb.Children.Add(svDot);
            _svGrid = new Grid { Height = SvH };
            var svGrid = _svGrid;
            svGrid.Children.Add(_svHue); svGrid.Children.Add(svWhite); svGrid.Children.Add(svBlack); svGrid.Children.Add(_svThumb);
            svGrid.SizeChanged += (_, _) => SyncFromHsv();
            // ClipToBounds off so the indicator dot shows fully when it sits at an edge/corner.
            _svArea = new Border { Height = SvH + 2, CornerRadius = UiKit.RadControl, ClipToBounds = false,
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1), Child = svGrid, Cursor = Cursors.Cross };
            _svArea.MouseLeftButtonDown += (s, e) => { _svArea.CaptureMouse(); SvPick(e.GetPosition(svGrid)); };
            _svArea.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) SvPick(e.GetPosition(svGrid)); };
            _svArea.MouseLeftButtonUp += (s, e) => _svArea.ReleaseMouseCapture();
            var hueRect = new Rectangle { Width = HueW, Height = SvH, Fill = HueStripBrush() };
            // The theme's own slider thumb (DarkSliderThumb, as on the annotate bar), turned on its side.
            // The strip handles the drag, so the thumb takes no input of its own.
            _hueThumb = new System.Windows.Controls.Primitives.Thumb { Width = HueW + 6, Height = HueThumbH, IsHitTestVisible = false,
                Style = Application.Current.TryFindResource("DarkSliderThumb") as Style };
            // Strip-wide, so the border hugs the strip; the thumb overhangs 3px each side (Canvas does not clip).
            var hueCanvas = new Canvas { Width = HueW, Height = SvH };
            Canvas.SetLeft(_hueThumb, -3);
            hueCanvas.Children.Add(_hueThumb);
            var hueGrid = new Grid();
            hueGrid.Children.Add(hueRect); hueGrid.Children.Add(hueCanvas);
            // The gap sits outside the border so the outline hugs the strip; inside it, the border
            // framed an empty gutter beside the strip (2026-11-25).
            var hueArea = new Border { Child = hueGrid, Cursor = Cursors.SizeNS, CornerRadius = UiKit.RadControl,
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(8, 0, 0, 0) };
            hueArea.MouseLeftButtonDown += (s, e) => { hueArea.CaptureMouse(); HuePick(e.GetPosition(hueRect)); };
            hueArea.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) HuePick(e.GetPosition(hueRect)); };
            hueArea.MouseLeftButtonUp += (s, e) => hueArea.ReleaseMouseCapture();
            DockPanel.SetDock(hueArea, Dock.Right);
            pickRow.Children.Add(hueArea);
            pickRow.Children.Add(_svArea);
            panel.Children.Add(pickRow);
            // Values, in two even rows under the color square: the preview and a wide eyedropper
            // button side by side, then Hex, R, G and B with small captions over the boxes. One row of
            // four boxes fits the dialog's width; the old single row crushed R, G and B (2026-11-25).
            var topRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InputGap) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _newSwatch = new Border { Height = SwatchRowHeight, CornerRadius = UiKit.RadControl,
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1) };
            topRow.Children.Add(_newSwatch);

            var fieldRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            for (int i = 0; i < 3; i++)
            {
                fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InputGap) });
                fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            _hexBox = MakeTextBox(double.NaN);
            System.Windows.Automation.AutomationProperties.SetAutomationId(_hexBox, "PickerHex");
            System.Windows.Automation.AutomationProperties.SetName(_hexBox, L("Str_Color_Hex"));
            _hexBox.MaxLength = 7;
            _hexBox.LostFocus += (_, _) => CommitHex();
            _hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
            fieldRow.Children.Add(FieldGroup(L("Str_Color_Hex"), _hexBox, L("Str_Color_Hex")));
            _rBox = NumBox(); _gBox = NumBox(); _bBox = NumBox();
            var rgb = new[]
            {
                FieldGroup("R", _rBox, L("Str_Color_Red")),
                FieldGroup("G", _gBox, L("Str_Color_Green")),
                FieldGroup("B", _bBox, L("Str_Color_Blue"))
            };
            for (int i = 0; i < rgb.Length; i++)
            {
                Grid.SetColumn(rgb[i], 2 + i * 2);
                fieldRow.Children.Add(rgb[i]);
            }

            _eyedropBtn = new Button
            {
                Height = SwatchRowHeight, HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = R("BgCanvas"), BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1),
                // No Cursor here: the crosshair belongs to the CAPTURE window that opens on click.
                // On the button it appeared on hover, before the pick had started (2026-08-01).
                Content = CrosshairIcon(), ToolTip = L("Str_Color_EyedropTT"),
                Template = MakeBtnTemplate()
            };
            // Same hover treatment as the dialog's chips (grayer fill), and RunEyedropper holds the
            // armed tint + accent border for as long as the capture is live (2026-08-01).
            _eyedropBtn.MouseEnter += (_, _) => { if (!_eyedropArmed) _eyedropBtn.Background = R("CardBorderBrush"); };
            _eyedropBtn.MouseLeave += (_, _) => { if (!_eyedropArmed) _eyedropBtn.Background = R("BgCanvas"); };
            _eyedropBtn.Click += (_, _) => RunEyedropper();
            Grid.SetColumn(_eyedropBtn, 2);
            topRow.Children.Add(_eyedropBtn);
            panel.Children.Add(topRow);
            panel.Children.Add(fieldRow);

            // Saved swatches: nine even cells across the full width, then Replace and Reset as an even
            // pair under them, the same height as the fields above.
            _savedRow = new System.Windows.Controls.Primitives.UniformGrid { Columns = SwatchCols, Rows = 1,
                Margin = new Thickness(-SwatchGap / 2, 12, -SwatchGap / 2, 0) };
            panel.Children.Add(_savedRow);
            var swActions = new Grid { Margin = new Thickness(0, InputGap, 0, 0) };
            swActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            swActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InputGap) });
            swActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _replaceBtn = Chip(L("Str_Color_Replace"), L("Str_Color_ReplaceTT"));
            // Real buttons around the drawn chips, so Tab, Space and Enter reach them and a screen
            // reader hears Replace as a toggle (BF-U005).
            _replaceToggle = Pressable(new System.Windows.Controls.Primitives.ToggleButton(), _replaceBtn, L("Str_Color_Replace"),
                () => { _replaceArmed = !_replaceArmed; UpdateReplaceChip(); RebuildSavedRow(); });
            var resetBtn = Chip(L("Str_Color_Reset"), L("Str_Color_ResetTT"));
            var reset = Pressable(new Button(), resetBtn, L("Str_Color_Reset"),
                () => { StoreSaved([.. DefaultSwatches]); _replaceArmed = false; UpdateReplaceChip(); RebuildSavedRow(); SwatchesChanged?.Invoke(); });
            Grid.SetColumn(reset, 2);
            swActions.Children.Add(_replaceToggle);
            swActions.Children.Add(reset);
            panel.Children.Add(swActions);
            UpdateReplaceChip();
            RebuildSavedRow();
            // OK / Cancel
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = MakeButton(L("Str_Btn_CancelDlg"), false); cancel.Click += (_, _) => { DialogResult = false; Close(); }; cancel.IsCancel = true;
            var ok = MakeButton(L("Str_Btn_OK"), true); ok.Margin = new Thickness(8, 0, 0, 0); ok.Click += (_, _) => Accept(); ok.IsDefault = true;
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancel, "PickerCancel");
            System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PickerOK");
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
        private double SvWidth => _svGrid.ActualWidth > 0 ? _svGrid.ActualWidth : SvW;
        private void SvPick(Point p) { _s = Clamp01(p.X / SvWidth); _v = Clamp01(1 - p.Y / SvH); SyncFromHsv(); }
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
            Canvas.SetLeft((UIElement)_svThumb.Children[0], _s * SvWidth - 6);
            Canvas.SetTop((UIElement)_svThumb.Children[0], (1 - _v) * SvH - 6);
            Canvas.SetTop(_hueThumb, Math.Max(0, Math.Min(SvH - HueThumbH, _h / 360.0 * SvH - HueThumbH / 2)));   // keep the handle inside the strip
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
                _eyedropBtn.Content = CrosshairIcon(SelectedFillText());
            }
            try { RunEyedropperCore(); }
            finally
            {
                _eyedropArmed = false;
                if (_eyedropBtn != null)
                {
                    _eyedropBtn.Background  = R("BgCanvas");
                    _eyedropBtn.BorderBrush = R("CardBorderBrush");
                    _eyedropBtn.Content = CrosshairIcon();
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
                    _ = ReleaseDC(IntPtr.Zero, dc);
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
        private static List<Color> LoadSaved()
        {
            var raw = App.GetSetting(SavedKey);
            if (string.IsNullOrWhiteSpace(raw)) return [.. DefaultSwatches];   // first run = defaults
            var list = new List<Color>();
            foreach (var part in raw!.Split(','))
                if (TryParseHex(part.Trim(), out Color c)) list.Add(c);
            return list.Count > 0 ? list : [.. DefaultSwatches];
        }
        private static void StoreSaved(List<Color> list) =>
            App.SetSetting(SavedKey, string.Join(",", list.Take(SwatchMax).Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}")));
        private void UpdateReplaceChip()
        {
            if (_replaceBtn is null) return;
            if (_replaceToggle is not null) _replaceToggle.IsChecked = _replaceArmed;
            _replaceBtn.Background = _replaceArmed ? R("RowSelectedBrush") : R("PaneBrush");
            if (_replaceBtn.Child is TextBlock label)
                label.Foreground = _replaceArmed ? SelectedFillText() : R("TextBrush");
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
                var sw = new Border { Height = 22, CornerRadius = UiKit.RadControl,
                    Background = new SolidColorBrush(c), BorderThickness = new Thickness(_replaceArmed ? 2 : 1) };
                if (_replaceArmed) sw.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush"); else sw.BorderBrush = R("CardBorderBrush");
                var swatch = Pressable(new Button(), sw, $"#{c.R:X2}{c.G:X2}{c.B:X2}", () =>
                {
                    if (_replaceArmed)
                    {
                        var list = LoadSaved();
                        if (idx < list.Count) { list[idx] = HsvToRgb(_h, _s, _v); StoreSaved(list); }
                        _replaceArmed = false; UpdateReplaceChip(); RebuildSavedRow(); SwatchesChanged?.Invoke();
                    }
                    else SetFromColor(c);
                });
                swatch.Margin = new Thickness(SwatchGap / 2, 0, SwatchGap / 2, 0);
                swatch.ToolTip = _replaceArmed ? L("Str_Color_SwatchSetTT") : L("Str_Color_SwatchUseTT");
                _savedRow.Children.Add(swatch);
            }
        }
        // ── Small themed control builders ───────────────────────────────────────
        /// <summary>A short caption over the box; screen readers get the full name.</summary>
        private static StackPanel FieldGroup(string label, TextBox box, string name)
        {
            System.Windows.Automation.AutomationProperties.SetName(box, name);
            var g = new StackPanel();
            g.Children.Add(new TextBlock
            {
                Text = label, Foreground = R("MutedTextBrush"), FontSize = 11, Margin = new Thickness(1, 0, 0, 3),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            g.Children.Add(box);
            return g;
        }
        private TextBox NumBox()
        {
            var b = MakeTextBox(double.NaN);
            b.MaxLength = 3;
            b.TextAlignment = TextAlignment.Center;
            b.LostFocus += (_, _) => CommitRgb();
            b.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitRgb(); };
            return b;
        }
        private static TextBox MakeTextBox(double width)
        {
            // Use the one shared field implementation. In particular, TextFieldBrush is white on
            // 98SE while the document canvas is gray; a local BgCanvas field was visibly wrong.
            var box = UiKit.Field(double.IsNaN(width) ? 40 : width);
            // NaN stretches the box across its grid cell.
            if (double.IsNaN(width)) { box.Width = double.NaN; box.HorizontalAlignment = HorizontalAlignment.Stretch; }
            box.Height = FieldHeight;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.Padding = new Thickness(4, 0, 4, 0);
            return box;
        }
        // A crosshair/target glyph drawn in vectors, to match the KillerPDF look.
        // Text on the armed RowSelectedBrush fill: a theme whose fill is solid (Trash Polka Yellow, 98SE) states it.
        private static Brush SelectedFillText()
            => Application.Current.Resources["TileCheckedTextBrush"] as Brush ?? R("TextBrush");

        private static Grid CrosshairIcon(Brush? color = null)
        {
            // Big and in the accent at rest, so the eyedropper reads as a tool instead of a stray glyph.
            var g = new Grid { Width = 26, Height = 26 };
            var fg = color ?? R("PrimaryBrush");
            g.Children.Add(new Rectangle { Width = 2, Fill = fg, HorizontalAlignment = HorizontalAlignment.Center });
            g.Children.Add(new Rectangle { Height = 2, Fill = fg, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(new Ellipse { Width = 15, Height = 15, Stroke = fg, StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Fill = Brushes.Transparent });
            return g;
        }
        // A chrome-free button showing a drawn visual: keyboard focus, Space/Enter, an Invoke (or
        // Toggle) pattern and an accessible name, with the visual left exactly as it was drawn.
        private static T Pressable<T>(T button, Border visual, string name, Action click)
            where T : System.Windows.Controls.Primitives.ButtonBase
        {
            button.Content = visual;
            button.Cursor = Cursors.Hand;
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Padding = new Thickness(0);
            button.Template = new ControlTemplate(typeof(T)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => click();
            return button;
        }

        private Border Chip(string text, string tip)
        {
            var b = new Border { Height = FieldHeight, MinWidth = 22, CornerRadius = UiKit.RadControl, Cursor = Cursors.Hand,
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1), Background = R("PaneBrush"),
                Padding = new Thickness(6, 0, 6, 0), ToolTip = tip,
                Child = new TextBlock { Text = text, Foreground = R("TextBrush"), FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            // Unified hover with the Cancel button (grayer fill), respecting Replace's armed highlight.
            b.MouseEnter += (_, _) => { if (b != _replaceBtn || !_replaceArmed) b.Background = R("CardBorderBrush"); };
            b.MouseLeave += (_, _) => { b.Background = (b == _replaceBtn && _replaceArmed) ? R("RowSelectedBrush") : R("PaneBrush"); };
            return b;
        }
        private static Button MakeButton(string text, bool primary)
        {
            // 98SE: the shared kit button already carries the beveled ChipFace treatment, so the
            // dialog's OK / Cancel match the classic toolbar instead of the modern accent pair.
            if (Services.ThemeManager.Current == Services.Theme.SE98)
            {
                var se = UiKit.Make(text, primary);
                se.Height = 28; se.MinWidth = 74; se.Padding = new Thickness(12, 0, 12, 0);
                return se;
            }
            var btn = new Button { Content = text, Height = 28, MinWidth = 74, Padding = new Thickness(12, 0, 12, 0),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.TemplateProperty, MakeBtnTemplate()));
            // Rest brushes match UiKit.Make's accent pair (SelectionFg on SelectionBg): the old
            // PrimaryBrush-on-RowSelectedBrush pairing put accent text on an accent-tinted fill,
            // which left "OK" unreadable at rest on several themes until hover repainted it (#227).
            style.Setters.Add(new Setter(Control.ForegroundProperty, primary ? R("SelectionFg") : R("TextBrush")));
            style.Setters.Add(new Setter(Control.BackgroundProperty, primary ? R("SelectionBg") : R("PaneBrush")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, primary ? R("PrimaryBrush") : R("CardBorderBrush")));
            // Hover: OK fills solid accent (OnPrimaryBrush text for contrast); Cancel goes a shade grayer.
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            if (primary)
            {
                hover.Setters.Add(new Setter(Control.BackgroundProperty, R("PrimaryBrush")));
                hover.Setters.Add(new Setter(Control.ForegroundProperty, R("OnPrimaryBrush")));
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
            bf.SetValue(Border.CornerRadiusProperty, UiKit.RadControl);
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
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT p);
        [LibraryImport("user32.dll")]
        private static partial IntPtr GetDC(IntPtr hwnd);
        [LibraryImport("user32.dll")]
        private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [LibraryImport("gdi32.dll")]
        private static partial uint GetPixel(IntPtr hdc, int x, int y);
    }
}
