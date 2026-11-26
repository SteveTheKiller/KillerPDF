using System.Windows;
using KillerPDF.Controls;
using Xunit;
using Edges = KillerPDF.Controls.AltDrag.ResizeEdges;

namespace KillerPDF.Tests;

// Steve 2026-11-26: Alt+right-drag resizes any window from the nearest edge or corner, as on Linux.
// These pin which edges a press picks and how far the window may grow or shrink.
public sealed class AltDragResizeTests
{
    private static readonly Size Window900x600 = new(900, 600);

    [Theory]
    [InlineData(50, 50, Edges.Left | Edges.Top)]          // top-left third
    [InlineData(850, 550, Edges.Right | Edges.Bottom)]    // bottom-right third
    [InlineData(450, 50, Edges.Top)]                      // top edge, middle third across
    [InlineData(850, 300, Edges.Right)]                   // right edge, middle third down
    [InlineData(400, 250, Edges.Left | Edges.Top)]        // the middle: nearest corner
    [InlineData(500, 350, Edges.Right | Edges.Bottom)]
    public void APressPicksTheEdgesOfItsThird(double x, double y, object expected)   // object: the enum is internal
        => Assert.Equal((Edges)expected, AltDrag.EdgesAt(new Point(x, y), Window900x600));

    [Fact]
    public void DraggingTheBottomRightCornerGrowsTheWindowAndKeepsItsTopLeft()
    {
        Rect r = AltDrag.Resized(new Rect(100, 50, 900, 600), new Vector(120, 80), Edges.Right | Edges.Bottom,
            new Size(200, 150), new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(new Rect(100, 50, 1020, 680), r);
    }

    [Fact]
    public void DraggingTheLeftEdgeMovesItAndKeepsTheRightEdge()
    {
        Rect r = AltDrag.Resized(new Rect(100, 50, 900, 600), new Vector(-40, 30), Edges.Left,
            new Size(200, 150), new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(new Rect(60, 50, 940, 600), r);
    }

    // The top-left corner pulled far past the bottom-right stops at the minimum, with the
    // bottom-right corner where it was: right 1000 - 200 = 800, bottom 650 - 150 = 500.
    [Fact]
    public void AWindowNeverShrinksBelowItsMinimum()
    {
        Rect r = AltDrag.Resized(new Rect(100, 50, 900, 600), new Vector(5000, 5000), Edges.Left | Edges.Top,
            new Size(200, 150), new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(new Rect(800, 500, 200, 150), r);
    }

    [Fact]
    public void AWindowNeverGrowsPastItsMaximum()
    {
        Rect r = AltDrag.Resized(new Rect(100, 50, 900, 600), new Vector(500, 500), Edges.Right | Edges.Bottom,
            new Size(200, 150), new Size(1000, 640));

        Assert.Equal(new Rect(100, 50, 1000, 640), r);
    }
}
