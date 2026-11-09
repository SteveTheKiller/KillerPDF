using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ViewerRenderResolutionTests
{
    [Theory]
    [InlineData(1.0, 1.0, 1.0, 2048)]
    [InlineData(1.5, 1.5, 1.0, 3072)]
    [InlineData(1.0, 1.0, 2.0, 4096)]
    [InlineData(2.0, 2.0, 2.0, 6144)]
    public void TwoPageSecondaryMatchesPrimary(
        double dpiX, double dpiY, double zoom, int expected)
    {
        Assert.Equal(expected, ViewerRenderResolution.Primary(dpiX, dpiY, zoom));
        Assert.Equal(expected, ViewerRenderResolution.Secondary(true, dpiX, dpiY, zoom));
    }

    // A Grid tile is drawn at the size it shows in device pixels: the tile's longest side in DIPs (the render canvas,
    // 2048 for every page) times the view zoom times the monitor scale, rounded up. 98% in Grid is a view zoom of
    // about 0.49 (1003.5 px), 294% about 1.47 (3010.6 px). A 200% monitor at 294% would need 6021 px, which is capped
    // at 4096 px a side (64 MB of BGRA), as the largest page drawn anywhere stays within the primary's 6144.
    [Theory]
    [InlineData(2048, 1.0, 1.0, 0.49, 1004)]
    [InlineData(2048, 1.0, 1.0, 1.47, 3011)]
    [InlineData(2048, 1.5, 1.25, 0.49, 1506)]
    [InlineData(2048, 2.0, 2.0, 1.47, 4096)]
    [InlineData(2048, 1.0, 1.0, 0.01, 64)]
    public void GridTileMatchesItsSizeOnScreen(double longestDip, double dpiX, double dpiY, double zoom, int expected)
    {
        Assert.Equal(expected, ViewerRenderResolution.GridTile(longestDip, dpiX, dpiY, zoom));
    }

    [Fact]
    public void GridSecondaryRetainsMemoryLimitedBudget()
    {
        Assert.Equal(1536, ViewerRenderResolution.Secondary(false, 1.0, 1.0, 4.0));
        Assert.Equal(3072, ViewerRenderResolution.Secondary(false, 2.0, 2.0, 4.0));
    }
}
