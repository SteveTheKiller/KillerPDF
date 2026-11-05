using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuBzzTerminationTests
{
    [Fact]
    public void ReadsIndependentNineCharacterTextWithVirtualFinalBits()
    {
        // Independently encoded text payload: 000009, nine ASCII A bytes, version 1.
        var document = DjvuDocument.Open(Page(Convert.FromHexString("FFFFF1DFFE6982BFA3")));
        Assert.Equal("AAAAAAAAA", document.ReadText(0));
    }

    [Theory]
    [InlineData("FFFFFC849DBF", "header")]
    [InlineData("FFFFF9DFFE69F862", "text or version")]
    [InlineData("FFFFFAFF7DA23C657F", "text or version")]
    [InlineData("FFFFF8DFFE6D44F2C7", "UTF8")]
    [InlineData("FFFFE9824DB69849B9A22D8F13C043FC6A3F", "zone record")]
    public void RejectsIndependentlyEncodedMalformedTextPayloads(string compressed, string error)
    {
        var document = DjvuDocument.Open(Page(Convert.FromHexString(compressed)));
        var exception = Assert.Throws<InvalidDataException>(() => document.ReadText(0));
        Assert.Contains(error, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnIndependentlyEncodedUnsupportedTextVersion()
    {
        var document = DjvuDocument.Open(Page(Convert.FromHexString("FFFFF9EB73176A9E86")));
        Assert.Throws<NotSupportedException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RejectsIndependentlyClassifiedInvalidTextPrefixes(int bytes)
    {
        // Independent decoding yields empty payloads for 0..2 and errors for 4..8.
        var compressed = Convert.FromHexString("FFFFF1DFFE6982BFA3")[..bytes];
        var document = DjvuDocument.Open(Page(compressed));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Fact]
    public void RejectsThePrefixWhoseIndependentPayloadHasAnInvalidVersion()
    {
        // FFFFF1 independently decodes to thirteen zero bytes, not valid TXT data.
        var document = DjvuDocument.Open(Page(Convert.FromHexString("FFFFF1")));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData("BFFFFE00")]
    [InlineData("00000000")]
    [InlineData("FFFFFE")]
    public void RejectsInvalidAnnouncedBlockSizesBeforeAllocatingDecodedText(string compressed)
    {
        var document = DjvuDocument.Open(Page(Convert.FromHexString(compressed)));
        var exception = Assert.Throws<InvalidDataException>(() => document.ReadText(0));
        Assert.Contains("resource limits", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VirtualArithmeticBitsDoNotRepairAPhysicallyTruncatedContainer()
    {
        var input = Page(Convert.FromHexString("FFFFF1DFFE6982BFA3"));
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(input.AsMemory(0, input.Length - 1)));
    }

    [Fact]
    public void CompressedTextReadObservesCancellation()
    {
        var document = DjvuDocument.Open(Page(Convert.FromHexString("FFFFF1DFFE6982BFA3")));
        Assert.Throws<OperationCanceledException>(() => document.ReadText(0, new CancellationToken(true)));
    }

    private static byte[] Page(byte[] compressed) =>
        [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]),
            .. Chunk("TXTz", compressed)])];

    private static byte[] Chunk(string id, byte[] payload) =>
        [.. System.Text.Encoding.ASCII.GetBytes(id),
            (byte)(payload.Length >> 24), (byte)(payload.Length >> 16),
            (byte)(payload.Length >> 8), (byte)payload.Length, .. payload,
            .. (payload.Length % 2 == 1 ? new byte[1] : [])];
}
