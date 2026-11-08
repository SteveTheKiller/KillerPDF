using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Project Gutenberg contents tables (Little Women, Pride and Prejudice) use captions, colspan rows and short rows
// in automatic tables. The test font draws A 6px wide at 10px; a 60px page with 2px margins leaves 56px.
public sealed class EpubTableShapeTests
{
    [Fact]
    public void ACaptionLaysOutCenteredAboveTheRowsAtTheTableWidth()
    {
        var page = Single("<table><caption>A</caption><tr><td>A</td><td>A</td></tr></table>");

        // Two 6px columns make a 12px table; the HTML caption is centered in it: 2 + (12 - 6) / 2.
        Assert.Equal(new[] { (5d, 2d), (2d, 12d), (8d, 12d) }, Letters(page));
    }

    [Fact]
    public void AColspanCellSpansItsColumnsAndWidensThemEvenly()
    {
        var page = Single("<table><tr><td colspan='2'>AAA</td></tr><tr><td>A</td><td>A</td></tr></table>");

        // The spanning cell needs 18px over two 6px columns, so each column grows by 3px to 9px.
        Assert.Equal(new[] { (2d, 2d), (8d, 2d), (14d, 2d), (2d, 12d), (11d, 12d) }, Letters(page));
    }

    [Fact]
    public void ARowWithFewerCellsLeavesItsLastColumnsEmpty()
    {
        var page = Single("<table><tr><td>A</td></tr><tr><td>A</td><td>A</td></tr></table>");

        Assert.Equal(new[] { (2d, 2d), (2d, 12d), (8d, 12d) }, Letters(page));
    }

    private static EpubLayoutPage Single(string body)
    {
        var layout = Layout(new Book { Chapters = { Chapter(body) } }, 60, 60);
        Assert.Empty(layout.ChapterDiagnostics);
        return Assert.Single(layout.Pages);
    }

    private static (double Left, double Top)[] Letters(EpubLayoutPage page) => page.TextPlacements
        .Where(placement => page.Text[placement.Start - page.TextStart] == 'A')
        .Select(placement => (placement.Left, placement.Top)).ToArray();
}
