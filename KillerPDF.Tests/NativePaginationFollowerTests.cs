using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The window follows a long EPUB's pagination and shows the pages it adds. Showing them is display only: a view update
// that fails must not leave the book short of its pages, or a page jump past the frontier waits for them forever.
public sealed class NativePaginationFollowerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AFailedViewUpdateStillTakesEveryPageInAndShowsTheFinishedBook()
    {
        using var gate = new ManualResetEventSlim();
        using var book = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(),
            NativeProgressiveOpenTests.Gated(gate), inBackground: true);
        var shown = new List<(int Before, int After, bool Finished)>();
        bool failed = false;

        var following = NativePaginationFollower.FollowAsync(book, TimeSpan.FromMilliseconds(20), () => false,
            (before, after, finished) =>
            {
                shown.Add((before, after, finished));
                if (failed) return;
                failed = true;
                gate.Set();
                throw new InvalidOperationException("A view could not take in the new pages.");
            });
        await Record.ExceptionAsync(() => following.WaitAsync(Patience));
        await book.Pagination.WaitAsync(Patience);

        Assert.True(failed);
        Assert.Equal(book.PageCount, book.ShownPageCount);
        Assert.Equal((book.PageCount, true), (shown[^1].After, shown[^1].Finished));
    }
}
