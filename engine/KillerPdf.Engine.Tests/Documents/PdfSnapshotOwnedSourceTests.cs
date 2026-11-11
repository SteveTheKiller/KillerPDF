using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// A caller that already holds a private copy of the file (the app captures one when it opens a book) hands it over
// instead of paying for another full copy: a 64 MiB DjVu or 128 MiB EPUB would otherwise be held twice.
public sealed class PdfSnapshotOwnedSourceTests
{
    private const int PayloadBytes = 4 * 1024 * 1024;

    [Fact]
    public void ADjvuSnapshotOpenedFromAnOwnedArrayDoesNotCopyIt()
    {
        byte[] small = PdfDjvuScanResolutionTests.HalfBlackPage(16, 16, 300, 1);
        _ = PdfReadSnapshot.OpenDjvu(small);
        byte[] source = new byte[small.Length + 8 + PayloadBytes];
        small.CopyTo(source, 0);
        "JUNK"u8.CopyTo(source.AsSpan(small.Length));
        BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(small.Length + 4), PayloadBytes);
        BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(8), source.Length - 12);
        string expectedHash = Convert.ToHexString(SHA256.HashData(source));
        _ = PdfReadSnapshot.OpenDjvuOwned((byte[])source.Clone());
        byte[] owned = (byte[])source.Clone();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = PdfReadSnapshot.OpenDjvuOwned(owned);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expectedHash, snapshot.SourceSha256);
        Assert.Equal(1, snapshot.PageCount);
        Assert.True(allocated < PayloadBytes / 4, $"Opening an owned {source.Length} byte array allocated {allocated}.");
    }

    [Fact]
    public void AnEpubSnapshotOpenedFromAnOwnedArrayCopiesItOneTimeLessThanOpeningFromMemory()
    {
        byte[] book = Padded(PdfNativeRegionRenderTests.Book(), PayloadBytes);
        var options = PdfNativeRegionRenderTests.Options();
        _ = PdfReadSnapshot.OpenEpub(book, options);
        byte[] owned = (byte[])book.Clone();
        _ = PdfReadSnapshot.OpenEpubOwned((byte[])book.Clone(), options);

        long start = GC.GetAllocatedBytesForCurrentThread();
        var copied = PdfReadSnapshot.OpenEpub(book, options);
        long copyingCost = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        var taken = PdfReadSnapshot.OpenEpubOwned(owned, options);
        long owningCost = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(copied.SourceSha256, taken.SourceSha256);
        Assert.Equal(copied.PageCount, taken.PageCount);
        Assert.True(copyingCost - owningCost >= book.Length * 9L / 10,
            $"Opening from memory allocated {copyingCost}, from an owned array {owningCost}, for a {book.Length} byte package.");
    }

    // The book with an unreferenced stored entry that makes the package <paramref name="extra"/> bytes larger.
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
