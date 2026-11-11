using System.Buffers.Binary;
using System.Security.Cryptography;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDjvuSnapshotOwnershipTests
{
    [Fact]
    public void OpeningADjvuSnapshotCapturesTheInputOnceAndKeepsItsIdentityAfterTheCallerChangesIt()
    {
        byte[] small = PdfDjvuScanResolutionTests.HalfBlackPage(16, 16, 300, 1);
        _ = PdfReadSnapshot.OpenDjvu(small);
        const int payloadBytes = 4 * 1024 * 1024;
        byte[] source = new byte[small.Length + 8 + payloadBytes];
        small.CopyTo(source, 0);
        "JUNK"u8.CopyTo(source.AsSpan(small.Length));
        BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(small.Length + 4), payloadBytes);
        BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(8), source.Length - 12);
        string expectedHash = Convert.ToHexString(SHA256.HashData(source));

        long before = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = PdfReadSnapshot.OpenDjvu(source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expectedHash, snapshot.SourceSha256);
        Array.Clear(source);
        Assert.Equal(expectedHash, snapshot.SourceSha256);
        Assert.Equal(1, snapshot.PageCount);
        Assert.True(allocated < source.Length + 256 * 1024,
            $"Opening {source.Length} bytes allocated {allocated}; only one owned source copy is needed.");
    }
}
