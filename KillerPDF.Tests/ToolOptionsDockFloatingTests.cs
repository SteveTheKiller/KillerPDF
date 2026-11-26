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
        Rect clip = bar.Clip!.Bounds;
        Assert.Equal(200, clip.Width, 3);
        Assert.Equal(ToolOptionsDock.MinimizedHeight, clip.Height, 3);
        Assert.Equal(0, clip.Top, 3);
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
        Assert.Equal(barBounds.Height - 1, grip.Height);   // clear of the bottom border, so the dots sit centered

        dock.Floating = false;
        Relayout(dock, 600);
        Assert.Equal(new Thickness(0), bar.BorderThickness);
        Assert.Equal(new CornerRadius(0), bar.CornerRadius);
        Assert.Equal(0, bar.Padding.Left);
        Assert.Null(dock.Grip);
    });

    // The strip keeps the tab look: a bottom border along with the sides, and slightly rounded bottom corners.
    [Fact]
    public void AMinimizedStripHasABottomBorderAndRoundedBottomCorners() => RunSta(() =>
    {
        var (dock, bar, _) = Laid(floating: true);
        bar.BorderBrush = Brushes.Red;
        dock.ToggleMinimized();
        Relayout(dock, 600);
        var strip = Assert.IsType<Border>(dock.MinimizedStrip);
        Assert.Equal(new Thickness(1, 0, 1, 1), strip.BorderThickness);
        Assert.Same(Brushes.Red, strip.BorderBrush);
        Assert.Equal(new CornerRadius(0, 0, ToolOptionsDock.MinimizedCornerRadius, ToolOptionsDock.MinimizedCornerRadius), strip.CornerRadius);
        // The bar under the strip is cut to the same rounded shape, so no square corner shows past it.
        double h = ToolOptionsDock.MinimizedHeight;
        Assert.False(bar.Clip!.FillContains(new Point(0.3, h - 0.3)));
        Assert.True(bar.Clip.FillContains(new Point(0.5, 0.5)));
        Assert.True(bar.Clip.FillContains(new Point(100, h - 0.5)));
    });

    // Choosing another tool while minimized, or opening the app minimized, hands the dock a new bar. The
    // strip must keep that bar's width, or there is nothing left to double-click to bring it back.
    [Fact]
    public void ANewBarWhileMinimizedKeepsAFullWidthClickableStrip() => RunSta(() =>
    {
        var (dock, _, _) = Laid(floating: true);
        dock.ToggleMinimized();
        var next = new Border { Width = 180, Height = 30, Child = new Button(), Background = Brushes.Gray };
        dock.Bar = next;
        Relayout(dock, 600);
        Assert.Equal(Visibility.Visible, next.Visibility);
        Assert.Equal(180, Bounds(dock.MinimizedStrip!, dock).Width);
        Assert.Equal(180, next.RenderSize.Width);
    });

    // 98SE has square bars: a theme without rounding sets the corners to 0, and a theme switch applies at once.
    [Fact]
    public void SquareCornersApplyToTheBarAndTheStripAndFollowAThemeSwitch() => RunSta(() =>
    {
        var (dock, bar, _) = Laid(floating: true);
        dock.CornerRadius = 0;
        Relayout(dock, 600);
        Assert.Equal(new CornerRadius(0), bar.CornerRadius);
        dock.ToggleMinimized();
        Relayout(dock, 600);
        Assert.Equal(new CornerRadius(0), Assert.IsType<Border>(dock.MinimizedStrip).CornerRadius);
        dock.ToggleMinimized();
        dock.CornerRadius = ToolOptionsDock.FloatingCornerRadius;
        Relayout(dock, 600);
        Assert.Equal(new CornerRadius(0, 0, ToolOptionsDock.FloatingCornerRadius, ToolOptionsDock.FloatingCornerRadius), bar.CornerRadius);
    });

    // The bar's grain and 98SE dark lower edge span its full width, padding included. Floating widens the
    // left padding for the grip, and the edge has to follow it or the bottom line stops short of the grip.
    [Fact]
    public void AFloatingBarsTextureAndLowerEdgeStillReachItsLeftSide() => RunSta(() =>
    {
        var bar = new ToolOptionsBar(EditTool.Draw, "Draw options", "More settings");
        var dock = new ToolOptionsDock { Floating = true, Document = new Border(), Bar = bar };
        dock.Measure(new Size(900, 400));
        dock.Arrange(new Rect(0, 0, 900, 400));
        var layers = Assert.IsType<Grid>(bar.Child);
        foreach (var layer in layers.Children.OfType<Border>())
            Assert.Equal(-bar.Padding.Left, layer.Margin.Left);
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

    // Under the tab strip the active tab paints over the card's top border, so a bar slid beneath it
    // merged into the tab. Its top corner rounds while under the active tab, meets the border square
    // under an inactive one, and the dock reports where the border line has to be drawn back.
    private static (ToolOptionsDock Dock, Border Bar) UnderTheStrip((double, double)? openSpan)
    {
        var bar = new Border
        {
            Width = 200, Height = 30, Background = Brushes.Black, BorderBrush = Brushes.Lime,
        };
        var dock = new ToolOptionsDock { Floating = true, Document = new Border { Background = Brushes.White }, Bar = bar };
        dock.Resources["PaneBorderBrush"] = Brushes.Red;
        dock.SealsTop = true;
        dock.OpenSpan = openSpan;
        dock.Measure(new Size(600, 400));
        dock.Arrange(new Rect(0, 0, 600, 400));
        dock.SlideTo(4);
        dock.Measure(new Size(600, 400));
        dock.Arrange(new Rect(0, 0, 600, 400));
        return (dock, bar);
    }

    private static Func<int, int, Color> Pixels(UIElement element)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var data = new byte[600 * 400 * 4];
        bitmap.CopyPixels(data, 600 * 4, 0);
        return (x, y) =>
        {
            int i = (y * 600 + x) * 4;
            return Color.FromRgb(data[i + 2], data[i + 1], data[i]);
        };
    }

    [Fact]
    public void UnderTheActiveTabTheBarsTopCornerRoundsInThePaneBorderColor() => RunSta(() =>
    {
        var (dock, bar) = UnderTheStrip((0, 150));
        Assert.Equal(4, Bounds(bar, dock).Left);
        var at = Pixels(dock);
        // Outside the curve the page shows; the curve is the pane border's color.
        Assert.Equal(Colors.White, at(4, 0));
        Assert.Equal(Colors.White, at(5, 0));
        Assert.Equal(Colors.Red, at(6, 0));
        Assert.Equal(Colors.Red, at(7, 0));
        Assert.Equal(Colors.Black, at(8, 0));
        Assert.Equal(Colors.White, at(4, 1));
        Assert.Equal(Colors.Red, at(5, 1));
        Assert.Equal(Colors.Black, at(6, 1));
        Assert.Equal(Colors.White, at(4, 2));
        Assert.Equal(Colors.Red, at(5, 2));
        Assert.Equal(Colors.Lime, at(4, 3));
        // The right corner is under an inactive tab: square, the border line already runs above it.
        Assert.Equal(Colors.Lime, at(203, 0));
        // The line picks up where the curve's top run ends and runs to the bar's square right end.
        Assert.Equal((8.0, 204.0), dock.TopSeal);
    });

    [Fact]
    public void UnderAnInactiveTabTheBarsTopCornersStaySquare() => RunSta(() =>
    {
        var (dock, _) = UnderTheStrip((300, 450));
        var at = Pixels(dock);
        Assert.Equal(Colors.Lime, at(4, 0));
        Assert.Equal(Colors.Lime, at(4, 1));
        Assert.Equal(Colors.Black, at(6, 0));
        Assert.Equal((4.0, 204.0), dock.TopSeal);
    });

    [Fact]
    public void WithoutTheStripOnTopTheBarHasNoSealOrCurve() => RunSta(() =>
    {
        var (dock, _) = UnderTheStrip((0, 150));
        dock.SealsTop = false;
        dock.Measure(new Size(600, 400));
        dock.Arrange(new Rect(0, 0, 600, 400));
        Assert.Null(dock.TopSeal);
        Assert.Equal(Colors.Lime, Pixels(dock)(4, 0));
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
