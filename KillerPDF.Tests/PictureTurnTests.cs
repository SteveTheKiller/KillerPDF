using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The picture card's rotate buttons turn a pasted picture in place: it keeps its center, its footprint swaps on a
// quarter turn, and its Rotation adds the turn so the preview and the saved page draw it turned.
public sealed class PictureTurnTests
{
    [Fact]
    public void AQuarterTurnSwapsTheFootprintAroundTheCenter()
    {
        var picture = new ImageAnnotation { Position = new Point(100, 50), Scale = 0.5, SourceWidth = 400, SourceHeight = 200 };

        PictureTurn.Turn(picture, 90);

        Assert.Equal(90, picture.Rotation);
        Assert.Equal(200, picture.SourceWidth);
        Assert.Equal(400, picture.SourceHeight);
        Assert.Equal(new Point(150, 0), picture.Position);   // 200x100 centered at (200,100) becomes 100x200
    }

    [Fact]
    public void TurnsWrapAndUpsideDownKeepsTheFootprint()
    {
        var picture = new ImageAnnotation { Position = new Point(10, 20), Scale = 1, SourceWidth = 30, SourceHeight = 40, Rotation = 270 };

        PictureTurn.Turn(picture, 180);
        Assert.Equal(90, picture.Rotation);
        Assert.Equal(new Point(10, 20), picture.Position);
        Assert.Equal(30, picture.SourceWidth);

        PictureTurn.Turn(picture, -90);
        Assert.Equal(0, picture.Rotation);
    }
}
