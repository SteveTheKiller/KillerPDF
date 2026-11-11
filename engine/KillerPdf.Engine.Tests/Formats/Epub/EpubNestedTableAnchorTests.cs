using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubNestedTableAnchorTests
{
    // A link to an id inside a table nested in another table's cell lands where that text is drawn: on the page and at
    // the height the outer row was placed, not at the top of the book's first page.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALinkIntoANestedTableLandsOnTheNestedCellsPageAndPosition(bool nested)
    {
        string inner = "<a id='target'/>AAAA";
        string table = nested
            ? "<table style='width:200px'><tr><td style='width:200px'><table style='width:100px'><tr><td style='width:100px'>" + inner + "</td></tr></table></td></tr></table>"
            : "<table style='width:100px'><tr><td style='width:100px'>" + inner + "</td></tr></table>";
        // The page holds four 10 px lines, so six short paragraphs push the table to the second page.
        var layout = Layout(new Book { Chapters = { Chapter(string.Concat(Enumerable.Repeat("<p>A</p>", 6)) + table) } }, 400, 60);

        Assert.Empty(layout.ChapterDiagnostics);
        int pageIndex = Enumerable.Range(0, layout.Pages.Count).First(index => layout.Pages[index].Text.Contains("AAAA"));
        Assert.True(pageIndex > 0);
        var page = layout.Pages[pageIndex];
        var placement = page.TextPlacements.First(item => item.Start == page.TextStart + page.Text.IndexOf("AAAA", StringComparison.Ordinal));
        var destination = layout.ResolveNavigationTarget("OEBPS/text/c0.xhtml#target");
        Assert.Equal(pageIndex, destination.PageIndex);
        Assert.Equal(placement.Top, destination.Top, 3);
        Assert.Equal(placement.Left, destination.Left, 3);
    }
}
