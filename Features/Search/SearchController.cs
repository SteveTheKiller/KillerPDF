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

        internal SearchController(ISearchHost host) => _host = host;

        // Whole-document search results (PDF-space rects per page). Settable references on
        // purpose: Tabs.cs parks these per tab and swaps them back on a tab switch, exactly as
        // it did when they were MainWindow fields.
        internal Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects { get; set; } = [];
        internal List<int> ResultPages { get; set; } = [];
        internal int PageCursor { get; set; } = -1;

        // Flat, reading-ordered list of every match (page + rect) so Enter steps word-by-word rather
        // than page-by-page; _matchCursor indexes it and that match is drawn with extra emphasis.
        private readonly List<(int page, double left, double bottom, double right, double top)> _matches = [];
        private int _matchCursor = -1;
        private int _totalHits;

        /// <summary>True while any page has result rects - the F3 and repaint-on-page-change gate.</summary>
        internal bool HasResults => AllSearchRects.Count > 0;

        /// <summary>The emphasised match, or null when the cursor is not on one.</summary>
        internal (int page, double left, double bottom, double right, double top)? CurrentMatch =>
            _matchCursor >= 0 && _matchCursor < _matches.Count ? _matches[_matchCursor] : null;

        internal bool TryGetPageRects(int page,
            out List<(double left, double bottom, double right, double top)> rects) =>
            AllSearchRects.TryGetValue(page, out rects!);

        internal IEnumerable<int> PagesWithResults => AllSearchRects.Keys;

        /// <summary>The query-too-short reset (search box text dropped under 2 chars).</summary>
        internal void ClearMatches()
        {
            AllSearchRects.Clear();
            ResultPages.Clear();
            _matches.Clear();
            _matchCursor = -1;
            PageCursor = -1;
        }

        /// <summary>The new/closed-document reset - exactly the three things FileOperations
        /// cleared when these were fields (the match list is rebuilt by the next Run).</summary>
        internal void ClearPageResults()
        {
            AllSearchRects.Clear();
            ResultPages.Clear();
            PageCursor = -1;
        }

        internal void Run(string query)
        {
            _host.ClearHighlights();
            AllSearchRects.Clear();
            ResultPages.Clear();
            _matches.Clear();
            _matchCursor = -1;
            PageCursor = -1;

            if (string.IsNullOrWhiteSpace(query) || _host.CurrentFile is null)
            {
                _host.SetResultText("");
                return;
            }

            try
            {
                var sr = _searchService.Search(_host.CurrentFile, query);

                foreach (var kvp in sr.PageRects)
                    AllSearchRects[kvp.Key] = kvp.Value;
                ResultPages.AddRange(sr.ResultPages);

                // Flatten every match into one reading-ordered list (page asc, then top-to-bottom,
                // then left-to-right) so navigation steps word-by-word across the whole document.
                foreach (var page in ResultPages)
                    foreach (var (left, bottom, right, top) in AllSearchRects[page].OrderByDescending(r => r.top).ThenBy(r => r.left))
                        _matches.Add((page, left, bottom, right, top));

                if (_matches.Count == 0)
                {
                    _host.SetResultText("No matches");
                    return;
                }

                _totalHits = sr.TotalHits;

                // Start at the first match on or after the current page.
                int startPage = _host.CurrentPageIndex;
                _matchCursor = _matches.FindIndex(m => m.page >= startPage);
                if (_matchCursor < 0) _matchCursor = 0;

                GoToCurrentMatch();
            }
            catch
            {
                _host.SetResultText("Search error");
            }
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
        // highlights with the current match emphasised. Shared by Run and Next/Prev.
        private void GoToCurrentMatch()
        {
            if (_matchCursor < 0 || _matchCursor >= _matches.Count) return;
            int targetPage = _matches[_matchCursor].page;
            PageCursor = ResultPages.IndexOf(targetPage);   // keep the persisted page-cursor sane
            UpdateStatus();
            if (_host.CurrentPageIndex != targetPage)
                _host.GoToPage(targetPage);
            _host.RepaintHighlights();
        }

        // Compact count ("12 / 73" = current match / total matches); page breakdown in the tooltip.
        private void UpdateStatus()
        {
            if (_matches.Count == 0)
            {
                _host.SetResultCount("No matches", null);
                return;
            }
            int pages = ResultPages.Count;
            _host.SetResultCount($"{_matchCursor + 1} / {_matches.Count}",
                $"{_matches.Count} match{(_matches.Count != 1 ? "es" : "")} on {pages} page{(pages != 1 ? "s" : "")}");
        }
    }
}
