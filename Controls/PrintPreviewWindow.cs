using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using KillerPDF.Services;

namespace KillerPDF
{
    /// <summary>
    /// KillerPDF's own print dialog with a working preview. WPF's built-in PrintDialog
    /// reports "This app doesn't support print preview", so we render the rasterized
    /// pages ourselves, expose printer / orientation / copies / page-range settings,
    /// and drive the spooler via a non-UI PrintDialog when the user clicks Print.
    /// </summary>
    internal sealed partial class PrintPreviewWindow : Window
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
        // Set while a job is rasterizing and spooling. The print scrim blocks the mouse but takes no
        // keyboard focus, so a keystroke in the Pages box (or an arrow key in a combo) still re-runs
        // UpdatePreview mid-job; without this it would re-enable Print and Enter (IsDefault) would
        // spool a second copy behind the scrim. Only the failure path clears it - success closes.
        private bool _printing;
        public volatile bool Canceled;        // set on close so the background render stops

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

        // Immutable copy of every layout choice used while composing a print job. The progress
        // scrim blocks the mouse but deliberately does not steal keyboard focus, so controls can
        // still receive keys while the 300-DPI pages are rendering. A job must not observe those
        // changes halfway through (especially N-up, which controls the page-loop increment).
        private readonly record struct PrintLayout(
            bool Landscape, int AlignH, int AlignV, int ScaleMode, double CustomPct,
            double MarginPx, int NUp, bool Duplex, bool Grayscale);

        private PrintLayout CurrentLayout() => new(
            _landscape, _alignH, _alignV, _scaleMode, _customPct, _marginPx,
            _nUp, _duplex, _grayscale);

        // Printable area in DIPs for the currently selected printer + orientation.
        private double _areaW = 816;   // Letter portrait fallback (8.5in * 96)
        private double _areaH = 1056;  // (11in * 96)

        private readonly Grid _previewHost = new();
        private readonly TextBlock _pageLabel = new();
        private readonly TextBlock _renderLabel = new();   // "Rendering X / Y" line shown above the page nav
        private Button _previousPage = null!;
        private Button _nextPage = null!;
        private ComboBox _printerCombo = null!;
        // #186: manual paper pick. Index 0 = "Match document" (the automatic MediaSizeForDocument
        // behavior); the rest are the driver's supported sizes, repopulated on printer change.
        private ComboBox _paperCombo = null!;
        private readonly System.Collections.Generic.List<PageMediaSize> _paperSizes = [];
        private PageMediaSize? _paperOverride;
        // #186 follow-up (adeit): paper SOURCE. Index 0 = printer default (ticket untouched);
        // the rest are the driver's reported input bins. WPF's InputBin enum is coarse - named
        // trays would need raw PrintTicket XML, which is not worth the driver roulette.
        private ComboBox _sourceCombo = null!;
        private readonly System.Collections.Generic.List<InputBin> _sourceBins = [];
        private InputBin? _sourceOverride;
        private TextBox _copiesBox = null!;
        private TextBox _pagesBox = null!;
        private Grid _rootGrid = null!;   // body host; the print-progress scrim is layered here

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

            Title  = S("Str_Print_Title");
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
            LoadPrinters();
            UpdateDuplexAvailability();
            RefreshArea();
            UpdatePreview();
        }

        protected override void OnClosed(EventArgs e)
        {
            Canceled = true;   // stop any in-flight background page rendering
            base.OnClosed(e);
            try { _server?.Dispose(); } catch { }
            // We own the flattened temp (kept alive so Print could re-rasterize at 300 DPI); clean it up.
            if (_cleanupPath != null) try { System.IO.File.Delete(_cleanupPath); } catch { }
        }

        // Brush, NOT SolidColorBrush: gradient themes (98SE's title bar, and any theme free to
        // define a surface as a gradient) made the old hard cast throw InvalidCastException the
        // moment the print preview opened.
        private static Brush R(string key)
            => Application.Current.Resources[key] as Brush ?? Brushes.Transparent;

        // For the one place that needs a raw COLOR (the busy-scrim veil): gradients fall back to
        // their first stop.
        private static Color RColor(string key) => Application.Current.Resources[key] switch
        {
            SolidColorBrush s => s.Color,
            LinearGradientBrush { GradientStops.Count: > 0 } g => g.GradientStops[0].Color,
            _ => Colors.Transparent,
        };

        private static string S(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

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
            var stepTemplate = BuildStepperTemplate();
            System.Windows.Controls.Primitives.RepeatButton Step(string glyph, int delta, int row)
            {
                var b = new System.Windows.Controls.Primitives.RepeatButton
                {
                    Content         = glyph,
                    Padding         = new Thickness(0),
                    FontSize        = 7,
                    Foreground      = R("TextBrush"),
                    Background      = R("ComboButtonBrush"),
                    BorderBrush     = R("ButtonEdgeBrush"),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    Focusable       = false,
                    OverridesDefaultStyle = true,
                    Template        = stepTemplate
                };
                b.Click += (_, _) => set(get() + delta);
                Grid.SetRow(b, row);
                return b;
            }
            g.Children.Add(Step("▲", +1, 0));
            g.Children.Add(Step("▼", -1, 1));
            return g;
        }

        // The stock RepeatButton template paints a white system spinner regardless of the active
        // palette. This template uses the same guaranteed combo-face and button-bevel resources as
        // the rest of the app; the bevel resources are zero-width outside classic themes.
        private static ControlTemplate BuildStepperTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));
            var face = new FrameworkElementFactory(typeof(Border)) { Name = "face" };
            face.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            face.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            face.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            face.SetValue(Border.CornerRadiusProperty, UiKit.RadControl);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            face.AppendChild(content);
            grid.AppendChild(face);

            var light = new FrameworkElementFactory(typeof(Border)) { Name = "light" };
            light.SetResourceReference(Border.BorderBrushProperty, "BevelLightBrush");
            light.SetResourceReference(Border.BorderThicknessProperty, "ButtonBevelLightThickness");
            grid.AppendChild(light);
            var dark = new FrameworkElementFactory(typeof(Border)) { Name = "dark" };
            dark.SetResourceReference(Border.BorderBrushProperty, "BevelDarkBrush");
            dark.SetResourceReference(Border.BorderThicknessProperty, "ButtonBevelDarkThickness");
            grid.AppendChild(dark);

            var template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.RepeatButton)) { VisualTree = grid };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, R("ComboButtonHoverBrush"), "face"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BorderBrushProperty, R("BevelDarkBrush"), "light"));
            pressed.Setters.Add(new Setter(Border.BorderBrushProperty, R("BevelLightBrush"), "dark"));
            template.Triggers.Add(pressed);
            return template;
        }

        // Pulls a named Style from the owning MainWindow so this dialog reuses the
        // app's themed ComboBox / chrome-close-button styling verbatim.
        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        private void ApplyComboStyle(ComboBox combo)
        {
            // Match the dialog buttons and the file dialog's field height.
            combo.Height = 28;
            if (FindOwnerStyle("DarkComboBox") is Style s)
            {
                combo.Style = s;
            }
            else
            {
                combo.Foreground  = R("TextBrush");
                combo.BorderBrush = R("CardBorderBrush");
                combo.Background  = R("ComboFieldBrush");
            }
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
        private double ScaleFor(int idx, double areaW, double areaH, int[] rw, int[] rh, PrintLayout layout)
        {
            double actual = _pageDipW[idx] / Math.Max(1, rw[idx]);
            return layout.ScaleMode switch
            {
                1 => actual,
                2 => actual * (layout.CustomPct / 100.0),
                _ => Math.Min(areaW / rw[idx], areaH / rh[idx])
            };
        }

        // Page offset within the printable area for the current alignment selection.
        private static double OffsetH(double areaW, double imgW, PrintLayout layout)
            => layout.AlignH == 0 ? 0 : layout.AlignH == 2 ? areaW - imgW : (areaW - imgW) / 2;
        private static double OffsetV(double areaH, double imgH, PrintLayout layout)
            => layout.AlignV == 0 ? 0 : layout.AlignV == 2 ? areaH - imgH : (areaH - imgH) / 2;

        // Column/row grid for the current pages-per-sheet count, oriented to the sheet.
        private static (int cols, int rows) NupGrid(PrintLayout layout) => layout.NUp switch
        {
            2 => layout.Landscape ? (2, 1) : (1, 2),
            4 => (2, 2),
            6 => layout.Landscape ? (3, 2) : (2, 3),
            9 => (3, 3),
            _ => (1, 1)
        };

        // The page indices the preview walks AND the Print button sends - whatever range is typed in the
        // Pages box (blank = every page; a range that matches no page = empty, which the preview and the
        // print guard both surface). Driving the preview off this keeps it showing exactly the pages that
        // will print (type "6" -> preview page 6).
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

        // Builds one sheet (aw x ah DIPs, white) holding the given source pages. 1-up honors the
        // scale mode + alignment + margin; N-up fits each page into its grid cell. Shared by the
        // preview and the print path so what you see is what prints.
        private Grid ComposeSheet(System.Collections.Generic.List<int> idxs, double aw, double ah,
                                  BitmapSource?[] pages, int[] rw, int[] rh)
            => ComposeSheet(idxs, aw, ah, pages, rw, rh, CurrentLayout());

        private Grid ComposeSheet(System.Collections.Generic.List<int> idxs, double aw, double ah,
                                  BitmapSource?[] pages, int[] rw, int[] rh, PrintLayout layout)
        {
            var sheet = new Grid
            {
                Width = aw, Height = ah, Background = Brushes.White, ClipToBounds = true,
                UseLayoutRounding = true, SnapsToDevicePixels = true
            };
            var canvas = new Canvas();
            double m = layout.MarginPx;

            if (layout.NUp <= 1)
            {
                if (idxs.Count > 0)
                {
                    int idx = idxs[0];
                    double s = ScaleFor(idx, aw - 2 * m, ah - 2 * m, rw, rh, layout);
                    double iw = rw[idx] * s, ih = rh[idx] * s;
                    // Snap to the printable area when the page is within a pixel of filling it, so the
                    // white sheet doesn't peek through as a 1px hairline at the page edge (float seam).
                    if (iw >= (aw - 2 * m) - 1.5) iw = aw - 2 * m + 1;   // +1 bleed: covers the right hairline (clipped by the sheet)
                    if (ih >= (ah - 2 * m) - 1.5) ih = ah - 2 * m + 1;
                    BitmapSource source = layout.Grayscale
                        ? PrintColorConverter.CreateGrayscaleBitmap(pages[idx]!) : pages[idx]!;
                    var img = new Image { Source = source, Width = iw, Height = ih };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, m + OffsetH(aw - 2 * m, iw, layout));
                    Canvas.SetTop(img, m + OffsetV(ah - 2 * m, ih, layout));
                    canvas.Children.Add(img);
                }
            }
            else
            {
                var (cols, rows) = NupGrid(layout);
                const double gap = 6;
                double cellW = (aw - 2 * m) / cols, cellH = (ah - 2 * m) / rows;
                for (int i = 0; i < idxs.Count && i < cols * rows; i++)
                {
                    int idx = idxs[i];
                    int row = i / cols, col = i % cols;
                    double availW = Math.Max(1, cellW - gap), availH = Math.Max(1, cellH - gap);
                    double s = Math.Min(availW / rw[idx], availH / rh[idx]);
                    double iw = rw[idx] * s, ih = rh[idx] * s;
                    BitmapSource source = layout.Grayscale
                        ? PrintColorConverter.CreateGrayscaleBitmap(pages[idx]!) : pages[idx]!;
                    var img = new Image { Source = source, Width = iw, Height = ih };
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
            _duplexCheck.ToolTip = ok ? null : S("Str_Print_NoTwoSidedSupport");
        }

        // ---- UI construction -------------------------------------------------

        private void BuildUi()
        {
            // Print Preview used to rebuild the dialog frame, title bar, bevels and clipping here.
            // That private shell drifted from every other dialog. Keep only its body and put it in
            // the canonical shared frame.
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(BuildSettingsColumn());
            body.Children.Add(BuildPreviewColumn());

            _rootGrid = new Grid();
            _rootGrid.Children.Add(body);
            Content = DialogChrome.Frame(this, Owner, S("Str_Print_Title"),
                () => { DialogResult = false; Close(); }, _rootGrid);
        }

        private Grid BuildSettingsColumn()
        {
            // Options live in a scroller (buttons are pinned below), so only a little top/side inset.
            var panel = new StackPanel { Margin = new Thickness(16, 8, 12, 4) };

            // Collapsible sections, the Transform/Stamp dialog pattern (WrapSection): PRINTER and
            // OUTPUT open, LAYOUT tucked away - it holds the set-and-forget options.
            int secPrinter = panel.Children.Count;

            panel.Children.Add(Label(S("Str_Print_Printer")));
            var printerCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(printerCombo);
            printerCombo.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < _queues.Count)
                {
                    _queue = _queues[i];
                    if (_paperCombo != null) PopulatePaperSizes();     // new driver, new paper list
                    if (_sourceCombo != null) PopulatePaperSources();  // and new input bins
                    RefreshArea(); UpdateDuplexAvailability(); UpdatePreview();
                }
            };
            _printerCombo = printerCombo;
            panel.Children.Add(printerCombo);

            // #186: paper size. "Match document" keeps the automatic pick; anything else
            // overrides both the preview sheet and the spooled ticket.
            panel.Children.Add(Label(S("Str_Print_Paper")));
            var paper = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(paper);
            _paperCombo = paper;
            PopulatePaperSizes();
            paper.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                _paperOverride = i > 0 && i - 1 < _paperSizes.Count ? _paperSizes[i - 1] : null;
                RefreshArea();
                UpdatePreview();
            };
            panel.Children.Add(paper);

            // Paper source (adeit's #186 follow-up). Default leaves the ticket alone.
            panel.Children.Add(Label(S("Str_Print_Source")));
            var source = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(source);
            _sourceCombo = source;
            PopulatePaperSources();
            source.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                _sourceOverride = i > 0 && i - 1 < _sourceBins.Count ? _sourceBins[i - 1] : null;
            };
            panel.Children.Add(source);
            WrapSection(panel, secPrinter, S("Str_Print_SecPrinter"), expanded: true);
            int secLayout = panel.Children.Count;

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
            _scaleBox = UiKit.Field();
            _scaleBox.Text = "100";
            _scaleBox.VerticalContentAlignment = VerticalAlignment.Center;
            _scaleBox.ToolTip = S("Str_Print_ScaleHint");
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
            WrapSection(panel, secLayout, S("Str_Print_SecLayout"), expanded: false);
            int secOutput = panel.Children.Count;

            // Color vs black & white. Sent on the print ticket so color-restricted print policies
            // (e.g. "B&W needs no password") see the job correctly instead of treating it as color.
            panel.Children.Add(Label(S("Str_Print_Color")));
            var colorMode = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(colorMode);
            colorMode.Items.Add(S("Str_Print_Color"));
            colorMode.Items.Add(S("Str_Print_BW"));
            _grayscale = App.GetSetting("PrintGrayscale") == "1";   // restore last color choice
            colorMode.SelectedIndex = _grayscale ? 1 : 0;
            colorMode.SelectionChanged += (s, _) =>
            {
                _grayscale = ((ComboBox)s).SelectedIndex == 1;
                UpdatePreview();
            };
            panel.Children.Add(colorMode);

            panel.Children.Add(Label(S("Str_Print_Copies")));
            _copiesBox = UiKit.Field();
            _copiesBox.Text = "1";
            _copiesBox.VerticalContentAlignment = VerticalAlignment.Center;
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
            _pagesBox = UiKit.Field();
            _pagesBox.Text = "";
            _pagesBox.Margin = new Thickness(0, 4, 0, 2);
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
            WrapSection(panel, secOutput, S("Str_Print_SecOutput"), expanded: true);
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
                // Stay out of the way while the open sections fit. Expanding Layout or shortening
                // the window still produces a real scrollbar when the options overflow.
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

        private DockPanel BuildPreviewColumn()
        {
            var wrap = new Border
            {
                Background       = R("BgCanvas"),
                BorderBrush      = R("PaneBorderBrush"),   // 1px frame, matching the main document pane
                BorderThickness  = new Thickness(1),
                // Keep a real gutter between the settings/scrollbar column and the preview frame.
                Margin           = new Thickness(8, 4, 8, 12),
                CornerRadius     = UiKit.RadControl
            };

            var grid = new Grid();
            grid.Children.Add(_previewHost);

            var navigation = new Grid { Height = 42, Margin = new Thickness(8, 0, 8, 8) };
            navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            navigation.ColumnDefinitions.Add(new ColumnDefinition());
            navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _previousPage = MakeButton("‹", false);
            _previousPage.Width = 44;
            _previousPage.ToolTip = S("Str_Kb_PrevPage");
            _previousPage.Click += (_, _) => { if (_previewIndex > 0) { _previewIndex--; UpdatePreview(); } };
            Grid.SetColumn(_previousPage, 0);
            navigation.Children.Add(_previousPage);
            _nextPage = MakeButton("›", false);
            _nextPage.Width = 44;
            _nextPage.ToolTip = S("Str_Kb_NextPage");
            _nextPage.Click += (_, _) => { if (_previewIndex < SheetCount() - 1) { _previewIndex++; UpdatePreview(); } };
            Grid.SetColumn(_nextPage, 2);
            navigation.Children.Add(_nextPage);

            _pageLabel.Foreground = R("TextBrush");
            _pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _pageLabel.FontSize = 12;

            // Rendering progress stays with the counter, but the compact strip now lives below the
            // framed content pane instead of consuming document-preview height.
            _renderLabel.Foreground = R("MutedTextBrush");
            _renderLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _renderLabel.FontSize = 11;
            _renderLabel.Visibility = Visibility.Collapsed;
            var navCenter = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            navCenter.Children.Add(_renderLabel);
            navCenter.Children.Add(_pageLabel);
            Grid.SetColumn(navCenter, 1);
            navigation.Children.Add(navCenter);

            // Film grain over the preview canvas, behind the page so it textures the margins
            // around the sheet rather than the document itself.
            var previewGrain = MakeGrainLayer();
            if (previewGrain != null)
            {
                Panel.SetZIndex(previewGrain, 0);
                Panel.SetZIndex(_previewHost, 1);
                grid.Children.Add(previewGrain);
            }

            wrap.Child = grid;
            var previewColumn = new DockPanel();
            DockPanel.SetDock(navigation, Dock.Bottom);
            previewColumn.Children.Add(navigation);
            previewColumn.Children.Add(UiKit.PaneWithShadow(wrap));
            Grid.SetColumn(previewColumn, 1);
            return previewColumn;
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
        // #186: fill the paper combo from the current queue's capabilities. Index 0 is always
        // the automatic "match document" entry; a driver that reports nothing leaves only that.
        private void PopulatePaperSizes()
        {
            _paperSizes.Clear();
            _paperCombo.Items.Clear();
            _paperCombo.Items.Add(S("Str_Print_PaperAuto"));
            try
            {
                if (_queue != null)
                    foreach (var ms in _queue.GetPrintCapabilities().PageMediaSizeCapability)
                    {
                        if (ms is null || !ms.Width.HasValue || !ms.Height.HasValue) continue;
                        _paperSizes.Add(ms);
                        _paperCombo.Items.Add(PaperDisplayName(ms));
                    }
            }
            catch { /* driver quirk - automatic entry only */ }
            _paperCombo.SelectedIndex = 0;
            _paperOverride = null;
        }

        // Collapsible section, the TransformWindow.WrapSection pattern: lifts the children added
        // since <paramref name="start"/> into a togglable body under a chevron header.
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
            var label = UiKit.SectionHeader(title);
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

        // Input bins from the driver; "Unknown" entries are noise and dropped.
        private void PopulatePaperSources()
        {
            _sourceBins.Clear();
            _sourceCombo.Items.Clear();
            _sourceCombo.Items.Add(S("Str_Print_SourceAuto"));
            try
            {
                if (_queue != null)
                    foreach (var bin in _queue.GetPrintCapabilities().InputBinCapability)
                    {
                        if (bin == InputBin.Unknown) continue;
                        _sourceBins.Add(bin);
                        _sourceCombo.Items.Add(CamelCaseBoundaryRegex().Replace(bin.ToString(), " "));
                    }
            }
            catch { /* driver quirk - default entry only */ }
            _sourceCombo.SelectedIndex = 0;
            _sourceOverride = null;
        }

        // "NorthAmericaLetter" -> "North America Letter (8.5 x 11 in)"; "ISOA4" -> "ISO A4
        // (210 x 297 mm)". North American papers show inches, everything else millimeters.
        private static string PaperDisplayName(PageMediaSize ms)
        {
            string raw = ms.PageMediaSizeName?.ToString() ?? "";
            string name = raw;
            if (name.StartsWith("ISO", StringComparison.Ordinal)) name = "ISO " + name[3..];
            else if (name.StartsWith("JIS", StringComparison.Ordinal)) name = "JIS " + name[3..];
            name = PaperNameBoundaryRegex().Replace(name, " ");
            string dims;
            if (raw.StartsWith("NorthAmerica", StringComparison.Ordinal))
            {
                double win = ms.Width!.Value / 96.0, hin = ms.Height!.Value / 96.0;
                dims = $"{win:0.##} x {hin:0.##} in";
            }
            else
            {
                double wmm = ms.Width!.Value / 96.0 * 25.4, hmm = ms.Height!.Value / 96.0 * 25.4;
                dims = $"{wmm:0} x {hmm:0} mm";
            }
            return name.Length > 0 ? $"{name} ({dims})" : dims;
        }

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
                    // A manual pick from the paper combo overrides the automatic match (#186).
                    var docMedia = _paperOverride ?? MediaSizeForDocument();
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
            if (_pages.Length == 0)
            {
                _pageLabel.Text = S("Str_Print_NoPages");
                _renderLabel.Visibility = Visibility.Collapsed;
                _previousPage?.IsEnabled = false;
                _nextPage?.IsEnabled = false;
                return;
            }

            var selected = SelectedIndices();
            if (selected.Count == 0)
            {
                // Reuses the string the print-time guard already shows, so there is nothing new to
                // translate. The Pages box drives this on every keystroke, so the message appears as
                // soon as the range stops matching anything.
                _pageLabel.Text = "";
                UpdateRenderLabel();
                _previewHost.Children.Add(new TextBlock
                {
                    Text                = S("Str_Dlg_NoValidPages"),
                    Foreground          = R("MutedTextBrush"),
                    FontSize            = 12,
                    Margin              = new Thickness(24),
                    TextWrapping        = TextWrapping.Wrap,
                    TextAlignment       = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                });
                _printBtn?.IsEnabled = false;
                _previousPage?.IsEnabled = false;
                _nextPage?.IsEnabled = false;
                return;
            }
            _printBtn?.IsEnabled = !_isLoading && !_printing;
            int sheets = Math.Max(1, (selected.Count + _nUp - 1) / _nUp);
            int sheet = Math.Max(0, Math.Min(_previewIndex, sheets - 1));
            _previewIndex = sheet;
            _previousPage?.IsEnabled = sheet > 0;
            _nextPage?.IsEnabled = sheet + 1 < sheets;

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
            _printBtn?.IsEnabled = true;
            UpdatePreview();
        }

        public void LoadFailed(string message)
        {
            _isLoading = false;
            _previewHost.Children.Clear();
            _previewHost.Children.Add(new TextBlock
            {
                Text                = string.Format(S("Str_Print_PreviewFailed"), message),
                Foreground          = R("MutedTextBrush"),
                TextWrapping        = TextWrapping.Wrap,
                TextAlignment       = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(24)
            });
        }

        // Spinning ring + progress text shown in the preview area while pages render.
        private StackPanel BuildLoadingIndicator()
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

            if (!int.TryParse(_copiesBox.Text?.Trim(), out int copies) || copies < 1)
                copies = 1;

            // The 300 DPI re-rasterize below (plus the compose + spool) runs long enough on real
            // documents that the window froze with no feedback - it read as a crash. Cover the card
            // with a progress scrim, push the heavy rasterization onto a background thread, and only
            // return to the PDF once the job is handed to the spooler.
            var overlay = ShowPrintOverlay(out TextBlock statusText);
            _printing = true;
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
                ticket.Duplexing = _duplex ? Duplexing.TwoSidedLongEdge : Duplexing.OneSided;
                ticket.OutputColor = _grayscale ? OutputColor.Grayscale : OutputColor.Color;
                // Same paper pick the preview used: the manual combo choice when one is set,
                // otherwise the automatic document-size match (see MediaSizeForDocument, #186).
                var docMedia = _paperOverride ?? MediaSizeForDocument();
                if (docMedia != null) ticket.PageMediaSize = docMedia;
                if (_sourceOverride is { } bin) ticket.InputBin = bin;   // paper source (#186)
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
                var layout = CurrentLayout();
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
                        var pixels = KillerPDF.Services.PdfiumInterop.RenderPageWithAnnotations(_renderPath, idx, w, h)
                            ?? pr.GetImage();
                        var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);   // #141
                        bs.Freeze();
                        hiPages[idx] = bs; hiW[idx] = w; hiH[idx] = h;
                        int shown = done;
                        try { statusText.Dispatcher.Invoke(() => statusText.Text = string.Format(S("Str_Print_Preparing"), shown, total)); }
                        catch { /* window closing */ }
                    }
                });

                statusText.Text = S("Str_Print_Sending");

                // Compose the sheets and spool them from a dedicated print thread - see
                // SpoolOnPrintThreadAsync for why this cannot stay on the UI thread. The sheet
                // sequence still carries the copy/duplex layout and `ticket` the orientation/color/
                // duplex, so the output is identical to the old path (issue #83 copy handling
                // unchanged - ticket.CopyCount stays 1).
                bool ok = await SpoolOnPrintThreadAsync(
                    indices, copies, aw, ah, hiPages, hiW, hiH, ticket, _queue.FullName, layout);

                PrintedPageCount = ok ? indices.Count : 0;
                DialogResult = ok;
                Close();
            }
            catch (Exception ex)
            {
                RemoveOverlay(overlay);   // drop the scrim so the error dialog isn't stuck behind it
                _printing = false;
                // Re-derive Print rather than switching it straight back on: the Pages box could have
                // been retyped behind the scrim, and a range that now matches nothing must stay disabled.
                UpdatePreview();
                KillerDialog.Show(this, S("Str_Err_PrintFailed") + "\n" + ex.GetType().Name + ": " + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [System.Text.RegularExpressions.GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
        private static partial System.Text.RegularExpressions.Regex CamelCaseBoundaryRegex();

        [System.Text.RegularExpressions.GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=\d)(?=[A-Z])")]
        private static partial System.Text.RegularExpressions.Regex PaperNameBoundaryRegex();

        /// <summary>
        /// Composes the sheet sequence and spools it from a dedicated STA thread that owns its own
        /// Dispatcher, returning true once the job has reached the spooler.
        /// </summary>
        /// <remarks>
        /// XpsDocumentWriter.WriteAsync is asynchronous only in the sense that it returns straight
        /// away: the serialization itself runs as dispatcher work items on the CALLING thread, and
        /// every selected page has to be encoded into the XPS package there. Driven from the UI
        /// thread that starved input for the entire spool - measured on A4 at 300 DPI, roughly 320 ms
        /// of solid UI block per page with only ~6% of input-priority work items getting through, so
        /// the window sat dead (scrim included, since the scrim needs that same thread to paint)
        /// until the printer had the whole job.
        ///
        /// Frozen BitmapSources cross threads freely, and the immutable PrintLayout snapshot keeps
        /// sheet composition independent of any controls receiving keys behind the progress scrim.
        /// The FixedPages are built on, and stay on, this thread. Output is otherwise unaffected:
        /// same FixedDocument, same ticket, same spooler.
        /// </remarks>
        private Task<bool> SpoolOnPrintThreadAsync(
            List<int> indices, int copies, double aw, double ah,
            BitmapSource?[] hiPages, int[] hiW, int[] hiH, PrintTicket ticket, string queueName,
            PrintLayout layout)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new System.Threading.Thread(() =>
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                // WriteAsync completes back on this dispatcher, so it has to be pumping before the
                // work starts - queue the job, then run the loop.
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        done.TrySetResult(await ComposeAndSpool(
                            indices, copies, aw, ah, hiPages, hiW, hiH, ticket, queueName, layout));
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { dispatcher.InvokeShutdown(); }
                }));
                System.Windows.Threading.Dispatcher.Run();
            });
            // STA: both the XPS serializer and the spooler reach apartment-bound COM underneath.
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "KillerPDF print spool";
            thread.Start();
            return done.Task;
        }

        /// <summary>
        /// Builds the sheet sequence and hands it to the spooler. Runs entirely on the print thread.
        /// </summary>
        private async Task<bool> ComposeAndSpool(
            List<int> indices, int copies, double aw, double ah,
            BitmapSource?[] hiPages, int[] hiW, int[] hiH, PrintTicket ticket, string queueName,
            PrintLayout layout)
        {
            var fixedDoc = new FixedDocument();
            // Group the selected pages into sheets of layout.NUp and compose each sheet (margins +
            // alignment + scale are all handled inside ComposeSheet, shared with the preview).
            // The whole sheet sequence is emitted `copies` times so the app controls the copy
            // count directly rather than trusting PrintTicket.CopyCount (issue #83).
            // Under two-sided printing an odd-sheet copy would leave the next copy starting on the
            // back of this copy's last sheet. Pad each copy (bar the last) with a blank sheet so every
            // copy begins on a fresh front side.
            int sheetsPerCopy = (indices.Count + layout.NUp - 1) / layout.NUp;
            bool padForDuplex = layout.Duplex && copies > 1 && (sheetsPerCopy % 2 == 1);
            for (int copy = 0; copy < copies; copy++)
            {
                for (int start = 0; start < indices.Count; start += layout.NUp)
                {
                    var chunk = indices.Skip(start).Take(layout.NUp).ToList();

                    var fp = new FixedPage { Width = aw, Height = ah };
                    var sheet = ComposeSheet(chunk, aw, ah, hiPages, hiW, hiH, layout);
                    FixedPage.SetLeft(sheet, 0);
                    FixedPage.SetTop(sheet, 0);
                    fp.Children.Add(sheet);
                    fp.Measure(new Size(aw, ah));
                    fp.Arrange(new Rect(new Point(), new Size(aw, ah)));

                    var pc = new PageContent();
                    ((IAddChild)pc).AddChild(fp);
                    fixedDoc.Pages.Add(pc);
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

            // A PrintQueue holds a spooler handle opened by whichever thread created it, so this
            // thread opens its own by name rather than borrowing the one the dialog is holding.
            using var server = new LocalPrintServer();
            using var queue  = ResolveQueue(server, queueName);

            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            var spooled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            return await spooled.Task;
        }

        /// <summary>
        /// Reopens a print queue by name on the calling thread. The dialog enumerates local queues
        /// and connections off the local server, so its FullName resolves here too; fall back to
        /// matching that same enumeration for the names the spooler will not take directly.
        /// </summary>
        private static PrintQueue ResolveQueue(LocalPrintServer server, string fullName)
        {
            try { return server.GetPrintQueue(fullName); }
            catch
            {
                var match = server
                    .GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                    .FirstOrDefault(q => q.FullName == fullName);
                if (match != null) return match;
                throw;
            }
        }

        // Body scrim with a spinner + live status line, shown while a print job rasterizes and
        // spools. It is painted last over _rootGrid, so its background swallows clicks and the
        // controls underneath cannot be re-triggered mid-print. Returns the scrim; `status` is its
        // message line, updated as the job progresses.
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
                Text                = S("Str_Print_PreparingToPrint"),
                Foreground          = R("TextBrush"),
                FontSize            = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 14, 0, 0)
            };

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(ring);
            stack.Children.Add(status);

            // Veil in the card's own color at high opacity, so the scrim reads on either theme.
            var veil = RColor("BackgroundBrush");
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

        // Parses "1-3,5" style ranges into sorted 0-based indices. Blank = all pages; a range that
        // matches no page returns empty and the callers surface it.
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
                        // Clamp the ends rather than testing each i inside the loop. With the test
                        // inside, "1-2147483647" ran i++ past int.MaxValue, wrapped to int.MinValue,
                        // and i <= b was true again - the loop never ended. The Pages box drives the
                        // preview live, so that froze the app on a keystroke. Same output either way.
                        if (a < 1) a = 1;
                        if (b > count) b = count;
                        for (int i = a; i <= b; i++) set.Add(i - 1);
                    }
                }
                else if (int.TryParse(part, out int v))
                {
                    if (v >= 1 && v <= count) set.Add(v - 1);
                }
            }
            // A blank box already returned every page above, so reaching here with nothing resolved
            // means the text matched no page - a number past the end, or a typo. Return the empty
            // set and let the callers surface it. Falling back to every page here meant a slipped
            // keystroke in the Pages box silently spooled the whole document.
            return [.. set];
        }

        // Shared themed button (UiKit.Make) so the print dialog matches every other dialog.
        private static Button MakeButton(string label, bool accent) => UiKit.Make(label, accent);
    }
}
