using System.Text;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// A composed-ink proof converts RGB paint and ICC-tagged CMYK paint to ink through the document's output
// intent. A page transparency group must not change that: the same artwork proofs to the same pixels
// with and without the group.
public sealed class PdfPrecisionComposedInkProfileTests
{
    private const string Over = "/ExtGState << /O << /op true /OP true /OPM 1 >> >>";

    [Theory]
    [InlineData("0.8 0.3 0.2 rg 0 0 2 2 re f")]
    [InlineData("0.1 g 0 0 2 2 re f")]
    [InlineData("/Icc cs 0 0 0 1 sc 0 0 2 2 re f")]
    public void AnOutputIntentConvertsRgbAndTaggedPaintTheSameWithAndWithoutAPageGroup(string fill)
    {
        PdfRenderedFrame plain = Proof(Page(fill, group: false));
        PdfRenderedFrame grouped = Proof(Page(fill, group: true));
        var differences = new List<string>();
        for (int pixel = 0; pixel < plain.Pixels.Length / 4; pixel++)
            for (int channel = 0; channel < 4; channel++)
            {
                float expected = plain.Pixels[pixel * 4 + channel], actual = grouped.Pixels[pixel * 4 + channel];
                if (Math.Abs(expected - actual) > .004f)
                    differences.Add($"pixel {pixel} channel {channel}: {expected} without a group, {actual} with one");
            }
        Assert.True(differences.Count == 0, string.Join("; ", differences));
    }

    private static PdfRenderedFrame Proof(byte[] file) => new PdfPageRenderer(PdfDocument.Open(file)).RenderFrame(0,
        new PdfRenderOptions(4, 2)
        {
            Proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true),
                new PdfProofOptions { RenderingIntent = KillerPdf.Engine.Authoring.PdfRenderingIntent.RelativeColorimetric,
                    BlackPointCompensation = false })
        });

    // The overprinted cyan rectangle forces the composed-ink route.
    private static byte[] Page(string fill, bool group)
    {
        using var stream = typeof(PdfCmykViewingDefault).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.ColorProfiles.CGATS001Compat-v2-micro.icc")!;
        var profile = new byte[stream.Length];
        stream.ReadExactly(profile);
        string groupEntry = group ? "/Group << /S /Transparency /CS /DeviceRGB >> " : "";
        return TestIccProfiles.Pdf([
            "<< /Type /Catalog /Pages 2 0 R /OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (Test) /DestOutputProfile 5 0 R >>] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 4 2] {groupEntry}/Resources << {Over} /ColorSpace << /Icc [/ICCBased 5 0 R] >> >> /Contents 4 0 R >>",
            TestIccProfiles.Stream("", Encoding.ASCII.GetBytes(fill + " /O gs 1 0 0 0 k 2 0 2 2 re f")),
            TestIccProfiles.Stream("/N 4", profile)]);
    }
}
