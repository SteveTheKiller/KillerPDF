using System.Collections.Concurrent;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// The shared layout-ahead thread lays out the chapter being read and its neighbors; a page turn redirects it, a reader
// who needs another chapter stops it, and a reader who needs the chapter it is laying out waits for it instead of
// laying that chapter out a second time. The chapters here are laid out by a stand-in that records each request.
[Collection(nameof(EpubLayoutAheadCollection))]
public sealed class EpubLayoutAheadTests
{
    private const int Chapters = 5, PagesPerChapter = 4;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public void TheThreadLaysOutTheChapterBeingReadAndBothNeighbors()
    {
        var book = new FakeBook();

        _ = book.Page(2, 0).TextPlacements;

        Assert.True(SpinWait.SpinUntil(() => book.Cache.ResidentPageCount(1) == PagesPerChapter
            && book.Cache.ResidentPageCount(2) == PagesPerChapter && book.Cache.ResidentPageCount(3) == PagesPerChapter, Patience),
            "The layout-ahead thread did not lay out chapter 2 and its neighbors.");
        Assert.Equal(0, book.Cache.ResidentPageCount(0));
        Assert.Equal(0, book.Cache.ResidentPageCount(4));
    }

    [Fact]
    public void APageTurnReplacesTheChaptersWaitingToBeLaidOutAhead()
    {
        var book = new FakeBook();
        using (EpubLayoutAhead.Hold())
        {
            _ = book.Page(1, 0).TextPlacements;
            _ = book.Page(4, 0).TextPlacements;
            book.Cache.LayOutAheadNow();
        }

        // Chapter 4 is finished and chapter 3 (its only neighbor) laid out; chapter 1's neighbors are no longer wanted.
        Assert.Equal(new[] { 4, 3 }, book.AheadRequests());
    }

    [Fact]
    public async Task AReaderWhoNeedsAnotherChapterStopsTheLayoutAhead()
    {
        var started = new ManualResetEventSlim();
        bool cancelled = false;
        var book = new FakeBook((chapter, ahead, token) =>
        {
            // The first layout ahead of chapter 3 runs until something cancels it.
            if (!ahead || chapter != 3 || started.IsSet) return;
            started.Set();
            cancelled = token.WaitHandle.WaitOne(Patience);
            token.ThrowIfCancellationRequested();
        });
        _ = book.Page(2, 0).TextPlacements;
        Assert.True(started.Wait(Patience), "The layout-ahead thread never reached chapter 3.");

        var reader = Task.Run(() => book.Page(0, 2).TextPlacements);

        var placements = await reader.WaitAsync(Patience);
        Assert.True(cancelled, "The layout ahead of chapter 3 was not cancelled for the reader.");
        Assert.Single(placements);
    }

    [Fact]
    public async Task AReaderWaitsForTheChapterBeingLaidOutAheadInsteadOfLayingItOutAgain()
    {
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var book = new FakeBook((chapter, ahead, token) =>
        {
            if (!ahead || chapter != 3) return;
            started.Set();
            Assert.True(release.Wait(Patience));
        });
        _ = book.Page(2, 0).TextPlacements;
        Assert.True(started.Wait(Patience), "The layout-ahead thread never reached chapter 3.");

        var reader = Task.Run(() => book.Page(3, PagesPerChapter - 1).TextPlacements);
        // The reader is now waiting for chapter 3; it must neither cancel that work nor start its own.
        await Task.Delay(300);
        Assert.False(reader.IsCompleted);
        release.Set();

        Assert.Single(await reader.WaitAsync(Patience));
        Assert.Equal(1, book.Requests.Count(request => request.Chapter == 3));
    }

    // Five chapters of four pages each; every page holds one placement whose text names its chapter and page.
    private sealed class FakeBook
    {
        private readonly EpubLayoutPage[][] _entries = new EpubLayoutPage[Chapters][];
        internal ConcurrentQueue<(int Chapter, bool Ahead)> Requests { get; } = new();
        internal EpubChapterPages Cache { get; }

        internal FakeBook(Action<int, bool, CancellationToken>? during = null)
        {
            Cache = new EpubChapterPages((chapter, from, stop, token) =>
            {
                bool ahead = token.CanBeCanceled;
                Requests.Enqueue((chapter, ahead));
                during?.Invoke(chapter, ahead, token);
                var pages = Pages(chapter).Take(Math.Min(stop, PagesPerChapter))
                    .Select((page, ordinal) => ordinal < from ? null : page).ToList();
                return new EpubChapterRun(pages, pages.Count - from);
            }, Enumerable.Range(0, Chapters).ToArray());
            for (int chapter = 0; chapter < Chapters; chapter++)
            {
                Cache.Register(chapter, PagesPerChapter);
                _entries[chapter] = Pages(chapter).Select((page, ordinal) => page.Indexed(Cache, ordinal)).ToArray();
            }
        }

        internal EpubLayoutPage Page(int chapter, int ordinal) => _entries[chapter][ordinal];

        internal int[] AheadRequests() => Requests.Where(request => request.Ahead).Select(request => request.Chapter).ToArray();

        private static List<EpubLayoutPage> Pages(int chapter) => Enumerable.Range(0, PagesPerChapter).Select(ordinal =>
            new EpubLayoutPage($"{chapter}:{ordinal}", ordinal * 3, chapter,
                [new EpubTextPlacement(chapter, ordinal * 3, 3, 0, 0, 10, 10)], Array.Empty<NativePagePaint>())).ToList();
    }
}
