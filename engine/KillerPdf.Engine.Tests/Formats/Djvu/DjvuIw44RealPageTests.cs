using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// Real IW44 images come from c44, whose default output is 99 slices in three chunks (74, 15 and 10). Letter and A4
// pages are 2550 x 3300 at 300 dpi and 5100 x 6600 or 4960 x 7016 at 600 dpi. These pages carry c44's chunk shape over
// pseudo-random arithmetic data, which activates far more coefficients than a scan does. The expected hashes are the
// pixel bytes DjVuLibre 3.5.29 ddjvu wrote for the same files (PGM or PPM data after the header).
public sealed class DjvuIw44RealPageTests
{
    // A decode keeps 2 bytes per coefficient (the 32-pixel-padded page) and writes 1 gray byte per pixel; the chunk
    // copies add a quarter byte per pixel. 5100 x 6600: 5120 x 6624 x 2 + 33,660,000 + 8,415,000 = 109.9 MB.
    [Theory]
    [InlineData(2550, 3300, 1u, "878EAB4CBA55A3EC82D9E26D0D5CB6D11551ED998B65D5BEA43E1B0C39FB0E92", 30)]
    [InlineData(5100, 6600, 2u, "1EB131ACC457F1F01C23825DF88A55C7D303B72246A54D425AD5D147694908A9", 112)]
    [InlineData(4960, 7016, 3u, "D323035F33D62FAECE23B4BCE3D0081461021CE0F210BCEFFD8B3688E21BE8D4", 116)]
    public void ARealSliceCountGrayscalePageDecodesLikeTheReferenceDecoder(int width, int height, uint seed, string expected,
        int allocationMiB)
    {
        var chunks = CodedChunks(color: false, width, height, crcb: 0, seed, payloadDivisor: 4);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        byte[] gray = DjvuIw44Decoder.DecodeGrayscale(chunks, width, height, CancellationToken.None, callerBytesPerPixel: 0);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(gray)));
        Assert.True(allocated < (long)allocationMiB << 20, $"A {width} x {height} gray decode allocated {allocated} bytes.");
        // About 1 second for a 600 dpi page on the development machine; the bound leaves room for a loaded test host.
        Assert.True(watch.ElapsedMilliseconds < 15_000, $"A {width} x {height} gray decode took {watch.ElapsedMilliseconds} ms.");
    }

    // c44's default color coding is full-resolution chroma that starts 10 slices after luminance (delay byte 0x8a);
    // -crcbhalf codes half-resolution chroma with the same delay (0x0a). A decode allocates 2 bytes per padded
    // coefficient for each of three components, three signed planes and the RGB output (6 bytes per pixel), and the
    // chunk copies. 5100 x 6600: 3 x 5120 x 6624 x 2 + 6 x 33,660,000 + 16,830,000 = 422.0 MB.
    [Theory]
    [InlineData(2550, 3300, 0x8a, 4u, 2, "DADF6149D466C3706A57FD1D9D97CB0522189ECD3959B69D776932148DC058FE", 112)]
    [InlineData(2550, 3300, 0x0a, 5u, 1, "2AAB2502080CC8F140EA6687D9CD503B7DAC6519EBD7D1858B32140940C2FB29", 112)]
    [InlineData(5100, 6600, 0x8a, 6u, 2, "BA5141B1A0D59A32EC386CC24F3129C9AD5C815680EE20992A396E0070DEC7F1", 410)]
    [InlineData(4960, 7016, 0x8a, 7u, 2, "C58DF7FB960FCEE3B09D801ABEBD2127478C55CD240D10A9CC7AB784FA0E239D", 424)]
    public void ARealSliceCountColorImageDecodesLikeTheReferenceDecoder(int width, int height, int crcb, uint seed, int payloadDivisor,
        string expected, int allocationMiB)
    {
        var chunks = CodedChunks(color: true, width, height, (byte)crcb, seed, payloadDivisor);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        byte[] rgb = DjvuIw44Decoder.DecodeColor(chunks, width, height, CancellationToken.None, callerBytesPerPixel: 0);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(rgb)));
        Assert.True(allocated < (long)allocationMiB << 20, $"A {width} x {height} color decode allocated {allocated} bytes.");
        Assert.True(watch.ElapsedMilliseconds < 30_000, $"A {width} x {height} color decode took {watch.ElapsedMilliseconds} ms.");
    }

    // The viewer draws a 600 dpi gray page's thumbnail inside its 128 MiB budget: the decode holds 2 bytes per padded
    // coefficient, a block row, 1 gray byte per pixel and the 8.4 MB of chunks, 110.4 MB in all.
    [Fact]
    public void A600DpiGrayscalePageDrawsAsAThumbnailInsideTheViewerBudget()
    {
        var chunks = CodedChunks(color: false, 5100, 6600, crcb: 0, 2u, payloadDivisor: 4);
        byte[] info = [5100 >> 8, 5100 & 0xff, 6600 >> 8, 6600 & 0xff, 24, 0, 0x58, 0x02, 22, 1];
        var form = new List<byte>("DJVU"u8.ToArray());
        form.AddRange(Chunk("INFO", info));
        foreach (var chunk in chunks) form.AddRange(Chunk("BG44", chunk.ToArray()));
        var snapshot = KillerPdf.Engine.Documents.PdfReadSnapshot.OpenDjvu((byte[])[.. "AT&T"u8, .. Chunk("FORM", [.. form])]);
        var thumb = snapshot.RenderPage(0, new KillerPdf.Engine.Rendering.PdfRenderOptions(396, 512, maximumPixelBytes: 128L << 20));
        Assert.Equal(396, thumb.Width);
        Assert.Equal(512, thumb.Height);
    }

    // c44 -dpi 100 of an owned 211 x 149 scan-like color page (paper grain, text strokes, a red heading, a photo
    // band) with its defaults: 74 + 15 + 10 slices, full-resolution chroma delayed 10 slices, dimensions that are
    // not whole blocks. ddjvu's PPM data for it has SHA-256 72A9F1AE...
    [Fact]
    public void ARealC44ColorPageDecodesLikeTheReferenceDecoder()
    {
        byte[] file = Convert.FromBase64String(OwnedC44Color);
        Assert.Equal("C090CFA05081EFD01C42390466983D84560800981BAE02E111EC0298D35D73E6", Convert.ToHexString(SHA256.HashData(file)));
        var chunks = new List<ReadOnlyMemory<byte>>();
        for (int position = 16; position + 8 <= file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(position + 4));
            if (file.AsSpan(position, 4).SequenceEqual("BG44"u8)) chunks.Add(file.AsMemory(position + 8, length));
            position += 8 + length + (length & 1);
        }
        Assert.Equal(3, chunks.Count);
        byte[] rgb = DjvuIw44Decoder.DecodeColor(chunks, 211, 149, CancellationToken.None, callerBytesPerPixel: 0);
        Assert.Equal("72A9F1AEFD676F567D4922AEABE4711C803B1679D5BB8FBB817526C7FE54A98E", Convert.ToHexString(SHA256.HashData(rgb)));
    }

    // Refusals come from the headers alone, before any image buffer. A 65535 x 65535 gray page needs 12.9 GB; 17 chunks
    // of 255 slices exceed the 4096-slice limit; a 7000 x 7000 color page needs 343 MB at its peak. A 4800 x 6048
    // color page fits in 203 MB but 200 slices of all three components make 2.06 G coefficient visits and
    // reconstruction steps, over the 2 G work limit (4800 x 6016 is just under it).
    [Theory]
    [InlineData(false, 65535, 65535, 99, 1)]
    [InlineData(false, 64, 64, 255, 17)]
    [InlineData(true, 7000, 7000, 99, 1)]
    [InlineData(true, 4800, 6048, 200, 1)]
    public void HostileSizesAndSliceCountsFailBeforeDecoding(bool color, int width, int height, int slices, int chunkCount)
    {
        var chunks = new List<ReadOnlyMemory<byte>>();
        for (int serial = 0; serial < chunkCount; serial++)
            chunks.Add(serial == 0
                ? new byte[] { 0, (byte)slices, (byte)(color ? 1 : 0x81), 2, (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 0x80, 0x55, 0xaa }
                : new byte[] { (byte)serial, (byte)slices, 0x55, 0xaa });
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<InvalidDataException>(() => color
            ? DjvuIw44Decoder.DecodeColor(chunks, width, height, CancellationToken.None, callerBytesPerPixel: 0)
            : DjvuIw44Decoder.DecodeGrayscale(chunks, width, height, CancellationToken.None, callerBytesPerPixel: 0));
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("combined storage or coefficient-work limit", error.Message);
        Assert.InRange(allocated, 0, (1 << 20) - 1);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"The refusal took {watch.ElapsedMilliseconds} ms.");
    }

    // The largest color decode the limits admit takes about 14 seconds on the development machine; it stops within a
    // block row of work once cancelled.
    [Fact]
    public void TheLargestAdmittedDecodeStopsWhenCancelled()
    {
        var chunks = CodedChunks(color: true, 4800, 6016, crcb: 0x80, 8u, payloadDivisor: 8);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            DjvuIw44Decoder.DecodeColor(chunks, 4800, 6016, cancellation.Token, callerBytesPerPixel: 0));
        Assert.True(watch.ElapsedMilliseconds < 3000, $"Cancellation took {watch.ElapsedMilliseconds} ms to stop the decode.");
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. System.Text.Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }

    internal const string OwnedC44Color =
        "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";

    // c44's chunk shape: 74, 15 and 10 slices. The data is xorshift32 output, payloadDivisor sets its total size as
    // pixels / divisor bytes split 2 : 3 : 5, enough that the reference decoder never reads past a chunk's end.
    internal static List<ReadOnlyMemory<byte>> CodedChunks(bool color, int width, int height, byte crcb, uint seed, int payloadDivisor)
    {
        int[] slices = [74, 15, 10], shares = [2, 3, 5];
        uint state = seed;
        int total = width * height / payloadDivisor;
        var chunks = new List<ReadOnlyMemory<byte>>();
        for (int serial = 0; serial < 3; serial++)
        {
            byte[] header = serial == 0
                ? [0, (byte)slices[0], (byte)(color ? 1 : 0x81), 2, (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, crcb]
                : [(byte)serial, (byte)slices[serial]];
            var chunk = new byte[header.Length + total * shares[serial] / 10];
            header.CopyTo(chunk, 0);
            for (int i = header.Length; i < chunk.Length; i++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                chunk[i] = (byte)(state >> 24);
            }
            chunks.Add(chunk);
        }
        return chunks;
    }
}
