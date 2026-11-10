using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuRenderingTests
{
    [Theory]
    [InlineData((byte)1, 3, 2, new float[] { 1, 0, 0, 1, 1, 1 })]
    [InlineData((byte)2, 3, 2, new float[] { 1, 1, 1, 0, 0, 1 })]
    [InlineData((byte)5, 2, 3, new float[] { 1, 1, 1, 0, 1, 0 })]
    [InlineData((byte)6, 2, 3, new float[] { 0, 1, 0, 1, 1, 1 })]
    public void PreservesTopToBottomG4RowsUnderAllOrientations(byte orientation, int width, int height, float[] expected)
    {
        // The second row ends at reference change 1 plus 2 using T.6 VR(2) = 000011.
        var document = DjvuDocument.Open(Page(3, 2, orientation, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])));
        var frame = document.RenderPage(0);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(expected, frame.Pixels.ToArray().Where((_, i) => i % 4 == 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsInvalidRenderPageIndexes(int pageIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DjvuDocument.Open(Page(3, 1, 1)).RenderPage(pageIndex));
    }

    [Fact]
    public void MalformedCompressedTextDoesNotPreventIndependentImageRendering()
    {
        var document = DjvuDocument.Open(Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]), Chunk("TXTz", [0])));
        Assert.Equal(3, document.RenderPage(0).Width);
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Fact]
    public void RendersEachBundledPhysicalPageFromItsOwnMask()
    {
        var first = Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]))[4..];
        var second = Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 1, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]))[4..];
        byte[] directory = [0x81, 0, 2, 0, 0, 0, 36, 0, 0, 0, 0];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(7), (uint)(36 + first.Length));
        var file = new byte[] { 0x41, 0x54, 0x26, 0x54 }.Concat(Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", directory), .. first, .. second])).ToArray();
        var document = DjvuDocument.Open(file);
        Assert.Equal(1f, document.RenderPage(0).Pixels[0]);
        Assert.Equal(0f, document.RenderPage(1).Pixels[0]);
    }

    [Fact]
    public void ContainerDiagnosticsDescribeLimitedMmrRendering()
    {
        var document = DjvuDocument.Open(Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20])));
        Assert.Contains(document.Diagnostics, d => d.Code == "SmmrRenderingLimited");
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == "ImageDecodingUnsupported");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedG4PayloadsProduceFormatErrorsWithoutRecoveryFrames(bool invalidCode)
    {
        byte[] encoded = invalidCode ? new byte[] { 0 } : [];
        var document = DjvuDocument.Open(Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, .. encoded])));
        Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
    }

    [Fact]
    public void DoesNotClaimRenderingForUnknownPageVersions()
    {
        var file = Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]));
        file[28] = 99;
        var document = DjvuDocument.Open(file);
        Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
    }

    [Fact]
    public void CancellationIsCheckedBeforeRenderPageSelection()
    {
        var document = DjvuDocument.Open(Page(3, 1, 1));
        Assert.Throws<OperationCanceledException>(() => document.RenderPage(-1, new CancellationToken(true)));
    }

    [Fact]
    public void RejectsDuplicateMmrMasksRatherThanSilentlySelectingOne()
    {
        var mask = Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]);
        var document = DjvuDocument.Open(Page(3, 1, 1, mask, mask));
        Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
    }

    [Fact]
    public void AppliesTheMmrReverseVideoFlag()
    {
        var document = DjvuDocument.Open(Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 1, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20])));
        Assert.Equal(new float[] { 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, document.RenderPage(0).Pixels.ToArray());
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void RejectsStripedAndReservedMmrVariants(byte flags)
    {
        var document = DjvuDocument.Open(Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, flags, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20])));
        Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
    }

    [Theory]
    [InlineData((byte)2, 3, 1, new float[] { 0, 0, 1 })]
    [InlineData((byte)5, 1, 3, new float[] { 1, 0, 0 })]
    [InlineData((byte)6, 1, 3, new float[] { 0, 0, 1 })]
    public void AppliesInfoOrientationToFrameDimensionsAndPixels(byte orientation, int width, int height, float[] expected)
    {
        var document = DjvuDocument.Open(Page(3, 1, orientation, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20])));
        var frame = document.RenderPage(0);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(expected, frame.Pixels.ToArray().Where((_, i) => i % 4 == 0));
    }

    [Fact]
    public void ChecksTheFrameStorageBudgetBeforeInvokingTheDecoder()
    {
        var document = DjvuDocument.Open(Page(10000, 1000, 1, Chunk("Smmr", [.. "MMR"u8, 0, 0x27, 0x10, 3, 0xe8])));
        var exception = Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        Assert.Contains("frame", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnSmmrMaskWithABackgroundThatIsNotIw44IsInvalid()
    {
        // 7.1.3.3 lets Smmr stand in for Sjbz on a compound page, so a one-byte BG44 is a malformed background.
        var document = DjvuDocument.Open(Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]), Chunk("BG44", [0])));
        Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void AnSmmrMaskSelectsBlackOverASubsampledBackground(byte invert)
    {
        // The 3x2 mask of PreservesTopToBottomG4RowsUnderAllOrientations (white, black, black / white, white, white)
        // over a 1x1 grayscale BG44 with no coded slices (gray 127, subsample 3). The invert flag swaps the mask.
        var document = DjvuDocument.Open(Page(3, 2, 1,
            Chunk("Smmr", [.. "MMR"u8, invert, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80]),
            Chunk("BG44", [0, 0, 0x81, 2, 0, 1, 0, 1, 0xff])));
        float gray = (float)Math.Pow(127 / 255d, 2.2);
        float[] expected = invert == 0 ? [gray, 0, 0, gray, gray, gray] : [0, gray, gray, 0, 0, 0];
        var pixels = document.RenderPage(0).Pixels.ToArray();
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(expected[i], pixels[i * 4], 5);
            Assert.Equal(expected[i], pixels[i * 4 + 1], 5);
            Assert.Equal(expected[i], pixels[i * 4 + 2], 5);
        }
    }

    [Theory]
    [InlineData("Sjbz")]
    [InlineData("INCL")]
    [InlineData("XXXX")]
    [InlineData("ANTa")]
    [InlineData("")]
    public void UnsupportedOrMissingVisualContentDoesNotReturnASuccessfulFrame(string extraChunk)
    {
        var chunks = new List<byte[]>();
        if (extraChunk.Length != 0)
        {
            chunks.Add(Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]));
            chunks.Add(Chunk(extraChunk, [0]));
        }
        var document = DjvuDocument.Open(Page(3, 1, 1, chunks.ToArray()));
        Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("magic")]
    [InlineData("width")]
    [InlineData("height")]
    public void RequiresTheSpecifiedMmrHeaderAndMatchingPageDimensions(string defect)
    {
        byte[] payload = [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20];
        if (defect == "short") payload = payload[..7];
        if (defect == "magic") payload[0] = 0;
        if (defect == "width") payload[5] = 2;
        if (defect == "height") payload[7] = 2;
        var document = DjvuDocument.Open(Page(3, 1, 1, Chunk("Smmr", payload)));
        Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
    }

    [Fact]
    public void RendersANativeMmrPageAsOwnedLinearStraightAlphaPixels()
    {
        // T.6 horizontal mode 001, white run 1 = 000111, black run 2 = 11, then EOFB.
        var file = Page(3, 1, 1, Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]));
        var document = DjvuDocument.Open(file);
        Array.Clear(file);
        var frame = document.RenderPage(0);
        Assert.Equal(3, frame.Width);
        Assert.Equal(1, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(new float[] { 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1 }, frame.Pixels.ToArray());
    }

    private static byte[] Page(int width, int height, byte orientation, params byte[][] chunks) => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8, .. Chunk("INFO", [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 26, 0, 0x2c, 1, 22, orientation]), .. chunks.SelectMany(c => c)])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        var length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
