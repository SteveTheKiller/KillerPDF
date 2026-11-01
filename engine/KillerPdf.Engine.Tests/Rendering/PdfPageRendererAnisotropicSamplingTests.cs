using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererAnisotropicSamplingTests
{
    [Theory]
    [InlineData(false, 1, false, 1)]
    [InlineData(false, 1, true, 1)]
    [InlineData(false, 1, false, 4)]
    [InlineData(false, 1, true, 4)]
    [InlineData(false, 3, false, 1)]
    [InlineData(false, 3, true, 1)]
    [InlineData(false, 3, false, 4)]
    [InlineData(false, 3, true, 4)]
    [InlineData(true, 1, false, 1)]
    [InlineData(true, 1, true, 1)]
    [InlineData(true, 1, false, 4)]
    [InlineData(true, 1, true, 4)]
    [InlineData(true, 3, false, 1)]
    [InlineData(true, 3, true, 1)]
    [InlineData(true, 3, false, 4)]
    [InlineData(true, 3, true, 4)]
    public void ReducingOneImageAxisPreservesThinLineCoverage(bool reduceY, int components,
        bool interpolate, int stripePhase)
    {
        int width = reduceY ? 16 : 128, height = reduceY ? 128 : 16;
        int outputWidth = reduceY ? 32 : 16, outputHeight = reduceY ? 16 : 32;
        byte[] samples = new byte[width * height * components];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int component = 0; component < components; component++)
                    samples[(y * width + x) * components + component] =
                        (reduceY ? y : x) % 8 == stripePhase ? (byte)0 : (byte)255;
        byte[] content = Encoding.ASCII.GetBytes($"q {outputWidth} 0 0 {outputHeight} 0 0 cm /I Do Q\n");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(outputWidth, outputHeight, content).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference image = update.AddObject(new PdfStream(D(
            ("Type", N("XObject")), ("Subtype", N("Image")),
            ("Width", new PdfInteger(width)), ("Height", new PdfInteger(height)),
            ("ColorSpace", N(components == 1 ? "DeviceGray" : "DeviceRGB")), ("BitsPerComponent", new PdfInteger(8)),
            ("Interpolate", new PdfBoolean(interpolate))), samples));
        var changed = new PdfDictionary(page.Dictionary.Where(entry => !entry.Key.Equals(N("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(N("Resources"), D(("XObject", D(("I", image)))))));
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber, changed).Build());
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(outputWidth, outputHeight));

        // Each output pixel covers eight samples along the reduced axis, exactly one black.
        // Retaining that one-eighth coverage gives 255 * 7/8, rounded to 223.
        int pixel = (outputHeight / 2 * outputWidth + outputWidth / 2) * 4;
        Assert.InRange(rendered.Pixels.Span[pixel], (byte)222, (byte)224);
        Assert.Equal(rendered.Pixels.Span[pixel], rendered.Pixels.Span[pixel + 1]);
        Assert.Equal(rendered.Pixels.Span[pixel], rendered.Pixels.Span[pixel + 2]);
        Assert.Empty(rendered.Diagnostics);
    }

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary D(params (string Name, PdfObject Value)[] values) =>
        new(values.Select(value => new KeyValuePair<PdfName, PdfObject>(N(value.Name), value.Value)));
}
