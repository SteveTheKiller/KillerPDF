using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #495: a drop in the page view goes where the marker shows, between the tiles the pointer is between. The tiles'
// margins are transparent to hit tests, so a hit-test slot sent a drop between tiles to the end of the list.
public sealed class MergeDropSlotTests
{
    // Two rows of three 100 x 150 tiles, with a 10 pixel gap between tiles and rows, and a last row of one.
    private static readonly Rect[] Tiles =
    [
        new(0, 0, 100, 150), new(110, 0, 100, 150), new(220, 0, 100, 150),
        new(0, 160, 100, 150), new(110, 160, 100, 150), new(220, 160, 100, 150),
        new(0, 320, 100, 150),
    ];

    [Theory]
    [InlineData(105, 75, 1)]     // in the gap between the first two tiles
    [InlineData(30, 75, 0)]      // the left half of the first tile
    [InlineData(80, 75, 1)]      // the right half of the first tile
    [InlineData(215, 155, 2)]    // in the gap between rows, level with the row above
    [InlineData(400, 75, 3)]     // right of a row's last tile: after it, not at the end
    [InlineData(105, 235, 4)]    // the second row's first gap
    [InlineData(50, 500, 7)]     // below the last row
    [InlineData(400, 400, 7)]    // right of the last tile
    public void ADropGoesBetweenTheTilesThePointerIsBetween(double x, double y, int expected)
    {
        Assert.Equal(expected, MergeDropSlot.Find(Tiles, new Point(x, y)).Index);
    }

    [Fact]
    public void TheMarkerSitsOnTheEdgeOfTheTileTheDropGoesBeforeOrAfter()
    {
        var before = MergeDropSlot.Find(Tiles, new Point(105, 75));
        Assert.Equal((110d, 0d, 150d), (before.X, before.Top, before.Bottom));

        var afterRow = MergeDropSlot.Find(Tiles, new Point(400, 200));
        Assert.Equal((320d, 160d, 310d), (afterRow.X, afterRow.Top, afterRow.Bottom));
    }

    [Fact]
    public void AnEmptyViewTakesTheDropAtTheStart()
    {
        Assert.Equal(0, MergeDropSlot.Find([], new Point(50, 50)).Index);
    }
}
