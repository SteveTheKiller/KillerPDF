namespace KillerPDF.Services;

/// <summary>Takes a book's newly paginated pages into the window while the rest of it is still paginated.</summary>
internal static class NativePaginationFollower
{
    /// <summary>Every <paramref name="refresh"/> the book takes in the pages paginated since the last look and
    /// <paramref name="showViews"/> shows them (shown count before, after, and whether pagination has finished). The last
    /// call comes once every page is known. Stops early when <paramref name="stopped"/> says the window is going away.</summary>
    /// <remarks>Showing is display only. A view update that fails is skipped, and the book still takes in every page,
    /// so a page jump or link waiting for a page past the frontier gets it.</remarks>
    internal static async Task FollowAsync(NativeWorkingDocument book, TimeSpan refresh, Func<bool> stopped,
        Action<int, int, bool> showViews)
    {
        while (book.IsPaginating)
        {
            await Task.WhenAny(book.Pagination, Task.Delay(refresh));
            if (stopped()) return;
            Show(book, showViews, finished: false);
        }
        if (stopped()) return;
        Show(book, showViews, finished: true);
    }

    private static void Show(NativeWorkingDocument book, Action<int, int, bool> showViews, bool finished)
    {
        var (before, after) = book.ShowPaginatedPages();
        try { showViews(before, after, finished); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The views keep the pages they already show; the next look shows what they can.
        }
    }
}
