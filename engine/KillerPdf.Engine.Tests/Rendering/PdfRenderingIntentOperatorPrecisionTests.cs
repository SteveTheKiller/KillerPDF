using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRenderingIntentOperatorPrecisionTests
{
    [Fact]
    public void RelativeIntentOperatorRetainsLiteralDeviceRgbPaint()
    {
        byte[] bytes = Document("/RelativeColorimetric ri 1 0 0 rg 0 0 1 1 re f");
        byte[] original = bytes.ToArray();

        var frame = new PdfPageRenderer(PdfDocument.Open(bytes)).RenderFrame(0, Options());

        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.ToArray());
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("Perceptual")]
    [InlineData("RelativeColorimetric")]
    [InlineData("Saturation")]
    [InlineData("AbsoluteColorimetric")]
    public void StandardIntentNamesKeepLiteralFullAndRegionDevicePixels(string intent)
    {
        byte[] bytes = Document($"/{intent} ri 1 0 0 rg 0 0 1 1 re f 0 1 0 rg 1 0 1 1 re f", 2);
        byte[] original = bytes.ToArray();
        var renderer = new PdfPageRenderer(PdfDocument.Open(bytes));

        Assert.Equal(new float[] { 1, 0, 0, 1, 0, 1, 0, 1 }, renderer.RenderFrame(0, Options(2)).Pixels.ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, renderer.RenderFrameRegion(0, Options(2), 1, 0, 1, 1).Pixels.ToArray());
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void RestoreReturnsToRelativeIntentBeforeSupportedIccImagePaint()
    {
        var renderer = PdfPrecisionIccImageTests.Renderer(Convert.FromBase64String(PdfPrecisionIccImageTests.RgbProfileBase64),
            content: "/RelativeColorimetric ri q /Perceptual ri 0 1 0 rg 0 0 2 1 re f Q 2 0 0 1 0 0 cm /Image Do");

        var frame = renderer.RenderFrame(0, Options(2));

        float[] expected = [1.51082417f, -.47186967f, .03469589f, 1, 1.51087027f, -.47188407f, .03469695f, 1];
        for (int channel = 0; channel < expected.Length; channel++)
            Assert.InRange(frame.Pixels[channel], expected[channel] - .000002f, expected[channel] + .000002f);
        Assert.Equal(frame.Pixels[4..].ToArray(), renderer.RenderFrameRegion(0, Options(2), 1, 0, 1, 1).Pixels.ToArray());
    }

    [Theory]
    [InlineData("Perceptual")]
    [InlineData("Saturation")]
    [InlineData("AbsoluteColorimetric")]
    public void IntentOperatorDoesNotExpandUnsupportedIccImageIntents(string intent)
    {
        var renderer = PdfPrecisionIccImageTests.Renderer(Convert.FromBase64String(PdfPrecisionIccImageTests.RgbProfileBase64),
            content: $"/{intent} ri 2 0 0 1 0 0 cm /Image Do");

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, Options(2)));
    }

    [Fact]
    public void UnknownIntentIsRefusedPreciselyWhileCompatibilityKeepsItsFallback()
    {
        byte[] bytes = Document("/UnknownIntent ri 1 0 0 rg 0 0 1 1 re f");
        byte[] original = bytes.ToArray();
        var renderer = new PdfPageRenderer(PdfDocument.Open(bytes));
        var compatibility = renderer.Render(0, Options());
        Assert.Empty(compatibility.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, compatibility.Pixels.ToArray());

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, Options()));

        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("ri")]
    [InlineData("1 ri")]
    [InlineData("(RelativeColorimetric) ri")]
    [InlineData("/Perceptual /RelativeColorimetric ri")]
    public void MalformedIntentOperandsKeepCompatibilityDiagnosisAndAreRefusedPrecisely(string operation)
    {
        byte[] bytes = Document($"{operation} 1 0 0 rg 0 0 1 1 re f");
        byte[] original = bytes.ToArray();
        var renderer = new PdfPageRenderer(PdfDocument.Open(bytes));
        var compatibility = renderer.Render(0, Options());
        Assert.Contains("Rendering operator ri is not implemented.", compatibility.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, compatibility.Pixels.ToArray());

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, Options()));

        Assert.Equal(compatibility.Pixels.ToArray(), renderer.Render(0, Options()).Pixels.ToArray());
        Assert.Equal(original, bytes);
    }

    private static byte[] Document(string content, int width = 1) =>
        new PdfDocumentBuilder().AddPage(width, 1, Encoding.ASCII.GetBytes(content)).Build();
    private static PdfRenderOptions Options(int width = 1) => new(width, 1, transparentBackground: true);
}
