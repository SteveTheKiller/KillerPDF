using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeSearchServiceTests
{
    [Fact]
    public async Task SearchControllerEmphasizesEachAreaOfTheCurrentPhraseAndMovesEmphasis()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A A A A",
            Zone(10, 20, 10, 10, 0, 1), Zone(30, 20, 10, 10, 2, 1),
            Zone(10, 80, 10, 10, 4, 1), Zone(30, 80, 10, 10, 6, 1))));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = revision });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A A");
        Assert.Equal("1 / 2", await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, controller.CurrentOccurrence!.Start);
        var firstAreas = controller.CurrentOccurrence.Areas.ToArray();
        Assert.Equal(2, firstAreas.Length);
        Assert.All(firstAreas, area => Assert.True(controller.IsCurrentArea(0, area)));
        Assert.False(controller.IsCurrentArea(1, firstAreas[0]));
        var union = controller.CurrentMatch!.Value;
        Assert.False(controller.IsCurrentArea(0, new(union.left, union.bottom, union.right, union.top)));
        controller.Next();
        Assert.Equal(4, controller.CurrentOccurrence!.Start);
        Assert.All(firstAreas, area => Assert.False(controller.IsCurrentArea(0, area)));
        Assert.All(controller.CurrentOccurrence.Areas, area => Assert.True(controller.IsCurrentArea(0, area)));
    }

    [Fact]
    public async Task SearchControllerDoesNotRestoreNativeMatchesAfterRevisionChanges()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = revision });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A");
        await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var parked = controller.Capture();
        host.SearchRevision++;
        controller.Restore(parked);
        Assert.Null(controller.CurrentOccurrence);
        Assert.False(controller.HasResults);
    }

    [Fact]
    public async Task SearchControllerRestoresEachNativeTabsResultIdentity()
    {
        using var first = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        using var second = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A A")));
        var firstHost = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = first });
        var secondHost = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = second });
        var controller = new KillerPDF.Features.SearchController(firstHost);
        var other = new KillerPDF.Features.SearchController(secondHost);
        controller.Run("A");
        other.Run("A");
        await Task.WhenAll(firstHost.Completed.Task, secondHost.Completed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        var firstParked = controller.Capture();
        firstHost.NativeSearchRevisionId = second.Id;
        controller.Restore(other.Capture());
        Assert.True(controller.ResultsCurrent);
        Assert.Equal(second.Id, controller.Capture().NativeRevisionId);
        firstHost.NativeSearchRevisionId = first.Id;
        controller.Restore(firstParked);
        Assert.True(controller.ResultsCurrent);
        Assert.Equal(first.Id, controller.Capture().NativeRevisionId);
    }

    [Fact]
    public async Task SearchControllerRestoresNativeSearchAtItsOriginalPathAndRevision()
    {
        using var first = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        using var second = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A A")));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = first });
        var otherHost = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = second })
            { CurrentFile = "another-book.djvu", SearchRevision = 4 };
        var controller = new KillerPDF.Features.SearchController(host);
        var other = new KillerPDF.Features.SearchController(otherHost);
        controller.Run("A");
        other.Run("A");
        await Task.WhenAll(host.Completed.Task, otherHost.Completed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        host.NativeSearchRevisionId = second.Id;
        host.CurrentFile = otherHost.CurrentFile;
        host.SearchRevision = 4;
        controller.Restore(other.Capture());
        Assert.True(controller.ResultsCurrent);
        host.SearchRevision = 5;
        Assert.False(controller.ResultsCurrent);
    }

    [Fact]
    public async Task SearchControllerDropsPendingResultsWhenNativeOwnerChangesDuringCapture()
    {
        using var first = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        using var second = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = first });
        host.OnCapture = () => host.NativeSearchRevisionId = second.Id;
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (controller.IsSearching) await Task.Delay(1, deadline.Token);
        Assert.Null(controller.CurrentOccurrence);
        Assert.False(host.Completed.Task.IsCompleted);
    }

    [Fact]
    public async Task SearchControllerDoesNotRestoreNativeMatchesIntoAnotherOwner()
    {
        using var first = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        using var second = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = first });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A");
        await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var parked = controller.Capture();
        controller.Restore(parked);
        Assert.NotNull(controller.CurrentOccurrence);
        host.NativeSearchRevisionId = second.Id;
        controller.Restore(parked);
        Assert.Null(controller.CurrentOccurrence);
        Assert.False(controller.HasResults);
    }

    [Fact]
    public async Task SearchControllerRejectsResultsAfterNativeOwnerChangesAtTheSamePath()
    {
        using var first = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        using var second = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = first });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A");
        await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(controller.ResultsCurrent);
        host.NativeSearchRevisionId = second.Id;
        Assert.False(controller.ResultsCurrent);
    }

    [Fact]
    public async Task SearchControllerNavigatesNativeMatchesInLogicalSourceOrder()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A A",
            Zone(10, 20, 10, 10, 0, 1), Zone(10, 100, 10, 10, 2, 1))));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = revision });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A");
        await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, controller.CurrentOccurrence!.Start);
        controller.Next();
        Assert.Equal(2, controller.CurrentOccurrence!.Start);
    }

    [Fact]
    public async Task SearchControllerPreservesSeparateAreasOfOneNativePhrase()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A A",
            Zone(10, 20, 30, 40, 0, 1), Zone(50, 80, 30, 40, 2, 1))));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = revision });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A A");
        Assert.Equal("1 / 1", await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, controller.AllSearchRects[0].Count);
        var first = controller.AllSearchRects[0][0];
        var second = controller.AllSearchRects[0][1];
        Assert.Equal(2.4, first.left, 12);
        Assert.Equal(4.8, first.bottom, 12);
        Assert.Equal(9.6, first.right, 12);
        Assert.Equal(14.4, first.top, 12);
        Assert.Equal(12.0, second.left, 12);
        Assert.Equal(19.2, second.bottom, 12);
        Assert.Equal(19.2, second.right, 12);
        Assert.Equal(28.8, second.top, 12);
    }

    [Fact]
    public async Task SearchControllerCountsUnlocatedNativeMatchesWithoutPaintingRectangles()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A A")));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = revision });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A");
        Assert.Equal("1 / 2", await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(controller.HasResults);
        Assert.Empty(controller.AllSearchRects);
        Assert.Null(controller.CurrentMatch);
        Assert.Equal(new[] { 0 }, controller.ResultPages);
        controller.Next();
        Assert.Equal(2, controller.CurrentOccurrence!.Start);
    }

    private sealed class SearchHost(CurrentSearchSnapshot source) : KillerPDF.Features.ISearchHost
    {
        public Guid? NativeSearchRevisionId { get; set; } = source.NativeRevision?.Id;
        public TaskCompletionSource<string> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Windows.Window Window => throw new NotSupportedException();
        public string? CurrentFile { get; set; } = "native-search-controller.djvu";
        public long SearchRevision { get; set; }
        public int CurrentPageIndex => 0;
        public Action? OnCapture { get; set; }
        public CurrentSearchSnapshot CaptureSearchSnapshot()
        {
            OnCapture?.Invoke();
            return source;
        }
        public string Loc(string key) => key;
        public void SetStatus(string text) { }
        public void GoToPage(int pageIdx) { }
        public void SetResultText(string text) => Completed.TrySetResult(text);
        public void SetResultCount(string text, string? tooltip) => Completed.TrySetResult(text);
        public void ClearHighlights() { }
        public bool? ReplaceShown { get; private set; }
        public void ShowReplaceAvailable(bool available) => ReplaceShown = available;
        public Func<int, bool> OnScreen { get; set; } = _ => true;
        public bool IsPageOnScreen(int pageIdx) => OnScreen(pageIdx);
        public int Repaints { get; private set; }
        public void RepaintHighlights() => Repaints++;
        public bool IsCovered(int pageIdx, double left, double bottom, double right, double top) => false;
    }

    [Fact]
    public void NativeSearchFinishesDenseDisjointTextWithinItsCancellationDeadline()
    {
        byte[][] zones = Enumerable.Range(0, 20_000)
            .Select(index => Zone(index % 100, index / 100, 1, 1, index, 1)).ToArray();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(new string('A', 20_000), zones)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "A", true, false, deadline.Token);
        Assert.Equal(20_000, result.TotalHits);
        Assert.Equal(20_000, result.PageRects[0].Count);
    }

    [Fact]
    public void NativeSearchRejectsExcessiveUnlocatedMatchesWithoutReturningPartialResults()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(new string('A', 100_001))));
        var error = Assert.Throws<InvalidDataException>(() => SearchService.Search(
            new CurrentSearchSnapshot([], []) { NativeRevision = revision }, "A", true, false, CancellationToken.None));
        Assert.Contains("match limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeSearchRejectsExcessiveHighlightOutputWithoutReturningPartialMatches()
    {
        byte[][] zones = Enumerable.Range(0, 4096).Select(index => Zone(index % 100, index / 100, 1, 1, 0, 64)).ToArray();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(new string('A', 64), zones)));
        var error = Assert.Throws<InvalidDataException>(() => SearchService.Search(
            new CurrentSearchSnapshot([], []) { NativeRevision = revision }, "A", true, false, CancellationToken.None));
        Assert.Contains("area limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableNativePagesAreReportedInsteadOfBeingTreatedAsAbsentText(bool allUnreadable)
    {
        byte[] bad = Page("A");
        bad[^2] = 2;
        byte[] bytes = allUnreadable ? bad : Bundle(Page("A", Zone(1, 2, 3, 4, 0, 1)), bad);
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(bytes));
        var source = new CurrentSearchSnapshot([], []) { NativeRevision = revision };
        if (allUnreadable)
        {
            var error = Assert.Throws<InvalidDataException>(() => SearchService.Search(source, "A", true, false, CancellationToken.None));
            Assert.Contains("No page", error.Message, StringComparison.Ordinal);
            return;
        }
        Assert.Equal(2, revision.PageCount);
        var result = SearchService.Search(source, "A", true, false, CancellationToken.None);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal(new[] { 0 }, result.ResultPages);
        Assert.Equal(new[] { 1 }, result.UnreadablePages);
        Assert.Contains("version", result.UnreadableMessages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NativeSearchCancellationAndRetirementDoNotReturnFalseNoMatchResults()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page("A", Zone(1, 2, 3, 4, 0, 1)));
        using var retired = new PdfCapturedReadRevision(snapshot);
        using var current = new PdfCapturedReadRevision(snapshot);
        var oldSource = new CurrentSearchSnapshot([], []) { NativeRevision = retired };
        var currentSource = new CurrentSearchSnapshot([], []) { NativeRevision = current };
        Assert.ThrowsAny<OperationCanceledException>(() => SearchService.Search(currentSource, "A", true, false,
            new CancellationToken(true)));
        retired.Dispose();
        Assert.Throws<ObjectDisposedException>(() => SearchService.Search(oldSource, "A", true, false, CancellationToken.None));
        Assert.Equal(1, SearchService.Search(currentSource, "A", true, false, CancellationToken.None).TotalHits);
    }

    [Theory]
    [InlineData("a", true, false, 1)]
    [InlineData("a", false, false, 4)]
    [InlineData("a", false, true, 2)]
    [InlineData("fi", true, true, 1)]
    public void NativeSearchUsesExistingFoldingCaseAndWholeWordRules(string query, bool matchCase, bool wholeWord, int count)
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(
            Page("A a AA \ufb01", Zone(10, 20, 30, 40, 0, 10))));
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            query, matchCase, wholeWord, CancellationToken.None);
        Assert.Equal(count, result.TotalHits);
        if (query == "fi") Assert.Equal((7, 1), (Assert.Single(result.Hits).Start, result.Hits[0].Length));
    }

    [Fact]
    public void SearchesCapturedEpubLayoutWithoutPdfConversion()
    {
        using var resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        byte[] bytes = Book();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenEpub(bytes,
            new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 }));
        Array.Clear(bytes);
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "A", true, true, CancellationToken.None);
        Assert.Equal(2, result.TotalHits);
        Assert.Equal(new[] { 0, 2 }, result.Hits.Select(hit => hit.Start));
        Assert.Equal(new[] { 0 }, result.ResultPages);
        Assert.All(result.Hits, hit => Assert.False(hit.Source!.Editable));

        var located = SearchService.LocateNative(revision, 0, result.Hits, CancellationToken.None);
        Assert.All(located, hit =>
        {
            Assert.True(hit.HasGeometry);
            Assert.False(hit.Source!.Editable);
            Assert.Single(hit.Areas);
            Assert.Equal(24, hit.bottom, 12);
            Assert.Equal(33, hit.top, 12);
        });
        Assert.Equal(3, located[0].left, 12);
    }

    // A hit found from page text alone gets its highlight areas when its page is located: the same areas, union and
    // page text a search that read the geometry gives (see SearchesCapturedEpubLayoutWithoutPdfConversion).
    [Fact]
    public void LocatingAnEpubPagesHitsGivesTheirHighlightAreas()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenEpub(Book(), SmallPage()));
        SearchHit Unlocated(int start) => new(0, 0, 0, 0, 0, start, 1) { HasGeometry = false, Located = false };

        var located = SearchService.LocateNative(revision, 0, [Unlocated(2), Unlocated(0)], CancellationToken.None);

        Assert.Equal(new[] { 0, 2 }, located.Select(hit => hit.Start));
        Assert.All(located, hit =>
        {
            Assert.True(hit.Located);
            Assert.True(hit.HasGeometry);
            Assert.Equal("A A", hit.Source!.Content.TrimEnd('\n'));
            Assert.False(hit.Source.Editable);
            var area = Assert.Single(hit.Areas);
            Assert.Equal((area.Left, area.Bottom, area.Right, area.Top), (hit.left, hit.bottom, hit.right, hit.top));
            Assert.Equal(24, hit.bottom, 12);
            Assert.Equal(33, hit.top, 12);
        });
        Assert.Equal(3, located[0].left, 12);
    }

    private static EpubLayoutOptions SmallPage()
    {
        using var resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        return new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 };
    }

    // Search matches each page's indexed text first; only a page with a match reads its geometry, so a rare word
    // lays out at most the one chapter it is in instead of every chapter of the book.
    [Fact]
    public void NativeSearchLaysOutOnlyTheChapterWithTheMatch()
    {
        using var resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        string filler = string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA</p>", 30));
        var snapshot = PdfReadSnapshot.OpenEpub(Chapters(filler, filler, filler + "<p>AA BB</p>", filler, filler),
            new EpubLayoutOptions(200, 160, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 });
        using var revision = new PdfCapturedReadRevision(snapshot);
        var chapters = ChapterLayouts(snapshot);
        chapters.GetType().GetProperty("PrefetchNeighbors", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(chapters, false);
        Assert.True(snapshot.PageCount > 10);

        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "BB", true, true, CancellationToken.None);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("BB", hit.Source!.Content.Substring(hit.Start, hit.Length));
        Assert.Equal(new[] { hit.page }, result.ResultPages);
        Assert.True(Assert.Single(SearchService.LocateNative(revision, hit.page, [hit], CancellationToken.None)).HasGeometry);
        int layouts = ChapterLayoutCount(chapters);
        Assert.True(layouts <= 1, $"A search with one match laid out {layouts} chapters.");
    }

    // A common word matches on nearly every page. Its hit list (pages, counts and the text around each hit) comes from
    // the page index alone, so listing it lays out no chapter; highlight areas are read later for the pages shown.
    [Fact]
    public void NativeSearchListsACommonWordsHitsWithoutLayingOutAChapter()
    {
        string filler = string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA</p>", 30));
        var snapshot = PdfReadSnapshot.OpenEpub(Chapters(filler, filler, filler, filler, filler), MediumPage());
        using var revision = new PdfCapturedReadRevision(snapshot);
        var chapters = ChapterLayouts(snapshot);
        StopLayingOutAhead(chapters);
        Assert.True(snapshot.PageCount > 10);

        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "AAAA", true, true, CancellationToken.None);

        // Two whole-word matches in each of 30 paragraphs in each of 5 chapters.
        Assert.Equal(300, result.TotalHits);
        Assert.Equal(300, result.Hits.Count);
        Assert.True(result.ResultPages.Count > 10);
        Assert.All(result.Hits, hit => Assert.Equal("AAAA", hit.Source!.Content.Substring(hit.Start, hit.Length)));
        Assert.Equal(0, ChapterLayoutCount(chapters));
    }

    // The search bar reads highlight areas only for what the reader sees: the current hit's page at once, and any other
    // page when the painter asks for it, each laying out about one page from the page index.
    [Fact]
    public async Task SearchControllerLocatesOnlyTheCurrentHitsPageAndThePagesItIsAskedFor()
    {
        string filler = string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA</p>", 30));
        var snapshot = PdfReadSnapshot.OpenEpub(Chapters(filler, filler, filler, filler, filler), MediumPage());
        using var revision = new PdfCapturedReadRevision(snapshot);
        var chapters = ChapterLayouts(snapshot);
        StopLayingOutAhead(chapters);
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = revision });
        var controller = new KillerPDF.Features.SearchController(host) { MatchCase = true, WholeWord = true };

        controller.Run("AAAA");
        Assert.Equal("1 / 300", await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // One repaint when the search lands, one when the current hit's areas arrive.
        await Until(() => host.Repaints >= 2);

        var current = controller.CurrentOccurrence!;
        Assert.Equal(0, current.page);
        Assert.True(current.HasGeometry);
        Assert.NotEmpty(current.Areas);
        Assert.All(current.Areas, area => Assert.True(controller.IsCurrentArea(0, area)));
        Assert.NotNull(controller.CurrentMatch);
        Assert.Equal(new[] { 0 }, controller.AllSearchRects.Keys);
        Assert.Equal(controller.ResultPages, controller.PagesWithResults);
        Assert.Equal(1, ChapterLayoutCount(chapters));

        int last = controller.ResultPages[^1];
        Assert.False(controller.TryGetPageRects(last, out _));
        await Until(() => host.Repaints >= 3);
        Assert.True(controller.TryGetPageRects(last, out var rectangles));
        Assert.NotEmpty(rectangles);
        Assert.Equal(new[] { 0, last }, controller.AllSearchRects.Keys.Order());
        Assert.Equal(2, ChapterLayoutCount(chapters));
        Assert.Equal(3, host.Repaints);
    }

    // A long book can have an overlay for every page, and the painter offers each of them. Only the pages in the
    // viewport have their highlight areas read, so painting cannot lay out the whole book behind the reader's page.
    [Fact]
    public async Task SearchControllerLocatesNoPageOffTheScreen()
    {
        string filler = string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA</p>", 30));
        var snapshot = PdfReadSnapshot.OpenEpub(Chapters(filler, filler, filler, filler, filler), MediumPage());
        using var revision = new PdfCapturedReadRevision(snapshot);
        var chapters = ChapterLayouts(snapshot);
        StopLayingOutAhead(chapters);
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = revision }) { OnScreen = page => page == 0 };
        var controller = new KillerPDF.Features.SearchController(host) { MatchCase = true, WholeWord = true };

        controller.Run("AAAA");
        Assert.Equal("1 / 300", await host.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Until(() => host.Repaints >= 2);
        foreach (int page in controller.PagesWithResults.ToArray()) controller.TryGetPageRects(page, out _);
        await Task.Delay(300);

        Assert.Equal(new[] { 0 }, controller.AllSearchRects.Keys);
        Assert.Equal(1, ChapterLayoutCount(chapters));
        Assert.True(controller.WaitsForPagesOnScreen);

        int last = controller.ResultPages[^1];
        host.OnScreen = page => page == last;
        Assert.False(controller.TryGetPageRects(last, out _));
        await Until(() => host.Repaints >= 3);
        Assert.True(controller.TryGetPageRects(last, out _));
        Assert.Equal(2, ChapterLayoutCount(chapters));
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }

    // Highlight rectangles are in page points, like all native text geometry. An EPUB page is laid out in CSS pixels at
    // 96 per inch, so the 200 by 160 pixel page the highlights are placed on measures 150 by 120 points; a hit at the
    // right margin then sits at the page's right edge instead of three quarters of the way across.
    [Fact]
    public void NativeHighlightsArePlacedOnThePageMeasuredInPoints()
    {
        var snapshot = PdfReadSnapshot.OpenEpub(Chapters("<p style='text-align:right'>AAAA</p>"), MediumPage());
        using var revision = new PdfCapturedReadRevision(snapshot);

        var size = KillerPDF.Features.SearchController.NativeHighlightPageSize(snapshot.GetPageGeometry(0));

        Assert.Equal((150.0, 120.0), size);
        var hit = Assert.Single(SearchService.LocateNative(revision, 0,
            [new SearchHit(0, 0, 0, 0, 0, 0, 4) { HasGeometry = false, Located = false }], CancellationToken.None));
        // Right-aligned text ends inside the small right margin, so within the last tenth of the page's width.
        Assert.InRange(hit.right, size.Width * 0.9, size.Width);
    }

    private static EpubLayoutOptions MediumPage()
    {
        using var resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        return new EpubLayoutOptions(200, 160, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 };
    }

    private static void StopLayingOutAhead(object chapters) =>
        chapters.GetType().GetProperty("PrefetchNeighbors", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(chapters, false);

    private static int ChapterLayoutCount(object chapters) =>
        (int)chapters.GetType().GetProperty("Layouts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(chapters)!;

    // The search bar offers Replace only on a tab whose text can be replaced. An EPUB or DjVu tab is read-only until it
    // is converted to PDF, so the bar there is Find alone; coming back to a PDF tab, or opening one, brings Replace back.
    [Fact]
    public void ReplaceIsOfferedOnlyOnPdfTabs()
    {
        using var book = new PdfCapturedReadRevision(PdfReadSnapshot.OpenEpub(Book(), SmallPage()));
        using var scan = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        var host = new SearchHost(new CurrentSearchSnapshot([], []) { NativeRevision = book });
        var controller = new KillerPDF.Features.SearchController(host);

        Assert.False(controller.ReplaceAvailable);
        controller.Restore(null);
        Assert.False(host.ReplaceShown);

        host.NativeSearchRevisionId = null;
        Assert.True(controller.ReplaceAvailable);
        controller.Restore(null);
        Assert.True(host.ReplaceShown);

        host.NativeSearchRevisionId = scan.Id;
        controller.ClearPageResults();
        Assert.False(host.ReplaceShown);
        host.NativeSearchRevisionId = null;
        controller.ClearPageResults();
        Assert.True(host.ReplaceShown);
    }

    // An EPUB tab is read-only until Convert to PDF: opening the book lets the tab's PDF working document go, and both
    // Replace commands stop at the missing document before they read the current match or run a search. So a hit whose
    // highlight areas are still being read is never replaced, and Replace never starts the search over.
    [Fact]
    public void ReplaceOnAnEpubTabStopsBeforeItReadsTheMatchOrSearches()
    {
        string adopt = Between(Source("Shell", "NativeDocuments.cs"), "private void AdoptNativeDocument(",
            "ActiveViewer.NativeDocumentRef = opened;");
        Assert.Contains("_doc = null;", adopt);
        string bar = Source("Shell", "Search.cs");
        foreach (var (start, end) in new[] { ("private void ReplaceCurrentMatch()", "private void ReplaceAllMatches()"),
            ("private void ReplaceAllMatches()", "private void AfterReplace(") })
        {
            string body = Between(bar, start, end);
            int guard = body.IndexOf("_doc is null) return;", StringComparison.Ordinal);
            Assert.True(guard >= 0, start);
            foreach (string use in new[] { "Search.", "ReplaceText", "EnsureEditable" })
            {
                int at = body.IndexOf(use, StringComparison.Ordinal);
                Assert.True(at < 0 || at > guard, $"{start} uses {use} before the document check.");
            }
        }
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Not found: " + start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "Not found: " + end);
        return source[from..to];
    }

    private static string Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    // The engine's chapter re-layout cache, reached by reflection because the app tests do not see engine internals.
    private static object ChapterLayouts(PdfReadSnapshot snapshot)
    {
        const BindingFlags members = BindingFlags.NonPublic | BindingFlags.Instance;
        object layout = typeof(PdfReadSnapshot).GetProperty("EpubLayout", members)!.GetValue(snapshot)!;
        return layout.GetType().GetProperty("ChapterPages", members)!.GetValue(layout)!;
    }

    internal static byte[] Chapters(params string[] bodies)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            string manifest = string.Concat(bodies.Select((_, index) =>
                $"<item id=\"c{index}\" href=\"c{index}.xhtml\" media-type=\"application/xhtml+xml\"/>"));
            string spine = string.Concat(bodies.Select((_, index) => $"<itemref idref=\"c{index}\"/>"));
            Add("EPUB/book.opf", $"""
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-search-chapters</dc:identifier><dc:title>Native search</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-05T00:00:00Z</meta></metadata>
                  <manifest>{manifest}<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine>{spine}</spine>
                </package>
                """);
            for (int index = 0; index < bodies.Length; index++)
                Add($"EPUB/c{index}.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Chapter</title></head><body>"
                    + bodies[index] + "</body></html>");
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="c0.xhtml">Native search</a></li></ol></nav></body></html>
                """);
            void Add(string path, string text, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }

    [Fact]
    public void NativeSearchIdentitySeparatesOwnersOfTheSameSource()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page("A", Zone(1, 2, 3, 4, 0, 1)));
        using var first = new PdfCapturedReadRevision(snapshot);
        using var second = new PdfCapturedReadRevision(snapshot);
        var source = new CurrentSearchSnapshot([], []) { NativeRevision = first };
        var same = new CurrentSearchSnapshot([], []) { NativeRevision = first };
        var other = new CurrentSearchSnapshot([], []) { NativeRevision = second };
        Assert.True(source.SameContent(same));
        Assert.Equal(source.Fingerprint(), same.Fingerprint());
        Assert.False(source.SameContent(other));
        Assert.NotEqual(source.Fingerprint(), other.Fingerprint());
        Assert.Equal(first.Id, SearchService.Search(source, "A", true, false, CancellationToken.None).NativeRevisionId);
        Assert.Equal(second.Id, SearchService.Search(other, "A", true, false, CancellationToken.None).NativeRevisionId);
    }

    [Fact]
    public void NativePhraseUsesSeveralActualAreasForOneLogicalOccurrence()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A A",
            Zone(10, 20, 30, 40, 0, 1), Zone(50, 80, 30, 40, 2, 1))));
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "A A", true, false, CancellationToken.None);
        Assert.Equal(1, result.TotalHits);
        var hit = Assert.Single(result.Hits);
        Assert.Equal((0, 3), (hit.Start, hit.Length));
        Assert.Equal(2, hit.Areas.Count);
        Assert.Equal(2, result.PageRects[0].Count);
        Assert.Equal(2.4, hit.Areas[0].Left, 12);
        Assert.Equal(14.4, hit.Areas[0].Top, 12);
        Assert.Equal(12, hit.Areas[1].Left, 12);
        Assert.Equal(19.2, hit.Areas[1].Bottom, 12);
        Assert.Equal(28.8, hit.Areas[1].Top, 12);
    }

    [Fact]
    public void UnlocatedNativeTextMatchesWithoutInventingAHighlightRectangle()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "A", true, false, CancellationToken.None);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal(new[] { 0 }, result.ResultPages);
        Assert.False(Assert.Single(result.Hits).HasGeometry);
        Assert.Empty(result.PageRects);
    }

    [Fact]
    public void SearchesCapturedNativeUnicodeTextWithoutParsingPdfBytes()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A\u00e9\U0001f600",
            Zone(10, 20, 30, 40, 0, 7))));
        var source = new CurrentSearchSnapshot([], []) { NativeRevision = revision };
        var result = SearchService.Search(source, "\u00e9\U0001f600", true, false, CancellationToken.None);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal(new[] { 0 }, result.ResultPages);
        var hit = Assert.Single(result.Hits);
        Assert.Equal((0, 1, 3), (hit.page, hit.Start, hit.Length));
        Assert.Equal(2.4, hit.left, 12);
        Assert.Equal(4.8, hit.bottom, 12);
        Assert.Equal(9.6, hit.right, 12);
        Assert.Equal(14.4, hit.top, 12);
        Assert.NotNull(hit.Source);
        Assert.False(hit.Source.Editable);
        Assert.Single(result.PageRects[0]);
    }

    internal static byte[] Book(string? body = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-search</dc:identifier><dc:title>Native search</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-05T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native search</title></head><body><p>A A</p></body></html>
                """.Replace("<p>A A</p>", body ?? "<p>A A</p>", StringComparison.Ordinal));
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native search</a></li></ol></nav></body></html>
                """);
            void Add(string path, string text, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }

    private static byte[] Bundle(byte[] firstPage, byte[] secondPage)
    {
        byte[] first = firstPage[4..], second = secondPage[4..];
        byte[] directory = [0x81, 0, 2, 0, 0, 0, 36, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32BigEndian(directory.AsSpan(7), 36 + first.Length);
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", directory), .. first, .. second])];
    }

    private static byte[] Page(string text, params byte[][] zones)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]),
            .. Chunk("TXTa", [(byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length,
                .. bytes, 1, .. zones.SelectMany(zone => zone)])])];
    }

    private static byte[] Zone(int left, int bottom, int width, int height, int start, int length)
    {
        var result = new byte[17];
        result[0] = 6;
        int[] values = [left, bottom, width, height, start];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1 + index * 2), checked((ushort)(values[index] + 0x8000)));
        result[11] = (byte)(length >> 16); result[12] = (byte)(length >> 8); result[13] = (byte)length;
        return result;
    }

    private static byte[] Chunk(string id, byte[] data) => [.. Encoding.ASCII.GetBytes(id),
        (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
        .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];
}
