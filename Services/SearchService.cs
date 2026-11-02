using PdfDocument = KillerPDF.Services.PdfContentDocument;

namespace KillerPDF.Services
{
    internal sealed record SearchHit(int page, double left, double bottom, double right, double top,
        int Start, int Length)
    {
        internal SearchTextSource? Source { get; init; }
        internal (int page, double left, double bottom, double right, double top) Rectangle =>
            (page, left, bottom, right, top);
    }

    internal sealed record SearchTextSource(int Page, double Left, double Bottom, double Right, double Top,
        string Content, Guid AnnotationId, string? FieldName = null, bool Editable = true)
    {
        internal string? WidgetId { get; init; }
        internal bool Findable { get; init; } = true;
        internal int MaximumLength { get; init; }
        internal KillerPdf.Engine.Documents.PdfContentBounds? RedactionBounds { get; init; }
    }

    internal sealed record CurrentSearchSnapshot(byte[] Bytes, IReadOnlyList<SearchTextSource> Text)
    {
        internal IReadOnlyList<SearchTextSource> Covers { get; init; } = [];
        /// <summary>The protection of the tab the bytes came from, so a file read under its key searches.</summary>
        internal OpenedProtection? Protection { get; init; }
        internal IReadOnlyList<int> UnreadablePages { get; init; } = [];
        internal string Fingerprint()
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            hash.AppendData(Bytes);
            object Describe(SearchTextSource source) => new { source.Page, source.Left, source.Bottom, source.Right, source.Top,
                source.Content, source.AnnotationId, source.FieldName, source.Editable, source.WidgetId, source.RedactionBounds, source.Findable, source.MaximumLength };
            hash.AppendData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                { Text = Text.Select(Describe), Covers = Covers.Select(Describe), UnreadablePages }));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        internal bool SameContent(CurrentSearchSnapshot other) => Bytes.AsSpan().SequenceEqual(other.Bytes)
            && Text.SequenceEqual(other.Text) && Covers.SequenceEqual(other.Covers) && UnreadablePages.SequenceEqual(other.UnreadablePages);
        internal bool IsCovered(int page, KillerPdf.Engine.Documents.PdfContentBounds bounds) => Covers.Any(cover =>
            cover.Page == page && Math.Max(0, Math.Min(bounds.Right, cover.Right) - Math.Max(bounds.Left, cover.Left))
                * Math.Max(0, Math.Min(bounds.Top, cover.Top) - Math.Max(bounds.Bottom, cover.Bottom))
                > bounds.Width * bounds.Height * .5);
    }

    internal sealed class SearchResult
    {
        internal List<SearchHit> Hits { get; } = [];
        public Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> PageRects { get; } = [];
        public List<int> ResultPages { get; } = [];
        public int TotalHits { get; set; }

        /// <summary>
        /// Zero-based pages whose text could not be read, all of it or only part. A search that found
        /// nothing on the other pages has not shown the term is absent from these. A page that lost only
        /// part of its content keeps the matches found in the rest, which are in <see cref="PageRects"/>.
        /// </summary>
        public List<int> UnreadablePages { get; } = [];

        /// <summary>Why each page in <see cref="UnreadablePages"/> was skipped, by page.</summary>
        public Dictionary<int, string> UnreadableMessages { get; } = [];
    }

    /// <summary>
    /// Stops a redaction that could not search every page, because finishing would leave a term on those
    /// pages while the run reports nothing to remove. Both lists hold zero-based pages.
    /// </summary>
    internal sealed class RedactionPagesSkippedException(IReadOnlyList<int> fieldPages, IReadOnlyList<int> contentPages)
        : InvalidOperationException(Describe(fieldPages, contentPages,
            "Redaction stopped because the form fields or comments on these pages could not be read, so a term there might be missed: {0}",
            "Unreadable pages, not searched: {0}"))
    {
        /// <summary>Pages with a form field or comment that could not be read.</summary>
        internal IReadOnlyList<int> FieldPages { get; } = fieldPages;

        /// <summary>Pages whose content could not be read.</summary>
        internal IReadOnlyList<int> ContentPages { get; } = contentPages;

        /// <summary>The same message from translated text, each with the page list as {0}.</summary>
        internal string Localized(string fieldPagesText, string contentPagesText) =>
            Describe(FieldPages, ContentPages, fieldPagesText, contentPagesText);

        private static string Describe(IReadOnlyList<int> fieldPages, IReadOnlyList<int> contentPages,
            string fieldPagesText, string contentPagesText)
        {
            var parts = new List<string>();
            if (fieldPages.Count > 0) parts.Add(string.Format(fieldPagesText, SearchService.SkippedPagesText(fieldPages)));
            if (contentPages.Count > 0) parts.Add(string.Format(contentPagesText, SearchService.SkippedPagesText(contentPages)));
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Runs one search at a time off the UI thread. A newer run cancels the one before it, and a run
    /// that was cancelled, was overtaken or whose document changed delivers nothing, so old results
    /// never land on a tab that has moved on. Start and cancel it from the UI thread.
    /// </summary>
    internal sealed class LatestSearchRunner
    {
        private CancellationTokenSource? _running;

        /// <summary>True while a run has not delivered or been dropped.</summary>
        internal bool IsRunning => _running is not null;

        internal void Cancel()
        {
            _running?.Cancel();
            _running = null;
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a worker and returns its result on the calling context, or
        /// null when the result must be dropped. <paramref name="stillCurrent"/> says on that context
        /// whether the result still applies. The failure of a run that still counts is thrown.
        /// </summary>
        internal async Task<T?> RunAsync<T>(Func<CancellationToken, T> work, Func<bool> stillCurrent) where T : class
        {
            Cancel();
            using var run = new CancellationTokenSource();
            _running = run;
            bool Delivers() => ReferenceEquals(_running, run) && stillCurrent();
            try
            {
                T result = await Task.Run(() => work(run.Token), run.Token);
                return Delivers() ? result : null;
            }
            catch (Exception) when (!Delivers()) { return null; }
            finally
            {
                if (ReferenceEquals(_running, run)) _running = null;
            }
        }
    }

    internal sealed class SearchService
    {
        internal static (IReadOnlyList<KillerPdf.Engine.Documents.PdfRedactionMatch> Matches, IReadOnlyList<int> Unreadable)
            FindCurrentRedaction(CurrentSearchSnapshot snapshot, KillerPdf.Engine.Documents.PdfRedactionSearchOptions options)
        {
            var (pages, unreadable) = LoadPagesForReview(snapshot.Bytes, snapshot.Protection);
            var result = KillerPdf.Engine.Documents.PdfRedactionSearch.Find(pages, options).Matches
                .Where(match => !(match.Areas.Count > 0 ? match.Areas : [match.Bounds])
                    .Any(area => snapshot.IsCovered(match.PageIndex, area))).ToList();
            var annotations = KillerPdf.Engine.Documents.PdfRedactionSearch.FindInAnnotations(
                PdfEngineIntegration.OpenForReading(snapshot.Bytes, snapshot.Protection), options);
            result.AddRange(annotations.Matches.Where(match => !snapshot.Text.Any(source => source.WidgetId == match.Id)
                && !snapshot.IsCovered(match.PageIndex, match.Bounds)));
            foreach (SearchTextSource source in snapshot.Text)
                foreach (var match in KillerPdf.Engine.Documents.PdfRedactionSearch.FindInText(source.Content, options))
                    result.Add(new KillerPdf.Engine.Documents.PdfRedactionMatch(
                        $"current:{source.WidgetId ?? source.AnnotationId.ToString()}:{source.Page}:{match.Start}:{match.Length}",
                        source.Page, match.Value, source.RedactionBounds ?? new(source.Left, source.Bottom, source.Right, source.Top),
                        -1, 0, options.ReasonCode, options.OverlayText,
                        source.FieldName is null ? KillerPdf.Engine.Documents.PdfRedactionTargetKind.Text
                            : KillerPdf.Engine.Documents.PdfRedactionTargetKind.FormField));
            return (result.AsReadOnly(), unreadable.Concat(annotations.UnreadableAnnotationPages)
                .Concat(snapshot.UnreadablePages).Distinct().Order().ToArray());
        }

        internal static SearchResult Search(CurrentSearchSnapshot snapshot, string query, bool matchCase,
            bool wholeWord, CancellationToken cancellationToken)
        {
            SearchResult result = Search(snapshot.Bytes, snapshot.Protection, query, matchCase, wholeWord, cancellationToken);
            foreach (int page in snapshot.UnreadablePages)
                if (!result.UnreadablePages.Contains(page)) result.UnreadablePages.Add(page);
            foreach (SearchTextSource source in snapshot.Text.Where(source => source.Findable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var span in TextReplacePlanner.FindSpans(source.Content, query, matchCase, wholeWord))
                {
                    result.Hits.Add(new SearchHit(source.Page, source.Left, source.Bottom, source.Right, source.Top,
                        span.Start, span.Length) { Source = source });
                    if (!result.PageRects.TryGetValue(source.Page, out var rects))
                    {
                        result.PageRects[source.Page] = rects = [];
                        result.ResultPages.Add(source.Page);
                    }
                    rects.Add((source.Left, source.Bottom, source.Right, source.Top));
                    result.TotalHits++;
                }
            }
            return result;
        }

        // The reader's notes on a page that lost content to a stream nothing could decode: missing when
        // recovery read the stream as empty, truncated when it gave up on the streams after it. The engine
        // keeps its constants internal; the tests in SearchRedactionCallersTests fail if these drift.
        private static readonly string[] LostContentNotes =
        [
            "Page content is missing because a stream could not be decoded.",
            "Page content was truncated because a stream could not be decoded."
        ];

        /// <summary>
        /// Scans every page of <paramref name="filePath"/> for <paramref name="query"/>, ignoring case
        /// unless <paramref name="matchCase"/> is set; <paramref name="wholeWord"/> skips matches inside
        /// longer words. Returns an empty result when query is blank. A file that cannot be opened, or whose
        /// pages all fail to read, throws, because an empty result would tell the user the term is absent. A
        /// page the text reader refuses, or that lost content to a stream nothing could decode, is listed in
        /// <see cref="SearchResult.UnreadablePages"/>, so one bad page does not cost the matches on every
        /// other page and never reads as a page without the term.
        /// </summary>
        public static SearchResult Search(string filePath, string query, bool matchCase = false, bool wholeWord = false) =>
            string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(filePath)
                ? new SearchResult()
                : Search(System.IO.File.ReadAllBytes(filePath), null, query, matchCase, wholeWord, CancellationToken.None);

        /// <summary>
        /// The same search over the file's bytes, for a worker that must not hold the working file open
        /// while an edit rewrites it. <paramref name="cancellationToken"/> stops it at the next page.
        /// </summary>
        internal static SearchResult Search(byte[] source, OpenedProtection? protection, string query, bool matchCase,
            bool wholeWord, CancellationToken cancellationToken)
        {
            var result = new SearchResult();
            if (string.IsNullOrWhiteSpace(query)) return result;

            using var doc = PdfDocument.Open(source, protection);
            var failures = new PageFailures();
            for (int pi = 0; pi < doc.NumberOfPages; pi++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                KillerPdf.Engine.Documents.PdfPageContent page;
                List<(double Left, double Bottom, double Right, double Top)> hits;
                try
                {
                    page = doc.GetPage(pi + 1);
                    var occurrences = FindHitsOnPage(page, pi, query, matchCase, wholeWord);
                    result.Hits.AddRange(occurrences);
                    hits = [.. occurrences.Select(hit => (hit.left, hit.bottom, hit.right, hit.top))];
                }
                catch (Exception ex) when (CanSkipPage(ex))
                {
                    result.UnreadablePages.Add(pi);
                    result.UnreadableMessages[pi] = ex.Message;
                    failures.Refused(ex);
                    continue;
                }
                if (failures.LostContent(page) is { } lost)
                {
                    result.UnreadablePages.Add(pi);
                    result.UnreadableMessages[pi] = lost;
                }
                if (hits.Count > 0)
                {
                    result.PageRects[pi] = hits;
                    result.ResultPages.Add(pi);
                    result.TotalHits += hits.Count;
                }
            }

            failures.RequireAReadablePage(doc.NumberOfPages);
            return result;
        }

        // A page the reader refuses is skipped; running out of memory or being cancelled is not that.
        private static bool CanSkipPage(Exception ex) => ex is not (OutOfMemoryException or OperationCanceledException);

        // What a pass over the whole document could not read. A page the reader refuses is a failure. A
        // page that lost a stream was only partly read: what did decode still counts, so it is reported,
        // and it is a failure only when nothing on it could be read.
        private sealed class PageFailures
        {
            private int _count;
            private Exception? _first;

            internal void Refused(Exception failure)
            {
                _count++;
                _first ??= failure;
            }

            // The reader's note when this page lost content, otherwise null.
            internal string? LostContent(KillerPdf.Engine.Documents.PdfPageContent page)
            {
                string? lost = page.Diagnostics.FirstOrDefault(LostContentNotes.Contains);
                if (lost is not null && page.Words.Count == 0) Refused(new System.IO.InvalidDataException(lost));
                return lost;
            }

            // Every page failing is a file that cannot be searched, not a file without the term.
            internal void RequireAReadablePage(int pageCount)
            {
                if (_first is not null && _count == pageCount)
                    throw new System.IO.InvalidDataException("No page of the document could be read: " + _first.Message, _first);
            }
        }

        /// <summary>
        /// The one-based page numbers of <paramref name="zeroBasedPages"/> as a comma-separated list for a
        /// message, cut after ten so a document that fails everywhere does not fill the status bar.
        /// </summary>
        internal static string SkippedPagesText(IEnumerable<int> zeroBasedPages)
        {
            const int Shown = 10;
            List<int> pages = [.. zeroBasedPages.Select(page => page + 1)];
            string text = string.Join(", ", pages.Take(Shown).Select(page => page.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return pages.Count > Shown ? text + ", ..." : text;
        }

        internal static List<(double Left, double Bottom, double Right, double Top)> FindMatchesOnPage(
            KillerPdf.Engine.Documents.PdfPageContent page, string query, bool matchCase = false, bool wholeWord = false)
            => [.. FindHitsOnPage(page, 0, query, matchCase, wholeWord)
                .Select(hit => (hit.left, hit.bottom, hit.right, hit.top))];

        private static List<SearchHit> FindHitsOnPage(KillerPdf.Engine.Documents.PdfPageContent page,
            int pageIndex, string query, bool matchCase, bool wholeWord)
        {
            var result = new List<SearchHit>();
            query = KillerPdf.Engine.Documents.PdfSearchText.Fold(query);
            if (query.Length == 0) return result;
            var options = new KillerPdf.Engine.Documents.PdfRedactionSearchOptions
            {
                Query = wholeWord ? @"(?<![\p{L}\p{Nd}_])" + System.Text.RegularExpressions.Regex.Escape(query)
                    + @"(?![\p{L}\p{Nd}_])" : query,
                Kind = wholeWord ? KillerPdf.Engine.Documents.PdfRedactionSearchKind.RegularExpression
                    : KillerPdf.Engine.Documents.PdfRedactionSearchKind.ExactText,
                MatchCase = matchCase
            };
            foreach (var match in KillerPdf.Engine.Documents.PdfRedactionSearch.Find([page], options).Matches)
            {
                var bounds = match.Areas.Count > 0
                    ? new KillerPdf.Engine.Documents.PdfContentBounds(match.Areas.Min(area => area.Left),
                        match.Areas.Min(area => area.Bottom), match.Areas.Max(area => area.Right), match.Areas.Max(area => area.Top))
                    : match.Bounds;
                string[] identity = match.Id.Split(':');
                result.Add(new SearchHit(pageIndex, bounds.Left, bounds.Bottom, bounds.Right, bounds.Top,
                    int.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(identity[2], System.Globalization.CultureInfo.InvariantCulture)));
            }
            return result;
        }

        /// <summary>
        /// Finds every case-insensitive occurrence of <paramref name="query"/> on every page and
        /// returns the bounds of only the matched characters, one rectangle per word a match touches. A page
        /// that cannot be read throws, so the caller never proceeds as if that page had been searched.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindExactMatches(
            string filePath, string query) => FindExactMatches(filePath, query, false, false, null);

        /// <summary>
        /// The same search, except a page that cannot be read is left out and its zero-based number added to
        /// <paramref name="unreadablePages"/>, which the caller must show the user. Still throws when the
        /// file cannot be opened or no page can be read.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindExactMatches(
            string filePath, string query, out List<int> unreadablePages) =>
            FindExactMatches(filePath, query, false, false, out unreadablePages);

        /// <summary>
        /// The same search with the search bar's Match case and Whole word, so what it marks is what the
        /// search highlighted and not the same letters inside other words.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindExactMatches(
            string filePath, string query, bool matchCase, bool wholeWord, out List<int> unreadablePages)
        {
            unreadablePages = [];
            return FindExactMatches(filePath, query, matchCase, wholeWord, unreadablePages);
        }

        private static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindExactMatches(
            string filePath, string query, bool matchCase, bool wholeWord, List<int>? unreadablePages)
        {
            var result = new Dictionary<int, List<(double, double, double, double)>>();
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(filePath)) return result;
            // Marking matches for a redaction edits the document, so the file is a plain working copy.
            using var doc = PdfDocument.Open(filePath, null);
            var failures = new PageFailures();
            for (int pi = 0; pi < doc.NumberOfPages; pi++)
            {
                KillerPdf.Engine.Documents.PdfPageContent page;
                List<(double Left, double Bottom, double Right, double Top)> hits;
                try
                {
                    page = doc.GetPage(pi + 1);
                    hits = FindExactMatchesOnPage(page, query, matchCase, wholeWord);
                }
                catch (Exception ex) when (unreadablePages is not null && CanSkipPage(ex))
                {
                    unreadablePages.Add(pi);
                    failures.Refused(ex);
                    continue;
                }
                if (failures.LostContent(page) is { } lost)
                {
                    if (unreadablePages is null) throw new System.IO.InvalidDataException(lost);
                    unreadablePages.Add(pi);
                }
                if (hits.Count > 0) result[pi] = hits;
            }
            failures.RequireAReadablePage(doc.NumberOfPages);
            return result;
        }

        /// <summary>
        /// Every page's content for a review search such as Find and Redact. A page the reader refuses stays
        /// in the list as null, because the review numbers pages by position, and a page that lost content
        /// stays with what could be read. The zero-based number of either is returned for the caller to show
        /// the user. Throws when the file cannot be opened or no page can be read.
        /// </summary>
        internal static (IReadOnlyList<KillerPdf.Engine.Documents.PdfPageContent?> Pages, IReadOnlyList<int> Unreadable)
            LoadPagesForReview(string filePath, OpenedProtection? protection) =>
            LoadPagesForReview(System.IO.File.ReadAllBytes(filePath), protection);

        internal static (IReadOnlyList<KillerPdf.Engine.Documents.PdfPageContent?> Pages, IReadOnlyList<int> Unreadable)
            LoadPagesForReview(byte[] source, OpenedProtection? protection)
        {
            using var doc = PdfDocument.Open(source, protection);
            var pages = new List<KillerPdf.Engine.Documents.PdfPageContent?>(doc.NumberOfPages);
            var unreadable = new List<int>();
            var failures = new PageFailures();
            for (int pi = 0; pi < doc.NumberOfPages; pi++)
            {
                KillerPdf.Engine.Documents.PdfPageContent page;
                try { page = doc.GetPage(pi + 1); }
                catch (Exception ex) when (CanSkipPage(ex))
                {
                    pages.Add(null);
                    unreadable.Add(pi);
                    failures.Refused(ex);
                    continue;
                }
                pages.Add(page);
                if (failures.LostContent(page) is not null) unreadable.Add(pi);
            }
            failures.RequireAReadablePage(doc.NumberOfPages);
            return (pages, unreadable);
        }

        /// <summary>
        /// The areas a redaction of <paramref name="term"/> covers: its page-text matches, plus every form
        /// field and annotation whose value or text contains it, which the page content does not include.
        /// Case-insensitive, like <see cref="FindExactMatches(string, string)"/>.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindRedactionTermAreas(
            string filePath, string term) => FindRedactionTermAreas(filePath, [term]);

        /// <summary>
        /// The areas for several terms at once, merged per page. The file is read and parsed once for all
        /// of them; a macro with many terms on a large scan otherwise parsed it twice per term. A page it
        /// could not search throws <see cref="RedactionPagesSkippedException"/> once every page has been
        /// tried, so no redaction finishes having left a page out.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindRedactionTermAreas(
            string filePath, IEnumerable<string> terms)
        {
            var result = new Dictionary<int, List<(double, double, double, double)>>();
            if (string.IsNullOrWhiteSpace(filePath)) return result;
            List<string> queries = [.. terms.Where(term => !string.IsNullOrWhiteSpace(term))];
            if (queries.Count == 0) return result;
            byte[] bytes = System.IO.File.ReadAllBytes(filePath);
            // A redaction edits the document, so the file is a plain working copy or a command line input.
            var (pages, unreadableContent) = LoadPagesForReview(bytes, null);
            var document = PdfEngineIntegration.OpenForReading(bytes, null);
            var unreadableFields = new SortedSet<int>();
            foreach (string term in queries)
            {
                for (int pi = 0; pi < pages.Count; pi++)
                {
                    if (pages[pi] is not { } page) continue;
                    var hits = FindExactMatchesOnPage(page, term);
                    if (hits.Count > 0) Areas(pi).AddRange(hits);
                }
                var options = new KillerPdf.Engine.Documents.PdfRedactionSearchOptions
                {
                    Kind = KillerPdf.Engine.Documents.PdfRedactionSearchKind.ExactText,
                    Query = term.Trim(),
                    Timeout = System.TimeSpan.FromSeconds(2)
                };
                var annotations = KillerPdf.Engine.Documents.PdfRedactionSearch.FindInAnnotations(document, options);
                unreadableFields.UnionWith(annotations.UnreadableAnnotationPages);
                foreach (var match in annotations.Matches)
                {
                    var areas = Areas(match.PageIndex);
                    var area = (match.Bounds.Left, match.Bounds.Bottom, match.Bounds.Right, match.Bounds.Top);
                    if (!areas.Contains(area)) areas.Add(area);
                }
            }
            if (unreadableFields.Count > 0 || unreadableContent.Count > 0)
                throw new RedactionPagesSkippedException([.. unreadableFields], unreadableContent);
            return result;

            List<(double, double, double, double)> Areas(int page)
            {
                if (!result.TryGetValue(page, out var areas)) result[page] = areas = [];
                return areas;
            }
        }

        internal static List<(double Left, double Bottom, double Right, double Top)> FindExactMatchesOnPage(
            KillerPdf.Engine.Documents.PdfPageContent page, string query, bool matchCase = false, bool wholeWord = false)
        {
            string needle = KillerPdf.Engine.Documents.PdfSearchText.Fold(query).Trim();
            if (needle.Length == 0) return [];
            var result = new List<(double, double, double, double)>();
            foreach (SearchHit hit in FindHitsOnPage(page, 0, needle, matchCase, wholeWord))
            {
                int wordStart = 0;
                foreach (var word in page.Words)
                {
                    int wordEnd = wordStart + word.Text.Length;
                    if (wordEnd > hit.Start && wordStart < hit.Start + hit.Length)
                    {
                        var boxes = new List<KillerPdf.Engine.Documents.PdfContentBounds>();
                        int offset = wordStart;
                        foreach (var letter in word.Letters)
                        {
                            int end = offset + letter.Value.Length;
                            if (end > hit.Start && offset < hit.Start + hit.Length) boxes.Add(letter.BoundingBox);
                            offset = end;
                        }
                        var bounds = boxes.Count > 0 && offset == wordEnd
                            ? new KillerPdf.Engine.Documents.PdfContentBounds(boxes.Min(box => box.Left),
                                boxes.Min(box => box.Bottom), boxes.Max(box => box.Right), boxes.Max(box => box.Top))
                            : word.BoundingBox;
                        result.Add((bounds.Left, bounds.Bottom, bounds.Right, bounds.Top));
                    }
                    wordStart = wordEnd + 1;
                }
            }
            return result;
        }
    }
}
