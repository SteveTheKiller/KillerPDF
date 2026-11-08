using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Decision 0009 and EPUB 3.3 section 5.7.4: non-linear spine items follow the linear reading order;
// page-spread and rendition properties on itemrefs only affect synthetic spreads and are ignored.
public sealed class EpubSpineOrderTests
{
    [Fact]
    public void NonLinearItemsAreAppendedAfterTheLinearReadingOrder()
    {
        var book = new Book { Chapters = { Chapter("<p>A</p>"), Chapter("<p>AA</p>"), Chapter("<p>AAA</p>") } };
        book.ItemrefAttributes[1] = " linear=\"no\"";

        var layout = Layout(book);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(new[] { 0, 2, 1 }, layout.Pages.Select(page => page.SpineIndex));
        Assert.Equal(2, layout.ResolveNavigationTarget("OEBPS/text/c1.xhtml").PageIndex);
    }

    [Theory]
    [InlineData(" properties=\"page-spread-right\"")]
    [InlineData(" properties=\"rendition:page-spread-center rendition:layout-reflowable\"")]
    public void SpreadAndRenditionPropertiesAreIgnored(string attributes)
    {
        var book = new Book { Chapters = { Chapter("<p>A</p>"), Chapter("<p>A</p>") } };
        book.ItemrefAttributes[0] = attributes;

        var layout = Layout(book);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(new[] { 0, 1 }, layout.Pages.Select(page => page.SpineIndex));
    }
}
