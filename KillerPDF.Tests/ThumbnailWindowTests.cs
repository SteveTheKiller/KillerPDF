using System.Collections.Concurrent;
using System.IO;
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
        // The callbacks signal this monitor, so the test waits for each one to happen instead of polling a clock.
        // The wait is bounded only so that a callback which never comes fails the test instead of hanging it.
        var gate = new object();
        int drained = 0, shownCount = 0, drainedAtLastShown = 0;
        int lastShownPage = -1;
        using var window = new ThumbnailWindow<string>(100, (page, _) => $"thumb {page}",
            (page, image) =>
            {
                if (image is null) return;
                lock (gate)
                {
                    shownCount++;
                    lastShownPage = page;
                    drainedAtLastShown = drained;
                    Monitor.PulseAll(gate);
                }
            }, retain: 8,
            drained: () => { lock (gate) { drained++; Monitor.PulseAll(gate); } });

        // The drain that follows the last row drawn is the one that lets the renderer go, so it must come after the
        // last row was published, whichever pass of the background thread drew it.
        window.Request(3);
        window.Request(4);
        int afterFirst = WaitForDrainAfterRow(gate, () => shownCount == 2, () => (drained, drainedAtLastShown),
            "The renderer was not let go after the rows were drawn.");
        Assert.True(afterFirst > 0);

        window.Request(5);
        int afterSecond = WaitForDrainAfterRow(gate, () => shownCount == 3 && lastShownPage == 5,
            () => (drained, drainedAtLastShown), "The renderer was not let go after a later row was drawn.");
        Assert.True(afterSecond > afterFirst);
    }

    // Waits until the row condition holds and the drain count has gone past the count when the last row was published;
    // returns the drain count then. All state is read under the gate that the callbacks pulse.
    private static int WaitForDrainAfterRow(object gate, Func<bool> rowsShown, Func<(int Drained, int AtLastShown)> counts,
        string failure)
    {
        var hang = System.Diagnostics.Stopwatch.StartNew();
        lock (gate)
        {
            while (!(rowsShown() && counts() is var (drained, atLastShown) && drained > atLastShown))
            {
                TimeSpan left = TimeSpan.FromSeconds(60) - hang.Elapsed;
                Assert.True(left > TimeSpan.Zero && Monitor.Wait(gate, left), failure);
            }
            return counts().Drained;
        }
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

    // A row that shows a carried-over picture is cleared when its own draw fails, which makes it ask for its picture
    // again. A page that cannot be drawn is not drawn a second time because of that.
    [Fact]
    public void APageThatFailedToDrawIsNotDrawnAgainWhenItsRowAsksAfterTheFailure()
    {
        int attempts = 0, asked = 0;
        ThumbnailWindow<string>? window = null;
        window = new ThumbnailWindow<string>(100, (page, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("this page cannot be drawn");
        }, (page, image) =>
        {
            // The row clears its picture and reads Thumbnail again, which asks for the page.
            if (image is null && Interlocked.Increment(ref asked) == 1) window!.Request(page);
        }, retain: 8);
        using (window)
        {
            window.Request(5);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref asked) >= 1, TimeSpan.FromSeconds(10)));
            Thread.Sleep(300);

            Assert.Equal(1, Volatile.Read(ref attempts));
        }
    }

    // A draw that throws may have hit a passing problem, such as a briefly locked file. It is not drawn again inside
    // the pass that failed it, but a request after the pass has drained draws it.
    [Fact]
    public void APageWhoseDrawThrewIsDrawnWhenAskedForAfterThePassDrained()
    {
        int attempts = 0;
        using var drained = new SemaphoreSlim(0);
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(100, (page, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("briefly locked");
            return $"thumb {page}";
        }, (page, image) => { if (image is not null) shown[page] = image; }, retain: 8, drained: () => drained.Release());

        window.Request(3);
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)), "The pass did not drain.");
        window.Request(3);

        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(3), TimeSpan.FromSeconds(10)),
            "The page was never drawn when asked for after the failed pass.");
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    // A page the renderer refuses by returning nothing is a settled answer, so it is not drawn again.
    [Fact]
    public void APageTheRendererRefusedIsNotDrawnAgainAfterThePassDrained()
    {
        int attempts = 0;
        using var drained = new SemaphoreSlim(0);
        using var window = new ThumbnailWindow<string>(100, (page, _) => { Interlocked.Increment(ref attempts); return null; },
            (page, image) => { }, retain: 8, drained: () => drained.Release());

        window.Request(3);
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)));
        window.Request(3);
        Thread.Sleep(300);

        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    // A page that drew after a throw has proved it can be drawn, so its single retry is given back. Otherwise a page
    // that threw once, drew, was released past the retained number and then threw a second time (another passing
    // problem) would be settled as failed and stay blank for the life of the list.
    [Fact]
    public void APageThatDrewAfterAThrowGetsItsRetryBack()
    {
        int calls3 = 0;
        using var drained = new SemaphoreSlim(0);
        var shown = new ConcurrentDictionary<int, string>();
        using var window = new ThumbnailWindow<string>(100, (page, _) =>
        {
            if (page != 3) return $"thumb {page}";
            // Script for page 3: throw, draw, (evicted by page 4), throw, draw.
            int call = Interlocked.Increment(ref calls3);
            if (call is 1 or 3) throw new IOException("briefly locked");
            return $"thumb 3 call {call}";
        }, (page, image) => { if (image is null) shown.TryRemove(page, out _); else shown[page] = image; },
            retain: 1, drained: () => drained.Release());

        window.Request(3);                                   // throws (first)
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)));
        window.Request(3);                                   // draws
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("thumb 3 call 2", shown[3]);
        window.Request(4);                                   // evicts page 3
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(shown.ContainsKey(3));
        window.Request(3);                                   // throws a second, separate time
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)));
        window.Request(3);                                   // must be drawn, not settled as failed

        Assert.True(SpinWait.SpinUntil(() => shown.ContainsKey(3), TimeSpan.FromSeconds(10)),
            "The page was settled as failed although it had drawn since its first throw.");
        Assert.Equal(4, Volatile.Read(ref calls3));
    }

    // A page the renderer refuses by throwing is tried once more, then settled, so a broken page does not reopen and
    // parse the file on every later pass.
    [Fact]
    public void APageWhoseDrawKeepsThrowingIsDrawnAtMostTwice()
    {
        int attempts = 0;
        using var drained = new SemaphoreSlim(0);
        using var window = new ThumbnailWindow<string>(100, (page, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new NotSupportedException("refused");
        }, (page, image) => { }, retain: 8, drained: () => drained.Release());

        // Two passes draw it; a settled page starts no third pass, so the third request is only given time to act.
        for (int cycle = 0; cycle < 2; cycle++)
        {
            window.Request(3);
            Assert.True(drained.Wait(TimeSpan.FromSeconds(10)), "The pass did not drain.");
        }
        window.Request(3);
        Thread.Sleep(300);

        Assert.True(Volatile.Read(ref attempts) <= 2, $"The page was drawn {attempts} times.");
    }
}
