using System.Windows;
using System.Windows.Media;
using KillerPDF.Presentation;
using Xunit;

namespace KillerPDF.Tests;

// #534: the native page window cuts whatever the main window floats over the page out of its region. These check the
// geometry the region is built from; the window placement itself needs a window and is covered by the UiSmoke runs.
public sealed class ViewportOcclusionTests
{
    static readonly Rect Page = new(0, 0, 600, 800);

    [Fact]
    public void NoHolesLeavesTheClipAsItIs()
    {
        var clip = new RectangleGeometry(Page);
        Assert.Same(clip, ViewportOcclusion.Cut(clip, []));
    }

    [Fact]
    public void ABarOverThePageIsCutOutWithAPixelOfMargin()
    {
        var cut = ViewportOcclusion.Cut(new RectangleGeometry(Page), [new Rect(100, 0, 300, 40)]);

        Assert.False(cut.FillContains(new Point(250, 20)));
        Assert.False(cut.FillContains(new Point(99.5, 20)));   // the padding keeps the bar's antialiased edge
        Assert.False(cut.FillContains(new Point(250, 40.5)));
        Assert.True(cut.FillContains(new Point(50, 20)));
        Assert.True(cut.FillContains(new Point(250, 60)));
        Assert.True(cut.IsFrozen);
    }

    [Fact]
    public void AFullWindowOverlayEmptiesTheRegion()
    {
        var cut = ViewportOcclusion.Cut(new RectangleGeometry(Page), [new Rect(-500, -500, 2000, 2000)]);
        Assert.True(cut.IsEmpty() || cut.GetArea() < 1e-6);
    }

    [Fact]
    public void AHoleOutsideThePageChangesNothing()
    {
        var cut = ViewportOcclusion.Cut(new RectangleGeometry(Page), [new Rect(700, 0, 50, 50)]);
        Assert.Equal(Page.Width * Page.Height, cut.GetArea(), 1);
    }

    [Fact]
    public void SeveralHolesAreAllCut()
    {
        var cut = ViewportOcclusion.Cut(new RectangleGeometry(Page),
            [new Rect(0, 0, 600, 30), new Rect(500, 760, 80, 26), Rect.Empty, new Rect(10, 10, 0, 0)]);

        Assert.False(cut.FillContains(new Point(300, 10)));
        Assert.False(cut.FillContains(new Point(540, 770)));
        Assert.True(cut.FillContains(new Point(300, 400)));
    }
}
