using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuPdfConversionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData((byte)1, 3, 2, new byte[] { 255, 0, 0, 255, 255, 255 })]
    [InlineData((byte)2, 3, 2, new byte[] { 255, 255, 255, 0, 0, 255 })]
    [InlineData((byte)5, 2, 3, new byte[] { 255, 255, 255, 0, 255, 0 })]
    [InlineData((byte)6, 2, 3, new byte[] { 0, 255, 0, 255, 255, 255 })]
    public void ConversionPreservesLiteralPixelsPhysicalSizeOrientationAndNativeSource(
        byte orientation, int width, int height, byte[] expected)
    {
        // T.6: horizontal white run 1, black run 2; next row VR(2), then EOFB.
        byte[] source = Page(orientation);
        byte[] original = source.ToArray();
        output.WriteLine("DjVu fixture SHA256: " + Convert.ToHexString(SHA256.HashData(source)));
        var native = DjvuDocument.Open(source);
        float[] nativeBefore = native.RenderPage(0).Pixels.ToArray();
        Assert.Equal(expected.Select(value => value / 255f),
            nativeBefore.Where((_, index) => index % 4 == 0));

        byte[] converted = native.ConvertToPdf();

        var pdf = PdfDocument.Open(converted);
        var geometry = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Equal(width / 2d, geometry.PhysicalWidth, 6);
        Assert.Equal(height / 2d, geometry.PhysicalHeight, 6);
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(width, height));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(new byte[] { expected[index], expected[index], expected[index], 255 },
                pixels.AsSpan(index * 4, 4).ToArray());
        Assert.Equal(original, source);
        Assert.Equal(nativeBefore, native.RenderPage(0).Pixels.ToArray());
    }

    [Fact]
    public void ConversionKeepsBundledPhysicalPageOrder()
    {
        byte[] first = Page(1)[4..];
        byte[] second = Page(5)[4..];
        int secondOffset = 36 + first.Length;
        byte[] source = [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8,
            .. Chunk("DIRM", [0x81, 0, 2, 0, 0, 0, 36,
                (byte)(secondOffset >> 24), (byte)(secondOffset >> 16),
                (byte)(secondOffset >> 8), (byte)secondOffset]), .. first, .. second])];
        var native = DjvuDocument.Open(source);

        var pdf = PdfDocument.Open(native.ConvertToPdf());

        var pages = PdfPageInformation.Read(pdf);
        Assert.Equal(2, pages.Count);
        Assert.Equal(1.5, pages[0].PhysicalWidth, 6);
        Assert.Equal(1, pages[0].PhysicalHeight, 6);
        Assert.Equal(1, pages[1].PhysicalWidth, 6);
        Assert.Equal(1.5, pages[1].PhysicalHeight, 6);
        var renderer = new PdfPageRenderer(pdf);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 255 },
            renderer.Render(0, new PdfRenderOptions(3, 2)).Pixels.ToArray().Where((_, i) => i % 4 == 0));
        Assert.Equal(new byte[] { 255, 255, 255, 0, 255, 0 },
            renderer.Render(1, new PdfRenderOptions(2, 3)).Pixels.ToArray().Where((_, i) => i % 4 == 0));
    }

    [Fact]
    public void ConversionRejectsTextRatherThanDroppingIt()
    {
        var native = DjvuDocument.Open(Page(1, Chunk("TXTa", [0, 0, 1, (byte)'A', 1])));
        Assert.Equal("A", native.ReadText(0));
        Assert.Contains("preserve text", Assert.Throws<NotSupportedException>(() => native.ConvertToPdf()).Message);
        Assert.Equal("A", native.ReadText(0));
    }

    [Fact]
    public void ConversionRejectsNavigationRatherThanDroppingIt()
    {
        var native = DjvuDocument.Open(Convert.FromHexString(
            "41542654464F524D00000064444A564D4449524D0000001181000100000052FFFFF8BF9E1FCC877DC900" +
            "4E41564D00000020FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601"));
        Assert.NotEmpty(native.ReadNavigation());
        Assert.Contains("preserve navigation", Assert.Throws<NotSupportedException>(() => native.ConvertToPdf()).Message);
        Assert.NotEmpty(native.ReadNavigation());
    }

    [Theory]
    [InlineData("Sjbz")]
    [InlineData("ANTa")]
    [InlineData("BG44")]
    public void ConversionPropagatesTheNativeUnsupportedLayerDiagnosis(string layer)
    {
        var native = DjvuDocument.Open(Page(1, Chunk(layer, [0])));
        var rendering = Assert.Throws<NotSupportedException>(() => native.RenderPage(0));
        var conversion = Assert.Throws<NotSupportedException>(() => native.ConvertToPdf());
        Assert.Equal(rendering.Message, conversion.Message);
    }

    [Fact]
    public void ConversionDoesNotIgnoreUnreadableText()
    {
        var native = DjvuDocument.Open(Page(1, Chunk("TXTz", [0])));
        Assert.Throws<InvalidDataException>(() => native.ConvertToPdf());
    }

    [Fact]
    public void ConversionHonorsCancellationBeforeDoingWork()
    {
        var native = DjvuDocument.Open(Page(1));
        Assert.Throws<OperationCanceledException>(() => native.ConvertToPdf(new CancellationToken(true)));
    }

    private static byte[] Page(byte orientation, params byte[][] extra) => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 144, 0, 22, orientation]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80]),
            .. extra.SelectMany(chunk => chunk)])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        int length = payload.Length;
        return [.. Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16),
            (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
