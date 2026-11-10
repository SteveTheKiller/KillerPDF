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
        var pagination = Task.Run(() => cache.Paginate(token =>
        {
            int run = Interlocked.Increment(ref runs);
            (run == 1 ? firstRun : secondRun).Set();
            release.Wait(token);
            return run;
        }));
        Assert.True(firstRun.Wait(Patience), "Pagination never started.");

        // The first reader stops the first run; pagination then runs the chapter again.
        Assert.NotEmpty((await Task.Run(() => entries[0].TextPlacements).WaitAsync(Patience)));
        Assert.True(secondRun.Wait(Patience), "Pagination did not run the chapter again.");
        var reader = Task.Run(() => entries[1].TextPlacements);
        bool readerReturned = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(1))) == reader;
        release.Set();
        await reader.WaitAsync(Patience);
        await pagination.WaitAsync(Patience);

        Assert.True(readerReturned, "The reader waited for pagination to finish the chapter it was running again.");
    }

    private static EpubLayoutPage Page(int chapter) =>
        new($"{chapter}", 0, chapter, [new EpubTextPlacement(chapter, 0, 1, 0, 0, 10, 10)], Array.Empty<NativePagePaint>());
}
