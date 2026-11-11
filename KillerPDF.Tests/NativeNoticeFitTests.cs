using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The book and DjVu notice takes the app scale. In a pane too narrow for it at that scale (the narrowest window at a
// large app size leaves the document pane 244 px wide), it is drawn smaller, just enough to fit the pane and leave
// its message 120 wide, so the message wraps but stays whole and Details keeps its
// size. Wherever it already fits, it keeps the app scale. Nothing here is shown.
public sealed class NativeNoticeFitTests
{
    // Built like the real notice: a padded, bordered bar holding a row with the glyph at the left, Close and Details
    // docked right and the wrapping message filling the rest.
    private static (Border Notice, TextBlock Message, Button Details) Notice()
    {
        var glyph = new TextBlock { Width = 14, Margin = new Thickness(0, 0, 8, 0) };
        var message = new TextBlock
        {
            Text = "Parts of this document could not be shown or read.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var details = new Button { Width = 77, Height = 26, Margin = new Thickness(12, 0, 0, 0) };
        var close = new Button { Width = 30, Height = 26, Margin = new Thickness(6, 0, 0, 0) };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 2, 2, 2) };
        DockPanel.SetDock(glyph, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(details, Dock.Right);
        row.Children.Add(glyph);
        row.Children.Add(close);
        row.Children.Add(details);
        row.Children.Add(message);
        var notice = new Border
        {
            Child = row,
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            MaxWidth = 640
        };
        return (notice, message, details);
    }

    // Worked: the notice needs 285 at scale 1, so at 2.5 it needs 712.5. A 244 pane cannot hold that and the notice is
    // drawn at 244 / 285 = 0.85614; a 600 pane gives 600 / 285 = 2.10526; a 900 pane holds 712.5, so 2.5 stays.
    // At 1.0 the narrowest pane, 293, holds 285.
    [Theory]
    [InlineData(2.5, 244, 0.85614)]
    [InlineData(2.5, 900, 2.5)]
    [InlineData(2.5, 600, 2.10526)]
    [InlineData(1.0, 900, 1.0)]
    [InlineData(1.0, 293, 1.0)]
    public void TheNoticeIsDrawnSmallerOnlyWhenThePaneCannotHoldItAtTheAppScale(double appScale, double pane, double expected)
    {
        Assert.Equal(expected, NoticePlacement.FitScale(appScale, pane, 285), 4);
    }

    // Border 1 + padding 4 on each side (10), the row's margins 6 + 2 (8), the glyph 14 + 8 (22), Details 77 + 12
    // (89), Close 30 + 6 (36) and 120 for the message: 285.
    [Fact]
    public void TheNoticeNeedsItsControlsPlusRoomForAWrappedMessage()
    {
        RunSta(() =>
        {
            var (notice, message, _) = Notice();

            Assert.Equal(285, NoticePlacement.MinimumWidth(notice, message), 3);
        });
    }

    // The narrowest window at app size 2.5: a 244 pane. Drawn at the fitted scale, the notice sits inside the pane,
    // Details gets its full 77 and the message wraps within its 120 and gets all the room it asks for.
    [Fact]
    public void AtTheNarrowestPaneTheNoticeFitsWithItsMessageWholeAndDetailsFullSize()
    {
        RunSta(() =>
        {
            var (notice, message, details) = Notice();
            var pane = new Grid { Width = 244, Height = 700 };
            pane.Children.Add(notice);
            double scale = NoticePlacement.FitScale(2.5, 244, NoticePlacement.MinimumWidth(notice, message));
            notice.LayoutTransform = new ScaleTransform(scale, scale);
            pane.Measure(new Size(244, 700));
            pane.Arrange(new Rect(0, 0, 244, 700));
            pane.UpdateLayout();

            Rect drawn = notice.TransformToAncestor(pane).TransformBounds(new Rect(notice.RenderSize));
            Assert.True(drawn.Left >= -0.5 && drawn.Right <= 244.5, "the notice spans " + drawn);
            Assert.Equal(77, details.RenderSize.Width, 3);
            // Wrapped within its 120, the message is as wide as its widest line.
            Assert.InRange(message.RenderSize.Width, 100, 120.5);
            Assert.True(message.DesiredSize.Width <= message.RenderSize.Width + 0.5,
                "the message asks for " + message.DesiredSize.Width + " wide and gets " + message.RenderSize.Width);
            Assert.True(message.DesiredSize.Height <= message.RenderSize.Height + 0.5,
                "the message asks for " + message.DesiredSize.Height + " and gets " + message.RenderSize.Height);
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
