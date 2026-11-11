using System.Runtime.InteropServices;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A fault in one optional part of a page (its text layer, the platform JPEG encoder) costs only that part: the page
// still converts and the rest of the book is unaffected.
public sealed class DjvuConversionFallbackTests
{
    [Fact]
    public void ATextLayerInAVersionTheReaderDoesNotKnowCostsOnlyThePageText()
    {
        // The Sjbz of DjvuJb2Tests (56 x 32) with a TXTa chunk holding "abc" and then version byte 2.
        var data = DjvuJb2Tests.Page(56, 32, 1, DjvuJb2Tests.Chunk("Sjbz", Convert.FromHexString("826C97EA17032BCDF26753F5D158491ECE4D21C50FA445251FF1C6B11C5102AAD66CC9ECF6202997")),
            DjvuJb2Tests.Chunk("TXTa", [0, 0, 3, (byte)'a', (byte)'b', (byte)'c', 2]));
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(DjvuDocument.Open(data).ConvertToPdf(notices));

        Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Single(new PdfPageContentReader(pdf).ReadImagePlacements(0));
        var notice = Assert.Single(notices);
        Assert.Equal("TextNotConverted", notice.Code);
        Assert.Equal(0, notice.PageIndex);
    }
}
