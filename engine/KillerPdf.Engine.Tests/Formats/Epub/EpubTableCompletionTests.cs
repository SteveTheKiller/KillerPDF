using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Documents;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Tables that used to refuse their chapter. The test font draws A 6px wide at 10px; a 60px page with 2px margins
// leaves 56px, and lines and rows are 10px tall.
public sealed class EpubTableCompletionTests
{
    [Theory]
    [InlineData("100%", 30d)]
    [InlineData("40px", 22d)]
    public void ExplicitWidthTableWithAutomaticCellsSharesItsWidth(string width, double secondLeft)
    {
        string body = "<table style='width:" + width + "'><tr><td>A</td><td>A</td></tr></table>";
        var page = Single(body);
        Assert.Equal(new[] { (2d, 2d), (secondLeft, 2d) }, Letters(page));
        var book = EpubDocument.Open(new Book { Chapters = { Chapter(body) } }.Build());
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont))));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("AA", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
    }

    // An explicit-width table takes its columns from its first row; a spanning cell covers the sum of its columns.
    [Theory]
    [InlineData("<td colspan='2'>AAA</td>")]
    [InlineData("<td colspan='2' style='width:40px'>AAA</td>")]
    public void AColspanCellInAnExplicitWidthTableSpansItsColumns(string spanning)
    {
        var page = Single("<table style='width:40px'><tr><td style='width:20px'>A</td><td style='width:20px'>A</td></tr>"
            + "<tr>" + spanning + "</tr><tr><td style='width:20px'>A</td><td style='width:20px'>A</td></tr></table>");

        Assert.Equal(new[] { (2d, 2d), (22d, 2d), (2d, 12d), (8d, 12d), (14d, 12d), (2d, 22d), (22d, 22d) }, Letters(page));
    }

    [Fact]
    public void ANestedExplicitTableLaysOutInsideItsCell()
    {
        var page = Single("<table style='width:40px'><tr><td style='width:40px'>"
            + "<table style='width:30px'><tr><td style='width:10px'>A</td><td style='width:20px'>A</td></tr></table>"
            + "</td></tr><tr><td style='width:40px'>A</td></tr></table>");

        Assert.Equal(new[] { (2d, 2d), (12d, 2d), (2d, 12d) }, Letters(page));
    }

    // CSS Tables: a table's widths are the sums of its columns, so a nested two-column table makes its outer column 12px.
    [Fact]
    public void ANestedAutomaticTableSizesItsOuterColumnByItsOwnColumns()
    {
        var page = Single("<table><tr><td><table><tr><td>A</td><td>A</td></tr></table></td><td>A</td></tr></table>");

        Assert.Equal(new[] { (2d, 2d), (8d, 2d), (14d, 2d) }, Letters(page));
    }

    // CSS 2.1 section 17.2.1: text directly in a row is wrapped in an anonymous cell, and text directly in a table
    // in an anonymous row with one anonymous cell.
    [Theory]
    [InlineData("<table><tr><td>A</td>AA</tr></table>", new[] { 2d, 8, 14 }, new[] { 2d, 2, 2 })]
    [InlineData("<table><tr><td>A</td></tr>AA</table>", new[] { 2d, 2, 8 }, new[] { 2d, 12, 12 })]
    [InlineData("<table><tbody><tr><td>A</td></tr>AA</tbody></table>", new[] { 2d, 2, 8 }, new[] { 2d, 12, 12 })]
    public void TextDirectlyInATableIsWrappedInAnAnonymousCell(string body, double[] lefts, double[] tops)
    {
        var page = Single(body);

        Assert.Equal(lefts.Zip(tops).ToArray(), Letters(page));
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
