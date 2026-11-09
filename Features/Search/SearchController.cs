using KillerPDF.Services;

namespace KillerPDF.Features
{
    /// <summary>
    /// The document-search state machine: runs SearchService over the working file, keeps the
    /// per-page rect map and the flat reading-ordered match list, and steps the match cursor.
    /// Moved out of Search.cs (MainWindow) in the KillerUI refactor.
    ///
    /// Holds no controls. Talks to the window only through <see cref="ISearchHost"/>; the search
    /// bar UI, debounce, and the highlight painting stay in the shell half (Shell/Search.cs).
    /// </summary>
    internal sealed class SearchController
    {
        private readonly ISearchHost _host;
        private readonly SearchService _searchService = new();

        // The search runs on a worker, so a long document no longer holds the window. Only this
        // controller's UI-thread code starts or cancels it, and results land back on the UI thread.
        private readonly LatestSearchRunner _runner = new();
        private string? _searchedFile;
        private long _resultRevision = -1;
        private Guid? _resultNativeRevisionId;
        internal bool ResultsCurrent => _searchedFile == _host.CurrentFile && _resultRevision == _host.SearchRevision
            && _resultNativeRevisionId == _host.NativeSearchRevisionId;

        internal SearchController(ISearchHost host) => _host = host;

        // Whole-document search results (PDF-space rects per page). Settable references on
        // purpose: Tabs.cs parks these per tab and swaps them back on a tab switch, exactly as
        // it did when they were MainWindow fields.
        internal Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects { get; set; } = [];
        internal List<int> ResultPages { get; set; } = [];
        internal int PageCursor { get; set; } = -1;

        /// <summary>Zero-based pages the last search could not read. A search that found nothing on the
        /// others has not shown the term is absent from these, so every result view names them.</summary>
        internal IReadOnlyList<int> UnreadablePages { get; set; } = [];

        // Flat, reading-ordered list of every match (page + rect) so Enter steps word-by-word rather
        // than page-by-page; _matchCursor indexes it and that match is drawn with extra emphasis.
        private readonly List<SearchHit> _matches = [];
        private int _matchCursor = -1;
        private int _totalHits;

        /// <summary>True while any page has result rects - the F3 and repaint-on-page-change gate.</summary>
        internal bool HasResults => _matches.Count > 0;

        /// <summary>Match case and Whole word, the search bar toggles. Find and Replace uses the same
        /// options, so what it replaces is what the highlights showed.</summary>
        internal bool MatchCase { get; set; }
        internal bool WholeWord { get; set; }

        /// <summary>The text of the most recent search, for features that act on its exact matches.</summary>
        internal string LastQuery { get; private set; } = string.Empty;

        /// <summary>Match case and Whole word as that search ran. The bar's toggles can differ by now,
        /// after a tab switch or while a newer search runs.</summary>
        internal bool LastMatchCase { get; private set; }
        internal bool LastWholeWord { get; private set; }

        /// <summary>True while a search is on the worker. The results showing are still the older ones.</summary>
        internal bool IsSearching => _runner.IsRunning;

        /// <summary>The emphasized match, or null when the cursor is not on one.</summary>
        internal (int page, double left, double bottom, double right, double top)? CurrentMatch =>
            CurrentOccurrence is { HasGeometry: true } occurrence ? occurrence.Rectangle : null;

        internal SearchHit? CurrentOccurrence =>
            _matchCursor >= 0 && _matchCursor < _matches.Count ? _matches[_matchCursor] : null;

        internal bool IsCurrentArea(int page, KillerPdf.Engine.Documents.PdfContentBounds area)
        {
            if (CurrentOccurrence is not { HasGeometry: true } current || current.page != page) return false;
            return current.Areas.Count > 0 ? current.Areas.Contains(area)
                : current.left == area.Left && current.bottom == area.Bottom
                    && current.right == area.Right && current.top == area.Top;
        }

        /// <summary>The page's highlight rectangles. A native EPUB page whose highlight areas are not read yet
        /// has none; asking starts reading them, and the highlights repaint when they arrive.</summary>
        internal bool TryGetPageRects(int page,
            out List<(double left, double bottom, double right, double top)> rects)
        {
            if (AllSearchRects.TryGetValue(page, out rects!)) return true;
            Locate(page);
            return false;
        }

        /// <summary>The pages the painter offers to <see cref="TryGetPageRects"/>: every page with a hit while
        /// some pages' highlight areas are still to be read, otherwise the pages with rectangles.</summary>
        internal IEnumerable<int> PagesWithResults => _locator is not null ? ResultPages : AllSearchRects.Keys;

        // A native EPUB search lists its hits from page text alone. A page's highlight areas are read on a worker when
        // the reader looks at it (the current hit's page, and each page on screen), then the highlights repaint.
        private PdfCapturedReadRevision? _locator;
        private readonly HashSet<int> _unlocatedPages = [];
        private readonly HashSet<int> _locating = [];
        private int _locateGeneration;

        // Forgets the pages waiting for their areas; a reading still running for them is dropped when it lands.
        private void ForgetLocation()
        {
            _locator = null;
            _unlocatedPages.Clear();
            _locating.Clear();
            _locateGeneration++;
        }

        private void TrackLocation(PdfCapturedReadRevision? locator)
        {
            ForgetLocation();
            foreach (SearchHit hit in _matches)
                if (!hit.Located) _unlocatedPages.Add(hit.page);
            if (_unlocatedPages.Count > 0 && locator is not null) _locator = locator;
            else _unlocatedPages.Clear();
        }

        private void Locate(int page)
        {
            if (_locator is not { } revision || !_unlocatedPages.Contains(page) || !_locating.Add(page)) return;
            _ = LocateAsync(revision, page, [.. HitsOn(page)], _locateGeneration);
        }

        private async Task LocateAsync(PdfCapturedReadRevision revision, int page, SearchHit[] hits, int generation)
        {
            IReadOnlyList<SearchHit>? located = null;
            try { located = await Task.Run(() => SearchService.LocateNative(revision, page, hits, CancellationToken.None)); }
            // A closed book or a page that cannot be laid out keeps its hits counted, without highlights.
            catch (Exception) { }
            if (generation != _locateGeneration) return;
            _locating.Remove(page);
            _unlocatedPages.Remove(page);
            located ??= [.. hits.Select(hit => hit with { Located = true, HasGeometry = false })];
            var byPosition = located.ToDictionary(hit => (hit.Start, hit.Length));
            for (int at = FirstMatchOn(page); at < _matches.Count && _matches[at].page == page; at++)
                if (byPosition.TryGetValue((_matches[at].Start, _matches[at].Length), out var hit)) _matches[at] = hit;
            var rectangles = located.Where(hit => hit.HasGeometry)
                .SelectMany(hit => hit.Areas.Select(area => (area.Left, area.Bottom, area.Right, area.Top))).ToList();
            if (rectangles.Count > 0) AllSearchRects[page] = rectangles;
            _host.RepaintHighlights();
        }

        private IEnumerable<SearchHit> HitsOn(int page)
        {
            for (int at = FirstMatchOn(page); at < _matches.Count && _matches[at].page == page; at++) yield return _matches[at];
        }

        // A native match list is in page order, so the first match on a page is found by halving.
        private int FirstMatchOn(int page)
        {
            int low = 0, high = _matches.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (_matches[middle].page < page) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        /// <summary>The part of a search a tab parks through <see cref="Capture"/>. Its rects, pages
        /// and page cursor already park as fields of their own on the tab's session.</summary>
        internal sealed record ParkedSearch(string Query, bool MatchCase, bool WholeWord,
            IReadOnlyList<SearchHit> Matches, int MatchCursor)
        {
            internal Guid? NativeRevisionId { get; init; }
            internal string? File { get; init; }
            internal long Revision { get; init; } = -1;
            /// <summary>The native book the matches' highlight areas are still read from, when some are.</summary>
            internal PdfCapturedReadRevision? Locator { get; init; }
        }

        internal ParkedSearch Capture() =>
            new(LastQuery, LastMatchCase, LastWholeWord, [.. _matches], _matchCursor)
                { NativeRevisionId = _resultNativeRevisionId, File = _searchedFile, Revision = _resultRevision, Locator = _locator };

        /// <summary>Puts back what <see cref="Capture"/> parked for the tab coming in, or a blank
        /// state for a tab that has never searched.</summary>
        internal void Restore(ParkedSearch? parked)
        {
            if (parked is not null && (parked.NativeRevisionId != _host.NativeSearchRevisionId
                || parked.NativeRevisionId is not null && parked.Revision != _host.SearchRevision))
            {
                ClearMatches();
                _host.ClearHighlights();
                return;
            }
            // Another document coming in ends a search for the one going out. A pane that only
            // re-applies its own session keeps its search running.
            if (_host.CurrentFile != _searchedFile) CancelPending();
            LastQuery = parked?.Query ?? string.Empty;
            LastMatchCase = parked?.MatchCase ?? false;
            LastWholeWord = parked?.WholeWord ?? false;
            _matches.Clear();
            if (parked is not null) _matches.AddRange(parked.Matches);
            TrackLocation(parked?.Locator);
            _matchCursor = parked?.MatchCursor ?? -1;
            _resultNativeRevisionId = parked?.NativeRevisionId;
            if (parked?.NativeRevisionId is not null)
            {
                _searchedFile = parked.File;
                _resultRevision = parked.Revision;
            }
        }

        /// <summary>Stops a running search and leaves the results on show as they are.</summary>
        internal void CancelPending() => _runner.Cancel();

        /// <summary>The query-too-short reset (search box text dropped under 2 chars).</summary>
        internal void ClearMatches()
        {
            CancelPending();
            ForgetLocation();
            AllSearchRects.Clear();
            ResultPages.Clear();
            _matches.Clear();
            _matchCursor = -1;
            PageCursor = -1;
            UnreadablePages = [];
        }

        /// <summary>The new/closed-document reset. Nothing here describes the next document, so the
        /// query and the match list go with the rects.</summary>
        internal void ClearPageResults()
        {
            ClearMatches();
            LastQuery = string.Empty;
        }

        /// <summary>
        /// Searches the working file on a worker. What is showing stays until the result arrives, and a
        /// search stopped by <see cref="CancelPending"/>, overtaken by a newer one or outlived by its
        /// document leaves it there untouched.
        /// </summary>
        internal void Run(string query) => _ = RunAsync(query);

        // Finishes once the result has landed or been dropped. It handles its own failures.
        private async Task RunAsync(string query)
        {
            CancelPending();
            if (string.IsNullOrWhiteSpace(query) || _host.CurrentFile is not { } file)
            {
                ClearResults(query ?? string.Empty, MatchCase, WholeWord);
                _host.SetResultText("");
                return;
            }

            (bool matchCase, bool wholeWord) = (MatchCase, WholeWord);
            try
            {
                // Read here rather than on the worker, so nothing holds the working file open while an
                // edit rewrites it and the worker searches one version from start to end.
                FileRevision? revision = Revision(file);
                CurrentSearchSnapshot source = _host.CaptureSearchSnapshot();
                long editRevision = _host.SearchRevision;
                _searchedFile = file;
                SearchResult? found = await _runner.RunAsync(
                    token => SearchService.Search(source, query, matchCase, wholeWord, token),
                    () => _host.CurrentFile == file && Revision(file) == revision && _host.SearchRevision == editRevision
                        && _host.NativeSearchRevisionId == source.NativeRevision?.Id);
                if (found is null) return;
                _resultRevision = editRevision;
                _resultNativeRevisionId = found.NativeRevisionId;
                ShowResults(query, matchCase, wholeWord, found, source.NativeRevision);
            }
            catch
            {
                ClearResults(query, matchCase, wholeWord);
                _host.SetResultText(_host.Loc("Str_Search_Error"));
            }
        }

        // Which version of the file a search read. Length and write time, not the full hash a save
        // checks, because the UI thread reads this after every search.
        private static FileRevision? Revision(string file)
        {
            var info = new System.IO.FileInfo(file);
            return info.Exists ? new FileRevision(info.Length, info.LastWriteTimeUtc) : null;
        }

        private void ClearResults(string query, bool matchCase, bool wholeWord)
        {
            LastQuery = query;
            LastMatchCase = matchCase;
            LastWholeWord = wholeWord;
            _host.ClearHighlights();
            ForgetLocation();
            AllSearchRects.Clear();
            ResultPages.Clear();
            _matches.Clear();
            _matchCursor = -1;
            PageCursor = -1;
            UnreadablePages = [];
        }

        private void ShowResults(string query, bool matchCase, bool wholeWord, SearchResult sr,
            PdfCapturedReadRevision? locator)
        {
            ClearResults(query, matchCase, wholeWord);
            UnreadablePages = [.. sr.UnreadablePages];

            // Text hidden under an edit, such as a replaced line, no longer counts as a match.
            foreach (var group in sr.Hits.GroupBy(hit => hit.page))
            {
                var visible = group.Where(hit => hit.Source is not null || !_host.IsCovered(hit.page, hit.left, hit.bottom, hit.right, hit.top)).ToList();
                if (visible.Count == 0) continue;
                var rectangles = visible.Where(hit => hit.HasGeometry)
                    .SelectMany(hit => hit.Areas.Count > 0
                        ? hit.Areas.Select(area => (area.Left, area.Bottom, area.Right, area.Top))
                        : new[] { (hit.left, hit.bottom, hit.right, hit.top) }).ToList();
                if (rectangles.Count > 0) AllSearchRects[group.Key] = rectangles;
                ResultPages.Add(group.Key);
            }
            ResultPages.Sort();

            // Flatten every match into one reading-ordered list (page asc, then top-to-bottom,
            // then left-to-right) so navigation steps word-by-word across the whole document.
            var ordered = sr.NativeRevisionId is not null
                ? sr.Hits.OrderBy(hit => hit.page).ThenBy(hit => hit.Start)
                : sr.Hits.OrderBy(hit => hit.page).ThenByDescending(hit => hit.top).ThenBy(hit => hit.left);
            foreach (SearchHit hit in ordered)
                if (hit.Source is not null || !_host.IsCovered(hit.page, hit.left, hit.bottom, hit.right, hit.top)) _matches.Add(hit);
            TrackLocation(locator);

            if (_matches.Count == 0)
            {
                // Pages that could not be read were not searched, so this is not a plain "No matches".
                if (UnreadablePages.Count == 0)
                {
                    _host.SetResultText(_host.Loc("Str_Search_NoMatches"));
                    return;
                }
                _host.SetResultText(string.Format(_host.Loc("Str_Search_NoMatchesPartial"), UnreadablePages.Count));
                _host.SetStatus(SkippedPagesText());
                return;
            }

            _totalHits = _matches.Count;

            // Start at the first match on or after the current page.
            int startPage = _host.CurrentPageIndex;
            _matchCursor = _matches.FindIndex(m => m.page >= startPage);
            if (_matchCursor < 0) _matchCursor = 0;

            GoToCurrentMatch();
            if (UnreadablePages.Count > 0) _host.SetStatus(SkippedPagesText());
        }

        internal void Next()
        {
            if (_matches.Count == 0) return;
            _matchCursor = (_matchCursor + 1) % _matches.Count;
            GoToCurrentMatch();
        }

        internal void Prev()
        {
            if (_matches.Count == 0) return;
            _matchCursor = (_matchCursor - 1 + _matches.Count) % _matches.Count;
            GoToCurrentMatch();
        }

        // Navigates to the current match's page (if needed), updates the counter, and repaints
        // highlights with the current match emphasized. Shared by Run and Next/Prev.
        private void GoToCurrentMatch()
        {
            if (_matchCursor < 0 || _matchCursor >= _matches.Count) return;
            int targetPage = _matches[_matchCursor].page;
            PageCursor = ResultPages.IndexOf(targetPage);   // keep the persisted page-cursor sane
            UpdateStatus();
            if (_host.CurrentPageIndex != targetPage)
                _host.GoToPage(targetPage);
            Locate(targetPage);
            _host.RepaintHighlights();
        }

        // Compact count ("12 / 73" = current match / total matches); page breakdown in the tooltip.
        private void UpdateStatus()
        {
            if (_matches.Count == 0)
            {
                _host.SetResultCount(_host.Loc("Str_Search_NoMatches"), null);
                return;
            }
            int pages = ResultPages.Count;
            string summary = string.Format(_host.Loc("Str_Search_Summary"), _matches.Count, pages);
            if (UnreadablePages.Count > 0) summary += "\n" + SkippedPagesText();
            _host.SetResultCount($"{_matchCursor + 1} / {_matches.Count}", summary);
        }

        private string SkippedPagesText() => SkippedPagesMessage(UnreadablePages);

        /// <summary>Names the (zero-based) pages that were not searched because they could not be read,
        /// for the search bar, the status line and the Find-and-Redact warning.</summary>
        internal string SkippedPagesMessage(IEnumerable<int> pages) =>
            string.Format(_host.Loc("Str_Search_PagesSkipped"), SearchService.SkippedPagesText(pages));
    }
}
