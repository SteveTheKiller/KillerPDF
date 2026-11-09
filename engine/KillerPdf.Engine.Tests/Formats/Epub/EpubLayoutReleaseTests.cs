using System.Runtime.CompilerServices;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Closing a book ends its layout: layout ahead queued for it is cancelled and dropped, so it neither keeps the closed
// book in memory nor lays out another of its chapters.
[Collection(nameof(EpubLayoutAheadCollection))]
public sealed class EpubLayoutReleaseTests
{
    [Fact]
    public void AReleasedBookIsCollectedWhileItsLayoutAheadWaits()
    {
        using var hold = EpubLayoutAhead.Hold();
        var book = OpenReadAndRelease(out _);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(book.TryGetTarget(out _), "The layout-ahead queue kept a released book alive.");
    }

    [Fact]
    public void AReleasedBookLaysNoMoreChaptersOutAhead()
    {
        EpubChapterPages cache;
        using (EpubLayoutAhead.Hold())
        {
            var snapshot = Open();
            cache = snapshot.EpubLayout!.ChapterPages!;
            Assert.NotEmpty(snapshot.ReadTextPage(MiddleChapterPage(snapshot)).Regions);
            snapshot.ReleaseLayout();
            snapshot.ReleaseLayout();
        }

        // The same work the background thread would do, run here so the result does not depend on its timing.
        cache.LayOutAheadNow();

        Assert.Equal(0, cache.AheadLayouts);
    }

    [Fact]
    public void AReleasedBookKeepsItsTextButRefusesNewLayout()
    {
        var snapshot = Open();
        snapshot.EpubLayout!.ChapterPages!.PrefetchNeighbors = false;
        string text = snapshot.ReadPageText(MiddleChapterPage(snapshot));

        snapshot.ReleaseLayout();

        Assert.Equal(text, snapshot.ReadPageText(MiddleChapterPage(snapshot)));
        Assert.Throws<ObjectDisposedException>(() => snapshot.ReadTextPage(MiddleChapterPage(snapshot)));
        Assert.Throws<ObjectDisposedException>(() => snapshot.RenderPage(0));
    }

    // The chapter cache is what the queue holds, and through its layout function the whole book: its package, fonts
    // and caches. The snapshot itself can be collected either way, so the cache is the one watched.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<EpubChapterPages> OpenReadAndRelease(out int page)
    {
        var snapshot = Open();
        page = MiddleChapterPage(snapshot);
        // Reading a page lays its chapter out and queues its neighbors to be laid out ahead.
        Assert.NotEmpty(snapshot.ReadTextPage(page).Regions);
        snapshot.ReleaseLayout();
        return new WeakReference<EpubChapterPages>(snapshot.EpubLayout!.ChapterPages!);
    }

    private static PdfReadSnapshot Open()
    {
        var book = new Book();
        for (int chapter = 0; chapter < 5; chapter++)
            book.Chapters.Add(Chapter(string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 60))));
        return PdfReadSnapshot.OpenEpub(book.Build(), Options());
    }

    private static int MiddleChapterPage(PdfReadSnapshot snapshot)
        => Enumerable.Range(0, snapshot.PageCount).First(page => snapshot.EpubLayout!.Pages[page].SpineIndex == 2);
}

/// <summary>Tests that hold the shared layout-ahead thread run one at a time.</summary>
[CollectionDefinition(nameof(EpubLayoutAheadCollection), DisableParallelization = true)]
public sealed class EpubLayoutAheadCollection;
