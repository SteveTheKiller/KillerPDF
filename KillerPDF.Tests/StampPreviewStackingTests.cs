using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

// #478: on a page with a filled red Shapes rectangle, the Stamp preview drew the image watermark over the
// rectangle, but after Apply the rectangle covered the watermark. The preview must stack the watermark and the
// markup in the order Apply burns them.
public sealed class StampPreviewStackingTests
{
    private const double PageWidth = 400, PageHeight = 300;
    private const int RenderWidth = 2048, RenderHeight = 1536;

    [Fact]
    public void PreviewShowsTheShapesRectangleOverTheWatermarkAsApplyBurnsIt()
    {
        RunSta(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), $"killerpdf-wm478-stack-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                // A solid blue 200 x 150 pt picture at 72 DPI, centered at full opacity, and a smaller red
                // Shapes rectangle over the page center, so the left edge of the picture stays uncovered.
                string image = Path.Combine(folder, "logo.png");
                File.WriteAllBytes(image, SolidPng(200, 150, Colors.Blue));
                var spec = new StampSpec
                {
                    WmEnabled = true, WmIsImage = true, WmImagePath = image, WmScale = 1.0,
                    WmAngle = 0, WmOpacity = 1, WmPosH = 1, WmPosV = 1,
                };
                var rectangle = new HighlightAnnotation
                {
                    PageIndex = 0, IsShape = true,
                    Bounds = new Rect(RenderWidth * 0.35, RenderHeight * 0.35, RenderWidth * 0.3, RenderHeight * 0.3),
                };
                rectangle.SetColor(Colors.Red);
                // A see-through green shape on bare paper: drawn once in the preview, as Apply draws it once.
                var tint = new HighlightAnnotation
                {
                    PageIndex = 0, IsShape = true,
                    Bounds = new Rect(RenderWidth * 0.05, RenderHeight * 0.05, RenderWidth * 0.15, RenderHeight * 0.15),
                };
                tint.SetColor(Color.FromArgb(128, 0, 160, 0));
                var annotations = new Dictionary<int, List<PageAnnotation>> { [0] = [rectangle, tint] };
                var renderDims = new Dictionary<int, (int w, int h)> { [0] = (RenderWidth, RenderHeight) };

                // Apply: the saved page with the watermark and the markup burned in.
                string applied = Path.Combine(folder, "applied.pdf");
                File.WriteAllBytes(applied, new PdfDocumentBuilder().AddBlankPage(PageWidth, PageHeight).Build());
                PdfEngineBurn.Burn(applied, annotations, renderDims, spec);
                BitmapSource appliedPage = RenderPage(applied);
                Color appliedCenter = Pixel(appliedPage, 0.5, 0.5);
                Assert.True(IsRed(appliedCenter), $"Apply should leave the rectangle over the watermark, got {appliedCenter}");
                Assert.True(IsBlue(Pixel(appliedPage, 0.29, 0.5)), "Apply should show the uncovered watermark");

                // Preview: the page layer, the watermark the dialog lays over it, and the markup layer, stacked
                // as the dialog stacks them. Shapes are not highlighter marks, so the page layer is the clean page.
                string clean = Path.Combine(folder, "clean.pdf");
                File.WriteAllBytes(clean, new PdfDocumentBuilder().AddBlankPage(PageWidth, PageHeight).Build());
                Assert.False(StampPreviewMarkup.BurnPageLayer(clean, Path.Combine(folder, "page-layer.pdf"), 0,
                    annotations, renderDims, null));
                BitmapSource page = RenderPage(clean);

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(image);
                bitmap.EndInit();
                (double wpt, double hpt) = WatermarkImageSize.Points(bitmap.PixelWidth, bitmap.PixelHeight,
                    bitmap.DpiX, bitmap.DpiY, PageWidth, spec.WmScale);
                var stamps = new Canvas { Width = PageWidth, Height = PageHeight };
                var watermark = new Image { Source = bitmap, Width = wpt, Height = hpt, Stretch = Stretch.Fill };
                Canvas.SetLeft(watermark, (PageWidth - wpt) / 2);
                Canvas.SetTop(watermark, (PageHeight - hpt) / 2);
                stamps.Children.Add(watermark);

                BitmapSource? markup = StampPreviewMarkup.Render(annotations[0], renderDims[0], PageWidth, PageHeight, 1100);
                BitmapSource preview = Compose(page, stamps, markup);
                Color previewCenter = Pixel(preview, 0.5, 0.5);

                Assert.True(IsRed(previewCenter), $"the preview should match Apply (red over the watermark), got {previewCenter}");
                Color previewEdge = Pixel(preview, 0.29, 0.5);
                Assert.True(IsBlue(previewEdge), $"the markup layer should leave the uncovered watermark showing, got {previewEdge}");
                Color previewTint = Pixel(preview, 0.1, 0.1), appliedTint = Pixel(appliedPage, 0.1, 0.1);
                Assert.True(Math.Abs(previewTint.R - appliedTint.R) <= 12 && Math.Abs(previewTint.G - appliedTint.G) <= 12
                    && Math.Abs(previewTint.B - appliedTint.B) <= 12,
                    $"a see-through shape should look the same in the preview ({previewTint}) as after Apply ({appliedTint})");
                Color previewPaper = Pixel(preview, 0.9, 0.1);
                Assert.True(previewPaper.R > 240 && previewPaper.G > 240 && previewPaper.B > 240,
                    $"the markup layer should be clear away from the markup, got {previewPaper}");
            }
            finally { Directory.Delete(folder, recursive: true); }
        });
    }

    // A highlighter mark darkens what is under it (Multiply) and never hides the text, so over black page
    // content it stays black after Apply; the preview must not lift it toward the highlight color.
    [Fact]
    public void PreviewShowsAHighlighterOverPageTextAsApplyBurnsIt()
    {
        RunSta(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), $"killerpdf-wm478-mark-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            try
            {
                var ink = new PdfContentStreamBuilder().SaveState().SetFillRgb(0, 0, 0)
                    .Rectangle(40, 200, 120, 60).Fill().RestoreState();
                byte[] dark = new PdfDocumentBuilder().AddPage(PageWidth, PageHeight, ink).Build();
                // Page space 40..160 x 200..260 is canvas 0.1..0.4 x 0.133..0.333; the mark covers part of it.
                var mark = new HighlightAnnotation
                {
                    PageIndex = 0,
                    Bounds = new Rect(RenderWidth * 0.05, RenderHeight * 0.15, RenderWidth * 0.3, RenderHeight * 0.1),
                };
                var annotations = new Dictionary<int, List<PageAnnotation>> { [0] = [mark] };
                var renderDims = new Dictionary<int, (int w, int h)> { [0] = (RenderWidth, RenderHeight) };

                string applied = Path.Combine(folder, "applied.pdf");
                File.WriteAllBytes(applied, dark);
                PdfEngineBurn.Burn(applied, annotations, renderDims);
                BitmapSource appliedPage = RenderPage(applied);

                string clean = Path.Combine(folder, "clean.pdf");
                File.WriteAllBytes(clean, dark);
                BitmapSource? markup = StampPreviewMarkup.Render(annotations[0], renderDims[0], PageWidth, PageHeight, 1100);
                string layered = Path.Combine(folder, "page-layer.pdf");
                string pageLayer = StampPreviewMarkup.BurnPageLayer(clean, layered, 0, annotations, renderDims, null)
                    ? layered : clean;
                BitmapSource preview = Compose(RenderPage(pageLayer), new Canvas(), markup);

                foreach ((double fx, double fy) in new[] { (0.2, 0.2), (0.07, 0.2) })
                {
                    Color want = Pixel(appliedPage, fx, fy), got = Pixel(preview, fx, fy);
                    Assert.True(Math.Abs(want.R - got.R) <= 12 && Math.Abs(want.G - got.G) <= 12
                        && Math.Abs(want.B - got.B) <= 12,
                        $"at ({fx}, {fy}) the preview shows {got} but Apply gives {want}");
                }
            }
            finally { Directory.Delete(folder, recursive: true); }
        });
    }

    private static BitmapSource Compose(BitmapSource page, Canvas stamps, BitmapSource? markup)
    {
        stamps.Width = PageWidth;
        stamps.Height = PageHeight;
        var host = new Grid { Width = PageWidth, Height = PageHeight };
        StampPreviewMarkup.Stack(host,
            new Image { Source = page, Width = PageWidth, Height = PageHeight, Stretch = Stretch.Fill },
            stamps,
            new Image { Source = markup, Width = PageWidth, Height = PageHeight, Stretch = Stretch.Fill });
        host.Measure(new Size(PageWidth, PageHeight));
        host.Arrange(new Rect(0, 0, PageWidth, PageHeight));
        var preview = new RenderTargetBitmap((int)PageWidth, (int)PageHeight, 96, 96, PixelFormats.Pbgra32);
        preview.Render(host);
        return preview;
    }

    private static BitmapSource RenderPage(string path)
    {
        using var session = PdfPageRenderSession.OpenEngineFirst(path, 1100, 1100, null);
        var rendered = session.RenderPage(0);
        var bitmap = BitmapSource.Create(rendered.Width, rendered.Height, 96, 96, PixelFormats.Bgra32, null,
            rendered.Pixels, rendered.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color Pixel(BitmapSource source, double fx, double fy)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect((int)(converted.PixelWidth * fx), (int)(converted.PixelHeight * fy), 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static bool IsRed(Color color) => color.R > 200 && color.G < 60 && color.B < 60;
    private static bool IsBlue(Color color) => color.B > 200 && color.R < 60 && color.G < 60;

    private static byte[] SolidPng(int width, int height, Color color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 72, 72, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
