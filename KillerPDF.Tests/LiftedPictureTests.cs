using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A picture lifted from a page shown turned (a /Rotate page, or one turned in the app) is copied turned the same
// way, so cut and paste puts it back as it was instead of upright and sticking out past the page.
public sealed class LiftedPictureTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    public void ALiftedPictureTakesThePagesTurn(int pageRotation, int expected)
    {
        var bounds = new Rect(10, 20, 40, 120);

        ImageAnnotation copy = LiftedPicture.Copy(3, bounds, pageRotation);

        Assert.Equal(expected, copy.Rotation);
        Assert.Equal(3, copy.PageIndex);
        Assert.Equal(bounds.TopLeft, copy.Position);
        Assert.Equal((40.0, 120.0), (copy.SourceWidth, copy.SourceHeight));
        Assert.Equal(1, copy.Scale);
    }
}
