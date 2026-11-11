using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // ============================================================
        // Text tool settings bar
        // ============================================================

        // Page points per canvas unit for a rendered page, following its on-screen turn. Text sizes
        // are shown in points and stored in canvas units, and the export converts back the same way.
        private double PointsPerCanvasUnit(int pageIndex, double renderHeight)
        {
            var session = EnsureEngineDocumentSession();
            if (pageIndex < 0 || pageIndex >= session.PageCount) return 0;
            var info = session.Pages[pageIndex];
            int rotation = _pageRotations.TryGetValue(pageIndex, out int stored) ? stored : info.Rotation;
            return RedactionGeometry.PointsPerCanvasUnit(info.Width, info.Height, renderHeight, rotation);
        }

        // Underline and/or strikethrough as one decoration collection (either, both, or none).
        private static TextDecorationCollection? BuildDecorations(bool underline, bool strike)
        {
            if (!underline && !strike) return null;
            var d = new TextDecorationCollection();
            if (underline) foreach (var x in TextDecorations.Underline) d.Add(x);
            if (strike) foreach (var x in TextDecorations.Strikethrough) d.Add(x);
            return d;
        }

        // Apply the current typeface + Bold/Italic/Underline/Strikethrough to an in-canvas edit box, so
        // editing stays WYSIWYG with the text bar. Bad font names fall back to Segoe UI rather than throwing.
        private void StyleEditBox(TextBox tb)
        {
            try { tb.FontFamily = new FontFamily(_textFontName); } catch { tb.FontFamily = UiKit.UiFont; }
            tb.FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal;
            tb.FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal;
            tb.TextDecorations = BuildDecorations(_textUnderline, _textStrike);
            ApplyLetterSpacing(tb, tb.Text, CanvasLetterSpacing(tb.Tag is int page ? page : PageList.SelectedIndex));
            tb.TextChanged -= ActiveTextBox_TextChanged;
            tb.TextChanged += ActiveTextBox_TextChanged;
        }

        private void ActiveTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
                ApplyLetterSpacing(tb, tb.Text,
                    CanvasLetterSpacing(tb.Tag is int page ? page : PageList.SelectedIndex));
        }

        private double CanvasLetterSpacing(int pageIndex)
        {
            double spacing = _textLetterSpacing;
            if (_doc is not null && pageIndex >= 0 && _renderDims.TryGetValue(pageIndex, out var rd) && rd.h > 0)
            {
                double sy = PointsPerCanvasUnit(pageIndex, rd.h);
                if (sy > 0) spacing /= sy;
            }
            return spacing;
        }

        private static void ApplyLetterSpacing(DependencyObject control, string text, double spacing)
        {
            var effects = new TextEffectCollection();
            if (Math.Abs(spacing) >= .01)
            {
                int column = 0;
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
                while (elements.MoveNext())
                {
                    string current = elements.GetTextElement();
                    int index = elements.ElementIndex;
                    if (current is "\r" or "\n") { column = 0; continue; }
                    effects.Add(new TextEffect
                    {
                        PositionStart = index,
                        PositionCount = current.Length,
                        Transform = new TranslateTransform(column * spacing, 0)
                    });
                    column++;
                }
            }
            control.SetValue(TextBlock.TextEffectsProperty, effects);
        }

        private void ApplyTextStyleToActiveBox()
        {
            if (_activeTextBox is null) return;
            _activeTextBox.Foreground = new SolidColorBrush(_textColor);
            _activeTextBox.Background = TextEditBackground();   // reflect the chosen fill live
            StyleEditBox(_activeTextBox);                       // typeface + B/I/S live
            int pg = _activeTextBox.Tag is int tp ? tp : PageList.SelectedIndex;
            double fontCanvas = _textFontSize;
            if (_doc is not null && pg >= 0 && _renderDims.TryGetValue(pg, out var rd) && rd.h > 0)
            {
                double sy = PointsPerCanvasUnit(pg, rd.h);
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            _activeTextBox.FontSize = fontCanvas;
        }

        // Applies the current text style to whatever is active: the live edit box if one is open,
        // otherwise the selected text box (so its color / fill / size can be changed after placing it).
        // Opens the full RGB color picker seeded with the current color; applies the result on OK.
        private void OpenColorPicker(Color current, Action<Color> apply, Action? refreshBar = null)
        {
            var dlg = new ColorPickerDialog(this, Color.FromRgb(current.R, current.G, current.B));
            // Live-update the annotate bar behind the (modal) dialog whenever the shared palette is edited.
            if (refreshBar is not null) dlg.SwatchesChanged += refreshBar;
            dlg.ShowDialog();
            // dlg.Accepted, NEVER ShowDialog's return: the eyedropper's nested capture modal can
            // corrupt the outer dialog frame so ShowDialog returns false after a real OK, and the
            // pick was silently dropped - shapes then drew whatever color last got through
            // (the "purple/gray rectangles" bug, trace-proven 2026-08-01).
            if (dlg.Accepted) apply(dlg.SelectedColor);
        }

        // One slider gesture, a drag or a run of key presses, is one undo step, so a slider on the options bar
        // takes its snapshot at the first change only. Each slider owns a SliderGesture made for it and passes it
        // to the apply call; a swatch pick, a toggle or a stepper passes none and is one step by itself.
        // Ctrl+Z puts back how the selected annotations looked before the change. Called before the first change
        // of a gesture; the rest of a slider gesture adds no step.
        private void PushStyleUndo(IEnumerable<PageAnnotation> annotations, Controls.SliderGesture? gesture = null)
        {
            if (gesture is not null && !gesture.Starts()) return;
            foreach (int page in annotations.Select(a => a.PageIndex).Distinct())
                ActiveViewer.PushPageSnapshotUndoExt(page);
        }

        private void ApplyTextStyleToSelection(Controls.SliderGesture? gesture = null)
        {
            if (_activeTextBox is not null)
            {
                ApplyTextStyleToActiveBox();
                return;
            }
            // Apply to the primary selection AND every shift-selected text annotation (these can span pages).
            var touched = new HashSet<int>();
            void Apply(TextAnnotation ta)
            {
                ta.SetColor(_textColor);
                ta.SetFill(_textFillColor);
                ta.FontName = _textFontName;
                ta.Bold = _textBold;
                ta.Italic = _textItalic;
                ta.Strike = _textStrike;
                ta.Underline = _textUnderline;
                ta.LetterSpacing = CanvasLetterSpacing(ta.PageIndex);
                double sy = 1.0;
                if (_doc is not null && _renderDims.TryGetValue(ta.PageIndex, out var rd) && rd.h > 0)
                    sy = PointsPerCanvasUnit(ta.PageIndex, rd.h);
                if (sy > 0 && _textFontSize > 0) ta.FontSize = _textFontSize / sy;
                touched.Add(ta.PageIndex);
            }
            var targets = new List<TextAnnotation>();
            if (_selectedAnnotation is TextAnnotation primary) targets.Add(primary);
            foreach (var a in _selectedSet)
                if (a is TextAnnotation ta && !ReferenceEquals(ta, _selectedAnnotation)) targets.Add(ta);
            if (targets.Count == 0) return;
            PushStyleUndo(targets, gesture);
            foreach (var target in targets) Apply(target);
            if (touched.Count == 0) return;

            MarkDirty();
            int primPage = (_selectedAnnotation as TextAnnotation)?.PageIndex ?? -1;
            foreach (int p in touched) if (p != primPage) RenderAllAnnotations(p);
            if (primPage >= 0)
            {
                RenderAllAnnotations(primPage);   // render primary's page last so its chrome lands on _activeCanvas
                if (_selectionBorder is not null) _activeCanvas.Children.Add(_selectionBorder);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
            }
        }

        // Draw-bar counterpart to ApplyTextStyleToSelection: when a highlight / line / ink annotation
        // is selected (not just being freshly drawn), push the bar's current color, opacity and width
        // onto it and repaint - so editing an existing annotation works the same as setting up a new one.
        private void ApplyDrawStyleToSelection(Controls.SliderGesture? gesture = null)
        {
            // Apply to the primary selection AND every shift-selected highlight / line / ink annotation.
            var touched = new HashSet<int>();
            void Apply(PageAnnotation a)
            {
                if (a is HighlightAnnotation ha)
                {
                    ha.SetColor(ha.Style == HighlightStyle.Fill ? _highlightColor : _lineAnnotColor);
                    touched.Add(ha.PageIndex);
                }
                else if (a is InkAnnotation ia)
                {
                    ia.SetColor(_drawColor);
                    ia.StrokeWidth = _drawWidth;
                    touched.Add(ia.PageIndex);
                }
                else if (a is MeasurementAnnotation ma)
                {
                    ma.SetColor(Color.FromArgb(_drawOpacity, _drawColor.R, _drawColor.G, _drawColor.B));
                    ma.StrokeWidth = _drawWidth;
                    touched.Add(ma.PageIndex);
                }
            }
            var targets = new List<PageAnnotation>();
            if (_selectedAnnotation is not null) targets.Add(_selectedAnnotation);
            foreach (var a in _selectedSet)
                if (!ReferenceEquals(a, _selectedAnnotation)) targets.Add(a);
            if (targets.Any(a => a is HighlightAnnotation or InkAnnotation or MeasurementAnnotation))
                PushStyleUndo(targets.Where(a => a is HighlightAnnotation or InkAnnotation or MeasurementAnnotation), gesture);
            foreach (var target in targets) Apply(target);
            if (touched.Count == 0) return;

            MarkDirty();
            int primPage = _selectedAnnotation?.PageIndex ?? -1;
            foreach (int p in touched) if (p != primPage) RenderAllAnnotations(p);
            if (primPage >= 0)
            {
                RenderAllAnnotations(primPage);
                ReattachSelectionVisuals();
            }
        }

        private void ShowTextSettings()
        {
            var bar = NewToolBar(EditTool.Text);

            // Font: the typeface (fixed 140 with an ellipsis and its name as the tooltip, since font
            // names vary without limit), then Bold, Italic, Strikethrough and Underline toggles whose
            // glyphs preview their own effect.
            var fontBox = new ComboBox
            {
                Width = 140, Height = Controls.ToolOptionsControls.ControlHeight,
                VerticalAlignment = VerticalAlignment.Center, FontFamily = UiKit.UiFont, FontSize = 11,
                MaxDropDownHeight = 320, Margin = new Thickness(0, 0, 4, 0), ToolTip = _textFontName
            };
            if (FindResource("DarkComboBox") is Style cbStyle) fontBox.Style = cbStyle;
            if (FindResource("DarkComboItem") is Style ciStyle) fontBox.ItemContainerStyle = ciStyle;
            fontBox.Height = Controls.ToolOptionsControls.ControlHeight;
            System.Windows.Automation.AutomationProperties.SetName(fontBox, LabelName(Loc("Str_Bar_Font")));
            System.Windows.Automation.AutomationProperties.SetAutomationId(fontBox, "TextFont");
            foreach (var fn in SystemFontNames) fontBox.Items.Add(fn);
            fontBox.SelectedItem = _textFontName;
            fontBox.SelectionChanged += (s, e) =>
            {
                if (fontBox.SelectedItem is string fn) { _textFontName = fn; fontBox.ToolTip = fn; ApplyTextStyleToSelection(); }
            };
            bar.Add(Controls.ToolOptionGroup.Font, BarGroup(BarLabel(Loc("Str_Bar_Font")), fontBox));

            FrameworkElement StyleToggle(string glyph, string tipKey, string id, bool active, FontWeight fw, FontStyle fs,
                TextDecorationCollection? deco, Action onClick)
            {
                var text = new TextBlock
                {
                    Text = glyph, FontFamily = UiKit.UiFont, FontSize = 12, FontWeight = fw, FontStyle = fs,
                    TextDecorations = deco, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                var toggle = Controls.ToolOptionsControls.IconToggle(text, active, Loc(tipKey), () => { onClick(); ShowTextSettings(); });
                System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, id);
                return toggle;
            }
            bar.Add(Controls.ToolOptionGroup.Style, BarGroup(
                StyleToggle("B", "Str_Lbl_Bold", "TextBold", _textBold, FontWeights.Bold, FontStyles.Normal, null,
                    () => { _textBold = !_textBold; ApplyTextStyleToSelection(); }),
                StyleToggle("I", "Str_Lbl_Italic", "TextItalic", _textItalic, FontWeights.Normal, FontStyles.Italic, null,
                    () => { _textItalic = !_textItalic; ApplyTextStyleToSelection(); }),
                StyleToggle("S", "Str_Lbl_Strike", "TextStrike", _textStrike, FontWeights.Normal, FontStyles.Normal, TextDecorations.Strikethrough,
                    () => { _textStrike = !_textStrike; ApplyTextStyleToSelection(); }),
                StyleToggle("U", "Str_Lbl_Underline", "TextUnderline", _textUnderline, FontWeights.Normal, FontStyles.Normal, TextDecorations.Underline,
                    () => { _textUnderline = !_textUnderline; ApplyTextStyleToSelection(); })));

            // Size: slider, minus and plus steppers and an exact 40-wide box, in points.
            var sizeSlider = BarSlider(8, 72, Math.Max(8, Math.Min(72, _textFontSize)), LabelName(Loc("Str_Bar_Size")));
            sizeSlider.TickFrequency = 1;
            sizeSlider.IsSnapToTickEnabled = true;
            // Editable size box (type an exact value; the slider stays for quick coarse adjustment).
            var sizeBox = new TextBox
            {
                Text = $"{_textFontSize:0.##}",
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                Width = 40,
                Height = Controls.ToolOptionsControls.ControlHeight,
                MaxLength = 4,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1),
                Template = FlatTextBoxTemplate()
            };
            System.Windows.Automation.AutomationProperties.SetName(sizeBox, LabelName(Loc("Str_Bar_Size")) + " (pt)");
            System.Windows.Automation.AutomationProperties.SetAutomationId(sizeBox, "TextSizeBox");
            sizeBox.SetResourceReference(TextBox.BackgroundProperty, "TextFieldBrush");
            sizeBox.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            sizeBox.SetResourceReference(TextBox.BorderBrushProperty, "CardBorderBrush");
            sizeBox.SetResourceReference(TextBox.CaretBrushProperty, "PrimaryBrush");
            sizeBox.SetResourceReference(TextBox.SelectionBrushProperty, "RowSelectedBrush");   // no WPF-default blue
            var ptLabel = new TextBlock
            {
                Text = "pt",
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };
            ptLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            // Slider drives the box; the box drives the slider. _suppressSizeSync breaks the feedback loop.
            var sizeGesture = new Controls.SliderGesture(sizeSlider);
            sizeSlider.ValueChanged += (s, e) =>
            {
                if (_suppressSizeSync) return;
                _textFontSize = e.NewValue;
                sizeBox.Text = $"{e.NewValue:0.##}";
                ApplyTextStyleToSelection(sizeGesture);
            };
            void CommitSizeBox()
            {
                if (double.TryParse(sizeBox.Text, out double v) && double.IsFinite(v))
                {
                    _textFontSize = Math.Clamp(v, 1, 400);
                    _suppressSizeSync = true;
                    sizeSlider.Value = Math.Max(8, Math.Min(72, _textFontSize));   // thumb clamps; box keeps exact
                    _suppressSizeSync = false;
                    ApplyTextStyleToSelection();
                }
                sizeBox.Text = $"{_textFontSize:0.##}";   // normalize / revert invalid input
            }
            // Set an exact size and keep the slider + box in step (slider thumb clamps to 8-72).
            void SetSize(double v)
            {
                _textFontSize = Math.Clamp(v, 1, 400);
                _suppressSizeSync = true;
                sizeSlider.Value = Math.Max(8, Math.Min(72, _textFontSize));
                _suppressSizeSync = false;
                sizeBox.Text = $"{_textFontSize:0.##}";
                ApplyTextStyleToSelection();
            }
            // Stepper buttons (minus / plus) for one-point nudges next to the slider.
            FrameworkElement StepButton(string glyph, string name, string id, Action onClick)
            {
                var step = Controls.ToolOptionsControls.Step(glyph, name, onClick);
                step.Margin = new Thickness(3, 0, 0, 0);
                System.Windows.Automation.AutomationProperties.SetAutomationId(step, id);
                return step;
            }
            sizeBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { CommitSizeBox(); e.Handled = true; }
                else if (e.Key == Key.Escape) { sizeBox.Text = $"{_textFontSize:0.##}"; e.Handled = true; }
            };
            sizeBox.LostFocus += (s, e) => CommitSizeBox();
            sizeBox.GotFocus += (s, e) => sizeBox.SelectAll();
            bar.Add(Controls.ToolOptionGroup.Size, BarGroup(BarLabel(Loc("Str_Bar_Size")), sizeSlider,
                StepButton("−", Loc("Str_Bar_SizeDown"), "TextSizeDown", () => SetSize(_textFontSize - 1)),
                StepButton("+", Loc("Str_Bar_SizeUp"), "TextSizeUp", () => SetSize(_textFontSize + 1)),
                sizeBox, ptLabel));

            // Color, then Fill with its No fill chip first. A swatch press rebuilds the bar so the
            // new current color shows checked; the dock gives the keyboard focus back to it.
            void PickText(Color c)
            {
                _textColor = Color.FromArgb(_textOpacity, c.R, c.G, c.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            }
            var (grpColor, colorChip) = BuildColorGroup("Text", _textColor, true, PickText,
                () => OpenColorPicker(_textColor, PickText, () => ShowTextSettings()));
            bar.Add(Controls.ToolOptionGroup.Color, grpColor, colorChip);

            void PickFill(Color c)
            {
                byte a = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;   // enable at full/current opacity
                _textFillColor = Color.FromArgb(a, c.R, c.G, c.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            }
            bool noFill = _textFillColor.A == 0;
            var noneChip = Controls.ToolOptionsControls.NoFill(noFill, Loc("Str_Bar_NoFill"), () =>
                {
                    _textFillColor = Color.FromArgb(0, _textFillColor.R, _textFillColor.G, _textFillColor.B);
                    ApplyTextStyleToSelection();
                    ShowTextSettings();
                });
            System.Windows.Automation.AutomationProperties.SetAutomationId(noneChip, "FillNone");
            var fillShown = noFill ? Colors.White : _textFillColor;
            var (grpFill, _) = BuildColorGroup("Fill", fillShown, !noFill, PickFill,
                () => OpenColorPicker(fillShown, PickFill, () => ShowTextSettings()), noneChip);

            // Opacity and Fill Opacity: two independent groups.
            var opacitySlider = BarSlider(10, 255, _textOpacity, LabelName(Loc("Str_Bar_Opacity")));
            var opacityLabel = BarReadout($"{(int)(_textOpacity / 255.0 * 100)}%");
            var opacityGesture = new Controls.SliderGesture(opacitySlider);
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                _textOpacity = a;
                _textColor = Color.FromArgb(a, _textColor.R, _textColor.G, _textColor.B);
                ApplyTextStyleToSelection(opacityGesture);
            };

            byte curFillA = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;
            var fillOpSlider = BarSlider(10, 255, curFillA, LabelName(Loc("Str_Bar_FillOpacity")));
            var fillOpLabel = BarReadout($"{(int)(curFillA / 255.0 * 100)}%");
            var fillOpGesture = new Controls.SliderGesture(fillOpSlider);
            fillOpSlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                fillOpLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                // Dragging opacity turns the fill on (defaults to the current color, white for whiteout).
                _textFillColor = Color.FromArgb(a, _textFillColor.R, _textFillColor.G, _textFillColor.B);
                ApplyTextStyleToSelection(fillOpGesture);
            };

            var spacingSlider = BarSlider(-2, 20, _textLetterSpacing, LabelName(Loc("Str_Bar_LetterSpacing")));
            spacingSlider.TickFrequency = .25;
            spacingSlider.IsSnapToTickEnabled = true;
            var spacingLabel = BarReadout($"{_textLetterSpacing:0.##} pt", 48);
            var spacingGesture = new Controls.SliderGesture(spacingSlider);
            spacingSlider.ValueChanged += (_, e) =>
            {
                _textLetterSpacing = Math.Round(e.NewValue, 2);
                spacingLabel.Text = $"{_textLetterSpacing:0.##} pt";
                ApplyTextStyleToSelection(spacingGesture);
            };

            bar.Add(Controls.ToolOptionGroup.Fill, grpFill);
            bar.Add(Controls.ToolOptionGroup.Opacity, BarGroup(BarLabel(Loc("Str_Bar_Opacity")), opacitySlider, opacityLabel));
            bar.Add(Controls.ToolOptionGroup.FillOpacity, BarGroup(BarLabel(Loc("Str_Bar_FillOpacity")), fillOpSlider, fillOpLabel));
            bar.Add(Controls.ToolOptionGroup.Spacing, BarGroup(BarLabel(Loc("Str_Bar_LetterSpacing")), spacingSlider, spacingLabel));

            if (_selectedAnnotation is TextAnnotation { PairId.Length: > 0 } replacement)
            {
                double BlurUnit()
                {
                    if (_doc is not null && _renderDims.TryGetValue(replacement.PageIndex, out var dimensions)
                        && dimensions.h > 0)
                    {
                        double unit = PointsPerCanvasUnit(replacement.PageIndex, dimensions.h);
                        if (unit > 0) return unit;
                    }
                    return 1;
                }
                var blurSlider = BarSlider(0, 2, Math.Clamp(replacement.BlurRadius * BlurUnit(), 0, 2), Loc("Str_Bar_Blur"));
                blurSlider.Name = "ReplacementTextBlurSlider";
                blurSlider.TickFrequency = .05;
                blurSlider.IsSnapToTickEnabled = true;
                var blurLabel = BarReadout($"{blurSlider.Value:0.##} pt", 48);
                // One undo step per drag or key press, not one per tick.
                var blurGesture = new Controls.SliderGesture(blurSlider);
                blurSlider.ValueChanged += (_, e) =>
                {
                    if (!ReferenceEquals(_selectedAnnotation, replacement)) return;
                    double radius = Math.Round(e.NewValue, 2) / BlurUnit();
                    if (replacement.BlurRadius == radius) return;
                    if (blurGesture.Starts()) ActiveViewer.PushPageSnapshotUndoExt(replacement.PageIndex);
                    replacement.BlurRadius = radius;
                    blurLabel.Text = $"{e.NewValue:0.##} pt";
                    MarkDirty();
                    RenderAllAnnotations(replacement.PageIndex);
                    if (_selectionBorder is not null) _activeCanvas.Children.Add(_selectionBorder);
                    foreach (var handle in _resizeHandles) _activeCanvas.Children.Add(handle);
                };
                bar.Add(Controls.ToolOptionGroup.Blur, BarGroup(BarLabel(Loc("Str_Bar_Blur")), blurSlider, blurLabel));
            }

            var paragraphButton = UiKit.Make(Loc("Str_Paragraph_Title"), false);
            paragraphButton.Height = Controls.ToolOptionsControls.ControlHeight;
            paragraphButton.Padding = new Thickness(10, 0, 10, 0);
            paragraphButton.VerticalAlignment = VerticalAlignment.Center;
            paragraphButton.ToolTip = ShortcutTip("Str_Paragraph_Title", "AltShift:P");
            System.Windows.Automation.AutomationProperties.SetAutomationId(paragraphButton, "TextEditParagraph");
            paragraphButton.Click += (_, _) => EditParagraph(PageList.SelectedIndex);
            bar.Add(Controls.ToolOptionGroup.Paragraph, paragraphButton);

            // The replacement layer grid is three rows tall, so it always lives in the "..." menu.
            if (BuildReplacementLayerControls() is { } replacementLayers)
                bar.Add(Controls.ToolOptionGroup.Layers, replacementLayers, overflowOnly: true);

            DockToolBar(bar);
            _textSettingsBar = bar;
            _annotBarTool = EditTool.Text;
        }

        private void HideTextSettings()
        {
            UndockToolBar(_textSettingsBar);
            _textSettingsBar = null;
            if (_annotBarTool == EditTool.Text) _annotBarTool = null;
        }

        private void PlaceImageFromDialog(Point pos, int pageIdx)
        {
            var dlg = new Controls.ImageAssetPickerDialog(this);
            if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.SelectedPath)) return;

            try
            {
                var imgBytes = File.ReadAllBytes(dlg.SelectedPath);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(imgBytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();

                double srcW = bmp.PixelWidth > 0 ? bmp.PixelWidth : 400;
                double srcH = bmp.PixelHeight > 0 ? bmp.PixelHeight : 300;

                // Default the placed image to ~50% of the page's longest side (in render-dim
                // units) so it is a usable size regardless of page dimensions, never upscaling
                // beyond the source's native resolution.
                double pageMax = _renderDims.TryGetValue(pageIdx, out var rdImg)
                    ? Math.Max(rdImg.w, rdImg.h) : 2048.0;
                double MaxCanvasDim = pageMax * 0.5;
                double scale = Math.Min(1.0, Math.Min(MaxCanvasDim / srcW, MaxCanvasDim / srcH));

                var imgAnnot = new ImageAnnotation
                {
                    PageIndex = pageIdx,
                    Position = pos,
                    Scale = scale,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    ImageData = Convert.ToBase64String(imgBytes)
                };

                // Switch to Select FIRST so placement renders last and nothing wipes the image
                // (calling SetTool between render and select was what made the image vanish).
                SetTool(EditTool.Select);
                AddAnnotation(imgAnnot);
                RenderAllAnnotations(pageIdx);
                double w = srcW * scale;
                double h = srcH * scale;
                SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                SetStatus(Loc("Str_St_ImagePlaced"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_LoadImageFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Ctrl+V: drop a clipboard image (as an image annotation) or clipboard text (as a text
        // annotation) onto the current page, centered, then select it. Coordinates are in the page's
        // render-dim space (== _renderDims[page]), matching how clicks place annotations.
        private void PasteFromClipboard()
        {
            if (_doc is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) pageIdx = 0;
            if (pageIdx >= EnsureEngineDocumentSession().PageCount) return;

            double pw = _renderDims.TryGetValue(pageIdx, out var rd) ? rd.w : 2048.0;
            double ph = _renderDims.TryGetValue(pageIdx, out var rd2) ? rd2.h : 2048.0;

            try
            {
                if (Clipboard.ContainsImage())
                {
                    var src = Clipboard.GetImage();
                    if (src is null) { SetStatus(Loc("Str_St_ClipImageUnreadable")); return; }

                    byte[] imgBytes = BitmapHelpers.EncodeClipboardImagePng(src);

                    double srcW = src.PixelWidth > 0 ? src.PixelWidth : 400;
                    double srcH = src.PixelHeight > 0 ? src.PixelHeight : 300;
                    double pageMax = Math.Max(pw, ph);
                    double maxCanvasDim = pageMax * 0.5;
                    double scale = Math.Min(1.0, Math.Min(maxCanvasDim / srcW, maxCanvasDim / srcH));
                    double w = srcW * scale, h = srcH * scale;
                    var pos = new Point((pw - w) / 2, (ph - h) / 2);

                    var imgAnnot = new ImageAnnotation
                    {
                        PageIndex = pageIdx,
                        Position = pos,
                        Scale = scale,
                        SourceWidth = srcW,
                        SourceHeight = srcH,
                        ImageData = Convert.ToBase64String(imgBytes)
                    };
                    SetTool(EditTool.Select);
                    AddAnnotation(imgAnnot);
                    RenderAllAnnotations(pageIdx);
                    SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                    SetStatus(Loc("Str_St_PastedImage"));
                }
                else if (Clipboard.ContainsText())
                {
                    string content = Clipboard.GetText().Trim();
                    if (string.IsNullOrEmpty(content)) { SetStatus(Loc("Str_St_ClipNoText")); return; }

                    // Convert the point size to the page's canvas units (see PlaceTextBox).
                    double fontCanvas = _textFontSize;
                    double sy = PointsPerCanvasUnit(pageIdx, Math.Max(1.0, ph));
                    if (sy > 0) fontCanvas = _textFontSize / sy;

                    var ta = new TextAnnotation
                    {
                        PageIndex = pageIdx,
                        Position = new Point(pw * 0.25, ph * 0.45),
                        Content = content,
                        FontSize = fontCanvas,
                        LetterSpacing = CanvasLetterSpacing(pageIdx)
                    };
                    ta.SetColor(_textColor);
                    SetTool(EditTool.Select);
                    AddAnnotation(ta);
                    RenderAllAnnotations(pageIdx);
                    SelectAnnotation(ta, AnnotBounds(ta));
                    SetStatus(Loc("Str_St_PastedText"));
                }
                else
                {
                    SetStatus(Loc("Str_St_ClipEmpty"));
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_PasteFailed") + "\n" + ex.Message, "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}
