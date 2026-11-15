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
        var bar = new Border { Width = 200, Height = 30 };
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

    [Fact]
    public void FloatingTheBarIsCenteredNearTheTopAndDrawnOverThePage() => RunSta(() =>
    {
        var (dock, bar, _) = Laid(floating: true);
        Rect barBounds = Bounds(bar, dock);
        Assert.Equal(200, barBounds.Width);
        Assert.Equal(200, barBounds.Left);
        Assert.InRange(barBounds.Top, 1, 16);
        Assert.Same(bar, VisualTreeHelper.GetChild(dock, VisualTreeHelper.GetChildrenCount(dock) - 1));
    });

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
