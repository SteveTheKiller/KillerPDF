using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The vector Transform puts the page where the raster Transform drew it. A dark mark on an unrotated page image goes
// through the raster steps (the in-app quarter turn, then PageTransformRaster.ComposeTransform) and through the vector
// matrix, and both have to land it in the same spot on a page of the same size. Rendered off-screen, no window.
public sealed class PageTransformGeometryTests
{
    private const int W = 60, H = 40;
    private const double CropLeft = 10, CropBottom = 20;

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory]
    [InlineData(0, 0, 1.0, false, false, false)]
    [InlineData(90, 0, 1.0, false, false, false)]
    [InlineData(180, 0, 1.0, false, false, false)]
    [InlineData(270, 0, 1.0, false, false, false)]
    [InlineData(0, 30, 1.0, false, false, false)]
    [InlineData(0, -45, 1.0, false, false, false)]
    [InlineData(90, 20, 1.0, false, false, false)]
    [InlineData(0, 0, 1.0, false, true, false)]
    [InlineData(0, 0, 1.0, false, false, true)]
    [InlineData(270, 15, 1.0, false, true, true)]
    [InlineData(0, 0, 0.5, false, false, false)]
    [InlineData(0, 0, 0.5, true, false, false)]
    [InlineData(90, 30, 1.5, false, true, false)]
    [InlineData(90, -30, 0.75, true, false, true)]
    public void TheVectorMatrixPutsThePageWhereTheRasterTransformDrewIt(int rotation, double angle, double scale,
        bool fixedPage, bool flipH, bool flipV) => OnSta(() =>
    {
        // A dark 6 x 6 block centered on (10, 8) of the unrotated page image, one pixel per point.
        var bytes = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                byte value = x >= 7 && x < 13 && y >= 5 && y < 11 ? (byte)0 : (byte)255;
                int i = (y * W + x) * 4;
                bytes[i] = bytes[i + 1] = bytes[i + 2] = value;
                bytes[i + 3] = 255;
            }
        var (rotated, rw, rh) = BitmapHelpers.RotateBitmap(bytes, W, H, rotation);
        var source = BitmapSource.Create(rw, rh, 96, 96, PixelFormats.Bgra32, null, rotated, rw * 4);
        source.Freeze();
        BitmapSource composed = PageTransformRaster.ComposeTransform(source, angle, scale, fixedPage, flipH, flipV);
        var converted = new FormatConvertedBitmap(composed, PixelFormats.Bgra32, null, 0);
        int cw = converted.PixelWidth, ch = converted.PixelHeight;
        var pixels = new byte[cw * ch * 4];
        converted.CopyPixels(pixels, cw * 4, 0);
        double weight = 0, sumX = 0, sumY = 0;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                double dark = 255 - pixels[(y * cw + x) * 4 + 1];
                if (dark < 16) continue;
                weight += dark;
                sumX += dark * (x + 0.5);
                sumY += dark * (y + 0.5);
            }
        Assert.True(weight > 0);

        var t = PageTransformGeometry.For(CropLeft, CropBottom, W, H, rotation, angle, scale, fixedPage, flipH, flipV);
        double px = CropLeft + 10, py = CropBottom + H - 8;
        double mx = t.A * px + t.C * py + t.E;
        double my = t.Height - (t.B * px + t.D * py + t.F);

        Assert.InRange(t.Width, cw - 1.5, cw + 1.5);
        Assert.InRange(t.Height, ch - 1.5, ch + 1.5);
        Assert.InRange(mx, sumX / weight - 1.0, sumX / weight + 1.0);
        Assert.InRange(my, sumY / weight - 1.0, sumY / weight + 1.0);
    });

    // #522: a landscape page turned upright by Transform keeps its words, on the page it was, and the other pages
    // are left alone.
    [Fact]
    public void TransformingAWorkingFileKeepsTheTextOfEveryTurnedPage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-transform-{Guid.NewGuid():N}.pdf");
        var text = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 16)
            .MoveText(40, 200).ShowLatin1Text("Landscape words").EndText();
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(792, 612, text).AddBlankPage(612, 792).Build());
        try
        {
            PageTransformGeometry.TransformPages(path, [0], new Dictionary<int, int> { [0] = 90 },
                angleDeg: 7, scale: 0.9, fixedPage: false, flipH: false, flipV: false);

            var document = PdfDocument.Open(File.ReadAllBytes(path));
            var pages = PdfPageInformation.Read(document);
            Assert.True(pages[0].Height > pages[0].Width);
            Assert.Equal(612, pages[1].Width, 3);
            Assert.Contains("Landscape words", new PdfPageContentReader(document).Read(0).Text);
        }
        finally { File.Delete(path); }
    }
}
