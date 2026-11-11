using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// A region render draws page P as if the whole page were W x H and keeps only the requested rectangle, so a viewer
// can show the visible part of a book page at device resolution without paying for the whole page.
public sealed class PdfNativeRegionRenderTests
{
    private const int FullWidth = 480, FullHeight = 640;

    [Theory]
    [InlineData(0, 0, FullWidth, FullHeight)]
    [InlineData(0, 0, 37, 53)]
    [InlineData(101, 77, 64, 90)]
    [InlineData(FullWidth - 37, FullHeight - 70, 37, 70)]
    [InlineData(5, 300, 470, 1)]
    public void EpubRegionIsTheMatchingCropOfTheFullPageRender(int left, int top, int width, int height)
    {
        var snapshot = PdfReadSnapshot.OpenEpub(Book(), Options());
        var full = snapshot.RenderPage(0, new PdfRenderOptions(FullWidth, FullHeight));
        var region = snapshot.RenderPageRegion(0, new PdfRenderOptions(FullWidth, FullHeight), left, top, width, height);
        AssertCrop(full, region, left, top, width, height);
    }

    [Fact]
    public void EpubFixturePaintsTextImageAndBackgroundAcrossThePage()
    {
        // The crop checks above only mean something when the page draws every kind of paint.
        var full = PdfReadSnapshot.OpenEpub(Book(), Options()).RenderPage(0, new PdfRenderOptions(FullWidth, FullHeight));
        var pixels = full.Pixels;
        bool dark = false, red = false, background = false;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            float r = pixels[offset], g = pixels[offset + 1], b = pixels[offset + 2];
            dark |= r < .05f && g < .05f && b < .05f;
            red |= r > .9f && g < .05f && b < .05f;
            background |= Math.Abs(b - g) > .05f && r < g;
        }
        Assert.True(dark && red && background, $"text {dark}, image {red}, background {background}");
    }

    [Fact]
    public void EpubRegionOfAPageTooLargeToDrawWholeRendersWithMemoryForTheRegionOnly()
    {
        var snapshot = PdfReadSnapshot.OpenEpub(Book(), Options());
        // 6144 x 8192 is 806 MB as float pixels, past every native page limit.
        var huge = new PdfRenderOptions(6144, 8192);
        Assert.ThrowsAny<Exception>(() => snapshot.RenderPage(0, huge));
        var small = snapshot.RenderPageRegion(0, new PdfRenderOptions(480, 640), 20, 20, 30, 40);
        // Aim the region at the first ink of the 480 x 640 render, 12.8 times larger on the huge page.
        var preview = snapshot.RenderPage(0, new PdfRenderOptions(480, 640)).Pixels.ToArray();
        int ink = Array.FindIndex(preview, value => value < .05f) / 4;
        int inkLeft = Math.Clamp((int)(ink % 480 * 12.8) - 192, 0, 6144 - 384);
        int inkTop = Math.Clamp((int)(ink / 480 * 12.8) - 64, 0, 8192 - 384);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var region = snapshot.RenderPageRegion(0, huge, inkLeft, inkTop, 384, 384);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(384, region.Width);
        Assert.Equal(384, region.Height);
        // The region's float frame is 2.25 MiB; allow working buffers but nothing near the whole page.
        Assert.True(allocated < 24L << 20, $"A 384 x 384 region of a 6144 x 8192 page allocated {allocated} bytes.");
        // The scaled-up region still holds ink: text drawn at the requested scale, not a blank frame.
        Assert.Contains(region.Pixels.ToArray(), value => value < .05f);
        Assert.Equal(30, small.Width);
    }

    [Fact]
    public void EpubRegionPaysOnlyForTheTextInsideIt()
    {
        // A page full of small text, magnified a hundred times; the region covers one corner of the top margin.
        string text = string.Concat(Enumerable.Repeat("<p>AAAA AAA AA AAAA A AAA AAAA AA</p>", 40));
        var snapshot = PdfReadSnapshot.OpenEpub(Book(text), Options() with { FontSize = 2, Margin = 4 });
        var huge = new PdfRenderOptions(6000, 8000);
        var corner = snapshot.RenderPageRegion(0, huge, 0, 0, 256, 256);
        // Only the light blue page background there (its darkest channel is red at about 0.35 linear), no black text.
        Assert.DoesNotContain(corner.Pixels.ToArray(), value => value < .3f);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        snapshot.RenderPageRegion(0, huge, 0, 0, 256, 256);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The region's float frame is 1 MiB; the glyphs drawn elsewhere on the page must cost nothing here.
        Assert.True(allocated < 3L << 20, $"A 256 x 256 margin region allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, -1, 10, 10)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(FullWidth - 9, 0, 10, 10)]
    [InlineData(0, FullHeight - 9, 10, 10)]
    public void RegionsOutsideThePageOrEmptyAreRejected(int left, int top, int width, int height)
    {
        var options = new PdfRenderOptions(FullWidth, FullHeight);
        var epub = PdfReadSnapshot.OpenEpub(Book(), Options());
        var djvu = PdfReadSnapshot.OpenDjvu(SmmrPage(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => epub.RenderPageRegion(0, options, left, top, width, height));
        Assert.Throws<ArgumentOutOfRangeException>(() => djvu.RenderPageRegion(0, options, left, top, width, height));
    }

    [Fact]
    public void RegionRequestsCheckPageOptionsAndCancellationFirst()
    {
        var options = new PdfRenderOptions(FullWidth, FullHeight);
        foreach (var snapshot in new[] { PdfReadSnapshot.OpenEpub(Book(), Options()), PdfReadSnapshot.OpenDjvu(SmmrPage(5)) })
        {
            Assert.Throws<OperationCanceledException>(() => snapshot.RenderPageRegion(-1, null!, 0, 0, 1, 1, new CancellationToken(true)));
            Assert.Throws<ArgumentNullException>(() => snapshot.RenderPageRegion(0, null!, 0, 0, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPageRegion(1, options, 0, 0, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPageRegion(-1, options, 0, 0, 1, 1));
            Assert.Throws<NotSupportedException>(() => snapshot.RenderPageRegion(0,
                new PdfRenderOptions(FullWidth, FullHeight) { SeparationPlate = "Black" }, 0, 0, 1, 1));
        }
    }

    // Orientation 1 is upright, 2 upside down, 5 counterclockwise and 6 clockwise; the 3 x 2 mask turns with it.
    [Theory]
    [InlineData((byte)1, 24, 16, 0, 0, 24, 16)]
    [InlineData((byte)1, 24, 16, 5, 3, 11, 9)]
    [InlineData((byte)2, 24, 16, 13, 7, 11, 9)]
    [InlineData((byte)5, 16, 24, 0, 11, 16, 13)]
    [InlineData((byte)6, 16, 24, 7, 0, 9, 24)]
    [InlineData((byte)6, 2, 3, 1, 1, 1, 2)]
    public void DjvuBitonalRegionIsTheMatchingCropOfTheFullPageRender(byte orientation,
        int pageWidth, int pageHeight, int left, int top, int width, int height)
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(SmmrPage(orientation));
        var full = snapshot.RenderPage(0, new PdfRenderOptions(pageWidth, pageHeight));
        var region = snapshot.RenderPageRegion(0, new PdfRenderOptions(pageWidth, pageHeight), left, top, width, height);
        AssertCrop(full, region, left, top, width, height);
    }

    [Theory]
    [InlineData(16, 16, 3, 5, 9, 7)]
    [InlineData(3, 3, 1, 0, 2, 3)]
    [InlineData(4, 4, 0, 0, 4, 4)]
    public void DjvuGrayscaleRegionIsTheMatchingCropOfTheFullPageRender(int pageWidth, int pageHeight,
        int left, int top, int width, int height)
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(GrayRamp);
        var full = snapshot.RenderPage(0, new PdfRenderOptions(pageWidth, pageHeight));
        var region = snapshot.RenderPageRegion(0, new PdfRenderOptions(pageWidth, pageHeight), left, top, width, height);
        AssertCrop(full, region, left, top, width, height);
    }

    [Fact]
    public void DjvuRegionOfAPageTooLargeToDrawWholeRendersWithMemoryForTheRegionOnly()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(SmmrPage(1));
        // 16000 x 8000 is 2 GB as float pixels, past the storage and resampling work limits.
        var huge = new PdfRenderOptions(16000, 8000);
        Assert.ThrowsAny<Exception>(() => snapshot.RenderPage(0, huge));
        snapshot.RenderPageRegion(0, new PdfRenderOptions(24, 16), 0, 0, 1, 1);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var region = snapshot.RenderPageRegion(0, huge, 15960, 7970, 40, 30);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(40, region.Width);
        Assert.Equal(30, region.Height);
        Assert.True(allocated < 1L << 20, $"A 40 x 30 region of a 16000 x 8000 page allocated {allocated} bytes.");
        // Bilinear centers run from x = 15960.5 * 3 / 16000 - .5 = 2.49 to 2.4999 and y = 1.49 to 1.4999, so both taps
        // of every region pixel clamp onto the natural mask's bottom-right pixel (2, 1).
        float corner = snapshot.RenderPage(0).Pixels[(1 * 3 + 2) * 4];
        var pixels = region.Pixels;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            Assert.Equal(corner, pixels[offset]);
    }

    private static void AssertCrop(PdfRenderedFrame full, PdfRenderedFrame region, int left, int top, int width, int height)
    {
        Assert.Equal(width, region.Width);
        Assert.Equal(height, region.Height);
        Assert.Equal(full.ColorEncoding, region.ColorEncoding);
        var source = full.Pixels;
        var crop = region.Pixels;
        for (int y = 0; y < height; y++)
        {
            var expected = source.Slice(((top + y) * full.Width + left) * 4, width * 4);
            var actual = crop.Slice(y * width * 4, width * 4);
            if (!expected.SequenceEqual(actual))
                Assert.Fail($"Row {top + y} of the region differs from the full page render.");
        }
    }

    // DjVuLibre's c44 output for a 4 x 4 gray ramp at gamma 2.2 (djvu-reference-fixture calibration-ramp-gamma22).
    private static readonly byte[] GrayRamp = Convert.FromHexString(
        "41542654464f524d00000039444a5655494e464f0000000a00040004180064001601424734340000001b006481020004000400ffd5a537f9348f11cfc657f6acd695fca23c");

    private static byte[] SmmrPage(byte orientation) => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 0x2c, 1, 22, orientation]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }

    // 60 x 80 CSS pixels, so the 480 x 640 full render is eight times natural size.
    internal static EpubLayoutOptions Options() => new(60, 80,
        TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true))) { FontSize = 10, Margin = 4 };

    private const string RegionBody = """<p>AA A AAA</p><p><img src="red.png" alt="" style="width:12px;height:12px"/></p><p style="color:#000000">A AA</p>""";

    internal static byte[] Book(string body = RegionBody)
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
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:region</dc:identifier><dc:title>Region</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-10T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="image" href="red.png" media-type="image/png"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", """<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Region</title></head><body style="background-color:#a0c0f0">"""
                + body + "</body></html>");
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Region</a></li></ol></nav></body></html>
                """);
            using (var image = zip.CreateEntry("EPUB/red.png", CompressionLevel.NoCompression).Open())
                image.Write(RedPng());
            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }

    // A 2 x 2 opaque red RGB PNG.
    private static byte[] RedPng()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk("IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            zlib.Write(new byte[] { 0, 255, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255, 0, 0 });
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return output.ToArray();

        void Chunk(string type, byte[] data)
        {
            Span<byte> integer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length);
            output.Write(integer);
            byte[] name = Encoding.ASCII.GetBytes(type);
            output.Write(name);
            output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
            output.Write(integer);
        }
    }
}
