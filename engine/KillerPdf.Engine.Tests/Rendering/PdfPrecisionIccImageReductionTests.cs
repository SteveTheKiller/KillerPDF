using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionIccImageReductionTests
{
    private const string GammaRgbProfile =
        "AAACCAAAAAACQAAAbW50clJHQiBYWVogB+oACwADAAAAAAAAYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAPbWAAEAAAAA0y10ZXN0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJZGVzYwAAAPAAAAByY3BydAAAAWQAAAAjd3RwdAAAAYgAAAAUclhZWgAAAZwAAAAUZ1hZWgAAAbAAAAAUYlhZWgAAAcQAAAAUclRSQwAAAdgAAAAOZ1RSQwAAAegAAAAOYlRSQwAAAfgAAAAOZGVzYwAAAAAAAAAYU3ludGhldGljIFhZWi1iYXNpcyBSR0IAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB0ZXh0AAAAAFB1YmxpYyBkb21haW4gdGVzdCBmaXh0dXJlAABYWVogAAAAAAAA9tYAAQAAAADTLVhZWiAAAAAAAAD21gAAAAAAAAAAWFlaIAAAAAAAAAAAAAEAAAAAAABYWVogAAAAAAAAAAAAAAAAAADTLWN1cnYAAAAAAAAAAQIAAABjdXJ2AAAAAAAAAAECAAAAY3VydgAAAAAAAAABAgAAAA==";

    private const string GammaGrayProfile =
        "AAABbAAAAAACQAAAbW50ckdSQVlYWVogB+oACwADAAAAAAAAYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAPbWAAEAAAAA0y10ZXN0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEZGVzYwAAALQAAABwY3BydAAAASQAAAAjd3RwdAAAAUgAAAAUa1RSQwAAAVwAAAAOZGVzYwAAAAAAAAAWU3ludGhldGljIGxpbmVhciBHcmF5AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHRleHQAAAAAUHVibGljIGRvbWFpbiB0ZXN0IGZpeHR1cmUAAFhZWiAAAAAAAAD21gABAAAAANMtY3VydgAAAAAAAAABAgAAAA==";
    private const string GrayJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAALCAAQABABAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AP4f6/YCv9ACv2Ar/9k=";

    [Fact]
    public void IntegerAreaReductionAveragesEncodedSamplesBeforeGammaTrc()
    {
        var renderer = Renderer();

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));

        // Four repeated rows alternate 16384 and 49152. The encoded area mean is 32768/65535.
        // Independent Decimal60 ICC/Bradford vectors apply gamma 2 after averaging.
        AssertPixel(frame, 0, 0.75542361f, -0.23593843f, 0.01734821f);
    }

    [Fact]
    public void CodecReducedIccJpegCannotMasqueradeAsOriginalPrecisionSamples()
    {
        var renderer = Renderer(gray: true, bits: 8, jpeg: true);
        var options = new PdfRenderOptions(1, 1, transparentBackground: true);

        Assert.Empty(renderer.Render(0, options).Diagnostics);
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, options));
    }

    [Theory]
    [InlineData(true, 16)]
    [InlineData(true, 8)]
    [InlineData(false, 8)]
    public void GrayAndEightBitAreaMeansRetainOriginalEncodedComponents(bool gray, int bits)
    {
        var renderer = Renderer(gray: gray, bits: bits);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1, transparentBackground: true));

        // Independent means are 32768/65535 for 16-bit or 128/255 for 8-bit before gamma 2.
        if (!gray)
            AssertPixel(frame, 0, 0.76133688f, -0.23778530f, 0.01748401f);
        else if (bits == 8)
            AssertPixel(frame, 0, 0.25196625f, 0.25196396f, 0.25196659f, 0.000006f);
        else
            AssertPixel(frame, 0, 0.25000924f, 0.25000696f, 0.25000958f, 0.000006f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnequalIntegerFactorsRetainSeparateAdjacentSixteenBitAreaMeans(bool gray)
    {
        var renderer = Renderer(gray: gray, width: 2, height: 2, sourceHeight: 6,
            row: [32768, 32769, 32769, 32770]);
        var options = new PdfRenderOptions(2, 2, transparentBackground: true);
        Assert.Empty(renderer.Render(0, options).Diagnostics);

        var frame = renderer.RenderFrame(0, options);

        // Each output footprint covers 2 columns by 3 rows. Encoded means differ by 1/65535.
        for (int row = 0; row < 2; row++)
        {
            if (gray)
            {
                AssertPixel(frame, row * 2, 0.25001687f, 0.25001459f, 0.25001721f, 0.000006f);
                AssertPixel(frame, row * 2 + 1, 0.25003213f, 0.25002985f, 0.25003247f, 0.000006f);
            }
            else
            {
                AssertPixel(frame, row * 2, 0.75544666f, -0.23594563f, 0.01734874f);
                AssertPixel(frame, row * 2 + 1, 0.75549277f, -0.23596003f, 0.01734980f);
                Assert.True(frame.Pixels[row * 8 + 1] < 0);
            }
            Assert.True(frame.Pixels[row * 8 + 4] > frame.Pixels[row * 8]);
        }
    }

    [Theory]
    [InlineData(3, 3, null)]
    [InlineData(4, 2, null)]
    [InlineData(2, 8, null)]
    public void FractionalSingleAxisAndMixedReductionGeometryRetainsPrecision(int width, int height, string? content)
    {
        var renderer = Renderer(width: width, height: height, content: content);
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(width, height));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(width * height * 4, frame.Pixels.Length);
        Assert.Contains(frame.Pixels.ToArray(), value => value < 0);
    }

    [Theory]
    [InlineData(1, 1, "1 0 0 1 0.5 0 cm /Image Do")]
    [InlineData(1, 1, "1 0 0.25 1 0 0 cm /Image Do")]
    public void UnalignedAndShearedAreaGeometryRemainsRejected(int width, int height, string? content)
    {
        var renderer = Renderer(width: width, height: height, content: content);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(width, height)));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 2)]
    public void AreaDecodeOutsideDeviceRangeRemainsRejected(double minimum, double maximum)
    {
        var renderer = Renderer(gray: true, decode: [minimum, maximum]);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    private static PdfPageRenderer Renderer(bool gray = false, int bits = 16, bool jpeg = false,
        int width = 1, int height = 1, int sourceHeight = 4, int[]? row = null,
        string? content = null, double[]? decode = null)
    {
        byte[] profile = Convert.FromBase64String(gray ? GammaGrayProfile : GammaRgbProfile);
        Assert.Equal(gray ? "736FE315493475E60B7EE28585B729B2C93CBD0C288530831497CB87170BDE99"
            : "16757CE04B74548F55CD75A2BCD4F732C3D48EF4515FE1DEB1CB0DEC712CB3C4",
            Convert.ToHexString(SHA256.HashData(profile)));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height,
            Encoding.ASCII.GetBytes(content ?? $"{width} 0 0 {height} 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        int components = gray ? 1 : 3;
        row ??= bits == 8 ? [64, 192, 64, 192] : [16384, 49152, 16384, 49152];
        byte[] samples = jpeg ? Convert.FromBase64String(GrayJpeg)
            : Enumerable.Range(0, sourceHeight).SelectMany(_ => row)
                .SelectMany(value => Enumerable.Range(0, components).SelectMany(channel => bits == 16
                    ? new byte[] { (byte)((channel == 0 ? value : 0) >> 8), (byte)(channel == 0 ? value : 0) }
                    : new byte[] { (byte)(channel == 0 ? value : 0) })).ToArray();
        var icc = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(components))]), profile));
        var entries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(jpeg ? 16 : row.Length)),
            Entry("Height", new PdfInteger(jpeg ? 16 : sourceHeight)),
            Entry("ColorSpace", new PdfArray([Name("ICCBased"), icc])), Entry("BitsPerComponent", new PdfInteger(bits)),
            Entry("Interpolate", new PdfBoolean(false)) };
        if (jpeg) entries.Add(Entry("Filter", Name("DCTDecode")));
        if (decode is not null)
            entries.Add(Entry("Decode", new PdfArray(decode.Select(value => (PdfObject)new PdfReal(value)))));
        var image = update.AddObject(new PdfStream(new PdfDictionary(entries), samples));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        return new PdfPageRenderer(PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build()));
    }

    private static void AssertPixel(PdfRenderedFrame frame, int pixel, float red, float green, float blue,
        float tolerance = 0.000002f)
    {
        float[] expected = [red, green, blue];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[pixel * 4 + channel], expected[channel] - tolerance, expected[channel] + tolerance);
        Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
