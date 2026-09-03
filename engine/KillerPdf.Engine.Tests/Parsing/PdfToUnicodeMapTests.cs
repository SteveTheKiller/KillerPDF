using System.Text;
using KillerPdf.Engine.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfToUnicodeMapTests
{
    private const string Space = "1 begincodespacerange <0000> <FFFF> endcodespacerange ";

    [Fact]
    public void DecodesLigaturesAndSupplementaryCharacters()
    {
        var map = Parse(Space + "3 beginbfchar <0001> <0041> <0002> <00660069> <0003> <D83DDE00> endbfchar");
        var decoded = map.Decode(new byte[] { 0, 1, 0, 2, 0, 3 });
        Assert.Equal(new[] { "A", "fi", char.ConvertFromUtf32(0x1F600) }, decoded.Select(c => c.Text));
        Assert.Equal(new uint[] { 1, 2, 3 }, decoded.Select(c => c.Code));
        Assert.All(decoded, c => Assert.Equal(2, c.ByteLength));
    }

    [Fact]
    public void ExpandsSequentialAndArrayRanges()
    {
        var map = Parse(Space + "2 beginbfrange <0001> <0003> <0041> <0004> <0005> [<00660069> <03A9>] endbfrange");
        Assert.Equal("ABCfi\u03A9", string.Concat(map.Decode(new byte[] { 0, 1, 0, 2, 0, 3, 0, 4, 0, 5 }).Select(c => c.Text)));
    }

    [Fact]
    public void DistinguishesVariableLengthSourceCodes()
    {
        var map = Parse("2 begincodespacerange <00> <7F> <8000> <FFFF> endcodespacerange " +
            "2 beginbfchar <41> <0041> <8001> <4E2D> endbfchar");
        Assert.Equal(new[] { "A", "\u4E2D" }, map.Decode(new byte[] { 65, 128, 1 }).Select(c => c.Text));
        Assert.Throws<FormatException>(() => map.Decode(new byte[] { 128 }));
        Assert.Throws<NotSupportedException>(() => map.Decode(new byte[] { 66 }));
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
