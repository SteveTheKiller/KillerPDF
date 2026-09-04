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

/// <summary>Pairs one page-local rasterization plan with its rendered pixels.</summary>
public sealed record PdfPartialRasterizationReplacement(
    int PageIndex, PdfPartialRasterizationPlan Plan, PdfImage Raster);

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
        return Apply(document,
            [new PdfPartialRasterizationReplacement(pageIndex, plan, raster)]);
    }

    /// <summary>Replaces multiple non-overlapping selected regions in one incremental revision.</summary>
    public static byte[] Apply(PdfDocument document,
        IEnumerable<PdfPartialRasterizationReplacement> replacements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(replacements);
        PdfPartialRasterizationReplacement[] selected = replacements.ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("At least one raster replacement is required.",
                nameof(replacements));
        var reader = new PdfPageContentReader(document);
        var editor = new PdfIncrementalPageEditor(document);
        foreach (IGrouping<int, PdfPartialRasterizationReplacement> pageGroup
            in selected.GroupBy(replacement => replacement.PageIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageContent page = reader.Read(pageGroup.Key, cancellationToken);
            PdfPartialRasterizationReplacement[] pageReplacements = pageGroup.ToArray();
            for (int index = 0; index < pageReplacements.Length; index++)
            {
                ValidateReplacement(page, pageReplacements[index]);
                for (int other = 0; other < index; other++)
                    if (Intersects(pageReplacements[index].Plan.Region,
                            pageReplacements[other].Plan.Region))
                        throw new ArgumentException(
                            "Raster replacement regions cannot overlap.", nameof(replacements));
            }
            var retained = new List<PdfContentInstruction>(
                page.Instructions.Count + pageReplacements.Length + 6)
            {
                new("q", 0, []),
                Rectangle(0, 0, page.Width, page.Height)
            };
            foreach (PdfPartialRasterizationReplacement replacement in pageReplacements)
                retained.Add(Rectangle(replacement.Plan.Region.Left,
                    replacement.Plan.Region.Bottom, replacement.Plan.Region.Width,
                    replacement.Plan.Region.Height));
            retained.Add(new PdfContentInstruction("W*", 0, []));
            retained.Add(new PdfContentInstruction("n", 0, []));
            retained.AddRange(page.Instructions);
            retained.Add(new PdfContentInstruction("Q", 0, []));
            editor.SetPageContent(pageGroup.Key, retained);
            foreach (PdfPartialRasterizationReplacement replacement in pageReplacements)
                editor.AppendPageContent(pageGroup.Key, page.Width, page.Height,
                    new PdfContentStreamBuilder().DrawImage(replacement.Raster,
                        replacement.Plan.Region.Left, replacement.Plan.Region.Bottom,
                        replacement.Plan.Region.Width, replacement.Plan.Region.Height));
        }
        return editor.Build();
    }

    private static void ValidateReplacement(PdfPageContent page,
        PdfPartialRasterizationReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement.Plan);
        ArgumentNullException.ThrowIfNull(replacement.Raster);
        PdfPartialRasterizationPlan plan = replacement.Plan;
        if (plan.Region.Left < 0 || plan.Region.Bottom < 0
            || plan.Region.Right > page.Width || plan.Region.Top > page.Height
            || plan.Region.Width <= 0 || plan.Region.Height <= 0)
            throw new ArgumentException(
                "The rasterization plan does not fit the selected page.", nameof(replacement));
        if (replacement.Raster.Width != plan.PixelWidth
            || replacement.Raster.Height != plan.PixelHeight)
            throw new ArgumentException(
                "The raster dimensions do not match the rasterization plan.",
                nameof(replacement));
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
