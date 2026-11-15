using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using PdfRenderOptions = KillerPdf.Engine.Rendering.PdfRenderOptions;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrintPagePlacementTests
{
    private const double DipsPerPoint = 96.0 / 72.0;

    // #481: an A4 page followed by a 2579 x 3645 pt page, printed with Fit to page on A4,
    // JIS B4 and JIS B5 sheets (printable areas in DIPs).
    [Theory]
    [InlineData(793.7, 1122.5)]
    [InlineData(971.3, 1375.7)]
    [InlineData(687.9, 971.3)]
    public void OversizedPageFitToPageRendersAtThePrintedSize(double areaWidth, double areaHeight)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-print-oversized-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(595, 842).AddBlankPage(2579, 3645).Build());
            double pageWidth = 2579 * DipsPerPoint, pageHeight = 3645 * DipsPerPoint;
            using PdfPageRenderSession session = PrintPermission.OpenRenderSession(path, PrintAllowance.Allowed, null);

            var placement = PrintPagePlacement.Place(pageWidth, pageHeight, areaWidth, areaHeight,
                PrintPagePlacement.FitToPage, 100, autoRotate: true);
            PdfRenderedPage page = PrintPagePlacement.Render(session, 1, pageWidth, pageHeight, placement,
                PrintPermission.FullDpi, requireComplete: true);

            // The page fits the sheet by its width or its height, whichever is tighter, and prints at 300 DPI.
            double printedWidth = Math.Min(areaWidth, areaHeight * 2579 / 3645);
            double printedHeight = printedWidth * 3645 / 2579;
            Assert.InRange(page.Width, printedWidth * 300 / 96 - 2, printedWidth * 300 / 96 + 1);
            Assert.InRange(page.Height, printedHeight * 300 / 96 - 2, printedHeight * 300 / 96 + 1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PageThatFitsItsSheetKeepsItsFullPrintResolution()
    {
        // A5 on an A4 sheet grows to fit; its raster stays at the page's own 300 DPI size.
        double pageWidth = 420 * DipsPerPoint, pageHeight = 595 * DipsPerPoint;
        var placement = PrintPagePlacement.Place(pageWidth, pageHeight, 793.7, 1122.5, PrintPagePlacement.FitToPage, 100,
            autoRotate: true);

        var (width, height) = PrintPagePlacement.RasterBox(pageWidth, pageHeight, placement, 300);

        Assert.Equal(1750, width);
        Assert.InRange(height, 2478, 2479);
    }

    [Theory]
    [InlineData(10746, 15187)]   // 2579 x 3645 pt at Actual size and 300 DPI
    [InlineData(60000, 416)]     // a 200 inch banner
    public void PrintRasterThatCannotFitIsDownscaledToTheRenderLimit(int width, int height)
    {
        var (limitedWidth, limitedHeight) = PrintPagePlacement.WithinRenderLimit(width, height);

        Assert.True((long)limitedWidth * limitedHeight * 4 <= PdfRenderOptions.MaximumPixelBytes);
        Assert.InRange(limitedWidth, 1, PdfRenderOptions.MaximumDimension);
        Assert.InRange(limitedHeight, 1, PdfRenderOptions.MaximumDimension);
        // Only as much as the limit needs, and in the same shape.
        Assert.True(limitedWidth == PdfRenderOptions.MaximumDimension
            || (long)(limitedWidth + 1) * (limitedHeight + 1) * 4 > PdfRenderOptions.MaximumPixelBytes);
        Assert.InRange((double)limitedWidth / limitedHeight, (double)width / height * 0.99, (double)width / height * 1.01);
    }

    [Fact]
    public void PrintRasterWithinTheLimitIsUnchanged()
    {
        Assert.Equal((2479, 3508), PrintPagePlacement.WithinRenderLimit(2479, 3508));
    }

    // #482: an A4 landscape page in a portrait job, printed with Fit to page on a portrait A4 sheet.
    [Fact]
    public void LandscapePageFitToPageTurnsToFillAPortraitSheet()
    {
        double pageWidth = 842 * DipsPerPoint, pageHeight = 595 * DipsPerPoint;

        var placement = PrintPagePlacement.Place(pageWidth, pageHeight, 793.7, 1122.5,
            PrintPagePlacement.FitToPage, 100, autoRotate: true);

        Assert.True(placement.Rotated);
        // Turned, it fills the sheet like a portrait A4 page instead of a 561 DIP strip across it.
        Assert.InRange(placement.Width, 792, 793.7);
        Assert.InRange(placement.Height, 1121, 1122.5);
    }

    [Fact]
    public void PortraitPageFitToPageTurnsToFillALandscapeSheet()
    {
        var placement = PrintPagePlacement.Place(595 * DipsPerPoint, 842 * DipsPerPoint, 1122.5, 793.7,
            PrintPagePlacement.FitToPage, 100, autoRotate: true);

        Assert.True(placement.Rotated);
        Assert.InRange(placement.Width, 1121, 1122.5);
        Assert.InRange(placement.Height, 792, 793.7);
    }

    [Theory]
    [InlineData(PrintPagePlacement.ActualSize, true)]
    [InlineData(PrintPagePlacement.CustomScale, true)]
    [InlineData(PrintPagePlacement.ShrinkOversized, true)]
    [InlineData(PrintPagePlacement.FitToPage, false)]   // N-up cells keep their pages upright
    public void OnlyFitToPageWithAutoRotateTurnsAPage(int scaleMode, bool autoRotate)
    {
        var placement = PrintPagePlacement.Place(842 * DipsPerPoint, 595 * DipsPerPoint, 793.7, 1122.5,
            scaleMode, 100, autoRotate);

        Assert.False(placement.Rotated);
    }

    [Theory]
    [InlineData(612, 792)]   // same orientation as the sheet
    [InlineData(700, 700)]   // square
    public void PageThatAlreadyMatchesTheSheetIsNotTurned(double width, double height)
    {
        var placement = PrintPagePlacement.Place(width * DipsPerPoint, height * DipsPerPoint, 793.7, 1122.5,
            PrintPagePlacement.FitToPage, 100, autoRotate: true);

        Assert.False(placement.Rotated);
    }

    [Fact]
    public void TurnedPageRastersAtTheSizeItPrints()
    {
        // A 3645 x 2579 pt landscape page turned onto a portrait A4 sheet prints about 1122 x 793 DIPs
        // in its own orientation, so its 300 DPI raster is landscape and about 3507 x 2480 pixels.
        double pageWidth = 3645 * DipsPerPoint, pageHeight = 2579 * DipsPerPoint;
        var placement = PrintPagePlacement.Place(pageWidth, pageHeight, 793.7, 1122.5,
            PrintPagePlacement.FitToPage, 100, autoRotate: true);

        var (width, height) = PrintPagePlacement.RasterBox(pageWidth, pageHeight, placement, 300);

        Assert.InRange(width, 3500, 3508);
        Assert.InRange(height, 2476, 2481);
    }

    [Fact]
    public void TurnRotatesThePageRasterAQuarterTurnCounterclockwise()
    {
        // Left pixel red, right pixel blue: a counterclockwise turn puts blue on top.
        byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 255];
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        source.Freeze();

        BitmapSource turned = PrintPagePlacement.Turn(source);

        Assert.Equal(1, turned.PixelWidth);
        Assert.Equal(2, turned.PixelHeight);
        var converted = new FormatConvertedBitmap(turned, PixelFormats.Bgra32, null, 0);
        byte[] result = new byte[8];
        converted.CopyPixels(result, 4, 0);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, result);
        Assert.True(turned.IsFrozen);
    }

    // #492: printing crashed when the page raster was an unfrozen bitmap owned by another thread, because
    // freezing the turned bitmap reaches into its source.
    [Fact]
    public void TurnAcceptsAnUnfrozenPageRasterFromAnotherThread()
    {
        BitmapSource? source = null;
        System.Windows.Threading.Dispatcher? owner = null;
        using var ready = new ManualResetEventSlim();
        var ownerThread = new Thread(() =>
        {
            byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 255];
            source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            owner = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        ownerThread.SetApartmentState(ApartmentState.STA);
        ownerThread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(60)));
        try
        {
            Exception? error = null;
            BitmapSource? turned = null;
            var printer = new Thread(() =>
            {
                try { turned = PrintPagePlacement.Turn(source!); }
                catch (Exception e) { error = e; }
            });
            printer.SetApartmentState(ApartmentState.STA);
            printer.Start();
            Assert.True(printer.Join(TimeSpan.FromSeconds(60)));
            Assert.Null(error);
            Assert.True(turned!.IsFrozen);
            Assert.Equal(1, turned.PixelWidth);
            Assert.Equal(2, turned.PixelHeight);
        }
        finally { owner!.InvokeShutdown(); }
    }
}
