using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionAxialShadingTests
{
    [Fact]
    public void AxialGrayRetainsSubByteFunctionValuesAtPixelCenters()
    {
        var renderer = Renderer();

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        // ISO 32000-1 sections 7.10.3 and 8.7.4.5.3 give encoded values
        // 0.500025 and 0.500075 here. Independent 50-digit decimal sRGB decoding
        // gives the frozen linear values below, before float32 storage.
        AssertPixel(frame, 0, 0.21406428f, 0.21406428f, 0.21406428f);
        AssertPixel(frame, 1, 0.21411057f, 0.21411057f, 0.21411057f);
        Assert.NotEqual(frame.Pixels[0], frame.Pixels[4]);
    }

    [Fact]
    public void AxialRgbRetainsEachSubByteFunctionComponent()
    {
        var renderer = Renderer("DeviceRGB", [0.25, 0.5, 0.75], [0.2501, 0.5001, 0.7501]);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        AssertPixel(frame, 0, 0.05088610f, 0.21406428f, 0.52256050f);
        AssertPixel(frame, 1, 0.05090612f, 0.21411057f, 0.52263840f);
        Assert.NotEqual(frame.Pixels[0], frame.Pixels[4]);
    }

    [Fact]
    public void AxialShadingHonorsTransformClipExtensionAndOpacity()
    {
        var renderer = Renderer(content: "1 0 4 1 re W n 1 0 0 1 2 0 cm /Half gs /Sh1 sh",
            width: 6, extend: true);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(6, 1, transparentBackground: true));

        Assert.Equal(0f, frame.Pixels[3]);
        AssertPixel(frame, 1, 0.21404114f, 0.21404114f, 0.21404114f, 0.5f);
        AssertPixel(frame, 2, 0.21406428f, 0.21406428f, 0.21406428f, 0.5f);
        AssertPixel(frame, 3, 0.21411057f, 0.21411057f, 0.21411057f, 0.5f);
        Assert.Equal(0f, frame.Pixels[4 * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[5 * 4 + 3]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void UnportedShadingTypesAreRejected(int type)
    {
        var renderer = Renderer(shadingType: type);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void UnportedShadingFunctionsAreRejected(int type)
    {
        var renderer = Renderer(functionType: type);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void ComponentFunctionArraysAreRejected()
    {
        var renderer = Renderer(functionArray: true);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void UnportedCmykShadingIsRejected()
    {
        var renderer = Renderer("DeviceCMYK", [0, 0, 0, 0.5], [0, 0, 0, 0.5001]);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void CompatibilityRenderingKeepsItsByteGradient()
    {
        var renderer = Renderer();
        var options = new PdfRenderOptions(2, 1, transparentBackground: true);

        var page = renderer.Render(0, options);

        Assert.Empty(page.Diagnostics);
        Assert.Equal(new byte[] { 128, 128, 128, 255, 128, 128, 128, 255 }, page.Pixels.ToArray());
        Assert.Equal(page.Pixels.ToArray(), renderer.RenderFrame(0, options).ToSrgbBgra32());
    }

    private static PdfPageRenderer Renderer(string colorSpace = "DeviceGray",
        double[]? c0 = null, double[]? c1 = null, string content = "/Sh1 sh",
        int width = 2, bool extend = false, int shadingType = 2, int functionType = 2,
        bool functionArray = false)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(width, 1, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var function = new PdfDictionary([
            Entry("FunctionType", new PdfInteger(functionType)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(c0 ?? [0.5])), Entry("C1", Numbers(c1 ?? [0.5001])),
            Entry("N", new PdfInteger(1))
        ]);
        var shading = new PdfDictionary([
            Entry("ShadingType", new PdfInteger(shadingType)), Entry("ColorSpace", Name(colorSpace)),
            Entry("Coords", Numbers(0, 0, 2, 0)),
            Entry("Function", functionArray ? new PdfArray([function]) : function),
            Entry("Extend", new PdfArray([new PdfBoolean(extend), new PdfBoolean(false)]))
        ]);
        var resources = new PdfDictionary([
            Entry("Shading", new PdfDictionary([Entry("Sh1", shading)])),
            Entry("ExtGState", new PdfDictionary([Entry("Half",
                new PdfDictionary([Entry("ca", new PdfReal(0.5))]))]))
        ]);
        var update = new PdfIncrementalUpdateBuilder(source);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build()));
    }

    private static void AssertPixel(PdfRenderedFrame frame, int x, float red, float green, float blue,
        float alpha = 1f)
    {
        int offset = x * 4;
        Assert.InRange(frame.Pixels[offset], red - 0.00000004f, red + 0.00000004f);
        Assert.InRange(frame.Pixels[offset + 1], green - 0.00000004f, green + 0.00000004f);
        Assert.InRange(frame.Pixels[offset + 2], blue - 0.00000004f, blue + 0.00000004f);
        Assert.Equal(alpha, frame.Pixels[offset + 3]);
    }

    private static PdfArray Numbers(params double[] values) =>
        new(values.Select(value => (PdfObject)new PdfReal(value)));
    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
