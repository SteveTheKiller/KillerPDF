using EngineRenderOptions = KillerPdf.Engine.Rendering.PdfRenderOptions;

namespace KillerPDF.Services;

/// <summary>Where one page lands on a printed sheet and how large its print raster is.</summary>
internal static class PrintPagePlacement
{
    internal const int FitToPage = 0;
    internal const int ActualSize = 1;
    internal const int CustomScale = 2;
    internal const int ShrinkOversized = 3;

    /// <summary>A page's printed size on the sheet in DIPs.</summary>
    internal readonly record struct Placement(double Width, double Height);

    /// <summary>The printed size of a page in an area of the sheet under one scale mode.</summary>
    internal static Placement Place(double pageWidth, double pageHeight, double areaWidth, double areaHeight,
        int scaleMode, double customPercent)
    {
        if (!Valid(pageWidth, pageHeight)) return new Placement(Math.Max(1, areaWidth), Math.Max(1, areaHeight));
        double fit = Math.Min(Math.Max(1, areaWidth) / pageWidth, Math.Max(1, areaHeight) / pageHeight);
        double scale = scaleMode switch
        {
            ActualSize => 1,
            CustomScale => customPercent / 100.0,
            ShrinkOversized => Math.Min(1, fit),
            _ => fit
        };
        return new Placement(pageWidth * scale, pageHeight * scale);
    }

    /// <summary>The pixel box a page renders into for printing at a resolution (#481): the size it
    /// prints on the sheet, never above the page's own size at that resolution, and within the
    /// engine's render limit.</summary>
    internal static (int Width, int Height) RasterBox(double pageWidth, double pageHeight, Placement placement, double dpi)
    {
        double pixelsPerDip = dpi / 96.0;
        if (!Valid(pageWidth, pageHeight))
            return WithinRenderLimit(Pixels(placement.Width * pixelsPerDip), Pixels(placement.Height * pixelsPerDip));
        double scale = Math.Min(1, Math.Min(placement.Width / pageWidth, placement.Height / pageHeight));
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

    private static bool Valid(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
}
