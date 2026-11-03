using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // This document-free bridge shares the established outline flattening and coverage engine.
    internal static CoverageMask RasterizeNativeGlyph(PdfGlyphOutline outline,
        double left, double baseline, double scale, int width, int height,
        CancellationToken cancellationToken)
    {
        var source = FlattenGlyphOutlineCore(outline);
        var polygons = new List<Point[]>(source.Count);
        foreach (var path in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transformed = new Point[path.Length];
            for (int index = 0; index < path.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                transformed[index] = new(left + path[index].X * scale, baseline - path[index].Y * scale);
            }
            polygons.Add(transformed);
        }
        return RasterizePolygons(polygons, false, width, height, rent: true);
    }
}
