using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Features;
using Xunit;

namespace KillerPDF.Tests;

// The find bar's search box takes the room its other controls leave, measured, instead of a fixed allowance. The bar
// here is built with the real bar's sizes: the grip 20 wide, the replace chevron 26 + 4, the magnifier 12 + 6, the
// options and the three buttons 26 each, the status text 96 + 4, the pane margin 8 each side and the bar's padding 4
// and border 1 each side. Everything but the box is 324 wide, the status text in it 100. Nothing here is shown.
public sealed class SearchBarFitTests
{
    private sealed record Bar(Border Frame, Grid Panel, TextBox Find, TextBox Replace, TextBlock Status);

    private static Bar Build()
    {
        var panel = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        for (int column = 0; column < 5; column++) panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 6, 0) };
        for (int column = 0; column < 2; column++)
        {
            var stack = new StackPanel();
            for (int row = 0; row < 3; row++) stack.Children.Add(new Ellipse { Width = 3, Height = 3, Margin = new Thickness(1.5) });
            dots.Children.Add(stack);
        }
        var find = new TextBox { Width = 200, Height = 26 };
        var replace = new TextBox { Width = 200, Height = 26, Margin = new Thickness(0, 4, 0, 0) };
        var status = new TextBlock { Width = SearchBarFit.StatusWidth, Margin = new Thickness(2, 0, 2, 0) };
        var tail = new StackPanel { Orientation = Orientation.Horizontal };
        tail.Children.Add(new Button { Width = 26, Height = 24 });
        tail.Children.Add(new Button { Width = 26, Height = 24 });
        tail.Children.Add(status);
        for (int button = 0; button < 3; button++) tail.Children.Add(new Button { Width = 26, Height = 24 });
        void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            panel.Children.Add(element);
        }
        Place(dots, 0, 0);
        Place(new Button { Width = 26, Height = 24, Margin = new Thickness(0, 0, 4, 0) }, 0, 1);
        Place(new TextBlock { Width = 12, Margin = new Thickness(0, 0, 6, 0) }, 0, 2);
        Place(find, 0, 3);
        Place(tail, 0, 4);
        Place(replace, 1, 3);
        var frame = new Border { Padding = new Thickness(4), BorderThickness = new Thickness(1), Child = panel,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        return new Bar(frame, panel, find, replace, status);
    }

    // The bar in a pane that is `paneWidth` in the app's layout units, under an app scale as the real window applies
    // it, laid out for real.
    private static (Grid Pane, Bar Bar) InPane(double paneWidth, double appScale)
    {
        var bar = Build();
        var pane = new Grid { Width = paneWidth, Height = 300 };
        pane.Children.Add(bar.Frame);
        var host = new Border { LayoutTransform = new ScaleTransform(appScale, appScale), Child = pane };
        host.Measure(new Size(10000, 10000));
        host.Arrange(new Rect(0, 0, host.DesiredSize.Width, host.DesiredSize.Height));
        host.UpdateLayout();
        return (pane, bar);
    }

    private static void Relayout(Grid pane)
    {
        var host = (FrameworkElement)pane.Parent;
        host.InvalidateMeasure();
        host.Measure(new Size(10000, 10000));
        host.Arrange(new Rect(0, 0, host.DesiredSize.Width, host.DesiredSize.Height));
        host.UpdateLayout();
    }

    // Everything but the box is 324 wide, so a pane of 524 or more gives the full 200 box. The 314 the bar used to
    // reserve left out its 10 of padding and border, so a pane of 400 got an 86 box and a 410 wide bar.
    [Theory]
    [InlineData(1.0, 600, 200)]
    [InlineData(1.0, 524, 200)]
    [InlineData(1.5, 524, 200)]
    [InlineData(2.5, 524, 200)]
    [InlineData(1.0, 400, 76)]
    [InlineData(1.5, 400, 76)]
    [InlineData(2.5, 400, 76)]
    public void TheBoxTakesWhatTheBarsOtherControlsLeave(double appScale, double paneWidth, double expectedBox)
    {
        RunSta(() =>
        {
            var (pane, bar) = InPane(paneWidth, appScale);

            double box = SearchBarFit.Fit(bar.Frame, [bar.Find, bar.Replace], bar.Status, pane.ActualWidth);
            Relayout(pane);

            Assert.Equal(expectedBox, box, 3);
            Assert.Equal(expectedBox, bar.Find.Width, 3);
            Assert.Equal(expectedBox, bar.Replace.Width, 3);
            Assert.True(bar.Frame.ActualWidth <= paneWidth + 0.01, $"the bar is {bar.Frame.ActualWidth} wide in a {paneWidth} pane");
        });
    }

    // The document pane is at least 360 wide, so the bar is made to fit there: 324 + 60 is 384, which the status text
    // gives up 24 of (96 to 72) so the box keeps its 60; at 340 it gives up 44 (96 to 52). It goes down to 48 at most.
    [Theory]
    [InlineData(1.0, 360, 72)]
    [InlineData(1.5, 360, 72)]
    [InlineData(2.5, 360, 72)]
    [InlineData(1.0, 340, 52)]
    public void AtTheNarrowestDocumentPaneTheBarStillFitsByNarrowingTheStatusText(double appScale, double paneWidth, double expectedStatus)
    {
        RunSta(() =>
        {
            var (pane, bar) = InPane(paneWidth, appScale);

            double box = SearchBarFit.Fit(bar.Frame, [bar.Find, bar.Replace], bar.Status, pane.ActualWidth);
            Relayout(pane);

            Assert.Equal(60, box, 3);
            Assert.Equal(expectedStatus, bar.Status.Width, 3);
            Assert.True(bar.Frame.ActualWidth <= paneWidth + 0.01, $"the bar is {bar.Frame.ActualWidth} wide in a {paneWidth} pane");
        });
    }

    // The bar floats by a margin from the pane's left and top, which is its place, not part of its width.
    [Fact]
    public void TheBarsPlaceInThePaneIsNotCountedAsItsWidth()
    {
        RunSta(() =>
        {
            var (pane, bar) = InPane(400, 1.0);
            bar.Frame.Margin = new Thickness(90, 6, 0, 0);

            double box = SearchBarFit.Fit(bar.Frame, [bar.Find, bar.Replace], bar.Status, 400);

            Assert.Equal(76, box, 3);
        });
    }

    // Narrower than that the bar cannot fit; the box and status text stop at their minimums.
    [Fact]
    public void ABarTooWideForItsPaneKeepsTheMinimums()
    {
        RunSta(() =>
        {
            var (pane, bar) = InPane(300, 1.0);

            double box = SearchBarFit.Fit(bar.Frame, [bar.Find, bar.Replace], bar.Status, pane.ActualWidth);

            Assert.Equal(60, box, 3);
            Assert.Equal(48, bar.Status.Width, 3);
        });
    }

    // Fitting again with more room gives the status text back, so a window that grows restores the bar.
    [Fact]
    public void AWiderPaneGivesTheStatusTextItsWidthBack()
    {
        RunSta(() =>
        {
            var (pane, bar) = InPane(340, 1.0);
            SearchBarFit.Fit(bar.Frame, [bar.Find, bar.Replace], bar.Status, 340);
            Assert.Equal(52, bar.Status.Width, 3);

            SearchBarFit.Fit(bar.Frame, [bar.Find, bar.Replace], bar.Status, 600);

            Assert.Equal(96, bar.Status.Width, 3);
            Assert.Equal(200, bar.Find.Width, 3);
        });
    }

    // The window sizes the box through the measured fit, not through a fixed allowance for the other controls.
    [Fact]
    public void TheWindowFitsTheBoxThroughTheMeasuredFitAndKeepsNoFixedAllowance()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(directory!.FullName, "Shell", "Search.cs"));
        Assert.DoesNotContain("reserved = 314", source, StringComparison.Ordinal);
        Assert.Contains("SearchBarFit.Fit(", source, StringComparison.Ordinal);
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