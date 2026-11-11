using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// A long book opens with its first chapter while the rest is paginated in the background, in reading order. Pages
// before the frontier are final, and the finished book is the one a blocking open lays out. A chapter that names the
// font family "Later" makes the font resolver wait, so where the background work stands is never left to timing.
[Collection(nameof(EpubLayoutAheadCollection))]
public sealed class EpubProgressivePaginationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ABookOpensWithItsFirstChapterAndPaginatesTheRestInTheBackground()
    {
        byte[] book = Book(gatedChapter: 1);
        var expected = PdfReadSnapshot.OpenEpub(book, Options());
        int firstChapterPages = expected.EpubLayout!.Pages.Count(page => page.SpineIndex == 0);
        using var gate = new ManualResetEventSlim();

        var snapshot = PdfReadSnapshot.OpenEpubProgressive(book, Gated(gate));
        bool paginating = snapshot.IsPaginating;
        int frontier = snapshot.PageCount;
        gate.Set();
        await snapshot.Pagination.WaitAsync(Patience);

        Assert.True(paginating, "The open waited for the whole book.");
        Assert.Equal(firstChapterPages, frontier);
        Assert.False(snapshot.IsPaginating);
        AssertSamePages(expected, snapshot);
    }

    [Fact]
    public async Task AReaderDoesNotWaitForTheChapterBeingPaginated()
    {
        // Chapter 3 asks the resolver for 120 families, 25 ms each: about 3 s of pagination the reader must not wait for.
        const int slowFamilies = 120;
        var book = new Book();
        for (int chapter = 0; chapter < 5; chapter++)
            book.Chapters.Add(Chapter((chapter == 3
                    ? string.Concat(Enumerable.Range(0, slowFamilies).Select(family => $"<p style=\"font-family: Slow{family}\">AAA</p>"))
                    : "") + string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 60))));
        byte[] bytes = book.Build();
        var expected = PdfReadSnapshot.OpenEpub(bytes, Options());
        using var slow = new ManualResetEventSlim();
        var options = Options() with
        {
            FontFaceResolver = request =>
            {
                if (request.Family?.Name.StartsWith("Slow", StringComparison.Ordinal) == true)
                {
                    slow.Set();
                    Thread.Sleep(25);
                }
                return null;
            }
        };
        var snapshot = PdfReadSnapshot.OpenEpubProgressive(bytes, options);
        Assert.True(slow.Wait(Patience), "Pagination never reached chapter 3.");
        // Chapter 2 is neither the one opened to nor the next one, so reading its page lays it out again.
        int page = Enumerable.Range(0, snapshot.PageCount).First(index => snapshot.EpubLayout!.Pages[index].SpineIndex == 2);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotEmpty(snapshot.ReadTextPage(page).Regions);
        long readMs = watch.ElapsedMilliseconds;
        bool stillPaginating = snapshot.IsPaginating;
        await snapshot.Pagination.WaitAsync(Patience);

        Assert.True(stillPaginating, "Pagination finished before the read, so the read proves nothing.");
        Assert.True(readMs < 1500, $"The read waited {readMs} ms for the chapter being paginated.");
        // Chapter 3 was stopped and paginated again from its start: the book is still the one a blocking open lays out.
        AssertSamePages(expected, snapshot);
    }

    [Fact]
    public async Task PaginationFinishesWhileAReaderKeepsLayingOutOtherChapters()
    {
        // Chapter 6 asks for 60 families, 25 ms each. A reader who keeps turning through chapters 0 to 5, more than the
        // chapter cache holds, lays one out again on almost every read and so keeps stopping pagination.
        var book = new Book();
        for (int chapter = 0; chapter < 8; chapter++)
            book.Chapters.Add(Chapter((chapter == 6
                    ? string.Concat(Enumerable.Range(0, 60).Select(family => $"<p style=\"font-family: Slow{family}\">AAA</p>"))
                    : "") + string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 20))));
        byte[] bytes = book.Build();
        var expected = PdfReadSnapshot.OpenEpub(bytes, Options());
        using var slow = new ManualResetEventSlim();
        var options = Options() with
        {
            FontFaceResolver = request =>
            {
                if (request.Family?.Name.StartsWith("Slow", StringComparison.Ordinal) == true)
                {
                    slow.Set();
                    Thread.Sleep(25);
                }
                return null;
            }
        };
        var snapshot = PdfReadSnapshot.OpenEpubProgressive(bytes, options);
        Assert.True(slow.Wait(Patience), "Pagination never reached chapter 6.");
        int[] firstPages = Enumerable.Range(0, 6)
            .Select(chapter => Enumerable.Range(0, snapshot.PageCount).First(page => snapshot.EpubLayout!.Pages[page].SpineIndex == chapter))
            .ToArray();

        var reading = System.Diagnostics.Stopwatch.StartNew();
        for (int read = 0; snapshot.IsPaginating && reading.Elapsed < Patience; read++)
            Assert.NotEmpty(snapshot.ReadTextPage(firstPages[read % firstPages.Length]).Regions);

        Assert.False(snapshot.IsPaginating, "Pagination never finished while the reader kept reading.");
        await snapshot.Pagination;
        AssertSamePages(expected, snapshot);
    }

    [Fact]
    public async Task ALinkIntoAChapterNotYetPaginatedWaitsForItsPageNumbers()
    {
        var book = new Book();
        for (int chapter = 0; chapter < 4; chapter++)
            book.Chapters.Add(Chapter((chapter == 2 ? "<p style=\"font-family: Later\">AAA</p>" : "")
                + string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 60))
                + (chapter == 2 ? "<p id=\"late\">AAA</p>" : "")));
        byte[] bytes = book.Build();
        var expected = PdfReadSnapshot.OpenEpub(bytes, Options()).ResolveNavigationTarget("OEBPS/text/c2.xhtml#late");
        using var gate = new ManualResetEventSlim();
        var snapshot = PdfReadSnapshot.OpenEpubProgressive(bytes, Gated(gate));

        var waiting = snapshot.WaitForNavigationTargetAsync("OEBPS/text/c2.xhtml#late");
        await Task.Delay(100);
        bool waited = !waiting.IsCompleted;
        // Not "not found": its chapter simply has no page numbers yet.
        Assert.Throws<InvalidOperationException>(() => snapshot.ResolveNavigationTarget("OEBPS/text/c2.xhtml#late"));
        gate.Set();
        await waiting.WaitAsync(Patience);

        Assert.True(waited, "The wait returned before the chapter was paginated.");
        Assert.Equal(expected, snapshot.ResolveNavigationTarget("OEBPS/text/c2.xhtml#late"));
        await snapshot.Pagination.WaitAsync(Patience);
    }

    [Fact]
    public async Task APageWaitEndsWhenPaginationReachesThePageOrTheEndOfTheBook()
    {
        byte[] book = Book(gatedChapter: 1);
        int total = PdfReadSnapshot.OpenEpub(book, Options()).PageCount;
        using var gate = new ManualResetEventSlim();
        var snapshot = PdfReadSnapshot.OpenEpubProgressive(book, Gated(gate));

        var last = snapshot.WaitForPageAsync(total - 1);
        var past = snapshot.WaitForPageAsync(total);
        await Task.Delay(100);
        bool waited = !last.IsCompleted && !past.IsCompleted;
        gate.Set();

        Assert.True(waited, "A page past the frontier was reported before pagination reached it.");
        Assert.True(await last.WaitAsync(Patience));
        Assert.False(await past.WaitAsync(Patience));
        Assert.True(await snapshot.WaitForPageAsync(0));
    }

    [Fact]
    public async Task ReleasingABookStopsItsPaginationAndLetsItBeCollected()
    {
        using var gate = new ManualResetEventSlim();
        int chaptersReported = 0;
        var (cache, pagination) = OpenAndRelease(gate, () => Interlocked.Increment(ref chaptersReported));
        gate.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pagination.WaitAsync(Patience));
        // Only the chapter the book opened with was paginated.
        Assert.Equal(1, Volatile.Read(ref chaptersReported));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return !cache.TryGetTarget(out _);
        }, Patience), "The released book stayed in memory after its pagination stopped.");
    }

    [Fact]
    public async Task AFailureWithIsolationDisabledEndsPaginationWithThatErrorAndKeepsThePagesBeforeIt()
    {
        byte[] book = Book(gatedChapter: 2);
        int before = PdfReadSnapshot.OpenEpub(book, Options()).EpubLayout!.Pages.Count(page => page.SpineIndex < 2);
        var options = Options() with
        {
            IsolateChapterFailures = false,
            FontFaceResolver = request => request.Family?.Name == "Later"
                ? throw new InvalidOperationException("The resolver failed.") : null
        };
        var snapshot = PdfReadSnapshot.OpenEpubProgressive(book, options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.Pagination.WaitAsync(Patience));

        Assert.Equal("The resolver failed.", error.Message);
        Assert.False(snapshot.IsPaginating);
        Assert.Equal(before, snapshot.PageCount);
    }

    // The chapter cache is what pagination's thread holds, and through it the whole book; nothing else keeps it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference<EpubChapterPages> Cache, Task Pagination) OpenAndRelease(ManualResetEventSlim gate,
        Action chapterReported)
    {
        var snapshot = PdfReadSnapshot.OpenEpubProgressive(Book(gatedChapter: 1),
            Gated(gate) with { ChapterProgress = (_, _) => chapterReported() });
        snapshot.ReleaseLayout();
        return (new WeakReference<EpubChapterPages>(snapshot.EpubLayout!.ChapterPages!), snapshot.Pagination);
    }

    internal static byte[] Book(int gatedChapter, int chapters = 4)
    {
        var book = new Book();
        for (int chapter = 0; chapter < chapters; chapter++)
            book.Chapters.Add(Chapter((chapter == gatedChapter ? "<p style=\"font-family: Later\">AAA</p>" : "")
                + string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 60))));
        return book.Build();
    }

    // The fallback face everywhere, as Options() lays out; only the "Later" family waits for the gate first.
    internal static EpubLayoutOptions Gated(ManualResetEventSlim gate) => Options() with
    {
        FontFaceResolver = request =>
        {
            if (request.Family?.Name == "Later") Assert.True(gate.Wait(Patience), "The test never opened the gate.");
            return null;
        }
    };

    internal static void AssertSamePages(PdfReadSnapshot expected, PdfReadSnapshot actual)
    {
        Assert.Equal(expected.PageCount, actual.PageCount);
        var left = expected.EpubLayout!.Pages;
        var right = actual.EpubLayout!.Pages;
        for (int index = 0; index < expected.PageCount; index++)
        {
            Assert.Equal(left[index].SpineIndex, right[index].SpineIndex);
            Assert.Equal(left[index].TextStart, right[index].TextStart);
            Assert.Equal(left[index].Text, right[index].Text);
            Assert.Equal(left[index].TextPlacements, right[index].TextPlacements);
        }
    }
}
