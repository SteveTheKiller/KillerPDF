using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrintOrientationPolicyTests
{
    [Fact]
    public void IsLandscape_UsesCurrentPageOrientation()
    {
        double[] widths = [612, 792];
        double[] heights = [792, 612];

        Assert.False(PrintOrientationPolicy.IsLandscape(widths, heights, 0));
        Assert.True(PrintOrientationPolicy.IsLandscape(widths, heights, 1));
    }

    [Fact]
    public void IsLandscape_FallsBackToFirstValidPage()
    {
        double[] widths = [double.NaN, 792, 612];
        double[] heights = [792, 612, 792];

        Assert.True(PrintOrientationPolicy.IsLandscape(widths, heights, 8));
    }

    [Fact]
    public void IsLandscape_DefaultsToPortraitWithoutValidDimensions()
    {
        Assert.False(PrintOrientationPolicy.IsLandscape([0, double.PositiveInfinity], [792, 612], -1));
    }

    [Fact]
    public void IsLandscape_TreatsSquarePageAsPortrait()
    {
        Assert.False(PrintOrientationPolicy.IsLandscape([612], [612], 0));
    }
}
