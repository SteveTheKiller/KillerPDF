using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuTextTests
{
    [Fact]
    public void ReadsTheIndependentLongRepeatedTextWithUnicodeAndNewline()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000034444A5655494E464F0000000A000200011A002C011601" +
            "5458547A00000016FFDFF3DFF35F9F519ED18F361186A71C92425F61C0E7");
        Assert.Equal("BA04769BCF198FBCBF977EE28772A26F818118CF2744B902C04663A765664D65",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        Assert.Equal(new string('A', 8192) + " Caf\u00e9\n", DjvuDocument.Open(input).ReadText(0));
    }

    [Fact]
    public void ReadsIndependentlyEncodedUnicodeTextWithoutZones()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000038444A5655494E464F0000000A000200011A002C011601" +
            "5458547A00000019FFFFEBDFFC5042E9EBF0318DC264BA9731BDA676E26E7DE13600");
        Assert.Equal("A95ADEC7374850A4CDE21AAE4F0B831F292814D9ACAB7050F041212B3C49B289",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        Assert.Equal("Caf\u00e9 \u05e9\u05dc\u05d5\u05dd\n", document.ReadText(0));
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == "CompressedTextUnsupported");
    }

    [Fact]
    public void AcceptsACompleteZoneTreeWithSiblingWords()
    {
        byte[] parent = [1, 0x80, 0, 0x80, 0, 0x80, 100, 0x80, 100, 0x80, 0, 0, 0, 3, 0, 0, 2];
        byte[] first = [6, 0x80, 0, 0x80, 0, 0x80, 20, 0x80, 20, 0x80, 0, 0, 0, 1, 0, 0, 0];
        byte[] second = [6, 0x80, 20, 0x80, 0, 0x80, 40, 0x80, 20, 0x80, 0, 0, 0, 2, 0, 0, 0];
        var document = DjvuDocument.Open(Page(Chunk("TXTa", [0, 0, 3, 65, 66, 67, 1, .. parent, .. first, .. second])));
        Assert.Equal("ABC", document.ReadText(0));
    }

    [Fact]
    public void AnExplicitEmptyTextChunkReturnsAnEmptyString()
    {
        Assert.Equal(string.Empty, DjvuDocument.Open(Page(Chunk("TXTa", [0, 0, 0, 1]))).ReadText(0));
    }

    [Fact]
    public void ReadsEachBundledPhysicalPageFromItsOwnForm()
    {
        var first = Page(Chunk("TXTa", [0, 0, 1, 65, 1]))[4..];
        var second = Page(Chunk("TXTa", [0, 0, 1, 66, 1]))[4..];
        byte[] directory = [0x81, 0, 2, 0, 0, 0, 36, 0, 0, 0, 0];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(7), (uint)(36 + first.Length));
        var file = new byte[] { 0x41, 0x54, 0x26, 0x54 }.Concat(Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", directory), .. first, .. second])).ToArray();
        var document = DjvuDocument.Open(file);
        Array.Clear(file);
        Assert.Equal("A", document.ReadText(0));
        Assert.Equal("B", document.ReadText(1));
    }

    [Fact]
    public void OwnsOnlyTheProvidedMemorySlice()
    {
        var file = Page(Chunk("TXTa", [0, 0, 1, 65, 1]));
        byte[] backing = [1, 2, 3, .. file, 4, 5];
        var document = DjvuDocument.Open(backing.AsMemory(3, file.Length));
        Array.Clear(backing);
        Assert.Equal("A", document.ReadText(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsTheExactTextAndZoneBudgetBoundaries(bool zoneBudget)
    {
        byte[] payload;
        if (zoneBudget)
        {
            byte[] zone = [1, 0x80, 0, 0x80, 0, 0x80, 100, 0x80, 100, 0x80, 0, 0, 0, 1, 0, 0, 0];
            payload = [0, 0, 1, 65, 1, .. Enumerable.Repeat(zone, 65_536).SelectMany(z => z)];
        }
        else
            payload = [0x40, 0, 0, .. Enumerable.Repeat((byte)65, 4 * 1024 * 1024), 1];
        var text = DjvuDocument.Open(Page(Chunk("TXTa", payload))).ReadText(0);
        Assert.Equal(zoneBudget ? 1 : 4_194_304, text.Length);
        Assert.Equal('A', text[0]);
        Assert.Equal('A', text[^1]);
    }

    [Fact]
    public void DuplicatePlainTextChunksAreRejectedRatherThanSilentlySelectingOne()
    {
        var text = Chunk("TXTa", [0, 0, 1, 65, 1]);
        var document = DjvuDocument.Open(Page(text, text));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsPageIndexesOutsideTheDiscoveredPages(int pageIndex)
    {
        var document = DjvuDocument.Open(Page());
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReadText(pageIndex));
    }

    [Fact]
    public void CancellationIsObservedBeforePageSelection()
    {
        var document = DjvuDocument.Open(Page());
        Assert.Throws<OperationCanceledException>(() => document.ReadText(-1, new CancellationToken(true)));
    }

    [Fact]
    public void UnsupportedTextVersionsDoNotReturnUnverifiedText()
    {
        var document = DjvuDocument.Open(Page(Chunk("TXTa", [0, 0, 1, 65, 2])));
        Assert.Throws<NotSupportedException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsTextAndZoneCountsBeyondTheReadBudgets(bool zoneBudget)
    {
        byte[] payload;
        if (zoneBudget)
        {
            byte[] zone = [1, 0x80, 0, 0x80, 0, 0x80, 100, 0x80, 100, 0x80, 0, 0, 0, 1, 0, 0, 0];
            payload = [0, 0, 1, 65, 1, .. Enumerable.Repeat(zone, 65_537).SelectMany(z => z)];
        }
        else
            payload = [0x40, 0, 1, .. Enumerable.Repeat((byte)65, 4 * 1024 * 1024 + 1), 1];
        var document = DjvuDocument.Open(Page(Chunk("TXTa", payload)));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("type")]
    [InlineData("children")]
    [InlineData("range")]
    [InlineData("length")]
    public void RejectsMalformedOptionalTextZoneStructure(string defect)
    {
        byte[] zone = [1, 0x80, 0, 0x80, 0, 0x80, 100, 0x80, 100, 0x80, 0, 0, 0, 1, 0, 0, 0];
        byte[] zones = zone;
        if (defect == "partial") zones = zone[..^1];
        if (defect == "type") zones[0] = 0;
        if (defect == "children") zones[16] = 1;
        if (defect == "length") zones[13] = 2;
        if (defect == "range") { zones[10] = 1; }
        var document = DjvuDocument.Open(Page(Chunk("TXTa", [0, 0, 1, 65, 1, .. zones])));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 0 })]
    [InlineData(new byte[] { 0, 0, 5, 65, 1 })]
    [InlineData(new byte[] { 0, 0, 1, 65 })]
    public void RejectsTruncatedTextHeadersBodiesAndMissingVersions(byte[] payload)
    {
        var document = DjvuDocument.Open(Page(Chunk("TXTa", payload)));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData(0xff, 0xff)]
    [InlineData(0xc0, 0xaf)]
    [InlineData(0xed, 0xa0)]
    public void RejectsMalformedUtf8InsteadOfSubstitutingReplacementCharacters(byte first, byte second)
    {
        var document = DjvuDocument.Open(Page(Chunk("TXTa", [0, 0, 2, first, second, 1])));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsDuplicateOrConflictingTextChunks(bool includePlainText)
    {
        var compressed = Chunk("TXTz", Convert.FromHexString("FFFFEBDFFC5042E9EBF0318DC264BA9731BDA676E26E7DE136"));
        var other = includePlainText ? Chunk("TXTa", [0, 0, 1, 65, 1]) : compressed;
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(Page(compressed, other)).ReadText(0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FF")]
    [InlineData("FFFF")]
    [InlineData("FFFFEBDF")]
    public void RejectsMalformedOrInsufficientCompressedText(string compressed)
    {
        var document = DjvuDocument.Open(Page(Chunk("TXTz", Convert.FromHexString(compressed))));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Fact]
    public void RejectsAnOversizedCompressedTextBlockBeforeTextParsing()
    {
        var document = DjvuDocument.Open(Page(Chunk("TXTz", Convert.FromHexString("BFFFFE00"))));
        var error = Assert.Throws<InvalidDataException>(() => document.ReadText(0));
        Assert.Contains("resource limits", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PagesWithoutTextReturnAnEmptyString()
    {
        Assert.Equal(string.Empty, DjvuDocument.Open(Page()).ReadText(0));
    }

    [Fact]
    public void ReadsPlainTextFromAnOwnedSnapshot()
    {
        var file = Page(Chunk("TXTa", [0, 0, 8, 0x43, 0x61, 0x66, 0xc3, 0xa9, 0x0a, 0x1f, 0, 1]));
        var document = DjvuDocument.Open(file);
        Array.Clear(file);
        Assert.Equal("Caf\u00e9\n\u001f\0", document.ReadText(0));
    }

    private static byte[] Page(params byte[][] chunks) => [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8, .. Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]), .. chunks.SelectMany(c => c)])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        var length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
