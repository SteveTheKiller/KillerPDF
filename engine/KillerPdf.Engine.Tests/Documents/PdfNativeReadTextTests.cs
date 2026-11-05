using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfNativeReadTextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeTextValidatesPagesAndCancellationBeforeReading(bool epub)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = epub ? PdfReadSnapshot.OpenEpub(Book("A"),
            new EpubLayoutOptions(16, 14, font) { FontSize = 10, Margin = 2 })
            : PdfReadSnapshot.OpenDjvu(DjvuPage([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadTextPage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadTextPage(snapshot.PageCount));
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => snapshot.ReadTextPage(-1, canceled));
        Assert.Throws<OperationCanceledException>(() => snapshot.ReadTextPage(0, canceled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedNativeTextAndRegionsRemainImmutable(bool epub)
    {
        byte[] bytes = epub ? Book("A") : DjvuPage([.. Zone(6, 1, 2, 3, 4, 0, 1)]);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = epub ? PdfReadSnapshot.OpenEpub(bytes,
            new EpubLayoutOptions(16, 14, font) { FontSize = 10, Margin = 2 }) : PdfReadSnapshot.OpenDjvu(bytes);
        Array.Clear(bytes);
        var page = snapshot.ReadTextPage(0);
        var region = Assert.Single(page.Regions);
        var collection = Assert.IsAssignableFrom<IList<PdfReadTextRegion>>(page.Regions);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        Assert.Throws<NotSupportedException>(() => collection[0] = region with { Start = 100 });
        Assert.Equal("A", page.Text.Substring(region.Start, region.Length));
        Assert.Equal(region, Assert.Single(snapshot.ReadTextPage(0).Regions));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(31)]
    public void StructuralSeparatorsStayInTextWithoutMakingPageSizedSelectionTargets(byte separator)
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(DjvuPage([
            .. Zone(1, 0, 0, 100, 200, 0, 8, 2),
            .. Zone(4, 0, 0, 100, 200, 7, 1),
            .. Zone(6, -100, 0, 30, 40, -8, 1)], separator));
        var page = snapshot.ReadTextPage(0);
        Assert.Equal((char)separator, page.Text[^1]);
        var region = Assert.Single(page.Regions);
        Assert.Equal((0, 1), (region.Start, region.Length));
        Assert.Equal("A", page.Text.Substring(region.Start, region.Length));
    }

    [Fact]
    public void NativeTextWithoutEncodedZonesDoesNotInventSelectableBoxes()
    {
        var page = PdfReadSnapshot.OpenDjvu(DjvuPage([])).ReadTextPage(0);
        Assert.Equal("A\u00e9\U0001f600\n", page.Text);
        Assert.Empty(page.Regions);
    }

    [Fact]
    public void EpubPaginationUsesPageLocalOffsetsWithoutDroppingSeparators()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = PdfReadSnapshot.OpenEpub(Book("A A"),
            new EpubLayoutOptions(16, 14, font) { FontSize = 10, Margin = 2 });
        Assert.Equal(2, snapshot.PageCount);
        Assert.Equal("A ", snapshot.ReadTextPage(0).Text);
        Assert.Equal("A", snapshot.ReadTextPage(1).Text);
        Assert.Equal(new PdfReadTextRegion(0, 1, new PdfContentBounds(1.5, 1.5, 6, 9)),
            Assert.Single(snapshot.ReadTextPage(1).Regions));
    }

    [Fact]
    public void DjvuTextUsesFinestSourceZonesAndRecordedResolutionWithoutRotatingTwice()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(DjvuPage([
            .. Zone(1, 0, 0, 100, 200, 0, 8, 1),
            .. Zone(6, 10, 20, 30, 40, 1, 6)]));
        var page = snapshot.ReadTextPage(0);
        Assert.Equal("A\u00e9\U0001f600\n", page.Text);
        var region = Assert.Single(page.Regions);
        Assert.Equal(1, region.Start);
        Assert.Equal(3, region.Length);
        Assert.Equal(2.4, region.Bounds.Left, 12);
        Assert.Equal(33.6, region.Bounds.Bottom, 12);
        Assert.Equal(9.6, region.Bounds.Right, 12);
        Assert.Equal(43.2, region.Bounds.Top, 12);
        Assert.Equal(90, snapshot.GetPageGeometry(0).IntrinsicRotation);
        Assert.Equal(300, snapshot.GetPageGeometry(0).DpiX);
    }

    [Fact]
    public void EpubTextUsesLocalSourceRangesAndUnrotatedPointBounds()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = PdfReadSnapshot.OpenEpub(Book("A"),
            new EpubLayoutOptions(16, 14, font) { FontSize = 10, Margin = 2 });
        var page = snapshot.ReadTextPage(0);
        Assert.Equal("A", page.Text);
        Assert.Equal(new PdfReadTextRegion(0, 1, new PdfContentBounds(1.5, 1.5, 6, 9)),
            Assert.Single(page.Regions));
        Assert.Equal(0, snapshot.GetPageGeometry(0).IntrinsicRotation);
    }

    private static byte[] DjvuPage(byte[] zones, byte separator = 10) => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 5]),
            .. Chunk("TXTa", [0, 0, 8, 65, 0xc3, 0xa9, 0xf0, 0x9f, 0x98, 0x80, separator, 1, .. zones])])];

    private static byte[] Zone(byte type, int left, int bottom, int width, int height, int start, int length, int children = 0)
    {
        var result = new byte[17];
        result[0] = type;
        int[] values = [left, bottom, width, height, start];
        for (int index = 0; index < values.Length; index++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1 + index * 2),
                checked((ushort)(values[index] + 0x8000)));
        result[11] = (byte)(length >> 16); result[12] = (byte)(length >> 8); result[13] = (byte)length;
        result[14] = (byte)(children >> 16); result[15] = (byte)(children >> 8); result[16] = (byte)children;
        return result;
    }

    private static byte[] Chunk(string id, byte[] data) => [.. Encoding.ASCII.GetBytes(id),
        (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
        .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];

    private static byte[] Book(string text)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-text</dc:identifier><dc:title>Native text</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-05T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native text</title></head><body><p>{text}</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native text</a></li></ol></nav></body></html>
                """);
            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }
}
