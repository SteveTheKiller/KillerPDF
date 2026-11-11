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

    [Fact]
    public void AJpegEncoderFailureOnOnePhotoPageKeepsThatPageLosslessAndConvertsTheRest()
    {
        byte[] photoForm = Convert.FromBase64String(DjvuIw44RealPageTests.OwnedC44Color)[4..];
        byte[] bitonalForm = DjvuJb2Tests.Page(56, 32, 1, DjvuJb2Tests.Chunk("Sjbz", Convert.FromHexString("826C97EA17032BCDF26753F5D158491ECE4D21C50FA445251FF1C6B11C5102AAD66CC9ECF6202997")))[4..];
        var data = DjvuTestStreams.Bundle(("photo.djvu", 1, photoForm), ("text.djvu", 1, bitonalForm));
        var options = new DjvuPdfConversionOptions
        {
            JpegEncoder = (_, _, _, _, _) => throw new COMException("The image encoder failed.", unchecked((int)0x88982F50))
        };
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(DjvuDocument.Open(data).ConvertToPdf(options, notices));

        Assert.Equal(2, PdfPageInformation.Read(pdf).Count);
        var image = Assert.Single(new PdfPageContentReader(pdf).ReadImagePlacements(0));
        Assert.Equal((211, 149), (image.PixelWidth, image.PixelHeight));
        Assert.DoesNotContain(notices, n => n.Code == "PageNotConverted");
        var notice = Assert.Single(notices);
        Assert.Equal("JpegEncodingFailed", notice.Code);
        Assert.Equal(0, notice.PageIndex);
    }

    [Fact]
    public void AnOutOfMemoryFailureOfTheJpegEncoderIsNotHidden()
    {
        var document = DjvuDocument.Open(Convert.FromBase64String(DjvuIw44RealPageTests.OwnedC44Color));
        var options = new DjvuPdfConversionOptions
        {
            JpegEncoder = (_, _, _, _, _) => throw new OutOfMemoryException()
        };

        Assert.Throws<OutOfMemoryException>(() => document.ConvertToPdf(options, new List<DjvuDiagnostic>()));
    }
}
