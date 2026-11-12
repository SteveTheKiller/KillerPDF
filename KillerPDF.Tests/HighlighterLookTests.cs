using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The on-screen highlighter mark (Controls/Viewer/HighlighterLook.cs) multiplies the page under it, like the saved
// copy does (#200): the paper takes the color and the text stays dark at any opacity. Laid out and rendered
// off-screen; no window is created.
public sealed class HighlighterLookTests
{
    private const int W = 20, H = 10;

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static BitmapSource Bitmap(int width, int height, Func<int, int, Color> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Color c = pixel(x, y);
                int i = (y * width + x) * 4;
                bytes[i] = c.B; bytes[i + 1] = c.G; bytes[i + 2] = c.R; bytes[i + 3] = 255;
            }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // A page grid shaped like the viewer's: the page image first, the annotation canvas over it.
    private static (Grid Grid, Canvas Canvas) Page(BitmapSource page)
    {
        var grid = new Grid { Width = W, Height = H };
        grid.Children.Add(new Image { Source = page, Stretch = Stretch.Fill, Width = W, Height = H });
        var canvas = new Canvas { Width = W, Height = H };
        grid.Children.Add(canvas);
        return (grid, canvas);
    }

    private static byte[] Render(Grid grid)
    {
        grid.Measure(new Size(W, H));
        grid.Arrange(new Rect(0, 0, W, H));
        grid.UpdateLayout();
        var target = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        target.Render(grid);
        var pixels = new byte[W * H * 4];
        target.CopyPixels(pixels, W * 4, 0);
        return pixels;
    }

    private static void AssertPixel(byte[] pixels, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * W + x) * 4;
        Assert.InRange(pixels[i + 2], Math.Max(0, r - 2), Math.Min(255, r + 2));
        Assert.InRange(pixels[i + 1], Math.Max(0, g - 2), Math.Min(255, g + 2));
        Assert.InRange(pixels[i], Math.Max(0, b - 2), Math.Min(255, b + 2));
    }

    private static void Mark(Canvas canvas, Rect bounds, Color color, bool inverted = false, Geometry? carved = null)
    {
        canvas.Measure(new Size(W, H));
        canvas.Children.Add(HighlighterLook.Build(canvas, bounds, carved, color, inverted ? new HighlighterLook.Dark(true, []) : HighlighterLook.Dark.Off));
    }

    private static readonly Color Yellow = Color.FromArgb(255, 255, 255, 0);

    [Fact]
    public void FullOpacityMarkKeepsTextBlackAndTurnsThePaperYellow() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (x, _) => x < W / 2 ? Colors.Black : Colors.White));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Yellow);

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 0, 0, 0);
        AssertPixel(pixels, 15, 5, 255, 255, 0);
    });

    [Fact]
    public void HalfOpacityMarkTintsThePaperHalfwayAndLeavesTextBlack() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (x, _) => x < W / 2 ? Colors.Black : Colors.White));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Color.FromArgb(128, 255, 255, 0));

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 0, 0, 0);
        AssertPixel(pixels, 15, 5, 255, 255, 127);
    });

    [Fact]
    public void MarkOnlyCoversItsOwnBounds() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (_, _) => Colors.White));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(10, 0, 10, H), Yellow);

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 255, 255, 255);
        AssertPixel(pixels, 15, 5, 255, 255, 0);
    });

    [Fact]
    public void MarkMultipliesTheSharperZoomDetailWhereItLies() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (_, _) => Colors.White));
        // The viewer's zoom detail: a sibling image over the right half, placed by margin in page-image units.
        var detail = new Image
        {
            Source = Bitmap(10, 10, (_, _) => Colors.Red), Stretch = Stretch.Fill, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Width = 10, Height = H, Margin = new Thickness(10, 0, 0, 0)
        };
        Panel.SetZIndex(detail, -1);
        Panel.SetZIndex(grid.Children[0], -2);
        grid.Children.Add(detail);
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Color.FromArgb(255, 255, 128, 255));

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 255, 128, 255);
        AssertPixel(pixels, 15, 5, 255, 0, 0);
    });

    [Fact]
    public void DarkModeShowsTheInvertedSavedLook() => OnSta(() =>
    {
        // Dark mode shows the page inverted: dark paper, light text. The saved highlight inverts with it.
        var (grid, canvas) = Page(Bitmap(W, H, (x, _) => x < W / 2 ? Colors.White : Colors.Black));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Yellow, inverted: true);

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 255, 255, 255);
        AssertPixel(pixels, 15, 5, 0, 0, 255);
    });

    [Fact]
    public void CarvedMarkLeavesTheErasedPartUntouched() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (_, _) => Colors.White));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Yellow, carved: new RectangleGeometry(new Rect(0, 0, 10, H)));

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 255, 255, 0);
        AssertPixel(pixels, 15, 5, 255, 255, 255);
    });

    [Fact]
    public void RefreshPicksUpZoomDetailThatArrivesAfterTheMark() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (_, _) => Colors.White));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Color.FromArgb(255, 255, 128, 255));
        var detail = new Image
        {
            Source = Bitmap(10, 10, (_, _) => Colors.Red), Stretch = Stretch.Fill, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Width = 10, Height = H, Margin = new Thickness(10, 0, 0, 0)
        };
        Panel.SetZIndex(detail, -1);
        Panel.SetZIndex(grid.Children[0], -2);
        grid.Children.Add(detail);
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));

        HighlighterLook.Refresh(canvas);
        byte[] pixels = Render(grid);

        Assert.Single(canvas.Children.OfType<FrameworkElement>());
        AssertPixel(pixels, 15, 5, 255, 0, 0);

        grid.Children.Remove(detail);
        HighlighterLook.Refresh(canvas);
        pixels = Render(grid);
        AssertPixel(pixels, 15, 5, 255, 128, 255);
    });

    [Fact]
    public void ViewerDrawsHighlighterMarksAndRefreshesThemWithTheZoomDetail()
    {
        string root = RepoRoot();
        string annotations = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Annotations.cs"));
        string refinement = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Refinement.cs"));
        Assert.Contains("PdfBurn.IsHighlighterMark(ha)", annotations);
        Assert.Contains("HighlighterLook.Build(", annotations);
        // Added, removed and cancelled details each refresh the marks over them: three call sites, not declarations.
        Assert.Equal(3, Count(refinement, "RefreshHighlighterMarks(grid);"));
    }

    [Fact]
    public void RefreshSwitchesAMarkIntoAndOutOfDarkMode() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (_, _) => Colors.Black));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Yellow);
        AssertPixel(Render(grid), 15, 5, 0, 0, 0);

        HighlighterLook.Refresh(canvas, new HighlighterLook.Dark(true, []));
        AssertPixel(Render(grid), 15, 5, 0, 0, 255);
    });

    [Fact]
    public void AnnotationsDrawnBeforeTheMarkAreMultipliedNotHidden() => OnSta(() =>
    {
        // An Edit Text cover (opaque white) over black page text, then a highlight over both: the mark must show the
        // cover, tinted, never the page text the cover hides. That is what the saved copy draws.
        var (grid, canvas) = Page(Bitmap(W, H, (_, _) => Colors.Black));
        var cover = new System.Windows.Shapes.Rectangle { Width = 10, Height = H, Fill = Brushes.White };
        Canvas.SetLeft(cover, 10); Canvas.SetTop(cover, 0);
        canvas.Children.Add(cover);
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Yellow);

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 0, 0, 0);
        AssertPixel(pixels, 15, 5, 255, 255, 0);
    });

    [Fact]
    public void AnnotationsDrawnAfterTheMarkStayOnTop() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (_, _) => Colors.White));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Yellow);
        var box = new System.Windows.Shapes.Rectangle { Width = 10, Height = H, Fill = Brushes.Red };
        Canvas.SetLeft(box, 10); Canvas.SetTop(box, 0);
        canvas.Children.Add(box);

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 255, 255, 0);
        AssertPixel(pixels, 15, 5, 255, 0, 0);
    });

    [Fact]
    public void OverlappingMarksCompoundLikeTheSavedCopy() => OnSta(() =>
    {
        var (grid, canvas) = Page(Bitmap(W, H, (_, _) => Colors.White));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Color.FromArgb(255, 255, 255, 0));
        Mark(canvas, new Rect(10, 0, 10, H), Color.FromArgb(255, 0, 255, 255));

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 255, 255, 0);
        AssertPixel(pixels, 15, 5, 0, 255, 0);
    });

    [Fact]
    public void DarkModeLeavesUninvertedImagesOnThePlainMultiply() => OnSta(() =>
    {
        // Dark mode inverts the page but keeps pictures as they are, so over a kept region the saved highlight shows
        // as a plain multiply, and over the inverted rest as the inverted look.
        var (grid, canvas) = Page(Bitmap(W, H, (x, _) => x < W / 2 ? Colors.Black : Colors.White));
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        canvas.Measure(new Size(W, H));
        canvas.Children.Add(HighlighterLook.Build(canvas, new Rect(0, 0, W, H), null, Yellow,
            new HighlighterLook.Dark(true, [new Rect(0.5, 0, 0.5, 1)])));

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 4, 5, 0, 0, 255);
        AssertPixel(pixels, 15, 5, 255, 255, 0);
    });

    [Fact]
    public void DarkModeBlankScaffoldStillShowsTheMark() => OnSta(() =>
    {
        var grid = new Grid { Width = W, Height = H, Background = Brushes.White };
        grid.Children.Add(new Image { Width = W, Height = H });
        var canvas = new Canvas { Width = W, Height = H };
        grid.Children.Add(canvas);
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Yellow, inverted: true);

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 15, 5, 0, 0, 255);
    });

    [Theory]
    [InlineData(0, 0.1, 0.2, 0.3, 0.4)]
    [InlineData(90, 0.4, 0.1, 0.4, 0.3)]
    [InlineData(180, 0.6, 0.4, 0.3, 0.4)]
    [InlineData(270, 0.2, 0.6, 0.4, 0.3)]
    public void KeptRegionsFollowThePageRotation(int rotation, double x, double y, double w, double h)
    {
        // An image box at L=0.1 T=0.2 R=0.4 B=0.6 of the upright page, turned the way the viewer turns the bitmap.
        Rect kept = HighlighterLook.KeptOnPage(0.1, 0.2, 0.4, 0.6, rotation);
        Assert.Equal(x, kept.X, 9); Assert.Equal(y, kept.Y, 9);
        Assert.Equal(w, kept.Width, 9); Assert.Equal(h, kept.Height, 9);
    }

    [Fact]
    public void FilledShapesRectanglesAreLabelledAsDrawings()
    {
        string root = RepoRoot();
        string menu = File.ReadAllText(Path.Combine(root, "Shell", "ContextMenu.cs"));
        string annotations = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Annotations.cs"));
        Assert.Contains("HighlightAnnotation { IsShape: true } => Loc(\"Str_Annot_Drawing\")", menu);
        Assert.Contains("HighlightAnnotation { IsShape: true } => Loc(\"Str_Annot_Drawing\")", annotations);
    }

    [Fact]
    public void TurningDarkModeOnOrOffRefreshesTheMarks()
    {
        string settings = File.ReadAllText(Path.Combine(RepoRoot(), "Shell", "SettingsPanel.cs"));
        int set = settings.IndexOf("ActiveViewer.DocInvert = on;", StringComparison.Ordinal);
        int refresh = settings.IndexOf("ActiveViewer.RefreshHighlighterMarks()", StringComparison.Ordinal);
        Assert.True(set >= 0 && refresh > set, "the dark-mode toggle must refresh highlighter marks after setting DocInvert");
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void ABlankScaffoldStillShowsTheMarkColor() => OnSta(() =>
    {
        var grid = new Grid { Width = W, Height = H, Background = Brushes.White };
        grid.Children.Add(new Image { Width = W, Height = H });
        var canvas = new Canvas { Width = W, Height = H };
        grid.Children.Add(canvas);
        grid.Measure(new Size(W, H)); grid.Arrange(new Rect(0, 0, W, H));
        Mark(canvas, new Rect(0, 0, W, H), Yellow);

        byte[] pixels = Render(grid);

        AssertPixel(pixels, 15, 5, 255, 255, 0);
    });
}
