using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerPDF.Controls;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // The signature editor: the Create Signature window grown into the one place a signature is drawn, imported
    // or changed. A drawn signature gets the pen width; an imported picture gets the threshold that drops its
    // paper and a weight that grows or thins the ink. Both get edge feathering and an ink color. The preview
    // follows every change. The finished look is baked to a PNG only when a color or feather needs it, so a
    // plain black drawn signature stays vector strokes.
    public partial class MainWindow
    {
        /// <summary>What the editor opens on. Picture set means an imported signature; otherwise the strokes
        /// are drawn ink in CanvasWidth by CanvasHeight units.</summary>
        private sealed class SignatureDraft
        {
            public List<List<Point>> Strokes { get; init; } = [];
            public double StrokeWidth { get; init; } = 2.5;
            public BitmapSource? Picture { get; init; }
            public bool RawScan { get; init; }
            public double CanvasWidth { get; init; } = 400;
            public double CanvasHeight { get; init; } = 150;
            public double Threshold { get; init; } = SignatureInk.DefaultSensitivity;
            public double Weight { get; init; }
            public double Feather { get; init; }
            public uint? InkColor { get; init; }
        }

        /// <summary>The editor's result. ImageData is the baked look, or null for plain black strokes.</summary>
        private sealed record SignatureEdit(List<List<Point>> Strokes, double StrokeWidth, double CanvasWidth,
            double CanvasHeight, string? ImageData, double? Threshold, double Weight, double Feather, uint? InkColor);

        internal const uint SignatureBlack = 0xFF000000;
        private static readonly uint[] SignatureInkPresets = [SignatureBlack, 0xFF1F4FBF, 0xFF14286E];
        // A picture is processed at no more than this edge, which keeps a phone photo's blur in memory bounds.
        private const int SignatureWorkEdge = 2000;

        // Weight and feather are measured in 1/400ths of the signature's width (the drawing pad is 400 wide),
        // so a slider step looks the same on a drawn signature and a 3000-pixel photo.
        private static double SignaturePixelsPerUnit(int pixelWidth) => Math.Max(0.25, pixelWidth / 400.0);

        private SignatureEdit? OpenSignatureEditor(SignatureDraft draft, string title)
        {
            bool imageMode = draft.Picture is not null;
            var win = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height };
            DialogFrame.Configure(win, this);
            win.Resources["ChromeCloseCorner"] = new CornerRadius(0, UiKit.RadWindow.TopRight, 0, 0);

            var contentArea = new StackPanel();
            // White like the page the signature lands on, so the preview shows the real result.
            var previewBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = UiKit.RadControl,
                Height = 170
            };
            contentArea.Children.Add(previewBorder);

            double penWidth = Math.Clamp(draft.StrokeWidth, 1, 12);
            double threshold = draft.Threshold, weight = draft.Weight, feather = draft.Feather;
            uint? inkColor = draft.InkColor ?? (imageMode ? null : SignatureBlack);
            var strokes = draft.Strokes.Select(s => new List<Point>(s)).ToList();
            Action refresh;

            // ---- Preview -------------------------------------------------------------------------------
            Canvas? inkLayer = null;
            TextBlock? placeholder = null;
            if (imageMode)
            {
                previewBorder.Padding = new Thickness(8);
                var preview = new Image { Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
                previewBorder.Child = preview;
                // The sliders re-ink a reduced copy so a large phone photo still follows the thumb.
                var previewSource = SignatureInk.PreviewSource(draft.Picture!, 900);
                byte[] previewPixels = SignatureInk.ReadPixels(previewSource, out int pw, out int ph);
                double previewUnit = SignaturePixelsPerUnit(pw);
                refresh = () => preview.Source = SignatureInk.ToBitmap(SignatureInk.Process(previewPixels, pw, ph,
                    new SignatureLook(threshold, draft.RawScan, weight, feather, inkColor), previewUnit), pw, ph);
            }
            else
            {
                // The pad is the signature's own size inside a Viewbox, so an existing signature reopens at its
                // proportions and new strokes land in the same units.
                var canvas = new Canvas
                {
                    Width = draft.CanvasWidth, Height = draft.CanvasHeight,
                    Background = Brushes.White, ClipToBounds = true, Cursor = Cursors.Pen
                };
                previewBorder.Child = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
                var hint = new TextBlock
                {
                    Text = Loc("Str_Sig_DrawHere"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xb0, 0xb0, 0xb0)),
                    FontFamily = new FontFamily("Segoe Script, Segoe UI"),
                    FontSize = 18,
                    IsHitTestVisible = false,
                    Visibility = strokes.Count > 0 ? Visibility.Collapsed : Visibility.Visible
                };
                placeholder = hint;
                Canvas.SetLeft(hint, 18);
                Canvas.SetTop(hint, 14);
                canvas.Children.Add(hint);
                var ink = new Canvas { IsHitTestVisible = false };
                canvas.Children.Add(ink);

                Polyline MakeLine(IEnumerable<Point> points)
                {
                    var line = new Polyline
                    {
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    foreach (var p in points) line.Points.Add(p);
                    return line;
                }
                foreach (var stroke in strokes) ink.Children.Add(MakeLine(stroke));
                // A feathered look shows the same baked pixels the signature saves (at half the bake's scale), since
                // a vector blur would show a fuzzy line the feather never makes. The vector strokes show while drawing.
                var feathered = new Image { Width = draft.CanvasWidth, Height = draft.CanvasHeight, IsHitTestVisible = false, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(feathered, BitmapScalingMode.HighQuality);
                canvas.Children.Add(feathered);
                List<Point>? current = null;
                Polyline? currentLine = null;
                refresh = () =>
                {
                    var brush = new SolidColorBrush(ColorFromArgb(inkColor ?? SignatureBlack));
                    foreach (Polyline line in ink.Children)
                    {
                        line.Stroke = brush;
                        line.StrokeThickness = penWidth;
                    }
                    bool bake = feather > 0 && current is null && strokes.Count > 0;
                    feathered.Source = bake ? PreviewDrawnSignature(strokes, penWidth, draft.CanvasWidth, draft.CanvasHeight,
                        new SignatureLook(null, false, 0, feather, inkColor ?? SignatureBlack)) : null;
                    ink.Opacity = bake ? 0 : 1;
                };
                // Release and a lost capture (Alt+Tab, a dialog, a cancelled touch) end a stroke the same way: the
                // line already on the pad is kept, a tap becomes a dot, and the preview refreshes so it shows what
                // Save keeps. State is cleared before the release, which raises LostMouseCapture again.
                void EndStroke()
                {
                    if (current is null) return;
                    var finished = SignatureInk.FinishStroke(current);
                    var line = currentLine;
                    current = null;
                    currentLine = null;
                    if (finished is not null)
                    {
                        strokes.Add(finished);
                        if (line is not null && line.Points.Count < finished.Count)
                            for (int i = line.Points.Count; i < finished.Count; i++) line.Points.Add(finished[i]);
                    }
                    else if (line is not null) ink.Children.Remove(line);
                    if (canvas.IsMouseCaptured) canvas.ReleaseMouseCapture();
                    refresh();
                }
                System.Windows.Automation.AutomationProperties.SetName(canvas, Loc("Str_Sig_DrawingPad"));
                canvas.MouseLeftButtonDown += (s, e) =>
                {
                    hint.Visibility = Visibility.Collapsed;
                    var pos = e.GetPosition(canvas);
                    current = [pos];
                    currentLine = MakeLine(current);
                    ink.Children.Add(currentLine);
                    refresh();
                    canvas.CaptureMouse();
                };
                canvas.MouseMove += (s, e) =>
                {
                    if (current is null || currentLine is null) return;
                    var pos = e.GetPosition(canvas);
                    pos.X = Math.Clamp(pos.X, 0, canvas.Width);
                    pos.Y = Math.Clamp(pos.Y, 0, canvas.Height);
                    current.Add(pos);
                    currentLine.Points.Add(pos);
                };
                canvas.MouseLeftButtonUp += (s, e) => EndStroke();
                canvas.LostMouseCapture += (s, e) => EndStroke();
                inkLayer = ink;   // the Clear button below empties this layer
            }

            // ---- Controls ------------------------------------------------------------------------------
            var controls = new Grid { Margin = new Thickness(14, 6, 12, 0) };
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            TextBlock Muted(string text)
            {
                var label = new TextBlock { Text = text, FontFamily = UiKit.UiFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                return label;
            }
            void AddRow(string label, UIElement control, UIElement? readout = null)
            {
                int row = controls.RowDefinitions.Count;
                controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = Muted(label);
                Grid.SetRow(name, row);
                controls.Children.Add(name);
                if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(control, row);
                Grid.SetColumn(control, 1);
                controls.Children.Add(control);
                if (readout is not null)
                {
                    Grid.SetRow(readout, row);
                    Grid.SetColumn(readout, 2);
                    controls.Children.Add(readout);
                }
            }
            Slider AddSlider(string label, double min, double max, double step, double value, Func<double, string> format, Action<double> apply)
            {
                var slider = new Slider
                {
                    Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
                    SmallChange = step, LargeChange = step * 4, TickFrequency = step, IsSnapToTickEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)FindResource("DarkSlider")
                };
                System.Windows.Automation.AutomationProperties.SetName(slider, label);
                var readout = Muted(format(slider.Value));
                readout.Margin = new Thickness(8, 0, 0, 0);
                slider.ValueChanged += (s, e) =>
                {
                    readout.Text = format(e.NewValue);
                    apply(e.NewValue);
                    refresh();
                };
                AddRow(label, slider, readout);
                return slider;
            }

            Slider? firstSlider = null;
            if (imageMode)
                firstSlider = AddSlider(Loc("Str_Sig_Threshold"), SignatureInk.MinSensitivity, SignatureInk.MaxSensitivity, 1, threshold,
                    v => $"{v:0}", v => threshold = v);
            var penSlider = imageMode
                ? AddSlider(Loc("Str_Sig_Pen"), SignatureInk.MinWeight, SignatureInk.MaxWeight, 0.5, weight,
                    v => v > 0 ? $"+{v:0.#}" : $"{v:0.#}", v => weight = v)
                : AddSlider(Loc("Str_Sig_Pen"), 1, 12, 0.5, penWidth, v => $"{v:0.#}", v => penWidth = v);
            Slider focusTarget = firstSlider ?? penSlider;
            AddSlider(Loc("Str_Sig_Feather"), SignatureInk.MinFeather, SignatureInk.MaxFeather, 0.5, feather, v => $"{v:0.#}", v => feather = v);

            // Ink color: the original colors (pictures only), three pen inks, and the app's color picker.
            var swatchRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var swatches = new List<(Border Face, uint? Color)>();
            Border? customFace = null;
            uint? customColor = inkColor is uint c0 && !SignatureInkPresets.Contains(c0) ? c0 : null;
            void RefreshSwatches()
            {
                foreach (var (face, color) in swatches)
                    face.SetResourceReference(Border.BorderBrushProperty,
                        color == inkColor ? "PrimaryBrush" : "CardBorderBrush");
                if (customFace is not null)
                {
                    customFace.SetResourceReference(Border.BorderBrushProperty,
                        customColor is not null && customColor == inkColor ? "PrimaryBrush" : "CardBorderBrush");
                    if (customColor is uint cc) customFace.Background = new SolidColorBrush(ColorFromArgb(cc));
                }
            }
            Button Swatch(string name, Border face, Action click)
            {
                var button = new Button
                {
                    Content = face, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 0),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                    Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
                    ToolTip = name
                };
                System.Windows.Automation.AutomationProperties.SetName(button, name);
                button.Click += (s, e) => { click(); RefreshSwatches(); refresh(); };
                swatchRow.Children.Add(button);
                return button;
            }
            Border Face(Brush fill) => new()
            {
                Width = 24, Height = 20, CornerRadius = UiKit.RadControl, BorderThickness = new Thickness(2),
                Background = fill, SnapsToDevicePixels = true
            };
            if (imageMode)
            {
                // The picture's own colors: a sweep of hues, read as "as scanned" next to the solid inks.
                var original = Face(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0x20, 0x20, 0x20), 0), new(Color.FromRgb(0x1F, 0x4F, 0xBF), 0.5), new(Color.FromRgb(0xB0, 0x30, 0x30), 1)
                }, 0));
                swatches.Add((original, null));
                Swatch(Loc("Str_Sig_ColorOriginal"), original, () => inkColor = null);
            }
            string[] presetNames = [Loc("Str_Sig_ColorBlack"), Loc("Str_Sig_ColorBlue"), Loc("Str_Sig_ColorNavy")];
            for (int i = 0; i < SignatureInkPresets.Length; i++)
            {
                uint preset = SignatureInkPresets[i];
                var face = Face(new SolidColorBrush(ColorFromArgb(preset)));
                swatches.Add((face, preset));
                Swatch(presetNames[i], face, () => inkColor = preset);
            }
            customFace = Face(customColor is uint start ? new SolidColorBrush(ColorFromArgb(start)) : Brushes.Transparent);
            if (customColor is null)
                customFace.Child = new TextBlock { Text = "+", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (customFace.Child is TextBlock plus) plus.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Button? customButton = null;
            customButton = Swatch(Loc("Str_Sig_ColorCustom"), customFace, () =>
            {
                var dlg = new ColorPickerDialog(win, ColorFromArgb(inkColor ?? customColor ?? SignatureBlack));
                dlg.ShowDialog();
                // SelectedColor is applied whatever ShowDialog returns: a nested modal's fade-close reports false
                // even on OK, and on Cancel it is still the color the picker opened with (StampWindow does the same).
                var chosen = dlg.SelectedColor;
                customColor = 0xFF000000u | ((uint)chosen.R << 16) | ((uint)chosen.G << 8) | chosen.B;
                inkColor = customColor;
                customFace.Child = null;
                customButton?.Focus();
            });
            AddRow(Loc("Str_Sig_InkColor"), swatchRow);
            RefreshSwatches();
            contentArea.Children.Add(controls);

            // ---- Buttons -------------------------------------------------------------------------------
            SignatureEdit? result = null;
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 10, 12, 12) };
            if (!imageMode)
            {
                var clearBtn = UiKit.Make(Loc("Str_Sig_Clear"), accent: false);
                clearBtn.Margin = new Thickness(0, 0, 8, 0);
                clearBtn.Click += (s, e) =>
                {
                    strokes.Clear();
                    inkLayer?.Children.Clear();
                    if (placeholder is not null) placeholder.Visibility = Visibility.Visible;
                    refresh();
                };
                btnPanel.Children.Add(clearBtn);
            }
            var cancelBtn = UiKit.Make(Loc("Str_Btn_Cancel"), accent: false);
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.Click += (s, e) => win.Close();
            btnPanel.Children.Add(cancelBtn);
            var saveBtn = UiKit.Make(Loc("Str_Sig_SaveSig"), accent: true);
            saveBtn.Click += (s, e) =>
            {
                if (!imageMode && strokes.Count == 0)
                {
                    KillerDialog.Show(this, Loc("Str_Dlg_DrawSignatureFirst"), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var look = new SignatureLook(imageMode ? threshold : null, draft.RawScan, imageMode ? weight : 0, feather, inkColor);
                result = new SignatureEdit(strokes, imageMode ? draft.StrokeWidth : penWidth, draft.CanvasWidth, draft.CanvasHeight,
                    imageMode ? BakeSignaturePicture(draft.Picture!, look) : BakeDrawnSignature(strokes, penWidth, draft.CanvasWidth, draft.CanvasHeight, look),
                    imageMode ? threshold : null, look.Weight, feather, inkColor);
                win.Close();
            };
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            refresh();
            win.Content = DialogFrame.Frame(win, this, "KillerPDF - " + title, () => win.Close(), contentArea);
            // Keyboard focus starts on the first slider, a new empty pad included, so Tab and the arrows work at once.
            win.Loaded += (s, e) => focusTarget.Focus();
            win.ShowDialog();
            return result;
        }

        private static Color ColorFromArgb(uint argb) =>
            Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        /// <summary>Plain black ink with no feather stays vector strokes (null); anything else is drawn at four
        /// times the pad size and baked to a PNG.</summary>
        internal static string? BakeDrawnSignature(IReadOnlyList<List<Point>> strokes, double penWidth,
            double canvasWidth, double canvasHeight, SignatureLook look)
        {
            if ((look.InkColor ?? SignatureBlack) == SignatureBlack && look.Feather <= 0) return null;
            const double scale = 4;
            int w = Math.Max(1, (int)Math.Ceiling(canvasWidth * scale));
            int h = Math.Max(1, (int)Math.Ceiling(canvasHeight * scale));
            var points = strokes.Select(s => (IReadOnlyList<(double X, double Y)>)s.Select(p => (p.X, p.Y)).ToList()).ToList();
            byte[] pixels = SignatureInk.RasterizeStrokes(points, penWidth, w, h, scale);
            pixels = SignatureInk.Process(pixels, w, h, look with { Threshold = null, Weight = 0, InkColor = look.InkColor ?? SignatureBlack },
                SignaturePixelsPerUnit(w));
            return Convert.ToBase64String(SignatureInk.EncodePng(SignatureInk.ToBitmap(pixels, w, h)));
        }

        // The editor's feathered preview: the bake's own pipeline at half its scale, so the pad follows the slider.
        private static BitmapSource PreviewDrawnSignature(IReadOnlyList<List<Point>> strokes, double penWidth,
            double canvasWidth, double canvasHeight, SignatureLook look)
        {
            const double scale = 2;
            int w = Math.Max(1, (int)Math.Ceiling(canvasWidth * scale));
            int h = Math.Max(1, (int)Math.Ceiling(canvasHeight * scale));
            var points = strokes.Select(s => (IReadOnlyList<(double X, double Y)>)s.Select(p => (p.X, p.Y)).ToList()).ToList();
            byte[] pixels = SignatureInk.RasterizeStrokes(points, penWidth, w, h, scale);
            pixels = SignatureInk.Process(pixels, w, h, look, SignaturePixelsPerUnit(w));
            return SignatureInk.ToBitmap(pixels, w, h);
        }

        private static string BakeSignaturePicture(BitmapSource picture, SignatureLook look)
        {
            var work = SignatureInk.PreviewSource(picture, SignatureWorkEdge);
            byte[] pixels = SignatureInk.ReadPixels(work, out int w, out int h);
            pixels = SignatureInk.Process(pixels, w, h, look, SignaturePixelsPerUnit(w));
            return Convert.ToBase64String(SignatureInk.EncodePng(SignatureInk.ToBitmap(pixels, w, h)));
        }

        private static BitmapSource DecodeSignaturePng(string base64)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(base64));
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        // ---- Entry points ------------------------------------------------------------------------------

        /// <summary>SVG signatures keep their own outlines and colors, so the editor does not open on them.</summary>
        private static bool CanEditSignature(SavedSignature sig) => sig.Vectors is not { Count: > 0 };
        private static bool CanEditSignature(SignatureAnnotation sig) => sig.Vectors is not { Count: > 0 };

        private static SignatureDraft DraftFrom(SavedSignature sig)
        {
            var strokes = sig.Strokes.Select(s => s.Select(p => new Point(p.X, p.Y)).ToList()).ToList();
            // An imported signature reopens on its scan; a drawn one on its strokes, even when a color or
            // feather baked it to a picture; an older imported one on its cleaned picture.
            BitmapSource? picture = sig.SourceImageData is { } scan ? DecodeSignaturePng(scan)
                : strokes.Count == 0 && sig.ImageData is { } image ? DecodeSignaturePng(image) : null;
            return new SignatureDraft
            {
                Strokes = picture is null ? strokes : [],
                StrokeWidth = sig.StrokeWidth,
                Picture = picture,
                RawScan = sig.SourceImageData is not null,
                CanvasWidth = sig.CanvasWidth,
                CanvasHeight = sig.CanvasHeight,
                Threshold = sig.Threshold ?? SignatureInk.DefaultSensitivity,
                Weight = sig.Weight,
                Feather = sig.Feather,
                InkColor = sig.InkColor
            };
        }

        private static void ApplyEdit(SavedSignature sig, SignatureEdit edit)
        {
            sig.Strokes = [.. edit.Strokes.Select(s => s.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList())];
            sig.StrokeWidth = edit.StrokeWidth;
            sig.CanvasWidth = edit.CanvasWidth;
            sig.CanvasHeight = edit.CanvasHeight;
            sig.ImageData = edit.ImageData;
            sig.Threshold = edit.Threshold;
            sig.Weight = edit.Weight;
            sig.Feather = edit.Feather;
            sig.InkColor = edit.InkColor;
        }

        /// <summary>Create Signature: the editor on an empty pad. The new signature is saved and armed for placing.</summary>
        private void OpenSignatureCreator(SignatureKind kind = SignatureKind.Signature)
        {
            var edit = OpenSignatureEditor(new SignatureDraft(), Loc("Str_Sig_Create"));
            if (edit is null) return;
            var saved = new SavedSignature
            {
                Kind = kind,
                Name = $"{(kind == SignatureKind.Initials ? "Initials" : "Signature")} {_signatureStore.Signatures.Count(x => x.Kind == kind) + 1}"
            };
            ApplyEdit(saved, edit);
            _signatureStore.Add(saved);
            bool kept = PersistSignatures();
            _pendingSignature = saved;
            _annotationCanvas.Cursor = Cursors.Cross;
            SetStatus(Loc(kept ? "Str_St_SignatureSaved" : "Str_Err_SignatureLibrarySave"));
        }

        /// <summary>An imported scan or photo opens in the editor, which drops the paper before it is saved.
        /// The scan is kept (at most 2000 pixels on a side) so a later Edit can loosen the threshold again.</summary>
        private SavedSignature? ImportPictureSignature(BitmapSource bmp, string name, SignatureKind kind)
        {
            var scan = SignatureInk.PreviewSource(bmp, SignatureWorkEdge);
            var edit = OpenSignatureEditor(new SignatureDraft
            {
                Picture = scan, RawScan = true, CanvasWidth = bmp.PixelWidth, CanvasHeight = bmp.PixelHeight
            }, Loc("Str_Sig_AdjustImage"));
            if (edit is null) return null;
            var saved = new SavedSignature
            {
                Kind = kind,
                Name = name,
                SourceImageData = Convert.ToBase64String(SignatureInk.EncodePng(scan))
            };
            ApplyEdit(saved, edit);
            return saved;
        }

        /// <summary>The picker tile's Edit: changes the saved signature in place. Signatures already on a page
        /// are copies and keep their look; the next placement uses the edit.</summary>
        private void EditSavedSignature(SavedSignature sig)
        {
            if (!CanEditSignature(sig)) return;
            HideSignaturePopup(cancelPendingField: false);
            var edit = OpenSignatureEditor(DraftFrom(sig), Loc("Str_Sig_EditTitle"));
            if (edit is not null)
            {
                ApplyEdit(sig, edit);
                SetStatus(Loc(PersistSignatures() ? "Str_St_SignatureSaved" : "Str_Err_SignatureLibrarySave"));
            }
            ShowSignaturePopup();
        }

        /// <summary>The page card's Edit on a placed signature: changes that placement, as one undo step, at
        /// the same spot and size. A placed picture has lost its paper already, so its threshold can only tighten.</summary>
        private void EditPlacedSignature(SignatureAnnotation sig)
        {
            if (!CanEditSignature(sig)) return;
            SignatureDraft draft;
            try
            {
                draft = sig.ImageData is { } image
                    ? new SignatureDraft { Picture = DecodeSignaturePng(image), CanvasWidth = sig.SourceWidth, CanvasHeight = sig.SourceHeight, StrokeWidth = sig.StrokeWidth }
                    : new SignatureDraft
                    {
                        Strokes = [.. sig.Strokes.Select(s => new List<Point>(s))], StrokeWidth = sig.StrokeWidth,
                        CanvasWidth = sig.SourceWidth, CanvasHeight = sig.SourceHeight
                    };
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or System.IO.IOException or ArgumentException or InvalidOperationException)
            {
                KillerDialog.Show(this, Loc("Str_Err_ImportImageFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var edit = OpenSignatureEditor(draft, Loc("Str_Sig_EditTitle"));
            if (edit is null) return;
            ApplyCardStyle([sig], a =>
            {
                var placed = (SignatureAnnotation)a;
                placed.Strokes = [.. edit.Strokes.Select(s => new List<Point>(s))];
                placed.StrokeWidth = edit.StrokeWidth;
                placed.ImageData = edit.ImageData;
                placed.CachedBitmap = null;
            });
        }
    }
}
