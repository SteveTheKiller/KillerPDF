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

    // Line caps, joins and miter limits shape the stroke both renderers rasterize, and flatness,
    // marked points and Type 3 glyph metrics paint nothing in either, so the float frame matches the
    // byte render within its rounding: the source color quantized to a level and one composite.
    [Theory]
    [InlineData("1 J 6 w 2 2 m 8 8 l S")]
    [InlineData("2 J 1 j 4 w 2 2 m 8 2 l 5 8 l S")]
    [InlineData("2 j 1.5 M 4 w 2 2 m 8 2 l 3 4 l S")]
    [InlineData("0.5 i 2 2 m 8 2 l 5 8 l f")]
    [InlineData("/Point MP /Point << /A 1 >> DP 2 2 8 6 re f")]
    public void StrokeStylesAndOperatorsThatPaintNothingDrawAsTheByteRenderDoes(string content) =>
        AssertMatchesByteRender(Page(".5001 .2001 .7001 rg .3001 .6001 .1001 RG " + content));

    // A proof converts the composited page, so line styles set by operators proof exactly as the
    // same styles set by graphics-state parameters, and operators that paint nothing as their absence.
    [Theory]
    [InlineData("1 J 2 j 3 M 6 w 2 2 m 8 2 l 5 8 l S", "/LC 1 /LJ 2 /ML 3 /LW 6", "2 2 m 8 2 l 5 8 l S")]
    [InlineData("0.5 i /Point MP /Point << /A 1 >> DP 2 2 8 6 re f", "", "2 2 8 6 re f")]
    public void AProofOfOperatorStylesIsTheProofOfTheSameStyles(string operators, string entries, string paint)
    {
        const string color = ".5001 .2001 .7001 rg .3001 .6001 .1001 RG ";
        var options = new PdfRenderOptions(40, 40) { Proof = SyntheticProof() };

        PdfRenderedFrame styled = new PdfPageRenderer(PdfDocument.Open(Page(color + operators))).RenderFrame(0, options);
        PdfRenderedFrame equivalent = new PdfPageRenderer(PdfDocument.Open(Page("/S gs " + color + paint,
            "/ExtGState << /S << /Type /ExtGState " + entries + " >> >>"))).RenderFrame(0, options);

        Assert.Equal(equivalent.Pixels.ToArray(), styled.Pixels.ToArray());
    }

    // Device-dependent parameters (stroke adjustment, smoothness, flatness, halftones, black
    // generation, undercolor removal, default transfer) and keys this renderer does not know change
    // nothing the byte renderer draws (ISO 32000-1 10 and 8.4.5); the overprint mode only matters
    // while overprint is on, which the float path still refuses.
    [Theory]
    [InlineData("/SA true")]
    [InlineData("/SM 0.02 /FL 2")]
    [InlineData("/OPM 1")]
    [InlineData("/HT /Default /HTO [0 0]")]
    [InlineData("/BG2 /Default /UCR2 /Default /BG << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>")]
    [InlineData("/TR /Identity")]
    [InlineData("/TR2 /Default /UseBlackPtComp /OFF")]
    [InlineData("/LW 3 /LC 1 /LJ 1 /ML 4 /D [[2 1] 0]")]
    [InlineData("/Name /Mine /CALS_ca 0.5 /AAPL:AA false")]
    public void GraphicsStateParametersThatChangeNothingDrawnAreAccepted(string entries) =>
        AssertMatchesByteRender(Page("/S gs .5001 .2001 .7001 rg .3001 .6001 .1001 RG 2 2 6 6 re B",
            "/ExtGState << /S << /Type /ExtGState " + entries + " >> >>"));

    // Overprint, alpha as shape, text knockout off, transfer functions and black-point
    // compensation change paint the float path cannot reproduce yet, so it still refuses them.
    [Theory]
    [InlineData("/OP true")]
    [InlineData("/op true /OPM 1")]
    [InlineData("/AIS true")]
    [InlineData("/TK false")]
    [InlineData("/TR << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 2 >>")]
    [InlineData("/UseBlackPtComp /ON")]
    public void GraphicsStateParametersThatChangePaintRemainRefused(string entries)
    {
        byte[] pdf = Page("/S gs .5001 .2001 .7001 rg 2 2 6 6 re f",
            "/ExtGState << /S << /Type /ExtGState " + entries + " >> >>");

        Assert.Throws<NotSupportedException>(() =>
            new PdfPageRenderer(PdfDocument.Open(pdf)).RenderFrame(0, new PdfRenderOptions(10, 10)));
    }

    private static void AssertMatchesByteRender(byte[] pdf)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(pdf));
        var options = new PdfRenderOptions(40, 40);
        PdfRenderedPage page = renderer.Render(0, options);
        PdfRenderedFrame frame = renderer.RenderFrame(0, options);
        Assert.Empty(page.Diagnostics);
        ReadOnlySpan<byte> bytes = page.Pixels.Span;
        int painted = 0;
        for (int pixel = 0; pixel < 40 * 40; pixel++)
        {
            if (bytes[pixel * 4] != 255) painted++;
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange(Math.Abs(Encode(frame.Pixels[pixel * 4 + channel]) * 255 - bytes[pixel * 4 + 2 - channel]), 0, 1);
        }
        Assert.True(painted > 0);
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
