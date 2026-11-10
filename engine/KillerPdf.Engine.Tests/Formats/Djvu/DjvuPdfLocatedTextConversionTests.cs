using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuPdfLocatedTextConversionTests
{
    [Fact]
    public void ConversionPreservesLocatedLatinText()
    {
        var native = DjvuDocument.Open(Page(1, "A", Zone(0, 0, 2, 1, 0, 1)));

        var pdf = PdfDocument.Open(native.ConvertToPdf());

        Assert.Equal("A", new PdfPageContentReader(pdf).Read(0).Text);
    }

    [Theory]
    [InlineData((byte)1, 3, 2, 0, 0, 1, .5, new byte[] { 255, 0, 0, 255, 255, 255 })]
    [InlineData((byte)2, 3, 2, .5, .5, 1.5, 1, new byte[] { 255, 255, 255, 0, 0, 255 })]
    [InlineData((byte)5, 2, 3, 0, .5, .5, 1.5, new byte[] { 255, 255, 255, 0, 255, 0 })]
    [InlineData((byte)6, 2, 3, .5, 0, 1, 1, new byte[] { 0, 255, 0, 255, 255, 255 })]
    public void ConversionPreservesLiteralTextBoundsImagePixelsAndNativeSource(byte orientation,
        int width, int height, double left, double bottom, double right, double top, byte[] expected)
    {
        byte[] source = Page(orientation, "A", Zone(0, 0, 2, 1, 0, 1));
        byte[] original = source.ToArray();
        var native = DjvuDocument.Open(source);
        float[] originalPixels = native.RenderPage(0).Pixels.ToArray();

        var pdf = PdfDocument.Open(native.ConvertToPdf());

        var content = new PdfPageContentReader(pdf).Read(0);
        var letter = Assert.Single(content.Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(left, letter.BoundingBox.Left, 6);
        Assert.Equal(bottom, letter.BoundingBox.Bottom, 6);
        Assert.Equal(right, letter.BoundingBox.Right, 6);
        Assert.Equal(top, letter.BoundingBox.Top, 6);
        var geometry = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Equal(width / 2d, geometry.PhysicalWidth, 6);
        Assert.Equal(height / 2d, geometry.PhysicalHeight, 6);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(width, height));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(expected, frame.Pixels.ToArray().Where((_, index) => index % 4 == 0));
        Assert.Equal(original, source);
        Assert.Equal(originalPixels, native.RenderPage(0).Pixels.ToArray());
        Assert.Equal("A", native.ReadText(0));
    }

    [Fact]
    public void ConversionRetainsIndependentLogicalMappingsReadingOrderAndSeparators()
    {
        const string text = "\tA\nCaf\u00e9\r\n";
        var native = DjvuDocument.Open(Page(1, text,
            Zone(0, 0, 1, 1, 1, 1), Zone(2, 1, 1, 1, 3, 5)));

        byte[] converted = native.ConvertToPdf();

        var letters = new PdfPageContentReader(PdfDocument.Open(converted)).Read(0).Letters;
        Assert.Equal(text, string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(2, letters.Count);
        Assert.Equal(new PdfContentBounds(0, 0, .5, .5), letters[0].BoundingBox);
        Assert.Equal(new PdfContentBounds(1, .5, 1.5, 1), letters[1].BoundingBox);
        Assert.Equal(converted, native.ConvertToPdf());
    }

    [Theory]
    [InlineData("unlocated")]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("left")]
    [InlineData("bottom")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("whitespace")]
    [InlineData("zone-count")]
    [InlineData("logical-length")]
    public void ConversionLeavesOutTextItCannotPlaceAndNamesThePageWithoutChangingNativeSource(string scenario)
    {
        (string text, byte[][] zones) = scenario switch
        {
            "unlocated" => ("A", []),
            "prefix" => ("BA", [Zone(0, 0, 1, 1, 1, 1)]),
            "suffix" => ("AB", [Zone(0, 0, 1, 1, 0, 1)]),
            "gap" => ("ABA", [Zone(0, 0, 1, 1, 0, 1), Zone(2, 0, 1, 1, 2, 1)]),
            "overlap" => ("AB", [Zone(0, 0, 1, 1, 0, 2), Zone(2, 0, 1, 1, 1, 1)]),
            "left" => ("A", [Zone(-1, 0, 1, 1, 0, 1)]),
            "bottom" => ("A", [Zone(0, -1, 1, 1, 0, 1)]),
            "right" => ("A", [Zone(2, 0, 2, 1, 0, 1)]),
            "top" => ("A", [Zone(0, 1, 1, 2, 0, 1)]),
            "whitespace" => ("\t\n", []),
            "zone-count" => (new string('A', 4097),
                Enumerable.Range(0, 4097).Select(index => Zone(0, 0, 1, 1, index, 1)).ToArray()),
            "logical-length" => (new string('A', 65537), [Zone(0, 0, 1, 1, 0, 65537)]),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        byte[] source = Page(1, text, zones);
        byte[] original = source.ToArray();
        var native = DjvuDocument.Open(source);
        float[] originalPixels = native.RenderPage(0).Pixels.ToArray();
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(native.ConvertToPdf(notices));

        Assert.Empty(new PdfPageContentReader(pdf).Read(0).Letters);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(3, 2));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 255 }, frame.Pixels.ToArray().Where((_, index) => index % 4 == 0));
        var notice = Assert.Single(notices);
        Assert.Equal("TextNotConverted", notice.Code);
        Assert.StartsWith("Page 1 text was left out", notice.Message, StringComparison.Ordinal);
        Assert.Equal(0, notice.PageIndex);
        Assert.Equal(text, native.ReadText(0));
        Assert.Equal(original, source);
        Assert.Equal(originalPixels, native.RenderPage(0).Pixels.ToArray());
    }

    // The djvu2spec reference edition writes the prime of "Z'-Coder" as U+007F on 33 of its 39 pages.
    [Theory]
    [InlineData("\U0001f600", "�")]
    [InlineData("\0", "�")]
    [InlineData("Z\u007f-Coder", "Z�-Coder")]
    public void ConversionWritesCharactersTheTextLayerCannotRepresentAsReplacementCharacters(string text, string expected)
    {
        var native = DjvuDocument.Open(Page(1, text, Zone(0, 0, 1, 1, 0, Encoding.UTF8.GetByteCount(text))));
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(native.ConvertToPdf(notices));

        Assert.Equal(expected, string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        var notice = Assert.Single(notices);
        Assert.Equal("TextCharactersReplaced", notice.Code);
        Assert.StartsWith("Page 1 text", notice.Message, StringComparison.Ordinal);
        Assert.Equal(0, notice.PageIndex);
        Assert.Equal(text, native.ReadText(0));
    }

    [Fact]
    public void AMalformedTextLayerCostsOnlyItsPageText()
    {
        // A zone record cut short: reading the text fails, the page image still converts.
        byte[] source = Page(1, "A", Zone(0, 0, 1, 1, 0, 1)[..16]);
        var native = DjvuDocument.Open(source);
        Assert.Throws<InvalidDataException>(() => native.ReadText(0));
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(native.ConvertToPdf(notices));

        Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Empty(new PdfPageContentReader(pdf).Read(0).Letters);
        var notice = Assert.Single(notices);
        Assert.Equal("TextNotConverted", notice.Code);
        Assert.Equal(0, notice.PageIndex);
    }

    [Fact]
    public void ConversionHonorsCancellationBeforeReadingLocatedText()
    {
        var native = DjvuDocument.Open(Page(1, "A", Zone(0, 0, 1, 1, 0, 1)));
        Assert.Throws<OperationCanceledException>(() => native.ConvertToPdf(new CancellationToken(true)));
        Assert.Equal("A", native.ReadText(0));
    }

    private static byte[] Page(byte orientation, string text, params byte[][] zones)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 144, 0, 22, orientation]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80]),
            .. Chunk("TXTa", [(byte)(utf8.Length >> 16), (byte)(utf8.Length >> 8), (byte)utf8.Length,
                .. utf8, 1, .. zones.SelectMany(zone => zone)])])];
    }

    private static byte[] Zone(int left, int bottom, int width, int height, int start, int length,
        int children = 0, byte type = 6)
    {
        var result = new byte[17];
        result[0] = type;
        int[] values = [left, bottom, width, height, start];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1 + index * 2), checked((ushort)(values[index] + 0x8000)));
        result[11] = (byte)(length >> 16); result[12] = (byte)(length >> 8); result[13] = (byte)length;
        result[14] = (byte)(children >> 16); result[15] = (byte)(children >> 8); result[16] = (byte)children;
        return result;
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        int length = payload.Length;
        return [.. Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16),
            (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
