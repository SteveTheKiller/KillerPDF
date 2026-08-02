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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Text tool settings bar
        // ============================================================

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
                double sy = _doc.Pages[pg].Height.Point / rd.h;
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

        // Diagonal rainbow fill for the "more colors" swatches that open the picker.
        private static LinearGradientBrush RainbowBrush() => new()
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.25),
                new GradientStop(Colors.Lime, 0.5), new GradientStop(Colors.Cyan, 0.7),
                new GradientStop(Colors.Blue, 1)
            }
        };

        private void ApplyTextStyleToSelection()
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
                double sy = 1.0;
                if (_doc is not null && _renderDims.TryGetValue(ta.PageIndex, out var rd) && rd.h > 0)
                    sy = _doc.Pages[ta.PageIndex].Height.Point / rd.h;
                if (sy > 0 && _textFontSize > 0) ta.FontSize = _textFontSize / sy;
                touched.Add(ta.PageIndex);
            }
            if (_selectedAnnotation is TextAnnotation primary) Apply(primary);
            foreach (var a in _selectedSet)
                if (a is TextAnnotation ta && !ReferenceEquals(ta, _selectedAnnotation)) Apply(ta);
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
        private void ApplyDrawStyleToSelection()
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
            }
            if (_selectedAnnotation is not null) Apply(_selectedAnnotation);
            foreach (var a in _selectedSet)
                if (!ReferenceEquals(a, _selectedAnnotation)) Apply(a);
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
            bool appearing = _annotBarTool != EditTool.Text;   // real appear/switch vs same-tool refresh
            if (_textSettingsBar is not null)
            {
                if (appearing) FadeOutAndRemoveBar(_textSettingsBar);
                else (PagePreviewPanel.Parent as Grid)?.Children.Remove(_textSettingsBar);
                _textSettingsBar = null;
            }

            // Three self-contained 2-row blocks (Size/Font, Color/Fill, Opacity/Fill-Opacity) laid out in a
            // WrapPanel (built at the end) so they sit on one line when wide and drop to extra rows when the
            // window is too narrow - a wrap never splits a paired row, since each pair lives inside one block.
            Grid Block(int cols)
            {
                var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
                for (int ci = 0; ci < cols; ci++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                return g;
            }
            var gLeft = Block(1);
            var gPalette = Block(2);
            var gOpacity = Block(3);
            // Right margin = the gap to the next block; top/bottom = the gap when blocks wrap to new rows.
            gLeft.Margin = new Thickness(0, 2, 16, 2);
            gPalette.Margin = new Thickness(0, 2, 16, 2);
            gOpacity.Margin = new Thickness(0, 2, 0, 2);
            void Place(Grid g, UIElement el, int r, int col, int span = 1)
            {
                Grid.SetRow(el, r);
                Grid.SetColumn(el, col);
                if (span > 1) Grid.SetColumnSpan(el, span);
                g.Children.Add(el);
            }
            TextBlock DimLabel(string text, int top, bool rightAlign = false)
            {
                var t = new TextBlock
                {
                    Text = text,
                    FontFamily = UiKit.UiFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = rightAlign ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Margin = new Thickness(0, top, 6, 0)
                };
                t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                return t;
            }
            Border ColorSwatch(Color c, bool isActive, MouseButtonEventHandler onClick)
            {
                var sw = new Border
                {
                    Width = 18,
                    Height = 18,
                    Background = new SolidColorBrush(c),
                    BorderThickness = new Thickness(isActive ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                if (isActive) sw.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
                else sw.BorderBrush = _swatchDimBorder;
                sw.MouseLeftButtonDown += onClick;
                return sw;
            }
            // Opens the full RGB picker. When the current color isn't one of the presets it shows that
            // color with an accent ring (and a small rainbow corner), so the bar reflects a custom pick.
            Grid MoreColorsSwatch(Color current, bool customActive, MouseButtonEventHandler onClick)
            {
                var grid = new Grid { Width = 18, Height = 18, Margin = new Thickness(1), Cursor = Cursors.Hand, ToolTip = Loc("Str_Bar_MoreColors") };
                var bg = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(customActive ? 2 : 1),
                    Background = customActive
                        ? (Brush)new SolidColorBrush(Color.FromRgb(current.R, current.G, current.B))
                        : RainbowBrush()
                };
                if (customActive) bg.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush"); else bg.BorderBrush = _swatchDimBorder;
                grid.Children.Add(bg);
                if (customActive)
                    grid.Children.Add(new System.Windows.Shapes.Polygon
                    {
                        Points = [new Point(18, 7), new Point(18, 18), new Point(7, 18)],
                        Fill = RainbowBrush(),
                        IsHitTestVisible = false
                    });
                grid.MouseLeftButtonDown += onClick;
                return grid;
            }

            // Drag grip (col 0), spanning both rows and centred vertically (not pinned to the top row).
            // 4 dots since this bar is double height.
            var textGrip = MakeBarGrip(4);
            textGrip.VerticalAlignment = VerticalAlignment.Center;
            // (grip is added to the WrapPanel host directly, below.)

            // Color / Fill labels live in the palette block (col 0), with the swatch rows in col 1.
            Place(gPalette, DimLabel(Loc("Str_Bar_Color"), 0), 0, 0);
            Place(gPalette, DimLabel(Loc("Str_Bar_Fill"), 4), 1, 0);

            // Swatch rows, col 2. Row 0 gets a leading spacer the size of the "None" tile so the
            // color swatches sit directly above the fill swatches.
            var swatchRow1 = new StackPanel { Orientation = Orientation.Horizontal };
            swatchRow1.Children.Add(new Border { Width = 18, Height = 18, Margin = new Thickness(1), Background = Brushes.Transparent });
            foreach (var color in SwatchColors)
            {
                var c = color;
                bool isActive = c.R == _textColor.R && c.G == _textColor.G && c.B == _textColor.B;
                swatchRow1.Children.Add(ColorSwatch(c, isActive, (_, _) =>
                {
                    _textColor = Color.FromArgb(_textOpacity, c.R, c.G, c.B);
                    ApplyTextStyleToSelection();
                    ShowTextSettings();
                }));
            }
            bool textCustom = !SwatchColors.Any(sc => sc.R == _textColor.R && sc.G == _textColor.G && sc.B == _textColor.B);
            swatchRow1.Children.Add(MoreColorsSwatch(_textColor, textCustom, (_, _) => OpenColorPicker(_textColor, c =>
            {
                _textColor = Color.FromArgb(_textOpacity, c.R, c.G, c.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            }, () => ShowTextSettings())));
            Place(gPalette, swatchRow1, 0, 1);

            var swatchRow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            bool noneActive = _textFillColor.A == 0;
            var noneGrid = new Grid { Width = 18, Height = 18, Margin = new Thickness(1), Cursor = Cursors.Hand };
            var noneBg = new Border { CornerRadius = new CornerRadius(3), Background = Brushes.White, BorderThickness = new Thickness(noneActive ? 2 : 1) };
            if (noneActive) noneBg.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush"); else noneBg.BorderBrush = _swatchDimBorder;
            noneGrid.Children.Add(noneBg);
            noneGrid.Children.Add(new System.Windows.Shapes.Line { X1 = 3, Y1 = 15, X2 = 15, Y2 = 3, Stroke = Brushes.Red, StrokeThickness = 1.5 });
            noneGrid.MouseLeftButtonDown += (_, _) =>
            {
                _textFillColor = Color.FromArgb(0, _textFillColor.R, _textFillColor.G, _textFillColor.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            };
            swatchRow2.Children.Add(noneGrid);
            foreach (var color in SwatchColors)
            {
                var c = color;
                bool isActive = _textFillColor.A > 0 && c.R == _textFillColor.R && c.G == _textFillColor.G && c.B == _textFillColor.B;
                swatchRow2.Children.Add(ColorSwatch(c, isActive, (_, _) =>
                {
                    byte a = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;   // enable at full/current opacity
                    _textFillColor = Color.FromArgb(a, c.R, c.G, c.B);
                    ApplyTextStyleToSelection();
                    ShowTextSettings();
                }));
            }
            bool fillCustom = _textFillColor.A > 0 && !SwatchColors.Any(sc => sc.R == _textFillColor.R && sc.G == _textFillColor.G && sc.B == _textFillColor.B);
            swatchRow2.Children.Add(MoreColorsSwatch(_textFillColor.A == 0 ? Colors.White : _textFillColor, fillCustom, (_, _) => OpenColorPicker(_textFillColor.A == 0 ? Colors.White : _textFillColor, c =>
            {
                byte a = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;
                _textFillColor = Color.FromArgb(a, c.R, c.G, c.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            }, () => ShowTextSettings())));
            // Faint divider + a little breathing room between the Color row and the Fill row.
            var fillWrap = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                Margin = new Thickness(2, 5, 8, 0),
                Padding = new Thickness(0, 5, 0, 0),
                Child = swatchRow2
            };
            Place(gPalette, fillWrap, 1, 1);

            // Size group: bottom row of the left block, directly below the font / style row.
            var sizeStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            sizeStack.Children.Add(DimLabel(Loc("Str_Bar_Size"), 0));
            var sizeSlider = new Slider
            {
                Minimum = 8,
                Maximum = 72,
                Value = Math.Max(8, Math.Min(72, _textFontSize)),
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Style = (Style)FindResource("DarkSlider")
            };
            // Editable size box (type an exact value; the slider stays for quick coarse adjustment).
            var sizeBox = new TextBox
            {
                Text = $"{_textFontSize:F0}",
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                Width = 32,
                MaxLength = 4,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1),
                Template = FlatTextBoxTemplate()
            };
            sizeBox.SetResourceReference(TextBox.BackgroundProperty, "PaneBrush");
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
            sizeSlider.ValueChanged += (s, e) =>
            {
                if (_suppressSizeSync) return;
                _textFontSize = e.NewValue;
                sizeBox.Text = $"{e.NewValue:F0}";
                ApplyTextStyleToSelection();
            };
            void CommitSizeBox()
            {
                if (double.TryParse(sizeBox.Text, out double v))
                {
                    _textFontSize = Math.Max(1, Math.Min(400, Math.Round(v)));
                    _suppressSizeSync = true;
                    sizeSlider.Value = Math.Max(8, Math.Min(72, _textFontSize));   // thumb clamps; box keeps exact
                    _suppressSizeSync = false;
                    ApplyTextStyleToSelection();
                }
                sizeBox.Text = $"{_textFontSize:F0}";   // normalise / revert invalid input
            }
            // Set an exact size and keep the slider + box in step (slider thumb clamps to 8-72).
            void SetSize(double v)
            {
                _textFontSize = Math.Max(1, Math.Min(400, Math.Round(v)));
                _suppressSizeSync = true;
                sizeSlider.Value = Math.Max(8, Math.Min(72, _textFontSize));
                _suppressSizeSync = false;
                sizeBox.Text = $"{_textFontSize:F0}";
                ApplyTextStyleToSelection();
            }
            // Tiny stepper button (− / +) for one-point nudges next to the slider.
            Border StepButton(string glyph, Action onClick)
            {
                var st = new TextBlock
                {
                    Text = glyph,
                    FontFamily = UiKit.UiFont,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                st.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                var sb = new Border
                {
                    Width = 18,
                    Height = 20,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(3, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(1),
                    Background = Brushes.Transparent,
                    Child = st,
                    BorderBrush = _swatchDimBorder
                };
                sb.MouseLeftButtonDown += (_, _) => onClick();
                return sb;
            }
            sizeBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { CommitSizeBox(); e.Handled = true; }
                else if (e.Key == Key.Escape) { sizeBox.Text = $"{_textFontSize:F0}"; e.Handled = true; }
            };
            sizeBox.LostFocus += (s, e) => CommitSizeBox();
            sizeBox.GotFocus += (s, e) => sizeBox.SelectAll();
            sizeStack.Children.Add(sizeSlider);
            sizeStack.Children.Add(StepButton("−", () => SetSize(_textFontSize - 1)));   // minus
            sizeStack.Children.Add(StepButton("+", () => SetSize(_textFontSize + 1)));
            sizeStack.Children.Add(sizeBox);
            sizeStack.Children.Add(ptLabel);
            Place(gLeft, sizeStack, 1, 0);

            // Fill-row middle (under the Size group): typeface selector + Bold / Italic / Strikethrough.
            // A small square toggle whose glyph previews its own effect (bold B, italic I, struck-through S).
            Border StyleToggle(string glyph, string tip, bool active, FontWeight fw, FontStyle fs, TextDecorationCollection? deco, Action onClick)
            {
                var gt = new TextBlock
                {
                    Text = glyph,
                    FontFamily = UiKit.UiFont,
                    FontSize = 12,
                    FontWeight = fw,
                    FontStyle = fs,
                    TextDecorations = deco,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                gt.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                var b = new Border
                {
                    Width = 22,
                    Height = 20,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = tip,
                    BorderThickness = new Thickness(active ? 2 : 1),
                    Background = active ? AccentBrush(40) : Brushes.Transparent,
                    Child = gt
                };
                if (active) b.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush"); else b.BorderBrush = _swatchDimBorder;
                b.MouseLeftButtonDown += (_, _) => onClick();
                return b;
            }

            var fontStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            fontStack.Children.Add(DimLabel(Loc("Str_Bar_Font"), 0));
            var fontBox = new ComboBox
            {
                Width = 132,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                MaxDropDownHeight = 320,
                Margin = new Thickness(0, 0, 4, 0)
            };
            if (FindResource("DarkComboBox") is Style cbStyle) fontBox.Style = cbStyle;
            if (FindResource("DarkComboItem") is Style ciStyle) fontBox.ItemContainerStyle = ciStyle;
            foreach (var fn in SystemFontNames) fontBox.Items.Add(fn);
            fontBox.SelectedItem = _textFontName;
            fontBox.SelectionChanged += (s, e) =>
            {
                if (fontBox.SelectedItem is string fn) { _textFontName = fn; ApplyTextStyleToSelection(); }
            };
            fontStack.Children.Add(fontBox);
            fontStack.Children.Add(StyleToggle("B", Loc("Str_Lbl_Bold"), _textBold, FontWeights.Bold, FontStyles.Normal, null,
                () => { _textBold = !_textBold; ApplyTextStyleToSelection(); ShowTextSettings(); }));
            fontStack.Children.Add(StyleToggle("I", Loc("Str_Lbl_Italic"), _textItalic, FontWeights.Normal, FontStyles.Italic, null,
                () => { _textItalic = !_textItalic; ApplyTextStyleToSelection(); ShowTextSettings(); }));
            fontStack.Children.Add(StyleToggle("S", Loc("Str_Lbl_Strike"), _textStrike, FontWeights.Normal, FontStyles.Normal, TextDecorations.Strikethrough,
                () => { _textStrike = !_textStrike; ApplyTextStyleToSelection(); ShowTextSettings(); }));
            fontStack.Children.Add(StyleToggle("U", Loc("Str_Lbl_Underline"), _textUnderline, FontWeights.Normal, FontStyles.Normal, TextDecorations.Underline,
                () => { _textUnderline = !_textUnderline; ApplyTextStyleToSelection(); ShowTextSettings(); }));
            Place(gLeft, fontStack, 0, 0);

            // Opacity (row 0) and Fill Opacity (row 1) block: label col 0, slider col 1, value col 2.
            Place(gOpacity, DimLabel(Loc("Str_Bar_Opacity"), 0, rightAlign: true), 0, 0);
            var opacitySlider = new Slider
            {
                Minimum = 10,
                Maximum = 255,
                Value = _textOpacity,
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("DarkSlider")
            };
            Place(gOpacity, opacitySlider, 0, 1);
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(_textOpacity / 255.0 * 100)}%",
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Width = 40,
                TextAlignment = TextAlignment.Right
            };
            opacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Place(gOpacity, opacityLabel, 0, 2);
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                _textOpacity = a;
                _textColor = Color.FromArgb(a, _textColor.R, _textColor.G, _textColor.B);
                ApplyTextStyleToSelection();
            };

            Place(gOpacity, DimLabel(Loc("Str_Bar_FillOpacity"), 10, rightAlign: true), 1, 0);
            byte curFillA = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;
            var fillOpSlider = new Slider
            {
                Minimum = 10,
                Maximum = 255,
                Value = curFillA,
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
                Style = (Style)FindResource("DarkSlider")
            };
            Place(gOpacity, fillOpSlider, 1, 1);
            var fillOpLabel = new TextBlock
            {
                Text = $"{(int)(curFillA / 255.0 * 100)}%",
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 10, 0, 0),
                Width = 40,
                TextAlignment = TextAlignment.Right
            };
            fillOpLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            Place(gOpacity, fillOpLabel, 1, 2);
            fillOpSlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                fillOpLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                // Dragging opacity turns the fill on (defaults to the current color, white for whiteout).
                _textFillColor = Color.FromArgb(a, _textFillColor.R, _textFillColor.G, _textFillColor.B);
                ApplyTextStyleToSelection();
            };

            // Faint divider between the Opacity and Fill Opacity rows, matching the color/fill one.
            var opDivider = new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                Margin = new Thickness(0, 5, 8, 0)
            };
            Place(gOpacity, opDivider, 1, 1, 2);

            // Assemble the blocks into a wrapping host: grip + the three 2-row blocks, with thin separators
            // between them. When wide it's the same two rows as before; when narrow whole blocks drop down.
            var wrapHost = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2), Background = Brushes.Transparent };
            // Left-to-right collapse: gLeft (Font/Size) stays on row 1 with the grip; Color and Opacity live
            // in a nested wrap panel that, as the bar narrows, FIRST drops below gLeft as a whole (the break
            // lands "before the colors"), THEN splits so Opacity drops below Color. Inter-block spacing is
            // each block's right margin (set above), so it survives wrapping with no separators.
            var tRest = new WrapPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
            tRest.Children.Add(gPalette);
            tRest.Children.Add(gOpacity);
            wrapHost.Children.Add(textGrip);
            wrapHost.Children.Add(gLeft);
            wrapHost.Children.Add(tRest);
            _annotBarDragInners.Clear();
            _annotBarDragInners.Add(tRest);

            _textSettingsBar = new Border
            {
                BorderThickness = new Thickness(1, 0, 1, 1),   // no top border - the toolbar above already separates
                HorizontalAlignment = HorizontalAlignment.Right,  // right-anchored; slid via the grip
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Effect = AnnotBarShadow(),
                Child = BuildBarHost(wrapHost),
                Margin = new Thickness(0, 0, 0, 0)
            };
            _textSettingsBar.SetResourceReference(Border.BackgroundProperty, "BgFlyout");
            _textSettingsBar.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_textSettingsBar, 100);
                previewArea.Children.Add(_textSettingsBar);
                // Cap the wrap host to the document area's width so the blocks reflow on a narrow window.
                wrapHost.SetBinding(FrameworkElement.MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth")
                { Source = previewArea, Converter = _barWidthInset });
                // The nested Color+Opacity panel gets the same cap so that, once it has dropped to its own
                // row, it splits Color / Opacity when even that row is too narrow.
                tRest.SetBinding(FrameworkElement.MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth")
                { Source = previewArea, Converter = _barWidthInset });
                WireBarWrapAdaptation(wrapHost, textGrip, gLeft, previewArea);
                PlaceAnnotationBar(_textSettingsBar, textGrip, fadeIn: appearing);
            }
            _annotBarTool = EditTool.Text;
            _annotBarMinimized = false;   // a freshly built bar is full-size
        }

        private void HideTextSettings()
        {
            FadeOutAndRemoveBar(_textSettingsBar);
            _textSettingsBar = null;
            if (_annotBarTool == EditTool.Text) _annotBarTool = null;
        }

        private void PlaceImageFromDialog(Point pos, int pageIdx)
        {
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Open)
            {
                Title = "Insert Image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var imgBytes = File.ReadAllBytes(dlg.FileName);
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
                KillerDialog.Show(this, $"Could not load image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (pageIdx >= _doc.PageCount) return;

            double pw = _renderDims.TryGetValue(pageIdx, out var rd) ? rd.w : 2048.0;
            double ph = _renderDims.TryGetValue(pageIdx, out var rd2) ? rd2.h : 2048.0;

            try
            {
                if (Clipboard.ContainsImage())
                {
                    var src = Clipboard.GetImage();
                    if (src is null) { SetStatus(Loc("Str_St_ClipImageUnreadable")); return; }

                    // Encode to PNG so ImageAnnotation stores standard bytes (same as file import).
                    byte[] imgBytes;
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                    using (var ms = new MemoryStream()) { encoder.Save(ms); imgBytes = ms.ToArray(); }

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
                    double sy = _doc.Pages[pageIdx].Height.Point / Math.Max(1.0, ph);
                    if (sy > 0) fontCanvas = _textFontSize / sy;

                    var ta = new TextAnnotation
                    {
                        PageIndex = pageIdx,
                        Position = new Point(pw * 0.25, ph * 0.45),
                        Content = content,
                        FontSize = fontCanvas
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
                KillerDialog.Show(this, $"Could not paste:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}
