using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccProfileClassTests
{
    // The red column holds the sRGB green primary, so a used profile paints component red as green.
    private static readonly int[][] SwappedColumns =
        [TestIccProfiles.SrgbColumns[1], TestIccProfiles.SrgbColumns[0], TestIccProfiles.SrgbColumns[2]];

    [Theory]
    [InlineData("abst")]
    [InlineData("link")]
    [InlineData("nmcl")]
    public void AnIccBasedSourceOutsideTheSourceClassesFallsBackToItsAlternateVisibly(string profileClass)
    {
        // ISO 32000 ICCBased spaces take input, display, output or color-space profiles only.
        byte[] pdf = TestIccProfiles.IccRgbPage(TestIccProfiles.MatrixRgb(profileClass, SwappedColumns),
            "/CS0 cs 1 0 0 sc 0 0 1 1 re f");

        PdfRenderedPage page = new PdfPageRenderer(PdfDocument.Open(pdf)).Render(0, new PdfRenderOptions(1, 1));

        byte[] pixel = page.Pixels[..4].ToArray();
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixel);
        Assert.Contains("A source ICC profile could not be used; its alternate color space was used.", page.Diagnostics);
    }

    [Fact]
    public void ADisplayClassIccBasedSourceIsUsed()
    {
        byte[] pdf = TestIccProfiles.IccRgbPage(TestIccProfiles.MatrixRgb("mntr", SwappedColumns),
            "/CS0 cs 1 0 0 sc 0 0 1 1 re f");

        PdfRenderedPage page = new PdfPageRenderer(PdfDocument.Open(pdf)).Render(0, new PdfRenderOptions(1, 1));

        // The green primary converts back to sRGB green within s15Fixed16 rounding.
        ReadOnlySpan<byte> pixel = page.Pixels.Span[..4];
        Assert.InRange(pixel[0], 0, 3);
        Assert.InRange(pixel[1], 252, 255);
        Assert.InRange(pixel[2], 0, 3);
        Assert.Empty(page.Diagnostics);
    }
}
