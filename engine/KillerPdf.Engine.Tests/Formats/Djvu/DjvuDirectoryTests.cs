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
