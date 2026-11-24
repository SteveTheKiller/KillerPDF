using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Background pagination gives way to a reader who needs a chapter laid out. A reader is often the UI thread looking up
// links or selectable text under the pointer, so it must not wait for pagination to finish a chapter, also not when
// pagination is running that chapter again after an earlier reader stopped it.
public sealed class EpubPaginationPreemptionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AReaderStopsPaginationAlsoWhenItRunsAChapterAgainAfterAnEarlierReader()
    {
        var cache = new EpubChapterPages((chapter, from, stop, token) =>
            new EpubChapterRun([Page(chapter)], 1), [0, 1, 2]) { PrefetchNeighbors = false };
        var entries = new[] { 0, 1, 2 }.Select(chapter =>
        {
            cache.Register(chapter, 1);
            return Page(chapter).Indexed(cache, 0);
        }).ToArray();
        using var firstRun = new ManualResetEventSlim();
        using var secondRun = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int runs = 0;
        var pagination = OwnThread(() => cache.Paginate(token =>
        {
            int run = Interlocked.Increment(ref runs);
            (run == 1 ? firstRun : secondRun).Set();
            release.Wait(token);
            return run;
        }));
        Assert.True(firstRun.Wait(Patience), "Pagination never started.");

        // The first reader stops the first run; pagination then runs the chapter again.
        Assert.NotEmpty((await OwnThread(() => entries[0].TextPlacements).WaitAsync(Patience)));
        Assert.True(secondRun.Wait(Patience), "Pagination did not run the chapter again.");
        var reader = OwnThread(() => entries[1].TextPlacements);
        // Pagination stays held until the reader is done, so the reader can only return by stopping the run: there is
        // no race against a clock. Patience only ends the wait if the reader is stuck behind the run for good.
        bool readerReturned;
        try
        {
            await reader.WaitAsync(Patience);
            readerReturned = true;
        }
        catch (TimeoutException) { readerReturned = false; }
        finally { release.Set(); }
        await reader.WaitAsync(Patience);
        await pagination.WaitAsync(Patience);

        Assert.True(readerReturned, "The reader waited for pagination to finish the chapter it was running again.");
    }

    // Pagination runs on its own thread in the app, and the reader is usually the UI thread. Pool threads can be
    // taken for longer than Patience while the rest of the suite renders in parallel.
    private static Task<T> OwnThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static EpubLayoutPage Page(int chapter) =>
        new($"{chapter}", 0, chapter, [new EpubTextPlacement(chapter, 0, 1, 0, 0, 10, 10)], Array.Empty<NativePagePaint>());
}
