using KillerPDF.Controls;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    // How often the views take in the pages paginated since the last look, while a book is still paginating.
    private static readonly TimeSpan PaginationRefresh = TimeSpan.FromMilliseconds(500);

    // A long EPUB opens at its first chapter while the rest is paginated in the background. Every half second the
    // views showing it take in the new pages at their end: page slots, Grid tiles, the page list and the provisional
    // total ("/ 340+"). Pages already shown never change. When the last page is known the total settles and the page
    // list draws the thumbnails of the pages added meanwhile.
    private async void FollowNativePagination(NativeWorkingDocument book, string fileName)
    {
        try
        {
            await NativePaginationFollower.FollowAsync(book, PaginationRefresh,
                () => _windowResourcesReleased || Dispatcher.HasShutdownStarted,
                (before, after, finished) => ShowPaginatedPages(book, before, after, finished));
            if (_windowResourcesReleased || Dispatcher.HasShutdownStarted) return;
            // Closing the book cancels pagination; that needs no word. A failure keeps the pages already shown.
            if (book.Pagination.IsFaulted && !book.IsClosed && IsShowing(book))
            {
                // The dialog stays friendly; the cause goes to the app log for support.
                NativePaginationFollower.LogStopped(book, fileName, CrashReporter.Capture);
                KillerDialog.Show(this, Loc("Str_Native_PaginationStopped"),
                    Loc("Str_Dlg_AppTitle"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            else if (book.Pagination.IsCompletedSuccessfully && IsShowing(book)
                && _paginatingOpenStatus.Book == book && StatusText.Text == _paginatingOpenStatus.Text)
                SetStatus(OpenedStatus(fileName, book));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Following is display only; the book itself stays readable.
        }
        finally
        {
            // Nothing here may keep a closed book in memory.
            if (ReferenceEquals(_paginatingOpenStatus.Book, book)) _paginatingOpenStatus = default;
        }
    }

    private bool IsShowing(NativeWorkingDocument book) => ReferenceEquals(ActiveViewer.NativeDocumentRef, book);

    // The open message of a book still being paginated, so a finished pagination can correct its page count while the
    // status bar still shows it.
    private (NativeWorkingDocument? Book, string? Text) _paginatingOpenStatus;

    private string OpenedStatus(string fileName, NativeWorkingDocument book)
    {
        string status = string.Format(Loc("Str_Opened"), fileName,
            book.IsPaginating ? book.ShownPageCount + "+" : book.ShownPageCount.ToString());
        if (book.UnavailableChapterCount > 0)
            status += " " + string.Format(Loc("Str_Epub_ChaptersUnavailable"), book.UnavailableChapterCount);
        return status;
    }

    private void ShowPaginatedPages(NativeWorkingDocument book, int before, int after, bool finished)
    {
        foreach (var viewer in new[] { Viewer, ViewerB })
        {
            if (!ReferenceEquals(viewer.NativeDocumentRef, book)) continue;
            viewer.ShowPaginatedPagesExt(before);
            if (!ReferenceEquals(viewer, ActiveViewer)) continue;
            // The finished list is drawn whole once; until then the added pages are listed without thumbnails, so the
            // thumbnail reader does not keep stopping pagination to lay chapters out.
            if (finished) KeepReaderInput(RefreshPageList);
            else if (after > before) AppendNativePageList(book, before, after);
            _pageTotalLabel.Text = SidebarPageBinding.Total(after, book.IsPaginating); SizePageJumpBox();
            SyncPageNavigationButtons();
        }
    }

    private void AppendNativePageList(NativeWorkingDocument book, int before, int after)
    {
        if (PageList.ItemsSource is not PageThumbnailVm[] shown || shown.Length != before) { RefreshPageList(); return; }
        var revision = book.CaptureReadRevision();
        var proof = ActiveViewer.DocumentProofView?.CaptureProof();
        var items = new PageThumbnailVm[after];
        Array.Copy(shown, items, before);
        for (int index = before; index < after; index++) items[index] = new PageThumbnailVm(index, revision, 0, proof);
        KeepReaderInput(() =>
        {
            _sidebarPages.Show(items, provisional: book.IsPaginating);
            ApplyPageThumbnailLayout();
            ActiveViewer.SyncPageListSelection(ActiveViewer.CurrentPageIndex);
        });
        ActiveViewer.ThumbCache = items;
        ActiveViewer.ThumbCacheComplete = false;
    }

    // Re-seating the page list writes the current page into the page box and scrolls the list back to it. While a book
    // paginates that happens every half second, so it would wipe a page number being typed and pull the list away from
    // where the reader scrolled it; both are put back.
    private void KeepReaderInput(Action reseat)
    {
        bool typing = _pageJumpBox.IsKeyboardFocusWithin;
        string typed = _pageJumpBox.Text;
        int caret = _pageJumpBox.CaretIndex;
        var list = FindSidebarDescendant<System.Windows.Controls.ScrollViewer>(PageList);
        double offset = list?.VerticalOffset ?? 0;
        reseat();
        list?.ScrollToVerticalOffset(offset);
        if (!typing) return;
        _pageJumpBox.Text = typed;
        _pageJumpBox.CaretIndex = Math.Min(caret, typed.Length);
    }
}
