using Xunit;

namespace KillerPDF.Tests;

// End on a book that is still being paginated waits for its last page. A reader who then goes somewhere
// else, by Home, by paging or by a thumbnail, must not be pulled to the last page when pagination finishes.
public sealed class BookEndWaitTests
{
    [Fact]
    public void AWaitEndsWhenTheReaderChangesPageByAnyOtherMeans()
    {
        string viewer = ShellSource.Read("Controls", "Viewer", "PdfViewer.NativePagination.cs");
        string wait = ShellSource.Between(viewer, "internal bool GoToPageWhenPaginatedExt(", "internal async void GoToNativeTargetExt(");

        // The page the reader was on when the wait began is remembered, and the wait gives up when it changes.
        Assert.Contains("int startPage = _currentPage;", wait, StringComparison.Ordinal);
        Assert.Contains("_currentPage == startPage", wait, StringComparison.Ordinal);
        // Home leaves a reader on page 0 where they already are, so it ends the wait by name.
        Assert.Contains("internal void CancelPaginationWaitExt()", viewer, StringComparison.Ordinal);
        string keys = ShellSource.Read("Shell", "KeyboardShortcuts.cs");
        string home = ShellSource.Between(keys, "key == Key.Home && mods == ModifierKeys.None", "key == Key.End && mods == ModifierKeys.None");
        Assert.Contains("CancelPaginationWaitExt()", home, StringComparison.Ordinal);
    }
}