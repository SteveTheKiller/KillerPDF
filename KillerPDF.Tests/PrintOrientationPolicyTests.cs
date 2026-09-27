using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrintOrientationPolicyTests
{
    [Fact]
    public void UseLandscape_UsesFirstValidPageDimensions()
    {
        Assert.True(PrintOrientationPolicy.UseLandscape([0, 792], [0, 612]));
        Assert.False(PrintOrientationPolicy.UseLandscape([612], [792]));
    }

    [Fact]
    public void UseLandscape_DefaultsToPortraitWithoutValidDimensions()
    {
        Assert.False(PrintOrientationPolicy.UseLandscape([], []));
        Assert.False(PrintOrientationPolicy.UseLandscape([0], [792]));
    }
}
