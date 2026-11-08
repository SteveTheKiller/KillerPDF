using System.IO;
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
                PrintPagePlacement.FitToPage, 100);
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
        var placement = PrintPagePlacement.Place(pageWidth, pageHeight, 793.7, 1122.5, PrintPagePlacement.FitToPage, 100);

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
}
