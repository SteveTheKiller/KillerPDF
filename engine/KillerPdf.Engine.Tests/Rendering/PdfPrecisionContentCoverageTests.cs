using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Real PDF content the float renderer behind soft proofing and monitor color management draws
// instead of refusing it and leaving the page to the plain fallback.
public sealed class PdfPrecisionContentCoverageTests
{
    // Marked content only decides visibility, which the float path already honors, so a proof
    // of marked paint is the proof of the bare paint.
    [Theory]
    [InlineData("/Artifact BMC {0} EMC")]
    [InlineData("/Span << /MCID 0 >> BDC {0} EMC")]
    public void AProofOfMarkedContentIsTheProofOfItsPaint(string marked)
    {
        const string paint = ".5001 .2001 .7001 rg 0 0 10 10 re f";
        var options = new PdfRenderOptions(10, 10) { Proof = SyntheticProof() };

        PdfRenderedFrame proofed = new PdfPageRenderer(PdfDocument.Open(Page(string.Format(marked, paint))))
            .RenderFrame(0, options);
        PdfRenderedFrame bare = new PdfPageRenderer(PdfDocument.Open(Page(paint))).RenderFrame(0, options);

        Assert.Equal(bare.Pixels.ToArray(), proofed.Pixels.ToArray());
    }

    // A calibrated gray image (as Convert to PDF writes DjVu gray backgrounds) proofs as the color
    // its unproofed float frame shows: the proof of a fill of that frame's encoded color.
    [Fact]
    public void ACalibratedGrayImageProofsAsTheColorItsFloatFrameShows()
    {
        string samples = string.Concat(Enumerable.Range(0, 100).Select(i => $"{(i * 37 + 20) % 256:X2}")) + ">";
        byte[] pdf = PdfPrecisionSoftMaskTests.Pdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << /X 5 0 R >> >> /Contents 4 0 R >>",
            PdfPrecisionSoftMaskTests.Stream("q 10 0 0 10 0 0 cm /X Do Q"),
            $"<< /Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace [/CalGray << /WhitePoint [0.95047 1 1.08883] /Gamma 2.2 >>] /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length {samples.Length} >>\nstream\n{samples}\nendstream"]);
        var renderer = new PdfPageRenderer(PdfDocument.Open(pdf));
        PdfRenderedFrame plain = renderer.RenderFrame(0, new PdfRenderOptions(10, 10));

        PdfRenderedFrame proofed = renderer.RenderFrame(0, new PdfRenderOptions(10, 10) { Proof = SyntheticProof() });

        foreach (int pixel in new[] { 0, 37, 99 })
        {
            string color = string.Join(" ", Enumerable.Range(0, 3).Select(channel =>
                Encode(plain.Pixels[pixel * 4 + channel]).ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture)));
            PdfRenderedFrame direct = new PdfPageRenderer(PdfDocument.Open(Page($"{color} rg 0 0 10 10 re f")))
                .RenderFrame(0, new PdfRenderOptions(10, 10) { Proof = SyntheticProof() });
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange(proofed.Pixels[pixel * 4 + channel], direct.Pixels[channel] - .00001f, direct.Pixels[channel] + .00001f);
        }
    }

    private static double Encode(double linear) => linear <= .0031308
        ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;

    private static PdfDocumentProofSettings SyntheticProof() =>
        new(true, PdfColorProfile.Load(
            SyntheticCmykPrinter.Create()));

    private static byte[] Page(string content, string resources = "") => PdfPrecisionSoftMaskTests.Pdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << {resources} >> /Contents 4 0 R >>",
        PdfPrecisionSoftMaskTests.Stream(content)]);

}
