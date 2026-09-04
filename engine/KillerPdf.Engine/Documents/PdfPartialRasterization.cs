namespace KillerPdf.Engine.Documents;

/// <summary>Describes the page content and raster dimensions affected by a selected region.</summary>
public sealed record PdfPartialRasterizationPlan
{
    /// <summary>Gets the selected region in unrotated PDF page coordinates.</summary>
    public required PdfContentBounds Region { get; init; }
    /// <summary>Gets the raster resolution.</summary>
    public required double Dpi { get; init; }
    /// <summary>Gets the required raster width in pixels.</summary>
    public required int PixelWidth { get; init; }
    /// <summary>Gets the required raster height in pixels.</summary>
    public required int PixelHeight { get; init; }
    /// <summary>Gets text runs intersecting the selected region.</summary>
    public IReadOnlyList<PdfExtractedTextRun> TextRuns { get; init; } = [];
    /// <summary>Gets image placements intersecting the selected region.</summary>
    public IReadOnlyList<PdfExtractedImage> Images { get; init; } = [];
    /// <summary>Gets vector paths intersecting the selected region.</summary>
    public IReadOnlyList<PdfExtractedPath> Paths { get; init; } = [];
    /// <summary>Gets shading paints intersecting the selected region.</summary>
    public IReadOnlyList<PdfExtractedShading> Shadings { get; init; } = [];
}

/// <summary>Plans region rasterization without changing the source document.</summary>
public static class PdfPartialRasterization
{
    /// <summary>Validates a selected page region and identifies affected page content.</summary>
    public static PdfPartialRasterizationPlan Plan(
        PdfPageContent page, PdfContentBounds region, double dpi)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!double.IsFinite(region.Left) || !double.IsFinite(region.Bottom)
            || !double.IsFinite(region.Right) || !double.IsFinite(region.Top)
            || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region));
        if (region.Left < 0 || region.Bottom < 0
            || region.Right > page.Width || region.Top > page.Height)
            throw new ArgumentOutOfRangeException(
                nameof(region), "The raster region must remain inside the page.");
        if (!double.IsFinite(dpi) || dpi <= 0 || dpi > 2400)
            throw new ArgumentOutOfRangeException(nameof(dpi));
        int pixelWidth = CheckedPixels(region.Width, dpi);
        int pixelHeight = CheckedPixels(region.Height, dpi);
        return new PdfPartialRasterizationPlan
        {
            Region = region,
            Dpi = dpi,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            TextRuns = Array.AsReadOnly(page.TextRuns
                .Where(run => Intersects(run.BoundingBox, region)).ToArray()),
            Images = Array.AsReadOnly(page.Images
                .Where(image => Intersects(image.BoundingBox, region)).ToArray()),
            Paths = Array.AsReadOnly(page.Paths
                .Where(path => Intersects(path.BoundingBox, region)).ToArray()),
            Shadings = Array.AsReadOnly(page.Shadings
                .Where(shading => Intersects(shading.BoundingBox, region)).ToArray())
        };
    }

    private static int CheckedPixels(double points, double dpi)
    {
        double pixels = Math.Ceiling(points * dpi / 72d);
        if (pixels > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(points),
                "The raster region is too large.");
        return Math.Max(1, (int)pixels);
    }

    private static bool Intersects(PdfContentBounds content, PdfContentBounds region) =>
        content.Right > region.Left && content.Left < region.Right
        && content.Top > region.Bottom && content.Bottom < region.Top;
}
