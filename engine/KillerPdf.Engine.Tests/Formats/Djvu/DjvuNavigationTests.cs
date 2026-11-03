using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuNavigationTests
{
    [Fact]
    public void ReadsTheIndependentlyEncodedNestedUnicodeNavigation()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000064444A564D4449524D0000001181000100000052FFFFF8BF9E1FCC877DC900" +
            "4E41564D00000020FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        Assert.Equal("4C312B62F2F1D62D2228516A11045DD6E8D313472D19CC67E6A1F4B0184BA332",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));

        var document = DjvuDocument.Open(input);
        var root = Assert.Single(document.ReadNavigation());
        Assert.Equal("R\u00e9sum\u00e9", root.Label);
        Assert.Equal("#1", root.Target);
        var child = Assert.Single(root.Children);
        Assert.Equal("\u05e4\u05e8\u05e7", child.Label);
        Assert.Equal("#1", child.Target);
        Assert.Empty(child.Children);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == "OutlineUnsupported");
    }

    [Fact]
    public void ADocumentWithoutAnOutlineReturnsAnEmptyList()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        Assert.Empty(DjvuDocument.Open(input).ReadNavigation());
    }

    [Fact]
    public void RejectsDuplicateNavigationChunks()
    {
        var compressed = Convert.FromHexString("FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B");
        Assert.Equal("R\u00e9sum\u00e9", Assert.Single(DjvuDocument.Open(Bundle(compressed)).ReadNavigation()).Label);
        var document = DjvuDocument.Open(Bundle(compressed, compressed));
        Assert.Throws<InvalidDataException>(() => document.ReadNavigation());
    }

    [Fact]
    public void RejectsNavigationRelocatedAfterAPageWithoutBlockingPageDiscovery()
    {
        var original = Bundle(Convert.FromHexString("FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B"));
        byte[] relocated = [.. original[..42], .. original[82..], .. original[42..82]];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(relocated.AsSpan(27), 42);
        var document = DjvuDocument.Open(relocated);
        Assert.Equal(2, Assert.Single(document.Pages).Width);
        var error = Assert.Throws<InvalidDataException>(() => document.ReadNavigation());
        Assert.Contains("immediately follow DIRM", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsStandaloneNavigationWithoutADirectoryWhenNavigationIsRequested()
    {
        var info = Chunk("INFO", [0, 2, 0, 1, 26, 0, 0x2c, 1, 22, 1]);
        var navigation = Chunk("NAVM", Convert.FromHexString("FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B"));
        byte[] input = [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8, .. info, .. navigation])];
        var document = DjvuDocument.Open(input);
        Assert.Equal(2, Assert.Single(document.Pages).Width);
        var error = Assert.Throws<InvalidDataException>(() => document.ReadNavigation());
        Assert.Contains("immediately follow DIRM", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FF")]
    [InlineData("FFFF")]
    [InlineData("FFFFDCFE")]
    public void RejectsMalformedOrInsufficientCompressedNavigation(string compressed)
    {
        var document = DjvuDocument.Open(Bundle(Convert.FromHexString(compressed)));
        Assert.Throws<InvalidDataException>(() => document.ReadNavigation());
    }

    [Fact]
    public void RejectsAnOversizedNavigationBlockBeforeBookmarkParsing()
    {
        var document = DjvuDocument.Open(Bundle(Convert.FromHexString("BFFFFE00")));
        var error = Assert.Throws<InvalidDataException>(() => document.ReadNavigation());
        Assert.Contains("resource limits", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FFE3F59FC3609D8CB1CF", "64F0AB6B158CECBE653D8B99BD358E76C81FFD304D222B22DBC28218F477A8F9", "1024 bookmark")]
    [InlineData("FFFF159843321CA8F22DEBD81B", "AC7079EF776A879DC4E4077E8AA7B106F42E7472D25BCE9DE246E1D08BEF3740", "32 level")]
    [InlineData("FFFFF4893245D56879E5", "AA6142B64A8F028F42993C4EE295B15572A5F881E7D605BEB45848A7176D5EE7", "invalid UTF-8")]
    [InlineData("FFFFF7FF7CCD7405EF5F", "1CE2F3453D8C6FFD25F4F45BB1CF4981B1C67345FA4251C5045261050632A379", "Truncated DjVu navigation string")]
    public void RejectsIndependentlyEncodedInvalidOrOverBudgetNavigation(string compressed, string hash, string message)
    {
        var input = Bundle(Convert.FromHexString(compressed));
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        var error = Assert.Throws<InvalidDataException>(() => document.ReadNavigation());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FFE3FC9FC35F3632C73F", "AAF88DF3EF28287D0DBF5649D64963C592219B842FA1866E8AAE5EA92AC951B4", false)]
    [InlineData("FFFF1C984327FC8252126626FB", "B801352E5A252883F450868E37F5521D7EA9E58B65965093C3F651C66FEEB5A4", true)]
    public void AcceptsTheIndependentExactNodeAndDepthBoundaries(string compressed, string hash, bool depthBoundary)
    {
        var input = Bundle(Convert.FromHexString(compressed));
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var entries = DjvuDocument.Open(input).ReadNavigation();
        if (depthBoundary)
        {
            for (var level = 1; level <= 32; level++)
            {
                var entry = Assert.Single(entries);
                Assert.Equal(string.Empty, entry.Label);
                Assert.Equal(string.Empty, entry.Target);
                entries = entry.Children;
            }
            Assert.Empty(entries);
        }
        else
        {
            Assert.Equal(1024, entries.Count);
            foreach (var entry in entries)
            {
                Assert.Equal(string.Empty, entry.Label);
                Assert.Equal(string.Empty, entry.Target);
                Assert.Empty(entry.Children);
            }
        }
    }

    private static byte[] Bundle(params byte[][] navigationPayloads)
    {
        var navigation = navigationPayloads.Select(payload => Chunk("NAVM", payload)).SelectMany(chunk => chunk).ToArray();
        var pageOffset = 42 + navigation.Length;
        byte[] directory = [0x81, 0, 1, 0, 0, 0, 0, .. Convert.FromHexString("FFFFF8BF9E1FCC877DC9")];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(3), (uint)pageOffset);
        var page = Convert.FromHexString("464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", directory), .. navigation, .. page])];
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        var length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16),
            (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
