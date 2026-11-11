using KillerPDF.Services;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Controls;

// A long EPUB opens at its first chapter while the rest of the book is paginated in the background. The view grows at
// its end as the window takes in new pages; pages already shown never change. A jump or link past the pages known so
// far waits for pagination to reach its page, showing the chapter count as it goes, since a page's number depends on
// every chapter before it and so cannot be known any sooner.
public partial class PdfViewer
{
    private long _paginationWait;

    /// <summary>The page total as text: "340+" while the book is still being paginated.</summary>
    private string ReadPageTotal => _nativeDoc is { IsPaginating: true } ? ReadPageCount + "+" : ReadPageCount.ToString();

    /// <summary>Lays out the pages the window took in after <paramref name="before"/>, at the end of the view.</summary>
    internal void ShowPaginatedPagesExt(int before)
    {
        if (_nativeDoc is null) return;
        // A chapter paginated later can leave something out, which raises the diagnostics notice then.
        UpdateNativeNotice();
        // The page status names the total too ("Page 1 of 85+"), so it settles with the last page.
        if (!_nativeDoc.IsPaginating && Host?.IsViewerFocused(this) != false) PresentCurrentPageStatus();
        if (ReadPageCount <= before) return;
        if (_viewMode.IsContinuous())
        {
            // Before the continuous panel is built there is nothing to extend; SetupContinuousView lays out every page.
            if (_continuousPanel.Children.Count != before) return;
            for (int page = before; page < ReadPageCount; page++) AddContinuousSlot(page);
            LayoutContinuousSlots();
            // The pages around the one being read, which may include new ones when it is near the end. A pass still
            // drawing is left to finish: starting a new one cancels it, and on a slow machine a refresh every half second
            // kept the first page from ever being drawn. The scroll-settle pass picks up any new slot it misses.
            if (_continuousRenderTask is not { IsCompleted: false }) _ = RenderContinuousPages(_currentPage);
        }
        else if (_viewMode == ViewMode.Grid)
        {
            // A book that opened with fewer than three pages opened Grid that many across; it widens to the usual
            // three as pages arrive, unless the columns were changed since.
            if (before < 3 && _gridColumns == Math.Max(1, before) && ReadPageCount > before)
            {
                _gridColumns = Math.Min(ReadPageCount, 3);
                SetZoom(GridZoomForN(_gridColumns));
            }
            FillGridAroundCurrentPage();
        }
        // Two-Page shows the new partner of a last page that had none; Single has nothing more to show.
        else if (_viewMode == ViewMode.TwoPage && _currentPage >= before - 2) RenderPage(_currentPage);
    }

    /// <summary>
    /// Goes to a page past the ones shown once pagination reaches it (or to the last page, when the book is shorter
    /// or <paramref name="last"/> asks for the end). Returns false when the page is already shown or the book is not
    /// paginating, so the caller goes there itself. A newer wait replaces an older one.
    /// </summary>
    internal bool GoToPageWhenPaginatedExt(int pageIndex, Action<int> go, bool last = false)
    {
        if (_nativeDoc is not { IsPaginating: true } book || !last && pageIndex < ReadPageCount) return false;
        long request = ++_paginationWait;
        var session = _active;
        int startPage = _currentPage;
        _ = Wait();
        return true;

        async Task Wait()
        {
            // A reader who moves to another page meanwhile has changed their mind: the wait is over.
            while (request == _paginationWait && ReferenceEquals(_nativeDoc, book) && ReferenceEquals(_active, session)
                && _currentPage == startPage)
            {
                // Every page is shown once pagination ended and the window took in the last of them.
                bool complete = !book.IsPaginating && book.ShownPageCount == book.PageCount;
                if (!last && pageIndex < ReadPageCount || complete)
                {
                    go(Math.Min(last ? int.MaxValue : pageIndex, ReadPageCount - 1));
                    return;
                }
                ShowPaginationProgress(book);
                await Task.Delay(250);
            }
        }
    }

    /// <summary>Ends a page wait still pending, for a reader who goes elsewhere by a means that leaves the current
    /// page as it is, such as Home on the first page.</summary>
    internal void CancelPaginationWaitExt() => _paginationWait++;

    /// <summary>
    /// Follows a native navigation target. In a book still being paginated, a target whose chapter has no page numbers
    /// yet waits for them, and for the window to show its page, before <paramref name="go"/> runs.
    /// </summary>
    internal async void GoToNativeTargetExt(NativeWorkingDocument owner, string target, int? currentPage,
        Action<PdfReadNavigationDestination> go)
    {
        long request = ++_paginationWait;
        var session = _active;
        bool Current() => request == _paginationWait && ReferenceEquals(_nativeDoc, owner) && ReferenceEquals(_active, session);
        try
        {
            if (owner.IsPaginating)
            {
                var chapter = owner.WaitForNavigationTargetAsync(target);
                while (!chapter.IsCompleted)
                {
                    if (!Current()) return;
                    ShowPaginationProgress(owner);
                    await Task.WhenAny(chapter, Task.Delay(250));
                }
                await chapter;
            }
            if (!Current()) return;
            var destination = owner.ResolveNavigationTarget(target, currentPage);
            while (destination.PageIndex >= ReadPageCount)
            {
                ShowPaginationProgress(owner);
                await Task.Delay(100);
                if (!Current()) return;
            }
            go(destination);
        }
        catch (Exception error) when (error is System.IO.InvalidDataException or NotSupportedException
            or ArgumentException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            if (Current()) SetStatus(string.Format(Loc("Str_Pages_Failed"), error.Message));
        }
    }

    private void ShowPaginationProgress(NativeWorkingDocument book)
    {
        var (paginated, total) = book.PaginationProgress;
        SetStatus(string.Format(Loc("Str_Native_OpeningChapter"),
            System.IO.Path.GetFileName(_currentFile ?? ""), paginated, total));
    }
}
