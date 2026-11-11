using System.Text;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// A composed-ink proof holds 44 bytes a pixel (byte ink 4, alpha 4, spot base plate 4, float frame 32),
// and every named colorant the page paints adds a plate of 4 bytes a pixel. The frame is refused, as the
// float path refuses a frame that does not fit, before a plate beyond PixelByteLimit is rented.
public sealed class PdfPrecisionComposedInkBudgetTests
{
    private const int Side = 500;
    private const long FramePixels = Side * Side;
    // 11,000,000 bytes of frame storage plus 4,000,000 bytes for each of four plates.
    private const long Limit = FramePixels * 44 + 4 * FramePixels * 4;

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(8, false)]
    public void EveryNamedColorantPlateCountsAgainstTheComposedProofStorageLimit(int colorants, bool fits)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(colorants)));
        var options = new PdfRenderOptions(Side, Side, maximumPixelBytes: Limit)
        {
            Proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true),
                new PdfProofOptions { RenderingIntent = KillerPdf.Engine.Authoring.PdfRenderingIntent.RelativeColorimetric,
                    BlackPointCompensation = false })
        };
        if (fits) Assert.NotNull(renderer.RenderFrame(0, options));
        else Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, options));
    }

    private static byte[] Page(int colorants)
    {
        var spaces = new StringBuilder("/ColorSpace << ");
        var content = new StringBuilder("/O gs ");
        for (int index = 0; index < colorants; index++)
        {
            spaces.Append($"/S{index} [/Separation /Ink{index} /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 .{index + 1} 1 0] /N 1 >>] ");
            content.Append($"/S{index} cs 1 scn {index * 10} 0 10 {Side} re f ");
        }
        spaces.Append(">>");
        return TestIccProfiles.Pdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Side} {Side}] /Resources << /ExtGState << /O << /op true /OP true /OPM 1 >> >> {spaces} >> /Contents 4 0 R >>",
            TestIccProfiles.Stream("", Encoding.ASCII.GetBytes(content.ToString()))]);
    }
}
