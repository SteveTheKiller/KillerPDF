using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Search and other whole-book text reads use each page's indexed text; only pages whose geometry is needed lay out again.
public sealed class EpubPageTextReadTests
{
    [Fact]
    public void ReadingEveryPagesTextLaysNoChapterOutAgain()
    {
        var book = new Book();
        for (int chapter = 0; chapter < 6; chapter++)
            book.Chapters.Add(Chapter(string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA AAAAA AA AAAA AAA</p>", 60))));
        var snapshot = PdfReadSnapshot.OpenEpub(book.Build(), Options());
        var cache = snapshot.EpubLayout!.ChapterPages!;
        cache.PrefetchNeighbors = false;
        Assert.True(snapshot.PageCount > 6);

        for (int page = 0; page < snapshot.PageCount; page++) Assert.NotEmpty(snapshot.ReadPageText(page));

        Assert.Equal(0, cache.Layouts);
    }

    [Fact]
    public void PageTextLeavesOutHiddenTextAsTheTextPageDoes()
    {
        var book = new Book();
        book.Chapters.Add(Chapter("<p>AA <span style='visibility:hidden'>AAA</span> AAAA</p>"));
        var snapshot = PdfReadSnapshot.OpenEpub(book.Build(), Options());
        snapshot.EpubLayout!.ChapterPages!.PrefetchNeighbors = false;

        string text = snapshot.ReadPageText(0);

        // Two spaces stay where the hidden word was: only the hidden characters themselves are left out.
        Assert.Equal("AA  AAAA", text.TrimEnd('\n'));
        Assert.Equal(snapshot.ReadTextPage(0).Text, text);
    }
}
