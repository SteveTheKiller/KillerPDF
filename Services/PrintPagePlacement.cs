using System.Windows.Media;
using System.Windows.Media.Imaging;
using EngineRenderOptions = KillerPdf.Engine.Rendering.PdfRenderOptions;

namespace KillerPDF.Services;

/// <summary>Where one page lands on a printed sheet and how large its print raster is.</summary>
internal static class PrintPagePlacement
{
    internal const int FitToPage = 0;
    internal const int ActualSize = 1;
    internal const int CustomScale = 2;
    internal const int ShrinkOversized = 3;

    /// <summary>A page's printed size on the sheet in DIPs, after any quarter turn.</summary>
    internal readonly record struct Placement(double Width, double Height, bool Rotated = false);

    /// <summary>The printed size of a page in an area of the sheet under one scale mode.</summary>
    internal static Placement Place(double pageWidth, double pageHeight, double areaWidth, double areaHeight,
        int scaleMode, double customPercent, bool autoRotate)
    {
        if (!Valid(pageWidth, pageHeight)) return new Placement(Math.Max(1, areaWidth), Math.Max(1, areaHeight));
        // Fit to page turns a landscape page on a portrait sheet, or the reverse, a quarter turn so
        // it fills the sheet (#482). The other scale modes keep the page as it is.
        bool rotated = autoRotate && scaleMode == FitToPage && pageWidth != pageHeight
            && areaWidth != areaHeight && (pageWidth > pageHeight) != (areaWidth > areaHeight);
        if (rotated) (pageWidth, pageHeight) = (pageHeight, pageWidth);
        double fit = Math.Min(Math.Max(1, areaWidth) / pageWidth, Math.Max(1, areaHeight) / pageHeight);
        double scale = scaleMode switch
        {
            ActualSize => 1,
            CustomScale => customPercent / 100.0,
            ShrinkOversized => Math.Min(1, fit),
            _ => fit
        };
        return new Placement(pageWidth * scale, pageHeight * scale, rotated);
    }

    /// <summary>The pixel box a page renders into for printing at a resolution (#481): the size it
    /// prints on the sheet, never above the page's own size at that resolution, and within the
    /// engine's render limit.</summary>
    internal static (int Width, int Height) RasterBox(double pageWidth, double pageHeight, Placement placement, double dpi)
    {
        double pixelsPerDip = dpi / 96.0;
        if (!Valid(pageWidth, pageHeight))
            return WithinRenderLimit(Pixels(placement.Width * pixelsPerDip), Pixels(placement.Height * pixelsPerDip));
        // A turned page prints with its width along the sheet's height.
        var (printedWidth, printedHeight) = placement.Rotated
            ? (placement.Height, placement.Width) : (placement.Width, placement.Height);
        double scale = Math.Min(1, Math.Min(printedWidth / pageWidth, printedHeight / pageHeight));
        return WithinRenderLimit(Pixels(pageWidth * scale * pixelsPerDip), Pixels(pageHeight * scale * pixelsPerDip));
    }

    /// <summary>A pixel size reduced, keeping its shape, until it fits the engine's render limit.</summary>
    internal static (int Width, int Height) WithinRenderLimit(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        double pixelLimit = EngineRenderOptions.MaximumPixelBytes / 4.0;
        double shrink = Math.Min(1, Math.Min(Math.Sqrt(pixelLimit / ((double)width * height)),
            (double)EngineRenderOptions.MaximumDimension / Math.Max(width, height)));
        if (shrink >= 1) return (width, height);
        int limitedWidth = Math.Max(1, (int)(width * shrink));
        int limitedHeight = Math.Max(1, (int)(height * shrink));
        // Floating point can leave the product one row over the limit.
        while ((long)limitedWidth * limitedHeight * 4 > EngineRenderOptions.MaximumPixelBytes) limitedHeight--;
        return (Math.Min(limitedWidth, EngineRenderOptions.MaximumDimension),
            Math.Min(limitedHeight, EngineRenderOptions.MaximumDimension));
    }

    private static int Pixels(double value) =>
        double.IsFinite(value) ? (int)Math.Clamp(value, 1, int.MaxValue) : 1;

    /// <summary>Renders a page for printing at the size it prints on the sheet. A print job passes
    /// requireComplete: false and names incomplete pages through IncompletePageLog.</summary>
    internal static PdfRenderedPage Render(PdfPageRenderSession session, int pageIndex,
        double pageWidth, double pageHeight, Placement placement, double dpi, bool requireComplete,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var (width, height) = RasterBox(pageWidth, pageHeight, placement, dpi);
        PdfRenderedPage page = session.RenderFittedPage(pageIndex, width, height, includeFormFields: true,
            cancellationToken);
        if (requireComplete) page.RequireComplete(pageIndex);
        return page;
    }

    /// <summary>A page raster given the quarter turn its placement calls for: counterclockwise, the
    /// direction of a printer's landscape output, so the preview and the job turn it alike.</summary>
    internal static BitmapSource Turn(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Freezing the turned bitmap freezes its source too, which only the source's own thread may do.
        // A page raster made on another thread is copied there, frozen, before it is turned. (#492)
        // A frozen bitmap belongs to no thread and always passes CheckAccess; IsFrozen itself may only be
        // read on the owning thread, so CheckAccess comes first.
        BitmapSource page = source;
        if (!page.CheckAccess()) source = page.Dispatcher.Invoke(() => Frozen(page));
        else if (!page.IsFrozen) source = Frozen(page);
        var turned = new TransformedBitmap(source, new RotateTransform(270));
        turned.Freeze();
        return turned;
    }

    private static BitmapSource Frozen(BitmapSource page)
    {
        if (page.CanFreeze) { BitmapSource copy = page.Clone(); copy.Freeze(); return copy; }
        // A source that cannot freeze (still downloading, say) is copied pixel for pixel instead.
        var pixels = new WriteableBitmap(page);
        pixels.Freeze();
        return pixels;
    }

    private static bool Valid(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
}
