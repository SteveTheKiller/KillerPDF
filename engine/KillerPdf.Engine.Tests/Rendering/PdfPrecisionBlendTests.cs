using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionBlendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScreenKeepsDistinctColorChannelsAndFractionalBackdropAlpha(bool fractionalBackdrop)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder()
                .SetGraphicsState(new PdfGraphicsState(fillOpacity: fractionalBackdrop ? .25 : 1))
                .SetFillRgb(.25, .5, .125).Rectangle(0, 0, 1, 1).Fill()
                .SetGraphicsState(new PdfGraphicsState(fillOpacity: .5, blendMode: PdfBlendMode.Screen))
                .SetFillRgb(.5, .25, .75).Rectangle(0, 0, 1, 1).Fill()).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(1, 1, transparentBackground: fractionalBackdrop));

        // Opaque encoded RGB [7/16,9/16,29/64]; fractional [19/40,3/8,101/160].
        float[] expected = fractionalBackdrop ? [.1916267720252291f, .1160161342327660f, .3562474421312805f]
            : [.1606826777184872f, .2765176169956916f, .1731902535125494f];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], expected[channel] - .00000006f, expected[channel] + .00000006f);
        Assert.Equal(fractionalBackdrop ? .625f : 1, frame.Pixels[3]);
    }

    [Fact]
    public void ScreenBlendsEncodedRgbBeforeFinalFrameDecoding()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(.25, .25, .25).Rectangle(0, 0, 1, 1).Fill()
                .SetGraphicsState(new PdfGraphicsState(fillOpacity: .5, blendMode: PdfBlendMode.Screen))
                .SetFillRgb(.5, .5, .5).Rectangle(0, 0, 1, 1).Fill()).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(1, 1));

        // Independent result: encoded7/16, decoded with Decimal60 before implementation.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], .16068267771848722f - .00000006f,
                .16068267771848722f + .00000006f);
        Assert.Equal(1, frame.Pixels[3]);
    }
}
