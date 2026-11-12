using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// 1.9.4: a selected mark lends the draw bar its look; deselecting gives the tool its own look back, except for what
// the user changed on the bar in between.
public sealed class DrawToolLookTests
{
    private static readonly Color Yellow = Color.FromArgb(110, 255, 230, 0);
    private static readonly Color Blue = Color.FromArgb(255, 0, 90, 255);
    private static readonly Color Red = Color.FromArgb(255, 230, 20, 20);
    private static readonly Color Black = Colors.Black;

    private static DrawToolLook Look(Color highlight, byte opacity = 110, double width = 3) => new(highlight, Black, Black, opacity, width);

    [Fact]
    public void AnUntouchedBarGoesBackToTheToolsLook()
    {
        var tool = Look(Yellow);
        var mark = Look(Blue, opacity: 255, width: 12);
        var lease = new DrawLookLease();
        lease.Borrow(tool);
        lease.Note(mark);
        Assert.Equal(tool, lease.Release(mark));
        Assert.False(lease.IsActive);
    }

    [Fact]
    public void AColorPickedOnASelectedSolidHighlightKeepsTheHighlightersOpacity()
    {
        // Review r2 BF-001: the bar writes a picked color with the mark's alpha. The hue is the choice; the 100%
        // alpha came from the mark and must not make every later highlight solid (the 1.9.3 Reddit report).
        var tool = Look(Yellow);                                  // the highlighter: yellow at 110
        var mark = Look(Blue);                                    // a solid blue highlight, alpha 255
        var lease = new DrawLookLease();
        lease.Borrow(tool);
        lease.Note(mark);
        var settled = lease.Release(mark with { Highlight = Color.FromArgb(255, Red.R, Red.G, Red.B) })!;
        Assert.Equal(Color.FromArgb(110, Red.R, Red.G, Red.B), settled.Highlight);
        Assert.Equal(tool.Opacity, settled.Opacity);
    }

    [Fact]
    public void AColorPickedOnASelectedStrokeKeepsThePensOpacity()
    {
        var tool = new DrawToolLook(Yellow, Black, Color.FromArgb(255, 0, 0, 0), 255, 3);
        var mark = new DrawToolLook(Yellow, Black, Color.FromArgb(100, 0, 90, 255), 100, 12);
        var lease = new DrawLookLease();
        lease.Borrow(tool);
        lease.Note(mark);
        var settled = lease.Release(mark with { Draw = Color.FromArgb(100, 0, 200, 0) })!;   // ApplyPick: picked green, mark alpha
        Assert.Equal(Color.FromArgb(255, 0, 200, 0), settled.Draw);
        Assert.Equal(255, settled.Opacity);
        Assert.Equal(3, settled.Width);
    }

    [Fact]
    public void DraggingOnlyTheOpacityKeepsTheToolsColor()
    {
        var tool = Look(Yellow);
        var mark = Look(Color.FromArgb(200, 255, 0, 255));
        var lease = new DrawLookLease();
        lease.Borrow(tool);
        lease.Note(mark);
        var settled = lease.Release(mark with { Highlight = Color.FromArgb(120, 255, 0, 255) })!;   // the slider rewrites the alpha
        Assert.Equal(Color.FromArgb(120, Yellow.R, Yellow.G, Yellow.B), settled.Highlight);
    }

    [Fact]
    public void SelectingTheSameMarkAgainKeepsTheChoiceMadeInBetween()
    {
        // Review BF-004: a resize re-selects the mark without deselecting it. The red picked on the bar before the
        // resize must survive the second sync and the deselect.
        var tool = Look(Yellow);
        var mark = Look(Blue);
        var picked = mark with { Highlight = Color.FromArgb(Blue.A, Red.R, Red.G, Red.B) };   // red picked on the bar; the mark turns red
        var lease = new DrawLookLease();
        lease.Borrow(tool);
        lease.Note(mark);
        lease.Borrow(picked);                          // the resize selects the mark again
        lease.Note(picked);                            // and the sync reads the (now red) mark
        Assert.Equal(Color.FromArgb(Yellow.A, Red.R, Red.G, Red.B), lease.Release(picked)!.Highlight);
    }

    [Fact]
    public void NothingBorrowedReleasesNothing() => Assert.Null(new DrawLookLease().Release(Look(Yellow)));
}
