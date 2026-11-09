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
}
