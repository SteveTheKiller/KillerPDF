using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPaperSizeTests
{
    [Theory]
    [InlineData(612, 792, "Letter")]
    [InlineData(1008, 612, "Legal")]
    [InlineData(841.89, 595.28, "A4")]
    [InlineData(1584, 2448, "ANSI D")]
    public void IdentifyRecognizesPortraitAndLandscapeSizes(
        double width, double height, string expected)
    {
        Assert.Equal(expected, PdfPaperSize.Identify(width, height));
    }

    [Fact]
    public void IdentifyUsesExplicitToleranceAndRejectsInvalidDimensions()
    {
        Assert.Equal("A4", PdfPaperSize.Identify(596, 842, 1));
        Assert.Null(PdfPaperSize.Identify(596, 842, 0.1));
        Assert.Null(PdfPaperSize.Identify(500, 700));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPaperSize.Identify(0, 792));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPaperSize.Identify(612, 792, -1));
    }
}
