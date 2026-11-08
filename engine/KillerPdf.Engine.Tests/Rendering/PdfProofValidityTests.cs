using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfProofValidityTests
{
    // A zero X component makes the declared media white invalid while every colorimetric
    // transform of this sRGB-primaries matrix printer stays usable.
    private static readonly int[] InvalidWhite = [0, 65536, 54061];

    [Fact]
    public void AMalformedMediaWhiteStillAllowsARelativeColorimetricProof()
    {
        var printer = PdfColorProfile.Load(TestIccProfiles.MatrixRgb("prtr", white: InvalidWhite));

        PdfRenderedFrame frame = new PdfPageRenderer(PdfDocument.Open(RedPage()))
            .RenderFrame(0, Options(printer, PdfRenderingIntent.RelativeColorimetric));

        // Identity curves and sRGB primaries reproduce source red within s15Fixed16 rounding.
        Assert.InRange(frame.Pixels[0], 1 - 1e-3, 1 + 1e-3);
        Assert.InRange(frame.Pixels[1], -1e-3, 1e-3);
        Assert.InRange(frame.Pixels[2], -1e-3, 1e-3);
    }

    [Fact]
    public void AnAbsoluteProofWithAMalformedMediaWhiteNamesTheInvalidWhitePoint()
    {
        var printer = PdfColorProfile.Load(TestIccProfiles.MatrixRgb("prtr", white: InvalidWhite));

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(PdfDocument.Open(RedPage()))
            .RenderFrame(0, Options(printer, PdfRenderingIntent.AbsoluteColorimetric)));

        Assert.Contains("media-white point is invalid", error.Message, StringComparison.Ordinal);
    }

    private static byte[] RedPage() =>
        new PdfDocumentBuilder().AddPage(1, 1, "1 0 0 rg 0 0 1 1 re f"u8.ToArray()).Build();

    private static PdfRenderOptions Options(PdfColorProfile printer, PdfRenderingIntent intent) => new(1, 1)
    {
        Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: printer,
            options: new PdfProofOptions { RenderingIntent = intent, BlackPointCompensation = false })
    };
}
