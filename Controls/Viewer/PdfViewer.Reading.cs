using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // The screen-reader view of this pane's document (BF-U001): page text in the same reading
    // order selection and search use, mapped back to characters so a reader's range can be
    // outlined on the page, selected and scrolled to.
    public partial class PdfViewer
    {
        private ReadingSource? _readingSource;

        private void InitializeReadingSurface()
        {
            _readingSource = new ReadingSource(this);
            DocSurfacePad.Source = _readingSource;
        }

        /// <summary>Tells a screen reader the selection moved. Called when selection is repainted or cleared.</summary>
        private void NotifyReadingSelectionChanged() => _readingSource?.RaiseSelectionChanged();

        internal void NotifyReadingTextChanged() => _readingSource?.InvalidateText();
        private void NotifyReadingPageReady(int page) => _readingSource?.PageReady(page);

        private sealed class ReadingSource(PdfViewer viewer) : IDocumentReadingSource
        {
            private readonly Dictionary<int, PageReadingText> _pages = [];
            private object? _pagesVersion;
            private Dictionary<int, List<int>>? _structureOrder;
            // The structure order, comments and crop boxes come from the file alone, so they are
            // read again only when the working file changes, not after every overlay or form edit.
            private object? _factsVersion;
            private long _textRevision;
            private long _scrollRequest;
            private (long Request, object Version, long Generation, int Page, int Caret, bool? AlignToTop)? _pendingScroll;

            // A tagged PDF's reading order: for each page, its marked-content IDs in the order the
            // tag tree reaches them. Null for an untagged or unreadable tree, which reads in the
            // viewer's visual order instead.
            private Dictionary<int, List<int>>? StructureOrder(string file)
            {
                ReadDocumentFacts(file);
                return _structureOrder;
            }

            // Each page's comments as readable lines: the PDF's own review comments, read after the
            // page body so a screen reader reaches them in page order.
            private readonly Dictionary<int, List<KillerPdf.Engine.Documents.PdfCommentInfo>> _comments = [];
            private readonly Dictionary<int, (double Left, double Bottom)> _cropOrigins = [];

            private List<string> PageComments(string file, int page)
            {
                ReadDocumentFacts(file);
                var lines = new List<string>();
                if (_comments.TryGetValue(page, out var saved))
                    foreach (var comment in saved)
                        if (DescribeAnnotation(file, page, comment) is { } line) lines.Add(line);
                // Text boxes added in KillerPDF and not yet saved are on screen too.
                if (viewer._annotations.TryGetValue(page, out var placed))
                    foreach (var text in placed.OfType<TextAnnotation>())
                        if (!string.IsNullOrWhiteSpace(text.Content)) lines.Add(text.Content.Trim());
                return lines;
            }

            // One readable line for a page annotation: its comment text, or for bare markup what it
            // is, quoting the text a highlight, underline or strikeout covers.
            private string? DescribeAnnotation(string file, int page, KillerPdf.Engine.Documents.PdfCommentInfo comment)
            {
                var origin = _cropOrigins.TryGetValue(page, out var crop) ? crop : (0, 0);
                return ReadingAnnotationText.Describe(comment, () => viewer._textRuns.GetPage(file, viewer._protection,page),
                    origin, viewer.Loc);
            }
            private void ReadDocumentFacts(string file)
            {
                object facts = FileStamp();
                if (Equals(facts, _factsVersion)) return;
                _factsVersion = facts;
                _comments.Clear();
                _cropOrigins.Clear();
                KillerPdf.Engine.Documents.PdfDocument document;
                // The tab's file, read under the tab's protection like every other reader of it.
                try { document = PdfEngineIntegration.OpenForReading(System.IO.File.ReadAllBytes(file), viewer._protection); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _structureOrder = null; return; }
                try
                {
                    foreach (var comment in KillerPdf.Engine.Documents.PdfCommentReader.Read(document, includeMarkupWithoutText: true))
                    {
                        if (!_comments.TryGetValue(comment.PageIndex, out var list)) _comments[comment.PageIndex] = list = [];
                        list.Add(comment);
                    }
                    foreach (var box in KillerPdf.Engine.Documents.PdfPageInformation.Read(document).Select((info, index) => (info, index)))
                        _cropOrigins[box.index] = (box.info.Left, box.info.Bottom);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _comments.Clear(); _cropOrigins.Clear(); }
                try
                {
                    var order = new Dictionary<int, List<int>>();
                    foreach (var item in KillerPdf.Engine.Diagnostics.PdfAccessibilityReadingOrder.Read(document)
                                 .Items.OrderBy(item => item.Sequence))
                    {
                        if (item.MarkedContentId is not int mcid) continue;
                        if (!order.TryGetValue(item.PageIndex, out List<int>? list)) order[item.PageIndex] = list = [];
                        list.Add(mcid);
                    }
                    _structureOrder = order.Count == 0 ? null : order;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _structureOrder = null;   // no structure tree, or one that cannot be read
                }
            }

            public event Action? TextChanged;
            public event Action? SelectionChanged;

            internal void RaiseSelectionChanged() => SelectionChanged?.Invoke();

            internal void InvalidateText()
            {
                _textRevision++;
                _pages.Clear();
                _pagesVersion = Version;
                TextChanged?.Invoke();
            }

            public int PageCount => viewer._doc?.PageCount ?? 0;
            public int CurrentPage => viewer._currentPage;

            private (string? File, long Ticks) FileStamp()
            {
                string? file = viewer._currentFile;
                long ticks = 0;
                try { if (file is not null && System.IO.File.Exists(file)) ticks = System.IO.File.GetLastWriteTimeUtc(file).Ticks; }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                return (file, ticks);
            }

            public object Version
            {
                get
                {
                    var (file, ticks) = FileStamp();
                    // Placed text boxes are read too, so adding or removing one rebuilds the text.
                    return (file, ticks, PageCount, viewer._annotations.Values.Sum(list => list.Count), _textRevision);
                }
            }

            public PageReadingText PageText(int page)
            {
                object version = Version;
                if (!Equals(version, _pagesVersion))
                {
                    _pages.Clear();
                    _pagesVersion = version;
                    TextChanged?.Invoke();
                }
                if (_pages.TryGetValue(page, out PageReadingText? cached)) return cached;
                PageReadingText text = PageReadingText.Empty;
                if (viewer._currentFile is { } file && page >= 0 && page < PageCount)
                {
                    try
                    {
                        PageTextRuns? runs = viewer._textRuns.GetPage(file, viewer._protection,page);
                        if (runs is not null)
                            text = StructureOrder(file) is { } order && order.TryGetValue(page, out List<int>? mcids)
                                ? PageReadingText.BuildTagged([.. runs.Chars.Select(c => (c.Value, c.Word, c.Line, c.MarkedContentId))], mcids)
                                : PageReadingText.Build([.. runs.Chars.Select(c => (c.Value, c.Word, c.Line))]);
                        text = text.WithAppendix(PageComments(file, page));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        text = PageReadingText.Empty;   // an unreadable page reads as empty, never breaks the reader
                    }
                }
                return _pages[page] = text;
            }

            public IReadOnlyList<Rect> ScreenRects(int page, int startCaret, int endCaret)
            {
                var result = new List<Rect>();
                if (viewer._currentFile is not { } file || !viewer._renderDims.TryGetValue(page, out var rd)) return result;
                if (viewer.VisibleCanvasForPage(page) is not { } canvas || !canvas.IsVisible
                    || PresentationSource.FromVisual(canvas) is null) return result;
                PageTextRuns? runs = viewer._textRuns.GetPage(file, viewer._protection,page);
                if (runs is null) return result;
                foreach (Rect rect in viewer.CaretRangeRects(page, runs, startCaret, endCaret, rd))
                {
                    Point a = canvas.PointToScreen(rect.TopLeft), b = canvas.PointToScreen(rect.BottomRight);
                    Rect clipped = Rect.Intersect(new Rect(a, b), Viewport(canvas));
                    if (!clipped.IsEmpty && clipped.Width > 0 && clipped.Height > 0) result.Add(clipped);
                }
                return result;
            }

            private static Rect Viewport(FrameworkElement canvas)
            {
                for (DependencyObject? parent = canvas; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is ScrollContentPresenter viewport)
                        return new Rect(viewport.PointToScreen(new Point()),
                            viewport.PointToScreen(new Point(viewport.ActualWidth, viewport.ActualHeight)));
                return Rect.Empty;
            }

            public IReadOnlyList<(int Page, int Start, int End)> VisibleCaretRanges()
            {
                var result = new List<(int Page, int Start, int End)>();
                if (viewer._currentFile is not { } file) return result;
                foreach (var (page, dimensions) in viewer._renderDims.OrderBy(entry => entry.Key))
                {
                    if (viewer.VisibleCanvasForPage(page) is not { IsVisible: true } canvas
                        || PresentationSource.FromVisual(canvas) is null) continue;
                    Rect viewport = Viewport(canvas);
                    var pageBounds = new Rect(canvas.PointToScreen(new Point()),
                        canvas.PointToScreen(new Point(canvas.ActualWidth, canvas.ActualHeight)));
                    if (!viewport.IntersectsWith(pageBounds) || viewer._textRuns.GetPage(file, viewer._protection,page) is not { } runs) continue;
                    foreach (var (start, end) in VisibleTextGeometry.VisibleCarets(runs, dimensions.w, dimensions.h,
                                 viewer.EffectiveRotation(page), canvas.PointToScreen, viewport))
                        result.Add((page, start, end));
                }
                return result;
            }

            public (int Page, int Caret)? CaretFromScreenPoint(Point point)
            {
                if (viewer._currentFile is not { } file) return null;
                (int Page, int Caret)? closest = null;
                double distance = double.PositiveInfinity;
                foreach (var (page, dimensions) in viewer._renderDims)
                {
                    if (viewer.VisibleCanvasForPage(page) is not { IsVisible: true } canvas
                        || PresentationSource.FromVisual(canvas) is null) continue;
                    var bounds = new Rect(canvas.PointToScreen(new Point()),
                        canvas.PointToScreen(new Point(canvas.ActualWidth, canvas.ActualHeight)));
                    bounds.Intersect(Viewport(canvas));
                    if (bounds.IsEmpty) continue;
                    double dx = Math.Max(bounds.Left - point.X, Math.Max(0, point.X - bounds.Right));
                    double dy = Math.Max(bounds.Top - point.Y, Math.Max(0, point.Y - bounds.Bottom));
                    double candidate = dx * dx + dy * dy;
                    if (candidate >= distance || viewer._textRuns.GetPage(file, viewer._protection,page) is not { } runs
                        || runs.Chars.Count == 0) continue;
                    var (x, y) = viewer.CanvasToPdf(page, canvas.PointFromScreen(point), dimensions.w, dimensions.h, runs);
                    closest = (page, TextRunService.CaretFromPoint(runs, x, y));
                    distance = candidate;
                }
                return closest;
            }

            public void Select(int startPage, int startCaret, int endPage, int endCaret)
            {
                if (viewer._currentFile is null) return;
                viewer.ClearTextSelection();
                viewer._txtSelAnchor = (startPage, startCaret);
                viewer._txtSelFocus = (endPage, endCaret);
                viewer._txtSelHasRange = (startPage, startCaret) != (endPage, endCaret);
                viewer.RepaintTextSelection();
                // The selection is shown and available to Copy; the clipboard is left alone.
                viewer._selectedText = viewer._txtSelHasRange ? viewer.BuildSelectedText(out _) : null;
                RaiseSelectionChanged();
            }

            public (int StartPage, int StartCaret, int EndPage, int EndCaret)? Selection
            {
                get
                {
                    if (!viewer._txtSelHasRange) return null;
                    var (s, e) = viewer.OrderedSelection();
                    return (s.Page, s.Caret, e.Page, e.Caret);
                }
            }

            public void ScrollToPage(int page)
            {
                if (page >= 0 && page < PageCount && page != viewer._currentPage) viewer.NavigateToPageExt(page);
            }

            public void ScrollToCaret(int page, int caret, bool? alignToTop = null)
            {
                if (page < 0 || page >= PageCount) return;
                ScrollToPage(page);
                long request = ++_scrollRequest;
                _pendingScroll = (request, Version, viewer._primaryRenderGeneration, page, caret, alignToTop);
                viewer.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(async () =>
                {
                    try
                    {
                        await viewer.PrimaryRenderTask;
                        RevealScroll(request);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Reading caret scroll: {ex}");
                    }
                }));
            }

            internal void PageReady(int page)
            {
                if (_pendingScroll is not { } scroll || scroll.Page != page) return;
                viewer.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => RevealScroll(scroll.Request)));
            }

            private void RevealScroll(long request)
            {
                try { RevealScrollCore(request); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    System.Diagnostics.Debug.WriteLine($"Reading caret scroll: {ex}");
                }
            }

            private void RevealScrollCore(long request)
            {
                if (_pendingScroll is not { } scroll || scroll.Request != request) return;
                if (!Equals(scroll.Version, Version) || scroll.Generation != viewer._primaryRenderGeneration)
                {
                    _pendingScroll = null;
                    return;
                }
                if (viewer._currentFile is not { } file
                    || !viewer._renderDims.TryGetValue(scroll.Page, out var dimensions)
                    || viewer.VisibleCanvasForPage(scroll.Page) is not { IsVisible: true } canvas) return;
                _pendingScroll = null;
                if (viewer._textRuns.GetPage(file, viewer._protection,scroll.Page) is not { Chars.Count: > 0 } runs) return;
                int start = Math.Clamp(scroll.Caret, 0, runs.Chars.Count - 1);
                foreach (Rect rectangle in viewer.CaretRangeRects(scroll.Page, runs, start, start + 1, dimensions))
                {
                    Rect target = rectangle;
                    var line = runs.Lines[runs.Chars[start].Line];
                    var lineBounds = viewer.CaretRangeRects(scroll.Page, runs, line.Start, line.End, dimensions);
                    Visual? content = viewer.PagePreviewPanel.Content as Visual;
                    // Reveal readable context when it fits instead of leaving one glyph at the edge.
                    if (lineBounds.Count > 0 && content is not null)
                    {
                        Rect displayed = canvas.TransformToVisual(content).TransformBounds(lineBounds[0]);
                        if (displayed.Width <= viewer.PagePreviewPanel.ViewportWidth
                            && displayed.Height <= viewer.PagePreviewPanel.ViewportHeight)
                            target = lineBounds[0];
                    }
                    canvas.BringIntoView(target);
                    if (scroll.AlignToTop is bool top && content is not null)
                    {
                        Rect bounds = canvas.TransformToVisual(content).TransformBounds(rectangle);
                        viewer.PagePreviewPanel.ScrollToVerticalOffset(top ? bounds.Top
                            : bounds.Bottom - viewer.PagePreviewPanel.ViewportHeight);
                    }
                    break;
                }
            }
        }
    }
}
