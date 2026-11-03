using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // This document-free bridge shares the established outline flattening and coverage engine.
    internal static CoverageMask RasterizeNativeGlyph(PdfGlyphOutline outline,
        double left, double baseline, double scaleX, double scaleY, int width, int height,
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
                double x = left + path[index].X * scaleX;
                double y = baseline - path[index].Y * scaleY;
                if (!double.IsFinite(x) || !double.IsFinite(y))
                    throw new InvalidDataException("The transformed native glyph coordinates are not finite.");
                transformed[index] = new(x, y);
            }
            polygons.Add(transformed);
        }
        return RasterizePolygons(polygons, false, width, height, rent: true);
    }
}
