using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Opening a native book captures the file once, for the page views and for Convert to PDF to read later. The engine
// reads from that same captured array rather than copying it again, so a large file is held once, not twice or more.
public sealed class NativeOwnedSourceTests
{
    private const int PayloadBytes = 4 * 1024 * 1024;

    [Fact]
    public void OpeningADjvuDocumentCopiesTheFileOnlyOnce()
    {
        byte[] source = DjvuWithPayload();
        using (NativeWorkingDocument.OpenDjvu(NativeWorkingDocumentTests.DjvuPage())) { }
        using (NativeWorkingDocument.OpenDjvu(source)) { }

        long before = GC.GetAllocatedBytesForCurrentThread();
        using var owner = NativeWorkingDocument.OpenDjvu(source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1, owner.PageCount);
        Assert.True(allocated < source.Length + 512 * 1024,
            $"Opening {source.Length} bytes allocated {allocated}; one captured copy is enough.");
    }

    [Fact]
    public void OpeningAnEpubCopiesThePackageOnceBeforeTheEngineTakesOver()
    {
        byte[] book = Padded(NativeWorkingDocumentTests.Book(), PayloadBytes);
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        var options = new EpubLayoutOptions(80, 48, font) { FontSize = 12, Margin = 4 };
        _ = PdfReadSnapshot.OpenEpubOwned((byte[])book.Clone(), options);
        using (NativeWorkingDocument.OpenEpub(book, options)) { }
        byte[] ownedByEngine = (byte[])book.Clone();

        long start = GC.GetAllocatedBytesForCurrentThread();
        _ = PdfReadSnapshot.OpenEpubOwned(ownedByEngine, options);
        long engineAlone = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        using var owner = NativeWorkingDocument.OpenEpub(book, options);
        long throughTheApp = GC.GetAllocatedBytesForCurrentThread() - start;

        // The app's own capture is the one extra copy; the engine adds none of its own.
        Assert.True(throughTheApp - engineAlone < book.Length * 11L / 10,
            $"Through the app {throughTheApp}, engine alone {engineAlone}, package {book.Length} bytes.");
    }

    private static byte[] DjvuWithPayload()
    {
        byte[] page = NativeWorkingDocumentTests.DjvuPage();
        byte[] source = new byte[page.Length + 8 + PayloadBytes];
        page.CopyTo(source, 0);
        "JUNK"u8.CopyTo(source.AsSpan(page.Length));
        BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(page.Length + 4), PayloadBytes);
        BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(8), source.Length - 12);
        return source;
    }

    private static byte[] Padded(byte[] book, int extra)
    {
        using var stream = new MemoryStream();
        stream.Write(book);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("EPUB/padding.bin", CompressionLevel.NoCompression).Open();
            entry.Write(new byte[extra]);
        }
        return stream.ToArray();
    }
}
