using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

// The sidebar thumbnail of a page takes the monitor conversion the page itself takes.
public sealed class PageThumbnailMonitorTests
{
    // Linear sRGB red through the identity-XYZ test monitor quantizes to device bytes R=111, G=57, B=4.
    private static readonly byte[] MonitorRed = [4, 57, 111, 255];
    private static readonly byte[] SrgbRed = [0, 0, 255, 255];

    [Fact]
    public void APdfThumbnailOnAProfiledMonitorIsConvertedForThatMonitor()
    {
        WithRedPage(path =>
        {
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 8, 8, null);

            var drawn = PageThumbnailVm.RenderPdfThumbPixels(session, 0, 0, default,
                monitor: PdfViewingFrameConversionTests.Monitor());

            Assert.NotNull(drawn);
            Assert.Equal(MonitorRed, drawn.Value.Pixels.AsSpan(0, 4).ToArray());
        });
    }

    [Fact]
    public void APdfThumbnailWithoutAMonitorProfileKeepsThePlainSrgbPicture()
    {
        WithRedPage(path =>
        {
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 8, 8, null);
            byte[] plain = session.RenderPreview(0).Pixels;

            var drawn = PageThumbnailVm.RenderPdfThumbPixels(session, 0, 0, default);

            Assert.NotNull(drawn);
            Assert.Equal(SrgbRed, drawn.Value.Pixels.AsSpan(0, 4).ToArray());
            Assert.Equal(plain, drawn.Value.Pixels);
        });
    }

    // A blank DjVu page is white: linear sRGB white is D50 white (0.9642, 1, 0.8249) through the identity-XYZ
    // monitor, so device bytes R=246, G=255, B=210. Without a monitor profile it stays sRGB white.
    [Fact]
    public void ANativeThumbnailTakesTheMonitorConversionOnlyWhenTheMonitorHasAProfile()
    {
        using var owner = NativeWorkingDocument.OpenDjvu(NativeWorkingDocumentTests.DjvuPageWithText("AB"u8.ToArray(), zoned: true));
        var revision = owner.CaptureReadRevision();

        var converted = FirstPixel(PageThumbnailVm.BuildNativeThumb(revision, 0, 0, 16, 16, default,
            monitor: PdfViewingFrameConversionTests.Monitor()));
        var plain = FirstPixel(PageThumbnailVm.BuildNativeThumb(revision, 0, 0, 16, 16, default));

        Assert.InRange(converted[0], 209, 211);
        Assert.Equal(255, converted[1]);
        Assert.InRange(converted[2], 245, 247);
        Assert.Equal([255, 255, 255, 255], plain);
    }

    private static byte[] FirstPixel(System.Windows.Media.Imaging.BitmapSource? thumbnail)
    {
        Assert.NotNull(thumbnail);
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(thumbnail,
            System.Windows.Media.PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static void WithRedPage(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(8, 8,
                new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 0, 8, 8).Fill()).Build());
            test(path);
        }
        finally { PdfPageRenderSession.ForgetDocument(path); File.Delete(path); }
    }
}
