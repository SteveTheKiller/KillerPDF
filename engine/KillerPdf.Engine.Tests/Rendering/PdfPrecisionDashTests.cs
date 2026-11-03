using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionDashTests
{
    [Fact]
    public void DashedStrokesRetainSubByteSourceColorsInLinearFrame()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 2, Encoding.ASCII.GetBytes(
                "0 w [1 3] 0 d 0.5 G 0 1.5 m 8 1.5 l S "
                + "0.5001 G 0 0.5 m 8 0.5 l S"))
            .Build()));

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(8, 2));

        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertGray(frame, 0, 0, 0.21404113f, 0.21404116f);
        AssertGray(frame, 4, 0, 0.21404113f, 0.21404116f);
        AssertWhite(frame, 2, 0);
        AssertWhite(frame, 6, 0);
        AssertGray(frame, 0, 1, 0.21413370f, 0.21413373f);
        AssertGray(frame, 4, 1, 0.21413370f, 0.21413373f);
        AssertWhite(frame, 2, 1);
        AssertWhite(frame, 6, 1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public void DashPhaseRetainsPreciseColorAndMovesThePaintedIntervals(int phase)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, Encoding.ASCII.GetBytes(
                $"0 w [1 3] {phase} d 0.5001 G 0 0.5 m 8 0.5 l S"))
            .Build()));

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(8, 1));

        AssertWhite(frame, 0, 0);
        AssertGray(frame, 2, 0, 0.21413370f, 0.21413373f);
        AssertWhite(frame, 4, 0);
        AssertGray(frame, 6, 0, 0.21413370f, 0.21413373f);
    }

    [Fact]
    public void TransformedDashesScaleTheirLengthsWithoutQuantizingColor()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, "2 0 0 1 0 0 cm 0 w [1 1] 0 d 0.5001 G 0 0.5 m 4 0.5 l S"u8.ToArray())
            .Build()));

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(8, 1));

        foreach (int x in new[] { 0, 1, 4, 5 })
            AssertGray(frame, x, 0, 0.21413370f, 0.21413373f);
        foreach (int x in new[] { 2, 3, 6, 7 })
            AssertWhite(frame, x, 0);
    }

    [Theory]
    [InlineData("[0 0]")]
    [InlineData("[-1 1]")]
    public void InvalidDashPatternsStillRejectInsteadOfReturningAFrame(string pattern)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, Encoding.ASCII.GetBytes($"0 w {pattern} 0 d 0 0.5 m 8 0.5 l S"))
            .Build()));

        var error = Assert.Throws<FormatException>(() => renderer.RenderFrame(0, new PdfRenderOptions(8, 1)));

        Assert.Contains("dash pattern is invalid", error.Message);
    }

    [Fact]
    public void DenseDashRecoveryDoesNotSilentlyReturnASolidPrecisionFrame()
    {
        var renderer = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(new PdfDocumentBuilder()
            .AddPage(8, 1, "[0.0000000000001 0.0000000000001] 0 d 0 w 0 0.5 m 8 0.5 l S"u8.ToArray())
            .Build()));

        var error = Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(8, 1)));

        Assert.Contains("line-dash pattern exceeded the expansion limit", error.Message);
    }

    private static void AssertGray(PdfRenderedFrame frame, int x, int y, float minimum, float maximum)
    {
        int offset = (y * frame.Width + x) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], minimum, maximum);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    private static void AssertWhite(PdfRenderedFrame frame, int x, int y)
    {
        int offset = (y * frame.Width + x) * 4;
        Assert.Equal(1f, frame.Pixels[offset]);
        Assert.Equal(1f, frame.Pixels[offset + 1]);
        Assert.Equal(1f, frame.Pixels[offset + 2]);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }
}
