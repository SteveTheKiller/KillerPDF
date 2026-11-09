using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// A laid-out book keeps a page index and lays chapters out again on demand; every page must come back identical.
public sealed class EpubChapterRelayoutTests
{
    [Fact]
    public void PagesReadInAnyOrderMatchTheirFirstLayoutExactly()
    {
        // Spaced paragraphs, colored and linked text, a list, hidden text and a table, over enough chapters that the
        // three-chapter cache is emptied and refilled. The body margin and paragraph padding exercise the rule that only
        // the book's first page folds a paragraph's top margin into the body margin.
        string body = "<p id='top'>AAA AAAA <a href='c0.xhtml#top'>AA</a> <span style='color:#c00000'>AAAAA</span></p>"
            + "<ol><li>AAAA</li><li>AA AA</li></ol><p style='visibility:hidden'>AAAA</p>"
            + "<table style='width:200px'><tr><td style='width:100px' id='cell'>AA</td><td style='width:100px'>AAA</td></tr></table>"
            + string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 40));
        var book = new Book { Css = "body { margin: 8px } p { margin-top: 20px; padding-top: 1px }" };
        for (int chapter = 0; chapter < 6; chapter++) book.Chapters.Add(Chapter(body));
        var first = new Dictionary<int, string>();
        var document = EpubDocument.Open(book.Build());

        var layout = document.CreateLayout(Options(), (page, index) => first.Add(index, Fingerprint(page)), CancellationToken.None);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(layout.Pages.Count, first.Count);
        Assert.True(layout.Pages.Select(page => page.SpineIndex).Distinct().Count() == 6);
        // Last to first, then every third page: each chapter is evicted and laid out again several times.
        var order = Enumerable.Range(0, layout.Pages.Count).Reverse()
            .Concat(Enumerable.Range(0, layout.Pages.Count).Where(index => index % 3 == 0));
        foreach (int index in order) Assert.Equal(first[index], Fingerprint(layout.Pages[index]));
    }

    // A cold page turn lays out only as far as the page it needs; later pages cannot change earlier ones.
    [Fact]
    public void OpeningAPageLaysItsChapterOutOnlyUpToThatPage()
    {
        var book = new Book();
        for (int chapter = 0; chapter < 3; chapter++)
            book.Chapters.Add(Chapter(string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 400))));
        var layout = EpubDocument.Open(book.Build()).CreateLayout(Options());
        int first = Enumerable.Range(0, layout.Pages.Count).First(index => layout.Pages[index].SpineIndex == 1);
        int chapterPages = layout.Pages.Count(page => page.SpineIndex == 1);
        Assert.True(chapterPages > 4);
        var cache = layout.ChapterPages!;
        cache.PrefetchNeighbors = false;

        Assert.NotEmpty(layout.Pages[first + 1].TextPlacements);

        Assert.Equal(2, cache.ResidentPageCount(1));
        Assert.NotEmpty(layout.Pages[first + chapterPages - 1].TextPlacements);
        Assert.Equal(chapterPages, cache.ResidentPageCount(1));
    }

    // Turning past the end of a chapter, or back before its start, finds the neighbor already laid out.
    [Fact]
    public void ReadingAChapterLaysItsNeighborsOutInTheBackground()
    {
        var book = new Book();
        for (int chapter = 0; chapter < 5; chapter++)
            book.Chapters.Add(Chapter(string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 60))));
        var layout = EpubDocument.Open(book.Build()).CreateLayout(Options());
        var cache = layout.ChapterPages!;
        int middle = Enumerable.Range(0, layout.Pages.Count).First(index => layout.Pages[index].SpineIndex == 2);

        Assert.NotEmpty(layout.Pages[middle].TextPlacements);
        // The background thread may wait behind other books on a busy machine; doing its work here checks what it lays out.
        cache.LayOutAheadNow();

        Assert.Equal(layout.Pages.Count(page => page.SpineIndex == 1), cache.ResidentPageCount(1));
        Assert.Equal(layout.Pages.Count(page => page.SpineIndex == 3), cache.ResidentPageCount(3));
        Assert.Equal(0, cache.ResidentPageCount(0));
        Assert.Equal(0, cache.ResidentPageCount(4));
    }

    private static string Fingerprint(EpubLayoutPage page) => string.Join("|",
        page.Text, page.TextStart, page.SpineIndex, page.CanvasBackground,
        string.Join(";", page.TextPlacements), string.Join(";", page.NavigationPlacements),
        string.Join(";", page.Glyphs), string.Join(";", page.HiddenTextRanges),
        string.Join(";", page.Clusters.Values.Select(cluster => $"{cluster.Start},{cluster.Length},{cluster.Font.GetHashCode()},"
            + $"{cluster.Substituted},{cluster.Blank},{cluster.Missing},{string.Join(",", cluster.Glyphs)}")),
        string.Join(";", page.Markers));
}
