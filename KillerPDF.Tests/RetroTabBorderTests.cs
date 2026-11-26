using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The document tab's face (Controls/RetroTabBorder.cs) and the card's corners at the tab band
// (Controls/CardCorners.cs), laid out and rendered off-screen. No window is created.
public sealed class RetroTabBorderTests
{
    [Fact]
    public void ActiveTabClipsItsContentToTheFaceOnePixelInsideTheOutline() => OnSta(() =>
    {
        var content = new Grid();
        Tab(new CornerRadius(3, 3, 0, 0), new Thickness(0, 3, 0, 0), content);

        Geometry clip = Assert.IsAssignableFrom<Geometry>(content.Clip);
        // The face in the content's own coordinates: back out through the padding, below the stripe.
        Rect face = clip.Bounds;
        Assert.Equal(-12, face.X, 6);
        Assert.Equal(-3, face.Y, 6);
        Assert.Equal(200, face.Width, 6);
        Assert.Equal(27, face.Height, 6);
        // Its top corners round at 2, one less than the outline's 3.
        Assert.False(clip.FillContains(new Point(-11.8, -2.8)));
        Assert.True(clip.FillContains(new Point(-11.8, -0.8)));
        Assert.True(clip.FillContains(new Point(-9.8, -2.8)));
        Assert.False(clip.FillContains(new Point(187.8, -2.8)));
    });

    [Fact]
    public void InactiveClassicAndBottomRetroTabsLeaveTheirContentUnclipped() => OnSta(() =>
    {
        var inactive = new Grid();
        Tab(new CornerRadius(0), new Thickness(0, 0, 1, 0), inactive);
        Assert.Null(inactive.Clip);

        // 98SE: a 2px corner with no stripe, drawn by Border exactly as before.
        var classic = new Grid();
        Tab(new CornerRadius(2, 2, 0, 0), new Thickness(0), classic);
        Assert.Null(classic.Clip);

        var bottom = new Grid();
        Tab(new CornerRadius(3, 3, 0, 0), new Thickness(0, 3, 0, 0), bottom, retroBottomEdges: true);
        Assert.Null(bottom.Clip);
    });

    [Fact]
    public void FadingSidesRunThePaneEdgeUpBeneathThem() => OnSta(() =>
    {
        Color edge = Color.FromRgb(0, 200, 0), accent = Color.FromRgb(0, 0, 255);
        var tab = Tab(new CornerRadius(3, 3, 0, 0), new Thickness(1, 3, 1, 0), new Grid());
        tab.Background = Brushes.White;
        tab.DividerBrush = new SolidColorBrush(edge);
        tab.BorderBrush = new LinearGradientBrush(accent, Color.FromArgb(0, 0, 0, 255), 90);

        // Just above the foot's flare the accent has all but faded out, and the pane's edge carries
        // on up both sides.
        foreach (int x in new[] { 0, 199 })
        {
            Color foot = Pixel(tab, x, 25);
            Assert.Equal(255, foot.A);
            Assert.True(foot.G > 150 && foot.B < 64, $"Side {x} above the foot is {foot}.");
        }
        // The foot curves out into the card (#528), so the face fills the side column on the last row.
        Assert.Equal(Colors.White, Pixel(tab, 0, 29));
        Assert.Equal(Colors.White, Pixel(tab, 1, 29));

        // A solid ring (the focused pane's) is drawn as it is.
        tab.BorderBrush = new SolidColorBrush(accent);
        Assert.Equal(accent, Pixel(tab, 0, 20));
    });

    [Fact]
    public void ATabOnTheCardsEdgeRunsItsSideStraightDown() => OnSta(() =>
    {
        // #528: the first tab stands on the card's left edge. Its foot used to curve out past the
        // strip, where the viewport clipped the curve, and the face fill cut 4px out of the side.
        Color edge = Color.FromRgb(0, 200, 0);
        var tab = Tab(new CornerRadius(8, 8, 0, 0), new Thickness(1, 1, 1, 0), new Grid());
        tab.Background = Brushes.White;
        tab.BorderBrush = new SolidColorBrush(edge);
        tab.FlareLeft = false;
        foreach (int y in new[] { 25, 26, 27, 28, 29 })
            Assert.Equal(edge, Pixel(tab, 0, y));
        // The right foot still curves out into the card, so the face fills its side column there.
        Assert.Equal(Colors.White, Pixel(tab, 199, 29));
    });

    [Fact]
    public void APaintedFaceClippedToTheTabStillCoversTheCardLineInsideTheCurve() => OnSta(() =>
    {
        // Trash Polka paints the active tab with a brush clipped to the tab's own rectangle, so the
        // face inside the foot's curve, which lies outside the tab, painted nothing and the card's
        // top line ran on under the curve to the tab's side. Out there the face takes the pane color.
        Color pane = Color.FromRgb(200, 0, 0), edge = Color.FromRgb(0, 0, 255), line = Color.FromRgb(0, 200, 0);
        var clipped = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 200, 30)) };
        clipped.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(-50, 0, 300, 30))));
        var painted = new DrawingBrush(clipped)
        {
            Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 200, 30),
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 200, 30),
        };
        var tab = new RetroTabBorder
        {
            Width = 200, Height = 30, HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(6, 6, 0, 0), BorderThickness = new Thickness(1, 1, 1, 0),
            BorderBrush = new SolidColorBrush(edge), Background = painted, FlareLeft = false,
        };
        tab.Resources["ContentPaneBrush"] = new SolidColorBrush(pane);
        var host = new Grid { Width = 220, Height = 30, Background = Brushes.Black };
        host.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(line) });
        host.Children.Add(tab);
        host.Measure(new Size(220, 30));
        host.Arrange(new Rect(0, 0, 220, 30));

        // The last row: the tab's side column, then four face pixels outside it, then the curve's
        // two edge pixels, then the card line carrying on under the next tab.
        Assert.Equal(Colors.White, Pixel(host, 199, 29));
        foreach (int x in new[] { 200, 201, 202, 203 })
            Assert.Equal(pane, Pixel(host, x, 29));
        Assert.Equal(edge, Pixel(host, 204, 29));
        Assert.Equal(edge, Pixel(host, 205, 29));
        Assert.Equal(line, Pixel(host, 206, 29));
        // The row above: two face pixels, then the curve.
        Assert.Equal(pane, Pixel(host, 201, 28));
        Assert.Equal(edge, Pixel(host, 202, 28));
    });

    [Theory]
    [InlineData(false, false, false, 3, 3, 6, 6)]
    [InlineData(true, false, false, 0, 3, 6, 6)]
    [InlineData(false, true, false, 3, 0, 6, 6)]
    [InlineData(true, true, false, 0, 0, 6, 6)]
    [InlineData(true, false, true, 6, 6, 3, 0)]
    [InlineData(false, true, true, 6, 6, 0, 3)]
    public void CardCornersAtTheTabBandTakeTheTabRadiusUnlessTheActiveTabSitsOverThem(
        bool firstActive, bool lastActive, bool bottom, double topLeft, double topRight, double bottomRight, double bottomLeft)
    {
        Assert.Equal(new CornerRadius(topLeft, topRight, bottomRight, bottomLeft),
            CardCorners.AtTabBand(6, 3, firstActive, lastActive, bottom));
    }

    [Fact]
    public void CardCornersNeverRoundMoreThanTheCard()
    {
        Assert.Equal(new CornerRadius(2, 2, 2, 2), CardCorners.AtTabBand(2, 3, false, false, false));
        Assert.Equal(new CornerRadius(0), CardCorners.AtTabBand(0, 2, false, false, true));
    }

    [Fact]
    public void CardContentClipRoundsEachCornerOnePixelInsideTheCard() => OnSta(() =>
    {
        Geometry clip = CardCorners.ContentClip(new Size(100, 50), new CornerRadius(0, 3, 6, 6));

        Assert.True(clip.FillContains(new Point(0.2, 0.2)));        // square under the active first tab
        Assert.False(clip.FillContains(new Point(99.8, 0.2)));      // 2, inside the band's 3
        Assert.True(clip.FillContains(new Point(99.8, 2.2)));
        Assert.False(clip.FillContains(new Point(0.6, 49.4)));      // 5, inside the card's 6
        Assert.True(clip.FillContains(new Point(0.6, 44.8)));
    });

    private static RetroTabBorder Tab(CornerRadius corners, Thickness border, UIElement content, bool retroBottomEdges = false)
    {
        var tab = new RetroTabBorder
        {
            CornerRadius = corners,
            BorderThickness = border,
            Padding = new Thickness(12, 3, 5, 3),
            RetroBottomEdges = retroBottomEdges,
            Child = content,
        };
        tab.Measure(new Size(200, 30));
        tab.Arrange(new Rect(0, 0, 200, 30));
        return tab;
    }

    private static Color Pixel(FrameworkElement element, int x, int y)
    {
        element.Measure(new Size(element.ActualWidth, element.ActualHeight));
        element.Arrange(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The STA test timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
