using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfImageAreaSamplingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReducedAlternatingSamplesPreserveAverageColor(bool rgb, bool rotated)
    {
        byte[] values = Enumerable.Range(0, 16).SelectMany(index => rgb
            ? index % 2 == 0 ? new byte[] { 255, 0, 0 } : new byte[] { 0, 0, 255 }
            : new byte[] { index % 2 == 0 ? (byte)0 : (byte)255 }).ToArray();
        PdfImage image = rgb ? PdfImage.FromRgb(4, 4, values) : PdfImage.FromGray(4, 4, values);
        var content = new PdfContentStreamBuilder();
        if (rotated) content.Transform(0, 1, -1, 0, 2, 0);
        content.DrawImage(image, 0, 0, 2, 2);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 2, content).Build());
        var result = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 2));
        Assert.Empty(result.Diagnostics);
        for (int pixel = 0; pixel < 4; pixel++)
            Assert.Equal(new byte[] { 128, rgb ? (byte)0 : (byte)128, 128, 255 },
                result.Pixels.Slice(pixel * 4, 4).ToArray());
    }

    [Fact]
    public void ReductionUnderNonrectangularClipPreservesFineStripes()
    {
        byte[] values = Enumerable.Range(0, 256).Select(index => index % 2 == 0 ? (byte)0 : (byte)255).ToArray();
        var content = new PdfContentStreamBuilder().MoveTo(0, 0).LineTo(8, 0).LineTo(0, 8)
            .ClosePath().Clip().DrawImage(PdfImage.FromGray(16, 16, values), 0, 0, 8, 8);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8, content).Build());
        var result = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(8, 8));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, result.Pixels.Slice((6 * 8 + 1) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, result.Pixels.Slice((1 * 8 + 6) * 4, 4).ToArray());
    }

    [Fact]
    public void FractionalFootprintWeightsEdgeSamples()
    {
        // Half of each outer sample and all of the middle sample contribute.
        Assert.Equal(0x404040u, PdfImageAreaSampler.Sample([0, 128, 0], 3, 1, 1,
            1.5, 0.5, 2, 1));
        Assert.Equal(0x808080u, PdfImageAreaSampler.Sample([128], 1, 1, 1,
            0.25, 0.25, 2, 2));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public void PackedBinaryReductionMatchesByteSamples(int outputSize, bool rotated)
    {
        byte[] values = Enumerable.Range(0, 17 * 19)
            .Select(index => (index % 17 * 11 + index / 17 * 7) % 8 > 3 ? (byte)255 : (byte)0).ToArray();
        byte[] Render(PdfImage image)
        {
            var content = new PdfContentStreamBuilder();
            if (rotated) content.Transform(0, 1, -1, 0, 10, 0);
            content.MoveTo(0, 0).LineTo(10, 0).LineTo(0, 10).ClosePath().Clip()
                .DrawImage(image, 0, 0, 10, 10);
            var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10, content).Build());
            var result = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(outputSize, outputSize));
            Assert.Empty(result.Diagnostics);
            return result.Pixels.ToArray();
        }
        Assert.Equal(Render(PdfImage.FromGray(17, 19, values)), Render(PdfImage.FromBitonal(17, 19, values)));
    }
}
