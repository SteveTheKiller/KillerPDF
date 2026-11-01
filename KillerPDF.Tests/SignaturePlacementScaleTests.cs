using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SignaturePlacementScaleTests
{
    [Theory]
    [InlineData(false, 0.5)]
    [InlineData(true, 0.3)]
    public void StartsAtTheUsualPlacedSize(bool initials, double expected)
    {
        Assert.Equal(expected, SignaturePlacementScale.Default(initials));
    }

    [Fact]
    public void OneWheelNotchChangesTheSizeByTenPercent()
    {
        Assert.Equal(0.55, SignaturePlacementScale.Step(0.5, 120), 6);
        Assert.Equal(0.5 / 1.1, SignaturePlacementScale.Step(0.5, -120), 6);
    }

    [Fact]
    public void TheSizeStaysUsable()
    {
        Assert.Equal(SignaturePlacementScale.Minimum, SignaturePlacementScale.Step(SignaturePlacementScale.Minimum, -120));
        Assert.Equal(SignaturePlacementScale.Maximum, SignaturePlacementScale.Step(SignaturePlacementScale.Maximum, 120));
    }
}
