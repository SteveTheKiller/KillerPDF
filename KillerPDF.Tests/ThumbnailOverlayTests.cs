using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A page thumbnail shows what the page looks like, unsaved edits included: pictures, highlights, covers, drawings
// and text boxes are drawn over the page's own render, scaled from the editing canvas to the thumbnail.
public sealed class ThumbnailOverlayTests
{
    private static BitmapSource Solid(int width, int height, Color color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = color.A; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color At(BitmapSource image, int x, int y)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    [Fact]
    public void PagesWithoutEditsKeepTheirRender() => RunSta(() =>
    {
        var page = Solid(50, 50, Colors.White);
        Assert.Same(page, ThumbnailOverlay.Compose(page, [], new Size(100, 100), _ => null));
    });

    [Fact]
    public void APastedPictureShowsOnTheThumbnailScaledFromTheCanvas() => RunSta(() =>
    {
        var page = Solid(50, 50, Colors.White);
        var picture = new ImageAnnotation { Position = new Point(20, 20), SourceWidth = 40, SourceHeight = 40, Scale = 1 };

        var shown = ThumbnailOverlay.Compose(page, [picture], new Size(100, 100), _ => Solid(2, 2, Colors.Red));

        Assert.Equal((50, 50), (shown.PixelWidth, shown.PixelHeight));
        Assert.Equal(Colors.Red, At(shown, 20, 20));    // canvas (40,40), inside the picture
        Assert.Equal(Colors.White, At(shown, 5, 5));    // canvas (10,10), outside it
    });

    [Fact]
    public void HighlightsCoversAndDrawingsShow() => RunSta(() =>
    {
        var page = Solid(100, 100, Colors.White);
        var cover = new CoverAnnotation { Bounds = new Rect(0, 0, 30, 30) };
        cover.SetColor(Colors.Blue);
        var highlight = new HighlightAnnotation { Bounds = new Rect(60, 0, 30, 30) };
        highlight.SetColor(Color.FromArgb(255, 255, 255, 0));
        var ink = new InkAnnotation { Points = [new Point(0, 80), new Point(100, 80)], StrokeWidth = 6 };
        ink.SetColor(Colors.Black);

        var shown = ThumbnailOverlay.Compose(page, [cover, highlight, ink], new Size(100, 100), _ => null);

        Assert.Equal(Colors.Blue, At(shown, 15, 15));
        Assert.Equal(Color.FromArgb(255, 255, 255, 0), At(shown, 75, 15));
        Assert.Equal(Colors.Black, At(shown, 50, 80));
        Assert.Equal(Colors.White, At(shown, 50, 50));
    });

    [Fact]
    public void HiddenEditsStayOffTheThumbnail() => RunSta(() =>
    {
        var page = Solid(100, 100, Colors.White);
        var cover = new CoverAnnotation { Bounds = new Rect(0, 0, 50, 50), IsVisible = false };
        cover.SetColor(Colors.Blue);

        var shown = ThumbnailOverlay.Compose(page, [cover], new Size(100, 100), _ => null);

        Assert.Equal(Colors.White, At(shown, 25, 25));
    });

    [Fact]
    public void ATextBoxFillShows() => RunSta(() =>
    {
        var page = Solid(100, 100, Colors.White);
        var text = new TextAnnotation { Position = new Point(10, 10), Width = 60, Height = 30, Content = "" };
        text.SetFill(Colors.Green);

        var shown = ThumbnailOverlay.Compose(page, [text], new Size(100, 100), _ => null);

        Assert.Equal(Colors.Green, At(shown, 40, 25));
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
