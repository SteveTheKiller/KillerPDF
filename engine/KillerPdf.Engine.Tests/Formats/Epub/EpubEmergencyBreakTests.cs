using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Decision 0009: a word with no break opportunity breaks at the content width instead of refusing its chapter.
// The test font draws A 6px wide and a space 5px wide at 10px; a 16px page with 2px margins leaves 12px.
public sealed class EpubEmergencyBreakTests
{
    [Fact]
    public void AWordWiderThanTheLineBreaksWhereTheNextLetterNoLongerFits()
    {
        var page = Assert.Single(Layout(new Book { Chapters = { Chapter("<p>AAA</p>") } }, 16, 40).Pages);

        // Two 6px letters fill the 12px line; the third starts the next 10px line.
        Assert.Equal(new[]
        {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 6, 10),
            new EpubTextPlacement(0, 2, 1, 2, 12, 6, 10)
        }, page.TextPlacements);
    }

    [Fact]
    public void ALongWordAfterAShortOneStartsItsOwnLineBeforeBreaking()
    {
        var page = Assert.Single(Layout(new Book { Chapters = { Chapter("<p>A AAA</p>") } }, 16, 40).Pages);

        Assert.Equal(new[]
        {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 2, 1, 2, 12, 6, 10),
            new EpubTextPlacement(0, 3, 1, 8, 12, 6, 10),
            new EpubTextPlacement(0, 4, 1, 2, 22, 6, 10)
        }, page.TextPlacements);
    }

    [Fact]
    public void AnIndentedFirstWordWiderThanTheWholeLineBreaksToo()
    {
        var css = "p { text-indent: 6px }";
        var page = Assert.Single(Layout(new Book { Css = css, Chapters = { Chapter("<p>AAA</p>") } }, 16, 40).Pages);

        // After the 6px indent one letter fits; the remaining two fill the next line.
        Assert.Equal(new[]
        {
            new EpubTextPlacement(0, 0, 1, 8, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 2, 12, 6, 10),
            new EpubTextPlacement(0, 2, 1, 8, 12, 6, 10)
        }, page.TextPlacements);
    }

    [Fact]
    public void APreformattedLineWiderThanTheContentBoxBreaks()
    {
        var page = Assert.Single(Layout(new Book { Chapters = { Chapter("<pre>AAA</pre>") } }, 16, 60).Pages);

        var placements = page.TextPlacements;
        Assert.Equal(new double[] { 2, 8, 2 }, placements.Select(placement => placement.Left));
        Assert.Equal(placements[0].Top, placements[1].Top);
        Assert.Equal(placements[0].Top + 10, placements[2].Top);
    }

    [Fact]
    public void ATableRowTallerThanAPageLaysItsCellsOutAsBlocks()
    {
        // Five 10px lines cannot fit the 36px content box of a 40px page, so the row cannot stay whole.
        var layout = Layout(new Book { Chapters = { Chapter("<table><tr><td>A<br/>A<br/>A<br/>A<br/>A</td></tr></table>") } }, 30, 40);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(new double[] { 2, 12, 22 }, layout.Pages[0].TextPlacements.Select(placement => placement.Top));
        Assert.Equal(new double[] { 2, 12 }, layout.Pages[1].TextPlacements.Select(placement => placement.Top));
        Assert.All(layout.Pages.SelectMany(page => page.TextPlacements), placement => Assert.Equal(2, placement.Left));
    }
}
