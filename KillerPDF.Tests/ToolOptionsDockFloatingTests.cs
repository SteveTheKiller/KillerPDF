using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// #485: a docked bar pushes the page down when a tool is chosen. Floating, the page keeps its place and
// the bar sits over its top edge.
public sealed class ToolOptionsDockFloatingTests
{
    private static (ToolOptionsDock Dock, Border Bar, Border Page) Laid(bool floating)
    {
        var bar = new Border { Width = 200, Height = 30, Child = new Button() };
        var page = new Border();
        var dock = new ToolOptionsDock { Floating = floating, Document = page, Bar = bar };
        dock.Measure(new Size(600, 400));
        dock.Arrange(new Rect(0, 0, 600, 400));
        return (dock, bar, page);
    }

    private static Rect Bounds(UIElement child, Visual parent) =>
        child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));

    [Fact]
    public void DockedTheBarTakesItsHeightAboveThePage() => RunSta(() =>
    {
        var (dock, bar, page) = Laid(floating: false);
        Assert.Equal(30, Bounds(page, dock).Top);
        Assert.Equal(370, Bounds(page, dock).Height);
        Assert.Equal(0, Bounds(bar, dock).Top);
    });

    [Fact]
    public void FloatingThePageKeepsTheWholeAreaWithOrWithoutABar() => RunSta(() =>
    {
        var (dock, _, page) = Laid(floating: true);
        Assert.Equal(new Rect(0, 0, 600, 400), Bounds(page, dock));
        dock.Bar = null;
        dock.Measure(new Size(600, 400));
        dock.Arrange(new Rect(0, 0, 600, 400));
        Assert.Equal(new Rect(0, 0, 600, 400), Bounds(page, dock));
    });

    // Like the 1.9.2 bar: it hangs from the top edge, right-anchored until moved, and is drawn over the page.
    [Fact]
    public void FloatingTheBarHangsFromTheTopEdgeAtTheRightAndIsDrawnOverThePage() => RunSta(() =>
    {
        var (dock, bar, page) = Laid(floating: true);
        Rect barBounds = Bounds(bar, dock);
        Assert.Equal(new Rect(600 - 200 - ToolOptionsDock.FloatingGap, 0, 200, 30), barBounds);
        var order = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(dock)).Select(i => VisualTreeHelper.GetChild(dock, i)).ToList();
        Assert.True(order.IndexOf(bar) > order.IndexOf(page));
    });

    [Fact]
    public void ASlidBarKeepsItsShareOfTheWidthAndStaysInside() => RunSta(() =>
    {
        var (dock, bar, _) = Laid(floating: true);
        dock.SlideTo(100);
        Relayout(dock, 600);
        Assert.Equal(100, Bounds(bar, dock).Left);
        Relayout(dock, 1200);
        Assert.Equal(300, Bounds(bar, dock).Left);   // its center stays at a third of the width: 400 - 100
        dock.SlideTo(-50);
        Relayout(dock, 1200);
        Assert.Equal(0, Bounds(bar, dock).Left);
        dock.SlideTo(5000);
        Relayout(dock, 1200);
        Assert.Equal(1000, Bounds(bar, dock).Left);
    });

    [Fact]
    public void MinimizingLeavesAThinStripInTheBarsPlace() => RunSta(() =>
    {
        var (dock, bar, page) = Laid(floating: true);
        double left = Bounds(bar, dock).Left;
        dock.ToggleMinimized();
        Relayout(dock, 600);
        Assert.True(dock.Minimized);
        // The bar stays the click target, cut down to the strip with its controls hidden, as in 1.9.2.
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal(new Rect(0, 0, 200, ToolOptionsDock.MinimizedHeight), bar.Clip!.Bounds);
        Assert.Equal(0, bar.Child.Opacity);
        Assert.False(bar.Child.IsHitTestVisible);
        Assert.Equal(new Rect(left, 0, 200, ToolOptionsDock.MinimizedHeight), Bounds(dock.MinimizedStrip!, dock));
        Assert.False(dock.MinimizedStrip!.IsHitTestVisible);
        Assert.Equal(new Rect(0, 0, 600, 400), Bounds(page, dock));
        dock.ToggleMinimized();
        Relayout(dock, 600);
        Assert.Null(bar.Clip);
        Assert.Equal(1, bar.Child.Opacity);
        Assert.True(bar.Child.IsHitTestVisible);
    });

    // Floating, the bar reads as a tab hanging from the edge: side and bottom borders, rounded bottom
    // corners, and grip dots on its left that say it can be dragged. Docked, it is a plain row again.
    [Fact]
    public void AFloatingBarHangsWithSideBordersRoundedBottomCornersAndAGrip() => RunSta(() =>
    {
        var (dock, bar, _) = Laid(floating: true);
        Assert.Equal(new Thickness(1, 0, 1, 1), bar.BorderThickness);
        Assert.Equal(new CornerRadius(0, 0, ToolOptionsDock.FloatingCornerRadius, ToolOptionsDock.FloatingCornerRadius), bar.CornerRadius);
        Assert.Equal(ToolOptionsDock.GripWidth, bar.Padding.Left);
        Rect grip = Bounds(dock.Grip!, dock);
        Rect barBounds = Bounds(bar, dock);
        Assert.Equal(barBounds.Left + 1, grip.Left);
        Assert.Equal(0, grip.Top);
        Assert.Equal(barBounds.Height, grip.Height);

        dock.Floating = false;
        Relayout(dock, 600);
        Assert.Equal(new Thickness(0), bar.BorderThickness);
        Assert.Equal(new CornerRadius(0), bar.CornerRadius);
        Assert.Equal(0, bar.Padding.Left);
        Assert.Null(dock.Grip);
    });

    [Fact]
    public void AMinimizedBarShowsNoGrip() => RunSta(() =>
    {
        var (dock, _, _) = Laid(floating: true);
        dock.ToggleMinimized();
        Relayout(dock, 600);
        Assert.Null(dock.Grip);
    });

    [Fact]
    public void DockedTheBarCannotBeMinimizedOrSlid() => RunSta(() =>
    {
        var (dock, bar, page) = Laid(floating: false);
        Rect before = Bounds(bar, dock);
        dock.ToggleMinimized();
        dock.SlideTo(0);
        Relayout(dock, 600);
        Assert.False(dock.Minimized);
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal(before, Bounds(bar, dock));
        Assert.Equal(30, Bounds(page, dock).Top);
    });

    private static void Relayout(ToolOptionsDock dock, double width)
    {
        dock.Measure(new Size(width, 400));
        dock.Arrange(new Rect(0, 0, width, 400));
    }

    [Fact]
    public void SwitchingModesMovesThePageWithoutRebuildingTheBar() => RunSta(() =>
    {
        var (dock, bar, page) = Laid(floating: false);
        dock.Floating = true;
        dock.UpdateLayout();
        dock.Measure(new Size(600, 400));
        dock.Arrange(new Rect(0, 0, 600, 400));
        Assert.Same(bar, dock.Bar);
        Assert.Equal(0, Bounds(page, dock).Top);
    });

    private static void RunSta(Action action)
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
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
