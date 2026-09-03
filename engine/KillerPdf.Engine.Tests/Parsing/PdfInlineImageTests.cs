using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfInlineImageTests
{
    [Fact]
    public void RawSamplesContainingOperatorsAreNeverTokenized()
    {
        byte[] payload = " EI (false) Tj "u8.ToArray();
        var result = Read($"q BI /W {payload.Length} /H 1 /BPC 8 /CS /G ID ", payload, " EI (real) Tj Q");
        Assert.Equal(["q", "BI", "Tj", "Q"], result.Select(i => i.Operator));
        Assert.Equal(payload, result[1].InlineImageData!.Value.ToArray());
        Assert.Null(result[0].InlineImageData);
        var dictionary = Assert.IsType<PdfDictionary>(Assert.Single(result[1].Operands));
        Assert.True(dictionary.ContainsKey(new PdfName("Width"u8)));
        Assert.Equal("DeviceGray", Assert.IsType<PdfName>(dictionary[new PdfName("ColorSpace"u8)]).ValueAsLatin1());
    }

    [Fact]
    public void RawRowsAreIndividuallyBytePaddedAndCrLfIsOneSeparator()
    {
        var result = Read("BI /W 1 /H 2 /IM true ID\r\n", [0x80, 0], " EI Q");
        Assert.Equal(new byte[] { 0x80, 0 }, result[0].InlineImageData!.Value.ToArray());
        Assert.Equal("Q", result[1].Operator);
    }

    [Theory]
    [InlineData("/W 1 /Width 1 /H 1 /IM true ID x EI")]
    [InlineData("/W 1 /H 1 /IM true ID x EIx")]
    [InlineData("/W 1 /H 1 /IM true ID xEI")]
    [InlineData("/W 9 /H 2 /IM true ID x EI")]
    [InlineData("/W 1 /H 1 /BPC 3 /CS /G ID x EI")]
    [InlineData("/W 2147483647 /H 2147483647 /BPC 16 /CS /CMYK ID x EI")]
    [InlineData("/F [] ID x EI")]
    public void RejectsMalformedImages(string content) => Assert.Throws<PdfSyntaxException>(() =>
        PdfContentStreamReader.Read(Encoding.ASCII.GetBytes("BI " + content)));

    [Theory]
    [InlineData("AHx", "454920>")]
    [InlineData("A85", " EI ~>")]
    public void AsciiEncodingsUseTheirEndMarker(string filter, string encoded)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(encoded);
        var result = Read($"BI /W 1 /H 1 /F /{filter} ID ", bytes, " EI Q");
        Assert.Equal(bytes, result[0].InlineImageData!.Value.ToArray());
        Assert.Equal("Q", result[1].Operator);
    }

    [Fact]
    public void RunLengthLiteralCanContainItsOwnEndByteAndEi()
    {
        byte[] bytes = [4, 128, 32, 69, 73, 32, 128];
        var result = Read("BI /F /RL ID ", bytes, " EI Q");
        Assert.Equal(bytes, result[0].InlineImageData!.Value.ToArray());
    }

    [Fact]
    public void JpegSegmentsCanContainFakeEndMarkers()
    {
        byte[] bytes = [255, 216, 255, 224, 0, 7, 255, 217, 32, 69, 73,
            255, 218, 0, 2, 1, 255, 0, 217, 255, 208, 2, 255, 217];
        var result = Read("BI /F /DCT ID ", bytes, " EI Q");
        Assert.Equal(bytes, result[0].InlineImageData!.Value.ToArray());
        Assert.Equal("Q", result[1].Operator);
    }

    [Fact]
    public void FlateConsumesExactlyOneStreamIncludingChecksum()
    {
        byte[] encoded = Compress(" EI (fake) Tj "u8.ToArray());
        var result = Read("BI /F /Fl ID ", encoded, " EI Q");
        Assert.Equal(encoded, result[0].InlineImageData!.Value.ToArray());
        Assert.Equal("Q", result[1].Operator);
    }

    [Fact]
    public void TruncatedFlateAndBadChecksumDoNotReachFollowingOperators()
    {
        byte[] encoded = Compress("hello"u8.ToArray());
        Assert.Throws<PdfSyntaxException>(() => Read("BI /F /Fl ID ", encoded[..^4], " EI Q"));
        encoded[^1] ^= 1;
        Assert.Throws<PdfSyntaxException>(() => Read("BI /F /Fl ID ", encoded, " EI Q"));
    }

    [Fact]
    public void AmbiguousFiltersAreExplicitlyUnsupported() => Assert.Throws<NotSupportedException>(() =>
        Read("BI /F /Unsupported ID ", " EI "u8.ToArray(), "EI Q"));

    [Fact]
    public void ResolvesResourceNamedColorSpace()
    {
        var result = PdfContentStreamReader.Read("BI /W 1 /H 1 /BPC 8 /CS /Cs1 ID abc EI Q"u8.ToArray(),
            resolveColorComponents: name => name.ValueAsLatin1() == "Cs1" ? 3 : null);
        Assert.Equal("abc"u8.ToArray(), result[0].InlineImageData!.Value.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LzwHonorsEarlyChangeAcrossCodeWidthBoundary(int earlyChange)
    {
        // 256 clear, 260 literal codes, then EOD. The final codes use ten bits.
        var bits = new List<int>();
        Add(256, 9);
        for (int i = 0; i < 260; i++) Add(i % 256, i < 255 - earlyChange ? 9 : 10);
        Add(257, 10);
        byte[] data = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) data[i / 8] |= (byte)(bits[i] << (7 - i % 8));
        var result = Read($"BI /F /LZW /DP << /EarlyChange {earlyChange} >> ID ", data, " EI Q");
        Assert.Equal(data, result[0].InlineImageData!.Value.ToArray());
        Assert.Equal("Q", result[1].Operator);
        void Add(int code, int width)
        {
            for (int shift = width - 1; shift >= 0; shift--) bits.Add((code >> shift) & 1);
        }
    }

    private static IReadOnlyList<PdfContentInstruction> Read(string prefix, byte[] data, string suffix) =>
        PdfContentStreamReader.Read((byte[])[.. Encoding.ASCII.GetBytes(prefix), .. data, .. Encoding.ASCII.GetBytes(suffix)]);

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zip = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) zip.Write(bytes);
        return output.ToArray();
    }
}
