using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // Flatten calls made on this thread by the native glyph bridge, so a test can count the work it repeats.
    [ThreadStatic] internal static long NativeFlattenCalls;

    // Flattening depends on the outline alone (glyph units, before any placement or scale), and a font hands out one
    // outline object per glyph, so repeats of a glyph on a page, and on later pages, share one flattened copy.
    // The renderer-owned cache needs an instance, which this bridge does not have; this one is bounded the same way
    // (4096 outlines, 16 MiB of path points), keyed by outline identity, and its lookups are locked, so concurrent
    // native renders may share it. Callers only read the cached paths.
    private static readonly BoundedCache<PdfGlyphOutline, IReadOnlyList<Point[]>> NativeGlyphPaths = CreateGlyphPathCache();

    private static IReadOnlyList<Point[]> FlattenNativeOutline(PdfGlyphOutline outline)
    {
        NativeFlattenCalls++;
        return FlattenGlyphOutlineCore(outline);
    }

    // This document-free bridge shares the established outline flattening and coverage engine.
    internal static CoverageMask RasterizeNativeGlyph(PdfGlyphOutline outline,
        double left, double baseline, double scaleX, double scaleY, int width, int height,
        CancellationToken cancellationToken, (int Left, int Top, int Right, int Bottom)? bounds = null)
    {
        var source = NativeGlyphPaths.GetOrAdd(outline, FlattenNativeOutline);
        var polygons = new List<Point[]>(source.Count);
        foreach (var path in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transformed = new Point[path.Length];
            for (int index = 0; index < path.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                double x = left + path[index].X * scaleX;
                double y = baseline - path[index].Y * scaleY;
                if (!double.IsFinite(x) || !double.IsFinite(y))
                    throw new InvalidDataException("The transformed native glyph coordinates are not finite.");
                transformed[index] = new(x, y);
            }
            polygons.Add(transformed);
        }
        // Bounds keep only the pixels inside them, each with its unbounded coverage.
        return RasterizePolygons(polygons, false, width, height, rent: true, bounds);
    }
}
