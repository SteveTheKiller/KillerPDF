using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfScaledReadSnapshotTests
{
    [Fact]
    public void RequestedSizeRerasterizesOriginalOutlinesAndPreservesReadIdentity()
    {
        var snapshot = Snapshot();
        var identity = snapshot.LayoutIdentity;
        string source = snapshot.SourceSha256;
        var geometry = snapshot.GetPageGeometry(0);
        var frame = snapshot.RenderPage(0, new PdfRenderOptions(32, 28));
        Assert.Equal(32, frame.Width);
        Assert.Equal(28, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.InRange(frame.Pixels[(10 * 32 + 14) * 4 + channel], 0.21413370f, 0.21413373f);
            Assert.InRange(frame.Pixels[(10 * 32 + 8) * 4 + channel],
                (float)(0.7379582286993577 - 0.000002), (float)(0.7379582286993577 + 0.000002));
        }
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((10 * 32 + 28) * 4, 4).ToArray());
        for (int alpha = 3; alpha < frame.Pixels.Length; alpha += 4)
            Assert.Equal(1f, frame.Pixels[alpha]);
        Assert.Same(geometry, snapshot.GetPageGeometry(0));
        Assert.Equal(16, geometry.Width);
        Assert.Equal(14, geometry.Height);
        Assert.Equal(96, geometry.DpiX);
        Assert.Equal(96, geometry.DpiY);
        Assert.Equal(identity, snapshot.LayoutIdentity);
        Assert.Equal(source, snapshot.SourceSha256);
    }

    [Fact]
    public void IndependentAxisScalingPreservesNaturalOutputAndLayoutPlacements()
    {
        var layout = EpubDocument.Open(Book()).CreateLayout(Options());
        var placements = layout.Pages[0].TextPlacements.ToArray();
        var natural = layout.RenderPage(0);
        var sizedNatural = layout.RenderPage(0, new PdfRenderOptions(16, 14));
        Assert.Equal(natural.Pixels.ToArray(), sizedNatural.Pixels.ToArray());
        var frame = layout.RenderPage(0, new PdfRenderOptions(48, 28));
        Assert.Equal(48, frame.Width);
        Assert.Equal(28, frame.Height);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[(10 * 48 + 21) * 4 + channel], 0.21413370f, 0.21413373f);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((5 * 48 + 7) * 4, 4).ToArray());
        Assert.Equal(placements, layout.Pages[0].TextPlacements);
        Assert.Equal(natural.Pixels.ToArray(), layout.RenderPage(0).Pixels.ToArray());
    }

    [Fact]
    public void CancellationPrecedesOptionsAndIndexValidation()
    {
        var snapshot = Snapshot();
        var layout = EpubDocument.Open(Book()).CreateLayout(Options());
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => snapshot.RenderPage(-1, null!, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(-1, null!, canceled));
        Assert.Throws<ArgumentNullException>(() => snapshot.RenderPage(-1, null!));
        Assert.Throws<ArgumentNullException>(() => layout.RenderPage(-1, null!));
        foreach (int index in new[] { -1, 1 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPage(index, new PdfRenderOptions(32, 28)));
            Assert.Throws<ArgumentOutOfRangeException>(() => layout.RenderPage(index, new PdfRenderOptions(32, 28)));
        }
    }

    [Fact]
    public void CallerBudgetCountsBothFloatBuffersBeforeAllocation()
    {
        var snapshot = Snapshot();
        var layout = EpubDocument.Open(Book()).CreateLayout(Options());
        var options = new PdfRenderOptions(32, 28, maximumPixelBytes: 10000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(() => snapshot.RenderPage(0, options));
        Assert.Throws<ArgumentException>(() => layout.RenderPage(0, options));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Rejected render allocated {allocated} bytes.");
        Assert.Equal(32, snapshot.RenderPage(0,
            new PdfRenderOptions(32, 28, maximumPixelBytes: 32 * 28 * 32)).Width);
        Assert.Throws<InvalidDataException>(() => snapshot.RenderPage(0, new PdfRenderOptions(4096, 1025)));
    }

    [Fact]
    public void NativeEpubRejectsSeparationPlatesAndRetainsIntrinsicWhitePaper()
    {
        var snapshot = Snapshot();
        var layout = EpubDocument.Open(Book()).CreateLayout(Options());
        var plate = new PdfRenderOptions(32, 28) { SeparationPlate = "Black" };
        Assert.Throws<NotSupportedException>(() => snapshot.RenderPage(0, plate));
        Assert.Throws<NotSupportedException>(() => layout.RenderPage(0, plate));
        var frame = snapshot.RenderPage(0, new PdfRenderOptions(32, 28, transparentBackground: true));
        Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((10 * 32 + 28) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(1, 3, 2)]
    [InlineData(2, 3, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(6, 2, 3)]
    public void SizedDjvuPreservesNaturalOrientationAndRejectsScalingBeforeDecode(byte orientation, int width, int height)
    {
        byte[] bytes = DjvuPage(orientation);
        var snapshot = PdfReadSnapshot.OpenDjvu(bytes);
        var natural = snapshot.RenderPage(0);
        var frame = snapshot.RenderPage(0, new PdfRenderOptions(width, height, maximumPixelBytes: 192));
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(natural.ColorEncoding, frame.ColorEncoding);
        Assert.Equal(natural.Pixels.ToArray(), frame.Pixels.ToArray());
        Assert.Equal(3, snapshot.GetPageGeometry(0).Width);
        Assert.Equal(2, snapshot.GetPageGeometry(0).Height);
        Assert.Throws<ArgumentException>(() => snapshot.RenderPage(0,
            new PdfRenderOptions(width, height, maximumPixelBytes: 50)));
        Assert.Throws<NotSupportedException>(() => snapshot.RenderPage(0,
            new PdfRenderOptions(width, height) { SeparationPlate = "Black" }));
        Assert.Throws<OperationCanceledException>(() => snapshot.RenderPage(-1, null!, new CancellationToken(true)));
        Assert.Throws<ArgumentNullException>(() => snapshot.RenderPage(-1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPage(-1, new PdfRenderOptions(width, height)));
        bytes[42] = 0;
        var malformed = PdfReadSnapshot.OpenDjvu(bytes);
        Assert.Throws<NotSupportedException>(() => malformed.RenderPage(0, new PdfRenderOptions(width * 2, height * 2)));
        Assert.Throws<InvalidDataException>(() => malformed.RenderPage(0, new PdfRenderOptions(width, height)));
    }

    [Fact]
    public void NonfiniteAxisScaleIsRejectedBeforeAllocatingTargetPixels()
    {
        var options = Options() with { PageWidth = 1e-310, PageHeight = 1e-310, FontSize = 1e-310, Margin = 0 };
        byte[] bytes = Book();
        var snapshot = PdfReadSnapshot.OpenEpub(bytes, options);
        var layout = EpubDocument.Open(bytes).CreateLayout(options);
        var renderOptions = new PdfRenderOptions(256, 256);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => snapshot.RenderPage(0, renderOptions));
        Assert.Throws<InvalidDataException>(() => layout.RenderPage(0, renderOptions));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Rejected scale allocated {allocated} bytes.");
    }

    private static byte[] DjvuPage(byte orientation) => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 0x2c, 1, 22, orientation]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }

    private static PdfReadSnapshot Snapshot() => PdfReadSnapshot.OpenEpub(Book(), Options());

    private static EpubLayoutOptions Options() => new(16, 14,
        TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true)))
    {
        FontSize = 10, Margin = 2, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001)
    };

    private static byte[] Book()
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
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-read</dc:identifier><dc:title>Native read</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native read</title></head><body><p>A</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native read</a></li></ol></nav></body></html>
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
