using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubJpegImageLayoutTests
{
    // Independently encoded Gray16 quadrant fixture with levels 16, 64, 192 and 240.
    private const string GrayJpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAALCAAQABABAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AP4f6/YCv9ACv2Ar/9k=";

    [Fact]
    public void PackagedJpegPreservesKnownGrayPixelsDimensionsAndReopenedPdf()
    {
        byte[] jpeg = Convert.FromBase64String(GrayJpeg);
        byte[] source = Package(jpeg);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = Font();
        var layout = book.CreateLayout(new EpubLayoutOptions(24, 24, font) { Margin = 2, FontSize = 10 });
        var frame = layout.RenderPage(0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 18, PageHeightPoints = 18, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var image = Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(16, image.PixelWidth);
        Assert.Equal(16, image.PixelHeight);
        Assert.Equal(12, image.BoundingBox.Width, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(24, 24));
        foreach (var (x, y, level, linear) in new[] {
            (4, 4, (byte)16, .0051815167f), (12, 4, (byte)64, .0512694584f),
            (4, 12, (byte)192, .5271151257f), (12, 12, (byte)240, .8713671192f) })
        {
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.InRange(frame.Pixels[(y * 24 + x) * 4 + channel], linear - .0000001f, linear + .0000001f);
                Assert.Equal(level, saved.Pixels.Span[(y * 24 + x) * 4 + channel]);
            }
        }
        Assert.Equal(jpeg, book.ReadResource("EPUB/check.jpg"));
        Assert.Equal(original, source);
    }

    // DC-only RGB blocks: coefficients -512, -384 and -256 at unit quantization.
    // The JPEG inverse DC transform gives literal RGB levels 64, 80 and 96.
    private const string RgbJpeg = "FFD8FFDB00430001010101010101010101010101010101010101010101010101010101010101010101010101010101010101010101010101010101010101010101010101010101FFC00011080008000803521100471100421100FFC40031000000000C000000000000000000000000000102030405060708090A0B100100000000000000000000000000000000FFDA000C03520047004200003F00A7FD27F4BFDFFFD9";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiteralRgbBlocksRemainColoredThroughLayoutScalingAndPdf(bool adobe)
    {
        byte[] jpeg = Convert.FromHexString(RgbJpeg);
        if (adobe) jpeg = WithSegment(jpeg, 0xee, "Adobe\0d\0\0\0\0\0"u8.ToArray());
        var book = EpubDocument.Open(Package(jpeg, "display:block;max-width:4px"));
        var font = Font();
        var frame = book.CreateLayout(new EpubLayoutOptions(24, 24, font) { Margin = 2, FontSize = 10 }).RenderPage(0);
        // Independent Decimal60 sRGB decoding of encoded levels 64, 80 and 96.
        float[] linear = [.0512694584f, .0802198203f, .1169706678f];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[(3 * 24 + 3) * 4 + channel], linear[channel] - .0000001f, linear[channel] + .0000001f);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((3 * 24 + 6) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 18, PageHeightPoints = 18, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var image = Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(8, image.PixelWidth);
        Assert.Equal(8, image.PixelHeight);
        Assert.Equal(3, image.BoundingBox.Width, 6);
        Assert.Equal(3, image.BoundingBox.Height, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(24, 24));
        Assert.Equal(new byte[] { 96, 80, 64, 255 }, saved.Pixels.Slice((3 * 24 + 3) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((3 * 24 + 6) * 4, 4).ToArray());
        Assert.Equal(jpeg, book.ReadResource("EPUB/check.jpg"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SquareJfifDensityPreservesCssPixelDimensions(byte unit)
    {
        byte[] jpeg = Convert.FromBase64String(GrayJpeg);
        jpeg[13] = unit;
        jpeg[15] = jpeg[17] = 72;
        var book = EpubDocument.Open(Package(jpeg));
        var font = Font();
        var frame = book.CreateLayout(new EpubLayoutOptions(24, 24, font) { Margin = 2, FontSize = 10 }).RenderPage(0);
        Assert.InRange(frame.Pixels[(4 * 24 + 4) * 4], .0051814167f, .0051816167f);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 18, PageHeightPoints = 18, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(12, Assert.Single(new PdfPageContentReader(pdf).Read(0).Images).BoundingBox.Width, 6);
        Assert.Equal(jpeg, book.ReadResource("EPUB/check.jpg"));
    }

    [Theory]
    [InlineData(0xe1, "457869660000", "EXIF")]
    [InlineData(0xe2, "4943435F50524F46494C4500", "ICC")]
    [InlineData(0xee, "41646F626500640000000002", "Adobe")]
    [InlineData(0xfe, "616263", "metadata")]
    [InlineData(0xed, "616263", "metadata")]
    public void UnsupportedColorOrientationAndOtherMetadataAreDiagnosed(byte marker, string hex, string diagnostic)
    {
        byte[] jpeg = WithSegment(Convert.FromBase64String(GrayJpeg), marker, Convert.FromHexString(hex));
        var error = Assert.Throws<NotSupportedException>(() => Layout(jpeg));
        Assert.Contains(diagnostic, error.Message);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(32769, 16)]
    [InlineData(1025, 1024)]
    public void InvalidOrOverBudgetSourceDimensionsAreRejectedBeforeDecoding(int width, int height)
    {
        byte[] jpeg = Convert.FromBase64String(GrayJpeg);
        int frame = FindFrame(jpeg);
        jpeg[frame + 5] = (byte)(height >> 8);
        jpeg[frame + 6] = (byte)height;
        jpeg[frame + 7] = (byte)(width >> 8);
        jpeg[frame + 8] = (byte)width;
        Assert.Contains("storage limit", Assert.Throws<InvalidDataException>(() => Layout(jpeg)).Message);
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(8, 4)]
    public void UnsupportedSourcePrecisionAndColorAreDiagnosed(byte bits, byte components)
    {
        byte[] jpeg = Convert.FromBase64String(GrayJpeg);
        int frame = FindFrame(jpeg);
        jpeg[frame + 4] = bits;
        jpeg[frame + 9] = components;
        Assert.Contains("eight-bit Gray or RGB", Assert.Throws<NotSupportedException>(() => Layout(jpeg)).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UndefinedOrNonsquareJfifDensityIsDiagnosed(byte vertical)
    {
        byte[] jpeg = Convert.FromBase64String(GrayJpeg);
        jpeg[17] = vertical;
        Assert.Contains("density", Assert.Throws<NotSupportedException>(() => Layout(jpeg)).Message);
    }

    [Fact]
    public void TruncatedJpegIsDiagnosedAndPreCanceledLayoutPreservesSource()
    {
        byte[] jpeg = Convert.FromBase64String(GrayJpeg);
        Assert.Throws<InvalidDataException>(() => Layout(jpeg[..^2]));
        Assert.Throws<InvalidDataException>(() => Layout(jpeg[..25]));
        byte[] source = Package(jpeg);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(
            new EpubLayoutOptions(24, 24, Font()), cancellation.Token));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("https://example.invalid/check.jpg")]
    [InlineData("//example.invalid/check.jpg")]
    [InlineData("check.jpg#fragment")]
    public void JpegSupportDoesNotPermitExternalOrFragmentImageSources(string reference)
    {
        var book = EpubDocument.Open(Package(Convert.FromBase64String(GrayJpeg), reference: reference));
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(24, 24, Font()) { Margin = 2, FontSize = 10 }));
    }

    [Fact]
    public void JfifAndExplicitRgbComponentColorConflictIsDiagnosed()
    {
        byte[] jpeg = WithSegment(Convert.FromHexString(RgbJpeg), 0xe0,
            Convert.FromHexString("4A46494600010100000100010000"));
        Assert.Contains("color", Assert.Throws<NotSupportedException>(() => Layout(jpeg)).Message);
    }

    [Fact]
    public void IndependentlyEncodedProgressiveGrayPreservesAllFourKnownLevels()
    {
        byte[] jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wgALCAAQABABAREA/8QAFgABAQEAAAAAAAAAAAAAAAAACAkK/9oACAEBAAAAAYfsDQAwP//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAQUCH//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEABj8CH//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAT8hH//aAAgBAQAAABAP/8QAFBABAAAAAAAAAAAAAAAAAAAAIP/aAAgBAQABPxAf/9k=");
        var book = EpubDocument.Open(Package(jpeg));
        var font = Font();
        var frame = book.CreateLayout(new EpubLayoutOptions(24, 24, font) { Margin = 2, FontSize = 10 }).RenderPage(0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 18, PageHeightPoints = 18, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(24, 24));
        foreach (var (x, y, level, linear) in new[] {
            (4, 4, (byte)16, .0051815167f), (12, 4, (byte)64, .0512694584f),
            (4, 12, (byte)192, .5271151257f), (12, 12, (byte)240, .8713671192f) })
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.InRange(frame.Pixels[(y * 24 + x) * 4 + channel], linear - .0000001f, linear + .0000001f);
                Assert.Equal(level, saved.Pixels.Span[(y * 24 + x) * 4 + channel]);
            }
        Assert.Equal(jpeg, book.ReadResource("EPUB/check.jpg"));
    }

    private static EpubLayoutDocument Layout(byte[] jpeg) => EpubDocument.Open(Package(jpeg))
        .CreateLayout(new EpubLayoutOptions(24, 24, Font()) { Margin = 2, FontSize = 10 });

    private static byte[] WithSegment(byte[] jpeg, byte marker, byte[] payload) =>
        [jpeg[0], jpeg[1], 0xff, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload, .. jpeg[2..]];

    private static int FindFrame(byte[] jpeg)
    {
        for (int index = 0; index < jpeg.Length - 1; index++)
            if (jpeg[index] == 0xff && jpeg[index + 1] == 0xc0) return index;
        throw new InvalidOperationException("Fixture frame missing.");
    }

    private static TrueTypeFont Font()
    {
        using var stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream("KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return TrueTypeFont.Load(bytes.ToArray());
    }

    private static byte[] Package(byte[] jpeg, string style = "display:block", string reference = "check.jpg")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='EPUB/book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:test:jpeg</dc:identifier><dc:title>JPEG</dc:title><dc:language>en</dc:language></metadata><manifest><item id='chapter' href='text.xhtml' media-type='application/xhtml+xml'/><item id='image' href='check.jpg' media-type='image/jpeg'/></manifest><spine><itemref idref='chapter'/></spine></package>");
            Add("EPUB/text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>JPEG</title></head><body><img style='" + style + "' src='" + reference + "' alt=''/></body></html>");
            using var entry = zip.CreateEntry("EPUB/check.jpg", CompressionLevel.NoCompression).Open();
            entry.Write(jpeg);
            void Add(string path, string text)
            {
                using var output = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
                output.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return buffer.ToArray();
    }
}
