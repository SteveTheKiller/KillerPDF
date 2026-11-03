using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionJpegSourceTests
{
    // Existing independently encoded 16x16 Gray fixture: quadrant values 16,64,192,240.
    private const string GrayJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAALCAAQABABAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AP4f6/YCv9ACv2Ar/9k=";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void CodecReducedDeviceJpegSamplesRemainOutsideThePrecisionContract(int size)
    {
        var renderer = Renderer(size);
        var options = new PdfRenderOptions(size, size, transparentBackground: true);

        var compatibility = renderer.Render(0, options);
        Assert.Empty(compatibility.Diagnostics);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte expected = size == 1 ? (byte)128 : QuadrantLevel(x, y, size);
                for (int channel = 0; channel < 3; channel++)
                    Assert.Equal(expected, compatibility.Pixels.Span[(y * size + x) * 4 + channel]);
                Assert.Equal((byte)255, compatibility.Pixels.Span[(y * size + x) * 4 + 3]);
            }
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, options));
    }

    [Fact]
    public void ReducedJpegExceptionPreservesTheActualDiagnosticReason()
    {
        var renderer = Renderer();

        var error = Assert.Throws<NotSupportedException>(() =>
            renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));

        Assert.StartsWith("The page contains rendering features unavailable to the high-precision path.", error.Message);
        Assert.Contains("Codec-reduced", error.Message);
    }

    [Fact]
    public void FullSourceJpegRetainsIndependentConstantBlockColorsAndByteOutput()
    {
        var renderer = Renderer(16);
        var options = new PdfRenderOptions(16, 16, transparentBackground: true);
        var compatibility = renderer.Render(0, options);
        Assert.Empty(compatibility.Diagnostics);

        var frame = renderer.RenderFrame(0, options);

        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                byte level = QuadrantLevel(x, y, 16);
                // Independent Decimal60 sRGB decoding of the fixture's known encoded levels.
                float expected = level switch {
                    16 => 0.0051815167f, 64 => 0.0512694584f,
                    192 => 0.5271151257f, _ => 0.8713671192f };
                int offset = (y * 16 + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    Assert.InRange(frame.Pixels[offset + channel], expected - 0.0000001f, expected + 0.0000001f);
                    Assert.Equal(level, compatibility.Pixels.Span[offset + channel]);
                }
                Assert.Equal(1f, frame.Pixels[offset + 3]);
                Assert.Equal((byte)255, compatibility.Pixels.Span[offset + 3]);
            }
        Assert.Equal(compatibility.Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void FirstActualCompatibilityDiagnosticIsCappedAt512Characters()
    {
        string missingFont = new('A', 600);
        var document = PdfDocument.OpenWithCompatibilityRecovery(new PdfDocumentBuilder()
            .AddPage(1, 1, Encoding.ASCII.GetBytes($"/{missingFont} 12 Tf")).Build());
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(1, 1);
        string expected = $"Missing font resource /{missingFont} was replaced with Helvetica.";
        Assert.Equal(expected, Assert.Single(renderer.Render(0, options).Diagnostics));

        var error = Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, options));

        Assert.Equal("The page contains rendering features unavailable to the high-precision path. "
            + expected[..512], error.Message);
    }

    private static byte QuadrantLevel(int x, int y, int size) => y < size / 2
        ? (byte)(x < size / 2 ? 16 : 64) : (byte)(x < size / 2 ? 192 : 240);

    private static PdfPageRenderer Renderer(int size = 1)
    {
        byte[] jpeg = Convert.FromBase64String(GrayJpeg);
        Assert.Equal("5BA66FC0C59C652C98CE618ACC6D58FBBD742849EEB54BB44D31128909BB847D",
            Convert.ToHexString(SHA256.HashData(jpeg)));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size,
            Encoding.ASCII.GetBytes($"{size} 0 0 {size} 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(16)), Entry("Height", new PdfInteger(16)),
            Entry("ColorSpace", Name("DeviceGray")), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("Filter", Name("DCTDecode")), Entry("Interpolate", new PdfBoolean(false))]), jpeg));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        return new PdfPageRenderer(PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build()));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
