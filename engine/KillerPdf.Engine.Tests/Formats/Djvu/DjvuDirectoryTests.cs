using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuDirectoryTests
{
    // Independently encoded metadata: sizes 30,30, flags 1,1, IDs p3,p2.
    private static byte[] ReferenceBundle() => Convert.FromHexString(
        "41542654464F524D00000062444A564D4449524D0000001A8100020000005000000032" +
        "FFFFF0BF347BCC70D5BE417048DC17" +
        "464F524D00000016444A5655494E464F0000000A000200011A002C011601" +
        "464F524D00000016444A5655494E464F0000000A000300011A002C011601");

    [Fact]
    public void OpensBundledPagesInDirectoryOrder()
    {
        var input = ReferenceBundle();
        Assert.Equal("4EC7E50CFB5DE8A8FC40259333DFC420298672873E1D91DD9FFABAEB4908AA19",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        Assert.Equal(new[] { 3, 2 }, DjvuDocument.Open(input).Pages.Select(p => p.Width));
    }

    [Fact]
    public void OpensBundlesWithASharedAnnotationComponent()
    {
        // FORM:DJVI "s.djvi" with directory kind 3 (shared annotations), then page "p1".
        // The metadata is BZZ-encoded with the DjVu.js reference encoder, and DjVuLibre
        // 3.5.29 ddjvu renders the page from this bundle.
        var input = Convert.FromHexString(
            "41542654464F524D0000006C444A564D4449524D000000228100020000003A0000005A" +
            "FFFFECBF3880C50C8AFFC4E86C93F326B1246515F3EC7F" +
            "464F524D00000018444A5649414E54610000000C286D6F646520636F6C6F7229" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        var page = Assert.Single(DjvuDocument.Open(input).Pages);
        Assert.Equal(2, page.Width);
        Assert.Equal("p1", page.ComponentId);
    }

    [Theory]
    // The page FORM:DJVU is listed with the shared-annotation kind 3.
    [InlineData("41542654464F524D0000006C444A564D4449524D000000218100020000003A0000005A" +
        "FFFFECBF3880C50C8B01DDF08022E1835DCC819E158300")]
    // The FORM:DJVI component is listed with the undefined kind 4.
    [InlineData("41542654464F524D0000006C444A564D4449524D000000228100020000003A0000005A" +
        "FFFFECBF3880C50C8AFF7B0C6C93F326B1246515F3EC7F")]
    public void RejectsDirectoryKindsThatDoNotMatchTheirComponent(string header)
    {
        var input = Convert.FromHexString(header +
            "464F524D00000018444A5649414E54610000000C286D6F646520636F6C6F7229" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(input));
    }

    [Fact]
    public void ExposesComponentIdsFromTheCompressedDirectory()
    {
        Assert.Equal(new[] { "p3", "p2" }, DjvuDocument.Open(ReferenceBundle()).Pages.Select(p => p.ComponentId));
    }

    [Theory]
    [InlineData("a", "FFFFF8BF9E1FCC877DC9")]
    [InlineData("b", "FFFFF8BF9D1FCC877DC9")]
    public void OpensIndependentlyEncodedSinglePageDirectories(string id, string compressed)
    {
        var input = Convert.FromHexString(
            "41542654464F524D0000003C444A564D4449524D000000118100010000002A" + compressed + "00" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        var page = Assert.Single(DjvuDocument.Open(input).Pages);
        Assert.Equal(2, page.Width);
        Assert.Equal(id, page.ComponentId);
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("FFFF")]
    [InlineData("FFFFF8")]
    [InlineData("FFFFF8BF")]
    [InlineData("FFFFF8BF9E")]
    [InlineData("FFFFF8BF9E1F")]
    [InlineData("FFFFF8BF9E1FCC")]
    [InlineData("FFFFF8BF9E1FCC87")]
    [InlineData("FFFFF8BF9E1FCC877D")]
    public void RejectsInsufficientCompressedDirectoryDataInsideAValidContainer(string compressed)
    {
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(SinglePageBundle(Convert.FromHexString(compressed))));
    }

    [Theory]
    [InlineData("BFFFFE00")]
    [InlineData("00000000")]
    public void RejectsAnnouncedBlocksAboveTheDirectoryBudget(string compressed)
    {
        // Initial pass-through bits encode sizes 0x400001 and 0xffffff, respectively.
        var error = Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(SinglePageBundle(Convert.FromHexString(compressed))));
        Assert.Contains("resource limits", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReframedCompressedInputMatchesTheIndependentSinglePageFixture()
    {
        var input = SinglePageBundle(Convert.FromHexString("FFFFF8BF9E1FCC877DC9"));
        Assert.Equal("645F8FD420D0ADD3B1CACEF6C1E6A272D229563CE872B1235650832083B64EE9",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        Assert.Equal("a", Assert.Single(DjvuDocument.Open(input).Pages).ComponentId);
    }

    [Fact]
    public void MatchesALargeDirectoryListedInReverseFileOrder()
    {
        // 32000 empty FORM:DJVI includes and one page, listed last to first. Matching each
        // directory offset by scanning the components made this quadratic.
        const int includes = 32_000;
        byte[] page = Convert.FromHexString("464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        // The odd directory length is followed by one zero pad byte.
        int count = includes + 1, directoryLength = 3 + 4 * count, start = 24 + directoryLength + 1;
        var input = new byte[start + includes * 12 + page.Length];
        "AT&TFORM"u8.CopyTo(input);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(8), (uint)(input.Length - 12));
        "DJVMDIRM"u8.CopyTo(input.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(20), (uint)directoryLength);
        input[24] = 0x81;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(25), (ushort)count);
        for (int i = 0; i < count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(27 + 4 * i), (uint)(start + 12 * (count - 1 - i)));
        for (int i = 0; i < includes; i++)
        {
            "FORM"u8.CopyTo(input.AsSpan(start + 12 * i));
            input[start + 12 * i + 7] = 4;
            "DJVI"u8.CopyTo(input.AsSpan(start + 12 * i + 8));
        }
        page.CopyTo(input, start + 12 * includes);
        var document = DjvuDocument.Open(input);
        Assert.Equal(2, Assert.Single(document.Pages).Width);
    }

    private static byte[] SinglePageBundle(byte[] compressed)
    {
        var directoryLength = 7 + compressed.Length;
        var pageOffset = 24 + directoryLength + directoryLength % 2;
        var input = new byte[pageOffset + 30];
        "AT&TFORM"u8.CopyTo(input);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(8), (uint)(input.Length - 12));
        "DJVMDIRM"u8.CopyTo(input.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(20), (uint)directoryLength);
        input[24] = 0x81;
        input[26] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(27), (uint)pageOffset);
        compressed.CopyTo(input, 31);
        Convert.FromHexString("464F524D00000016444A5655494E464F0000000A000200011A002C011601").CopyTo(input, pageOffset);
        return input;
    }
}
