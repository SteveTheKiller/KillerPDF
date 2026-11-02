using KillerPDF.Shell;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
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
using ContentDoc = KillerPDF.Services.PdfContentDocument;
using EngineDocs = KillerPdf.Engine.Documents;

namespace KillerPDF.Controls
{
    // Moved from Shell/TextEditing.cs; the namespace and class line are the only changes. Window
    // members spelled bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        private readonly ConditionalWeakTable<Canvas, SafeCanvas> _textEditorLayers = [];

        private SafeCanvas TextEditorLayerFor(Canvas pageCanvas)
        {
            if (_textEditorLayers.TryGetValue(pageCanvas, out SafeCanvas? existing))
            {
                EditableTextShortcutPolicy.AttachEditorLayer(pageCanvas, existing);
                return existing;
            }
            var layer = new SafeCanvas
            {
                Width = pageCanvas.Width,
                Height = pageCanvas.Height,
                Background = null,
                ClipToBounds = true
            };
            Panel.SetZIndex(layer, 1000);
            pageCanvas.SizeChanged += (_, _) =>
            {
                layer.Width = pageCanvas.ActualWidth > 0 ? pageCanvas.ActualWidth : pageCanvas.Width;
                layer.Height = pageCanvas.ActualHeight > 0 ? pageCanvas.ActualHeight : pageCanvas.Height;
            };
            EditableTextShortcutPolicy.AttachEditorLayer(pageCanvas, layer);
            _textEditorLayers.Add(pageCanvas, layer);
            return layer;
        }

        private void ClearRenderedCanvas(Canvas pageCanvas)
        {
            SafeCanvas editorLayer = TextEditorLayerFor(pageCanvas);
            for (int index = pageCanvas.Children.Count - 1; index >= 0; index--)
                if (!ReferenceEquals(pageCanvas.Children[index], editorLayer))
                    pageCanvas.Children.RemoveAt(index);
        }

        private void QueueTextEditorFocus(TextBox textBox, bool selectAll = false)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (!ReferenceEquals(_activeTextBox, textBox)
                    || textBox.Parent is not SafeCanvas) return;
                textBox.Focus();
                Keyboard.Focus(textBox);
                if (selectAll) textBox.SelectAll();
                if (!ReferenceEquals(_tehBox, textBox)) AttachTextEditResizeHandles(textBox);
            }));
        }

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

        // Page points per canvas unit for a rendered page, following its on-screen turn. Text sizes
        // are shown in points and stored in canvas units, and the export converts back the same way.
        private double PointsPerCanvasUnit(int pageIndex, double renderHeight)
        {
            var session = EnsureEngineDocumentSession();
            if (pageIndex < 0 || pageIndex >= session.PageCount) return 0;
            var info = session.Pages[pageIndex];
            return RedactionGeometry.PointsPerCanvasUnit(info.Width, info.Height, renderHeight, EffectiveRotation(pageIndex));
        }

        // Canvas units follow the render resolution, so a fixed canvas width came out narrower the
        // sharper the page was rendered. Convert the point width the same way as the font size.
        private double TextBoxDefaultWidth(int pageIndex)
        {
            if (_doc is not null && _renderDims.TryGetValue(pageIndex, out var dims) && dims.h > 0)
            {
                double sy = PointsPerCanvasUnit(pageIndex, dims.h);
                if (sy > 0) return TextBoxDefaultWidthPoints / sy;
            }
            return TextBoxDefaultWidthPoints;
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
                if (ok.Contains(c)) continue;              // ordinary punctuation is fine
                weird++;                                   // replacement / PUA / stray symbol = mapping break
            }
            if (total == 0) return false;
            return (double)weird / total > 0.15 || (double)letters / total < 0.35;
        }

        private void EditTextAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null
                || !_renderDims.TryGetValue(pageIdx, out var renderDimensions)) return;

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
                    // A replacement for edited page text carries the page's ink, which must not stay as
                    // the tool's color once the edit ends. An ordinary text box keeps syncing the bar.
                    if (placed.PairId.Length > 0) RememberTextToolDefaults();
                    _textColor = pcol;
                    _textOpacity = pcol.A;   // keep the opacity slider in sync with the edited text
                    _textFillColor = placed.GetFill();   // and the fill swatches in sync with the box
                    double syp = 1.0;
                    if (_doc is not null && _renderDims.TryGetValue(pageIdx, out var prd) && prd.h > 0)
                        syp = PointsPerCanvasUnit(pageIdx, prd.h);
                    _textFontSize = Math.Max(1, placed.FontSize * syp);
                    _textLetterSpacing = placed.LetterSpacing * syp;
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
                        Width = placed.Width > 0 ? placed.Width : TextBoxDefaultWidth(pageIdx),
                        MinHeight = 24,
                        Padding = new Thickness(2),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Tag = pageIdx
                    };
                    int placedTurn = TextTurn.Normalize(placed.Rotation);
                    if (placedTurn == 0)
                    {
                        Canvas.SetLeft(ptb, placed.Position.X);
                        Canvas.SetTop(ptb, placed.Position.Y);
                    }
                    else PlaceTurnedEditBox(ptb, placed.Position
                        + TextTurn.OriginOffset(placedTurn, placed.Width, placed.Height), placedTurn);
                    TextEditorLayerFor(_activeCanvas).Children.Add(ptb);
                    _activeTextBox = ptb;
                    StyleEditBox(ptb);   // restore the box's typeface + B/I/S
                    ptb.PreviewKeyDown += TextBox_PreviewKeyDown;
                    ptb.LostFocus += TextBox_LostFocus;
                    ptb.Loaded += (_, _) => QueueTextEditorFocus(ptb, selectAll: true);
                    ptb.PreviewMouseLeftButtonDown += (_, _) => QueueTextEditorFocus(ptb);
                    QueueTextEditorFocus(ptb, selectAll: true);
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
                var (renderW, renderH) = renderDimensions;

                using var contentDoc = ContentDoc.Open(_currentFile);
                if (pageIdx >= contentDoc.NumberOfPages) return;
                var page = contentDoc.GetPage(pageIdx + 1);

                double pdfW = page.Width;
                double pdfH = page.Height;
                // The canvas shows the page turned by its rotation while extracted text stays in the
                // unrotated page frame, so every box and baseline goes through the turn. Canvas units
                // per point are the same on both axes.
                int rotation = EffectiveRotation(pageIdx);
                double syInv = 1.0 / RedactionGeometry.PointsPerCanvasUnit(pdfW, pdfH, renderH, rotation);
                Point ToCanvas(double x, double y) =>
                    RedactionGeometry.PageToCanvas(x, y, x, y, pdfW, pdfH, renderW, renderH, rotation).TopLeft;

                // Convert all words to canvas coordinates upfront, with the turn each one reads at on
                // screen: the direction its first letter advances across the canvas.
                var allWords = page.GetWords().Select(w =>
                {
                    var b = w.BoundingBox;
                    Rect rect = RedactionGeometry.PageToCanvas(b.Left, b.Bottom, b.Right, b.Top,
                        pdfW, pdfH, renderW, renderH, rotation);
                    int wordTurn = 0;
                    if (w.Letters.Count > 0)
                    {
                        var first = w.Letters[0];
                        wordTurn = TextTurn.FromBaseline(ToCanvas(first.StartBaseLine.X, first.StartBaseLine.Y),
                            ToCanvas(first.EndBaseLine.X, first.EndBaseLine.Y));
                    }
                    return new { Word = w, Rect = rect, Turn = wordTurn };
                }).ToList();

                // An edit follows one reading direction: the clicked word's, or the nearest word's when
                // the click lands between words. Text reading another way is left out of the search.
                // Everything below works in that direction's reading frame, where the text reads left
                // to right, and maps back to the canvas for the cover and the box; for level text the
                // frame is the canvas itself.
                var clickedWord = allWords.FirstOrDefault(cw =>
                {
                    Rect hit = cw.Rect;
                    hit.Inflate(3, 3);
                    return hit.Contains(canvasPos);
                }) ?? allWords.OrderBy(cw => DistanceToRect(cw.Rect, canvasPos)).FirstOrDefault();
                int turn = clickedWord?.Turn ?? 0;
                var canvasWords = allWords.Where(cw => cw.Turn == turn)
                    .Select(cw => new { cw.Word, Rect = TextTurn.ToReading(cw.Rect, turn) })
                    .ToList();
                Point readingPos = TextTurn.ToReading(canvasPos, turn);

                // Flattened forms commonly represent an entry area as a run of dots or underscores.
                // Treat a click on that run as an empty text field instead of folding the placeholder
                // and its label into the nearest source-text line. The cover removes the printed marks
                // and the replacement box inherits their exact position and width.
                Rect? placeholder = TextEntryPlaceholder.FindNearest(
                    canvasWords.Select(w => new TextEntryPlaceholder.Candidate(w.Word.Text, w.Rect)),
                    readingPos);
                if (placeholder is Rect blank)
                {
                    double blankFont = Math.Max(blank.Height * .75, 8);
                    StartCoverTextEdit(pageIdx, blank, "", blankFont, "Segoe UI", syInv, turn: turn);
                    return;
                }

                if (canvasWords.Count == 0)
                {
                    // Scanned / image-only page: no text layer to detect. Fall back to a manual edit -
                    // drop a cover + empty text box at the click so the user can white out the scanned
                    // text and type over it by hand (resize the cover to fit).
                    double mf = Math.Max(_textFontSize * syInv, 8);   // current text size in canvas units
                    StartCoverTextEdit(pageIdx, new Rect(canvasPos.X, canvasPos.Y, 200, mf * 1.35), "", mf, "Segoe UI", syInv);
                    return;
                }

                // The words of the line the click lands on: the same row, the contiguous run around the click,
                // and only words near the nearest word's size, so a drop numeral beside it is left out.
                var lineWords = TextLineGathering.Gather(
                        [.. canvasWords.Select(cw => new TextLineGathering.Candidate(
                            cw.Word.Text, cw.Rect, cw.Word.Letters.Count > 0 ? cw.Word.Letters[0].PointSize : 0))],
                        readingPos)
                    .Select(index => canvasWords[index])
                    .ToList();

                if (lineWords.Count == 0)
                {
                    SetStatus(Loc("Str_St_NoTextLine"));
                    return;
                }
                // Compute the line's bounding box in the reading frame
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
                var lineRect = TextTurn.FromReading(
                    new Rect(cLeft, cTop, Math.Max(1, cWidth), Math.Max(1, cHeight)), turn);
                if (_annotations.TryGetValue(pageIdx, out var coveredPage)
                    && coveredPage.OfType<CoverAnnotation>().Any(c => c.Bounds.IntersectsWith(lineRect)))
                {
                    SetStatus(Loc("Str_St_LineAlreadyEdited"));
                    return;
                }

                // Get actual font info from engine letter data
                double canvasFontSize = cHeight * 0.75; // fallback
                string fontName = "Segoe UI"; // fallback
                bool fontBold = false;
                bool fontItalic = false;
                double? sourceLeft = null;
                double? sourceBaseline = null;
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

                        // In the reading frame the letter's origin is the left edge and baseline of
                        // the replacement, whichever way the text runs on the canvas.
                        Point baselineStart = TextTurn.ToReading(
                            ToCanvas(letter.StartBaseLine.X, letter.StartBaseLine.Y), turn);
                        sourceLeft = baselineStart.X;
                        sourceBaseline = baselineStart.Y;

                        // Font family from the LETTER, never the word (#166, thanks Ryokoxx):
                        // Word.FontName joins its letters' names ("Helvetica Helvetica Helvetica
                        // ..."), which FontFamily cannot resolve - so that fallback landed on the
                        // default font, exactly where no fallback at all would have. The outer
                        // catch already covers a read that throws, so this needs no inner one.
                        string? rawFont = letter.FontName;
                        if (!string.IsNullOrEmpty(rawFont))
                        {
                            var detected = PdfFontStyle.FromPdfName(rawFont!);
                            fontName = detected.Family;
                            fontBold = detected.Bold;
                            fontItalic = detected.Italic;
                        }
                    }
                }
                catch { /* use fallbacks */ }

                // Preserve the detected point size. Canvas scaling already converts PDF points
                // to the editor's coordinates, so applying another correction shrinks the text.
                // Scanned PDFs with a broken glyph->Unicode map extract as mojibake; in that case start the
                // box empty (like a manual edit) instead of pre-filling garbage - the user types over the
                // whited-out original.
                string prefill = LooksGarbled(lineText) ? "" : lineText;
                fontName = PdfFontStyle.ResolveInstalledFamily(fontName, MainWindow.SystemFontNames);
                double boxFontCanvas = Math.Max(canvasFontSize, syInv);
                double editorLeft = sourceLeft is double origin
                    ? PdfTextEditPlacement.LeftFromOrigin(origin, 1, 2, cLeft)
                    : cLeft;
                double editorTop = cTop;
                try
                {
                    if (sourceBaseline is double baseline)
                    {
                        var family = new FontFamily(fontName);
                        editorTop = PdfTextEditPlacement.TopFromBaseline(
                            baseline, family.Baseline, boxFontCanvas, 1, cTop);
                    }
                }
                catch { /* retain the extracted glyph top */ }
                // The line is rewritten in the page content itself when the engine can do it: its old
                // glyphs leave the content stream and the new text takes their place, in the same font
                // when that font can show it. Only text the page does not really draw, such as the
                // recognized layer over a scan, falls back to a cover with new text on top.
                var (pageLeft, pageBottom, pageRight, pageTop) = RedactionGeometry.CanvasToPage(lineRect,
                    pdfW, pdfH, renderW, renderH, rotation);
                var region = new EngineDocs.PdfContentBounds(pageLeft - 0.5, pageBottom - 0.5,
                    pageRight + 0.5, pageTop + 0.5);
                EngineDocs.PdfTextLineInspection? inspection =
                    PdfEngineIntegration.InspectTextLine(_currentFile, pageIdx, region);
                if (inspection is { CanEdit: true })
                {
                    StartDirectTextEdit(pageIdx, new Rect(cLeft, cTop, cWidth, cHeight), prefill,
                        boxFontCanvas, fontName, syInv, fontBold, fontItalic, editorLeft, editorTop, turn, region);
                    SetStatus(string.Format(Loc("Str_St_TextEditInPage"), _textFontSize.ToString("0.##")));
                    return;
                }
                ScanLineFit? scanFit = inspection?.Blocker == EngineDocs.PdfTextLineBlocker.InvisibleText && turn == 0
                    && prefill.Length > 0
                    ? MeasureScanLine(pageIdx, [.. lineWords.Select(w => w.Word.Text)], [.. lineWords.Select(w => w.Rect)])
                    : null;
                StartCoverTextEdit(pageIdx, new Rect(cLeft, cTop, cWidth, cHeight), prefill,
                    scanFit?.FontSize ?? boxFontCanvas, scanFit?.Family ?? fontName, syInv,
                    scanFit?.Bold ?? fontBold, scanFit is null && fontItalic, editorLeft, editorTop, turn,
                    scanFit?.Background, scanFit?.Ink);
                if (scanFit is not null && _pendingCover is not null)
                    _pendingScanLine = new PendingScanLine(_pendingCover.PairId,
                        [.. lineWords.Select(w => w.Word.Text)], [.. lineWords.Select(w => w.Rect)]);
                SetStatus(Loc(inspection?.Blocker == EngineDocs.PdfTextLineBlocker.InvisibleText
                    ? "Str_St_TextEditCoverScan" : "Str_St_TextEditCoverOther"));
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_St_TextEditError"), ex.Message));
            }
        }

        private sealed record ScanLineFit(Color Background, Color Ink, string Family, double FontSize, bool Bold);
        private sealed record PendingScanLine(string PairId, string[] Words, Rect[] Boxes);
        private PendingScanLine? _pendingScanLine;
        private static readonly Lazy<IReadOnlyList<EngineDocs.PdfScanTextFace>> ScanFaces = new(() =>
        {
            HashSet<string> bundled = [.. EngineDocs.PdfScanTextWriter.BundledFaces().Select(face => face.Family)];
            // The editor draws with installed fonts, so the engine's bundled faces are not offered.
            return [.. PdfEngineIntegration.ScanTextFaces().Where(face => !bundled.Contains(face.Family))];
        });

        // Measures a scanned line's colors, size and closest installed typeface from the page bitmap.
        // A line that cannot be measured returns null and keeps the sampled cover and default look.
        private ScanLineFit? MeasureScanLine(int pageIdx, string[] words, Rect[] boxes)
        {
            try
            {
                if (PageBitmapFor(pageIdx) is not { } bitmap || !_renderDims.TryGetValue(pageIdx, out var rd)
                    || rd.w <= 0 || rd.h <= 0 || ScanFaces.Value.Count == 0) return null;
                var bgra32 = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int width = bgra32.PixelWidth, height = bgra32.PixelHeight;
                byte[] pixels = new byte[width * height * 4];
                bgra32.CopyPixels(pixels, width * 4, 0);
                // Transparent pixels are the white page, as in ReadBgraPixel.
                for (int p = 0; p < pixels.Length; p += 4)
                {
                    int alpha = pixels[p + 3];
                    for (int channel = 0; channel < 3; channel++)
                        pixels[p + channel] = (byte)((pixels[p + channel] * alpha + 255 * (255 - alpha)) / 255);
                    pixels[p + 3] = 255;
                }
                double pixelsX = width / (double)rd.w, pixelsY = height / (double)rd.h;
                EngineDocs.PdfOcrPixelWord[] pixelWords = [.. words.Select((word, index) =>
                    ScanLineEdit.ToPixelWord(word, boxes[index], pixelsX, pixelsY))];
                if (EngineDocs.PdfScanTextAnalyzer.MeasureLine(pixels, width, height, pixelWords) is not { } look)
                    return null;
                EngineDocs.PdfScanTextFace face = EngineDocs.PdfScanTextWriter.ChooseFace(
                    new EngineDocs.PdfScanTextPageAnalysis(width, height, false, look.Words), ScanFaces.Value);
                double size = EngineDocs.PdfScanTextWriter.FontSizeFor(face, look.XHeight, look.CapHeight,
                    look.CapitalsOnly, 1 / pixelsY);
                static Color ToColor((byte Red, byte Green, byte Blue) value) =>
                    Color.FromRgb(value.Red, value.Green, value.Blue);
                return size > 0 ? new ScanLineFit(ToColor(look.BackgroundColor), ToColor(look.InkColor),
                    face.Family, size, look.Bold) : null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                or NotSupportedException or FormatException)
            {
                return null;
            }
        }

        // Drops an opaque cover at the given line and opens an editable text box on top of it - the two
        // halves of an in-place edit. Used for a detected PDF-text line and, on a scanned page with no
        // text layer, for a manual edit at the click point. boxFontCanvas is the on-canvas font size;
        // the cover fill and text ink are sampled from the page so the edit blends in.
        // lineRect and the editor origin are in the reading frame of 'turn' (the canvas itself for level
        // text); the cover is placed over the line on the canvas and the box opens turned to match.
        private void StartCoverTextEdit(int pageIdx, Rect lineRect, string text, double boxFontCanvas,
                                        string fontName, double syInv, bool bold = false, bool italic = false,
                                        double? editorLeft = null, double? editorTop = null, int turn = 0,
                                        Color? measuredBackground = null, Color? measuredInk = null)
        {
            double cLeft = lineRect.X, cTop = lineRect.Y, cWidth = lineRect.Width;
            Rect canvasLine = TextTurn.FromReading(lineRect, turn);
            // Pair id shared with the replacement text - the cover renders dashed while paired.
            var cover = new CoverAnnotation
            {
                PageIndex = pageIdx,
                PairId = Guid.NewGuid().ToString("N"),
                Bounds = new Rect(canvasLine.X - 3, canvasLine.Y - 3, canvasLine.Width + 6, canvasLine.Height + 6)
            };
            var sampleRect = canvasLine;
            Color coverBg = measuredBackground ?? SampleCoverColor(pageIdx, sampleRect);
            Color inkColor = measuredInk ?? SampleTextColor(pageIdx, sampleRect, coverBg);
            cover.SetColor(coverBg);
            RememberTextToolDefaults();
            _textColor = inkColor; _textOpacity = inkColor.A;
            _textFontSize = Math.Max(1, boxFontCanvas / syInv);   // canvas units -> points
            // Replacing raw PDF text starts from the detected font and its face styling. PDF fonts
            // encode bold and italic in the font name, so resetting these flags made every detected
            // line plain as soon as it was double-clicked (#182).
            _textFontName = string.IsNullOrEmpty(fontName) ? "Segoe UI" : fontName;
            _textBold = bold;
            _textItalic = italic;
            _textStrike = _textUnderline = false;
            _textLetterSpacing = 0;
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
            if (TextTurn.Normalize(turn) == 0)
            {
                Canvas.SetLeft(tb, editorLeft ?? cLeft);
                Canvas.SetTop(tb, editorTop ?? cTop);
            }
            else PlaceTurnedEditBox(tb, TextTurn.FromReading(new Point(editorLeft ?? cLeft, editorTop ?? cTop), turn), turn);
            TextEditorLayerFor(_activeCanvas).Children.Add(tb);
            _activeTextBox = tb;
            StyleEditBox(tb);
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.LostFocus += TextBox_LostFocus;
            tb.Loaded += (_, _) => QueueTextEditorFocus(tb, selectAll: true);
            tb.PreviewMouseLeftButtonDown += (_, _) => QueueTextEditorFocus(tb);
            QueueTextEditorFocus(tb, selectAll: true);
            ShowTextSettings();
            SetStatus(Loc("Str_St_EditingText"));
        }

        // A line of the page's own text being edited in place: where it is, what it said and how it
        // looked when the edit began, so the commit changes only what the user changed.
        internal sealed record DirectTextEdit(int PageIndex, EngineDocs.PdfContentBounds Region, string Original,
            string FontName, bool Bold, bool Italic, double FontSize, Color Ink);

        private DirectTextEdit? _pendingDirectEdit;

        internal sealed record PendingTextRecovery(string RawText, TextAnnotation Annotation,
            DirectTextEdit? DirectEdit, CoverAnnotation? PendingCover, double FontSize,
            double LetterSpacing, Color Ink);

        // Capture before filtering dirty sessions, without committing or redrawing the editor.
        internal PendingTextRecovery? CapturePendingTextForRecovery()
        {
            if (_activeTextBox is not { } box) return null;
            if (string.IsNullOrWhiteSpace(box.Text) && _pendingDirectEdit is null
                && _pendingCover is null && _reeditOriginal is null) return null;
            int page = box.Tag is int index ? index : _currentPage;
            TextAnnotation annotation = BuildCommittedTextAnnotation(box, page, box.Text.Trim(),
                TurnedEditOrigin(box), _pendingCover?.PairId ?? _reeditOriginal?.PairId ?? "");
            return new PendingTextRecovery(box.Text, annotation, _pendingDirectEdit, _pendingCover,
                _textFontSize, _textLetterSpacing, _textColor);
        }

        internal string WriteRecoverySnapshot(PendingTextRecovery? pending, Func<string> writeCopy)
        {
            if (pending is null) return writeCopy();
            var originalAnnotations = _annotations;
            try
            {
                _annotations = originalAnnotations.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
                TextAnnotation annotation = pending.Annotation;
                if (pending.DirectEdit is null)
                {
                    if (!_annotations.TryGetValue(annotation.PageIndex, out var list))
                        _annotations[annotation.PageIndex] = list = [];
                    if (annotation.Content.Length > 0) list.Add(annotation);
                    else if (pending.PendingCover is not null) list.Remove(pending.PendingCover);
                }
                string copy = writeCopy();
                if (pending.DirectEdit is { } direct)
                {
                    var (request, _, _) = BuildDirectTextEditRequest(direct, pending.RawText, annotation.FontName,
                        annotation.Bold, annotation.Italic, pending.FontSize, pending.Ink,
                        pending.LetterSpacing, annotation.Underline, annotation.Strike);
                    if (request is not null) PdfEngineIntegration.ReplaceTextLine(copy, request);
                }
                return copy;
            }
            finally { _annotations = originalAnnotations; }
        }

        // Opens the editor for a line that the commit rewrites in the page content. Nothing is added to
        // the page while typing: the box takes the page color so the old line does not show through,
        // and committing replaces the line's glyphs in the content stream. lineRect and the editor
        // origin are in the reading frame of 'turn', as for StartCoverTextEdit.
        private void StartDirectTextEdit(int pageIdx, Rect lineRect, string text, double boxFontCanvas,
                                         string fontName, double syInv, bool bold, bool italic,
                                         double? editorLeft, double? editorTop, int turn,
                                         EngineDocs.PdfContentBounds region)
        {
            double cLeft = lineRect.X, cTop = lineRect.Y, cWidth = lineRect.Width;
            Rect canvasLine = TextTurn.FromReading(lineRect, turn);
            Color background = SampleCoverColor(pageIdx, canvasLine);
            Color inkColor = SampleTextColor(pageIdx, canvasLine, background);
            RememberTextToolDefaults();
            _textColor = inkColor; _textOpacity = inkColor.A;
            _textFontSize = Math.Max(1, boxFontCanvas / syInv);   // canvas units -> points
            _textFontName = string.IsNullOrEmpty(fontName) ? "Segoe UI" : fontName;
            _textBold = bold;
            _textItalic = italic;
            _textStrike = _textUnderline = false;
            _textLetterSpacing = 0;
            _pendingDirectEdit = new DirectTextEdit(pageIdx, region, text, _textFontName, bold, italic,
                _textFontSize, inkColor);

            var tb = new TextBox
            {
                Text = text,
                Background = new SolidColorBrush(background),   // hides the old line while typing
                Foreground = new SolidColorBrush(inkColor),
                BorderBrush = (SolidColorBrush)FindResource("PrimaryBrush"),
                SelectionBrush = AccentBrush(),
                CaretBrush = new SolidColorBrush(inkColor),
                Template = FlatTextBoxTemplate(),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily(_textFontName),
                FontSize = boxFontCanvas,
                MinWidth = Math.Max(cWidth + 20, 80),
                MinHeight = 24,
                Padding = new Thickness(2, 0, 2, 0),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,   // a line keeps its breaks; it does not reflow
                Tag = pageIdx
            };
            if (TextTurn.Normalize(turn) == 0)
            {
                Canvas.SetLeft(tb, editorLeft ?? cLeft);
                Canvas.SetTop(tb, editorTop ?? cTop);
            }
            else PlaceTurnedEditBox(tb, TextTurn.FromReading(new Point(editorLeft ?? cLeft, editorTop ?? cTop), turn), turn);
            TextEditorLayerFor(_activeCanvas).Children.Add(tb);
            _activeTextBox = tb;
            StyleEditBox(tb);
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.LostFocus += TextBox_LostFocus;
            tb.Loaded += (_, _) => QueueTextEditorFocus(tb, selectAll: true);
            tb.PreviewMouseLeftButtonDown += (_, _) => QueueTextEditorFocus(tb);
            QueueTextEditorFocus(tb, selectAll: true);
            ShowTextSettings();
        }

        // Rewrites the edited line in the page content. Only what the user changed is passed on: the
        // new text, and a size, color, spacing, typeface or rule that differs from the line as found.
        private void CommitDirectTextEdit(DirectTextEdit edit, string typed)
        {
            var (request, restyled, family) = BuildDirectTextEditRequest(edit, typed, _textFontName,
                _textBold, _textItalic, _textFontSize, _textColor, _textLetterSpacing, _textUnderline, _textStrike);
            if (request is null) return;
            ApplyDirectTextEdit(request, restyled, family);
        }

        private static (string Text, bool Restyled, bool Resized, bool Recolored, bool Spaced, bool Changed)
            DescribeDirectTextEdit(DirectTextEdit edit, string typed, string fontName, bool bold,
                bool italic, double fontSize, Color color, double letterSpacing, bool underline, bool strike)
        {
            string text = string.Join("\n", typed.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(line => line.TrimEnd())).Trim('\n');
            bool restyled = !string.Equals(fontName, edit.FontName, StringComparison.OrdinalIgnoreCase)
                || bold != edit.Bold || italic != edit.Italic;
            bool resized = Math.Abs(fontSize - edit.FontSize) > 0.01;
            bool recolored = color.R != edit.Ink.R || color.G != edit.Ink.G || color.B != edit.Ink.B;
            bool spaced = Math.Abs(letterSpacing) > 0.001;
            return (text, restyled, resized, recolored, spaced,
                text != edit.Original || restyled || resized || recolored || spaced || underline || strike);
        }

        private static (EngineDocs.PdfTextLineEdit? Request, bool Restyled, string? Family)
            BuildDirectTextEditRequest(DirectTextEdit edit, string typed, string fontName, bool bold,
                bool italic, double fontSize, Color color, double letterSpacing, bool underline, bool strike)
        {
            var (text, restyled, resized, recolored, spaced, changed) = DescribeDirectTextEdit(edit, typed,
                fontName, bold, italic, fontSize, color, letterSpacing, underline, strike);
            if (!changed) return (null, restyled, null);

            var substitute = PdfEngineIntegration.LoadTextFont(fontName, text, bold, italic);
            var request = new EngineDocs.PdfTextLineEdit(edit.PageIndex, edit.Region, text)
            {
                Substitute = substitute?.Font,
                UseSubstitute = restyled && substitute is not null,
                FontSize = resized ? fontSize : null,
                FillColor = recolored
                    ? new KillerPdf.Engine.Authoring.PdfRgbColor(color.R / 255d, color.G / 255d, color.B / 255d) : null,
                CharacterSpacing = spaced ? letterSpacing : null,
                Underline = underline,
                Strike = strike
            };
            return (request, restyled, substitute?.Family);
        }

        private void ApplyDirectTextEdit(EngineDocs.PdfTextLineEdit request, bool restyled, string? family)
        {
            EngineDocs.PdfTextLineEditResult? result = null;
            Exception? failure = null;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                SaveTempAndReload(keepAnnotations: true, preserveZoom: true, finalizeSavedFile: path =>
                {
                    try { result = PdfEngineIntegration.ReplaceTextLine(path, request); }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                        or FormatException or ArgumentException or IOException) { failure = ex; }
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { failure ??= ex; }
            finally { Mouse.OverrideCursor = null; }

            if (failure is not null || result is null)
            {
                ReportResult(string.Format(Loc("Str_St_TextEditFailed"), failure?.Message ?? string.Empty));
                return;
            }
            string done = result.Font == EngineDocs.PdfTextLineFont.Original
                ? Loc("Str_St_TextEditedInPage")
                : string.Format(Loc(restyled ? "Str_St_TextEditedChosenFont" : "Str_St_TextEditedSubstitute"),
                    family ?? _textFontName);
            ReportResult(done);
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
        private SolidColorBrush TextEditBackground()
            => _textFillColor.A > 0 ? new SolidColorBrush(_textFillColor)
                                    : new SolidColorBrush(Color.FromArgb(64, 128, 128, 128));

        // True when 'pos' (in _activeCanvas coordinates) falls inside the text box currently being
        // edited AND that box lives on _activeCanvas. Used so a click inside the box doesn't get
        // treated as a request to place a new one (the Grid-view "box jumps to cursor" bug).
        private bool ClickInsideActiveTextBox(Point pos)
        {
            if (_activeTextBox is null
                || !ReferenceEquals(_activeTextBox.Parent, TextEditorLayerFor(_activeCanvas))) return false;
            double x = Canvas.GetLeft(_activeTextBox), y = Canvas.GetTop(_activeTextBox);
            if (double.IsNaN(x) || double.IsNaN(y)) return false;
            Size footprint = EditBoxFootprint(_activeTextBox);
            return pos.X >= x && pos.X <= x + footprint.Width && pos.Y >= y && pos.Y <= y + footprint.Height;
        }

        // The turn of an edit box, from the layout transform that turned it (0 for a level box).
        private static int EditBoxTurn(TextBox tb) =>
            tb.LayoutTransform is RotateTransform turned ? TextTurn.Normalize((int)Math.Round(turned.Angle)) : 0;

        // The edit box's own size: width along its lines, height across them.
        private static Size EditBoxSize(TextBox tb) => new(
            tb.ActualWidth > 0 ? tb.ActualWidth : tb.Width,
            tb.ActualHeight > 0 ? tb.ActualHeight : Math.Max(tb.MinHeight, 24));

        // The space the edit box takes on the canvas.
        private static Size EditBoxFootprint(TextBox tb)
        {
            Size size = EditBoxSize(tb);
            return TextTurn.Footprint(EditBoxTurn(tb), size.Width, size.Height);
        }

        // Frame origins of turned edit boxes: the canvas point where the first line starts.
        private readonly ConditionalWeakTable<TextBox, StrongBox<Point>> _turnedEditOrigins = [];

        // Opens an edit box turned to read like the text it edits. A turned box grows across its
        // lines toward a different canvas side than a level one, so it is re-placed from its frame
        // origin whenever its size changes, keeping the first line where it started.
        private void PlaceTurnedEditBox(TextBox tb, Point origin, int turn)
        {
            tb.LayoutTransform = new RotateTransform(TextTurn.Normalize(turn));
            _turnedEditOrigins.AddOrUpdate(tb, new StrongBox<Point>(origin));
            tb.SizeChanged += (_, _) => PlaceTurnedEditBoxFromOrigin(tb);
            PlaceTurnedEditBoxFromOrigin(tb);
        }

        private void PlaceTurnedEditBoxFromOrigin(TextBox tb)
        {
            if (!_turnedEditOrigins.TryGetValue(tb, out var origin)) return;
            Size size = EditBoxSize(tb);
            Point topLeft = origin.Value - TextTurn.OriginOffset(EditBoxTurn(tb), size.Width, size.Height);
            Canvas.SetLeft(tb, topLeft.X);
            Canvas.SetTop(tb, topLeft.Y);
            if (ReferenceEquals(_tehBox, tb)) LayoutTextEditHandles();
        }

        // Records where a turned edit box's first line starts after its corner was dragged.
        private void UpdateTurnedEditOrigin(TextBox tb, Point topLeft, Size size)
        {
            if (_turnedEditOrigins.TryGetValue(tb, out var origin))
                origin.Value = topLeft + TextTurn.OriginOffset(EditBoxTurn(tb), size.Width, size.Height);
        }

        private static double DistanceToRect(Rect rect, Point point)
        {
            double dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
            double dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void PlaceTextBox(Point pos, int pageIdx)
        {
            // _textFontSize is a point size; convert to the page's canvas (render-dim) units so
            // it renders and exports as real points. DrawAnnotationsOnDocument multiplies by
            // sy = page points per canvas unit, so dividing by sy here makes "14" export as 14pt.
            double fontCanvas = _textFontSize;
            if (_doc is not null && _renderDims.TryGetValue(pageIdx, out var rdims) && rdims.h > 0)
            {
                double sy = PointsPerCanvasUnit(pageIdx, rdims.h);
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            // Center the first line on the I-beam hotspot. The TextBox top edge previously started at
            // the hotspot, which made the typed text appear about half a line below the click.
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
                Width = TextBoxDefaultWidth(pageIdx),
                MinHeight = 24,
                Padding = new Thickness(2),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Tag = pageIdx
            };
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, EditableTextShortcutPolicy.TextBoxTop(pos.Y, fontCanvas));
            TextEditorLayerFor(_activeCanvas).Children.Add(tb);
            _activeTextBox = tb;
            StyleEditBox(tb);   // current typeface + B/I/S
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.LostFocus += TextBox_LostFocus;
            // Input priority runs after the placement click has completed, so that click cannot
            // immediately take focus back from the new editor.
            tb.Loaded += (_, _) => QueueTextEditorFocus(tb);
            tb.PreviewMouseLeftButtonDown += (_, _) => QueueTextEditorFocus(tb);
            QueueTextEditorFocus(tb);
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
                TextEditorLayerFor(_activeCanvas).Children.Add(hd);
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
            if (EditBoxTurn(_tehBox) is 90 or 270) (w, h) = (h, w);
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
            _tehBox?.SizeChanged -= TextEditBox_SizeChanged;
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

        // Enter inserts a line break. Ctrl+Enter commits the text box without requiring a pointer click.
        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelActiveTextEdit();
                e.Handled = true;
            }
            else if (EditableTextShortcutPolicy.BacksOutOfEdit(e.Key, Keyboard.Modifiers)
                     && sender is TextBox ztb && !ztb.CanUndo)
            {
                // The box has no typed text left to undo, so Ctrl+Z backs out of the whole in-place edit
                // (same as Escape) instead of being a no-op - otherwise a fresh edit could only be undone
                // after committing it. While the box still has edits to undo, WPF's TextBox handles Ctrl+Z.
                CancelActiveTextEdit();
                e.Handled = true;
            }
            else if (EditableTextShortcutPolicy.CommitsTextBox(e.Key, Keyboard.Modifiers))
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
            RestoreTextToolDefaults();
            RemoveTextEditHandles();
            RemoveReeditCoverOutline();   // edit canceled; drop the cover hint (repaint follows)
            if (_activeTextBox is not null)
            {
                RemoveFromParent(_activeTextBox);
                _activeTextBox = null;
            }
            // Canceling an existing-text edit drops the cover too (it was placed un-undone).
            if (_pendingCover is not null) DiscardPendingCover();
            _pendingDirectEdit = null;   // the page was never changed, so nothing to put back
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
            if (_activeTextBox is not null && (!string.IsNullOrWhiteSpace(_activeTextBox.Text) || _pendingCover is not null
                || _pendingDirectEdit is not null))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // A ComboBox popup has its own visual tree, so its focused item is not a
                    // descendant of the settings bar. Check the owning ComboBox as well.
                    if (_textSettingsBar is not null)
                    {
                        bool focusInBar = Keyboard.FocusedElement is DependencyObject fe
                            && IsDescendantOf(fe, _textSettingsBar);
                        if (focusInBar || ContainsOpenComboBox(_textSettingsBar)) return;
                    }
                    CommitActiveTextBox();
                }),
                System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private static bool ContainsOpenComboBox(DependencyObject root)
        {
            if (root is ComboBox { IsDropDownOpen: true }) return true;
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
                if (ContainsOpenComboBox(VisualTreeHelper.GetChild(root, i))) return true;
            return false;
        }

        // A turned box keeps its first line where the editor showed it, even when the committed
        // height differs from the editor's.
        private Point TurnedEditOrigin(TextBox tb)
        {
            Size editSize = EditBoxSize(tb);
            return new Point(Canvas.GetLeft(tb), Canvas.GetTop(tb))
                + TextTurn.OriginOffset(EditBoxTurn(tb), editSize.Width, editSize.Height);
        }

        // The annotation an editor commits to. Committing and render-failure recovery share it, so a
        // recovered box lands exactly where committing it would have put it.
        private TextAnnotation BuildCommittedTextAnnotation(TextBox tb, int pageIdx, string content,
            Point turnedOrigin, string pairId)
        {
            int turn = EditBoxTurn(tb);
            double boxW = (!double.IsNaN(tb.Width) && tb.Width > 0) ? tb.Width
                        : (tb.ActualWidth > 0 ? tb.ActualWidth : TextBoxDefaultWidth(pageIdx));
            var ta = new TextAnnotation
            {
                PageIndex = pageIdx,
                Position = new Point(Canvas.GetLeft(tb), Canvas.GetTop(tb)),
                Content = content,
                FontSize = tb.FontSize,
                LetterSpacing = CanvasLetterSpacing(pageIdx),
                FontName = _textFontName,
                Bold = _textBold,
                Italic = _textItalic,
                Strike = _textStrike,
                Underline = _textUnderline,
                Width = boxW,
                Rotation = turn,
                PairId = pairId
            };
            ta.SetColor(tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black);
            // A cover-paired edit gets no fill of its own - the opaque cover behind it is the backdrop.
            ta.SetFill(_pendingCover is not null ? Colors.Transparent : _textFillColor);
            // Free-form height if the box was manually resized; otherwise fit to the wrapped text.
            ta.Height = (!double.IsNaN(tb.Height) && tb.Height > 0)
                ? tb.Height
                : MeasureTextBoxHeight(content, boxW, tb.FontSize);
            if (turn != 0) ta.Position = turnedOrigin - TextTurn.OriginOffset(turn, ta.Width, ta.Height);
            // Keep the placed box fully on-page so its corners (and resize handles) stay reachable.
            ta.Position = ClampRectToPage(pageIdx, new Rect(ta.Position, ta.FootprintSize)).Location;
            return ta;
        }

        // Editing a line of the page borrows its look for the edit box and the text bar. The tool's
        // own settings return when the edit ends, so page ink never becomes the default for new text.
        // Selecting a replacement for edited page text borrows the same way until it is deselected.
        private sealed record TextToolDefaults(Color Color, byte Opacity, double FontSize, string FontName,
            bool Bold, bool Italic, bool Strike, bool Underline, double LetterSpacing, Color Fill);
        private TextToolDefaults? _defaultsBeforePageEdit;
        private TextToolDefaults? _selectedTextLook;   // the look the selection put on the bar, when it borrowed

        private TextToolDefaults CurrentTextToolDefaults() => new(_textColor, _textOpacity,
            _textFontSize, _textFontName, _textBold, _textItalic, _textStrike, _textUnderline, _textLetterSpacing,
            _textFillColor);

        // A page edit that starts while a replacement is still selected takes over the borrowed look.
        private void RememberTextToolDefaults()
        {
            _defaultsBeforePageEdit ??= CurrentTextToolDefaults();
            _selectedTextLook = null;
        }

        private void RestoreTextToolDefaults(bool refreshBar = true)
        {
            if (_defaultsBeforePageEdit is not { } saved) return;
            _defaultsBeforePageEdit = null;
            (_textColor, _textOpacity, _textFontSize, _textFontName) = (saved.Color, saved.Opacity, saved.FontSize, saved.FontName);
            (_textBold, _textItalic, _textStrike, _textUnderline) = (saved.Bold, saved.Italic, saved.Strike, saved.Underline);
            (_textLetterSpacing, _textFillColor) = (saved.LetterSpacing, saved.Fill);
            if (refreshBar && _currentTool == EditTool.Text) ShowTextSettings();
        }

        // Selecting a replacement for edited page text puts its look on the bar so it can be restyled.
        // Deselecting returns the tool's own look, except for anything the user changed on the bar
        // meanwhile, which is a deliberate choice. A page edit that is open owns the borrowed look.
        // The caller records _selectedTextLook once the bar is synced, because that is what release
        // compares against: recording it here would hold the tool's look and restore nothing.
        private bool BorrowSelectedTextLook()
        {
            if (_defaultsBeforePageEdit is not null) return false;
            RememberTextToolDefaults();
            return true;
        }

        private void ReleaseSelectedTextLook(bool refreshBar = true)
        {
            if (_selectedTextLook is not { } synced) return;
            _selectedTextLook = null;
            if (_activeTextBox is not null || _defaultsBeforePageEdit is not { } saved) return;
            _defaultsBeforePageEdit = null;
            // The opacity slider writes into _textColor too, so a new opacity is not a new ink.
            bool sameInk = _textColor.R == synced.Color.R && _textColor.G == synced.Color.G && _textColor.B == synced.Color.B;
            if (sameInk) _textColor = saved.Color;
            if (_textOpacity == synced.Opacity) _textOpacity = saved.Opacity;
            _textColor = Color.FromArgb(_textOpacity, _textColor.R, _textColor.G, _textColor.B);
            if (_textFontSize == synced.FontSize) _textFontSize = saved.FontSize;
            if (_textFontName == synced.FontName) _textFontName = saved.FontName;
            if (_textBold == synced.Bold) _textBold = saved.Bold;
            if (_textItalic == synced.Italic) _textItalic = saved.Italic;
            if (_textStrike == synced.Strike) _textStrike = saved.Strike;
            if (_textUnderline == synced.Underline) _textUnderline = saved.Underline;
            if (_textLetterSpacing == synced.LetterSpacing) _textLetterSpacing = saved.LetterSpacing;
            if (_textFillColor == synced.Fill) _textFillColor = saved.Fill;
            if (refreshBar && _currentTool == EditTool.Text) ShowTextSettings();
        }

        // For a window that is about to restart: puts the tool's own look back so persisting the tool
        // settings cannot save page ink. It leaves the bar alone because nothing may draw there.
        internal void RestoreTextToolDefaultsForExit()
        {
            ReleaseSelectedTextLook(refreshBar: false);
            RestoreTextToolDefaults(refreshBar: false);
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox is null) return;
            try { CommitTextBoxContents(); }
            finally { RestoreTextToolDefaults(); }
        }

        private void CommitTextBoxContents()
        {
            var tb = _activeTextBox!;
            _activeTextBox = null;
            RemoveTextEditHandles();
            RemoveReeditCoverOutline();   // the re-edit is ending; drop its cover hint (repaint follows)
            string reeditPair = _reeditOriginal?.PairId ?? "";   // preserve a re-edited text's cover pairing
            _reeditOriginal = null;   // committing replaces any annotation being re-edited

            string content = tb.Text.Trim();
            int pageIdx = tb.Tag is int idx ? idx : _currentPage;
            Point turnedOrigin = TurnedEditOrigin(tb);

            // Remove the editing box from whatever canvas actually parents it. _activeCanvas may have
            // moved to another page when the user clicked away to commit (continuous/grid), and a
            // re-looked-up page canvas can be a different instance than the one the box was placed on,
            // either of which leaves the box orphaned (visible, but immune to Delete/Clear All). Its
            // live Parent is the correct host.
            RemoveFromParent(tb);

            if (_pendingDirectEdit is { } direct)
            {
                // A line of the page's own text: the page content changes, no annotation is added.
                _pendingDirectEdit = null;
                CommitDirectTextEdit(direct, tb.Text);
                if (_currentTool != EditTool.Text) HideTextSettings();
                return;
            }

            PendingScanLine? scanLine = _pendingScanLine;
            _pendingScanLine = null;
            if (scanLine is not null && _pendingCover?.PairId == scanLine.PairId && content.Length > 0)
            {
                // A scanned line keeps the words the edit left alone: only the changed ones are covered.
                string family = _textFontName;
                bool bold = _textBold, italic = _textItalic;
                double fontSize = tb.FontSize;
                ScanLineEditPlan? plan = ScanLineEdit.Plan(scanLine.Words, scanLine.Boxes, content,
                    text => MeasureLineWidth(text, family, bold, italic, fontSize));
                if (plan is null)
                {
                    DiscardPendingCover();
                    if (_currentTool != EditTool.Text) HideTextSettings();
                    return;
                }
                _pendingCover!.Bounds = plan.Cover;
                if (plan.Text.Length == 0)
                {
                    PushUndo(new UndoEntry(UndoKind.AnnotationGroup, pageIdx,
                        WasDirty: _pendingEditWasDirty, AnnotGroup: [_pendingCover]));
                    _pendingCover = null;
                    MarkDirty();
                    RenderAllAnnotations(pageIdx);
                    if (_currentTool != EditTool.Text) HideTextSettings();
                    return;
                }
                content = plan.Text;
                Canvas.SetLeft(tb, Canvas.GetLeft(tb) + plan.Covered.Left - scanLine.Boxes[0].Left);
                tb.Width = Math.Max(plan.Covered.Width, MeasureLineWidth(content, family, bold, italic, fontSize) + 12);
            }

            if (!string.IsNullOrEmpty(content))
            {
                // Carry the pairing so the cover knows its partner text exists (renders dashed).
                TextAnnotation ta = BuildCommittedTextAnnotation(tb, pageIdx, content, turnedOrigin,
                    _pendingCover is not null ? _pendingCover.PairId : reeditPair);
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
                KillerDialog.Show(Host!.Window, string.Format(Loc("Str_Font_NoGlyphs"), missing), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* the warning must never be the thing that breaks placing text */ }
        }

        // Remove the not-yet-committed cover when an existing-text edit is canceled or left empty. The
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
            if (pageIdx == _currentPage && PageImage.Source is System.Windows.Media.Imaging.BitmapSource pbs)
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
