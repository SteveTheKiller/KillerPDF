using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The owner-restriction notice ("Enter password") is centered at the top of the document pane like the book notice. It
// takes the app scale, drawn smaller where the pane cannot hold it, and sits below the find bar while the two would
// overlap, so the find bar does not cover its message and button. Nothing here is shown.
public sealed class RestrictionNoticePlacementTests
{
    private static (Grid Pane, Border Notice, TextBlock Message, Border Find) Pane(double paneWidth)
    {
        var glyph = new TextBlock { Width = 14, Margin = new Thickness(0, 0, 8, 0) };
        var message = new TextBlock { Text = "This PDF is read-only because its owner restricts editing.", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var unlock = new Button { Width = 77, Height = 26, Margin = new Thickness(12, 0, 0, 0) };
        var close = new Button { Width = 30, Height = 26, Margin = new Thickness(6, 0, 0, 0) };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 2, 2, 2) };
        DockPanel.SetDock(glyph, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(unlock, Dock.Right);
        row.Children.Add(glyph);
        row.Children.Add(close);
        row.Children.Add(unlock);
        row.Children.Add(message);
        var notice = new Border
        {
            Child = row, Padding = new Thickness(4), BorderThickness = new Thickness(1), MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
        };
        var find = new Border
        {
            Width = 340, Height = 48, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 6, 0, 0)
        };
        var pane = new Grid { Width = paneWidth, Height = 700 };
        pane.Children.Add(notice);
        pane.Children.Add(find);
        Layout(pane);
        return (pane, notice, message, find);
    }

    private static void Layout(Grid pane)
    {
        pane.Measure(new Size(pane.Width, pane.Height));
        pane.Arrange(new Rect(0, 0, pane.Width, pane.Height));
        pane.UpdateLayout();
    }

    private static Rect Drawn(FrameworkElement element, Visual pane) =>
        element.TransformToAncestor(pane).TransformBounds(new Rect(element.RenderSize));

    // The notice needs 285 at scale 1 (see NativeNoticeFitTests). At app size 2.5 in a 360 pane it is drawn at
    // 360 / 285 = 1.26316 instead of 2.5, which would be 712 wide and cut off at both edges.
    [Fact]
    public void AtALargeAppSizeTheNoticeIsDrawnSmallerSoItFitsThePane()
    {
        RunSta(() =>
        {
            var (pane, notice, message, _) = Pane(360);

            NoticePlacement.Fit(notice, message, 2.5);
            Layout(pane);

            Assert.Equal(360.0 / 285, Assert.IsType<ScaleTransform>(notice.LayoutTransform).ScaleX, 4);
            Rect drawn = Drawn(notice, pane);
            Assert.True(drawn.Left >= -0.5 && drawn.Right <= 360.5, "the notice spans " + drawn);
        });
    }

    // The find bar open at the top left (x 8 to 348, y 6 to 54) overlaps the centered notice, which moves to 54 + 6 = 60
    // and then clears the bar. Without the bar it stays at the top.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.5)]
    public void TheNoticeSitsBelowAnOpenFindBarAndAtTheTopWithoutOne(double appScale)
    {
        RunSta(() =>
        {
            var (pane, notice, message, find) = Pane(600);
            NoticePlacement.Fit(notice, message, appScale);
            Layout(pane);

            NoticePlacement.Place(notice, find);
            Layout(pane);

            Assert.Equal(60, notice.Margin.Top, 3);
            Assert.False(Drawn(notice, pane).IntersectsWith(Drawn(find, pane)));
            // Placed, asking again does not move it.
            NoticePlacement.Place(notice, find);
            Assert.Equal(60, notice.Margin.Top, 3);

            find.Visibility = Visibility.Collapsed;
            NoticePlacement.Place(notice, find);
            Assert.Equal(0, notice.Margin.Top, 3);
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