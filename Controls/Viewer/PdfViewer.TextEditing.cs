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

namespace KillerPDF.Controls
{
    // Moved from Shell/TextEditing.cs; the namespace and class line are the only changes. Window
    // members spelled bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        // ============================================================
        // Inline text editing (double-click)
        // ============================================================

        // While a paired text is being re-edited, trace its cover with a dashed outline so the cover stays
        // visible (its opaque fill often matches the page, so without this it looks like the cover vanished).
        private void ShowReeditCoverOutline(string pairId, int pageIdx)
        {
            RemoveReeditCoverOutline();
            if (pairId.Length == 0 || !_annotations.TryGetValue(pageIdx, out var list)) return;
            var cover = list.OfType<CoverAnnotation>().FirstOrDefault(c => c.PairId == pairId);
            if (cover is null) return;
            double inv = 1.0;
            if (_activeCanvas.LayoutTransform is ScaleTransform st && st.ScaleX > 0.0001) inv = 1.0 / st.ScaleX;
            var pb = cover.Bounds;
            _reeditCoverOutline = new Rectangle
            {
                Width = pb.Width + 4,
                Height = pb.Height + 4,
                Stroke = DarkerAccentBrush(),
                StrokeThickness = 1.5 * inv,
                StrokeDashArray = [4, 3],
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_reeditCoverOutline, pb.X - 2);
            Canvas.SetTop(_reeditCoverOutline, pb.Y - 2);
            _activeCanvas.Children.Add(_reeditCoverOutline);
        }

        private void RemoveReeditCoverOutline()
        {
            if (_reeditCoverOutline is not null)
            {
                (_reeditCoverOutline.Parent as Canvas)?.Children.Remove(_reeditCoverOutline);
                _reeditCoverOutline = null;
            }
        }

        // Heuristic for a broken glyph->Unicode CMap (common on OCR'd scans): the extracted text comes out
        // as mojibake - replacement chars, private-use glyphs, or words peppered with currency/math symbols.
        // We don't pre-fill an in-place edit from text that looks like this. Conservative on purpose so clean
        // PDFs are never flagged; all-letter garbling (wrong letters that are still valid) can't be caught.
        private static bool LooksGarbled(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            const string ok = ".,;:!?'\"()[]{}-/\\&%@#*+=<>|~`^_$";
            int letters = 0, weird = 0, total = 0;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                total++;
                if (char.IsLetterOrDigit(c)) { letters++; continue; }
                if (ok.IndexOf(c) >= 0) continue;          // ordinary punctuation is fine
                weird++;                                   // replacement / PUA / stray symbol = mapping break
            }
            if (total == 0) return false;
            return (double)weird / total > 0.15 || (double)letters / total < 0.35;
        }

        private void EditTextAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;

            // Commit any existing edit first
            if (_activeTextBox is not null)
            {
                CommitActiveTextBox();
                return;
            }

            // Re-edit a user-placed text annotation: lift it into an editable box
            // pre-filled with its content, size (shown in points), and color.
            if (_annotations.TryGetValue(pageIdx, out var placedPage))
            {
                var placed = placedPage.OfType<TextAnnotation>()
                    .LastOrDefault(a => HitTestAnnotation(a, canvasPos, out _));
                if (placed is not null)
                {
                    var pcol = placed.GetColor();
                    _textColor = pcol;
                    _textOpacity = pcol.A;   // keep the opacity slider in sync with the edited text
                    _textFillColor = placed.GetFill();   // and the fill swatches in sync with the box
                    double syp = 1.0;
                    if (_doc is not null && _renderDims.TryGetValue(pageIdx, out var prd) && prd.h > 0)
                        syp = _doc.Pages[pageIdx].Height.Point / prd.h;
                    _textFontSize = Math.Max(1, Math.Round(placed.FontSize * syp));
                    // Sync the bar's typeface + B/I/S to the box being re-edited.
                    _textFontName = string.IsNullOrEmpty(placed.FontName) ? "Segoe UI" : placed.FontName;
                    _textBold = placed.Bold; _textItalic = placed.Italic; _textStrike = placed.Strike; _textUnderline = placed.Underline;

                    _reeditOriginal = placed;
                    placedPage.Remove(placed);
                    RenderAllAnnotations(pageIdx);
                    // Keep the paired cover visible (outlined) for the duration of the edit.
                    ShowReeditCoverOutline(placed.PairId, pageIdx);

                    var ptb = new TextBox
                    {
                        Text = placed.Content,
                        Background = TextEditBackground(),
                        Foreground = new SolidColorBrush(pcol),
                        BorderBrush = (SolidColorBrush)FindResource("PrimaryBrush"),
                        SelectionBrush = AccentBrush(),
                        CaretBrush = new SolidColorBrush(pcol),
                        Template = FlatTextBoxTemplate(),
                        BorderThickness = new Thickness(1),
                        FontFamily = UiKit.UiFont,
                        FontSize = placed.FontSize,
                        Width = placed.Width > 0 ? placed.Width : TextBoxDefaultWidth,
                        MinHeight = 24,
                        Padding = new Thickness(2),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Tag = pageIdx
                    };
                    Canvas.SetLeft(ptb, placed.Position.X);
                    Canvas.SetTop(ptb, placed.Position.Y);
                    _activeCanvas.Children.Add(ptb);
                    _activeTextBox = ptb;
                    StyleEditBox(ptb);   // restore the box's typeface + B/I/S
                    ptb.PreviewKeyDown += TextBox_PreviewKeyDown;
                    ptb.Loaded += (s, ev) => { ptb.Focus(); Keyboard.Focus(ptb); ptb.SelectAll(); ptb.LostFocus += TextBox_LostFocus; AttachTextEditResizeHandles(ptb); };
                    ShowTextSettings();
                    SetStatus(Loc("Str_St_EditingText"));
                    return;
                }
            }

            // The click landed on an existing text cover but not its replacement text (handled above).
            // Don't start a fresh detection over an edit that already exists - that would stack a second
            // cover+text. Bail so the user grabs the existing text/cover instead of duplicating it.
            if (_annotations.TryGetValue(pageIdx, out var coverPage)
                && coverPage.OfType<CoverAnnotation>().Any(c => { var b = c.Bounds; b.Inflate(6, 6); return b.Contains(canvasPos); }))
            {
                SetStatus(Loc("Str_St_AlreadyEditHere"));
                return;
            }

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sxInv = (double)renderW / pdfW; // pdf->canvas
                double syInv = (double)renderH / pdfH;

                // Convert all words to canvas coordinates upfront
                var canvasWords = page.GetWords().Select(w =>
                {
                    double cx = w.BoundingBox.Left * sxInv;
                    double cy = renderH - (w.BoundingBox.Top * syInv);
                    double cw = (w.BoundingBox.Right - w.BoundingBox.Left) * sxInv;
                    double ch = (w.BoundingBox.Top - w.BoundingBox.Bottom) * syInv;
                    return new { Word = w, Rect = new Rect(cx, cy, cw, ch) };
                }).ToList();

                if (canvasWords.Count == 0)
                {
                    // Scanned / image-only page: no text layer to detect. Fall back to a manual edit -
                    // drop a cover + empty text box at the click so the user can white out the scanned
                    // text and type over it by hand (resize the cover to fit).
                    double mf = Math.Max(_textFontSize * syInv, 8);   // current text size in canvas units
                    StartCoverTextEdit(pageIdx, new Rect(canvasPos.X, canvasPos.Y, 200, mf * 1.35), "", mf, "Segoe UI", syInv);
                    return;
                }

                // Find words on the same line as the click (Y overlap with tolerance)
                var clickY = canvasPos.Y;
                var lineWords = canvasWords
                    .Where(cw => clickY >= cw.Rect.Top - 3 && clickY <= cw.Rect.Bottom + 3)
                    .OrderBy(cw => cw.Rect.Left)  // strictly left-to-right
                    .ToList();

                if (lineWords.Count == 0)
                {
                    // Try nearest line within 20px
                    var nearest = canvasWords
                        .OrderBy(cw => Math.Abs((cw.Rect.Top + cw.Rect.Bottom) / 2 - clickY))
                        .First();
                    double nearMidY = (nearest.Rect.Top + nearest.Rect.Bottom) / 2;
                    lineWords = [..canvasWords
                        .Where(cw => Math.Abs((cw.Rect.Top + cw.Rect.Bottom) / 2 - nearMidY) < 5)
                        .OrderBy(cw => cw.Rect.Left)];
                }

                if (lineWords.Count == 0)
                {
                    SetStatus(Loc("Str_St_NoTextLine"));
                    return;
                }

                // Narrow to the contiguous run of words around the click. Words at the same Y in a
                // second column are separated by a large horizontal gap, so stop there instead of
                // merging both columns into one edit (the "weird text" / page-spanning edit).
                if (lineWords.Count > 1)
                {
                    int ci = 0; double bestDx = double.MaxValue;
                    for (int i = 0; i < lineWords.Count; i++)
                    {
                        var r = lineWords[i].Rect;
                        double dx = canvasPos.X < r.Left ? r.Left - canvasPos.X
                                  : canvasPos.X > r.Right ? canvasPos.X - r.Right : 0;
                        if (dx < bestDx) { bestDx = dx; ci = i; }
                    }
                    double gapMax = Math.Max(lineWords[ci].Rect.Height * 1.5, 24);   // word spacing is small; a column gap is large
                    int lo = ci, hi = ci;
                    while (lo > 0 && lineWords[lo].Rect.Left - lineWords[lo - 1].Rect.Right <= gapMax) lo--;
                    while (hi < lineWords.Count - 1 && lineWords[hi + 1].Rect.Left - lineWords[hi].Rect.Right <= gapMax) hi++;
                    lineWords = lineWords.GetRange(lo, hi - lo + 1);
                }

                // Compute bounding box in canvas space
                double cLeft = lineWords.Min(w => w.Rect.Left);
                double cTop = lineWords.Min(w => w.Rect.Top);
                double cRight = lineWords.Max(w => w.Rect.Right);
                double cBottom = lineWords.Max(w => w.Rect.Bottom);
                double cWidth = cRight - cLeft;
                double cHeight = cBottom - cTop;

                string lineText = string.Join(" ", lineWords.Select(w => w.Word.Text));

                // If this line is already covered by an edit, don't detect it again - that just stacks a
                // duplicate cover+text on top of the existing one. The original PDF text under a cover is
                // "consumed": re-edit by clicking the replacement text instead.
                var lineRect = new Rect(cLeft, cTop, Math.Max(1, cWidth), Math.Max(1, cHeight));
                if (_annotations.TryGetValue(pageIdx, out var coveredPage)
                    && coveredPage.OfType<CoverAnnotation>().Any(c => c.Bounds.IntersectsWith(lineRect)))
                {
                    SetStatus(Loc("Str_St_LineAlreadyEdited"));
                    return;
                }

                // Get actual font info from PdfPig letter data
                double canvasFontSize = cHeight * 0.75; // fallback
                string fontName = "Segoe UI"; // fallback
                var firstWord = lineWords.First().Word;
                try
                {
                    if (firstWord.Letters.Count > 0)
                    {
                        var letter = firstWord.Letters[0];
                        // PointSize is the glyph size in points. FontSize is the size as written in the
                        // content stream, which only matches the point size when the text matrix doesn't
                        // scale: a generator that emits "/F1 1 Tf" and scales through Tm reports FontSize
                        // 1, which collapsed the replacement onto the floor below and read back as 3pt
                        // (#163). Fall back to FontSize, then to the line-height estimate above, since
                        // PointSize can be 0 on fonts with no usable metrics (e.g. some Type3).
                        double pdfFontPts = letter.PointSize > 0 ? letter.PointSize : letter.FontSize;
                        if (pdfFontPts > 0)
                            canvasFontSize = pdfFontPts * syInv;

                        // Font family from the LETTER, never the word (#166, thanks Ryokoxx):
                        // Word.FontName joins its letters' names ("Helvetica Helvetica Helvetica
                        // ..."), which FontFamily cannot resolve - so that fallback landed on the
                        // default font, exactly where no fallback at all would have. The outer
                        // catch already covers a read that throws, so this needs no inner one.
                        string? rawFont = letter.FontName;
                        if (!string.IsNullOrEmpty(rawFont))
                        {
                            string fontStr = rawFont!;
                            // Strip PDF subset prefix (e.g. "ABCDEF+FontName" -> "FontName")
                            if (fontStr.Contains('+'))
                                fontStr = fontStr[(fontStr.IndexOf('+') + 1)..];
                            // Clean common suffixes
                            fontStr = fontStr.Replace(",Bold", "").Replace(",Italic", "")
                                             .Replace("-Bold", "").Replace("-Italic", "")
                                             .Replace("-Roman", "").Replace("-Regular", "");
                            if (!string.IsNullOrWhiteSpace(fontStr))
                                fontName = fontStr;
                        }
                    }
                }
                catch { /* use fallbacks */ }

                // Drop the cover + editable text box for the detected line. Detected size carries the
                // EditTextSizeCorrection (WPF renders the source point size ~25% large); manual edits don't.
                // Scanned PDFs with a broken glyph->Unicode map extract as mojibake; in that case start the
                // box empty (like a manual edit) instead of pre-filling garbage - the user types over the
                // whited-out original.
                string prefill = LooksGarbled(lineText) ? "" : lineText;
                StartCoverTextEdit(pageIdx, new Rect(cLeft, cTop, cWidth, cHeight), prefill,
                    Math.Max(canvasFontSize * EditTextSizeCorrection, 8), fontName, syInv);
            }
            catch (Exception ex)
            {
                SetStatus($"Text edit error: {ex.Message}");
            }
        }

        // Drops an opaque cover at the given line and opens an editable text box on top of it - the two
        // halves of an in-place edit. Used for a detected PDF-text line and, on a scanned page with no
        // text layer, for a manual edit at the click point. boxFontCanvas is the on-canvas font size;
        // the cover fill and text ink are sampled from the page so the edit blends in.
        private void StartCoverTextEdit(int pageIdx, Rect lineRect, string text, double boxFontCanvas, string fontName, double syInv)
        {
            double cLeft = lineRect.X, cTop = lineRect.Y, cWidth = lineRect.Width, cHeight = lineRect.Height;
            // Pair id shared with the replacement text - the cover renders dashed while paired.
            var cover = new CoverAnnotation
            {
                PageIndex = pageIdx,
                PairId = Guid.NewGuid().ToString("N"),
                Bounds = new Rect(cLeft - 3, cTop - 3, cWidth + 6, cHeight + 6)
            };
            var sampleRect = new Rect(cLeft, cTop, cWidth, cHeight);
            Color coverBg = SampleCoverColor(pageIdx, sampleRect);
            Color inkColor = SampleTextColor(pageIdx, sampleRect, coverBg);
            cover.SetColor(coverBg);
            _textColor = inkColor; _textOpacity = inkColor.A;
            _textFontSize = Math.Max(1, Math.Round(boxFontCanvas / syInv));   // canvas units -> points
            // Replacing raw PDF text starts from the detected font with no extra styling; the box below
            // already uses fontName, and the bar/commit read this state.
            _textFontName = string.IsNullOrEmpty(fontName) ? "Segoe UI" : fontName;
            _textBold = _textItalic = _textStrike = _textUnderline = false;
            _pendingEditWasDirty = _isDirty;   // capture before the cover dirties the doc
            if (!_annotations.ContainsKey(pageIdx)) _annotations[pageIdx] = [];
            _annotations[pageIdx].Add(cover);
            _pendingCover = cover;
            MarkDirty();
            RenderAllAnnotations(pageIdx);

            var tb = new TextBox
            {
                Text = text,
                Background = Brushes.Transparent,   // the opaque cover behind supplies the backdrop
                Foreground = new SolidColorBrush(inkColor),
                BorderBrush = (SolidColorBrush)FindResource("PrimaryBrush"),
                SelectionBrush = AccentBrush(),
                CaretBrush = new SolidColorBrush(inkColor),
                Template = FlatTextBoxTemplate(),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily(fontName),
                FontSize = boxFontCanvas,
                Width = Math.Max(cWidth + 20, 80),
                MinHeight = 24,
                Padding = new Thickness(2, 0, 2, 0),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Tag = pageIdx
            };
            Canvas.SetLeft(tb, cLeft);
            Canvas.SetTop(tb, cTop);
            _activeCanvas.Children.Add(tb);
            _activeTextBox = tb;
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.Loaded += (s, ev) => { tb.Focus(); Keyboard.Focus(tb); tb.SelectAll(); tb.LostFocus += TextBox_LostFocus; AttachTextEditResizeHandles(tb); };
            ShowTextSettings();
            SetStatus(string.IsNullOrEmpty(text)
                ? "Type your text, then drag the cover over the original - Enter to save, Escape to cancel"
                : "Editing text - change size/color above, Enter to save, Escape to cancel");
        }

        // ============================================================
        // Text box handling
        // ============================================================

        // A flat TextBox template (just a themed border hosting the text) so the OS default focus border
        // and selection chrome - the stray WPF "blue" - never show on the in-canvas text editor.
        private static ControlTemplate FlatTextBoxTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            return new ControlTemplate(typeof(TextBox)) { VisualTree = b };
        }

        // Background shown WHILE editing a text box: the chosen fill if one is set, otherwise a faint
        // translucent neutral gray. Gray (not white) so the empty editable box stays visible on both
        // light/white pages and dark pages; it's only shown during editing and never committed.
        private Brush TextEditBackground()
            => _textFillColor.A > 0 ? new SolidColorBrush(_textFillColor)
                                    : new SolidColorBrush(Color.FromArgb(64, 128, 128, 128));

        // True when 'pos' (in _activeCanvas coordinates) falls inside the text box currently being
        // edited AND that box lives on _activeCanvas. Used so a click inside the box doesn't get
        // treated as a request to place a new one (the Grid-view "box jumps to cursor" bug).
        private bool ClickInsideActiveTextBox(Point pos)
        {
            if (_activeTextBox is null || !ReferenceEquals(_activeTextBox.Parent, _activeCanvas)) return false;
            double x = Canvas.GetLeft(_activeTextBox), y = Canvas.GetTop(_activeTextBox);
            if (double.IsNaN(x) || double.IsNaN(y)) return false;
            double w = _activeTextBox.ActualWidth > 0 ? _activeTextBox.ActualWidth : _activeTextBox.Width;
            double h = _activeTextBox.ActualHeight > 0 ? _activeTextBox.ActualHeight : Math.Max(_activeTextBox.MinHeight, 24);
            return pos.X >= x && pos.X <= x + w && pos.Y >= y && pos.Y <= y + h;
        }

        private void PlaceTextBox(Point pos, int pageIdx)
        {
            // _textFontSize is a point size; convert to the page's canvas (render-dim) units so
            // it renders and exports as real points. DrawAnnotationsOnDocument multiplies by
            // sy = page.Height.Point / renderH, so dividing by sy here makes "14" export as 14pt.
            double fontCanvas = _textFontSize;
            if (_doc is not null && _renderDims.TryGetValue(pageIdx, out var rdims) && rdims.h > 0)
            {
                double sy = _doc.Pages[pageIdx].Height.Point / rdims.h;
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            // A default-size box dropped at the click point. Width is fixed (text wraps to it) and the
            // box auto-grows downward as you type; resize the width later via the corner handles.
            var tb = new TextBox
            {
                Background = TextEditBackground(),
                Foreground = new SolidColorBrush(_textColor),
                BorderBrush = (SolidColorBrush)FindResource("PrimaryBrush"),
                SelectionBrush = AccentBrush(),
                CaretBrush = new SolidColorBrush(_textColor),
                Template = FlatTextBoxTemplate(),
                BorderThickness = new Thickness(1),
                FontFamily = UiKit.UiFont,
                FontSize = fontCanvas,
                Width = TextBoxDefaultWidth,
                MinHeight = 24,
                Padding = new Thickness(2),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Tag = pageIdx
            };
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, pos.Y);
            _activeCanvas.Children.Add(tb);
            _activeTextBox = tb;
            StyleEditBox(tb);   // current typeface + B/I/S
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.LostFocus += TextBox_LostFocus;
            // Focus the box and attach its live resize handles once laid out. Loaded fires on first
            // placement; a dispatcher fallback covers re-entry (Text tool -> Select -> Text again),
            // where Loaded may have already run - without it the new box silently took no typing and
            // showed no handles. Activate is idempotent (guards against double focus/handle attach).
            void Activate()
            {
                if (!ReferenceEquals(_activeTextBox, tb)) return;
                tb.Focus();
                Keyboard.Focus(tb);
                if (!ReferenceEquals(_tehBox, tb)) AttachTextEditResizeHandles(tb);
            }
            tb.Loaded += (s, e) => Activate();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(Activate));
        }

        // ── Live resize handles around the editing TextBox ──────────────────────────────
        // Corner squares the user can drag to resize the box mid-edit, then keep typing. The
        // box auto-grows in height until a handle is dragged, after which the height is free-form.
        private void AttachTextEditResizeHandles(TextBox tb)
        {
            RemoveTextEditHandles();
            _tehBox = tb;
            double inv = 1.0;
            if (_activeCanvas.LayoutTransform is ScaleTransform sc && sc.ScaleX > 0.0001) inv = 1.0 / sc.ScaleX;
            double hs = 12 * inv;
            foreach (string tag in new[] { "NW", "NE", "SE", "SW" })
            {
                var hd = new Rectangle
                {
                    Width = hs,
                    Height = hs,
                    Fill = AccentBrush(),
                    Stroke = Brushes.White,
                    StrokeThickness = 1 * inv,
                    Cursor = (tag is "NW" or "SE") ? Cursors.SizeNWSE : Cursors.SizeNESW,
                    Focusable = false,   // so grabbing a handle does not blur (and commit) the TextBox
                    Tag = tag
                };
                Panel.SetZIndex(hd, 200);
                // Hit detection + drag are handled in the canvas gesture handlers (which run as
                // PreviewMouseLeftButtonDown and would otherwise intercept the click), mirroring the
                // committed-annotation resize handles.
                _textEditHandles.Add(hd);
                _activeCanvas.Children.Add(hd);
            }
            tb.SizeChanged += TextEditBox_SizeChanged;
            LayoutTextEditHandles();
        }

        private void TextEditBox_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTextEditHandles();

        private void LayoutTextEditHandles()
        {
            if (_tehBox is null || _textEditHandles.Count == 0) return;
            double x = Canvas.GetLeft(_tehBox), y = Canvas.GetTop(_tehBox);
            double w = _tehBox.ActualWidth > 0 ? _tehBox.ActualWidth : _tehBox.Width;
            double h = _tehBox.ActualHeight;
            foreach (var hd in _textEditHandles)
            {
                double hsz = hd.Width;
                (double cx, double cy) = (hd.Tag as string) switch
                {
                    "NW" => (x, y),
                    "NE" => (x + w, y),
                    "SW" => (x, y + h),
                    _ => (x + w, y + h)   // SE
                };
                Canvas.SetLeft(hd, cx - hsz / 2);
                Canvas.SetTop(hd, cy - hsz / 2);
            }
        }

        private void RemoveTextEditHandles()
        {
            if (_tehBox is not null) _tehBox.SizeChanged -= TextEditBox_SizeChanged;
            foreach (var hd in _textEditHandles) RemoveFromParent(hd);
            _textEditHandles.Clear();
            _tehBox = null;
            _draggingTextEditHandle = false;
        }

        // Remove a canvas child from whatever Panel actually parents it, instead of assuming it lives
        // on _activeCanvas. In continuous/grid view _activeCanvas follows the mouse to whichever page
        // was last clicked, so a text-edit box, its whiteout, or its handles - placed earlier on a
        // different page's canvas - would otherwise survive removal and become orphaned: still painted,
        // but unreachable by Delete, Clear All, or resize. Its live Parent is always the correct host.
        private static void RemoveFromParent(UIElement? el)
        {
            if (el is FrameworkElement fe && fe.Parent is Panel p)
                p.Children.Remove(el);
        }

        // Hit-test a live text-edit handle at the given canvas point; returns its corner tag or null.
        private string? TextEditHandleAt(Point pos)
        {
            foreach (var hd in _textEditHandles)
            {
                double hx = Canvas.GetLeft(hd), hy = Canvas.GetTop(hd);
                if (pos.X >= hx && pos.X <= hx + hd.Width &&
                    pos.Y >= hy && pos.Y <= hy + hd.Height)
                    return hd.Tag as string ?? "SE";
            }
            return null;
        }

        // Attached as PreviewKeyDown (tunneling) so Enter is caught before the TextBox inserts a line
        // break: Enter commits, Shift+Enter falls through to make a newline (the box is AcceptsReturn).
        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelActiveTextEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                     && sender is TextBox ztb && !ztb.CanUndo)
            {
                // The box has no typed text left to undo, so Ctrl+Z backs out of the whole in-place edit
                // (same as Escape) instead of being a no-op - otherwise a fresh edit could only be undone
                // after committing it. While the box still has edits to undo, WPF's TextBox handles Ctrl+Z.
                CancelActiveTextEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                CommitActiveTextBox();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control
                     && (e.Key == Key.B || e.Key == Key.I || e.Key == Key.U))
            {
                // 1.6.6: the standard formatting chords while typing in a box - mirror the text
                // bar's B/I/U toggles (whole-annotation style, like the buttons). Ctrl+I became
                // available when Invert moved to the bare N key.
                if (e.Key == Key.B) _textBold = !_textBold;
                else if (e.Key == Key.I) _textItalic = !_textItalic;
                else _textUnderline = !_textUnderline;
                if (sender is TextBox stb) StyleEditBox(stb);
                ApplyTextStyleToSelection();
                ShowTextSettings();
                e.Handled = true;
            }
        }

        // Abandons the in-progress text edit: removes the editing box and its handles, drops a pending
        // cover (placed un-undone), and restores a re-edited annotation. Shared by Escape and the
        // "Ctrl+Z with nothing left in the box" path.
        private void CancelActiveTextEdit()
        {
            RemoveTextEditHandles();
            RemoveReeditCoverOutline();   // edit cancelled; drop the cover hint (repaint follows)
            if (_activeTextBox is not null)
            {
                RemoveFromParent(_activeTextBox);
                _activeTextBox = null;
            }
            // Cancelling an existing-text edit drops the cover too (it was placed un-undone).
            if (_pendingCover is not null) DiscardPendingCover();
            if (_reeditOriginal is not null)
            {
                int rp = _reeditOriginal.PageIndex;
                if (!_annotations.TryGetValue(rp, out var rlist)) { rlist = []; _annotations[rp] = rlist; }
                rlist.Add(_reeditOriginal);
                _reeditOriginal = null;
                RenderAllAnnotations(rp);
            }
            if (_currentTool != EditTool.Text) HideTextSettings();
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Don't commit while a resize handle is being dragged (the box temporarily loses focus).
            if (_draggingTextEditHandle) return;
            // Commit if the box has content, or (for an existing-text edit) even when emptied, so the
            // pending cover is resolved instead of lingering when the user clicks away from a blank edit.
            if (_activeTextBox is not null && (!string.IsNullOrWhiteSpace(_activeTextBox.Text) || _pendingCover is not null))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Keep the edit box open if focus moved into the size/color bar so the
                    // user can restyle (the Size ComboBox takes focus; color swatches do not).
                    if (_textSettingsBar is not null && Keyboard.FocusedElement is DependencyObject fe
                        && IsDescendantOf(fe, _textSettingsBar))
                        return;
                    CommitActiveTextBox();
                }),
                System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            RemoveTextEditHandles();
            RemoveReeditCoverOutline();   // the re-edit is ending; drop its cover hint (repaint follows)
            string reeditPair = _reeditOriginal?.PairId ?? "";   // preserve a re-edited text's cover pairing
            _reeditOriginal = null;   // committing replaces any annotation being re-edited

            string content = tb.Text.Trim();
            int pageIdx = tb.Tag is int idx ? idx : PageList.SelectedIndex;
            double x = Canvas.GetLeft(tb);
            double y = Canvas.GetTop(tb);

            // Remove the editing box from whatever canvas actually parents it. _activeCanvas may have
            // moved to another page when the user clicked away to commit (continuous/grid), and a
            // re-looked-up page canvas can be a different instance than the one the box was placed on,
            // either of which leaves the box orphaned (visible, but immune to Delete/Clear All). Its
            // live Parent is the correct host.
            RemoveFromParent(tb);

            if (!string.IsNullOrEmpty(content))
            {
                double boxW = (!double.IsNaN(tb.Width) && tb.Width > 0) ? tb.Width
                            : (tb.ActualWidth > 0 ? tb.ActualWidth : TextBoxDefaultWidth);
                var ta = new TextAnnotation
                {
                    PageIndex = pageIdx,
                    Position = new Point(x, y),
                    Content = content,
                    FontSize = tb.FontSize,
                    FontName = _textFontName,
                    Bold = _textBold,
                    Italic = _textItalic,
                    Strike = _textStrike,
                    Underline = _textUnderline,
                    Width = boxW
                };
                ta.SetColor(tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black);
                // A cover-paired edit gets no fill of its own - the opaque cover behind it is the backdrop.
                ta.SetFill(_pendingCover is not null ? Colors.Transparent : _textFillColor);
                // Free-form height if the box was manually resized; otherwise fit to the wrapped text.
                ta.Height = (!double.IsNaN(tb.Height) && tb.Height > 0)
                    ? tb.Height
                    : MeasureTextBoxHeight(content, boxW, tb.FontSize);
                // Keep the placed box fully on-page so its corners (and resize handles) stay reachable.
                ta.Position = ClampRectToPage(pageIdx, new Rect(ta.Position, new Size(ta.Width, ta.Height))).Location;
                // Carry the pairing so the cover knows its partner text exists (renders dashed).
                ta.PairId = _pendingCover is not null ? _pendingCover.PairId : reeditPair;
                if (_pendingCover is not null)
                {
                    // Existing-text edit: the cover is already in _annotations. Add the text beside it and
                    // push ONE grouped undo so a single Ctrl+Z right after cancels the whole edit. After
                    // this, cover and text are independent annotations (move/resize/recolor separately).
                    _annotations[pageIdx].Add(ta);
                    PushUndo(new UndoEntry(UndoKind.AnnotationGroup, pageIdx,
                        WasDirty: _pendingEditWasDirty, AnnotGroup: [_pendingCover, ta]));
                    _pendingCover = null;
                    MarkDirty();
                }
                else
                {
                    AddAnnotation(ta);
                }
                RenderAllAnnotations(pageIdx);   // redraw on the correct page's canvas
                WarnIfGlyphsWillBeLost(ta);      // #168: say so NOW, not after saving and reopening
            }
            else if (_pendingCover is not null)
            {
                // Edit left empty - abandon it and drop the cover (added without its own undo entry).
                DiscardPendingCover();
            }
            if (_currentTool != EditTool.Text) HideTextSettings();
        }

        /// <summary>
        /// #168: the editor borrows glyphs from any installed font, so text ALWAYS looks right while
        /// being typed - but a save can only embed fonts, and a character no installed font carries
        /// becomes a box in the file. That used to be invisible until the user saved, closed and
        /// reopened. Now it is said at the moment the text is placed, while it can still be fixed.
        ///
        /// Only fires when the whole fallback chain comes up short (a box mixing two non-Latin
        /// scripts, or a script with no font installed at all), so it does not nag: ordinary
        /// Japanese, Chinese, Korean or Bengali text resolves silently.
        /// </summary>
        private void WarnIfGlyphsWillBeLost(TextAnnotation ta)
        {
            try
            {
                if (string.IsNullOrEmpty(ta.Content)) return;
                string want = string.IsNullOrEmpty(ta.FontName) ? "Segoe UI" : ta.FontName;
                string family = Services.FontCoverage.PickFamily(want, ta.Content);
                string missing = Services.FontCoverage.UncoveredChars(family, ta.Content);
                if (missing.Length == 0) return;
                // W, not `this`: KillerDialog.Show takes a Window? owner and this class is a
                // UserControl. W is the owning MainWindow.
                KillerDialog.Show(W, string.Format(Loc("Str_Font_NoGlyphs"), missing), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* the warning must never be the thing that breaks placing text */ }
        }

        // Remove the not-yet-committed cover when an existing-text edit is cancelled or left empty. The
        // cover was added straight to _annotations without an undo entry, so just drop it and repaint.
        private void DiscardPendingCover()
        {
            if (_pendingCover is null) return;
            int pg = _pendingCover.PageIndex;
            if (_annotations.TryGetValue(pg, out var list)) list.Remove(_pendingCover);
            _pendingCover = null;
            MarkDirty(_pendingEditWasDirty);
            RenderAllAnnotations(pg);
        }

        // ── Cover background sampling ───────────────────────────────────────────────
        // Reads the page background color around an existing-text line so a cover blends into colored
        // headers/panels instead of showing a white box. Best-effort: returns white on any failure.

        // The page's rendered bitmap: the Image sibling of its overlay canvas (continuous/grid/two-page),
        // or the single-view PageImage. View-mode independent so sampling works everywhere.
        private System.Windows.Media.Imaging.BitmapSource? PageBitmapFor(int pageIdx)
        {
            if (_continuousCanvases.TryGetValue(pageIdx, out var overlay) && overlay.Parent is Panel mp)
                foreach (var ch in mp.Children)
                    if (ch is Image im && im.Source is System.Windows.Media.Imaging.BitmapSource bs) return bs;
            if (pageIdx == PageList.SelectedIndex && PageImage.Source is System.Windows.Media.Imaging.BitmapSource pbs)
                return pbs;
            return null;
        }

        private static Color ReadBgraPixel(System.Windows.Media.Imaging.BitmapSource bmp, int x, int y)
        {
            x = Math.Max(0, Math.Min(x, bmp.PixelWidth - 1));
            y = Math.Max(0, Math.Min(y, bmp.PixelHeight - 1));
            var px = new byte[4];
            bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);   // Bgra32: B,G,R,A
            // Composite over white (the PDF page background) so transparent pixels - common on repaired
            // or scanned renders - read as white, not black. Returns an opaque color for sampling.
            double a = px[3] / 255.0;
            byte r = (byte)(px[2] * a + 255 * (1 - a));
            byte g = (byte)(px[1] * a + 255 * (1 - a));
            byte b = (byte)(px[0] * a + 255 * (1 - a));
            return Color.FromRgb(r, g, b);
        }

        /// <summary>Background color around a text line, in canvas (render-dim) coordinates. White on failure.</summary>
        private Color SampleCoverColor(int pageIdx, Rect textBounds)
        {
            try
            {
                var bmp = PageBitmapFor(pageIdx);
                if (bmp is null || !_renderDims.TryGetValue(pageIdx, out var rd) || rd.w <= 0 || rd.h <= 0)
                    return Colors.White;
                double sx = bmp.PixelWidth / (double)rd.w;   // render-dim -> bitmap pixels
                double sy = bmp.PixelHeight / (double)rd.h;
                // Sample the whitespace just above and below the line (usually pure background) at a few
                // x offsets; take the median by luminance to shrug off a stray glyph or anti-aliased edge.
                double gap = Math.Max(3.0, textBounds.Height * 0.4);
                var cols = new List<Color>();
                foreach (double f in new[] { 0.2, 0.5, 0.8 })
                {
                    double x = textBounds.Left + textBounds.Width * f;
                    cols.Add(ReadBgraPixel(bmp, (int)Math.Round(x * sx), (int)Math.Round((textBounds.Top - gap) * sy)));
                    cols.Add(ReadBgraPixel(bmp, (int)Math.Round(x * sx), (int)Math.Round((textBounds.Bottom + gap) * sy)));
                }
                if (cols.Count == 0) return Colors.White;
                cols.Sort((a, b) => (0.299 * a.R + 0.587 * a.G + 0.114 * a.B)
                                    .CompareTo(0.299 * b.R + 0.587 * b.G + 0.114 * b.B));
                return cols[cols.Count / 2];
            }
            catch { return Colors.White; }
        }

        private static double ColorDist(Color a, Color b)
        {
            double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        /// <summary>The text "ink" color of a line: the color inside the glyph box farthest from the
        /// page background. Averages the purest-ink samples so anti-aliased edges don't desaturate it.
        /// Black on failure or when no real contrast is found.</summary>
        private Color SampleTextColor(int pageIdx, Rect textBounds, Color bg)
        {
            try
            {
                var bmp = PageBitmapFor(pageIdx);
                if (bmp is null || !_renderDims.TryGetValue(pageIdx, out var rd) || rd.w <= 0 || rd.h <= 0)
                    return Colors.Black;
                double sx = bmp.PixelWidth / (double)rd.w;
                double sy = bmp.PixelHeight / (double)rd.h;
                int cols = 16, rows = Math.Max(3, (int)Math.Min(8, textBounds.Height / 3));
                var scored = new List<(double dist, Color c)>();
                for (int ix = 0; ix < cols; ix++)
                    for (int iy = 0; iy < rows; iy++)
                    {
                        double x = textBounds.Left + textBounds.Width * (ix + 0.5) / cols;
                        double y = textBounds.Top + textBounds.Height * (iy + 0.5) / rows;
                        var c = ReadBgraPixel(bmp, (int)Math.Round(x * sx), (int)Math.Round(y * sy));
                        scored.Add((ColorDist(c, bg), c));
                    }
                if (scored.Count == 0) return Colors.Black;
                scored.Sort((a, b) => b.dist.CompareTo(a.dist));   // most ink-like first
                double maxDist = scored[0].dist;
                if (maxDist < 24) return Colors.Black;             // no real contrast -> default
                double thresh = maxDist * 0.7;                     // purest-ink cluster only
                double r = 0, g = 0, bl = 0; int n = 0;
                foreach (var (dist, c) in scored) { if (dist < thresh) break; r += c.R; g += c.G; bl += c.B; n++; }
                return n == 0 ? Colors.Black : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(bl / n));
            }
            catch { return Colors.Black; }
        }
    }
}
