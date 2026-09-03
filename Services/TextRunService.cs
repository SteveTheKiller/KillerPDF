using System.IO;
using PdfDocument = KillerPDF.Services.PdfContentDocument;

namespace KillerPDF.Services
{
    /// <summary>One selectable character on a page, in reading order. Coordinates are PDF space
    /// (points, bottom-left origin), matching SearchService and ExtractTextFromRegion.</summary>
    internal readonly struct RunChar(string value, double left, double right, int word, int line)
    {
        public readonly string Value = value;   // engine letters can be multi-char (ligatures)
        public readonly double Left = left;
        public readonly double Right = right;
        public readonly int Word = word;       // ordinal of the word this char belongs to (for word counts / spacing)
        public readonly int Line = line;       // ordinal of the line this char belongs to
    }

    /// <summary>A visual line of text: a contiguous slice of the page's flattened char list plus its
    /// vertical band. Caret positions run 0..N over the flattened chars; a line's End caret is the
    /// next line's Start, so a selection ending at End stops cleanly at the line break.</summary>
    internal sealed class RunLine
    {
        public int Start;               // caret index of the line's first char
        public int Count;
        public double Top;              // PDF space: Top > Bottom
        public double Bottom;
        public double Left;
        public double Right;
        public bool RightToLeft;
        public int End => Start + Count;
    }

    /// <summary>Reading-order text geometry for one page.</summary>
    internal sealed class PageTextRuns
    {
        public double PdfWidth;
        public double PdfHeight;
        public List<RunChar> Chars = [];
        public List<RunLine> Lines = [];
    }

    /// <summary>
    /// Builds and caches per-page reading-order character runs for flowing text selection (#127).
    /// Word geometry comes from the engine's GetWords - the same source SearchService and the region
    /// text extractor use - so selection quads land exactly where search highlights do. Words are
    /// grouped into lines by vertical overlap and ordered top-to-bottom, then in each line's
    /// detected reading direction.
    /// Known shared limitation: like the search highlights, boxes ignore in-memory page rotation.
    /// Column note: line grouping is by vertical band, so side-by-side columns join into one line;
    /// good enough for v1, revisit with a segmenter if multi-column PDFs bite.
    /// </summary>
    internal sealed class TextRunService
    {
        // Keyed by (path, last-write ticks, page): a resave or temp-reload changes the key, so stale
        // geometry can never serve a newer file. Nulls are cached too - a file engine cannot open
        // should not be re-parsed on every click.
        private readonly Dictionary<(string Path, long Ticks, int Page), PageTextRuns?> _cache = [];

        public PageTextRuns? GetPage(string path, int pageIdx)
        {
            if (string.IsNullOrEmpty(path) || pageIdx < 0) return null;
            long ticks;
            try { ticks = File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return null; }

            var key = (path, ticks, pageIdx);
            if (_cache.TryGetValue(key, out var hit)) return hit;
            if (_cache.Count > 512) _cache.Clear();   // simple cap; entries are tiny but unbounded is unbounded

            PageTextRuns? runs = null;
            try
            {
                using var doc = PdfDocument.Open(path);
                if (pageIdx < doc.NumberOfPages)
                    runs = Build(doc.GetPage(pageIdx + 1));   // The app facade is 1-based
            }
            catch { /* encrypted/broken: selection just is not offered on this page */ }

            _cache[key] = runs;
            return runs;
        }

        // #185 helper: see the call site comment. Bands arrive top-to-bottom; the result is the
        // same tuple shape, reordered so the flattened char order reads one column at a time.
        private static List<(List<KillerPdf.Engine.Documents.PdfExtractedWord> Words, double Top, double Bottom)>
            OrderColumnAware(List<(List<KillerPdf.Engine.Documents.PdfExtractedWord> Words, double Top, double Bottom)> bands)
        {
            if (bands.Count < 2) return bands;

            double textL = double.MaxValue, textR = double.MinValue;
            foreach (var (ws, _, _) in bands)
                foreach (var w in ws)
                {
                    if (w.BoundingBox.Left < textL) textL = w.BoundingBox.Left;
                    if (w.BoundingBox.Right > textR) textR = w.BoundingBox.Right;
                }
            double wideW = (textR - textL) * 0.62;   // spans most of the text width = not a column line

            var reordered = new List<(List<KillerPdf.Engine.Documents.PdfExtractedWord>, double, double)>();
            var pending = new List<(List<KillerPdf.Engine.Documents.PdfExtractedWord> Words, double Top, double Bottom, double L, double R)>();

            void Flush()
            {
                if (pending.Count == 0) return;
                // Cluster segments into columns by X-interval overlap (>= half the narrower range).
                var cols = new List<(double L, double R, List<int> Idx)>();
                var byLeft = Enumerable.Range(0, pending.Count).OrderBy(i => pending[i].L).ToList();
                foreach (int i in byLeft)
                {
                    var (Words, Top, Bottom, L, R) = pending[i];
                    int hit = -1;
                    for (int c = 0; c < cols.Count && hit < 0; c++)
                    {
                        double ov = Math.Min(cols[c].R, R) - Math.Max(cols[c].L, L);
                        double minW = Math.Min(cols[c].R - cols[c].L, R - L);
                        if (minW > 0 && ov >= minW * 0.5) hit = c;
                    }
                    if (hit < 0) cols.Add((L, R, [i]));
                    else
                    {
                        var c0 = cols[hit];
                        c0.Idx.Add(i);
                        cols[hit] = (Math.Min(c0.L, L), Math.Max(c0.R, R), c0.Idx);
                    }
                }
                foreach (var (L, R, Idx) in cols.OrderBy(c => c.L))
                    foreach (int i in Idx.OrderByDescending(i => pending[i].Top))
                        reordered.Add((pending[i].Words, pending[i].Top, pending[i].Bottom));
                pending.Clear();
            }

            foreach (var (ws, _, _) in bands)
            {
                var sorted = ws.OrderBy(w => w.BoundingBox.Left).ToList();
                // Split threshold: well past a word space (~0.25em) but below a column gutter.
                double tw = 0; int tn = 0;
                foreach (var w in sorted) { tw += w.BoundingBox.Width; tn += Math.Max(1, w.Text.Length); }
                double gapT = Math.Max(10, (tn > 0 ? tw / tn : 5) * 3);

                var segs = new List<List<KillerPdf.Engine.Documents.PdfExtractedWord>> { new() { sorted[0] } };
                for (int i = 1; i < sorted.Count; i++)
                {
                    if (sorted[i].BoundingBox.Left - sorted[i - 1].BoundingBox.Right > gapT)
                        segs.Add([]);
                    segs[^1].Add(sorted[i]);
                }
                foreach (var sws in segs)
                {
                    double sT = double.MinValue, sB = double.MaxValue, sL = double.MaxValue, sR = double.MinValue;
                    foreach (var w in sws)
                    {
                        if (w.BoundingBox.Top > sT) sT = w.BoundingBox.Top;
                        if (w.BoundingBox.Bottom < sB) sB = w.BoundingBox.Bottom;
                        if (w.BoundingBox.Left < sL) sL = w.BoundingBox.Left;
                        if (w.BoundingBox.Right > sR) sR = w.BoundingBox.Right;
                    }
                    if (sR - sL >= wideW) { Flush(); reordered.Add((sws, sT, sB)); }
                    else pending.Add((sws, sT, sB, sL, sR));
                }
            }
            Flush();
            return reordered;
        }

        private static PageTextRuns Build(KillerPdf.Engine.Documents.PdfPageContent page)
        {
            var result = new PageTextRuns { PdfWidth = page.Width, PdfHeight = page.Height };
            var words = page.GetWords().ToList();
            if (words.Count == 0) return result;

            // Group words into lines: a word joins a line when its vertical band overlaps the line's
            // band by at least half the smaller height. Bands grow as members join.
            var lineWords = new List<(List<KillerPdf.Engine.Documents.PdfExtractedWord> Words, double Top, double Bottom)>();
            foreach (var w in words)
            {
                var bb = w.BoundingBox;
                double wTop = bb.Top, wBottom = bb.Bottom;
                int found = -1;
                for (int i = 0; i < lineWords.Count; i++)
                {
                    var (_, lTop, lBottom) = lineWords[i];
                    double overlap = Math.Min(lTop, wTop) - Math.Max(lBottom, wBottom);
                    double minH = Math.Min(lTop - lBottom, wTop - wBottom);
                    if (minH > 0 && overlap >= minH * 0.5) { found = i; break; }
                }
                if (found < 0)
                    lineWords.Add((new List<KillerPdf.Engine.Documents.PdfExtractedWord> { w }, wTop, wBottom));
                else
                {
                    var (Words, Top, Bottom) = lineWords[found];
                    Words.Add(w);
                    lineWords[found] = (Words, Math.Max(Top, wTop), Math.Min(Bottom, wBottom));
                }
            }

            // Reading order: lines top-to-bottom (PDF Y grows upward, so larger Top first).
            // Each line chooses its own horizontal direction so mixed-language pages work too.
            lineWords.Sort((a, b) => b.Top.CompareTo(a.Top));

            // ---- #185: column-aware reading order ----------------------------------------------
            // A Y band spans the whole page, so on a two-column layout every "line" mixed both
            // columns and a drag down one column swept its neighbor. Split each band into segments
            // at column-gutter-sized gaps, cluster narrow segments into columns by X overlap, and
            // emit whole columns left-to-right (top-to-bottom inside each). Wide segments - titles
            // and footers spanning the text width - close the open column section, so a
            // title / two columns / footer page keeps a sane order. A single-column page yields
            // one cluster and comes out in exactly the old order.
            lineWords = OrderColumnAware(lineWords);

            int wordOrdinal = 0;
            for (int li = 0; li < lineWords.Count; li++)
            {
                var (ws, top, bottom) = lineWords[li];
                bool rtl = IsRightToLeftText(ws.Select(w => w.Text));
                ws.Sort(rtl
                    ? (a, b) => b.BoundingBox.Right.CompareTo(a.BoundingBox.Right)
                    : (a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));

                var line = new RunLine
                {
                    Start = result.Chars.Count,
                    Top = top,
                    Bottom = bottom,
                    RightToLeft = rtl,
                };
                foreach (var w in ws)
                {
                    var letters = w.Letters.ToList();
                    letters.Sort(rtl
                        ? (a, b) => b.BoundingBox.Right.CompareTo(a.BoundingBox.Right)
                        : (a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
                    foreach (var letter in letters)
                    {
                        var g = letter.BoundingBox;
                        result.Chars.Add(new RunChar(letter.Value, g.Left, g.Right, wordOrdinal, li));
                    }
                    wordOrdinal++;
                }
                line.Count = result.Chars.Count - line.Start;
                if (line.Count == 0) continue;
                line.Left = result.Chars.Skip(line.Start).Take(line.Count).Min(c => c.Left);
                line.Right = result.Chars.Skip(line.Start).Take(line.Count).Max(c => c.Right);
                result.Lines.Add(line);
            }
            return result;
        }

        internal static bool IsRightToLeftText(IEnumerable<string> values)
        {
            int rtl = 0, ltr = 0;
            foreach (string value in values)
            {
                foreach (char c in value)
                {
                    if ((c >= '\u0590' && c <= '\u08FF') ||
                        (c >= '\uFB1D' && c <= '\uFDFF') ||
                        (c >= '\uFE70' && c <= '\uFEFF')) rtl++;
                    else if (char.IsLetter(c)) ltr++;
                }
            }
            return rtl > ltr;
        }

        /// <summary>True when the point sits ON text: inside a line's vertical band and within its
        /// horizontal extent (small slop). This is the gate that decides flowing selection vs the
        /// classic marquee - empty page areas must keep the marquee.</summary>
        public static bool IsOverText(PageTextRuns runs, double x, double y)
        {
            const double slop = 2.0;   // PDF points
            foreach (var line in runs.Lines)
                if (y <= line.Top + slop && y >= line.Bottom - slop &&
                    x >= line.Left - slop && x <= line.Right + slop)
                    return true;
            return false;
        }

        /// <summary>Caret position (0..Chars.Count) nearest a point, browser-style clamping:
        /// above the first line selects from the page start, below the last line to the page end,
        /// between lines snaps to the closer line, beyond a line's ends clamps to its ends.</summary>
        public static int CaretFromPoint(PageTextRuns runs, double x, double y)
        {
            if (runs.Lines.Count == 0) return 0;

            RunLine? target = null;
            double bestVertical = double.MaxValue;
            double bestHorizontal = double.MaxValue;
            foreach (var line in runs.Lines)
            {
                double vertical = y > line.Top ? y - line.Top
                    : y < line.Bottom ? line.Bottom - y : 0;
                double horizontal = x < line.Left ? line.Left - x
                    : x > line.Right ? x - line.Right : 0;
                if (vertical < bestVertical
                    || (vertical == bestVertical && horizontal < bestHorizontal))
                {
                    bestVertical = vertical;
                    bestHorizontal = horizontal;
                    target = line;
                }
            }
            // Above the first line entirely -> caret 0; below the last -> caret N.
            var first = runs.Lines[0];
            var last = runs.Lines[^1];
            if (y > first.Top && target == first && x < first.Left) return 0;
            if (y < last.Bottom && target == last && x > last.Right) return runs.Chars.Count;
            if (target is null) return 0;

            if (target.RightToLeft)
            {
                if (x >= target.Right) return target.Start;
                if (x <= target.Left) return target.End;
                for (int i = target.Start; i < target.End; i++)
                {
                    var c = runs.Chars[i];
                    double mid = (c.Left + c.Right) / 2;
                    if (x > mid) return i;
                }
                return target.End;
            }

            if (x <= target.Left) return target.Start;
            if (x >= target.Right) return target.End;
            for (int i = target.Start; i < target.End; i++)
            {
                var c = runs.Chars[i];
                double mid = (c.Left + c.Right) / 2;
                if (x < mid) return i;
            }
            return target.End;
        }

        /// <summary>Text for the caret range [start, end): spaces between words, newlines between
        /// lines. Also reports how many distinct words the range touches.</summary>
        public static string TextForRange(PageTextRuns runs, int start, int end, out int wordCount)
        {
            wordCount = 0;
            var sb = new System.Text.StringBuilder();
            int lastWord = -1, lastLine = -1;
            for (int i = Math.Max(0, start); i < Math.Min(end, runs.Chars.Count); i++)
            {
                var c = runs.Chars[i];
                if (lastLine >= 0 && c.Line != lastLine) sb.Append('\n');
                else if (lastWord >= 0 && c.Word != lastWord) sb.Append(' ');
                if (c.Word != lastWord) wordCount++;
                sb.Append(c.Value);
                lastWord = c.Word;
                lastLine = c.Line;
            }
            return sb.ToString();
        }
    }
}
