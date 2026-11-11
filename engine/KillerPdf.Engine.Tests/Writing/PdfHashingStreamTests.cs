using System.Security.Cryptography;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

// An object serializes as many one-byte and short writes. Each reaches the hash through a native call, so the
// stream gathers them and hands the hash large spans, with the same digest as hashing the bytes at once.
public sealed class PdfHashingStreamTests
{
    [Fact]
    public void ByteAndShortWritesReachTheHashInFewCallsWithTheSameDigest()
    {
        var bytes = new byte[100_000];
        new Random(7).NextBytes(bytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var stream = new PdfHashingStream(hash);

        int index = 0;
        while (index < bytes.Length)
        {
            if (index % 3 == 0) { stream.WriteByte(bytes[index]); index++; }
            else
            {
                int count = Math.Min(bytes.Length - index, 1 + index % 7);
                stream.Write(bytes.AsSpan(index, count));
                index += count;
            }
        }
        stream.Flush();

        Assert.Equal(SHA256.HashData(bytes), hash.GetHashAndReset());
        Assert.InRange(stream.AppendCalls, 1, 100_000 / 4096);
    }

    [Fact]
    public void LargeWritesKeepTheirOrderAmongBufferedBytes()
    {
        var bytes = new byte[50_000];
        new Random(11).NextBytes(bytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var stream = new PdfHashingStream(hash);

        stream.WriteByte(bytes[0]);
        stream.Write(bytes, 1, 40_000);
        stream.WriteByte(bytes[40_001]);
        stream.Write(bytes.AsSpan(40_002));
        stream.Flush();

        Assert.Equal(SHA256.HashData(bytes), hash.GetHashAndReset());
    }
}
