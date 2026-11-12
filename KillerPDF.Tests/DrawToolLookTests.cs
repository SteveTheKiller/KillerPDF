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
    public void AChangeMadeOnTheBarWhileSelectedStays()
    {
        var tool = Look(Yellow);
        var mark = Look(Blue, opacity: 255);
        var lease = new DrawLookLease();
        lease.Borrow(tool);
        lease.Note(mark);
        var settled = lease.Release(mark with { Highlight = Red })!;
        Assert.Equal(Red, settled.Highlight);
        Assert.Equal(tool.Opacity, settled.Opacity);   // the opacity was never touched, so the tool's comes back
    }

    [Fact]
    public void SelectingTheSameMarkAgainKeepsTheChoiceMadeInBetween()
    {
        // Review BF-004: a resize re-selects the mark without deselecting it. The red picked on the bar before the
        // resize must survive the second sync and the deselect.
        var tool = Look(Yellow);
        var mark = Look(Blue);
        var picked = mark with { Highlight = Red };   // the user picks red on the bar; the mark turns red too
        var lease = new DrawLookLease();
        lease.Borrow(tool);
        lease.Note(mark);
        lease.Borrow(picked);                          // the resize selects the mark again
        lease.Note(picked);                            // and the sync reads the (now red) mark
        Assert.Equal(Red, lease.Release(picked)!.Highlight);
    }

    [Fact]
    public void NothingBorrowedReleasesNothing() => Assert.Null(new DrawLookLease().Release(Look(Yellow)));
}
