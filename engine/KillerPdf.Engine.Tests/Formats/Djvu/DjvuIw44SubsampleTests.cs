using System.Buffers.Binary;
using System.Security.Cryptography;
using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A page shown smaller than itself decodes only down to the wavelet scale it needs (subsample 2, 4 or 8): the finer
// coefficients keep only their decoding state, so the arithmetic stream decodes exactly as it does at full size, and
// the image is the transform's own samples at that scale. DjVuLibre 3.5.29's ddjvu -subsample draws the same samples;
// it rounds the size (211 / 2 = 105.5 to 106 columns, 149 / 2 = 74.5 to 74 rows) and keeps the rectangle from the
// bottom left, as DjVu coordinates run, so its image is ours less the top rows or the right columns. The expected
// hashes are of ddjvu's PPM pixel bytes.
public sealed class DjvuIw44SubsampleTests
{
    [Theory]
    [InlineData(2, 106, 74, "5BD0C87357594B24A3D983EE632CD8B51C47A4D91F7AFF0FCDCF822CB936F25C")]
    [InlineData(4, 53, 37, "7D73491A01860CA4C7BAC5596E42C5563A018FA98B6A27C13E8688C45AD6DB66")]
    [InlineData(8, 26, 19, "7957722CDCA9F6F44422A34FC1BBDAB1AFEF3A8D185DDD2649DF93FFC3D5FDD6")]
    public void ARealC44ColorPageDecodesAtASubsampleLikeTheReferenceDecoder(int subsample, int referenceWidth,
        int referenceHeight, string expected)
    {
        byte[] file = Convert.FromBase64String(DjvuIw44RealPageTests.OwnedC44Color);
        var chunks = new List<ReadOnlyMemory<byte>>();
        for (int position = 16; position + 8 <= file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(position + 4));
            if (file.AsSpan(position, 4).SequenceEqual("BG44"u8)) chunks.Add(file.AsMemory(position + 8, length));
            position += 8 + length + (length & 1);
        }

        byte[] reduced = DjvuIw44Decoder.DecodeColor(chunks, 211, 149, CancellationToken.None, callerBytesPerPixel: 0,
            subsample: subsample);

        Assert.Equal(expected, BottomLeftHash(reduced, 211, 149, subsample, referenceWidth, referenceHeight));
    }

    // c44's 99-slice chunk shape over pseudo-random data activates almost every coefficient of every band and drives
    // the transform to the 16-bit limits where DjVuLibre saturates its last column pass, at a subsample too.
    [Theory]
    [InlineData(2, 1275, 1650, "47C86A637D9ED7C67DD1BBD56DDE255A4EAB083CF95028E5EF13FD1674BB38C5")]
    [InlineData(8, 319, 412, "D1B25DA6C36225876C2E3351232CC1DD087D4664E953024BD71D8338F53535E8")]
    public void APseudoRandomColorPageDecodesAtASubsampleLikeTheReferenceDecoder(int subsample, int referenceWidth,
        int referenceHeight, string expected)
    {
        var chunks = DjvuIw44RealPageTests.CodedChunks(color: true, 2550, 3300, 0x8a, 4u, payloadDivisor: 2);

        byte[] reduced = DjvuIw44Decoder.DecodeColor(chunks, 2550, 3300, CancellationToken.None, callerBytesPerPixel: 0,
            subsample: subsample);

        Assert.Equal(expected, BottomLeftHash(reduced, 2550, 3300, subsample, referenceWidth, referenceHeight));
    }

    // 5100 x 6600 at subsample 2: 160 x 207 blocks keep 256 16-bit coefficients and 768 two-bit states each per
    // component, 3 x 33,120 x 704 = 69.9 MB, plus a 2550 x 3300 plane per component and the RGB output: well under
    // the viewer's 128 MiB, where the full-size decode holds 3 x 67.8 MB of coefficients alone.
    [Fact]
    public void A600DpiColorPageAtSubsampleTwoDecodesWithinTheViewerBudget()
    {
        var chunks = DjvuIw44RealPageTests.CodedChunks(color: true, 5100, 6600, 0x8a, 6u, payloadDivisor: 2);
        long payload = chunks.Sum(c => (long)c.Length);
        Assert.True(DjvuIw44Decoder.ColorWorkingBytes(5100, 6600, payload, 0) > 128L << 20);
        Assert.True(DjvuIw44Decoder.ColorWorkingBytes(5100, 6600, payload, 0, subsample: 2) < 128L << 20);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();

        byte[] reduced = DjvuIw44Decoder.DecodeColor(chunks, 5100, 6600, CancellationToken.None, callerBytesPerPixel: 0,
            subsample: 2);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2550 * 3300 * 3, reduced.Length);
        Assert.True(allocated < 160L << 20, $"allocated {allocated >> 20} MiB");
    }

    // The reference's rectangle of a subsampled image: its bottom rows and left columns.
    private static string BottomLeftHash(byte[] rgb, int width, int height, int subsample, int keptWidth, int keptHeight)
    {
        int w = (width + subsample - 1) / subsample, h = (height + subsample - 1) / subsample;
        Assert.Equal(w * h * 3, rgb.Length);
        var kept = new byte[keptWidth * keptHeight * 3];
        for (int y = 0; y < keptHeight; y++)
            rgb.AsSpan(((y + h - keptHeight) * w) * 3, keptWidth * 3).CopyTo(kept.AsSpan(y * keptWidth * 3));
        return Convert.ToHexString(SHA256.HashData(kept));
    }
}
