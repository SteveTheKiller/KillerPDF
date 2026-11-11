using System.Collections.Concurrent;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The page list drew a thumbnail for every page of a document as it opened and kept them all: about 805 MiB for a
// 10,000-page PDF and 8 GiB for 100,000 pages, while the list shows a dozen rows. Thumbnails now load for the rows
// the list asks for and the oldest go once more than the retained number are held.
public sealed class ThumbnailWindowTests
{
    [Fact]
    public void OnlyTheRowsTheListAsksForAreDrawn()
    {
        var drawn = new ConcurrentBag<int>();
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(10_000, (page, _) => { drawn.Add(page); return $"thumb {page}"; },
            (page, image) => { if (image is null) shown.TryRemove(page, out _); else shown[page] = image; }, retain: 64);

        for (int page = 500; page < 510; page++) window.Request(page);

        Assert.True(SpinWait.SpinUntil(() => Enumerable.Range(500, 10).All(shown.ContainsKey), TimeSpan.FromSeconds(10)),
            "The requested rows were not drawn.");
        Thread.Sleep(200);
        Assert.Equal(Enumerable.Range(500, 10), drawn.Order());
        Assert.Equal("thumb 505", shown[505]);
    }

    [Fact]
    public void TheOldestThumbnailsGoOnceMoreThanTheRetainedNumberAreHeld()
    {
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(1_000, (page, _) => $"thumb {page}",
            (page, image) => { if (image is null) shown.TryRemove(page, out _); else shown[page] = image; }, retain: 4);

        foreach (int page in new[] { 1, 2, 3, 4, 5, 6 })
        {
            window.Request(page);
            Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(page), TimeSpan.FromSeconds(10)), $"Page {page} was not drawn.");
        }
        Thread.Sleep(200);

        // Pages 1 and 2 were drawn first, so they are the ones released.
        Assert.Equal([3, 4, 5, 6], shown.Keys.Order());
    }

    [Fact]
    public void ARowAskedForAgainAfterItWasReleasedIsDrawnAgain()
    {
        var drawn = new ConcurrentBag<int>();
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(100, (page, _) => { drawn.Add(page); return $"thumb {page}"; },
            (page, image) => { if (image is null) shown.TryRemove(page, out _); else shown[page] = image; }, retain: 1);

        window.Request(7);
        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(7), TimeSpan.FromSeconds(10)));
        window.Request(8);
        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(8) && !shown.ContainsKey(7), TimeSpan.FromSeconds(10)));
        window.Request(7);
        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(7), TimeSpan.FromSeconds(10)));

        Assert.Equal(2, drawn.Count(page => page == 7));
    }

    // The renderer behind the thumbnails holds decoded page images, so it is let go whenever no row is waiting and
    // opened again for the next request; a scanned PDF otherwise kept it for as long as the tab was open.
    [Fact]
    public void TheRendererIsLetGoWhenNoRowIsWaiting()
    {
        int drained = 0;
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(100, (page, _) => $"thumb {page}",
            (page, image) => { if (image is not null) shown[page] = image; }, retain: 8,
            drained: () => Interlocked.Increment(ref drained));

        window.Request(3);
        window.Request(4);
        Assert.True(SpinWait.SpinUntil(() => shown.Count == 2 && Volatile.Read(ref drained) >= 1, TimeSpan.FromSeconds(10)),
            "The renderer was not let go after the rows were drawn.");
        int afterFirst = Volatile.Read(ref drained);
        window.Request(5);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref drained) > afterFirst, TimeSpan.FromSeconds(10)),
            "The renderer was not let go after a later row was drawn.");
    }

    // A fast scroll realizes far more rows than the window retains. Drawing every one of them would release most
    // as soon as it was held, so only the newest retained number of waiting requests are kept.
    [Fact]
    public void AFastScrollDrawsNoMoreRowsThanTheWindowRetains()
    {
        int draws = 0;
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(1_000, (page, _) =>
        {
            Interlocked.Increment(ref draws);
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(10));
            return $"thumb {page}";
        }, (page, image) => { if (image is null) shown.TryRemove(page, out _); else shown[page] = image; }, retain: 4);

        window.Request(0);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "The first row did not start drawing.");
        for (int page = 1; page < 100; page++) window.Request(page);
        gate.Set();

        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(99) && shown.ContainsKey(96), TimeSpan.FromSeconds(10)),
            "The newest rows were not drawn.");
        Thread.Sleep(300);
        // The row in flight plus the newest four waiting rows.
        Assert.Equal(5, Volatile.Read(ref draws));
    }

    // A row dropped from the queue is not left marked as waiting, so asking for it again draws it.
    [Fact]
    public void ARowDroppedFromTheQueueCanBeAskedForAgain()
    {
        var drawn = new ConcurrentBag<int>();
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(1_000, (page, _) =>
        {
            drawn.Add(page);
            if (page == 0) { entered.Set(); gate.Wait(TimeSpan.FromSeconds(10)); }
            return $"thumb {page}";
        }, (page, image) => { if (image is null) shown.TryRemove(page, out _); else shown[page] = image; }, retain: 2);

        window.Request(0);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        for (int page = 1; page <= 5; page++) window.Request(page);   // 1, 2 and 3 are dropped
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(5) && shown.ContainsKey(4), TimeSpan.FromSeconds(10)));
        Thread.Sleep(200);
        window.Request(1);
        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(1), TimeSpan.FromSeconds(10)), "The dropped row was never drawn when asked for again.");
    }

    // An out-of-memory error while drawing one row must not leave the window believing a pass is still running or the
    // row still waiting, or that row and every later request would never be drawn.
    [Fact]
    public void ARowThatRanOutOfMemoryCanBeDrawnWhenAskedForAgain()
    {
        int attempts = 0;
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(100, (page, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new OutOfMemoryException();
            return $"thumb {page}";
        }, (page, image) => { if (image is null) shown.TryRemove(page, out _); else shown[page] = image; }, retain: 8);

        window.Request(3);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref attempts) >= 1, TimeSpan.FromSeconds(10)));
        Thread.Sleep(300);
        window.Request(3);
        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(3), TimeSpan.FromSeconds(10)), "The row was stuck after the failure.");
    }

    // A row that keeps a carried-over picture from the list it replaced must be told when its own draw fails, or it
    // shows the stale picture for good.
    [Fact]
    public void AFailedDrawIsPublishedSoTheRowDropsItsStalePicture()
    {
        var failed = new ConcurrentBag<int>();
        using var window = new ThumbnailWindow<string>(100, (page, _) => null,
            (page, image) => { if (image is null) failed.Add(page); }, retain: 8);

        window.Request(5);

        Assert.True(SpinWait.SpinUntil(() => failed.Contains(5), TimeSpan.FromSeconds(10)), "The failed draw was not published.");
    }
}
