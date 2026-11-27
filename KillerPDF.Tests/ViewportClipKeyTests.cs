using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Presentation;
using Xunit;

namespace KillerPDF.Tests;

// BF-A-014: the native viewport used to rebuild its whole clip on every layout pass. The key stands for everything the
// clip is built from, so a pass that changed none of it can stop before any geometry is combined.
public sealed class ViewportClipKeyTests
{
    static readonly Rect[] NoHoles = [];

    sealed class Rig
    {
        public Border Frame = new() { Width = 400, Height = 300, ClipToBounds = true };
        public Border Viewport = new() { Width = 300, Height = 200 };
        public Rig()
        {
            Frame.Child = new Canvas { Children = { Viewport } };
            Layout();
        }
        public void Layout()
        {
            Frame.Measure(new Size(1000, 1000));
            Frame.Arrange(new Rect(0, 0, Frame.DesiredSize.Width, Frame.DesiredSize.Height));
            Frame.UpdateLayout();
        }
        public ViewportClipKey Key(IReadOnlyList<Rect>? holes = null, uint dpi = 96, int width = 300, int height = 200) =>
            ViewportClipKey.Build(Viewport, holes ?? NoHoles, dpi, width, height);
    }

    static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void AnUnchangedLayoutGivesAnEqualKey() => RunSta(() =>
    {
        var rig = new Rig();
        Assert.True(rig.Key([new Rect(10, 0, 100, 30)]).IsSameAs(rig.Key([new Rect(10, 0, 100, 30)])));
    });

    [Fact]
    public void NoPreviousKeyIsNeverTheSame() => RunSta(() =>
    {
        Assert.False(new Rig().Key().IsSameAs(null));
    });

    [Fact]
    public void AHoleThatMovesOrResizesChangesTheKey() => RunSta(() =>
    {
        var rig = new Rig();
        var before = rig.Key([new Rect(10, 0, 100, 30)]);
        Assert.False(before.IsSameAs(rig.Key([new Rect(12, 0, 100, 30)])));
        Assert.False(before.IsSameAs(rig.Key([new Rect(10, 0, 100, 40)])));
        Assert.False(before.IsSameAs(rig.Key([])));
        Assert.False(before.IsSameAs(rig.Key([new Rect(10, 0, 100, 30), new Rect(0, 0, 5, 5)])));
    });

    [Fact]
    public void DpiAndPixelSizeChangeTheKey() => RunSta(() =>
    {
        var rig = new Rig();
        var before = rig.Key();
        Assert.False(before.IsSameAs(rig.Key(dpi: 144)));
        Assert.False(before.IsSameAs(rig.Key(width: 301)));
        Assert.False(before.IsSameAs(rig.Key(height: 199)));
    });

    [Fact]
    public void TheViewportsOwnSizeChangesTheKey() => RunSta(() =>
    {
        var rig = new Rig();
        var before = rig.Key();
        rig.Viewport.Width = 250;
        rig.Layout();
        Assert.False(before.IsSameAs(rig.Key()));
    });

    [Fact]
    public void AnAncestorThatClipsMoreOfTheViewportChangesTheKey() => RunSta(() =>
    {
        var rig = new Rig();
        var before = rig.Key();
        // The viewport keeps its own size, but the clipping frame around it shrinks, which cuts the page window.
        rig.Frame.Width = 150;
        rig.Layout();
        Assert.False(before.IsSameAs(rig.Key()));
    });

    [Fact]
    public void AClippingAncestorThatSlidesChangesTheKey() => RunSta(() =>
    {
        var rig = new Rig();
        var before = rig.Key();
        rig.Frame.RenderTransform = new TranslateTransform(40, 0);
        rig.Viewport.RenderTransform = Transform.Identity;
        rig.Layout();
        // The frame moved on its transform; the viewport sits at the same place inside it, so the key must follow the
        // frame's clip relative to the viewport, which is unchanged here, and stay equal.
        Assert.True(before.IsSameAs(rig.Key()));

        var viewportMoved = rig.Key();
        rig.Viewport.RenderTransform = new TranslateTransform(60, 0);
        rig.Layout();
        Assert.False(viewportMoved.IsSameAs(rig.Key()));
    });

    [Fact]
    public void ACurvedClipIsNeverTreatedAsUnchanged() => RunSta(() =>
    {
        var rig = new Rig();
        rig.Frame.Clip = new EllipseGeometry(new Rect(0, 0, 400, 300));
        rig.Layout();
        Assert.False(rig.Key().IsSameAs(rig.Key()));
    });

    [Fact]
    public void ARectangleClipThatChangesItsCornersChangesTheKey() => RunSta(() =>
    {
        var rig = new Rig();
        rig.Frame.ClipToBounds = false;   // an explicit clip together with the layout clip is one combined shape, which is never compared
        rig.Frame.Clip = new RectangleGeometry(new Rect(0, 0, 400, 300), 0, 0);
        rig.Layout();
        var square = rig.Key();
        Assert.True(square.IsSameAs(rig.Key()));
        rig.Frame.Clip = new RectangleGeometry(new Rect(0, 0, 400, 300), 20, 20);
        rig.Layout();
        Assert.False(square.IsSameAs(rig.Key()));
    });
}
