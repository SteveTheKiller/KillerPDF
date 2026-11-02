using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OutputPixelDimensionsTests
{
    [Theory]
    [InlineData(500, 300, 612, 792, 300, 500)]
    [InlineData(500, 300, 792, 612, 500, 300)]
    [InlineData(500, 500, 612, 792, 500, 500)]
    public void AnImagesPixelsTurnWithItsPage(int width, int height, double pageWidth, double pageHeight, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), OutputPixelDimensions.Oriented((width, height), pageWidth, pageHeight));

    // Issue 462: a 500 pixel image previewed at 1,100 showed 1,099 at x1.
    [Theory]
    [InlineData(500, 720, 50)]       // 500 pixels across a 10 inch page
    [InlineData(3000, 720, 300)]
    [InlineData(100000, 72, 1200)]   // clamped to the supported range
    [InlineData(1, 720, 24)]
    public void NativeDpiIsTheImagesOwnResolution(int pixels, double points, double expected)
    {
        Assert.Equal(expected, OutputPixelDimensions.NativeDpi((pixels, pixels), points, points, 150));
    }

    // Scans of one size land on a few resolutions, not one for every page's rounding.
    [Theory]
    [InlineData(2481, 595.3)]
    [InlineData(2479, 595.3)]
    [InlineData(2480, 595.2)]
    public void NativeDpiIsAWholeNumberSoSimilarPagesShareOneResolution(int pixels, double points)
    {
        double dpi = OutputPixelDimensions.NativeDpi((pixels, pixels), points, points, 150);
        Assert.Equal(Math.Round(dpi), dpi);
        Assert.Equal(300, dpi);
        var (width, _) = OutputPixelDimensions.FromPoints(points, points, dpi);
        Assert.InRange(width, pixels - 1, pixels + 1);
    }

    // A whole number that would shift the output by more than a pixel is not used.
    [Fact]
    public void NativeDpiKeepsTheExactResolutionWhenRoundingWouldChangeTheOutputSize()
    {
        double dpi = OutputPixelDimensions.NativeDpi((2547, 3296), 612, 792, 150);

        Assert.NotEqual(Math.Round(dpi), dpi);
        Assert.Equal((2547, 3296), OutputPixelDimensions.FromPoints(612, 792, dpi));
    }

    [Fact]
    public void TheExportedSizeOfAPageKeepingItsResolutionIsTheImagesOwn()
    {
        Assert.Equal((500, 500), OutputPixelDimensions.ExportSize(720, 720, 150, true, (500, 500)));
    }

    [Fact]
    public void TheExportedSizeFollowsTheDpiBoxWhenResolutionIsNotKept()
    {
        Assert.Equal((1500, 1500), OutputPixelDimensions.ExportSize(720, 720, 150, false, (500, 500)));
    }

    [Fact]
    public void ThePageWithNoSingleImageExportsAtTheDpiBoxEvenWhenResolutionIsKept()
    {
        Assert.Equal((1500, 1500), OutputPixelDimensions.ExportSize(720, 720, 150, true, null));
    }

    [Fact]
    public void NativeDpiFallsBackWithoutAPageImage()
    {
        Assert.Equal(150, OutputPixelDimensions.NativeDpi(null, 720, 720, 150));
    }

    [Fact]
    public void OneTimesMeansTheRealImageNotThePreview()
    {
        var (width, height) = OutputPixelDimensions.ScaleBase(1100, 1100, 1100, 1100, (500, 500));
        Assert.Equal("x1", OutputPixelDimensions.ScaleLabel(500, 500, width, height));
        Assert.Equal("x2", OutputPixelDimensions.ScaleLabel(1000, 1000, width, height));
    }

    [Fact]
    public void APreviewScaledInTheWindowScalesItsBaseWithIt()
    {
        Assert.Equal((250d, 250d), OutputPixelDimensions.ScaleBase(550, 550, 1100, 1100, (500, 500)));
        Assert.Equal((800d, 600d), OutputPixelDimensions.ScaleBase(800, 600, 1100, 1100, null));
    }

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

    [Fact]
    public void MatchesDpi_AllowsRendererRoundingButRejectsAnotherResolution()
    {
        Assert.True(OutputPixelDimensions.MatchesDpi(1275, 1651, 612, 792, 150));
        Assert.True(OutputPixelDimensions.MatchesDpi(1274, 1650, 612, 792, 150));
        Assert.False(OutputPixelDimensions.MatchesDpi(2550, 3300, 612, 792, 150));
    }
}
