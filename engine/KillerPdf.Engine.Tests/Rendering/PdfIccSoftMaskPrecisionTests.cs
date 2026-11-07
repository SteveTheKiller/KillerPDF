using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccSoftMaskPrecisionTests
{
    [Fact]
    public void NaturalRgbMatrixIccImageKeepsAdjacentSourceAndSoftMaskPrecision()
    {
        var document = Build(mask: [0, 1, 0, 2]);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options());

        // Fixed16 XYZ-basis profile and identity TRCs have independent Decimal60 color oracles.
        Pixel(frame, 0, [1.51082417f, -.47186967f, .03469589f], .000015259021896696422);
        Pixel(frame, 1, [1.51087027f, -.47188407f, .03469695f], .000030518043793392844);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
        Assert.True(frame.Pixels[7] > frame.Pixels[3]);
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData(false, 8, 8)]
    [InlineData(false, 8, 16)]
    [InlineData(false, 16, 8)]
    [InlineData(false, 16, 16)]
    [InlineData(true, 8, 8)]
    [InlineData(true, 8, 16)]
    [InlineData(true, 16, 8)]
    [InlineData(true, 16, 16)]
    public void MatrixIccSourceAndMaskDepthsRetainIndependentColorAndAlpha(bool gray, int bits, int maskBits)
    {
        var document = Build(maskBits == 8 ? [128, 129] : [128, 0, 128, 1], gray, bits, maskBits);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options());

        float[] first = gray
            ? bits == 16 ? [.50001085f, .50000629f, .50001152f] : [.50196402f, .50195944f, .50196469f]
            : bits == 16 ? [1.51082417f, -.47186967f, .03469589f] : [1.51672582f, -.47371291f, .03483142f];
        float[] second = bits == 8 ? first : gray
            ? [.50002611f, .50002155f, .50002678f] : [1.51087027f, -.47188407f, .03469695f];
        Pixel(frame, 0, first, maskBits == 8 ? .5019607843137255 : .5000076295109483, gray ? .000006f : .000002f);
        Pixel(frame, 1, second, maskBits == 8 ? .5058823529411764 : .500022888532845, gray ? .000006f : .000002f);
        if (bits == 16) Assert.True(frame.Pixels[4] > frame.Pixels[0]);
        Assert.True(frame.Pixels[7] > frame.Pixels[3]);
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(false, 16)]
    [InlineData(true, 8)]
    [InlineData(true, 16)]
    public void ZeroAndOpaqueMaskEndpointsRetainFiniteStraightPixelsAndSource(bool gray, int maskBits)
    {
        var document = Build(maskBits == 8 ? [0, 255] : [0, 0, 255, 255], gray, maskBits: maskBits);
        byte[] original = Snapshot(document);

        var frame = new PdfPageRenderer(document).RenderFrame(0, Options());

        Assert.Equal(0, frame.Pixels[3]);
        Assert.All(frame.Pixels.ToArray(), value => Assert.True(float.IsFinite(value)));
        Pixel(frame, 1, gray ? [.50002611f, .50002155f, .50002678f] : [1.51087027f, -.47188407f, .03469695f],
            1, gray ? .000006f : .000002f);
        Assert.Equal(original, Snapshot(document));
    }

    [Theory]
    [InlineData("fractional")]
    [InlineData("enlarged")]
    [InlineData("smoothed")]
    [InlineData("reduced")]
    [InlineData("default-profile")]
    [InlineData("nonrelative-intent")]
    [InlineData("black-point")]
    [InlineData("matte")]
    [InlineData("color-key")]
    public void UnsupportedIccMaskPaintsRemainRefused(string scenario)
    {
        string content = scenario switch {
            "fractional" => "2 0 0 1 .5 0 cm /Image Do",
            "enlarged" or "smoothed" => "4 0 0 1 0 0 cm /Image Do",
            "reduced" => "1 0 0 1 0 0 cm /Image Do",
            "black-point" => "/Bpc gs 2 0 0 1 0 0 cm /Image Do",
            _ => "2 0 0 1 0 0 cm /Image Do" };
        var renderer = PdfPrecisionIccImageTests.Renderer(Convert.FromBase64String(PdfPrecisionIccImageTests.RgbProfileBase64),
            content: content, interpolate: scenario == "smoothed", defaultSpace: scenario == "default-profile",
            intent: scenario == "nonrelative-intent" ? "Perceptual" : null,
            mask: scenario == "matte" ? "matte" : scenario == "color-key" ? "key" : "soft");

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, Options()));
    }

    [Fact]
    public void NondefaultSourceDecodeRemainsRefusedWithIccSoftMask()
    {
        var document = Build([128, 0, 128, 1], reverseDecode: true);
        byte[] original = Snapshot(document);

        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, Options()));

        Assert.Equal(original, Snapshot(document));
    }

    private static PdfDocument Build(byte[] mask, bool gray = false, int bits = 16, int maskBits = 16,
        bool reverseDecode = false)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("2 0 0 1 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(gray ? 1 : 3))]),
            Convert.FromBase64String(gray ? PdfPrecisionIccImageTests.GrayProfileBase64 : PdfPrecisionIccImageTests.RgbProfileBase64)));
        var softMask = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
            Entry("ColorSpace", Name("DeviceGray")), Entry("BitsPerComponent", new PdfInteger(maskBits))]), mask));
        byte[] samples = bits == 16
            ? gray ? [128, 0, 128, 1] : [128, 0, 0, 0, 0, 0, 128, 1, 0, 0, 0, 0]
            : gray ? [128, 128] : [128, 0, 0, 128, 0, 0];
        var entries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
            Entry("ColorSpace", new PdfArray([Name("ICCBased"), profile])),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("SMask", softMask) };
        if (reverseDecode) entries.Add(Entry("Decode", new PdfArray(Enumerable.Range(0, (gray ? 1 : 3) * 2)
            .Select(index => (PdfObject)new PdfInteger(1 - index % 2)))));
        var image = update.AddObject(new PdfStream(new PdfDictionary(entries), samples));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        return PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
    }

    private static byte[] Snapshot(PdfDocument document)
    {
        var root = Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        return new PdfIncrementalUpdateBuilder(document).ReplaceObject(root.ObjectNumber, document.Resolve(root)).Build();
    }

    private static void Pixel(PdfRenderedFrame frame, int pixel, float[] expected, double alpha, float tolerance = .000002f)
    {
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[pixel * 4 + channel], expected[channel] - tolerance, expected[channel] + tolerance);
        Assert.InRange((double)frame.Pixels[pixel * 4 + 3], alpha - .00000003, alpha + .00000003);
    }

    private static PdfRenderOptions Options() => new(2, 1, transparentBackground: true);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
