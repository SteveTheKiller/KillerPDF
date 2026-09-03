using System.Text;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Parsing;
using Xunit;

namespace KillerPdf.Engine.Tests.Parsing;

public sealed class PdfTextContentReaderTests
{
    private static readonly IReadOnlyDictionary<string, PdfExtractionFont> Fonts = new Dictionary<string, PdfExtractionFont>
    {
        ["F1"] = new(PdfToUnicodeMap.Parse(Encoding.ASCII.GetBytes(
            "1 begincodespacerange <00> <FF> endcodespacerange " +
            "2 beginbfchar <20> <0020> <41> <0041> endbfchar")), new Dictionary<uint, double> { [65] = 600, [32] = 250 })
    };

    [Fact]
    public void PlacesTextAndTjSpacingInPdfCoordinates()
    {
        var text = Read("BT /F1 10 Tf 1 0 0 1 40 700 Tm [(A) 100 (A)] TJ ET");
        Assert.Equal(2, text.Count);
        Assert.Equal(40, text[0].Origin.X);
        Assert.Equal(700, text[0].Origin.Y);
        Assert.Equal(46, text[0].AdvanceEnd.X);
        Assert.Equal(45, text[1].Origin.X);
        Assert.Equal("A", text[0].Text);
    }

    [Fact]
    public void AppliesWordCharacterSpacingHorizontalScaleAndRise()
    {
        var text = Read("BT /F1 10 Tf 2 Tc 4 Tw 50 Tz 3 Ts (A A) Tj ET");
        Assert.Equal(0, text[0].Origin.X);
        Assert.Equal(3, text[0].AdvanceEnd.X);
        Assert.Equal(4, text[1].Origin.X);
        Assert.Equal(8.25, text[2].Origin.X);
        Assert.All(text, p => Assert.Equal(3, p.Origin.Y));
    }

    [Fact]
    public void PreservesLineMatrixAcrossShowsAndQuoteOperators()
    {
        var text = Read("BT /F1 10 Tf 12 TL 1 0 0 1 40 700 Tm (AA) Tj (A) ' 0 0 (A) \" 5 -14 TD (A) Tj T* (A) Tj ET");
        Assert.Equal([40, 46, 40, 40, 45, 45], text.Select(p => p.Origin.X));
        Assert.Equal([700, 700, 688, 676, 662, 648], text.Select(p => p.Origin.Y));
    }

    [Fact]
    public void AppliesRotatedTextMatrixThenCurrentTransformation()
    {
        var text = Read("q 2 0 0 3 10 20 cm BT /F1 10 Tf 0 1 -1 0 40 100 Tm (AA) Tj ET Q");
        Assert.Equal(90, text[0].Origin.X);
        Assert.Equal(320, text[0].Origin.Y);
        Assert.Equal(338, text[0].AdvanceEnd.Y);
        Assert.Equal(338, text[1].Origin.Y);
    }

    [Fact]
    public void RestoresFontStateWithoutRestoringTextMatrix()
    {
        var text = Read("BT /F1 10 Tf (A) Tj q /F1 20 Tf (A) Tj Q (A) Tj ET");
        Assert.Equal([0, 6, 18], text.Select(p => p.Origin.X));
        Assert.Equal([10, 20, 10], text.Select(p => p.FontSize));
    }

    [Theory]
    [InlineData("BT /F1 10 Tf (A) Tj")]
    [InlineData("ET")]
    [InlineData("Q")]
    [InlineData("BT BT ET ET")]
    [InlineData("BT /F1 Tf ET")]
    [InlineData("BT /F1 10 Tf [true] TJ ET")]
    public void RejectsMalformedTextState(string content) => Assert.Throws<FormatException>(() => Read(content));

    [Theory]
    [InlineData("/X Do")]
    [InlineData("/GS gs")]
    [InlineData("BT /Missing 10 Tf (A) Tj ET")]
    public void ReportsUnresolvedResources(string content) => Assert.Throws<NotSupportedException>(() => Read(content));

    [Fact]
    public void BoundsOutputAndAllowsUnknownCompatibilityOperators()
    {
        Assert.Single(Read("BX 42 vendorOperator EX BT /F1 10 Tf (A) Tj ET"));
        Assert.Throws<FormatException>(() => PdfTextContentReader.Read(
            Encoding.ASCII.GetBytes("BT /F1 10 Tf (AA) Tj ET"), Fonts, maximumCharacters: 1));
    }

    private static IReadOnlyList<PdfTextPlacement> Read(string content) =>
        PdfTextContentReader.Read(Encoding.ASCII.GetBytes(content), Fonts);

    [Fact]
    public void ActualTextCannotBypassCharacterBudget()
    {
        Assert.Throws<FormatException>(() => PdfTextContentReader.Read(Encoding.ASCII.GetBytes(
            "/Span << /ActualText (long replacement) >> BDC BT /F1 10 Tf (A) Tj ET EMC"),
            Fonts, maximumCharacters: 5));
    }

    [Fact]
    public void UnusedActualTextDoesNotDecodeInvalidReplacement()
    {
        Assert.Empty(Read("/Span << /ActualText <FEFF00> >> BDC EMC"));
        var text = Read("/Span << /ActualText (outer) >> BDC /Span << /ActualText <FEFF00> >> BDC BT /F1 10 Tf (A) Tj ET EMC EMC");
        Assert.Equal("outer", Assert.Single(text).Text);
    }
}
