using System.IO.Compression;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfStreamDecoderOwnershipTests
{
    [Theory]
    [InlineData("Crypt")]
    [InlineData("Unknown")]
    public void Decode_PassThroughReturnsIndependentWritableBytes(string filter)
    {
        var stream = new PdfStream(new PdfDictionary([
            new(new PdfName("Filter"u8), new PdfName(System.Text.Encoding.ASCII.GetBytes(filter)))]),
            new byte[] { 1, 2, 3 });
        byte[] decoded = PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream);
        decoded[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, stream.EncodedData.ToArray());
    }

    [Fact]
    public void Decode_FlateDoesNotDuplicateCompressedInput()
    {
        byte[] samples = new byte[8 * 1024 * 1024];
        new Random(912).NextBytes(samples);
        using var encoded = new MemoryStream();
        using (var compressor = new ZLibStream(encoded, CompressionLevel.NoCompression, leaveOpen: true))
            compressor.Write(samples);
        var stream = new PdfStream(new PdfDictionary([
            new(new PdfName("Filter"u8), new PdfName("FlateDecode"u8))]), encoded.ToArray());
        PdfStreamDecoder.Decode(stream);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] decoded = PdfStreamDecoder.Decode(stream);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Includes the 8 MiB output and bounded scratch blocks, but no input-sized copy.
        Assert.InRange(allocated, samples.Length, 20 * 1024 * 1024);
        Assert.Equal(samples, decoded);
    }
}
