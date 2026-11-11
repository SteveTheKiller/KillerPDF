using System.Text;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Overprint pages beyond plain process path paints compose their inks before precise color conversion.
// Each 4 x 2 pt page renders to four flat columns. The expected inks are Ghostscript 10.08 bitcmyk
// renders of the same files at 72 dpi: -dOverprint=/simulate for pages with a spot colorant (a CMYK
// device has no spot plate) and -dOverprint=/enable for process-only pages. The expected appearance is
// the high-precision render of a page that paints those inks directly, without overprint.
public sealed class PdfPrecisionOverprintInkTests
{
    private const string Over = "/ExtGState << /O1 << /op true /OP true /OPM 1 >> /O0 << /op true /OP true /OPM 0 >> >>";
    private const string Spot = "/ColorSpace << /Spot [/Separation /KillerOrange /DeviceCMYK << /FunctionType 2 /Domain [0 1] "
        + "/C0 [0 0 0 0] /C1 [0 .6 1 0] /N 1 >>] >>";

    public static TheoryData<string, int[]> Cases => new()
    {
        { "spot-over-process", [255, 153, 255, 0, 255, 153, 255, 0, 0, 153, 255, 0, 0, 153, 255, 0] },
        { "spot-half-opm0", [0, 76, 127, 102, 0, 76, 127, 102, 0, 76, 127, 0, 0, 76, 127, 0] },
        { "image-under-opm1", [255, 255, 0, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255, 0, 0] },
        { "spot-image-over-cyan", [255, 153, 255, 0, 255, 76, 128, 0, 255, 0, 0, 0, 255, 153, 255, 0] },
        { "shading-opm1", [0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0] },
        { "form-opm1", [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0] },
        { "group-spot", [255, 153, 255, 0, 255, 153, 255, 0, 255, 0, 0, 0, 255, 0, 0, 0] },
        { "knockout-group", [255, 255, 0, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void OverprintedInksMatchTheReferenceCompositionThroughThePreciseConversion(string name, int[] inks)
    {
        PdfRenderOptions options = Proofed();
        PdfRenderedFrame actual = new PdfPageRenderer(PdfDocument.Open(Fixture(name))).RenderFrame(0, options);
        PdfRenderedFrame expected = new PdfPageRenderer(PdfDocument.Open(Reference(inks))).RenderFrame(0, options);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
                for (int channel = 0; channel < 4; channel++)
                {
                    int offset = (y * 4 + x) * 4 + channel;
                    Assert.True(Math.Abs(actual.Pixels[offset] - expected.Pixels[offset]) <= .008f,
                        $"{name} column {x} row {y} channel {channel}: {actual.Pixels[offset]} against {expected.Pixels[offset]}");
                }
        Assert.Equal(expected.AssumedCmykProfileSha256, actual.AssumedCmykProfileSha256);
    }

    [Fact]
    public void ComposedInkPagesSupportRegionsAndGamutWarnings()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Fixture("spot-over-process")));
        PdfRenderOptions options = Proofed(gamutWarnings: true);
        PdfRenderedFrame full = renderer.RenderFrame(0, options);
        PdfRenderedFrame region = renderer.RenderFrameRegion(0, options, 1, 0, 2, 1);
        for (int index = 0; index < 8; index++) Assert.Equal(full.Pixels[4 + index], region.Pixels[index]);
        Assert.NotNull(full.PrinterGamutWarnings);
    }

    [Fact]
    public void ComposedInkPagesHonorCancellation()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Fixture("group-spot")));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => renderer.RenderFrame(0, Proofed(), canceled.Token));
    }

    // Ink composition is a proof: plain color-managed viewing keeps refusing these pages, and the
    // viewer falls back to the byte render as it does for other content the float path cannot draw.
    [Fact]
    public void UnproofedViewingOfComposedInkPagesIsStillRefused()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Fixture("spot-over-process")));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(4, 2)));
    }

    private static PdfRenderOptions Proofed(bool gamutWarnings = false) => new PdfRenderOptions(4, 2)
    {
        Proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true),
            new PdfProofOptions { RenderingIntent = KillerPdf.Engine.Authoring.PdfRenderingIntent.RelativeColorimetric,
                BlackPointCompensation = false }, gamutWarnings: gamutWarnings)
    };

    // A qualified process overprint page that paints the composed inks on white: the native plate
    // path, which already supports proofing, converts exactly these inks.
    private static byte[] Reference(int[] inks)
    {
        var content = new StringBuilder("/O1 gs ");
        for (int x = 0; x < 4; x++)
            content.Append(FormattableString.Invariant(
                $"{inks[x * 4] / 255d:0.######} {inks[x * 4 + 1] / 255d:0.######} {inks[x * 4 + 2] / 255d:0.######} {inks[x * 4 + 3] / 255d:0.######} k {x} 0 1 2 re f "));
        return Page(Over, content.ToString());
    }

    private static byte[] Fixture(string name) => name switch
    {
        "spot-over-process" => Page(Over + " " + Spot, "1 0 0 0 k 0 0 2 2 re f /O1 gs /Spot cs 1 scn 0 0 4 2 re f"),
        "spot-half-opm0" => Page(Over + " " + Spot, "0 0 0 .4 k 0 0 2 2 re f /O0 gs /Spot cs .5 scn 0 0 4 2 re f"),
        "image-under-opm1" => Page(Over + " /XObject << /Im 5 0 R >>",
            "q 4 0 0 2 0 0 cm /Im Do Q /O1 gs 0 1 0 0 k 0 0 4 2 re f",
            TestIccProfiles.Stream("/Type /XObject /Subtype /Image /Width 4 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8",
                [255, 0, 0, 0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 0, 0])),
        "spot-image-over-cyan" => Page(Over + " " + Spot + " /XObject << /Im 5 0 R >>",
            "1 0 0 0 k 0 0 4 2 re f /O1 gs q 4 0 0 2 0 0 cm /Im Do Q",
            TestIccProfiles.Stream("/Type /XObject /Subtype /Image /Width 4 /Height 1 /ColorSpace /Spot /BitsPerComponent 8",
                [255, 128, 0, 255])),
        "shading-opm1" => Page(Over + " /Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [0 0 4 0] "
            + "/Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 1 0] /C1 [0 0 1 0] /N 1 >> >> >>",
            "1 0 0 0 k 0 0 4 2 re f /O1 gs /Sh sh"),
        "form-opm1" => Page("/XObject << /Fm 5 0 R >>", "1 0 0 0 k 0 0 4 2 re f /Fm Do",
            TestIccProfiles.Stream("/Type /XObject /Subtype /Form /BBox [0 0 4 2] /Resources << " + Over + " >>",
                Encoding.ASCII.GetBytes("/O1 gs 0 1 0 0 k 2 0 2 2 re f"))),
        "group-spot" => Page("/XObject << /Fm 5 0 R >>", "1 0 0 0 k 0 0 4 2 re f /Fm Do",
            TestIccProfiles.Stream("/Type /XObject /Subtype /Form /BBox [0 0 4 2] /Group << /S /Transparency /CS /DeviceCMYK >> "
                + "/Resources << " + Over + " " + Spot + " >>", Encoding.ASCII.GetBytes("/O1 gs /Spot cs 1 scn 0 0 2 2 re f"))),
        "knockout-group" => Page("/XObject << /Fm 5 0 R >>", "1 0 0 0 k 0 0 4 2 re f /Fm Do",
            TestIccProfiles.Stream("/Type /XObject /Subtype /Form /BBox [0 0 4 2] /Group << /S /Transparency /CS /DeviceCMYK /K true >> "
                + "/Resources << " + Over + " >>", Encoding.ASCII.GetBytes("/O1 gs 0 1 0 0 k 0 0 3 2 re f 0 0 1 0 k 1 0 3 2 re f"))),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static byte[] Page(string resources, string content, params string[] extra) => TestIccProfiles.Pdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 4 2] /Resources << {resources} >> /Contents 4 0 R >>",
        TestIccProfiles.Stream("", Encoding.ASCII.GetBytes(content)),
        .. extra]);
}
