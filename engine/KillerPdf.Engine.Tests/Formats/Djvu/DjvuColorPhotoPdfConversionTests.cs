using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// Convert to PDF keeps a color photo page (one color IW44 image, no mask) as one RGB image at its stored resolution.
// The converted page, drawn by KillerPDF's own PDF renderer at one PDF pixel per DjVu pixel, must match the native
// render of the DjVu page, which DjvuIw44RealPageTests pins to ddjvu's bytes.
public sealed class DjvuColorPhotoPdfConversionTests
{
    [Fact]
    public void PhotoConversionUsesTheRequestedEncoderAndPropagatesItsCancellation()
    {
        var document = DjvuDocument.Open(Convert.FromBase64String(DjvuIw44RealPageTests.OwnedC44Color));
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var options = new DjvuPdfConversionOptions
        {
            JpegQuality = 99,
            JpegEncoder = (samples, width, height, components, quality) =>
            {
                calls++;
                Assert.Equal((211, 149, 3, 99), (width, height, components, quality));
                Assert.Equal(211 * 149 * 3, samples.Length);
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        var error = Assert.Throws<OperationCanceledException>(() =>
            document.ConvertToPdf(options, new List<DjvuDiagnostic>(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, calls);
    }

    // KP-J2's owned c44 page (211 x 149 at 100 dpi, three chunks, full chroma delayed 10 slices) in every orientation,
    // and at two other stored gammas. Every channel is within one eight-bit level of the native page: the PDF holds
    // the eight-bit sRGB values of the page's linear colors and the renderer draws eight-bit sRGB, so each value goes
    // through one eight-bit sRGB rounding that the native float frame does not.
    [Theory]
    [InlineData((byte)1, (byte)22)]
    [InlineData((byte)2, (byte)22)]
    [InlineData((byte)5, (byte)22)]
    [InlineData((byte)6, (byte)22)]
    [InlineData((byte)1, (byte)10)]
    [InlineData((byte)1, (byte)18)]
    public void AColorPhotoPageConvertsToOneImageThatDrawsLikeTheNativePage(byte orientation, byte gamma)
    {
        var data = Convert.FromBase64String(DjvuIw44RealPageTests.OwnedC44Color);
        data[33] = orientation;
        data[32] = gamma;
        bool quarter = orientation is 5 or 6;
        int width = quarter ? 149 : 211, height = quarter ? 211 : 149;
        var notices = new List<DjvuDiagnostic>();
        var document = DjvuDocument.Open(data);

        var pdf = PdfDocument.Open(document.ConvertToPdf(notices));

        Assert.Empty(notices);
        var image = Assert.Single(new PdfPageContentReader(pdf).ReadImagePlacements(0));
        Assert.Equal((211, 149), (image.PixelWidth, image.PixelHeight));
        var native = document.RenderPage(0).Pixels.ToArray();
        var page = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(width, height));
        Assert.Empty(page.Diagnostics);
        Assert.Equal((width, height), (page.Width, page.Height));
        var drawn = page.Pixels.ToArray();
        double worst = 0;
        for (int p = 0; p < width * height; p++)
            for (int c = 0; c < 3; c++)
                worst = Math.Max(worst, Math.Abs(Srgb(native[p * 4 + c]) - drawn[p * 4 + 2 - c]));
        Assert.True(worst <= 1, $"a channel is {worst:0.00} eight-bit levels from the native page");
    }

    // A page larger than a whole-page float frame allows (128 MiB, 8,388,608 pixels; 3000 x 3000 is 9,000,000) converts
    // at its stored resolution: the conversion never reduces the photo the way a fitted view does. 3000 pixels at
    // 600 dpi is 5 inches, 360 points. The real 600 dpi letter page (5100 x 6600) is measured outside the suite.
    [Fact]
    public void AColorPhotoPageOverTheFrameLimitConvertsAtItsStoredResolution()
    {
        var document = DjvuDocument.Open(ColorPage(3000, 3000, 6u));

        var pdf = PdfDocument.Open(document.ConvertToPdf());

        var image = Assert.Single(new PdfPageContentReader(pdf).ReadImagePlacements(0));
        Assert.Equal((3000, 3000), (image.PixelWidth, image.PixelHeight));
        var size = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Equal((360d, 360d), (Math.Round(size.Width, 6), Math.Round(size.Height, 6)));
    }

    private static double Srgb(float linear)
    {
        double v = Math.Clamp(linear, 0f, 1f);
        return 255 * (v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);
    }

    private static byte[] ColorPage(int width, int height, uint seed)
    {
        var chunks = DjvuIw44RealPageTests.CodedChunks(color: true, width, height, 0x8a, seed, payloadDivisor: 2);
        byte[] info = [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 24, 0, 0x58, 0x02, 22, 1];
        var form = new List<byte>("DJVU"u8.ToArray());
        form.AddRange(Chunk("INFO", info));
        foreach (var chunk in chunks) form.AddRange(Chunk("BG44", chunk.ToArray()));
        return [.. "AT&T"u8, .. Chunk("FORM", [.. form])];
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        int length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8),
            (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
