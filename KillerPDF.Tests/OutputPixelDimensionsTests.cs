using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OutputPixelDimensionsTests
{
    [Fact]
    public void FromPoints_ConvertsLetterPageAtThreeHundredDpi()
    {
        Assert.Equal((2550, 3300), OutputPixelDimensions.FromPoints(612, 792, 300));
    }

    [Fact]
    public void FromPoints_RoundsToNearestWholePixel()
    {
        Assert.Equal((1240, 1754), OutputPixelDimensions.FromPoints(595, 842, 150));
    }

    [Theory]
    [InlineData(0, 792, 300)]
    [InlineData(612, -1, 300)]
    [InlineData(612, 792, 0)]
    public void FromPoints_InvalidInputReturnsZero(double width, double height, double dpi)
    {
        Assert.Equal((0, 0), OutputPixelDimensions.FromPoints(width, height, dpi));
    }

    [Theory]
    [InlineData(1000, 1000, 500, 500, "x2")]
    [InlineData(250, 250, 500, 500, "x0.5")]
    public void ScaleLabel_ReportsLinearResolutionChange(
        int outputWidth, int outputHeight, int sourceWidth, int sourceHeight, string expected)
    {
        Assert.Equal(expected, OutputPixelDimensions.ScaleLabel(
            outputWidth, outputHeight, sourceWidth, sourceHeight));
    }
}
