using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// Two hits that share one text region each get their own share of it by character position along the line. In a
// right-to-left run the first characters are at the right end.
public sealed class SearchRegionShareTests
{
    [Fact]
    public void ALeftToRightRegionIsSharedFromItsLeftEdge()
    {
        const string text = "abcdefghi";
        var region = new PdfReadTextRegion(0, 9, new PdfContentBounds(0, 0, 90, 10));

        var first = SearchService.ShareOfRegion(region, 0, 4, text);

        Assert.Equal(0, first.Left, 6);
        Assert.Equal(40, first.Right, 6);
    }

    [Fact]
    public void ARightToLeftRegionIsSharedFromItsRightEdge()
    {
        // Nine Hebrew letters; the first one is at the right end of the line.
        const string text = "\u05E9\u05DC\u05D5\u05DD\u05E2\u05D5\u05DC\u05DD\u05D0";
        var region = new PdfReadTextRegion(0, 9, new PdfContentBounds(0, 0, 90, 10));

        var first = SearchService.ShareOfRegion(region, 0, 4, text);
        var second = SearchService.ShareOfRegion(region, 5, 4, text);

        Assert.Equal(50, first.Left, 6);
        Assert.Equal(90, first.Right, 6);
        Assert.Equal(0, second.Left, 6);
        Assert.Equal(40, second.Right, 6);
    }

    [Fact]
    public void ARegionThatStartsWithNumbersTakesItsDirectionFromTheFirstLetter()
    {
        const string text = "12 \u0627\u0644\u0639\u0631\u0628";
        var region = new PdfReadTextRegion(0, text.Length, new PdfContentBounds(0, 0, 70, 10));

        var digits = SearchService.ShareOfRegion(region, 0, 2, text);

        Assert.Equal(52.5, digits.Left, 6);
        Assert.Equal(70, digits.Right, 6);
    }
}
