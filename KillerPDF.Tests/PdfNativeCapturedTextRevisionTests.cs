using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfNativeCapturedTextRevisionTests
{
    [Fact]
    public void PdfLeaseExplicitlyRejectsNativeTextAndStillHonorsCancellation()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(2, 1).Build());
        using var revision = new PdfCapturedReadRevision(document, "pdf-fixture");
        using var caller = new CancellationTokenSource();
        using var lease = revision.Rent(caller.Token);
        Assert.Throws<NotSupportedException>(() => lease.ReadNativeTextPage(0));
        Assert.Throws<NotSupportedException>(() => lease.ReadNativeTextPage(-1));
        caller.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => lease.ReadNativeTextPage(0));
    }

    [Fact]
    public void DisposedNativeTextLeaseReportsItsOwnLifetimeBeforeOtherValidation()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(DjvuPage()));
        using var caller = new CancellationTokenSource();
        var lease = revision.Rent(caller.Token);
        caller.Cancel();
        lease.Dispose();
        lease.Dispose();
        Assert.Equal("Lease", Assert.Throws<ObjectDisposedException>(() => lease.ReadNativeTextPage(-1)).ObjectName);
        Assert.Equal("Lease", Assert.Throws<ObjectDisposedException>(() => lease.ReadNativeTextPage(0)).ObjectName);
        using var next = revision.Rent();
        Assert.Equal("A\u00e9\U0001f600\n", next.ReadNativeTextPage(0).Text);
    }

    [Fact]
    public void RetiringOneNativeRevisionCancelsOnlyItsOwnTextLeases()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(DjvuPage());
        using var first = new PdfCapturedReadRevision(snapshot);
        using var second = new PdfCapturedReadRevision(snapshot);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
        using var retired = first.Rent();
        using var current = second.Rent();
        Assert.Equal("A\u00e9\U0001f600\n", retired.ReadNativeTextPage(0).Text);
        first.Dispose();
        Assert.ThrowsAny<OperationCanceledException>(() => retired.ReadNativeTextPage(-1));
        Assert.ThrowsAny<OperationCanceledException>(() => retired.ReadNativeTextPage(0));
        Assert.Throws<ObjectDisposedException>(() => first.Rent());
        Assert.Equal("A\u00e9\U0001f600\n", current.ReadNativeTextPage(0).Text);
        retired.Dispose();
        retired.Dispose();
        using var next = second.Rent();
        Assert.Equal("A\u00e9\U0001f600\n", next.ReadNativeTextPage(0).Text);
    }

    [Fact]
    public void NativeTextLeasePreservesPageValidationAndCallerCancellationPrecedence()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(DjvuPage()));
        using var caller = new CancellationTokenSource();
        using (var lease = revision.Rent(caller.Token))
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadNativeTextPage(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => lease.ReadNativeTextPage(1));
            caller.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => lease.ReadNativeTextPage(-1));
            Assert.ThrowsAny<OperationCanceledException>(() => lease.ReadNativeTextPage(0));
        }
        Assert.ThrowsAny<OperationCanceledException>(() => revision.Rent(caller.Token));
        using var next = revision.Rent();
        Assert.Equal("A\u00e9\U0001f600\n", next.ReadNativeTextPage(0).Text);
    }

    [Fact]
    public void CapturedEpubLeaseReadsOriginalTextAndPageLocalRegions()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        byte[] fontBytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(fontBytes);
        byte[] source = Book();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenEpub(source,
            new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes)) { FontSize = 12, Margin = 4 }));
        Array.Clear(source);
        using var lease = revision.Rent();
        var page = lease.ReadNativeTextPage(0);
        Assert.Equal("A", page.Text);
        var region = Assert.Single(page.Regions);
        Assert.Equal((0, 1), (region.Start, region.Length));
        Assert.Equal(3, region.Bounds.Left);
        Assert.True(region.Bounds.Right > region.Bounds.Left);
        Assert.True(region.Bounds.Top > region.Bounds.Bottom);
    }

    [Fact]
    public void CapturedDjvuLeaseReadsLiteralTextAndUnrotatedPointRegion()
    {
        byte[] source = DjvuPage();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(source));
        Array.Clear(source);
        using var lease = revision.Rent();
        var page = lease.ReadNativeTextPage(0);
        Assert.Equal("A\u00e9\U0001f600\n", page.Text);
        var region = Assert.Single(page.Regions);
        Assert.Equal(1, region.Start);
        Assert.Equal(3, region.Length);
        Assert.Equal(2.4, region.Bounds.Left, 12);
        Assert.Equal(33.6, region.Bounds.Bottom, 12);
        Assert.Equal(9.6, region.Bounds.Right, 12);
        Assert.Equal(43.2, region.Bounds.Top, 12);
    }

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
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:captured-text</dc:identifier><dc:title>Captured text</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-05T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Captured text</title></head><body><p>A</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Captured text</a></li></ol></nav></body></html>
                """);
            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }

    private static byte[] DjvuPage() => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 5]),
            .. Chunk("TXTa", [0, 0, 8, 65, 0xc3, 0xa9, 0xf0, 0x9f, 0x98, 0x80, 10, 1,
                .. Zone(1, 0, 0, 100, 200, 0, 8, 1), .. Zone(6, 10, 20, 30, 40, 1, 6)])])];

    private static byte[] Zone(byte type, int left, int bottom, int width, int height,
        int start, int length, int children = 0)
    {
        var bytes = new byte[17];
        bytes[0] = type;
        int[] values = [left, bottom, width, height, start];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1 + index * 2),
                checked((ushort)(values[index] + 0x8000)));
        bytes[11] = (byte)(length >> 16); bytes[12] = (byte)(length >> 8); bytes[13] = (byte)length;
        bytes[14] = (byte)(children >> 16); bytes[15] = (byte)(children >> 8); bytes[16] = (byte)children;
        return bytes;
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. length, .. payload,
            .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }
}
