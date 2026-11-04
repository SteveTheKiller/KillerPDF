using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionGraphicsStateTests
{
    [Theory]
    [InlineData("fill-overprint")]
    [InlineData("stroke-overprint")]
    [InlineData("overprint-mode")]
    [InlineData("alpha-shape")]
    [InlineData("text-knockout")]
    public void ActiveUnsupportedCompositingSettingsRemainDiagnosed(string setting)
    {
        var state = new PdfGraphicsState(
            fillOverprint: setting == "fill-overprint",
            strokeOverprint: setting == "stroke-overprint",
            overprintMode: setting == "overprint-mode" ? PdfOverprintMode.One : PdfOverprintMode.Zero,
            alphaIsShape: setting == "alpha-shape",
            textKnockout: setting != "text-knockout");
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetGraphicsState(state)
                .SetFillRgb(.5001, .5001, .5001).Rectangle(0, 0, 1, 1).Fill()).Build());

        var error = Assert.Throws<NotSupportedException>(() =>
            new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(1, 1)));

        Assert.Contains("graphics state", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthoringDefaultsRetainPreciseRgbPaint()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetGraphicsState(new PdfGraphicsState(fillOpacity: .5))
                .SetFillRgb(.5001, .5001, .5001).Rectangle(0, 0, 1, 1).Fill()).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(1, 1));

        // Encoded source-over on white is .75005, independently decoded with Decimal60.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], .5225994487667747f - .00000006f,
                .5225994487667747f + .00000006f);
        Assert.Equal(1, frame.Pixels[3]);
    }
}
