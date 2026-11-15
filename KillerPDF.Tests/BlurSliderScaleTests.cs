using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #467: the replacement-text blur slider is logarithmic. Light blur, which is what people use, takes most of the
// travel; the strongest blur sits in a short stretch at the right end.
public sealed class BlurSliderScaleTests
{
    [Fact]
    public void TheEndsAreNoBlurAndTheStrongest()
    {
        Assert.Equal(0, BlurSliderScale.ToPoints(0), 6);
        Assert.Equal(BlurSliderScale.MaximumPoints, BlurSliderScale.ToPoints(1), 6);
    }

    [Fact]
    public void LightBlurTakesMostOfTheTravel()
    {
        // Half the slider still reads well under a quarter of the strongest blur...
        Assert.True(BlurSliderScale.ToPoints(.5) < BlurSliderScale.MaximumPoints / 4);
        // ...and a quarter of the strongest blur needs more than half the travel.
        Assert.True(BlurSliderScale.ToSlider(BlurSliderScale.MaximumPoints / 4) > .5);
    }

    [Fact]
    public void TheCurveRisesEverywhere()
    {
        double previous = -1;
        for (int step = 0; step <= 100; step++)
        {
            double points = BlurSliderScale.ToPoints(step / 100d);
            Assert.True(points > previous);
            previous = points;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.05)]
    [InlineData(.3)]
    [InlineData(1)]
    [InlineData(2)]
    public void ABlurComesBackToTheSamePlace(double points) =>
        Assert.Equal(points, BlurSliderScale.ToPoints(BlurSliderScale.ToSlider(points)), 9);

    [Fact]
    public void BlurBeyondTheRangeSitsAtTheEnds()
    {
        Assert.Equal(1, BlurSliderScale.ToSlider(5), 9);
        Assert.Equal(0, BlurSliderScale.ToSlider(-1), 9);
    }
}
