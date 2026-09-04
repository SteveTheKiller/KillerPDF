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

    [Theory]
    [InlineData(612, 792, PdfPaperOrientation.Portrait)]
    [InlineData(792, 612, PdfPaperOrientation.Landscape)]
    [InlineData(720, 720, PdfPaperOrientation.Square)]
    public void DescribeReturnsDisplayUnitsAndOrientation(
        double width, double height, PdfPaperOrientation orientation)
    {
        PdfPaperSizeDescription description = PdfPaperSize.Describe(width, height);

        Assert.Equal(width / 72d, description.WidthInches, 10);
        Assert.Equal(height / 72d, description.HeightInches, 10);
        Assert.Equal(width * 25.4d / 72d, description.WidthMillimeters, 10);
        Assert.Equal(height * 25.4d / 72d, description.HeightMillimeters, 10);
        Assert.Equal(orientation, description.Orientation);
        Assert.Equal(width == 720 ? null : "Letter", description.CommonName);
    }
}
