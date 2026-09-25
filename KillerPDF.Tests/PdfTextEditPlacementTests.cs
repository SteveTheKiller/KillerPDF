using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfTextEditPlacementTests
{
    [Fact]
    public void LeftFromOrigin_AccountsForEditorInsets()
    {
        Assert.Equal(97, PdfTextEditPlacement.LeftFromOrigin(100, 1, 2, 40), 6);
    }

    [Theory]
    [InlineData(double.NaN, 1, 2)]
    [InlineData(100, -1, 2)]
    [InlineData(100, 1, -2)]
    public void LeftFromOrigin_UsesFallbackForInvalidMetrics(
        double sourceOrigin, double borderInset, double paddingLeft)
    {
        Assert.Equal(40, PdfTextEditPlacement.LeftFromOrigin(
            sourceOrigin, borderInset, paddingLeft, 40));
    }

    [Fact]
    public void TopFromBaseline_AlignsTheEditorBaseline()
    {
        Assert.Equal(82, PdfTextEditPlacement.TopFromBaseline(100, 0.8, 20, 2, 40), 6);
    }

    [Theory]
    [InlineData(double.NaN, 0.8, 20)]
    [InlineData(100, 0, 20)]
    [InlineData(100, 0.8, 0)]
    public void TopFromBaseline_UsesFallbackForInvalidMetrics(
        double sourceBaseline, double normalizedBaseline, double fontSize)
    {
        Assert.Equal(40, PdfTextEditPlacement.TopFromBaseline(
            sourceBaseline, normalizedBaseline, fontSize, 1, 40));
    }
}
