namespace KillerPDF.Features
{
    /// <summary>
    /// What SearchController needs from the window hosting it, beyond the shared shell services.
    /// Values and intents only, never a control - the highlight painting itself stays in the
    /// shell (Shell/Search.cs), because it draws onto the page overlay canvases.
    /// </summary>
    internal interface ISearchHost : IShellServices
    {
        /// <summary>Path of the working (temp) file the search reads. Null when no document.</summary>
        string? CurrentFile { get; }
        long SearchRevision { get; }
        Guid? NativeSearchRevisionId => null;
        Services.CurrentSearchSnapshot CaptureSearchSnapshot();

        /// <summary>The page currently selected in the sidebar/page list.</summary>
        int CurrentPageIndex { get; }

        /// <summary>True when part of the page is in the document viewport now. A long book can have an
        /// overlay for every page, so a page with an overlay is not necessarily on screen.</summary>
        bool IsPageOnScreen(int pageIdx) => true;

        /// <summary>Navigates to the page (the match being stepped to lives there).</summary>
        void GoToPage(int pageIdx);

        /// <summary>Writes the result counter's text only ("", "No matches", "Search error"),
        /// leaving the tooltip alone - mirrors what the old code did on those paths.</summary>
        void SetResultText(string text);

        /// <summary>Writes the "12 / 73" counter and its page-breakdown tooltip.</summary>
        void SetResultCount(string text, string? tooltip);

        /// <summary>Removes every highlight rectangle from the page overlays.</summary>
        void ClearHighlights();

        /// <summary>Repaints highlights on every page on screen right now, with the current
        /// match emphasized.</summary>
        void RepaintHighlights();

        /// <summary>True when an edit already covers this match (unrotated page points), so the
        /// text under it is no longer what the reader sees, as after Find and Replace.</summary>
        bool IsCovered(int pageIdx, double left, double bottom, double right, double top);
    }
}
