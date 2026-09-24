using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PinchZoomMathTests
{
    [Fact]
    public void ReusesOneContentAnchorAcrossRapidWheelUpdates()
    {
        (double x, double y) = PinchZoomMath.CaptureAnchor(
            zoom: 1, horizontalOffset: 100, verticalOffset: 200,
            originX: 300, originY: 250);

        PinchZoomResult first = PinchZoomMath.ApplyAtAnchor(
            zoom: 1.1, x, y, originX: 300, originY: 250);
        PinchZoomResult second = PinchZoomMath.ApplyAtAnchor(
            zoom: 1.21, x, y, originX: 300, originY: 250);

        Assert.Equal(140, first.HorizontalOffset, 6);
        Assert.Equal(245, first.VerticalOffset, 6);
        Assert.Equal(184, second.HorizontalOffset, 6);
        Assert.Equal(294.5, second.VerticalOffset, 6);
    }

    [Fact]
    public void ZoomsAroundTheGestureOrigin()
    {
        PinchZoomResult result = PinchZoomMath.Apply(
            oldZoom: 1, scale: 1.5, minimumZoom: .05, maximumZoom: 5,
            horizontalOffset: 100, verticalOffset: 200, originX: 300, originY: 250);

        Assert.Equal(1.5, result.Zoom);
        Assert.Equal(300, result.HorizontalOffset);
        Assert.Equal(425, result.VerticalOffset);
    }

    [Fact]
    public void UsesTheClampedScaleForOffsetsAtTheZoomLimit()
    {
        PinchZoomResult result = PinchZoomMath.Apply(
            oldZoom: 4, scale: 2, minimumZoom: .05, maximumZoom: 5,
            horizontalOffset: 100, verticalOffset: 80, originX: 200, originY: 120);

        Assert.Equal(5, result.Zoom);
        Assert.Equal(175, result.HorizontalOffset);
        Assert.Equal(130, result.VerticalOffset);
    }

    [Fact]
    public void DoesNotProduceNegativeScrollOffsetsWhenZoomingOut()
    {
        PinchZoomResult result = PinchZoomMath.Apply(
            oldZoom: 1, scale: .5, minimumZoom: .05, maximumZoom: 5,
            horizontalOffset: 0, verticalOffset: 0, originX: 200, originY: 120);

        Assert.Equal(.5, result.Zoom);
        Assert.Equal(0, result.HorizontalOffset);
        Assert.Equal(0, result.VerticalOffset);
    }
}
