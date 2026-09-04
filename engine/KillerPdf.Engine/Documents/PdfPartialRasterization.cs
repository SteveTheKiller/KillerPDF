using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

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

    /// <summary>Replaces the selected visual region with caller-rendered raster pixels.</summary>
    public static byte[] Apply(PdfDocument document, int pageIndex,
        PdfPartialRasterizationPlan plan, PdfImage raster)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(raster);
        PdfPageContent page = new PdfPageContentReader(document).Read(pageIndex);
        if (plan.Region.Left < 0 || plan.Region.Bottom < 0
            || plan.Region.Right > page.Width || plan.Region.Top > page.Height
            || plan.Region.Width <= 0 || plan.Region.Height <= 0)
            throw new ArgumentException(
                "The rasterization plan does not fit the selected page.", nameof(plan));
        if (raster.Width != plan.PixelWidth || raster.Height != plan.PixelHeight)
            throw new ArgumentException(
                "The raster dimensions do not match the rasterization plan.", nameof(raster));

        var retained = new List<PdfContentInstruction>(page.Instructions.Count + 7)
        {
            new("q", 0, []),
            Rectangle(0, 0, page.Width, page.Height),
            Rectangle(plan.Region.Left, plan.Region.Bottom,
                plan.Region.Width, plan.Region.Height),
            new("W*", 0, []),
            new("n", 0, [])
        };
        retained.AddRange(page.Instructions);
        retained.Add(new PdfContentInstruction("Q", 0, []));
        var replacement = new PdfContentStreamBuilder()
            .DrawImage(raster, plan.Region.Left, plan.Region.Bottom,
                plan.Region.Width, plan.Region.Height);
        return new PdfIncrementalPageEditor(document)
            .SetPageContent(pageIndex, retained)
            .AppendPageContent(pageIndex, page.Width, page.Height, replacement)
            .Build();
    }

    private static PdfContentInstruction Rectangle(
        double x, double y, double width, double height) =>
        new("re", 0, [Number(x), Number(y), Number(width), Number(height)]);

    private static PdfObject Number(double value) => value == Math.Truncate(value)
        ? new PdfInteger((long)value) : new PdfReal(value);

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
