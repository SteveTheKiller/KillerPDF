using System;
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
        private double _pageWpt, _pageHpt;
        private readonly int _pageCount;
        private int _pageIndex;
        private readonly StampSpec _spec;
        private readonly bool _hadExisting;   // dialog opened on a doc that already has stamps (#145)
        // Renders an arbitrary page for the preview stepper: returns that page's bitmap + size in points.
        private readonly Func<int, (BitmapSource? src, double wpt, double hpt)>? _pageProvider;
        private TextBlock _pageNavLabel = null!;
        private Button _prevArrow = null!, _nextArrow = null!;
        private System.Windows.Threading.DispatcherTimer? _navRenderTimer;

        private readonly Image _preview = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.4 }
        };
        private readonly Canvas _overlay = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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
        private StackPanel _numBody = null!;

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
        private StackPanel _wmBody = null!, _wmTextPanel = null!, _wmImagePanel = null!;

        private readonly Style? _darkSlider, _darkCombo;

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];
        private static string S(string key) => Application.Current.TryFindResource(key) as string ?? key;

        // (resource key, horizontal 0/1/2, vertical 0 top / 1 middle / 2 bottom)
        private static readonly (string key, int h, int v)[] Positions =
        [
            ("Str_Pos_BottomCenter", 1, 2), ("Str_Pos_BottomRight", 2, 2), ("Str_Pos_BottomLeft", 0, 2),
            ("Str_Pos_TopCenter", 1, 0), ("Str_Pos_TopRight", 2, 0), ("Str_Pos_TopLeft", 0, 0),
            ("Str_Pos_Center", 1, 1), ("Str_Pos_Custom", -1, -1)
        ];

        public StampWindow(Window owner, BitmapSource pageSrc, double pageWpt, double pageHpt,
                           int pageCount, int pageIndex, StampSpec? existing,
                           Func<int, (BitmapSource? src, double wpt, double hpt)>? pageProvider = null)
        {
            _pageSrc = pageSrc;
            _pageWpt = pageWpt;
            _pageHpt = pageHpt;
            _pageCount = pageCount;
            _pageIndex = pageIndex;
            _pageProvider = pageProvider;
            _hadExisting = existing is not null;   // #145: clearing existing stamps must stay applyable
            _spec = existing?.Clone() ?? new StampSpec { NumbersEnabled = true };
            Result = _spec;

            Title = "KillerPDF - " + S("Str_Stamp_Suffix");
            Width = 980;
            Height = 720;
            MinWidth = 680;
            MinHeight = 480;
            DialogChrome.Configure(this, owner, resizable: true);

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
            var root = new DockPanel();

            // ---- Right sidebar ----
            // Small right padding so the always-on scrollbar tucks near the window edge; the footer and
            // scrolled content get their own right inset so nothing sits under the bar.
            var sidebar = new Border { Width = 300, Background = Brushes.Transparent, Padding = new Thickness(16, 8, 4, 14) };
            DockPanel.SetDock(sidebar, Dock.Right);
            var side = new DockPanel();

            // Docked footer: Reset all link above a right-aligned Cancel / Apply row. Right inset keeps the
            // buttons off the reserved scrollbar gutter.
            var bottom = new StackPanel { Margin = new Thickness(0, 10, 12, 0) };
            var resetLink = UiKit.LinkLabel(S("Str_Tf_ResetAll"), ResetAll);
            resetLink.Margin = new Thickness(0, 0, 0, 8);
            bottom.Children.Add(resetLink);
            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = UiKit.Make(S("Str_Tf_Cancel"), false);
            cancelBtn.Click += (_, _2) => { Applied = false; Close(); };
            cancelBtn.IsCancel = true;   // Esc
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            actionRow.Children.Add(cancelBtn);
            _applyBtn = UiKit.Make(S("Str_Tf_Apply"), true);
            _applyBtn.Click += (_, _2) => CommitAndClose();
            _applyBtn.IsDefault = true;   // Enter
            actionRow.Children.Add(_applyBtn);
            bottom.Children.Add(actionRow);
            DockPanel.SetDock(bottom, Dock.Bottom);
            side.Children.Add(bottom);

            var stack = new StackPanel();
            stack.Children.Add(BuildWatermarkSection());
            stack.Children.Add(Divider());
            stack.Children.Add(BuildNumbersSection());

            // Scrollbar is ALWAYS reserved (Visible, not Auto) so the content never shifts left when it
            // appears. This is the rule for these sidebar windows.
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Visible, Content = stack };
            side.Children.Add(scroller);
            sidebar.Child = side;
            root.Children.Add(sidebar);

            // ---- Left preview ----
            var previewWrap = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(8, 4, 8, 12),
                ClipToBounds = true
            };
            previewWrap.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
            previewWrap.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");

            // The page image (row 0) and the stepper (row 1) live in separate rows so the stepper sits
            // BELOW the page instead of overlapping it - same row layout as the print preview.
            var previewLayout = new Grid();
            previewLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var imageHost = new Grid();
            AddGrain(imageHost, owner, 0.05, cornerRadius: 0);
            RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
            _preview.Source = _pageSrc;
            imageHost.Children.Add(_preview);
            imageHost.Children.Add(_overlay);
            Grid.SetRow(imageHost, 0);
            previewLayout.Children.Add(imageHost);
            previewWrap.Child = previewLayout;
            _previewArea = imageHost;   // size the page against the image row only, never the stepper row
            // Page stepper: the wheel over the preview (or the arrows) walks pages so you can preview the
            // stamp on any page - the per-page number and range checks update as you go. Only when the caller
            // supplies a page provider and there's more than one page.
            if (_pageProvider != null && _pageCount > 1)
            {
                var nav = BuildPageNav();
                Grid.SetRow(nav, 1);
                previewLayout.Children.Add(nav);
                previewWrap.PreviewMouseWheel += (_, e) =>
                {
                    int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
                    StepPage(e.Delta < 0 ? notches : -notches);
                    e.Handled = true;
                };
            }
            _previewArea.SizeChanged += (_, _2) => { SizePreviewImage(); Schedule(); };
            root.Children.Add(previewWrap);

            Content = DialogChrome.Frame(this, Owner, "KillerPDF - " + S("Str_Stamp_Suffix"), () => { Applied = false; Close(); }, root);

            // Esc-to-close is wired by DialogChrome.Frame; Enter commits.
            KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitAndClose(); };
        }

        private static void AddGrain(Grid host, Window owner, double fallback, double cornerRadius)
        {
            var grain = (owner as MainWindow)?.GrainTexture;
            if (grain == null) return;
            double op = Application.Current.Resources["GrainOpacity"] is double go ? go : fallback;
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(cornerRadius), IsHitTestVisible = false, Opacity = op,
                Background = new ImageBrush(grain) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 256, 256), Stretch = Stretch.None }
            });
        }

        // ---------- Page Numbers section ----------
        private FrameworkElement BuildNumbersSection()
        {
            var wrap = new StackPanel();
            _numBody = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            _numEnable = SectionToggle(S("Str_Stamp_SecNumbers"), _spec.NumbersEnabled);
            _numEnable.Checked   += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            _numEnable.Unchecked += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            wrap.Children.Add(SectionHeaderRow(_numEnable, _numBody));

            _numBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_StartAt")));
            _numStart = UiKit.Field();
            _numStart.Text = _spec.StartNumber.ToString();
            _numStart.Margin = new Thickness(0, 0, 0, 8);
            _numStart.TextChanged += (_, _2) => Schedule();
            _numBody.Children.Add(_numStart);

            _numBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Format")));
            _numFormat = UiKit.Field();
            _numFormat.Text = _spec.Format;
            _numFormat.TextChanged += (_, _2) => Schedule();
            _numBody.Children.Add(_numFormat);
            _numBody.Children.Add(new TextBlock { Text = S("Str_Stamp_Hint"), Foreground = R("MutedTextBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            _numBody.Children.Add(new TextBlock { Text = S("Str_Stamp_Hint2"), Foreground = R("MutedTextBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

            _numBody.Children.Add(SliderBoxRow(S("Str_Stamp_FontSize"), 6, 96, _spec.NumFontPt, out _, out _numSize));

            _numBody.Children.Add(ColorRow(S("Str_Stamp_Color"), _numColor, out _numSwatch, c => { _numColor = c; Schedule(); }));

            _numBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Pages")));
            _numRange = UiKit.Field();
            _numRange.Text = _spec.NumRange;
            _numRange.ToolTip = S("Str_Crop_RangeTip");
            _numRange.Margin = new Thickness(0, 0, 0, 8);
            _numRange.TextChanged += (_, _2) => Schedule();
            _numBody.Children.Add(_numRange);

            // Position is the last page-number option (it's the least-changed setting).
            _numBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Position")));
            _numPos = MakePosCombo(_spec.NumPosH, _spec.NumPosV);
            _numPos.SelectionChanged += (_, _2) => { UpdateMirrorEnabled(); Schedule(); };
            _numBody.Children.Add(_numPos);

            _numMirror = UiKit.CheckBox(S("Str_Stamp_Mirror"));
            _numMirror.IsChecked = _spec.NumMirror;
            _numMirror.Margin = new Thickness(0, 6, 0, 0);
            _numMirror.Checked   += (_, _2) => Schedule();
            _numMirror.Unchecked += (_, _2) => Schedule();
            _numBody.Children.Add(_numMirror);
            UpdateMirrorEnabled();

            wrap.Children.Add(_numBody);
            return wrap;
        }

        // ---------- Watermark section ----------
        private FrameworkElement BuildWatermarkSection()
        {
            var wrap = new StackPanel();
            _wmBody = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            _wmEnable = SectionToggle(S("Str_Stamp_SecWatermark"), _spec.WmEnabled);
            _wmEnable.Checked   += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            _wmEnable.Unchecked += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            wrap.Children.Add(SectionHeaderRow(_wmEnable, _wmBody));

            // Type: text vs image (clean UiKit radios line up with the section content directly).
            var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            _wmTextRadio = MakeRadio(S("Str_Stamp_WmText"), !_spec.WmIsImage);
            _wmTextRadio.Margin = new Thickness(0, 0, 14, 0);
            _wmImageRadio = MakeRadio(S("Str_Stamp_WmImage"), _spec.WmIsImage);
            _wmTextRadio.Checked  += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            _wmImageRadio.Checked += (_, _2) => { UpdateEnabledStates(); Schedule(); };
            typeRow.Children.Add(_wmTextRadio);
            typeRow.Children.Add(_wmImageRadio);
            _wmBody.Children.Add(typeRow);

            // Text sub-panel
            _wmTextPanel = new StackPanel();
            _wmTextPanel.Children.Add(UiKit.GroupLabel(S("Str_Stamp_WmTextLabel")));
            _wmText = UiKit.Field();
            _wmText.Text = _spec.WmText;
            _wmText.Margin = new Thickness(0, 0, 0, 8);
            _wmText.TextChanged += (_, _2) => Schedule();
            _wmTextPanel.Children.Add(_wmText);

            var wmFontRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            wmFontRow.Children.Add(new TextBlock { Text = S("Str_Bar_Font"), Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _wmFont = new ComboBox { Width = 188, Height = 26, MaxDropDownHeight = 320, VerticalAlignment = VerticalAlignment.Center };
            if (_darkCombo != null) _wmFont.Style = _darkCombo; else { _wmFont.Background = R("BgCanvas"); _wmFont.Foreground = R("TextBrush"); }
            foreach (var fn in MainWindow.SystemFontNames) _wmFont.Items.Add(fn);
            _wmFont.SelectedItem = _spec.WmFont;
            _wmFont.SelectionChanged += (_, _2) => Schedule();
            wmFontRow.Children.Add(_wmFont);
            _wmTextPanel.Children.Add(wmFontRow);
            _wmTextPanel.Children.Add(SliderBoxRow(S("Str_Stamp_FontSize"), 12, 200, _spec.WmFontPt, out _, out _wmSize));
            _wmTextPanel.Children.Add(ColorRow(S("Str_Stamp_Color"), _wmColor, out _wmSwatch, c => { _wmColor = c; Schedule(); }));
            _wmBody.Children.Add(_wmTextPanel);

            // Image sub-panel: filename fills the left, the Choose button sits right-aligned across from it.
            _wmImagePanel = new StackPanel();
            var imgRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var chooseBtn = UiKit.Make(S("Str_Stamp_ChooseImage"), false);
            chooseBtn.Click += (_, _2) => ChooseImage();
            DockPanel.SetDock(chooseBtn, Dock.Right);
            imgRow.Children.Add(chooseBtn);
            _wmImageLabel = new TextBlock { Text = System.IO.Path.GetFileName(_wmImagePath ?? ""), Foreground = R("MutedTextBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            imgRow.Children.Add(_wmImageLabel);
            _wmImagePanel.Children.Add(imgRow);
            _wmImagePanel.Children.Add(SliderBoxRow(S("Str_Stamp_Scale"), 10, 200, _spec.WmScale * 100, out _wmScale, out _));
            _wmBody.Children.Add(_wmImagePanel);

            // Shared watermark controls
            _wmBody.Children.Add(SliderBoxRow(S("Str_Stamp_Angle"), -90, 90, _spec.WmAngle, out _wmAngle, out _));
            _wmBody.Children.Add(SliderBoxRow(S("Str_Stamp_Opacity"), 5, 100, _spec.WmOpacity * 100, out _wmOpacity, out _));

            _wmBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Position")));
            _wmPos = MakePosCombo(_spec.WmPosH, _spec.WmPosV);
            _wmPos.SelectionChanged += (_, _2) => Schedule();
            _wmBody.Children.Add(_wmPos);

            _wmBody.Children.Add(UiKit.GroupLabel(S("Str_Stamp_Pages")));
            _wmRange = UiKit.Field();
            _wmRange.Text = _spec.WmRange;
            _wmRange.ToolTip = S("Str_Crop_RangeTip");
            _wmRange.TextChanged += (_, _2) => Schedule();
            _wmBody.Children.Add(_wmRange);

            wrap.Children.Add(_wmBody);
            return wrap;
        }

        // ---------- shared builders ----------
        private CheckBox SectionToggle(string text, bool on)
        {
            var cb = UiKit.CheckBox(text);
            cb.IsChecked = on;
            cb.FontSize = 13;
            cb.FontWeight = FontWeights.SemiBold;
            cb.VerticalAlignment = VerticalAlignment.Center;
            return cb;
        }

        // Collapsible section header. The enable checkbox itself expands (checked) or collapses (unchecked)
        // the body; the chevron is just a non-clickable indicator of that state.
        private FrameworkElement SectionHeaderRow(CheckBox enable, StackPanel body)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var chevron = new TextBlock
            {
                FontSize = 12, Foreground = R("MutedTextBrush"),
                Width = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            void Sync()
            {
                bool on = enable.IsChecked == true;
                body.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = on ? "▾" : "▸";   // down when expanded, right when collapsed
            }
            enable.Checked   += (_, _2) => Sync();
            enable.Unchecked += (_, _2) => Sync();
            Sync();
            row.Children.Add(chevron);
            row.Children.Add(enable);
            return row;
        }

        // A slider paired with a small numeric input box (two-way synced), e.g. font size.
        private FrameworkElement SliderBoxRow(string label, double min, double max, double value, out Slider slider, out TextBox box)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            panel.Children.Add(UiKit.GroupLabel(label));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var s = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), SmallChange = 1, LargeChange = 4, VerticalAlignment = VerticalAlignment.Center };
            if (_darkSlider != null) s.Style = _darkSlider;
            var b = UiKit.Field(46);
            b.Text = ((int)Math.Round(value)).ToString();
            b.Margin = new Thickness(8, 0, 0, 0);
            bool guard = false;
            s.ValueChanged += (_, _2) => { if (guard) return; guard = true; b.Text = ((int)Math.Round(s.Value)).ToString(); guard = false; Schedule(); };
            b.TextChanged += (_, _2) => { if (guard) return; if (double.TryParse(b.Text, out double d)) { guard = true; s.Value = Math.Max(min, Math.Min(max, d)); guard = false; Schedule(); } };
            Grid.SetColumn(s, 0); Grid.SetColumn(b, 1);
            grid.Children.Add(s); grid.Children.Add(b);
            panel.Children.Add(grid);
            slider = s; box = b;
            return panel;
        }

        private ComboBox MakePosCombo(int h, int v)
        {
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 8), Height = 26 };
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

        private RadioButton MakeRadio(string text, bool isChecked)
        {
            var rb = UiKit.Radio(text);
            rb.IsChecked = isChecked;
            return rb;
        }

        private FrameworkElement ColorRow(string label, Color initial, out Border swatch, Action<Color> onPick)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock { Text = label, Foreground = R("MutedTextBrush"), FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

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
                Content = sw, Cursor = Cursors.Hand, Focusable = false,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Template = new ControlTemplate(typeof(Button)) { VisualTree = cp }
            };
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
            };

            swatch = sw;
            row.Children.Add(btn);
            return row;
        }

        private FrameworkElement Divider() => new Border { Height = 1, Background = R("CardBorderBrush"), Opacity = 0.6, Margin = new Thickness(0, 12, 0, 12) };

        private void UpdateEnabledStates()
        {
            if (_wmTextPanel != null) _wmTextPanel.Visibility = _wmImageRadio.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            if (_wmImagePanel != null) _wmImagePanel.Visibility = _wmImageRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            // Nothing to apply unless at least one section is enabled - EXCEPT when the document
            // already has stamps: applying with both sections off is how they are removed (#145).
            if (_applyBtn != null) _applyBtn.IsEnabled = _hadExisting
                || _numEnable?.IsChecked == true || _wmEnable?.IsChecked == true;
        }

        // Mirroring only makes sense for a left/right position, so grey it out on a centered one.
        private void UpdateMirrorEnabled()
        {
            if (_numMirror == null || _numPos == null) return;
            _numMirror.IsEnabled = Positions[Math.Max(0, _numPos.SelectedIndex)].h != 1;
        }

        private void ChooseImage()
        {
            var ofd = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Open)
                          { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
            if (ofd.ShowDialog(this) == true)
            {
                _wmImagePath = ofd.FileName;
                _wmImageLabel.Text = System.IO.Path.GetFileName(_wmImagePath);
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
        private FrameworkElement BuildPageNav()
        {
            _prevArrow = MakeNavArrow("", () => GoToPage(_pageIndex - 1));   // ChevronLeft
            _nextArrow = MakeNavArrow("", () => GoToPage(_pageIndex + 1));   // ChevronRight
            _pageNavLabel = new TextBlock
            {
                FontFamily = UiKit.UiFont, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0)
            };
            _pageNavLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 8)
            };
            row.Children.Add(_prevArrow);
            row.Children.Add(_pageNavLabel);
            row.Children.Add(_nextArrow);
            UpdatePageNav();
            return row;
        }

        // Same chrome as the print preview stepper (UiKit.Make), so the two windows share one button style.
        private Button MakeNavArrow(string glyph, Action onClick)
        {
            var b = UiKit.Make(glyph, false);
            b.FontFamily = UiKit.IconFont;
            b.FontSize = 12;
            b.Click += (_, _2) => onClick();
            return b;
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
            var (src, wpt, hpt) = _pageProvider(_pageIndex);
            if (src == null) return;
            _pageSrc = src; _pageWpt = wpt; _pageHpt = hpt;
            _preview.Source = src;
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
            double availW = Math.Max(1, _previewArea.ActualWidth - 48);
            double availH = Math.Max(1, _previewArea.ActualHeight - 48);
            double ar = _pageHpt > 0 ? _pageWpt / _pageHpt : (_pageSrc.PixelWidth / (double)_pageSrc.PixelHeight);
            double w = availW, h = w / ar;
            if (h > availH) { h = availH; w = h * ar; }
            _preview.Width = w;
            _preview.Height = h;
            _overlay.Width = w;
            _overlay.Height = h;
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
                        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) if (i >= 1 && i <= pageCount) set.Add(i - 1);
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
                    double iw = Math.Min(pw, _wmImageSrc.PixelWidth * pxPerPt * 0.5) * scale;
                    double ih = iw * _wmImageSrc.PixelHeight / Math.Max(1, _wmImageSrc.PixelWidth);
                    var img = new Image { Source = _wmImageSrc, Width = iw, Height = ih, Opacity = _wmOpacity.Value / 100.0, Stretch = Stretch.Fill };
                    PlaceRotated(img, iw, ih, _wmPos.SelectedIndex, pw, ph, mx, my, _wmAngle.Value);
                }
                else if (_wmImageRadio.IsChecked != true && _wmText.Text.Length > 0)
                {
                    double fpx = ReadDouble(_wmSize, 64) * pxPerPt;
                    var tb = new TextBlock { Text = _wmText.Text, FontFamily = new FontFamily(_wmFont.SelectedItem as string ?? "Segoe UI"), FontWeight = FontWeights.Bold, FontSize = Math.Max(6, fpx), Foreground = new SolidColorBrush(_wmColor), Opacity = _wmOpacity.Value / 100.0 };
                    var sz = Measure(tb);
                    PlaceRotated(tb, sz.Width, sz.Height, _wmPos.SelectedIndex, pw, ph, mx, my, _wmAngle.Value);
                }
            }

            // Page number for the current page.
            if (_numEnable.IsChecked == true && ParseRange(_numRange.Text, _pageCount).Contains(_pageIndex))
            {
                double fpx = ReadDouble(_numSize, 12) * pxPerPt;
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
            el.Cursor = Cursors.SizeAll;
            bool dragging = false;
            el.MouseLeftButtonDown += (_, e) => { dragging = true; el.CaptureMouse(); e.Handled = true; };
            el.MouseLeftButtonUp   += (_, _2) => { dragging = false; el.ReleaseMouseCapture(); };
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

        private static double ReadDouble(TextBox tb, double fallback)
            => double.TryParse(tb.Text?.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out double d) && d > 0 ? d : fallback;
        private static int ReadInt(TextBox tb, int fallback)
            => int.TryParse(tb.Text?.Trim(), out int i) ? i : fallback;

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
            Schedule();
        }

        private void CommitAndClose()
        {
            _spec.NumbersEnabled = _numEnable.IsChecked == true;
            _spec.StartNumber = ReadInt(_numStart, 1);
            _spec.Format = _numFormat.Text.Length == 0 ? "{n}" : _numFormat.Text;
            _spec.NumFontPt = ReadDouble(_numSize, 12);
            _spec.NumColor = _numColor;
            _spec.NumRange = _numRange.Text.Trim();
            _spec.NumMirror = _numMirror.IsChecked == true;
            (_spec.NumPosH, _spec.NumPosV) = (Positions[Math.Max(0, _numPos.SelectedIndex)].h, Positions[Math.Max(0, _numPos.SelectedIndex)].v);

            _spec.WmEnabled = _wmEnable.IsChecked == true;
            _spec.WmIsImage = _wmImageRadio.IsChecked == true;
            _spec.WmText = _wmText.Text;
            _spec.WmFontPt = ReadDouble(_wmSize, 64);
            _spec.WmFont = _wmFont.SelectedItem as string ?? "Segoe UI";
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
