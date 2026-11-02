using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// R6-008: a limited page dropped into another window asks for the owner password in the window that
// holds the source tab, and no window points its chrome at a pane of another window.
public sealed class PageDropRouteTests
{
    private sealed class Window(string name)
    {
        public override string ToString() => name;
    }

    [Fact]
    public void ALimitedDropFromAnotherWindowIsGatedInTheSourcesWindow()
    {
        Window drop = new("drop"), source = new("source");

        Assert.Same(source, PageDropRoute.GateWindow(drop, source));
    }

    [Fact]
    public void ALimitedDropWithinOneWindowIsGatedThere()
    {
        Window window = new("one");

        Assert.Same(window, PageDropRoute.GateWindow(window, window));
    }

    // A source whose window cannot be found has no owner to ask, so nothing gates the drop and it is
    // refused. Gating it where it landed would ask the target tab, which holds none of the source's
    // limits, and let the pages through.
    [Fact]
    public void ALimitedDropWhoseSourceHasNoWindowIsRefused()
    {
        Window window = new("drop");

        Assert.Null(PageDropRoute.GateWindow<Window>(window, null));
    }

    [Fact]
    public void AWindowFocusesOnlyItsOwnPanes()
    {
        object first = new(), second = new(), foreign = new();

        Assert.True(PageDropRoute.MayFocus(first, first, second));
        Assert.True(PageDropRoute.MayFocus(second, first, second));
        Assert.False(PageDropRoute.MayFocus(foreign, first, second));
    }
}
