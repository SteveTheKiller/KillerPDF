using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The book and DjVu notice sits centered at the top of the document pane and takes the app scale; the find bar floats
// where the user put it (by default top right, 16 from the right edge and 6 from the top) and does not scale. When the
// two would overlap, the notice moves below the find bar so its message and Details button stay reachable; otherwise
// it stays at the top. The pane here is 1000 x 700, the notice 300 x 34 before scale and the find bar 494 x 48.
// Nothing here is shown.
public sealed class NativeNoticePlacementTests
{
    private const double PaneWidth = 1000, PaneHeight = 700;

    private static (Grid Pane, Border Notice, Border Find) Pane(double scale, Thickness findMargin)
    {
        var pane = new Grid { Width = PaneWidth, Height = PaneHeight };
        var notice = new Border
        {
            Width = 300, Height = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            LayoutTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale)
        };
        var find = new Border
        {
            Width = 494, Height = 48,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = findMargin
        };
        pane.Children.Add(notice);
        pane.Children.Add(find);
        Layout(pane);
        return (pane, notice, find);
    }

    private static void Layout(Grid pane)
    {
        pane.Measure(new Size(PaneWidth, PaneHeight));
        pane.Arrange(new Rect(0, 0, PaneWidth, PaneHeight));
        pane.UpdateLayout();
    }

    private static Rect Drawn(FrameworkElement element, Visual pane) =>
        element.TransformToAncestor(pane).TransformBounds(new Rect(element.RenderSize));

    // Default find bar: 1000 - 494 - 16 = 490, so x 490 to 984 and y 6 to 54. The notice is centered:
    // at 1.0 it spans x 350 to 650, at 1.5 it is 450 x 51 from x 275, at 2.5 it is 750 x 85 from x 125.
    // Each overlaps the find bar, so the notice moves to 54 + 6 = 60, and drawn there it clears the bar.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.5)]
    public void TheNoticeMovesBelowTheFindBarAtItsDefaultSpotAtEveryScale(double scale)
    {
        RunSta(() =>
        {
            var (pane, notice, find) = Pane(scale, new Thickness(490, 6, 0, 0));

            double top = NoticePlacement.TopClearOf(notice, find);

            Assert.Equal(60, top, 3);
            notice.Margin = new Thickness(0, top, 0, 0);
            Layout(pane);
            Assert.False(Drawn(notice, pane).IntersectsWith(Drawn(find, pane)));
            // Placed, the answer does not change, so the notice settles instead of moving back and forth.
            Assert.Equal(60, NoticePlacement.TopClearOf(notice, find), 3);
        });
    }

    // A find bar dragged down to y 300 to 348 is below the notice (y 0 to 34 at 1.0, 0 to 85 at 2.5), so the notice
    // keeps its place at the top.
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.5)]
    public void AFindBarDraggedAwayFromTheTopLeavesTheNoticeAtTheTop(double scale)
    {
        RunSta(() =>
        {
            var (_, notice, find) = Pane(scale, new Thickness(16, 300, 0, 0));

            Assert.Equal(0, NoticePlacement.TopClearOf(notice, find), 3);
        });
    }

    // A find bar at the top left, x 16 to 196 (180 wide): the notice at 1.0 spans 350 to 650 and is clear of it, so
    // it stays at the top; at 2.5 it spans 125 to 875, overlaps, and moves to 6 + 48 + 6 = 60. Only the drawn size,
    // not the unscaled 300, tells the two apart.
    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(2.5, 60)]
    public void WhetherTheNoticeMovesDependsOnItsScaledSize(double scale, double expected)
    {
        RunSta(() =>
        {
            var (pane, notice, find) = Pane(scale, new Thickness(16, 6, 0, 0));
            find.Width = 180;
            Layout(pane);

            Assert.Equal(expected, NoticePlacement.TopClearOf(notice, find), 3);
        });
    }

    [Fact]
    public void AClosedFindBarOrOneInTheOtherPaneLeavesTheNoticeAtTheTop()
    {
        RunSta(() =>
        {
            var (_, notice, find) = Pane(1.0, new Thickness(490, 6, 0, 0));
            find.Visibility = Visibility.Collapsed;
            Assert.Equal(0, NoticePlacement.TopClearOf(notice, find), 3);
            Assert.Equal(0, NoticePlacement.TopClearOf(notice, null), 3);

            var (_, _, elsewhere) = Pane(1.0, new Thickness(490, 6, 0, 0));
            var (_, here, _) = Pane(1.0, new Thickness(490, 6, 0, 0));
            Assert.Equal(0, NoticePlacement.TopClearOf(here, elsewhere), 3);
        });
    }

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
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
