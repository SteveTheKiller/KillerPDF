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

public sealed class NativeWorkingDocumentTests
{
    [Fact]
    public void NativeEpubOwnerCopiesOriginalPackageAndRendersItsOwnedSourceWithPortableFont()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")
            ?? throw new InvalidOperationException("The portable font fixture is missing.");
        byte[] fontBytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(fontBytes);
        var options = new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes))
        {
            FontSize = 12, Margin = 4, TextColor = new PdfRgbColor(.5001, .5001, .5001)
        };
        byte[] source = Book();
        byte[] original = source.ToArray();
        var direct = PdfReadSnapshot.OpenEpub(original, options);
        using var owner = NativeWorkingDocument.OpenEpub(source, options);
        Array.Clear(source);
        using var copy = new MemoryStream();
        owner.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
        Assert.Equal(PdfDocumentFormat.Epub, owner.Format);
        Assert.Equal(direct.SourceSha256, owner.ContentIdentity);
        Assert.Equal(1, owner.PageCount);
        using var revision = owner.CaptureReadRevision();
        Assert.Equal(PdfDocumentFormat.Epub, revision.Format);
        Assert.Equal(owner.ContentIdentity, revision.ContentIdentity);
        Assert.Equal(60, revision.GetPageGeometry(0).Width);
        Assert.Equal(36, revision.GetPageGeometry(0).Height);
        using var lease = revision.Rent();
        var renderOptions = new PdfRenderOptions(160, 144);
        PdfRenderedFrame frame = lease.RenderFrame(0, renderOptions);
        PdfRenderedFrame expected = direct.RenderPage(0, renderOptions);
        Assert.Equal(160, frame.Width);
        Assert.Equal(144, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(expected.Pixels.ToArray(), frame.Pixels.ToArray());
        Assert.Contains(frame.Pixels.ToArray(), channel => channel < .99f);
        Assert.Throws<ArgumentNullException>(() => NativeWorkingDocument.OpenEpub(original, null!));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            NativeWorkingDocument.OpenEpub(original, null!, new CancellationToken(true)));
        Assert.Throws<InvalidDataException>(() => NativeWorkingDocument.OpenEpub(Array.Empty<byte>(), options));
    }

    [Fact]
    public void NativeOwnerPropagatesMalformedContainerAndOriginalCodecDiagnoses()
    {
        Assert.Throws<InvalidDataException>(() => NativeWorkingDocument.OpenDjvu(Array.Empty<byte>()));
        byte[] unsupported = DjvuPage();
        unsupported[28] = 99;
        using (var owner = NativeWorkingDocument.OpenDjvu(unsupported))
        using (var revision = owner.CaptureReadRevision())
        using (var lease = revision.Rent())
        {
            Assert.Throws<NotSupportedException>(() => lease.RenderFrame(0, new PdfRenderOptions(2, 3)));
            using var copy = new MemoryStream();
            owner.WriteCopyTo(copy);
            Assert.Equal(unsupported, copy.ToArray());
        }
        byte[] malformed = DjvuPage();
        malformed[42] = 0;
        using var malformedOwner = NativeWorkingDocument.OpenDjvu(malformed);
        using var malformedRevision = malformedOwner.CaptureReadRevision();
        using var malformedLease = malformedRevision.Rent();
        var error = Assert.Throws<InvalidDataException>(() => malformedLease.RenderFrame(0, new PdfRenderOptions(2, 3)));
        Assert.Equal("Invalid Smmr MMR header.", error.Message);
        using var retained = new MemoryStream();
        malformedOwner.WriteCopyTo(retained);
        Assert.Equal(malformed, retained.ToArray());
    }

    [Fact]
    public void NativeOwnerRejectsOversizedSourceBeforeCopyingAndChecksCancellationFirst()
    {
        byte[] oversized = new byte[65 * 1024 * 1024];
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => NativeWorkingDocument.OpenDjvu(oversized));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Rejected source allocated {allocated} bytes.");
        Assert.ThrowsAny<OperationCanceledException>(() =>
            NativeWorkingDocument.OpenDjvu(oversized, new CancellationToken(true)));
    }

    [Fact]
    public void SourceCopyHonorsCancellationAndStreamDiagnosesWithoutChangingTheOwner()
    {
        byte[] original = LargeDjvuPage();
        using var owner = NativeWorkingDocument.OpenDjvu(original);
        using var revision = owner.CaptureReadRevision();
        Assert.Throws<ArgumentNullException>(() => owner.WriteCopyTo(null!));
        using var readOnly = new MemoryStream(Array.Empty<byte>(), writable: false);
        Assert.Throws<ArgumentException>(() => owner.WriteCopyTo(readOnly));
        var canceled = new CancellationToken(true);
        Assert.ThrowsAny<OperationCanceledException>(() => owner.WriteCopyTo(null!, canceled));
        Assert.ThrowsAny<OperationCanceledException>(() => NativeWorkingDocument.OpenDjvu(Array.Empty<byte>(), canceled));
        using var failed = new CopyProbeStream(fail: true);
        Assert.Throws<IOException>(() => owner.WriteCopyTo(failed));
        using var caller = new CancellationTokenSource();
        using var interrupted = new CopyProbeStream(caller.Cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => owner.WriteCopyTo(interrupted, caller.Token));
        Assert.InRange(interrupted.MaximumWriteBytes, 1, 65536);
        Assert.InRange(interrupted.Length, 1, original.Length - 1);
        using var complete = new CopyProbeStream();
        owner.WriteCopyTo(complete);
        Assert.InRange(complete.MaximumWriteBytes, 1, 65536);
        Assert.Equal(original, complete.ToArray());
        Assert.Same(revision, owner.CaptureReadRevision());
        using var lease = revision.Rent();
        Assert.Equal(0f, lease.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[20]);
    }

    [Fact]
    public void NativeOwnerRecapturesRetiredReadsAndClosesWithoutInterruptingAcquiredCopy()
    {
        byte[] original = LargeDjvuPage();
        using var owner = NativeWorkingDocument.OpenDjvu(original);
        using var first = owner.CaptureReadRevision();
        Assert.Same(first, owner.CaptureReadRevision());
        using var oldLease = first.Rent();
        first.Dispose();
        using var second = owner.CaptureReadRevision();
        Assert.NotSame(first, second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
        Assert.Same(second, owner.CaptureReadRevision());
        Assert.ThrowsAny<OperationCanceledException>(() => oldLease.RenderFrame(0, new PdfRenderOptions(2, 3)));
        Assert.Throws<ObjectDisposedException>(() => first.Rent());
        using var held = second.Rent();
        Assert.Equal(0f, held.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[20]);
        using var acquiredCopy = new CopyProbeStream(owner.Close);
        owner.WriteCopyTo(acquiredCopy);
        Assert.Equal(original, acquiredCopy.ToArray());
        Assert.Throws<ObjectDisposedException>(() => owner.CaptureReadRevision());
        using var rejected = new MemoryStream();
        Assert.Throws<ObjectDisposedException>(() => owner.WriteCopyTo(rejected));
        Assert.Throws<ObjectDisposedException>(() => second.Rent());
        Assert.ThrowsAny<OperationCanceledException>(() => held.RenderFrame(0, new PdfRenderOptions(2, 3)));
        Assert.Equal(PdfDocumentFormat.Djvu, owner.Format);
        Assert.Equal(1, owner.PageCount);
        Assert.Equal(second.ContentIdentity, owner.ContentIdentity);
        owner.Close();
        owner.Dispose();
    }

    [Fact]
    public void NativeOwnerPreservesOriginalDjvuBytesAndRendersCapturedIntrinsicOrientation()
    {
        byte[] source = DjvuPage();
        byte[] original = source.ToArray();
        using var owner = NativeWorkingDocument.OpenDjvu(source);
        Array.Clear(source);
        using var copy = new MemoryStream();
        owner.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
        Assert.Equal(PdfDocumentFormat.Djvu, owner.Format);
        Assert.Equal(1, owner.PageCount);
        using var revision = owner.CaptureReadRevision();
        Assert.Equal(PdfDocumentFormat.Djvu, revision.Format);
        Assert.Equal(owner.ContentIdentity, revision.ContentIdentity);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), owner.ContentIdentity);
        Assert.Equal(1, revision.PageCount);
        var geometry = revision.GetPageGeometry(0);
        Assert.Equal(.72, geometry.Width);
        Assert.Equal(.48, geometry.Height);
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
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-owner</dc:identifier><dc:title>Native owner</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native owner</title></head><body><p>A</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native owner</a></li></ol></nav></body></html>
                """);
            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }

    private sealed class CopyProbeStream(Action? afterWrite = null, bool fail = false) : Stream
    {
        private readonly MemoryStream _output = new();
        internal int MaximumWriteBytes { get; private set; }
        internal byte[] ToArray() => _output.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _output.Length;
        public override long Position { get => _output.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (fail) throw new IOException("Synthetic stream failure.");
            MaximumWriteBytes = Math.Max(MaximumWriteBytes, buffer.Length);
            _output.Write(buffer);
            afterWrite?.Invoke();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _output.Dispose();
            base.Dispose(disposing);
        }
    }

    private static byte[] LargeDjvuPage() => [.. "AT&T"u8,
        .. Chunk("FORM", [.. DjvuPage()[12..],
            .. Chunk("TXTa", [2, 0, 0, .. Enumerable.Repeat((byte)'A', 131072), 1])])];

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
