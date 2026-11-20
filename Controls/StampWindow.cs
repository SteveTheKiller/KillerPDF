using System;
using KillerPDF.Controls;
using KillerPDF.Services;
using KillerPDF.Shell;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace KillerPDF
{
    /// <summary>
    /// Combined "Stamp" tool, modeled on the Transform window: a live page preview on the left and an
    /// options sidebar on the right with two independent, toggleable sections - Page Numbers and
    /// Watermark (text or image). Apply hands a StampSpec back to the caller, which places the stamps on
    /// the editable stamp layer. Re-opening (double-click a stamp) seeds the window from the saved spec.
    /// </summary>
    internal sealed class StampWindow : Window
    {
        public bool Applied { get; private set; }
        public StampSpec Result { get; private set; }

        private BitmapSource _pageSrc;
        // The page's markup alone, drawn over the stamps as Apply orders them (#478); null when the page has none.
        private BitmapSource? _markupSrc;
        private double _pageWpt, _pageHpt;
        private readonly int _pageCount;
        private int _pageIndex;
        // The page the preview bitmap shows; the stepper returns to it when a page cannot be drawn.
        private int _shownPageIndex;
        private readonly StampSpec _spec;
        private readonly bool _hadExisting;   // dialog opened on a doc that already has stamps (#145)
        // Renders an arbitrary page for the preview stepper: returns that page's bitmap + size in points.
        private readonly Func<int, (BitmapSource? src, BitmapSource? markup, double wpt, double hpt)>? _pageProvider;
        private TextBlock _pageNavLabel = null!;
        private Button _prevArrow = null!, _nextArrow = null!;
        private System.Windows.Threading.DispatcherTimer? _navRenderTimer;

        private readonly Image _preview = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.45 }
        };
        private readonly Canvas _overlay = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        // Never hit-testable, so dragging a custom-placed stamp still reaches the stamp under the markup.
        private readonly Image _markup = new() { IsHitTestVisible = false, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private FrameworkElement _previewArea = null!;
        private Button _applyBtn = null!;
        private readonly System.Windows.Threading.DispatcherTimer _previewTimer;

        // Page-number controls
        private CheckBox _numEnable = null!;
        private CheckBox _numMirror = null!;
        private TextBox _numStart = null!, _numFormat = null!, _numSize = null!, _numRange = null!;
        private ComboBox _numPos = null!;
        private Border _numSwatch = null!;
        private Color _numColor;
        private TwoColumnPanel _numBody = null!;

        // Watermark controls
        private CheckBox _wmEnable = null!;
        private RadioButton _wmTextRadio = null!, _wmImageRadio = null!;
        private TextBox _wmText = null!, _wmSize = null!, _wmRange = null!;
        private ComboBox _wmPos = null!, _wmFont = null!;
        private Slider _wmAngle = null!, _wmOpacity = null!, _wmScale = null!;
        private Border _wmSwatch = null!;
        private Color _wmColor;
        private string? _wmImagePath;
        private BitmapImage? _wmImageSrc;
        private TextBlock _wmImageLabel = null!;
        private TwoColumnPanel _wmBody = null!;
        // Cells shown only for a text watermark or only for an image watermark.
        private UIElement[] _wmTextOnly = [], _wmImageOnly = [];
        private FrameworkElement _previewColumn = null!;

        private readonly Style? _darkSlider, _darkCombo;

        // Brush, never SolidColorBrush: any theme may state a surface as a gradient.
        private static Brush R(string key) => PageWindowLayout.ThemeBrush(Application.Current?.TryFindResource(key));
        private static string S(string key) => Application.Current.TryFindResource(key) as string ?? key;

        // (resource key, horizontal 0/1/2, vertical 0 top / 1 middle / 2 bottom)
        private static readonly (string key, int h, int v)[] Positions =
        [
            ("Str_Pos_BottomCenter", 1, 2), ("Str_Pos_BottomRight", 2, 2), ("Str_Pos_BottomLeft", 0, 2),
            ("Str_Pos_TopCenter", 1, 0), ("Str_Pos_TopRight", 2, 0), ("Str_Pos_TopLeft", 0, 0),
            ("Str_Pos_Center", 1, 1), ("Str_Pos_Custom", -1, -1)
        ];

        public StampWindow(Window owner, BitmapSource pageSrc, BitmapSource? markupSrc, double pageWpt, double pageHpt,
                           int pageCount, int pageIndex, StampSpec? existing,
                           Func<int, (BitmapSource? src, BitmapSource? markup, double wpt, double hpt)>? pageProvider = null)
        {
            _pageSrc = pageSrc;
            _markupSrc = markupSrc;
            _pageWpt = pageWpt;
            _pageHpt = pageHpt;
            _pageCount = pageCount;
            _pageIndex = pageIndex;
            _shownPageIndex = pageIndex;
            _pageProvider = pageProvider;
            _hadExisting = existing is not null;   // #145: clearing existing stamps must stay applyable
            _spec = existing?.Clone() ?? new StampSpec { NumbersEnabled = true };
            Result = _spec;

            Title = "KillerPDF - " + S("Str_Stamp_Suffix");
            System.Windows.Automation.AutomationProperties.SetAutomationId(this, "StampDialog");
            DialogFrame.Configure(this, owner, resizable: true);

            _darkSlider = owner.TryFindResource("DarkSlider") as Style;
            _darkCombo  = owner.TryFindResource("DarkComboBox") as Style;

            // Borrow the main window's themed scrollbar so the sidebar scroller isn't the OS-white default.
            if (owner.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style sbStyle)
                Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sbStyle;

            _numColor = _spec.NumColor;
            _wmColor  = _spec.WmColor;
            _wmImagePath = _spec.WmImagePath;

            _previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _previewTimer.Tick += (_, _2) => { _previewTimer.Stop(); RenderPreview(); };

            BuildUi(owner);
            LoadWatermarkImage();
            UpdateEnabledStates();
            RenderPreview();
        }

        private void Schedule() { _previewTimer.Stop(); _previewTimer.Start(); }

        private void BuildUi(Window owner)
        {
            // Footer: "Reset all" on the left, then Cancel and Apply, on one row.
            var resetLink = PageWindowLayout.LinkButton(S("Str_Tf_ResetAll"), ResetAll);
            var cancelBtn = UiKit.Make(S("Str_Tf_Cancel"), false);
            cancelBtn.Click += (_, _2) => { Applied = false; Close(); };
            cancelBtn.IsCancel = true;   // Esc
            _applyBtn = UiKit.Make(S("Str_Tf_Apply"), true);
            System.Windows.Automation.AutomationProperties.SetAutomationId(_applyBtn, "StampApply");
            _applyBtn.Click += (_, _2) => CommitAndClose();
            _applyBtn.IsDefault = true;   // Enter
            var footer = PageWindowLayout.Footer(resetLink, cancelBtn, _applyBtn);

            // Both sections are always open, so the window's height covers everything; a section's
            // controls stay disabled until its checkbox turns it on, and Apply writes only what is on.
            var options = new StackPanel();
            options.Children.Add(BuildWatermarkSection());
            options.Children.Add(PageWindowLayout.Divider());
            options.Children.Add(BuildNumbersSection());

            // ---- Preview: the framed page, with the stepper under the frame ----
            bool paged = _pageProvider != null && _pageCount > 1;
            var previewWrap = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = UiKit.RadCard,
                Margin = new Thickness(8, 8, 8, paged ? 0 : 8),
                ClipToBounds = true
            };
            previewWrap.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
            previewWrap.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");

            var imageHost = new Grid();
            AddGrain(imageHost, owner, 0.05, cornerRadius: 0);
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
            _preview.Source = _pageSrc;
            _markup.Source = _markupSrc;
            RenderOptions.SetBitmapScalingMode(_markup, BitmapScalingMode.HighQuality);
            // Page, then stamps, then the page's markup: the order Apply and save give them (#478).
            StampPreviewMarkup.Stack(imageHost, _preview, _overlay, _markup);
            previewWrap.Child = imageHost;
            _previewArea = imageHost;
            var previewColumn = new DockPanel();
            // Page stepper: the wheel over the preview (or the arrows) walks pages so you can preview the
            // stamp on any page - the per-page number and range checks update as you go. Only when the caller
            // supplies a page provider and there's more than one page.
            if (paged)
            {
                var nav = BuildPageNav();
                DockPanel.SetDock(nav, Dock.Bottom);
                previewColumn.Children.Add(nav);
                previewWrap.PreviewMouseWheel += (_, e) =>
                {
                    int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
                    StepPage(e.Delta < 0 ? notches : -notches);
                    e.Handled = true;
                };
            }
            _previewArea.SizeChanged += (_, _2) => { SizePreviewImage(); Schedule(); };
            // Family shadow under the content pane, like the main window (flat on 98SE).
            previewColumn.Children.Add(UiKit.PaneWithShadow(previewWrap));
            _previewColumn = previewColumn;

            // A work area narrower than the minimum window (456 options + 24 halo + 282 preview)
            // falls back to one 288 wide column of options.
            int columns = SystemParameters.WorkArea.Width < PageWindowLayout.OptionsWidth + 24 + 282 ? 1 : 2;
            var root = PageWindowLayout.Body(options, footer, previewColumn, out _, columns);

            Content = DialogFrame.Frame(this, Owner, "KillerPDF - " + S("Str_Stamp_Suffix"), () => { Applied = false; Close(); }, root);
            // Focus starts on the first setting, the Watermark switch; a checkbox leaves Enter to Apply.
            Loaded += (_, _2) => _wmEnable.Focus();
            // As tall as both open sections and the footer, resizable from every edge, remembered;
            // the first open widens until the page fills the preview's height.
            DialogFrame.EnablePageSizing(this, "Stamp", _previewColumn, fittedExtraWidth: () =>
                PageWindowLayout.FittedExtraWidth(new Size(_previewArea.ActualWidth, _previewArea.ActualHeight),
                    _pageHpt > 0 && _pageWpt > 0 ? new Size(_pageWpt, _pageHpt) : new Size(_pageSrc.PixelWidth, _pageSrc.PixelHeight)));

            // Esc-to-close is wired by DialogFrame.Frame; Enter commits.
            KeyDown += (_, e) => { if (e.Key == Key.Enter && _applyBtn?.IsEnabled == true) { CommitAndClose(); e.Handled = true; } };
        }

        private static void AddGrain(Grid host, Window owner, double fallback, double cornerRadius)
        {
            var grain = (owner as MainWindow)?.GrainTextureSource;
            if (grain == null) return;
            double op = Application.Current.Resources["GrainOpacity"] is double go ? go : fallback;
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(cornerRadius), IsHitTestVisible = false, Opacity = op,
                Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
            });
        }

        // ---------- Page Numbers section ----------
        private StackPanel BuildNumbersSection()
        {
            var wrap = new StackPanel();
            _numEnable = SectionToggle(S("Str_Stamp_SecNumbers"), _spec.NumbersEnabled);
            _numEnable.Checked   += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            _numEnable.Unchecked += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            wrap.Children.Add(PageWindowLayout.HeaderRow(_numEnable));

            _numFormat = MakeField(_spec.Format);
            _numFormat.TextChanged += (_, _2) => Schedule();
            var hints = new StackPanel();
            hints.Children.Add(PageWindowLayout.Hint(S("Str_Stamp_Hint")));
            hints.Children.Add(PageWindowLayout.Hint(S("Str_Stamp_Hint2")));
            ((FrameworkElement)hints.Children[0]).Margin = new Thickness(0, -4, 0, 0);

            _numStart = MakeField(_spec.StartNumber.ToString());
            _numStart.TextChanged += (_, _2) => Schedule();

            var numSize = SliderBoxRow(S("Str_Stamp_FontSize"), StampInput.NumFontMin, StampInput.NumFontMax, _spec.NumFontPt, out _, out _numSize);
            var numColor = ColorRow(S("Str_Stamp_Color"), _numColor, out _numSwatch, c => { _numColor = c; Schedule(); }, "StampPageNumberColor");

            _numPos = MakePosCombo(_spec.NumPosH, _spec.NumPosV);
            _numPos.SelectionChanged += (_, _2) => { UpdateMirrorEnabled(); Schedule(); };

            _numRange = MakeField(_spec.NumRange);
            _numRange.ToolTip = S("Str_Crop_RangeTip");
            _numRange.TextChanged += (_, _2) => Schedule();

            _numMirror = UiKit.CheckBox(S("Str_Stamp_Mirror"));
            _numMirror.IsChecked = _spec.NumMirror;
            _numMirror.Checked   += (_, _2) => Schedule();
            _numMirror.Unchecked += (_, _2) => Schedule();
            UpdateMirrorEnabled();

            _numBody = Grid2(
                Span(PageWindowLayout.Labeled(S("Str_Stamp_Format"), _numFormat)),
                Span(hints),
                PageWindowLayout.Labeled(S("Str_Stamp_StartAt"), _numStart), numSize,
                numColor, PageWindowLayout.Labeled(S("Str_Stamp_Position"), _numPos),
                PageWindowLayout.Labeled(S("Str_Stamp_Pages"), _numRange),
                PageWindowLayout.Labeled(" ", Row(_numMirror)));
            wrap.Children.Add(_numBody);
            return wrap;
        }

        // ---------- Watermark section ----------
        private StackPanel BuildWatermarkSection()
        {
            var wrap = new StackPanel();
            _wmEnable = SectionToggle(S("Str_Stamp_SecWatermark"), _spec.WmEnabled);
            _wmEnable.Checked   += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            _wmEnable.Unchecked += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            wrap.Children.Add(PageWindowLayout.HeaderRow(_wmEnable));

            // Type: text vs image.
            var typeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _wmTextRadio = MakeRadio(S("Str_Stamp_WmText"), !_spec.WmIsImage);
            _wmTextRadio.Margin = new Thickness(0, 0, 14, 0);
            _wmImageRadio = MakeRadio(S("Str_Stamp_WmImage"), _spec.WmIsImage);
            _wmTextRadio.Checked  += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            _wmImageRadio.Checked += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            typeRow.Children.Add(_wmTextRadio);
            typeRow.Children.Add(_wmImageRadio);

            _wmPos = MakePosCombo(_spec.WmPosH, _spec.WmPosV);
            _wmPos.SelectionChanged += (_, _2) => Schedule();

            // Text mode: the text, its font and size, and its color.
            _wmText = MakeField(_spec.WmText);
            _wmText.TextChanged += (_, _2) => Schedule();
            _wmFont = new ComboBox { Height = PageWindowLayout.ControlHeight, MaxDropDownHeight = 320 };
            if (_darkCombo != null) _wmFont.Style = _darkCombo; else { _wmFont.Background = R("BgCanvas"); _wmFont.Foreground = R("TextBrush"); }
            System.Windows.Automation.AutomationProperties.SetName(_wmFont, S("Str_Bar_Font"));
            foreach (var fn in MainWindow.SystemFontNames) _wmFont.Items.Add(fn);
            // A saved font this computer does not have stays in the list, so Apply keeps it.
            if (!string.IsNullOrEmpty(_spec.WmFont) && !_wmFont.Items.Contains(_spec.WmFont)) _wmFont.Items.Insert(0, _spec.WmFont);
            _wmFont.SelectedItem = _spec.WmFont;
            _wmFont.SelectionChanged += (_, _2) => Schedule();
            var textCell = Span(PageWindowLayout.Labeled(S("Str_Stamp_WmTextLabel"), _wmText));
            var fontCell = PageWindowLayout.Labeled(S("Str_Bar_Font"), _wmFont);
            var sizeCell = SliderBoxRow(S("Str_Stamp_FontSize"), StampInput.WmFontMin, StampInput.WmFontMax, _spec.WmFontPt, out _, out _wmSize);
            var colorCell = ColorRow(S("Str_Stamp_Color"), _wmColor, out _wmSwatch, c => { _wmColor = c; Schedule(); }, "StampWatermarkColor");

            // Image mode, in the same slots: the chosen file across both columns, then its scale.
            var imgRow = new DockPanel { MinHeight = PageWindowLayout.ControlHeight };
            var chooseBtn = UiKit.Make(S("Str_Stamp_ChooseImage"), false);
            chooseBtn.Height = PageWindowLayout.ControlHeight;
            chooseBtn.Padding = new Thickness(10, 0, 10, 0);
            chooseBtn.Click += (_, _2) => ChooseImage();
            DockPanel.SetDock(chooseBtn, Dock.Right);
            imgRow.Children.Add(chooseBtn);
            _wmImageLabel = new TextBlock { Text = System.IO.Path.GetFileName(_wmImagePath ?? ""), Foreground = R("MutedTextBrush"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            imgRow.Children.Add(_wmImageLabel);
            var imageCell = PageWindowLayout.Labeled(" ", imgRow);
            var scaleCell = SliderBoxRow(S("Str_Stamp_Scale"), 10, 200, _spec.WmScale * 100, out _wmScale, out _);

            _wmTextOnly = [textCell, fontCell, sizeCell, colorCell];
            _wmImageOnly = [imageCell, scaleCell];

            var opacityCell = SliderBoxRow(S("Str_Stamp_Opacity"), 5, 100, _spec.WmOpacity * 100, out _wmOpacity, out _);
            var angleCell = SliderBoxRow(S("Str_Stamp_Angle"), -90, 90, _spec.WmAngle, out _wmAngle, out _);
            _wmRange = MakeField(_spec.WmRange);
            _wmRange.ToolTip = S("Str_Crop_RangeTip");
            _wmRange.TextChanged += (_, _2) => Schedule();

            _wmBody = Grid2(
                PageWindowLayout.Labeled(" ", Row(typeRow)), PageWindowLayout.Labeled(S("Str_Stamp_Position"), _wmPos),
                Span(Swap(textCell, imageCell)),
                Swap(fontCell, scaleCell), Swap(sizeCell, new Border()),
                Swap(colorCell, new Border()), opacityCell,
                angleCell, PageWindowLayout.Labeled(S("Str_Stamp_Pages"), _wmRange));
            wrap.Children.Add(_wmBody);
            return wrap;
        }

        // ---------- shared builders ----------
        private static CheckBox SectionToggle(string text, bool on)
        {
            var cb = UiKit.CheckBox(text);
            cb.IsChecked = on;
            cb.FontSize = 13;
            cb.FontWeight = FontWeights.SemiBold;
            cb.VerticalAlignment = VerticalAlignment.Center;
            return cb;
        }

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

        // Two cells that take turns in one slot (text and image watermark), so switching never
        // moves the cells around them.
        private static Grid Swap(UIElement first, UIElement second)
        {
            var slot = new Grid();
            slot.Children.Add(first);
            slot.Children.Add(second);
            return slot;
        }

        private static FrameworkElement Row(FrameworkElement control)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(0);
            return new Border { MinHeight = PageWindowLayout.ControlHeight, Child = control };
        }

        private static TextBox MakeField(string text)
        {
            var field = UiKit.Field();
            field.Text = text;
            field.Height = PageWindowLayout.ControlHeight;
            field.VerticalContentAlignment = VerticalAlignment.Center;
            field.Padding = new Thickness(6, 0, 6, 0);
            // The field template has no disabled look; dim it like the checkboxes and radios while
            // its section is off.
            field.IsEnabledChanged += (_, _) => field.Opacity = field.IsEnabled ? 1 : 0.4;
            return field;
        }

        // A slider paired with a small numeric input box (two-way synced), e.g. font size.
        private StackPanel SliderBoxRow(string label, double min, double max, double value, out Slider slider, out TextBox box)
        {
            var grid = new Grid { Height = PageWindowLayout.ControlHeight };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var s = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), SmallChange = 1, LargeChange = 4, VerticalAlignment = VerticalAlignment.Center };
            if (_darkSlider != null) s.Style = _darkSlider;
            s.VerticalAlignment = VerticalAlignment.Center;
            s.Focusable = true;   // the app's DarkSlider style takes sliders out of Tab
            System.Windows.Automation.AutomationProperties.SetName(s, label);
            var b = MakeField(((int)Math.Round(value)).ToString());
            b.Width = 46;
            b.Margin = new Thickness(8, 0, 0, 0);
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            bool guard = false;
            s.ValueChanged += (_, _2) => { if (guard) return; guard = true; b.Text = ((int)Math.Round(s.Value)).ToString(); guard = false; Schedule(); };
            b.TextChanged += (_, _2) => { if (guard) return; if (StampInput.TryParseNumber(b.Text, CultureInfo.CurrentCulture, out double d)) { guard = true; s.Value = Math.Max(min, Math.Min(max, d)); guard = false; Schedule(); } };
            Grid.SetColumn(s, 0); Grid.SetColumn(b, 1);
            grid.Children.Add(s); grid.Children.Add(b);
            slider = s; box = b;
            return PageWindowLayout.Labeled(label, grid);
        }

        private ComboBox MakePosCombo(int h, int v)
        {
            var combo = new ComboBox { Height = PageWindowLayout.ControlHeight };
            System.Windows.Automation.AutomationProperties.SetName(combo, S("Str_Stamp_Position"));
            if (_darkCombo != null) combo.Style = _darkCombo; else { combo.Background = R("BgCanvas"); combo.Foreground = R("TextBrush"); }
            int sel = 0;
            for (int i = 0; i < Positions.Length; i++)
            {
                combo.Items.Add(S(Positions[i].key));
                if (Positions[i].h == h && Positions[i].v == v) sel = i;
            }
            combo.SelectedIndex = sel;
            return combo;
        }

        private static RadioButton MakeRadio(string text, bool isChecked)
        {
            var rb = UiKit.Radio(text);
            rb.IsChecked = isChecked;
            return rb;
        }

        private StackPanel ColorRow(string label, Color initial, out Border swatch, Action<Color> onPick, string automationId)
        {
            var sw = new Border
            {
                Width = 44, Height = 22, CornerRadius = UiKit.RadControl,
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(initial), SnapsToDevicePixels = true
            };

            // The swatch is a real Button (chrome-free template) so the click is rock-solid - a plain
            // Border's MouseLeftButtonUp was unreliable here, which is why the color never updated.
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            var btn = new Button
            {
                // Focusable (the default) so Tab reaches it and Space/Enter opens the picker (BF-U004).
                Content = sw, Cursor = Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Template = new ControlTemplate(typeof(Button)) { VisualTree = cp }
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(btn, automationId);
            System.Windows.Automation.AutomationProperties.SetName(btn, label);
            btn.Click += (_, _2) =>
            {
                var current = sw.Background is SolidColorBrush b ? b.Color : initial;
                var dlg = new ColorPickerDialog(this, current);
                dlg.ShowDialog();
                // Apply SelectedColor unconditionally rather than gating on DialogResult: opening the picker
                // as a nested dialog from this modal window + the fade-close makes ShowDialog return false even
                // on OK. On Cancel, SelectedColor is still the original color, so this is harmless.
                sw.Background = new SolidColorBrush(dlg.SelectedColor);
                onPick(dlg.SelectedColor);
                btn.Focus();   // keyboard users continue from the button they opened the picker with
            };

            swatch = sw;
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            return PageWindowLayout.Labeled(label, Row(btn));
        }

        private void UpdateEnabledStates()
        {
            bool image = _wmImageRadio?.IsChecked == true;
            foreach (var cell in _wmTextOnly) cell.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
            foreach (var cell in _wmImageOnly) cell.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            // A section's controls work only while its checkbox has it on.
            _wmBody?.IsEnabled = _wmEnable?.IsChecked == true;
            _numBody?.IsEnabled = _numEnable?.IsChecked == true;
            // Nothing to apply unless at least one section is enabled - EXCEPT when the document
            // already has stamps: applying with both sections off is how they are removed (#145).
            _applyBtn?.IsEnabled = _hadExisting
                || _numEnable?.IsChecked == true || _wmEnable?.IsChecked == true;
        }

        // Mirroring only makes sense for a left/right position, so gray it out on a centered one.
        private void UpdateMirrorEnabled()
        {
            if (_numMirror == null || _numPos == null) return;
            _numMirror.IsEnabled = Positions[Math.Max(0, _numPos.SelectedIndex)].h != 1;
        }

        private void ChooseImage()
        {
            var picker = new KillerPDF.Controls.ImageAssetPickerDialog(this);
            if (picker.ShowDialog() == true && !string.IsNullOrEmpty(picker.SelectedPath))
            {
                _wmImagePath = picker.SelectedPath;
                _wmImageLabel.Text = picker.SelectedName ?? System.IO.Path.GetFileName(_wmImagePath);
                LoadWatermarkImage();
                Schedule();
            }
        }

        private void LoadWatermarkImage()
        {
            _wmImageSrc = null;
            if (string.IsNullOrEmpty(_wmImagePath) || !System.IO.File.Exists(_wmImagePath)) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(_wmImagePath!);
                bmp.EndInit();
                bmp.Freeze();
                _wmImageSrc = bmp;
            }
            catch { _wmImageSrc = null; }
        }

        // ---------- preview ----------
        // ---------- Preview page stepper ----------
        // The nav row under the frame (spec 2.2): 36 high, 32 x 26 arrows at the frame's edges.
        private Grid BuildPageNav()
        {
            _prevArrow = PageNavButtons.Previous(() => GoToPage(_pageIndex - 1));
            _nextArrow = PageNavButtons.Next(() => GoToPage(_pageIndex + 1));
            _pageNavLabel = new TextBlock
            {
                FontFamily = UiKit.MonoFont, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            _pageNavLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var row = new Grid { Height = 36, Margin = new Thickness(8, 2, 8, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_pageNavLabel, 1);
            Grid.SetColumn(_nextArrow, 2);
            row.Children.Add(_prevArrow);
            row.Children.Add(_pageNavLabel);
            row.Children.Add(_nextArrow);
            UpdatePageNav();
            return row;
        }

        // Button clicks step one page and render immediately.
        private void GoToPage(int idx)
        {
            if (_pageProvider == null) return;
            idx = Math.Max(0, Math.Min(_pageCount - 1, idx));
            if (idx == _pageIndex) return;
            _pageIndex = idx;
            UpdatePageNav();
            RenderCurrentPage();
        }

        // Wheel stepping advances the page number (and arrow states) instantly and defers the heavy page
        // render until the wheel settles, so a fast flick scrolls quickly instead of blocking on each
        // page's rasterization.
        private void StepPage(int delta)
        {
            if (_pageProvider == null) return;
            int idx = Math.Max(0, Math.Min(_pageCount - 1, _pageIndex + delta));
            if (idx == _pageIndex) return;
            _pageIndex = idx;
            UpdatePageNav();
            _navRenderTimer ??= MakeNavRenderTimer();
            _navRenderTimer.Stop();
            _navRenderTimer.Start();
        }

        private System.Windows.Threading.DispatcherTimer MakeNavRenderTimer()
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            t.Tick += (_, _2) => { t.Stop(); RenderCurrentPage(); };
            return t;
        }

        private void RenderCurrentPage()
        {
            if (_pageProvider == null) return;
            var (src, markup, wpt, hpt) = _pageProvider(_pageIndex);
            if (src == null)
            {
                // The page number and arrows go back to the page still shown, so the preview's stamps match it.
                _pageIndex = _shownPageIndex;
                UpdatePageNav();
                return;
            }
            _shownPageIndex = _pageIndex;
            _pageSrc = src; _markupSrc = markup; _pageWpt = wpt; _pageHpt = hpt;
            _preview.Source = src;
            _markup.Source = markup;
            RenderPreview();
        }

        private void UpdatePageNav()
        {
            if (_pageNavLabel == null) return;
            _pageNavLabel.Text = string.Format(S("Str_PageOf"), _pageIndex + 1, _pageCount);
            _prevArrow.IsEnabled = _pageIndex > 0;
            _nextArrow.IsEnabled = _pageIndex < _pageCount - 1;
        }

        private void SizePreviewImage()
        {
            if (_previewArea == null) return;
            var page = _pageHpt > 0 && _pageWpt > 0 ? new Size(_pageWpt, _pageHpt) : new Size(_pageSrc.PixelWidth, _pageSrc.PixelHeight);
            Size fit = PageWindowLayout.FitPage(new Size(_previewArea.ActualWidth, _previewArea.ActualHeight), page, page);
            _preview.Width = fit.Width;
            _preview.Height = fit.Height;
            _overlay.Width = fit.Width;
            _overlay.Height = fit.Height;
            _markup.Width = fit.Width;
            _markup.Height = fit.Height;
        }

        private static HashSet<int> ParseRange(string range, int pageCount)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(range))
            {
                for (int i = 0; i < pageCount; i++) set.Add(i);
                return set;
            }
            foreach (var part in range.Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    if (int.TryParse(p[..dash].Trim(), out int a) && int.TryParse(p[(dash + 1)..].Trim(), out int b))
                        {
                            // Clamp the ends rather than testing each i: an unclamped "1-2147483647" wrapped
                            // i++ to int.MinValue at the top and never terminated. Same fix as ParseRange.
                            int lo = Math.Max(1, Math.Min(a, b)), hi = Math.Min(pageCount, Math.Max(a, b));
                            for (int i = lo; i <= hi; i++) set.Add(i - 1);
                        }
                }
                else if (int.TryParse(p, out int single) && single >= 1 && single <= pageCount) set.Add(single - 1);
            }
            return set;
        }

        private void RenderPreview()
        {
            _overlay.Children.Clear();
            _overlay.IsHitTestVisible = false;   // re-enabled by MakeDraggable only when a custom stamp is shown
            SizePreviewImage();
            double pw = _preview.Width, ph = _preview.Height;
            if (double.IsNaN(pw) || pw <= 0 || double.IsNaN(ph) || ph <= 0) return;
            double pxPerPt = _pageHpt > 0 ? ph / _pageHpt : 1;   // preview pixels per PDF point
            double mx = pw * 0.05, my = ph * 0.04;

            // Watermark sits under the page-number text (drawn first).
            if (_wmEnable.IsChecked == true && ParseRange(_wmRange.Text, _pageCount).Contains(_pageIndex))
            {
                if (_wmImageRadio.IsChecked == true && _wmImageSrc != null)
                {
                    double scale = _wmScale.Value / 100.0;
                    var (wpt, hpt) = WatermarkImageSize.Points(_wmImageSrc.PixelWidth, _wmImageSrc.PixelHeight,
                        _wmImageSrc.DpiX, _wmImageSrc.DpiY, pw / pxPerPt, scale);
                    double iw = wpt * pxPerPt, ih = hpt * pxPerPt;
                    var img = new Image { Source = _wmImageSrc, Width = iw, Height = ih, Opacity = _wmOpacity.Value / 100.0, Stretch = Stretch.Fill };
                    PlaceRotated(img, iw, ih, _wmPos.SelectedIndex, pw, ph, mx, my, _wmAngle.Value);
                }
                else if (_wmImageRadio.IsChecked != true && _wmText.Text.Length > 0)
                {
                    double fpx = ReadDouble(_wmSize, 64, StampInput.WmFontMin, StampInput.WmFontMax) * pxPerPt;
                    var tb = new TextBlock { Text = _wmText.Text, FontFamily = new FontFamily(_wmFont.SelectedItem as string ?? "Segoe UI"), FontWeight = FontWeights.Bold, FontSize = Math.Max(6, fpx), Foreground = new SolidColorBrush(_wmColor), Opacity = _wmOpacity.Value / 100.0 };
                    var sz = Measure(tb);
                    PlaceRotated(tb, sz.Width, sz.Height, _wmPos.SelectedIndex, pw, ph, mx, my, _wmAngle.Value);
                }
            }

            // Page number for the current page.
            if (_numEnable.IsChecked == true && ParseRange(_numRange.Text, _pageCount).Contains(_pageIndex))
            {
                double fpx = ReadDouble(_numSize, 12, StampInput.NumFontMin, StampInput.NumFontMax) * pxPerPt;
                string text = (_numFormat.Text.Length == 0 ? "{n}" : _numFormat.Text)
                    .Replace("{n}", (ReadInt(_numStart, 1) + _pageIndex).ToString())
                    .Replace("{N}", _pageCount.ToString());
                if (text.Length > 0)
                {
                    var tb = new TextBlock { Text = text, FontFamily = UiKit.UiFont, FontSize = Math.Max(5, fpx), Foreground = new SolidColorBrush(_numColor) };
                    var sz = Measure(tb);
                    int h = Positions[Math.Max(0, _numPos.SelectedIndex)].h, v = Positions[Math.Max(0, _numPos.SelectedIndex)].v;
                    double x, y;
                    if (h < 0)   // custom: drag the number anywhere on the page
                    {
                        bool mirroredHere = _numMirror.IsChecked == true && (_pageIndex % 2 == 1);
                        double cx = mirroredHere ? 1 - _spec.NumCustomX : _spec.NumCustomX;
                        x = cx * pw - sz.Width / 2;
                        y = _spec.NumCustomY * ph - sz.Height / 2;
                        MakeDraggable(tb, sz.Width, sz.Height, pw, ph, (fx, fy) =>
                        {
                            _spec.NumCustomX = mirroredHere ? 1 - fx : fx;
                            _spec.NumCustomY = fy;
                        });
                    }
                    else
                    {
                        if (_numMirror.IsChecked == true && h != 1 && (_pageIndex % 2 == 1)) h = 2 - h;
                        x = h == 0 ? mx : h == 2 ? pw - sz.Width - mx : (pw - sz.Width) / 2;
                        y = v == 0 ? my : v == 1 ? (ph - sz.Height) / 2 : ph - sz.Height - my;
                    }
                    Canvas.SetLeft(tb, x);
                    Canvas.SetTop(tb, y);
                    _overlay.Children.Add(tb);
                }
            }
        }

        private void PlaceRotated(FrameworkElement el, double w, double h, int posIndex, double pw, double ph, double mx, double my, double angle)
        {
            int hpos = Positions[Math.Max(0, posIndex)].h, vpos = Positions[Math.Max(0, posIndex)].v;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new RotateTransform(-angle);
            double x, y;
            if (hpos < 0)   // custom: drag the watermark anywhere on the page
            {
                x = _spec.WmCustomX * pw - w / 2;
                y = _spec.WmCustomY * ph - h / 2;
                MakeDraggable(el, w, h, pw, ph, (fx, fy) => { _spec.WmCustomX = fx; _spec.WmCustomY = fy; });
            }
            else
            {
                x = hpos == 0 ? mx : hpos == 2 ? pw - w - mx : (pw - w) / 2;
                y = vpos == 0 ? my : vpos == 1 ? (ph - h) / 2 : ph - h - my;
            }
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            _overlay.Children.Add(el);
        }

        // Makes a stamp element draggable in the preview; reports the new center as fractions of the page.
        private void MakeDraggable(FrameworkElement el, double elW, double elH, double pw, double ph, Action<double, double> onMove)
        {
            _overlay.IsHitTestVisible = true;
            el.IsHitTestVisible = true;
            el.Cursor = DragCursors.Open;
            bool dragging = false;
            el.MouseLeftButtonDown += (_, e) => { dragging = true; el.CaptureMouse(); DragCursors.BeginDrag(); e.Handled = true; };
            el.MouseLeftButtonUp   += (_, _2) => { dragging = false; el.ReleaseMouseCapture(); DragCursors.EndDrag(); };
            // A capture lost to an alt-tab or a dialog never reaches the button-up handler, which
            // would strand the closed hand on screen for the rest of the session.
            el.LostMouseCapture    += (_, _2) => { dragging = false; DragCursors.EndDrag(); };
            el.MouseMove += (_, e) =>
            {
                if (!dragging) return;
                var p = e.GetPosition(_overlay);
                double left = Math.Max(0, Math.Min(pw - elW, p.X - elW / 2));
                double top  = Math.Max(0, Math.Min(ph - elH, p.Y - elH / 2));
                Canvas.SetLeft(el, left);
                Canvas.SetTop(el, top);
                double fx = pw > 0 ? Math.Max(0, Math.Min(1, (left + elW / 2) / pw)) : 0.5;
                double fy = ph > 0 ? Math.Max(0, Math.Min(1, (top + elH / 2) / ph)) : 0.5;
                onMove(fx, fy);
            };
        }

        private static Size Measure(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize;
        }

        // A typed size is read in the current culture and held to the slider's range, so a
        // typed 5000 cannot reach the spec while the slider shows its maximum.
        private static double ReadDouble(TextBox tb, double fallback, double min, double max)
            => StampInput.ReadSize(tb.Text, CultureInfo.CurrentCulture, fallback, min, max);
        private static int ReadInt(TextBox tb, int fallback)
            => int.TryParse(tb.Text?.Trim(), out int i) ? i : fallback;

        private static int PositionIndex(int h, int v)
        {
            for (int i = 0; i < Positions.Length; i++)
                if (Positions[i].h == h && Positions[i].v == v) return i;
            return 0;
        }

        private void ResetAll()
        {
            var d = new StampSpec { NumbersEnabled = _numEnable.IsChecked == true };
            _numEnable.IsChecked = d.NumbersEnabled;
            _numStart.Text = d.StartNumber.ToString();
            _numFormat.Text = d.Format;
            _numSize.Text = d.NumFontPt.ToString("0");
            _numRange.Text = d.NumRange;
            _numColor = d.NumColor; _numSwatch.Background = new SolidColorBrush(d.NumColor);
            _wmText.Text = d.WmText;
            _wmSize.Text = d.WmFontPt.ToString("0");
            _wmFont.SelectedItem = d.WmFont;
            _wmColor = d.WmColor; _wmSwatch.Background = new SolidColorBrush(d.WmColor);
            _wmAngle.Value = d.WmAngle;
            _wmOpacity.Value = d.WmOpacity * 100;
            _wmScale.Value = d.WmScale * 100;
            _numPos.SelectedIndex = PositionIndex(d.NumPosH, d.NumPosV);
            _wmPos.SelectedIndex = PositionIndex(d.WmPosH, d.WmPosV);
            _numMirror.IsChecked = d.NumMirror;
            UpdateMirrorEnabled();
            _wmRange.Text = d.WmRange;
            _wmTextRadio.IsChecked = !d.WmIsImage;
            _wmImageRadio.IsChecked = d.WmIsImage;
            _wmImagePath = d.WmImagePath;
            _wmImageSrc = null;
            _wmImageLabel.Text = System.IO.Path.GetFileName(_wmImagePath ?? "");
            _spec.NumCustomX = d.NumCustomX; _spec.NumCustomY = d.NumCustomY;
            _spec.WmCustomX = d.WmCustomX; _spec.WmCustomY = d.WmCustomY;
            Schedule();
        }

        private void CommitAndClose()
        {
            _spec.NumbersEnabled = _numEnable.IsChecked == true;
            _spec.StartNumber = ReadInt(_numStart, 1);
            _spec.Format = _numFormat.Text.Length == 0 ? "{n}" : _numFormat.Text;
            _spec.NumFontPt = StampInput.ReadSize(_numSize.Text, CultureInfo.CurrentCulture, 12, StampInput.NumFontMin, StampInput.NumFontMax);
            _spec.NumColor = _numColor;
            _spec.NumRange = _numRange.Text.Trim();
            _spec.NumMirror = _numMirror.IsChecked == true;
            (_spec.NumPosH, _spec.NumPosV) = (Positions[Math.Max(0, _numPos.SelectedIndex)].h, Positions[Math.Max(0, _numPos.SelectedIndex)].v);

            _spec.WmEnabled = _wmEnable.IsChecked == true;
            _spec.WmIsImage = _wmImageRadio.IsChecked == true;
            _spec.WmText = _wmText.Text;
            _spec.WmFontPt = StampInput.ReadSize(_wmSize.Text, CultureInfo.CurrentCulture, 64, StampInput.WmFontMin, StampInput.WmFontMax);
            _spec.WmFont = StampInput.FontToKeep(_wmFont.SelectedItem as string, _spec.WmFont);
            _spec.WmColor = _wmColor;
            _spec.WmOpacity = _wmOpacity.Value / 100.0;
            _spec.WmAngle = _wmAngle.Value;
            _spec.WmScale = _wmScale.Value / 100.0;
            _spec.WmImagePath = _wmImagePath;
            _spec.WmRange = _wmRange.Text.Trim();
            (_spec.WmPosH, _spec.WmPosV) = (Positions[Math.Max(0, _wmPos.SelectedIndex)].h, Positions[Math.Max(0, _wmPos.SelectedIndex)].v);

            Result = _spec;
            Applied = true;
            Close();
        }
    }
}
