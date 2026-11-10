using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// Convert to PDF keeps a compound page's layers: the background as an image at its own resolution and the
// foreground as the page's bitonal mask painted in its colors, so text stays as sharp as the scan. The converted page,
// drawn by KillerPDF's own PDF renderer, must match what ddjvu draws for the DjVu page (DjvuCompoundTests' oracles).
public sealed class DjvuCompoundPdfConversionTests
{
    [Fact]
    public void ACompoundPageConvertsWithEachShapeInItsPaletteColorOverTheBackground()
    {
        var pdf = ConvertPage(Convert.FromHexString(DjvuCompoundTests.PaletteOverGrayHex));

        // 96 x 64 at 300 dpi is 23.04 x 15.36 points; drawn at 300 dpi, one PDF pixel per DjVu pixel. Every pixel is
        // within one eight-bit level of ddjvu's (the page's linear values through an eight-bit sRGB round trip).
        DjvuCompoundTests.AssertColors(DjvuCompoundTests.AuthoredColors(), Render(pdf, 96, 64), 1);
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)5)]
    [InlineData((byte)6)]
    public void AConvertedCompoundPageTurnsWithItsOrientation(byte orientation)
    {
        var data = Convert.FromHexString(DjvuCompoundTests.PaletteOverGrayHex);
        data[33] = orientation;
        bool quarter = orientation is 5 or 6;

        var pdf = ConvertPage(data);

        DjvuCompoundTests.AssertColors(DjvuCompoundTests.Turned(DjvuCompoundTests.AuthoredColors(), orientation),
            Render(pdf, quarter ? 64 : 96, quarter ? 96 : 64), 1);
    }

    [Fact]
    public void AnFg44ForegroundConvertsAsItsImageThroughTheMask()
    {
        // ddjvu: the left blocks (199, 41, 34), the right blocks (0, 67, 242), the rest white (DjvuCompoundTests).
        var pdf = ConvertPage(Convert.FromHexString(DjvuCompoundTests.ImageForegroundHex));

        var pixels = Render(pdf, 384, 384).Pixels.ToArray();
        void At(int x, int y, int r, int g, int b)
        {
            int o = (y * 384 + x) * 4;
            Assert.Equal(r, Level(pixels[o]), 2.0);
            Assert.Equal(g, Level(pixels[o + 1]), 2.0);
            Assert.Equal(b, Level(pixels[o + 2]), 2.0);
        }
        At(60, 80, 199, 41, 34);
        At(60, 300, 199, 41, 34);
        At(300, 80, 0, 67, 242);
        At(350, 330, 0, 67, 242);
        At(200, 200, 255, 255, 255);
        At(10, 10, 255, 255, 255);
    }

    [Fact]
    public void A600DpiCompoundPageConvertsWithItsBackgroundAtItsOwnResolutionAndOneBitMasks()
    {
        // The 5100 x 6600 page: a 1700 x 2200 gray background (subsample 3) and a black and a red shape group.
        var data = Convert.FromHexString(DjvuCompoundTests.Compound600DpiHex);

        var bytes = DjvuDocument.Open(data).ConvertToPdf();
        var pdf = PdfDocument.Open(bytes);

        var images = new PdfPageContentReader(pdf).ReadImagePlacements(0);
        // The background at its stored 1700 x 2200, never the 5100 x 6600 page; no image is a page-size color image.
        Assert.Contains(images, image => (image.PixelWidth, image.PixelHeight) == (1700, 2200));
        Assert.DoesNotContain(images, image => image.PixelWidth >= 5100 && image.PixelHeight >= 6600);
        // A 33.7 M pixel page: an RGB page image would be megabytes even compressed; layers keep it small.
        Assert.True(bytes.Length < 256 * 1024, $"{bytes.Length} bytes");
        // The red glyph's solid row at (2100, 304) and the paper beside the black glyph, drawn at quarter size.
        var frame = Render(pdf, 1275, 1650).Pixels.ToArray();
        int red = (76 * 1275 + 525) * 4, paper = (76 * 1275 + 70) * 4;
        Assert.Equal(127, Level(frame[paper]), 2.0);
        Assert.True(Level(frame[red]) > Level(frame[red + 1]) + 60, "the red glyph is red");
    }

    private static PdfDocument ConvertPage(byte[] data)
    {
        var notices = new List<DjvuDiagnostic>();
        var pdf = PdfDocument.Open(DjvuDocument.Open(data).ConvertToPdf(notices));
        Assert.Empty(notices);
        return pdf;
    }

    // The viewer's PDF path: eight-bit sRGB BGRA, read back as linear channels so DjvuCompoundTests' level checks apply.
    private static PdfRenderedFrame Render(PdfDocument pdf, int width, int height)
    {
        var page = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(width, height));
        Assert.Empty(page.Diagnostics);
        var bgra = page.Pixels.ToArray();
        var linear = new float[bgra.Length];
        static float Decode(byte value)
        {
            double v = value / 255d;
            return (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
        }
        for (int i = 0; i < bgra.Length; i += 4)
        {
            linear[i] = Decode(bgra[i + 2]);
            linear[i + 1] = Decode(bgra[i + 1]);
            linear[i + 2] = Decode(bgra[i]);
            linear[i + 3] = bgra[i + 3] / 255f;
        }
        return new PdfRenderedFrame(page.Width, page.Height, linear, PdfFrameColorEncoding.LinearSrgbD65);
    }

    private static double Level(float linear) => Math.Pow(linear, 1 / 2.2) * 255;
}
