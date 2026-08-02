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
    // Moved from Shell/Selection.cs; the namespace and class line are the only changes. Window
    // members spelled bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        // ============================================================
        // Selection
        // ============================================================

        // Resolve the active theme's "SelectionAccent" color: a per-theme color picked to stay
        // readable on the white PDF page (Accent is white in several themes, and AccentBorder is a
        // pale cream that washes out on white). Falls back to brand green.
        private Color AccentColor()
            => TryFindResource("SelectionAccent") is SolidColorBrush b ? b.Color : Color.FromRgb(30, 165, 76);
        internal SolidColorBrush AccentBrush(byte alpha = 255)
        {
            var c = AccentColor();
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
        // A darker shade of the accent, used for a cover's selection chrome and its in-edit outline so a
        // cover reads as distinct from the lighter accent on the text box stacked over it.
        private SolidColorBrush DarkerAccentBrush(byte alpha = 255)
        {
            var c = AccentColor();
            return new SolidColorBrush(Color.FromArgb(alpha, (byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6)));
        }

        // ============================================================
        // Flowing text selection (#127)
        // ============================================================
        // A drag that STARTS on a text character tracks the actual run of characters in reading
        // order, browser-style, instead of the rectangle marquee. Geometry comes from
        // TextRunService (PdfPig words, the same source as search), endpoints are caret positions
        // (page, 0..N over the page's flattened chars), and the painted quads use the exact
        // PDF-to-render math AddSearchHighlight uses, so everything lands where search lands.
        // Drags that start on empty page keep the classic marquee (annotation box-select,
        // region copy, OCR region) - see Canvas_MouseLeftButtonDown.

        private readonly TextRunService _textRuns = new();
        private bool _txtSelActive;                  // drag in progress
        private bool _txtSelHasRange;                // a committed selection is on screen
        private (int Page, int Caret) _txtSelAnchor;
        private (int Page, int Caret) _txtSelFocus;
        private Point _txtSelDownPos;                // press point (gesture canvas coords)
        private bool _txtSelDragStarted;             // true once movement exceeds the click threshold
        private PageAnnotation? _txtSelClickAnnot;   // annotation under the press; selected on plain click
        private Rect _txtSelClickAnnotBounds;
        private EditTool? _txtSelCommitTool;         // #127 Phase 2: non-null while a Highlight/Strike/
                                                     // Underline tool owns the flowing drag - the release
                                                     // commits annotations instead of copying text

        /// <summary>Canvas point to PDF space (points, bottom-left origin) - the inverse of the
        /// mapping AddSearchHighlight paints with, same as ExtractTextFromRegion.</summary>
        private static (double X, double Y) CanvasToPdf(Point pos, double renderW, double renderH, PageTextRuns runs)
            => (pos.X * runs.PdfWidth / renderW, runs.PdfHeight - pos.Y * runs.PdfHeight / renderH);

        /// <summary>Called from the Select tool's mouse-down. Returns true (and arms the drag) only
        /// when the press lands ON text; empty page falls through to the marquee.</summary>
        private bool TryBeginTextSelection(int pageIdx, Point pos)
        {
            if (_currentFile is null) return false;
            if (!_renderDims.TryGetValue(pageIdx, out var rd)) return false;
            var runs = _textRuns.GetPage(_currentFile, pageIdx);
            if (runs is null || runs.Chars.Count == 0) return false;

            var (px, py) = CanvasToPdf(pos, rd.w, rd.h, runs);
            if (!TextRunService.IsOverText(runs, px, py)) return false;

            ClearTextSelection();
            int caret = TextRunService.CaretFromPoint(runs, px, py);
            _txtSelAnchor = _txtSelFocus = (pageIdx, caret);
            _txtSelDownPos = pos;
            _txtSelDragStarted = false;
            _txtSelActive = true;
            return true;
        }

        /// <summary>Mouse-move while a flowing selection drag is live. Resolves which page the
        /// pointer is over (cross-page tracking in Continuous, where every overlay is a live tile),
        /// moves the focus caret, and repaints.</summary>
        private void UpdateTextSelectionDrag(MouseEventArgs e)
        {
            if (_currentFile is null) return;
            // Click-vs-drag threshold: below ~4px of movement this is still a click (which selects
            // the annotation under the press, if any, on mouse-up) - not a text drag.
            if (!_txtSelDragStarted)
            {
                var tcv = _gestureCanvas ?? _activeCanvas;
                if (tcv is null) return;
                var tp = e.GetPosition(tcv);
                if (Math.Abs(tp.X - _txtSelDownPos.X) < 4 && Math.Abs(tp.Y - _txtSelDownPos.Y) < 4) return;
                _txtSelDragStarted = true;
            }
            int page = _gesturePage;
            Canvas? cv = _gestureCanvas ?? _activeCanvas;

            if (_viewMode == ViewMode.Continuous)
            {
                foreach (var kv in _pages)
                {
                    var c = kv.Value;
                    double cw = double.IsNaN(c.Width) ? c.ActualWidth : c.Width;
                    double ch = double.IsNaN(c.Height) ? c.ActualHeight : c.Height;
                    var p = e.GetPosition(c);
                    if (p.X >= 0 && p.X <= cw && p.Y >= 0 && p.Y <= ch) { page = kv.Key; cv = c; break; }
                }
            }
            if (cv is null) return;

            // Clamp into the canvas so dragging past an edge clamps to the start/end of lines
            // instead of losing the selection.
            double cvW = double.IsNaN(cv.Width) ? cv.ActualWidth : cv.Width;
            double cvH = double.IsNaN(cv.Height) ? cv.ActualHeight : cv.Height;
            var pos = e.GetPosition(cv);
            pos = new Point(Math.Max(0, Math.Min(cvW, pos.X)), Math.Max(0, Math.Min(cvH, pos.Y)));

            if (!_renderDims.TryGetValue(page, out var rd)) return;
            var runs = _textRuns.GetPage(_currentFile, page);
            if (runs is null) return;

            var (px, py) = CanvasToPdf(pos, rd.w, rd.h, runs);
            var focus = (page, TextRunService.CaretFromPoint(runs, px, py));
            if (focus == _txtSelFocus) return;
            _txtSelFocus = focus;
            RepaintTextSelection();
        }

        /// <summary>Mouse-up: commit the range, copy it (matching the app's existing
        /// select-copies-immediately behavior), and leave the quads on screen.</summary>
        private void FinishTextSelection()
        {
            _txtSelActive = false;
            var clickAnnot = _txtSelClickAnnot;
            var clickBounds = _txtSelClickAnnotBounds;
            var commitTool = _txtSelCommitTool;
            _txtSelClickAnnot = null;
            _txtSelCommitTool = null;
            if (!_txtSelDragStarted || _txtSelAnchor == _txtSelFocus)
            {
                // Plain click: the annotation under the press (e.g. a highlight box covering this
                // paragraph) gets selected, exactly as it did before flowing selection existed.
                // (Select tool only - a highlight-tool click just drops the empty gesture.)
                ClearTextSelection();
                if (commitTool is null && clickAnnot is not null) SelectAnnotation(clickAnnot, clickBounds);
                return;
            }
            if (commitTool is EditTool hlTool)
            {
                CommitFlowingHighlight(hlTool);
                return;
            }
            _txtSelHasRange = true;

            int words;
            _selectedText = BuildSelectedText(out words);
            if (string.IsNullOrWhiteSpace(_selectedText))
            {
                SetStatus(Loc("Str_St_NoTextInSelection"));
                ClearTextSelection();
                return;
            }
            try { Clipboard.SetText(_selectedText); } catch { /* clipboard momentarily locked by another app */ }
            SetStatus($"Copied {words} word(s) to clipboard");
        }

        private ((int Page, int Caret) Start, (int Page, int Caret) End) OrderedSelection()
        {
            var a = _txtSelAnchor;
            var f = _txtSelFocus;
            bool aFirst = a.Page < f.Page || (a.Page == f.Page && a.Caret <= f.Caret);
            return aFirst ? (a, f) : (f, a);
        }

        /// <summary>The caret slice of the selection that falls on one page, or (0,0) when none.</summary>
        private (int Start, int End) SelectionSliceForPage(int page, int charCount)
        {
            var (s, e) = OrderedSelection();
            if (page < s.Page || page > e.Page) return (0, 0);
            int start = page == s.Page ? s.Caret : 0;
            int end = page == e.Page ? e.Caret : charCount;
            return (start, end);
        }

        private string BuildSelectedText(out int wordCount)
        {
            wordCount = 0;
            if (_currentFile is null) return string.Empty;
            var (s, e) = OrderedSelection();
            var sb = new System.Text.StringBuilder();
            for (int p = s.Page; p <= e.Page; p++)
            {
                var runs = _textRuns.GetPage(_currentFile, p);
                if (runs is null || runs.Chars.Count == 0) continue;
                var (start, end) = SelectionSliceForPage(p, runs.Chars.Count);
                if (start >= end) continue;
                string t = TextRunService.TextForRange(runs, start, end, out int w);
                if (t.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
                wordCount += w;
            }
            return sb.ToString();
        }

        /// <summary>Per-line rects (canvas render-dim space) of the current selection on one page -
        /// one rect per line, first selected char to last, browser-style. Shared by the selection
        /// quad painter and the flowing-highlight commit (#127 Phase 2) so a committed highlight
        /// lands exactly where the drag preview showed it.</summary>
        private List<Rect> SelectionLineRectsForPage(int page)
        {
            var result = new List<Rect>();
            if (_currentFile is null) return result;
            var (s, e) = OrderedSelection();
            if (page < s.Page || page > e.Page) return result;
            if (!_renderDims.TryGetValue(page, out var rd)) return result;
            var runs = _textRuns.GetPage(_currentFile, page);
            if (runs is null || runs.Chars.Count == 0) return result;
            var (start, end) = SelectionSliceForPage(page, runs.Chars.Count);
            if (start >= end) return result;

            double sx = rd.w / runs.PdfWidth;
            double sy = rd.h / runs.PdfHeight;

            int i = start;
            while (i < end)
            {
                var line = runs.Lines[runs.Chars[i].Line];
                int segEnd = Math.Min(end, line.End);

                double left = runs.Chars[i].Left;
                double right = runs.Chars[segEnd - 1].Right;
                double h = (line.Top - line.Bottom) * sy;
                double pad = h * 0.12;   // a touch of breathing room; tighter than search's 0.30

                result.Add(new Rect(left * sx, rd.h - line.Top * sy - pad,
                                    Math.Max((right - left) * sx, 2), Math.Max(h + pad * 2, 2)));
                i = segEnd;
            }
            return result;
        }

        /// <summary>Paints one page's selection quads onto its overlay. Called while dragging and
        /// from the tail of RenderAllAnnotations so the quads survive re-renders, exactly like
        /// search highlights do.</summary>
        private void ApplyTextSelectionQuads(int page, Canvas canvas)
        {
            if (!_txtSelActive && !_txtSelHasRange) return;
            foreach (var r in SelectionLineRectsForPage(page))
            {
                var rect = new Rectangle
                {
                    Opacity = 60.0 / 255.0,
                    Width = r.Width,
                    Height = r.Height,
                    IsHitTestVisible = false,
                    Tag = "TextSelQuad"
                };
                // Live theme binding (net48 rule: a plain brush snapshot won't follow a theme
                // switch) - the quads recolor the moment the theme or accent changes.
                rect.SetResourceReference(Shape.FillProperty, "SelectionAccent");
                Canvas.SetLeft(rect, r.X);
                Canvas.SetTop(rect, r.Y);
                canvas.Children.Add(rect);
            }
        }

        /// <summary>#127 Phase 2: turns the flowing selection into Highlight / Strikethrough /
        /// Underline annotations - one per selected line, grouped per page so one gesture behaves
        /// as one annotation (select, move, delete together), and one page-snapshot undo entry so
        /// Ctrl+Z reverts the whole gesture in a single step.</summary>
        private void CommitFlowingHighlight(EditTool tool)
        {
            var (s, e) = OrderedSelection();
            var perPage = new List<(int Page, List<Rect> Rects)>();
            for (int p = s.Page; p <= e.Page; p++)
            {
                var rects = SelectionLineRectsForPage(p);
                if (rects.Count > 0) perPage.Add((p, rects));
            }
            if (perPage.Count == 0) { ClearTextSelection(); return; }

            PushPagesSnapshotUndo(perPage.Select(pp => pp.Page));
            var style = tool == EditTool.Strikethrough ? HighlightStyle.Strikethrough
                      : tool == EditTool.Underline    ? HighlightStyle.Underline
                      : HighlightStyle.Fill;
            int total = 0;
            foreach (var (page, rects) in perPage)
            {
                // One group per page; a single-line highlight stays ungrouped.
                string gid = rects.Count > 1 ? Guid.NewGuid().ToString("N") : "";
                if (!_annotations.ContainsKey(page)) _annotations[page] = [];
                foreach (var r in rects)
                {
                    var ha = new HighlightAnnotation { PageIndex = page, Bounds = r, Style = style, GroupId = gid };
                    ha.SetColor(tool == EditTool.Highlight ? _highlightColor : _lineAnnotColor);
                    _annotations[page].Add(ha);
                    total++;
                }
            }
            MarkDirty();
            ClearTextSelection();
            foreach (var (page, _) in perPage) RenderAllAnnotations(page);
            SetStatus($"{(style == HighlightStyle.Fill ? "Highlighted" : style == HighlightStyle.Strikethrough ? "Struck through" : "Underlined")} {total} line(s)");
        }

        /// <summary>Drops and repaints the quads on every page the selection touches.</summary>
        private void RepaintTextSelection()
        {
            RemoveTextSelQuads();
            var (s, e) = OrderedSelection();
            for (int p = s.Page; p <= e.Page; p++)
            {
                var canvas = VisibleCanvasForPage(p);
                if (canvas is not null) ApplyTextSelectionQuads(p, canvas);
            }
        }

        private void RemoveTextSelQuads()
        {
            foreach (var canvas in AllPageCanvases())
            {
                var toRemove = canvas.Children.OfType<Rectangle>()
                    .Where(r => r.Tag is string s && s == "TextSelQuad").ToList();
                foreach (var r in toRemove)
                    canvas.Children.Remove(r);
            }
        }

        /// <summary>Ctrl+A: flowing select-all on the current page (quads over every line) and copy.</summary>
        private void SelectAllText()
        {
            if (_currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            try
            {
                var runs = _textRuns.GetPage(_currentFile, pageIdx);
                if (runs is null || runs.Chars.Count == 0)
                {
                    SetStatus(Loc("Str_St_NoTextOnPage"));
                    return;
                }
                ClearTextSelection();
                _txtSelAnchor = (pageIdx, 0);
                _txtSelFocus = (pageIdx, runs.Chars.Count);
                _txtSelHasRange = true;
                RepaintTextSelection();

                _selectedText = TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _);
                if (string.IsNullOrWhiteSpace(_selectedText))
                {
                    SetStatus(Loc("Str_St_NoTextOnPage"));
                    ClearTextSelection();
                    return;
                }
                Clipboard.SetText(_selectedText);
                SetStatus($"Selected all text - copied to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Select all error: {ex.Message}");
            }
        }

        private void CopySelectedText()
        {
            if (!string.IsNullOrEmpty(_selectedText))
            {
                Clipboard.SetText(_selectedText);
                SetStatus($"Copied to clipboard");
            }
            else
            {
                SetStatus(Loc("Str_St_NoTextSelected"));
            }
        }

        internal void ClearTextSelection()
        {
            if (_selectRect is not null)
            {
                // Remove from the rect's ACTUAL parent. Since the cross-page marquee rework the
                // selection box lives on the window-level MarqueeLayer, not the page canvas, so
                // removing from _activeCanvas was a silent no-op that orphaned the box on the
                // layer until app restart (#121).
                (_selectRect.Parent as Canvas)?.Children.Remove(_selectRect);
                _selectRect = null;
            }
            _selectedText = null;
            _txtSelActive = false;
            _txtSelHasRange = false;
            _txtSelDragStarted = false;
            _txtSelCommitTool = null;
            RemoveTextSelQuads();
        }

        /// <summary>Marquee fallback: rectangle region copy, used when a drag starts on EMPTY page
        /// (scans, margins). Kept word-box based on purpose - on pages with no text layer there is
        /// nothing to flow along, and this is also what the annotation box-select falls back to.</summary>
        private void ExtractTextFromRegion(int pageIdx, Rect canvasBounds)
        {
            if (_currentFile is null || pageIdx < 0) return;
            if (!_renderDims.ContainsKey(pageIdx)) return;

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1); // PdfPig is 1-based

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = pdfW / renderW;
                double sy = pdfH / renderH;

                // Convert canvas rect to PDF coordinates (flip Y - PDF origin is bottom-left)
                double pdfLeft = canvasBounds.Left * sx;
                double pdfRight = canvasBounds.Right * sx;
                double pdfTop = pdfH - (canvasBounds.Top * sy);
                double pdfBottom = pdfH - (canvasBounds.Bottom * sy);
                // pdfTop > pdfBottom because of Y flip
                double pdfMinY = Math.Min(pdfTop, pdfBottom);
                double pdfMaxY = Math.Max(pdfTop, pdfBottom);

                var words = page.GetWords()
                    .Where(w =>
                    {
                        var bb = w.BoundingBox;
                        double cx = (bb.Left + bb.Right) / 2;
                        double cy = (bb.Bottom + bb.Top) / 2;
                        return cx >= pdfLeft && cx <= pdfRight && cy >= pdfMinY && cy <= pdfMaxY;
                    })
                    .ToList();

                if (words.Count == 0)
                {
                    SetStatus(Loc("Str_St_NoTextInSelection"));
                    ClearTextSelection();
                    return;
                }

                _selectedText = WordsToText(words);

                Clipboard.SetText(_selectedText);
                int wordCount = words.Count;
                SetStatus($"Copied {wordCount} word(s) to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Text extraction error: {ex.Message}");
                ClearTextSelection();
            }
        }
    }
}
