using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfNativeCapturedReadRevisionTests
{
    [Fact]
    public void NativeEpubLeaseMatchesDirectSizedRenderingWithPortableFont()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")
            ?? throw new InvalidOperationException("The portable font fixture is missing.");
        byte[] fontBytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(fontBytes);
        var font = TrueTypeFont.Load(fontBytes);
        var snapshot = PdfReadSnapshot.OpenEpub(Book(), new EpubLayoutOptions(80, 48, font)
        {
            FontSize = 12, Margin = 4, TextColor = new PdfRgbColor(.5001, .5001, .5001)
        });
        using var revision = new PdfCapturedReadRevision(snapshot);
        Assert.Equal(PdfDocumentFormat.Epub, revision.Format);
        Assert.Equal(snapshot.SourceSha256, revision.ContentIdentity);
        Assert.Equal(1, revision.PageCount);
        Assert.Equal(60, revision.GetPageGeometry(0).Width);
        Assert.Equal(36, revision.GetPageGeometry(0).Height);
        Assert.Equal(0, revision.GetPageGeometry(0).Rotation);
        var options = new PdfRenderOptions(160, 144);
        PdfRenderedFrame direct = snapshot.RenderPage(0, options);
        using var lease = revision.Rent();
        PdfRenderedFrame frame = lease.RenderFrame(0, options);
        Assert.Equal(160, frame.Width);
        Assert.Equal(144, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(direct.ColorEncoding, frame.ColorEncoding);
        Assert.Equal(direct.Pixels.ToArray(), frame.Pixels.ToArray());
        Assert.Contains(frame.Pixels.ToArray(), channel => channel < .99f);
    }

    [Fact]
    public void NativeDjvuLeaseForwardsSizedRenderingAndCallerPixelBudget()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(DjvuPage()));
        using var lease = revision.Rent();
        var scaled = lease.RenderFrame(0, new PdfRenderOptions(4, 6));
        Assert.Equal(4, scaled.Width);
        Assert.Equal(6, scaled.Height);
        Assert.Equal(.4375f, scaled.Pixels[(2 * 4 + 2) * 4]);
        Assert.Throws<ArgumentException>(() => lease.RenderFrame(0,
            new PdfRenderOptions(2, 3, maximumPixelBytes: 50)));
        // The 3 x 2 mask is held as two one-byte rows and a two-entry float table (10 bytes); the 6-pixel float frame
        // is built and then copied (192 bytes).
        PdfRenderedFrame frame = lease.RenderFrame(0, new PdfRenderOptions(2, 3, maximumPixelBytes: 202));
        Assert.Equal(2, frame.Width);
        Assert.Equal(3, frame.Height);
        Assert.Equal(0f, frame.Pixels[20]);
    }

    [Fact]
    public void NativeLeasesPreserveCallerCancellationAndArgumentValidation()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(DjvuPage()));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => revision.Rent(canceled.Token));
        using (var lease = revision.Rent())
        {
            Assert.Throws<ArgumentNullException>(() => lease.RenderFrame(0, null!));
            foreach (int index in new[] { -1, 1 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => revision.GetPageGeometry(index));
                Assert.Throws<ArgumentOutOfRangeException>(() => lease.RenderFrame(index, new PdfRenderOptions(2, 3)));
            }
        }
        using (var caller = new CancellationTokenSource())
        using (var lease = revision.Rent(caller.Token))
        {
            caller.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => lease.RenderFrame(-1, null!));
        }
        using var next = revision.Rent();
        Assert.Equal(0f, next.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[20]);
    }

    [Fact]
    public void IndependentNativeRevisionsRetireOnlyTheirOwnLeases()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(DjvuPage());
        using var first = new PdfCapturedReadRevision(snapshot);
        using var second = new PdfCapturedReadRevision(snapshot);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
        using var oldLease = first.Rent();
        using (var current = second.Rent())
        {
            Assert.Equal(0f, oldLease.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[20]);
            first.Dispose();
            Assert.ThrowsAny<OperationCanceledException>(() => oldLease.RenderFrame(0, new PdfRenderOptions(2, 3)));
            Assert.Throws<ObjectDisposedException>(() => first.Rent());
            oldLease.Dispose();
            oldLease.Dispose();
            Assert.Equal(0f, current.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[20]);
        }
        using var next = second.Rent();
        Assert.Equal(1f, next.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[0]);
        Assert.Equal(0f, next.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[20]);
        first.Dispose();
    }

    [Fact]
    public void CapturedNativeDjvuKeepsPointGeometryAndRendersIntrinsicRotationOnce()
    {
        byte[] bytes = DjvuPage();
        var snapshot = PdfReadSnapshot.OpenDjvu(bytes);
        using var revision = new PdfCapturedReadRevision(snapshot);
        Array.Clear(bytes);
        Assert.Equal(PdfDocumentFormat.Djvu, revision.Format);
        Assert.Equal(snapshot.SourceSha256, revision.ContentIdentity);
        Assert.NotEqual(Guid.Empty, revision.Id);
        Assert.Equal(1, revision.PageCount);
        var geometry = revision.GetPageGeometry(0);
        Assert.Equal(0, geometry.Left);
        Assert.Equal(0, geometry.Bottom);
        Assert.Equal(.72, geometry.Width);
        Assert.Equal(.48, geometry.Height);
        Assert.Equal(1, geometry.UserUnit);
        Assert.Equal(90, geometry.Rotation);
        using var lease = revision.Rent();
        PdfRenderedFrame frame = lease.RenderFrame(0, new PdfRenderOptions(2, 3));
        Assert.Equal(2, frame.Width);
        Assert.Equal(3, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        float[] expected = [1, 1, 1, 0, 1, 0];
        for (int pixel = 0; pixel < 6; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
                Assert.Equal(expected[pixel], frame.Pixels[pixel * 4 + channel]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
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
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:captured-native</dc:identifier><dc:title>Captured native</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Captured native</title></head><body><p>A</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Captured native</a></li></ol></nav></body></html>
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
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 0x2c, 1, 22, 5]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }
}
