using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;

namespace KillerPDF
{
    /// <summary>
    /// KillerPDF's own print dialog with a working preview. WPF's built-in PrintDialog
    /// reports "This app doesn't support print preview", so we render the rasterized
    /// pages ourselves, expose printer / orientation / copies / page-range settings,
    /// and drive the spooler via a non-UI PrintDialog when the user clicks Print.
    /// </summary>
    internal sealed class PrintPreviewWindow : Window
    {
        private readonly BitmapSource?[] _pages;   // filled lazily as pages render in the background
        private readonly int[] _rasterW;
        private readonly int[] _rasterH;
        private readonly double[] _pageDipW;   // true physical page size in DIPs (for exact scaling)
        private readonly double[] _pageDipH;
        private readonly string  _renderPath;  // annotation-burned source PDF, re-read to rasterize at 300 DPI on print
        private readonly string? _cleanupPath; // temp flattened file owned by this window; deleted on close

        private int _loadedCount;              // pages rendered so far
        private bool _isLoading = true;        // true until every page has rendered
        private Button _printBtn = null!;      // disabled while pages are still loading
        public volatile bool Cancelled;        // set on close so the background render stops

        private readonly List<PrintQueue> _queues = [];
        private PrintQueue? _queue;
        private LocalPrintServer? _server;   // kept alive: queues reference their server
        private bool _landscape;
        private int _previewIndex;
        // Page position on the sheet: 0 = left/top, 1 = center, 2 = right/bottom.
        private int _alignH = 1;
        private int _alignV = 1;
        // Scale mode: 0 = fit to page, 1 = actual size (100%), 2 = custom percentage.
        private int _scaleMode = 0;
        private double _customPct = 100;
        private TextBox _scaleBox = null!;
        private double _marginPx;            // extra inset inside the printable area (DIPs)
        private int _nUp = 1;                // pages per sheet (1, 2, 4, 6, 9)
        private bool _duplex;                // two-sided printing (when the printer supports it)
        private CheckBox _duplexCheck = null!;
        private ComboBox _subsetCombo = null!;   // all / odd only / even only (#134, manual duplex)
        private bool _grayscale;             // send the job as grayscale/B&W rather than color

        // Printable area in DIPs for the currently selected printer + orientation.
        private double _areaW = 816;   // Letter portrait fallback (8.5in * 96)
        private double _areaH = 1056;  // (11in * 96)

        private readonly Grid _previewHost = new();
        private readonly TextBlock _pageLabel = new();
        private readonly TextBlock _renderLabel = new();   // "Rendering X / Y" line shown above the page nav
        private ComboBox _printerCombo = null!;
        private TextBox _copiesBox = null!;
        private TextBox _pagesBox = null!;
        private Grid _rootGrid = null!;   // clipped to rounded corners on resize

        // Segoe MDL2 Assets close glyph, matching the main window chrome close button.
        private const string CloseGlyph = "";

        /// <summary>Number of pages sent to the printer (set when the user prints).</summary>
        public int PrintedPageCount { get; private set; }

        public PrintPreviewWindow(Window? owner, int pageCount, double[] pageDipW, double[] pageDipH,
                                  string renderPath, string? cleanupPath)
        {
            // Pages render lazily on a background thread (fed in via SetRenderedPage), so the
            // window opens instantly and shows a spinner instead of blocking on large files.
            _pages   = new BitmapSource?[pageCount];
            _rasterW = new int[pageCount];
            _rasterH = new int[pageCount];
            _pageDipW = pageDipW;
            _pageDipH = pageDipH;
            _renderPath  = renderPath;
            _cleanupPath = cleanupPath;

            Title  = "KillerPDF - Print";
            Width  = 936;
            Height = 716;
            MinWidth  = 720;
            MinHeight = 480;
            DialogChrome.Configure(this, owner, resizable: true);
            UseLayoutRounding = true;

            // Borderless windows (WindowStyle.None) have no native resize border, so
            // WindowChrome restores edge resizing without showing the grip handle.
            // The visible card is inset by a small transparent margin for the drop shadow. The
            // resize border is a touch larger than that margin so the resize zone reaches the
            // card's visible edge rather than floating in the empty halo around it.
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(12),
                CaptionHeight         = 0,
                GlassFrameThickness   = new Thickness(0),
                CornerRadius          = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            // Reuse the main window's themed scrollbar (per-theme thumb) for this dialog's scrollers.
            if (owner?.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) is Style sbStyle)
                Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sbStyle;
            BuildUi();
            SizeChanged += (_, _) => ClipRoot();
            LoadPrinters();
            UpdateDuplexAvailability();
            RefreshArea();
            UpdatePreview();
        }

        protected override void OnClosed(EventArgs e)
        {
            Cancelled = true;   // stop any in-flight background page rendering
            base.OnClosed(e);
            try { _server?.Dispose(); } catch { }
            // We own the flattened temp (kept alive so Print could re-rasterize at 300 DPI); clean it up.
            if (_cleanupPath != null) try { System.IO.File.Delete(_cleanupPath); } catch { }
        }

        // Clips the content to the card's rounded corners (the rounded border alone doesn't clip
        // its children, so square corners would poke through).
        private void ClipRoot()
        {
            if (_rootGrid == null) return;
            _rootGrid.Clip = new RectangleGeometry(
                new Rect(0, 0, _rootGrid.ActualWidth, _rootGrid.ActualHeight), 6, 6);
        }

        private static SolidColorBrush R(string key)
            => (SolidColorBrush)Application.Current.Resources[key];

        private static string S(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        // Themes a TextBox so the OS default blue focus border / selection chrome doesn't show.
        private static void StyleTextBox(TextBox tb)
        {
            tb.BorderThickness     = new Thickness(1);
            tb.CaretBrush          = R("TextBrush");
            tb.SelectionBrush      = R("RowSelectedBrush");
            tb.SelectionTextBrush  = R("TextBrush");
            tb.Template            = MakeTextBoxTemplate();
        }

        // Wires a TextBox as a positive-integer field: digits only, clamped to [min,max], steppable with
        // the Up/Down arrow keys and the mouse wheel. Returns get/set so a spinner can drive the same value.
        private static (Func<int> Get, Action<int> Set) NumericField(TextBox box, int min, int max)
        {
            int Get() => int.TryParse(box.Text?.Trim(), out int n) ? Math.Min(Math.Max(n, min), max) : min;
            void Set(int n)
            {
                n = Math.Min(Math.Max(n, min), max);
                box.Text = n.ToString();
                box.CaretIndex = box.Text.Length;
            }
            box.PreviewTextInput += (_, ev) => ev.Handled = !ev.Text.All(char.IsDigit);
            DataObject.AddPastingHandler(box, (_, ev) =>
            {
                if (ev.DataObject.GetData(typeof(string)) is string s && !s.All(char.IsDigit))
                    ev.CancelCommand();
            });
            box.PreviewKeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Up)   { Set(Get() + 1); ev.Handled = true; }
                if (ev.Key == Key.Down) { Set(Get() - 1); ev.Handled = true; }
            };
            box.PreviewMouseWheel += (_, ev) => { Set(Get() + (ev.Delta > 0 ? 1 : -1)); ev.Handled = true; };
            box.LostFocus += (_, _) => Set(Get());
            return (Get, Set);
        }

        // Two stacked up/down stepper buttons (each half the field height) bound to the given get/set, sized to
        // sit flush against the right edge of a field inside a DockPanel/StackPanel row.
        private static Grid BuildStepper(Func<int> get, Action<int> set)
        {
            var g = new Grid { Width = 18, Margin = new Thickness(-1, 0, 0, 0) };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.Primitives.RepeatButton Step(string glyph, int delta, int row)
            {
                var b = new System.Windows.Controls.Primitives.RepeatButton
                {
                    Content         = glyph,
                    Padding         = new Thickness(0),
                    FontSize        = 7,
                    Foreground      = R("TextBrush"),
                    Background      = R("BgCanvas"),
                    BorderBrush     = R("CardBorderBrush"),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    Focusable       = false
                };
                b.Click += (_, _) => set(get() + delta);
                Grid.SetRow(b, row);
                return b;
            }
            g.Children.Add(Step("▲", +1, 0));
            g.Children.Add(Step("▼", -1, 1));
            return g;
        }


        private static ControlTemplate MakeTextBoxTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            var ct = new ControlTemplate(typeof(TextBox)) { VisualTree = b };
            // Dim the box when disabled (e.g. the custom-scale % field unless Scale = Custom) so it
            // reads as inactive instead of looking like an editable field.
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            ct.Triggers.Add(disabled);
            return ct;
        }

        // Pulls a named Style from the owning MainWindow so this dialog reuses the
        // app's themed ComboBox / chrome-close-button styling verbatim.
        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        private void ApplyComboStyle(ComboBox combo)
        {
            if (FindOwnerStyle("DarkComboBox") is Style s)
            {
                combo.Style = s;
            }
            else
            {
                combo.Foreground  = R("TextBrush");
                combo.BorderBrush = R("CardBorderBrush");
            }
            // Match the dropdown background to the document/preview area.
            combo.Background = R("BgCanvas");
        }

        // Builds a film-grain overlay matching the main window's texture and per-theme
        // opacity, or null if the owner hasn't generated a grain tile yet.
        private Border? MakeGrainLayer()
        {
            if ((Owner as MainWindow)?.GrainTexture is not ImageSource grain) return null;
            double op = Application.Current.TryFindResource("GrainOpacity") is double g ? g : 0.30;
            return new Border
            {
                IsHitTestVisible = false,
                Opacity          = op,
                Background = new ImageBrush(grain)
                {
                    TileMode      = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport      = new Rect(0, 0, 256, 256),
                    Stretch       = Stretch.None
                }
            };
        }

        // Raster-pixels -> DIP scale factor for a page under the current scale mode.
        // Fit shrinks the page to the printable area; actual/custom use the true physical size.
        private double ScaleFor(int idx, double areaW, double areaH, int[] rw, int[] rh)
        {
            double actual = _pageDipW[idx] / Math.Max(1, rw[idx]);
            return _scaleMode switch
            {
                1 => actual,
                2 => actual * (_customPct / 100.0),
                _ => Math.Min(areaW / rw[idx], areaH / rh[idx])
            };
        }

        // Page offset within the printable area for the current alignment selection.
        private double OffsetH(double areaW, double imgW)
            => _alignH == 0 ? 0 : _alignH == 2 ? areaW - imgW : (areaW - imgW) / 2;
        private double OffsetV(double areaH, double imgH)
            => _alignV == 0 ? 0 : _alignV == 2 ? areaH - imgH : (areaH - imgH) / 2;

        // Column/row grid for the current pages-per-sheet count, oriented to the sheet.
        private (int cols, int rows) NupGrid() => _nUp switch
        {
            2 => _landscape ? (2, 1) : (1, 2),
            4 => (2, 2),
            6 => _landscape ? (3, 2) : (2, 3),
            9 => (3, 3),
            _ => (1, 1)
        };

        // The page indices the preview walks AND the Print button sends - whatever range is typed in the
        // Pages box (blank or unparseable falls back to every page, matching ParseRange). Driving the
        // preview off this keeps it showing exactly the pages that will print (type "6" -> preview page 6).
        private List<int> SelectedIndices()
        {
            var list = ParseRange(_pagesBox.Text, _pages.Length);
            // Odd/even subset (#134): filters by 1-based page NUMBER on top of the typed range,
            // so "print odds, flip the stack, print evens" works like Word's manual duplex.
            // The preview follows, since everything drives off this list.
            int subset = _subsetCombo?.SelectedIndex ?? 0;
            if (subset != 0)
            {
                var filtered = new List<int>(list.Count);
                foreach (var i in list)
                    if ((i % 2 == 0) == (subset == 1)) filtered.Add(i);   // 0-based even index = odd page number
                list = filtered;
            }
            return list;
        }

        private int SheetCount() => _pages.Length == 0 ? 0 : (SelectedIndices().Count + _nUp - 1) / _nUp;

        // Builds one sheet (aw x ah DIPs, white) holding the given source pages. 1-up honours the
        // scale mode + alignment + margin; N-up fits each page into its grid cell. Shared by the
        // preview and the print path so what you see is what prints.
        private Grid ComposeSheet(System.Collections.Generic.List<int> idxs, double aw, double ah,
                                  BitmapSource?[] pages, int[] rw, int[] rh)
        {
            var sheet = new Grid
            {
                Width = aw, Height = ah, Background = Brushes.White, ClipToBounds = true,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            var canvas = new Canvas();
            double m = _marginPx;

            if (_nUp <= 1)
            {
                if (idxs.Count > 0)
                {
                    int idx = idxs[0];
                    double s = ScaleFor(idx, aw - 2 * m, ah - 2 * m, rw, rh);
                    double iw = rw[idx] * s, ih = rh[idx] * s;
                    // Snap to the printable area when the page is within a pixel of filling it, so the
                    // white sheet doesn't peek through as a 1px hairline at the page edge (float seam).
                    if (iw >= (aw - 2 * m) - 1.5) iw = aw - 2 * m + 1;   // +1 bleed: covers the right hairline (clipped by the sheet)
                    if (ih >= (ah - 2 * m) - 1.5) ih = ah - 2 * m + 1;
                    var img = new Image { Source = pages[idx]!, Width = iw, Height = ih };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, m + OffsetH(aw - 2 * m, iw));
                    Canvas.SetTop(img, m + OffsetV(ah - 2 * m, ih));
                    canvas.Children.Add(img);
                }
            }
            else
            {
                var (cols, rows) = NupGrid();
                const double gap = 6;
                double cellW = (aw - 2 * m) / cols, cellH = (ah - 2 * m) / rows;
                for (int i = 0; i < idxs.Count && i < cols * rows; i++)
                {
                    int idx = idxs[i];
                    int row = i / cols, col = i % cols;
                    double availW = Math.Max(1, cellW - gap), availH = Math.Max(1, cellH - gap);
                    double s = Math.Min(availW / rw[idx], availH / rh[idx]);
                    double iw = rw[idx] * s, ih = rh[idx] * s;
                    var img = new Image { Source = pages[idx]!, Width = iw, Height = ih };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, m + col * cellW + (cellW - iw) / 2);
                    Canvas.SetTop(img, m + row * cellH + (cellH - ih) / 2);
                    canvas.Children.Add(img);
                }
            }

            sheet.Children.Add(canvas);
            return sheet;
        }

        // Enables the two-sided checkbox only when the selected printer reports duplex support.
        private void UpdateDuplexAvailability()
        {
            bool ok = false;
            try
            {
                var caps = _queue?.GetPrintCapabilities();
                ok = caps?.DuplexingCapability?.Contains(Duplexing.TwoSidedLongEdge) == true;
            }
            catch { /* capability query not supported: leave disabled */ }

            if (_duplexCheck is null) return;
            _duplexCheck.IsEnabled = ok;
            if (!ok) { _duplexCheck.IsChecked = false; _duplex = false; }
            _duplexCheck.Opacity = ok ? 1.0 : 0.4;
            _duplexCheck.ToolTip = ok ? null : "The selected printer doesn't report two-sided support.";
        }

        // ---- UI construction -------------------------------------------------

        private void BuildUi()
        {
            var outer = new Border
            {
                Background      = R("BackgroundBrush"),
                BorderBrush     = R("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(7),
                // Halo must be >= BlurRadius + ShadowDepth or the window edge clips the shadow into a
                // thin hard line (invisible as a soft halo on light backgrounds). Recipe matches the
                // in-app card shadow so every dialog casts the same soft shadow on any background.
                Margin          = new Thickness(20),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color       = Colors.Black,
                    BlurRadius  = 16,
                    ShadowDepth = 3,
                    Direction   = 270,
                    Opacity     = 0.55
                }
            };
            var root = new DockPanel();
            // Film grain behind the whole dialog so the settings column and title bar carry
            // the same texture as the rest of the app. The preview column paints its own grain
            // over its lighter canvas.
            _rootGrid = new Grid();
            var bgGrain = MakeGrainLayer();
            if (bgGrain != null) _rootGrid.Children.Add(bgGrain);
            _rootGrid.Children.Add(root);
            outer.Child = _rootGrid;
            Content = outer;

            // Title bar (transparent so the dialog-wide grain shows through behind the title)
            // Shared KillerPDF dialog chrome: wordmark + courier suffix + the red ChromeCloseButton.
            var titleBar = DialogChrome.BuildTitleBar(this, Owner, S("Str_Print_Title"), () => { DialogResult = false; Close(); });
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            // Body: settings | preview
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(body);

            body.Children.Add(BuildSettingsColumn());
            body.Children.Add(BuildPreviewColumn());
        }

        private UIElement BuildSettingsColumn()
        {
            // Options live in a scroller (buttons are pinned below), so only a little top/side inset.
            var panel = new StackPanel { Margin = new Thickness(16, 8, 12, 4) };

            panel.Children.Add(Label(S("Str_Print_Printer")));
            var printerCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(printerCombo);
            printerCombo.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < _queues.Count) { _queue = _queues[i]; RefreshArea(); UpdateDuplexAvailability(); UpdatePreview(); }
            };
            _printerCombo = printerCombo;
            panel.Children.Add(printerCombo);

            panel.Children.Add(Label(S("Str_Print_Orientation")));
            var orient = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(orient);
            orient.Items.Add(S("Str_Print_Portrait"));
            orient.Items.Add(S("Str_Print_Landscape"));
            _landscape = App.GetSetting("PrintLandscape") == "1";   // restore last orientation
            orient.SelectedIndex = _landscape ? 1 : 0;
            orient.SelectionChanged += (s, _) =>
            {
                _landscape = ((ComboBox)s).SelectedIndex == 1;
                RefreshArea();
                UpdatePreview();
            };
            panel.Children.Add(orient);

            // Color vs black & white. Sent on the print ticket so color-restricted print policies
            // (e.g. "B&W needs no password") see the job correctly instead of treating it as color.
            panel.Children.Add(Label(S("Str_Print_Color")));
            var colorMode = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(colorMode);
            colorMode.Items.Add(S("Str_Print_Color"));
            colorMode.Items.Add(S("Str_Print_BW"));
            _grayscale = App.GetSetting("PrintGrayscale") == "1";   // restore last color choice
            colorMode.SelectedIndex = _grayscale ? 1 : 0;
            colorMode.SelectionChanged += (s, _) => _grayscale = ((ComboBox)s).SelectedIndex == 1;
            panel.Children.Add(colorMode);

            panel.Children.Add(Label(S("Str_Stamp_Position")));
            var position = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(position);
            // (resource key, horizontal 0/1/2, vertical 0/1/2)
            var positions = new (string key, int h, int v)[]
            {
                ("Str_Pos_Center", 1, 1), ("Str_Pos_Top", 1, 0), ("Str_Pos_Bottom", 1, 2),
                ("Str_Pos_Left", 0, 1), ("Str_Pos_Right", 2, 1),
                ("Str_Pos_TopLeft", 0, 0), ("Str_Pos_TopRight", 2, 0),
                ("Str_Pos_BottomLeft", 0, 2), ("Str_Pos_BottomRight", 2, 2)
            };
            foreach (var (key, _, _) in positions) position.Items.Add(S(key));
            position.SelectedIndex = 0;
            position.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < positions.Length)
                {
                    _alignH = positions[i].h;
                    _alignV = positions[i].v;
                    UpdatePreview();
                }
            };
            panel.Children.Add(position);

            // Margins: an extra inset applied inside the printable area.
            panel.Children.Add(Label(S("Str_Print_Margins")));
            var margins = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(margins);
            var marginOpts = new (string name, double inches)[]
            {
                (S("Str_Margin_None"), 0),
                ($"{S("Str_Margin_Narrow")} (0.25\")", 0.25),
                ($"{S("Str_Margin_Normal")} (0.5\")", 0.5),
                ($"{S("Str_Margin_Wide")} (1\")", 1.0)
            };
            foreach (var (name, _) in marginOpts) margins.Items.Add(name);
            margins.SelectedIndex = 0;
            margins.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < marginOpts.Length) { _marginPx = marginOpts[i].inches * 96.0; UpdatePreview(); }
            };
            panel.Children.Add(margins);

            // Pages per sheet (N-up): KillerPDF composes the sheet itself.
            panel.Children.Add(Label(S("Str_Print_PagesPerSheet")));
            var nup = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(nup);
            foreach (var n in new[] { "1", "2", "4", "6", "9" }) nup.Items.Add(n);
            nup.SelectedIndex = 0;
            nup.SelectionChanged += (s, _) =>
            {
                _nUp = int.TryParse((string)((ComboBox)s).SelectedItem, out int n) && n > 0 ? n : 1;
                _previewIndex = 0;
                UpdatePreview();
            };
            panel.Children.Add(nup);

            panel.Children.Add(Label(S("Str_Print_Scale")));
            var scale = new ComboBox { Margin = new Thickness(0, 4, 0, 6), Height = 26 };
            ApplyComboStyle(scale);
            scale.Items.Add(S("Str_Print_Fit"));
            scale.Items.Add(S("Str_Print_Actual"));
            scale.Items.Add(S("Str_Print_Custom"));
            scale.SelectedIndex = 0;
            panel.Children.Add(scale);

            // Custom percentage: a compact box (always 1-100ish) with a "%" suffix, revealed only
            // when "Custom" is chosen - it slides down into place instead of always taking space.
            _scaleBox = new TextBox
            {
                Text         = "100",
                Background    = R("BgCanvas"),
                Foreground    = R("TextBrush"),
                BorderBrush   = R("CardBorderBrush"),
                Padding       = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip       = S("Str_Print_ScaleHint")
            };
            StyleTextBox(_scaleBox);
            // Same numeric treatment as Copies: digits only, 1-1000 %, arrow-key / wheel / spinner stepping.
            var (getScale, setScale) = NumericField(_scaleBox, 1, 1000);
            _scaleBox.TextChanged += (s, _) =>
            {
                if (int.TryParse(((TextBox)s).Text?.Trim(), out int p) && p > 0)
                {
                    _customPct = p;
                    if (_scaleMode == 2) UpdatePreview();
                }
            };

            // Full-width row matching the Copies field: the box fills the column, with the stepper and the
            // "%" suffix docked at the right edge.
            var scaleRow = new DockPanel
            {
                Margin        = new Thickness(0, 0, 0, 12),
                LastChildFill = true,
                Visibility    = Visibility.Collapsed
            };
            var scalePct = new TextBlock
            {
                Text = "%", Foreground = R("MutedTextBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0)
            };
            DockPanel.SetDock(scalePct, Dock.Right);
            var scaleSpin = BuildStepper(getScale, setScale);
            DockPanel.SetDock(scaleSpin, Dock.Right);
            scaleRow.Children.Add(scalePct);    // rightmost
            scaleRow.Children.Add(scaleSpin);   // left of %
            scaleRow.Children.Add(_scaleBox);   // fills the rest of the column width
            var scaleSlide = new TranslateTransform();
            scaleRow.RenderTransform = scaleSlide;

            scale.SelectionChanged += (s, _) =>
            {
                _scaleMode = ((ComboBox)s).SelectedIndex;
                if (_scaleMode == 1) { _customPct = 100; _scaleBox.Text = "100"; }
                if (_scaleMode == 2)
                {
                    scaleRow.Visibility = Visibility.Visible;
                    scaleRow.BeginAnimation(UIElement.OpacityProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                            new Duration(TimeSpan.FromMilliseconds(140))));
                    scaleSlide.BeginAnimation(TranslateTransform.YProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(-8, 0,
                            new Duration(TimeSpan.FromMilliseconds(140)))
                        { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
                    _scaleBox.Focus();
                    _scaleBox.SelectAll();
                }
                else
                {
                    scaleRow.Visibility = Visibility.Collapsed;
                }
                UpdatePreview();
            };
            panel.Children.Add(scaleRow);

            panel.Children.Add(Label(S("Str_Print_Copies")));
            _copiesBox = new TextBox
            {
                Text        = "1",
                Background   = R("BgCanvas"),
                Foreground   = R("TextBrush"),
                BorderBrush  = R("CardBorderBrush"),
                Padding      = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            StyleTextBox(_copiesBox);
            // Copies is replicated `copies` times in DoPrint, so 1 means exactly one printout; min 1.
            var (getCopies, setCopies) = NumericField(_copiesBox, 1, 9999);

            // Stepper flush against the right edge of the full-width field, so the row lines up with the
            // Printer / Pages fields above and below it.
            var copiesSpin = BuildStepper(getCopies, setCopies);
            var copiesRow = new DockPanel { Margin = new Thickness(0, 4, 0, 12), LastChildFill = true };
            DockPanel.SetDock(copiesSpin, Dock.Right);
            copiesRow.Children.Add(copiesSpin);   // docked right, full field height
            copiesRow.Children.Add(_copiesBox);   // fills the rest of the column width
            panel.Children.Add(copiesRow);

            panel.Children.Add(Label(S("Str_Print_Pages")));
            _pagesBox = new TextBox
            {
                Text        = "",
                Margin      = new Thickness(0, 4, 0, 2),
                Background   = R("BgCanvas"),
                Foreground   = R("TextBrush"),
                BorderBrush  = R("CardBorderBrush"),
                Padding      = new Thickness(6, 4, 6, 4)
            };
            StyleTextBox(_pagesBox);
            // Typing a range re-filters the preview to just those pages (jump back to the first one).
            _pagesBox.TextChanged += (_, _) => { _previewIndex = 0; UpdatePreview(); };
            panel.Children.Add(_pagesBox);
            panel.Children.Add(new TextBlock
            {
                Text         = S("Str_Print_PagesHint"),
                Foreground   = R("MutedTextBrush"),
                FontSize     = 11,
                Margin       = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });

            // Odd/even subset (#134): Word-style manual duplex - print the odd pages, flip the
            // stack, print the even pages. Applies on top of the Pages range above.
            _subsetCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 14), Height = 26 };
            ApplyComboStyle(_subsetCombo);
            _subsetCombo.Items.Add(S("Str_Print_AllPages"));
            _subsetCombo.Items.Add(S("Str_Print_OddOnly"));
            _subsetCombo.Items.Add(S("Str_Print_EvenOnly"));
            _subsetCombo.SelectedIndex = 0;
            _subsetCombo.SelectionChanged += (_, _) => { _previewIndex = 0; UpdatePreview(); };
            panel.Children.Add(_subsetCombo);

            // Two-sided: the printer does the flipping; we just set the ticket when it's supported.
            _duplexCheck = UiKit.CheckBox(S("Str_Print_TwoSided"));
            _duplexCheck.Margin = new Thickness(0, 2, 0, 14);
            _duplexCheck.Checked   += (_, _) => _duplex = true;
            _duplexCheck.Unchecked += (_, _) => _duplex = false;
            _duplexCheck.IsChecked = App.GetSetting("PrintDuplex") == "1";   // restore; cleared below if unsupported
            panel.Children.Add(_duplexCheck);
            UpdateDuplexAvailability();

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = MakeButton(S("Str_Stamp_Cancel"), false);
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            cancel.IsCancel = true;          // Esc cancels the dialog
            var print = MakeButton(S("Str_Ctx_Print"), true);
            print.Click += (_, _) => DoPrint();
            print.IsDefault = true;          // Enter prints
            print.IsEnabled = !_isLoading;   // enabled once all pages have rendered
            _printBtn = print;
            cancel.Margin = new Thickness(8, 0, 0, 0);   // gap; Cancel sits to the right of Print
            btnRow.Children.Add(print);
            btnRow.Children.Add(cancel);

            // Scroll the options and PIN the buttons at the bottom, so they're never cut off when
            // the window is short or the custom-scale field is showing. Scroll wheel works too.
            var optionsScroller = new ScrollViewer
            {
                Content                       = panel,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(optionsScroller, 0);

            var btnHost = new Border { Child = btnRow, Padding = new Thickness(16, 8, 12, 12) };
            Grid.SetRow(btnHost, 1);

            var column = new Grid();
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.Children.Add(optionsScroller);
            column.Children.Add(btnHost);
            Grid.SetColumn(column, 0);
            return column;
        }

        private UIElement BuildPreviewColumn()
        {
            var wrap = new Border
            {
                Background       = R("BgCanvas"),
                BorderBrush      = R("PaneBorderBrush"),   // 1px frame, matching the main document pane
                BorderThickness  = new Thickness(1),
                Margin           = new Thickness(0, 4, 8, 12),
                CornerRadius     = new CornerRadius(4)
            };
            Grid.SetColumn(wrap, 1);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(_previewHost, 0);
            grid.Children.Add(_previewHost);

            var nav = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var prev = MakeButton("◀", false);   // left triangle
            prev.Click += (_, _) => { if (_previewIndex > 0) { _previewIndex--; UpdatePreview(); } };
            var next = MakeButton("▶", false);   // right triangle
            next.Click += (_, _) => { if (_previewIndex < SheetCount() - 1) { _previewIndex++; UpdatePreview(); } };
            _pageLabel.Foreground = R("TextBrush");
            _pageLabel.VerticalAlignment = VerticalAlignment.Center;
            _pageLabel.Margin = new Thickness(12, 0, 12, 0);
            _pageLabel.FontSize = 12;
            nav.Children.Add(prev);
            nav.Children.Add(_pageLabel);
            nav.Children.Add(next);

            // "Rendering X / Y" gets its own line above the page nav while pages stream in.
            _renderLabel.Foreground = R("MutedTextBrush");
            _renderLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _renderLabel.FontSize = 11;
            _renderLabel.Margin = new Thickness(0, 0, 0, 2);
            _renderLabel.Visibility = Visibility.Collapsed;

            var navColumn = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 8) };
            navColumn.Children.Add(_renderLabel);
            navColumn.Children.Add(nav);
            Grid.SetRow(navColumn, 1);
            grid.Children.Add(navColumn);

            // Film grain over the preview canvas, behind the page so it textures the margins
            // around the sheet rather than the document itself.
            var previewGrain = MakeGrainLayer();
            if (previewGrain != null)
            {
                Grid.SetRow(previewGrain, 0);
                Grid.SetRowSpan(previewGrain, 2);   // also texture the page-counter row so it isn't a flat gray bar
                Panel.SetZIndex(previewGrain, 0);
                Panel.SetZIndex(_previewHost, 1);
                Panel.SetZIndex(navColumn, 1);       // keep the counter and arrows above the grain
                grid.Children.Add(previewGrain);
            }

            wrap.Child = grid;
            return wrap;
        }

        private static TextBlock Label(string text) => new()
        {
            Text       = text,
            Foreground = R("TextBrush"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold
        };

        // ---- Behavior --------------------------------------------------------

        private void LoadPrinters()
        {
            try
            {
                _server = new LocalPrintServer();
                var found = _server.GetPrintQueues(
                [
                    EnumeratedPrintQueueTypes.Local,
                    EnumeratedPrintQueueTypes.Connections
                ]);
                foreach (var q in found) _queues.Add(q);
            }
            catch { /* spooler unavailable; fall back to default below */ }

            PrintQueue? def = null;
            try { def = LocalPrintServer.GetDefaultPrintQueue(); } catch { }
            if (def != null && !_queues.Any(q => q.FullName == def.FullName))
                _queues.Insert(0, def);

            foreach (var q in _queues) _printerCombo.Items.Add(q.FullName);

            string? savedPrinter = App.GetSetting("PrintPrinter");
            int sel = !string.IsNullOrEmpty(savedPrinter) ? _queues.FindIndex(q => q.FullName == savedPrinter) : -1;
            if (sel < 0) sel = def != null ? _queues.FindIndex(q => q.FullName == def.FullName) : 0;
            if (_queues.Count > 0)
            {
                _printerCombo.SelectedIndex = sel >= 0 ? sel : 0;
                _queue = _queues[_printerCombo.SelectedIndex];
            }
        }

        /// <summary>
        /// Picks the printer paper that matches the DOCUMENT page size (Acrobat's
        /// "choose paper source by PDF page size"). A printer defaulting to Letter under
        /// an A4 document letterboxes every sheet with white side margins; selecting the
        /// supported paper that matches the page removes them, in the preview and in the
        /// spooled output alike. Orientation-agnostic compare in DIPs with a 6-DIP
        /// (~1/16 inch) tolerance; the first page is the reference. Returns null when the
        /// printer stocks no matching paper - the printer default stays and fit-to-page
        /// letterboxing is then genuinely unavoidable.
        /// </summary>
        private PageMediaSize? MediaSizeForDocument()
        {
            try
            {
                if (_queue is null || _pageDipW.Length == 0) return null;
                double pw = _pageDipW[0], ph = _pageDipH[0];
                if (pw > ph) (pw, ph) = (ph, pw);   // portrait-normalize
                var caps = _queue.GetPrintCapabilities();
                foreach (var ms in caps.PageMediaSizeCapability)
                {
                    if (ms is null || !ms.Width.HasValue || !ms.Height.HasValue) continue;
                    double w = ms.Width.Value, h = ms.Height.Value;
                    if (w > h) (w, h) = (h, w);
                    if (Math.Abs(w - pw) <= 6 && Math.Abs(h - ph) <= 6) return ms;
                }
            }
            catch { /* driver quirk - keep the printer default */ }
            return null;
        }

        private void RefreshArea()
        {
            double w = 816, h = 1056;   // Letter portrait fallback
            try
            {
                if (_queue != null)
                {
                    var pd = new PrintDialog { PrintQueue = _queue };
                    // Paper follows the document page size when the printer supports it,
                    // so the preview sheet (and the print) has no letterbox margins.
                    var docMedia = MediaSizeForDocument();
                    if (docMedia != null)
                    {
                        var t = pd.PrintTicket;
                        t.PageMediaSize = docMedia;
                        pd.PrintTicket = t;
                    }
                    if (pd.PrintableAreaWidth > 0 && pd.PrintableAreaHeight > 0)
                    {
                        w = pd.PrintableAreaWidth;
                        h = pd.PrintableAreaHeight;
                    }
                }
            }
            catch { /* keep fallback */ }

            // Normalize to the requested orientation.
            if (_landscape) { if (w < h) (w, h) = (h, w); }
            else            { if (w > h) (w, h) = (h, w); }

            _areaW = w;
            _areaH = h;
        }

        private void UpdatePreview()
        {
            _previewHost.Children.Clear();
            if (_pages.Length == 0) { _pageLabel.Text = S("Str_Print_NoPages"); _renderLabel.Visibility = Visibility.Collapsed; return; }

            var selected = SelectedIndices();
            int sheets = Math.Max(1, (selected.Count + _nUp - 1) / _nUp);
            int sheet = Math.Max(0, Math.Min(_previewIndex, sheets - 1));
            _previewIndex = sheet;

            // Source pages on this sheet, taken from the SELECTED set (one for 1-up, up to _nUp for N-up).
            var idxs = new System.Collections.Generic.List<int>();
            for (int i = sheet * _nUp; i < Math.Min(selected.Count, sheet * _nUp + _nUp); i++)
                idxs.Add(selected[i]);

            // Page/sheet nav label is always shown; the "Rendering X / Y" line above it appears only while
            // pages are still streaming in. 1-up shows the real page number (so a filtered preview reads
            // "Page 6 of 108"); N-up shows the sheet position within the selected set.
            _pageLabel.Text = _nUp > 1
                ? $"Sheet {sheet + 1} of {sheets}"
                : string.Format(S("Str_PageOf"), idxs.Count > 0 ? idxs[0] + 1 : 1, _pages.Length);
            UpdateRenderLabel();

            // If any page on this sheet hasn't rendered yet, show a spinner instead of composing.
            if (idxs.Any(i => _pages[i] is null))
            {
                _previewHost.Children.Add(BuildLoadingIndicator());
                return;
            }

            var paper = ComposeSheet(idxs, _areaW, _areaH, _pages, _rasterW, _rasterH);

            var vb = new Viewbox
            {
                Child = paper, Stretch = Stretch.Uniform, Margin = new Thickness(20),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.5 }
            };
            _previewHost.Children.Add(vb);
        }

        // Shows/hides the "Rendering X / Y" line above the page nav based on load state.
        private void UpdateRenderLabel()
        {
            if (_isLoading)
            {
                _renderLabel.Text = $"Rendering {_loadedCount} / {_pages.Length}";
                _renderLabel.Visibility = Visibility.Visible;
            }
            else _renderLabel.Visibility = Visibility.Collapsed;
        }

        // Called (on the UI thread) by the background renderer as each page finishes.
        public void SetRenderedPage(int index, BitmapSource src, int w, int h)
        {
            if (index < 0 || index >= _pages.Length) return;
            _pages[index]   = src;
            _rasterW[index] = w;
            _rasterH[index] = h;
            _loadedCount++;

            int first = _previewIndex * _nUp;
            bool onCurrentSheet = index >= first && index < first + _nUp;
            if (onCurrentSheet)
                UpdatePreview();                 // reveal the page (or keep spinner if sheet incomplete)
            else if (_isLoading)
                UpdateRenderLabel();
        }

        // Called once every page has rendered: enables Print and finalizes the preview.
        public void FinishLoading()
        {
            _isLoading = false;
            if (_printBtn != null) _printBtn.IsEnabled = true;
            UpdatePreview();
        }

        public void LoadFailed(string message)
        {
            _isLoading = false;
            _previewHost.Children.Clear();
            _previewHost.Children.Add(new TextBlock
            {
                Text                = "Could not render preview:\n" + message,
                Foreground          = R("MutedTextBrush"),
                TextWrapping        = TextWrapping.Wrap,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(24)
            });
        }

        // Spinning ring + progress text shown in the preview area while pages render.
        private UIElement BuildLoadingIndicator()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 36, Height = 36, StrokeThickness = 3,
                Stroke = R("MutedTextBrush"),
                StrokeDashArray = [22, 200],
                StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rot = new RotateTransform();
            ring.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
                { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            sp.Children.Add(ring);
            sp.Children.Add(new TextBlock
            {
                Text                = $"Rendering {_loadedCount} / {_pages.Length}",
                Foreground          = R("MutedTextBrush"),
                FontSize            = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 12, 0, 0)
            });
            return sp;
        }

        // Persists the device-level print choices so the dialog reopens with the user's last setup.
        private void SavePrintPrefs()
        {
            try
            {
                if (_queue != null) App.SetSetting("PrintPrinter", _queue.FullName);
                App.SetSetting("PrintLandscape", _landscape ? "1" : "0");
                App.SetSetting("PrintGrayscale", _grayscale ? "1" : "0");
                App.SetSetting("PrintDuplex",    _duplex     ? "1" : "0");
            }
            catch { /* settings are best-effort */ }
        }

        private async void DoPrint()
        {
            if (_queue == null)
            {
                KillerDialog.Show(this, S("Str_Dlg_NoPrinter"), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Same list the preview and sheet count walk, so the odd/even subset (#134) reaches the
            // job as well - calling ParseRange directly here skipped it and printed every page.
            var indices = SelectedIndices();
            if (indices.Count == 0)
            {
                KillerDialog.Show(this, S("Str_Dlg_NoValidPages"), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int.TryParse(_copiesBox.Text?.Trim(), out int copies);
            if (copies < 1) copies = 1;

            // The 300 DPI re-rasterize below (plus the compose + spool) runs long enough on real
            // documents that the window froze with no feedback - it read as a crash. Cover the card
            // with a progress scrim, push the heavy rasterization onto a background thread, and only
            // return to the PDF once the job is handed to the spooler.
            var overlay = ShowPrintOverlay(out TextBlock statusText);
            _printBtn.IsEnabled = false;

            try
            {
                // Give the dispatcher one pass to actually paint the scrim BEFORE any work below.
                // Building the PrintDialog and reading PrintableAreaWidth queries the printer driver and
                // can stall for a beat; without this yield that stall happens while the old frame is still
                // on screen, so the click-to-scrim change looks laggy. Resuming at Background priority
                // (below Render) guarantees the scrim's render pass has run first.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                SavePrintPrefs();   // remember printer / orientation / color / two-sided for next time

                var pd = new PrintDialog { PrintQueue = _queue };
                var ticket = pd.PrintTicket;
                // Copies are produced by replicating the page sequence in the FixedDocument below
                // (see the outer copy loop), so the ticket itself only ever requests a single copy.
                // Relying on PrintTicket.CopyCount produced an extra copy on some printers (issue #83).
                ticket.CopyCount      = 1;
                ticket.PageOrientation = _landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
                if (_duplex) ticket.Duplexing = Duplexing.TwoSidedLongEdge;
                ticket.OutputColor = _grayscale ? OutputColor.Grayscale : OutputColor.Color;
                // Same paper pick the preview used: match the document page size so the
                // spooled sheet carries no letterbox margins (see MediaSizeForDocument).
                var docMedia = MediaSizeForDocument();
                if (docMedia != null) ticket.PageMediaSize = docMedia;
                pd.PrintTicket = ticket;

                double aw = pd.PrintableAreaWidth, ah = pd.PrintableAreaHeight;
                if (_landscape) { if (aw < ah) (aw, ah) = (ah, aw); }
                else            { if (aw > ah) (aw, ah) = (ah, aw); }
                if (aw <= 0 || ah <= 0) { aw = _areaW; ah = _areaH; }

                // Re-rasterize ONLY the selected pages at a true 300 DPI from the source, so the spooled
                // output is crisp regardless of the lighter preview rasters. Held only for this print call.
                // Frozen bitmaps cross threads freely, so the whole loop runs off the UI thread and reports
                // "Preparing page X of N" back to the scrim, keeping the window painting throughout.
                var hiPages = new BitmapSource?[_pages.Length];
                var hiW = new int[_pages.Length];
                var hiH = new int[_pages.Length];
                int total = indices.Count;
                await Task.Run(() =>
                {
                    using var dr = DocLib.Instance.GetDocReader(_renderPath, new PageDimensions(300.0 / 72.0));
                    int done = 0;
                    foreach (int idx in indices)
                    {
                        done++;
                        if (idx < 0 || idx >= _pages.Length) continue;
                        using var pr = dr.GetPageReader(idx);
                        int w = pr.GetPageWidth(), h = pr.GetPageHeight();
                        var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pr.GetImage(KillerPDF.Services.PdfRender.WithAnnotations), w * 4);   // #141
                        bs.Freeze();
                        hiPages[idx] = bs; hiW[idx] = w; hiH[idx] = h;
                        int shown = done;
                        try { statusText.Dispatcher.Invoke(() => statusText.Text = $"Preparing page {shown} of {total}…"); }
                        catch { /* window closing */ }
                    }
                });

                statusText.Text = "Sending to printer…";
                // Let the scrim repaint the new message before the UI-thread compose + spool below runs.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                var fixedDoc = new FixedDocument();
                // Group the selected pages into sheets of _nUp and compose each sheet (margins +
                // alignment + scale are all handled inside ComposeSheet, shared with the preview).
                // The whole sheet sequence is emitted `copies` times so the app controls the copy
                // count directly rather than trusting PrintTicket.CopyCount (issue #83).
                // Under two-sided printing an odd-sheet copy would leave the next copy starting on the
                // back of this copy's last sheet. Pad each copy (bar the last) with a blank sheet so every
                // copy begins on a fresh front side.
                int sheetsPerCopy = (indices.Count + _nUp - 1) / _nUp;
                bool padForDuplex = _duplex && copies > 1 && (sheetsPerCopy % 2 == 1);
                int composed = 0;   // yield to the dispatcher every so often so the spinner keeps turning
                for (int copy = 0; copy < copies; copy++)
                {
                    for (int start = 0; start < indices.Count; start += _nUp)
                    {
                        var chunk = indices.Skip(start).Take(_nUp).ToList();

                        var fp = new FixedPage { Width = aw, Height = ah };
                        var sheet = ComposeSheet(chunk, aw, ah, hiPages, hiW, hiH);
                        FixedPage.SetLeft(sheet, 0);
                        FixedPage.SetTop(sheet, 0);
                        fp.Children.Add(sheet);
                        fp.Measure(new Size(aw, ah));
                        fp.Arrange(new Rect(new Point(), new Size(aw, ah)));

                        var pc = new PageContent();
                        ((IAddChild)pc).AddChild(fp);
                        fixedDoc.Pages.Add(pc);

                        // Composing many high-res sheets can block long enough to stall the animation;
                        // hand the UI thread a render pass every dozen sheets to keep the scrim alive.
                        if (++composed % 12 == 0)
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    }

                    if (padForDuplex && copy < copies - 1)
                    {
                        var blank = new FixedPage { Width = aw, Height = ah };
                        blank.Measure(new Size(aw, ah));
                        blank.Arrange(new Rect(new Point(), new Size(aw, ah)));
                        var bpc = new PageContent();
                        ((IAddChild)bpc).AddChild(blank);
                        fixedDoc.Pages.Add(bpc);
                    }
                }

                // Spool ASYNCHRONOUSLY so the UI thread stays free and the spinner keeps turning while the
                // job serializes to the spooler. PrintDialog.PrintDocument does this synchronously, which
                // froze the animation until the printer had the whole document. The FixedDocument already
                // carries the copy/duplex layout and `ticket` the orientation/color/duplex, so the output
                // is identical to the old path (issue #83 copy handling unchanged - ticket.CopyCount stays 1).
                var writer = PrintQueue.CreateXpsDocumentWriter(_queue);
                var spooled = new TaskCompletionSource<bool>();
                writer.WritingCompleted += (_, ev) =>
                {
                    if (ev.Error is not null)  spooled.TrySetException(ev.Error);
                    else if (ev.Cancelled)     spooled.TrySetResult(false);
                    else                       spooled.TrySetResult(true);
                };
                // Write the FixedDocument itself, NOT its DocumentPaginator: the paginator path makes the
                // XPS serializer wrap each page's Visual in a fresh FixedPage, but the Visual already IS a
                // FixedPage - "FixedPage cannot contain another FixedPage". The FixedDocument overload
                // serializes the existing FixedPages directly.
                writer.WriteAsync(fixedDoc, ticket);
                bool ok = await spooled.Task;

                PrintedPageCount = ok ? indices.Count : 0;
                DialogResult = ok;
                Close();
            }
            catch (Exception ex)
            {
                RemoveOverlay(overlay);   // drop the scrim so the error dialog isn't stuck behind it
                _printBtn.IsEnabled = true;
                KillerDialog.Show(this, $"Print failed:\n{ex.GetType().Name}: {ex.Message}",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Full-card scrim with a spinner + live status line, shown while a print job rasterizes and
        // spools so the window shows progress instead of freezing silently. Added over _rootGrid (which
        // is already clipped to the card's rounded corners) and painted last, so it sits on top and its
        // Background swallows clicks - the buttons underneath can't be re-triggered mid-print. Returns
        // the scrim; `status` is its message line, updated as the job progresses.
        private Border ShowPrintOverlay(out TextBlock status)
        {
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 40, Height = 40, StrokeThickness = 3,
                Stroke = R("MutedTextBrush"),
                StrokeDashArray = [24, 200],
                StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rot = new RotateTransform();
            ring.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
                { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });

            status = new TextBlock
            {
                Text                = "Preparing to print…",
                Foreground          = R("TextBrush"),
                FontSize            = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 14, 0, 0)
            };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(ring);
            stack.Children.Add(status);

            // Veil in the card's own colour at high opacity, so the scrim reads on either theme.
            var veil = R("BackgroundBrush").Color;
            var overlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(232, veil.R, veil.G, veil.B)),
                Child      = stack
            };
            Panel.SetZIndex(overlay, 99);
            _rootGrid.Children.Add(overlay);
            return overlay;
        }

        private void RemoveOverlay(Border overlay) => _rootGrid.Children.Remove(overlay);

        // Parses "1-3,5" style ranges into sorted 0-based indices. Blank/invalid = all pages.
        private static List<int> ParseRange(string? text, int count)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0) return [.. Enumerable.Range(0, count)];

            var set = new SortedSet<int>();
            foreach (var raw in text.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                if (part.Contains('-'))
                {
                    var seg = part.Split('-');
                    if (seg.Length == 2 &&
                        int.TryParse(seg[0].Trim(), out int a) &&
                        int.TryParse(seg[1].Trim(), out int b))
                    {
                        if (a > b) (a, b) = (b, a);
                        for (int i = a; i <= b; i++)
                            if (i >= 1 && i <= count) set.Add(i - 1);
                    }
                }
                else if (int.TryParse(part, out int v))
                {
                    if (v >= 1 && v <= count) set.Add(v - 1);
                }
            }
            return set.Count == 0 ? [.. Enumerable.Range(0, count)] : [.. set];
        }

        // Shared themed button (UiKit.Make) so the print dialog matches every other dialog.
        private static Button MakeButton(string label, bool accent) => UiKit.Make(label, accent);
    }
}
