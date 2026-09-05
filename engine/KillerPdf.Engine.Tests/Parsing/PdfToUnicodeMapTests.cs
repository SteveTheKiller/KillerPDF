using System.Text;
using KillerPdf.Engine.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfToUnicodeMapTests
{
    [Fact]
    public void ParseFont_CompatibilityRecoveryReplacesInvalidUtf16Destinations()
    {
        byte[] source = Encoding.ASCII.GetBytes(
            "1 begincodespacerange <00> <ff> endcodespacerange "
            + "1 beginbfchar <41> <d800> endbfchar");

        Assert.Throws<FormatException>(() => PdfToUnicodeMap.Parse(source));
        PdfToUnicodeMap map = PdfToUnicodeMap.ParseWithCompatibilityRecovery(source);

        Assert.Equal("\uFFFD", Assert.Single(map.Decode([0x41])).Text);
    }

    [Theory]
    [InlineData("", "\uFFFD")]
    [InlineData("41", "\uFFFD")]
    public void ParseFont_CompatibilityRecoveryReplacesEmptyOrOddUtf16Destinations(
        string destination, string expected)
    {
        byte[] source = Encoding.ASCII.GetBytes(
            "1 begincodespacerange <00> <ff> endcodespacerange "
            + $"1 beginbfchar <41> <{destination}> endbfchar");

        Assert.Throws<FormatException>(() => PdfToUnicodeMap.Parse(source));
        PdfToUnicodeMap map = PdfToUnicodeMap.ParseWithCompatibilityRecovery(source);

        Assert.Equal(expected, Assert.Single(map.Decode([0x41])).Text);
    }

    [Fact]
    public void ParseFont_CompatibilityRecoveryKeepsFirstDuplicateMapping()
    {
        byte[] source = Encoding.ASCII.GetBytes(
            "1 begincodespacerange <00> <ff> endcodespacerange "
            + "2 beginbfchar <41> <0041> <41> <0042> endbfchar");

        Assert.Throws<FormatException>(() => PdfToUnicodeMap.Parse(source));
        PdfToUnicodeMap map = PdfToUnicodeMap.ParseWithCompatibilityRecovery(source);

        Assert.Equal("A", Assert.Single(map.Decode([0x41])).Text);
    }

    private const string Space = "1 begincodespacerange <0000> <FFFF> endcodespacerange ";

    [Fact]
    public void DecodesLigaturesAndSupplementaryCharacters()
    {
        var map = Parse(Space + "3 beginbfchar <0001> <0041> <0002> <00660069> <0003> <D83DDE00> endbfchar");
        var decoded = map.Decode([0, 1, 0, 2, 0, 3]);
        Assert.Equal(["A", "fi", char.ConvertFromUtf32(0x1F600)], decoded.Select(c => c.Text));
        Assert.Equal(new uint[] { 1, 2, 3 }, decoded.Select(c => c.Code));
        Assert.All(decoded, c => Assert.Equal(2, c.ByteLength));
    }

    [Fact]
    public void ExpandsSequentialAndArrayRanges()
    {
        var map = Parse(Space + "2 beginbfrange <0001> <0003> <0041> <0004> <0005> [<00660069> <03A9>] endbfrange");
        Assert.Equal("ABCfi\u03A9", string.Concat(map.Decode([0, 1, 0, 2, 0, 3, 0, 4, 0, 5]).Select(c => c.Text)));
    }

    [Fact]
    public void DistinguishesVariableLengthSourceCodes()
    {
        var map = Parse("2 begincodespacerange <00> <7F> <8000> <FFFF> endcodespacerange " +
            "2 beginbfchar <41> <0041> <8001> <4E2D> endbfchar");
        Assert.Equal(["A", "\u4E2D"], map.Decode([65, 128, 1]).Select(c => c.Text));
        Assert.Throws<FormatException>(() => map.Decode([128]));
        Assert.Throws<NotSupportedException>(() => map.Decode("B"u8));
    }

    [Fact]
    public void Decode_CompatibilityRecoveryReplacesIncompleteCharacterCodes()
    {
        var map = Parse(Space + "1 beginbfchar <0001> <0041> endbfchar");

        IReadOnlyList<PdfDecodedCharacter> decoded =
            map.DecodeWithCompatibilityRecovery([0, 1, 128]);

        Assert.Equal(["A", "\uFFFD"], decoded.Select(character => character.Text));
        Assert.Equal([2, 1], decoded.Select(character => character.ByteLength));
    }

    [Theory]
    [InlineData("1 beginbfchar <0001> <0041> <0002> <0042> endbfchar")]
    [InlineData("1 beginbfchar <0001> <D800> endbfchar")]
    [InlineData("1 beginbfchar <0001> <41> endbfchar")]
    [InlineData("2 beginbfchar <0001> <0041> <0001> <0042> endbfchar")]
    [InlineData("1 beginbfrange <0001> <0003> [<0041>] endbfrange")]
    [InlineData("1 beginbfrange <0003> <0001> <0041> endbfrange")]
    [InlineData("1 beginbfchar")]
    public void RejectsMalformedMappings(string mappings) => Assert.Throws<FormatException>(() => Parse(Space + mappings));

    [Fact]
    public void RejectsAmbiguousSpacesAndUnsupportedInheritance()
    {
        Assert.Throws<FormatException>(() => Parse("2 begincodespacerange <00> <FF> <0000> <FFFF> endcodespacerange"));
        Assert.Throws<NotSupportedException>(() => Parse(Space + "/Base usecmap"));
    }

    [Fact]
    public void BoundsRangeExpansion() => Assert.Throws<FormatException>(() => PdfToUnicodeMap.Parse(
        Encoding.ASCII.GetBytes(Space + "1 beginbfrange <0000> <FFFF> <0000> endbfrange"), maximumMappings: 10));

    private static PdfToUnicodeMap Parse(string source) => PdfToUnicodeMap.Parse(Encoding.ASCII.GetBytes(source));
}
