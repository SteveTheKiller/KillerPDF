using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // PDFium text-selection engine
        //
        // Chrome-style character selection needs: char-index-at-a-point, per-char boxes, the selection
        // rectangles for a char range (for glyph-level highlighting), and range->text. PdfPig (used for
        // the old box/word marquee) exposes none of these conveniently; PDFium's FPDFText_* API is built
        // for exactly this and reads object-stream PDFs (same reason links moved to PDFium). We reuse the
        // cached document handle from Links.cs (_linkPdfiumDoc via EnsureLinkPdfiumDoc) and cache one text
        // page for the active document page on top of it. The page + text-page handles are children of the
        // cached doc, so CloseLinkPdfiumDoc() also closes them (see the CloseTextPage() call there).
        //
        // Coordinates: PDFium text coords are PDF points, origin bottom-left, Y up. Canvas coords are the
        // page's render-dim space (the same space _renderDims and the link overlays use), origin top-left,
        // Y down. CanvasToPdfPoint / PdfRectToCanvasRect below convert between them.
        // ============================================================

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFText_ClosePage(IntPtr textPage);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_CountChars(IntPtr textPage);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFText_GetRect(IntPtr textPage, int rectIndex, out double left, out double top, out double right, out double bottom);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFText_GetText(IntPtr textPage, int startIndex, int count, [Out] ushort[] result);

        // Cached text page for the active document page. Both handles are children of _linkPdfiumDoc, so
        // CloseLinkPdfiumDoc() calls CloseTextPage(). Only touched on the UI thread (Select-tool handlers).
        private IntPtr _textPage        = IntPtr.Zero;   // FPDFText text-page handle
        private IntPtr _textPageParent  = IntPtr.Zero;   // the FPDF_LoadPage handle backing _textPage
        private int    _textPageIndex   = -1;

        /// <summary>
        /// Loads (and caches) the PDFium text page for pageIndex, reusing the shared document handle.
        /// Returns IntPtr.Zero if unavailable. Reopens when the page or the working file changes.
        /// </summary>
        private IntPtr EnsureTextPage(int pageIndex)
        {
            if (_textPage != IntPtr.Zero && _textPageIndex == pageIndex && _linkPdfiumDocPath == _currentFile)
                return _textPage;

            CloseTextPage();
            IntPtr doc = EnsureLinkPdfiumDoc();
            if (doc == IntPtr.Zero) return IntPtr.Zero;

            IntPtr page = FPDF_LoadPage(doc, pageIndex);
            if (page == IntPtr.Zero) return IntPtr.Zero;
            IntPtr textPage = FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero) { FPDF_ClosePage(page); return IntPtr.Zero; }

            _textPageParent = page;
            _textPage       = textPage;
            _textPageIndex  = pageIndex;
            return _textPage;
        }

        /// <summary>Closes the cached text page + its backing page handle. Called on page/doc change and
        /// from CloseLinkPdfiumDoc() (the text page is a child of the doc it was loaded from).</summary>
        private void CloseTextPage()
        {
            if (_textPage != IntPtr.Zero)
            {
                try { FPDFText_ClosePage(_textPage); } catch { }
                _textPage = IntPtr.Zero;
            }
            if (_textPageParent != IntPtr.Zero)
            {
                try { FPDF_ClosePage(_textPageParent); } catch { }
                _textPageParent = IntPtr.Zero;
            }
            _textPageIndex = -1;
        }

        // --- coordinate conversion (canvas render-dim space <-> PDF points) ---

        // PDF page size in points for the cached text page, or A4 fallback.
        private (double w, double h) TextPagePdfSize()
        {
            if (_textPageParent == IntPtr.Zero) return (595.28, 841.89);
            double w = FPDF_GetPageWidth(_textPageParent);
            double h = FPDF_GetPageHeight(_textPageParent);
            if (w <= 0) w = 595.28;
            if (h <= 0) h = 841.89;
            return (w, h);
        }

        // Canvas point (render-dim units) -> PDF point (points, origin bottom-left).
        private bool CanvasToPdfPoint(int pageIndex, Point canvas, out double pdfX, out double pdfY)
        {
            pdfX = pdfY = 0;
            if (!_renderDims.TryGetValue(pageIndex, out var rd) || rd.w <= 0 || rd.h <= 0) return false;
            var (pw, ph) = TextPagePdfSize();
            pdfX = canvas.X * (pw / rd.w);
            pdfY = ph - canvas.Y * (ph / rd.h);   // flip Y
            return true;
        }

        // PDF rect (left/top/right/bottom in points, Y up) -> canvas Rect (render-dim units, Y down).
        private Rect PdfRectToCanvasRect(int pageIndex, double left, double top, double right, double bottom)
        {
            if (!_renderDims.TryGetValue(pageIndex, out var rd)) return Rect.Empty;
            var (pw, ph) = TextPagePdfSize();
            double x = left  / pw * rd.w;
            double y = (ph - top) / ph * rd.h;
            double w = (right - left) / pw * rd.w;
            double h = (top - bottom) / ph * rd.h;
            return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
        }

        // --- query helpers (operate on the cached text page for pageIndex) ---

        // Char index under a canvas point, or -1. Tolerance is a few PDF points so a near-miss still hits.
        private int TextCharAtCanvasPoint(int pageIndex, Point canvas, double tolPts = 4.0)
        {
            IntPtr tp = EnsureTextPage(pageIndex);
            if (tp == IntPtr.Zero) return -1;
            if (!CanvasToPdfPoint(pageIndex, canvas, out double px, out double py)) return -1;
            int idx = FPDFText_GetCharIndexAtPos(tp, px, py, tolPts, tolPts);
            return idx < 0 ? -1 : idx;
        }

        // Expands a char index to the word around it as a [start, endExclusive) range, using whitespace
        // as the boundary. Returns (start, count); count 0 if the index isn't on a word char.
        private (int start, int count) TextWordRangeAt(int pageIndex, int charIndex)
        {
            IntPtr tp = EnsureTextPage(pageIndex);
            if (tp == IntPtr.Zero || charIndex < 0) return (charIndex, 0);
            int total = FPDFText_CountChars(tp);
            if (charIndex >= total) return (charIndex, 0);

            static bool IsWordChar(uint u) => u != 0 && !char.IsWhiteSpace((char)u);
            if (!IsWordChar(FPDFText_GetUnicode(tp, charIndex))) return (charIndex, 0);

            int start = charIndex;
            while (start > 0 && IsWordChar(FPDFText_GetUnicode(tp, start - 1))) start--;
            int end = charIndex;
            while (end + 1 < total && IsWordChar(FPDFText_GetUnicode(tp, end + 1))) end++;
            return (start, end - start + 1);
        }

        // Selection rectangles (canvas render-dim space) for a char range - one per visual line run.
        private List<Rect> TextRangeRects(int pageIndex, int start, int count)
        {
            var rects = new List<Rect>();
            IntPtr tp = EnsureTextPage(pageIndex);
            if (tp == IntPtr.Zero || count <= 0) return rects;
            int n = FPDFText_CountRects(tp, start, count);
            for (int i = 0; i < n; i++)
                if (FPDFText_GetRect(tp, i, out double l, out double t, out double r, out double b))
                {
                    var rect = PdfRectToCanvasRect(pageIndex, l, t, r, b);
                    if (rect.Width >= 0.5 && rect.Height >= 0.5) rects.Add(rect);
                }
            return rects;
        }

        // Unicode text for a char range.
        private string TextRangeString(int pageIndex, int start, int count)
        {
            IntPtr tp = EnsureTextPage(pageIndex);
            if (tp == IntPtr.Zero || count <= 0) return string.Empty;
            var buf = new ushort[count + 1];                 // +1 for PDFium's null terminator
            int written = FPDFText_GetText(tp, start, count, buf);
            if (written <= 1) return string.Empty;
            var chars = new char[written - 1];               // drop the terminator
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)buf[i];
            return new string(chars);
        }

        // --- text selection state, word + flowing drag selection, and highlight rendering ---

        private readonly List<Rectangle> _textSelRects = [];   // currently rendered highlight rects
        private int  _textSelPage    = -1;                     // page of the current selection (for repaint)
        private int  _textSelStart   = -1;                     // first char index of the current selection
        private int  _textSelCount   = 0;                      // char count of the current selection
        private bool _textDragging;                            // a flowing text drag is in progress
        private int  _textAnchorChar = -1;                     // char index where a flowing drag started
        private int  _textDragPage   = -1;                     // page the flowing selection is on (v1: single page)
        private Point _textDragStartPt;                        // canvas point the drag began at (click-vs-drag test)

        // Legacy box text-selection (Settings toggle). Default off = familiar flowing drag selection.
        private bool BoxTextSelectMode => App.GetSetting("BoxTextSelect") == "1";

        /// <summary>
        /// Double-click entry: selects the word under a canvas point on pageIndex. Returns false if there's
        /// no selectable PDF text there (the caller then falls back to its existing double-click behaviour).
        /// Highlights the word and copies it (the app's convention that a selection copies), and leaves it in
        /// _selectedText so Ctrl+C works too.
        /// </summary>
        private bool SelectWordAt(int pageIndex, Point canvasPos)
        {
            int ch = TextCharAtCanvasPoint(pageIndex, canvasPos);
            if (ch < 0) return false;
            var (start, count) = TextWordRangeAt(pageIndex, ch);
            if (count <= 0) return false;

            RenderTextSelection(pageIndex, start, count);
            var text = TextRangeString(pageIndex, start, count);
            _selectedText = text;
            if (!string.IsNullOrEmpty(text))
            {
                TrySetClipboard(text);
                SetStatus($"Copied: {text}");
            }
            return true;
        }

        // Records the selection range, then (re)draws its highlight.
        private void RenderTextSelection(int pageIndex, int start, int count)
        {
            _textSelPage = pageIndex; _textSelStart = start; _textSelCount = count;
            DrawTextSelectionHighlight();
        }

        // Draws one translucent rect per visual line run for the current selection range. Targets the
        // SELECTION's own page canvas (CanvasForPage), not _activeCanvas - which async tile renders can
        // re-point to a neighbour page mid-gesture (the wrong-tile hazard the marquee's _gestureCanvas avoids).
        private void DrawTextSelectionHighlight()
        {
            RemoveTextSelectionRects();
            if (_textSelCount <= 0) return;
            var canvas = CanvasForPage(_textSelPage);
            var fill = AccentBrush(70);
            foreach (var r in TextRangeRects(_textSelPage, _textSelStart, _textSelCount))
            {
                var rect = new Rectangle { Width = r.Width, Height = r.Height, Fill = fill, IsHitTestVisible = false };
                Canvas.SetLeft(rect, r.X);
                Canvas.SetTop(rect, r.Y);
                canvas.Children.Add(rect);
                _textSelRects.Add(rect);
            }
        }

        // Re-draws the selection highlight after a page repaint. RenderAllAnnotations clears the page canvas's
        // children (which would wipe the highlight while _selectedText stayed live), so it calls this at the
        // end. No-op unless the current selection is on the repainted page.
        private void RepaintTextSelection(int pageIndex)
        {
            if (_textSelCount > 0 && _textSelPage == pageIndex) DrawTextSelectionHighlight();
        }

        // Removes just the rendered rects (keeps the selection range so it can be redrawn).
        private void RemoveTextSelectionRects()
        {
            foreach (var r in _textSelRects)
                (r.Parent as Canvas)?.Children.Remove(r);
            _textSelRects.Clear();
        }

        // Clears the selection entirely: drops the rects AND forgets the range. Called from ClearTextSelection
        // (single click / tool switch / starting a new selection).
        private void ClearTextSelectionHighlight()
        {
            RemoveTextSelectionRects();
            _textSelPage = -1; _textSelStart = -1; _textSelCount = 0;
        }

        // Cancels an in-progress flowing drag and releases the mouse capture. Safe no-op if none is active.
        // Called from ClearTextSelection (tool switch) and FinishStuckGesture (lost mouse-up / deactivation).
        private void CancelTextDrag()
        {
            if (!_textDragging) return;
            _textDragging = false;
            (_gestureCanvas ?? _activeCanvas)?.ReleaseMouseCapture();
        }

        // Mouse-down: begin a flowing text selection if the point is on the page's text. Returns false when
        // there's no char there (the caller then falls back to the box marquee). Anchors at that char.
        private bool TextBeginDrag(int pageIndex, Point canvasPos)
        {
            int ch = TextCharAtCanvasPoint(pageIndex, canvasPos, 6.0);
            if (ch < 0) return false;
            _textDragging    = true;
            _textAnchorChar  = ch;
            _textDragPage    = pageIndex;
            _textDragStartPt = canvasPos;
            RenderTextSelection(pageIndex, ch, 1);         // seed the highlight with the anchor char
            _selectedText = TextRangeString(pageIndex, ch, 1);
            return true;
        }

        // Mouse-move: extend the selection from the anchor to the char under the pointer (either direction).
        private void TextExtendDrag(Point canvasPos)
        {
            if (!_textDragging) return;
            int focus = TextCharAtCanvasPoint(_textDragPage, canvasPos, 10.0);
            if (focus < 0) return;                          // off text / in a gap: keep the current selection
            int start = Math.Min(_textAnchorChar, focus);
            int count = Math.Abs(focus - _textAnchorChar) + 1;
            RenderTextSelection(_textDragPage, start, count);
            _selectedText = TextRangeString(_textDragPage, start, count);
        }

        // Mouse-up: finish the flowing selection - copy it (the app's selection-copies convention) and leave
        // it in _selectedText so Ctrl+C works. The highlight stays until the next click / tool switch.
        private void TextEndDrag()
        {
            _textDragging = false;
            var text = _selectedText;
            if (text is null || text.Length == 0) return;
            TrySetClipboard(text);
            int n = text.Length;
            SetStatus($"Copied {n} character{(n == 1 ? "" : "s")}");
        }

        // Highlight-tool release over text: lay a Fill highlight annotation over each selected text-run rect
        // (one per visual line) in the current highlight colour, grouped as a single undo. Then clears the
        // transient blue selection highlight so only the yellow highlight annotations remain.
        private void CommitTextHighlight()
        {
            int page = _textSelPage, start = _textSelStart, count = _textSelCount;
            if (count <= 0) { ClearTextSelection(); return; }

            var rects = TextRangeRects(page, start, count);
            if (!_annotations.TryGetValue(page, out var list)) { list = []; _annotations[page] = list; }
            var group = new List<PageAnnotation>();
            foreach (var r in rects)
            {
                if (r.Width < 1 || r.Height < 1) continue;
                var ha = new HighlightAnnotation { PageIndex = page, Bounds = r, Style = HighlightStyle.Fill };
                ha.SetColor(_highlightColor);
                list.Add(ha);
                group.Add(ha);
            }

            bool wasDirty = _isDirty;
            ClearTextSelection();   // drop the transient selection highlight + reset range/drag
            if (group.Count == 0) return;
            _undoStack.Push(new UndoEntry(UndoKind.AnnotationGroup, page, WasDirty: wasDirty, AnnotGroup: group));
            MarkDirty();
            RenderAllAnnotations(page);
            SetStatus($"Highlighted {group.Count} line{(group.Count == 1 ? "" : "s")}");
        }
    }
}
