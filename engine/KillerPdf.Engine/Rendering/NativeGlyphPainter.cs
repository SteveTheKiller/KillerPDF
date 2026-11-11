using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Filters;

namespace KillerPdf.Engine.Rendering;

internal abstract record NativePagePaint;
internal sealed record NativeGlyphPaint(PdfGlyphOutline Outline, double Left, double Baseline, double Scale, PdfRgbColor? Color = null) : NativePagePaint
{
    internal int SourceOffset { get; init; } = -1;
}
internal sealed record NativeImagePaint(PngDecodedImage Image, double Left, double Top) : NativePagePaint
{
    internal double Width { get; init; } = Image.Width;
    internal double Height { get; init; } = Image.Height;
}
internal sealed record NativeRectanglePaint(double Left, double Top, double Width, double Height, PdfRgbColor Color) : NativePagePaint;
internal sealed record NativeMarkerPaint(double CenterX, double CenterY, double Radius, PdfRgbColor Color) : NativePagePaint;

internal static class NativeGlyphPainter
{
    private const long MaximumWorkingPixelBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Paints a width x height page, or only the given region of it. A region keeps every paint at its whole-page
    /// position and coverage, so it is exactly that crop of the whole page, and only the region's pixels are stored.
    /// </summary>
    internal static PdfRenderedFrame Render(int width, int height, IReadOnlyList<NativePagePaint> paints,
        PdfRgbColor color, CancellationToken cancellationToken, double scaleX = 1, double scaleY = 1, PdfRgbColor? background = null,
        (int Left, int Top, int Width, int Height)? region = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(scaleX) || scaleX <= 0 || !double.IsFinite(scaleY) || scaleY <= 0)
            throw new InvalidDataException("The native page scale must be finite and positive.");
        var (regionLeft, regionTop, regionWidth, regionHeight) = region ?? (0, 0, width, height);
        if (regionLeft < 0 || regionTop < 0 || regionWidth <= 0 || regionHeight <= 0
            || (long)regionLeft + regionWidth > width || (long)regionTop + regionHeight > height)
            throw new ArgumentOutOfRangeException(nameof(region), "The region must fit inside the page raster.");
        int regionRight = regionLeft + regionWidth, regionBottom = regionTop + regionHeight;
        var bounds = (regionLeft, regionTop, regionRight, regionBottom);
        long pixelCount = (long)regionWidth * regionHeight;
        if (pixelCount > MaximumWorkingPixelBytes / (2 * 4 * sizeof(float)))
            throw new InvalidDataException("The native page exceeds its combined 128 MiB pixel storage limit.");
        if (paints.Count > 100_000)
            throw new InvalidDataException("The native page exceeds its paint limit.");
        var pixels = new float[checked((int)pixelCount * 4)];
        if (background is { } canvas)
        {
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if ((offset & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
                pixels[offset] = (float)canvas.Red;
                pixels[offset + 1] = (float)canvas.Green;
                pixels[offset + 2] = (float)canvas.Blue;
                pixels[offset + 3] = 1f;
            }
        }
        else Array.Fill(pixels, 1f);
        long imageWork = 0;
        long markerWork = 0;
        // Rectangles are charged against their own budget, which grows with the page raster: a page can
        // legitimately stack many page-sized backgrounds, so a fixed count of pixels would fail large pages, while a
        // multiple of the page size still bounds hostile input (at most 32 full overdraws, 16M pixels at the least).
        // The limit and the charge follow the whole page, not the region, so a page is accepted or refused the same
        // way whole or in tiles.
        // Image and marker work follow the same rule on the same page-clamped geometry: 32 page areas of image
        // pixels, and of marker pixels at 16 samples each, with the same 16M floor.
        long rectangleWork = 0;
        long rectangleLimit = Math.Max(16L * 1024 * 1024, (long)width * height * 32);
        long imageLimit = rectangleLimit;
        long markerLimit = Math.Max(16L * 1024 * 1024, (long)width * height * 32 * 16);
        foreach (var paint in paints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paint is NativeRectanglePaint rectangle)
            {
                double x0 = rectangle.Left * scaleX, y0 = rectangle.Top * scaleY;
                double x1 = x0 + rectangle.Width * scaleX, y1 = y0 + rectangle.Height * scaleY;
                if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1)
                    || rectangle.Width < 0 || rectangle.Height < 0)
                    throw new InvalidDataException("The native rectangle placement is invalid.");
                int leftEdge = (int)Math.Clamp(Math.Floor(x0), regionLeft, regionRight);
                int topEdge = (int)Math.Clamp(Math.Floor(y0), regionTop, regionBottom);
                int rightEdge = (int)Math.Clamp(Math.Ceiling(x1), regionLeft, regionRight);
                int bottomEdge = (int)Math.Clamp(Math.Ceiling(y1), regionTop, regionBottom);
                int pageLeft = (int)Math.Clamp(Math.Floor(x0), 0, width), pageRight = (int)Math.Clamp(Math.Ceiling(x1), 0, width);
                int pageTop = (int)Math.Clamp(Math.Floor(y0), 0, height), pageBottom = (int)Math.Clamp(Math.Ceiling(y1), 0, height);
                rectangleWork += (long)(pageRight - pageLeft) * (pageBottom - pageTop);
                if (rectangleWork > rectangleLimit)
                    throw new InvalidDataException("The native page exceeds its rectangle paint work limit.");
                for (int y = topEdge; y < bottomEdge; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double vertical = Math.Min(y + 1, y1) - Math.Max(y, y0);
                    for (int x = leftEdge; x < rightEdge; x++)
                    {
                        double coverage = vertical * (Math.Min(x + 1, x1) - Math.Max(x, x0));
                        FloatRgbCompositor.SourceOver(pixels, ((y - regionTop) * regionWidth + x - regionLeft) * 4,
                            rectangle.Color.Red, rectangle.Color.Green, rectangle.Color.Blue, coverage);
                    }
                }
                continue;
            }
            if (paint is NativeImagePaint image)
            {
                double imageLeft = image.Left * scaleX, imageTop = image.Top * scaleY;
                double imageWidth = image.Width * scaleX, imageHeight = image.Height * scaleY;
                if (!double.IsFinite(imageLeft) || !double.IsFinite(imageTop)
                    || !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) || imageWidth < 0 || imageHeight < 0)
                    throw new InvalidDataException("The native image placement is invalid.");
                if (imageWidth == 0 || imageHeight == 0) continue;
                int x0 = (int)Math.Clamp(Math.Ceiling(imageLeft - .5), regionLeft, regionRight);
                int y0 = (int)Math.Clamp(Math.Ceiling(imageTop - .5), regionTop, regionBottom);
                int x1 = (int)Math.Clamp(Math.Ceiling(imageLeft + imageWidth - .5), regionLeft, regionRight);
                int y1 = (int)Math.Clamp(Math.Ceiling(imageTop + imageHeight - .5), regionTop, regionBottom);
                int pageX0 = (int)Math.Clamp(Math.Ceiling(imageLeft - .5), 0, width);
                int pageY0 = (int)Math.Clamp(Math.Ceiling(imageTop - .5), 0, height);
                int pageX1 = (int)Math.Clamp(Math.Ceiling(imageLeft + imageWidth - .5), 0, width);
                int pageY1 = (int)Math.Clamp(Math.Ceiling(imageTop + imageHeight - .5), 0, height);
                imageWork += (long)Math.Max(0, pageX1 - pageX0) * Math.Max(0, pageY1 - pageY0);
                if (imageWork > imageLimit)
                    throw new InvalidDataException("The native page exceeds its image paint work limit.");
                for (int y = y0; y < y1; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int sy = Math.Clamp((int)((y + .5 - imageTop) * image.Image.Height / imageHeight), 0, image.Image.Height - 1);
                    for (int x = x0; x < x1; x++)
                    {
                        int sx = Math.Clamp((int)((x + .5 - imageLeft) * image.Image.Width / imageWidth), 0, image.Image.Width - 1);
                        int sample = (sy * image.Image.Width + sx) * 3;
                        FloatRgbCompositor.SourceOver(pixels, ((y - regionTop) * regionWidth + x - regionLeft) * 4,
                            image.Image.Samples[sample] / 255d, image.Image.Samples[sample + 1] / 255d,
                            image.Image.Samples[sample + 2] / 255d,
                            image.Image.Alpha is { } alpha ? alpha[sy * image.Image.Width + sx] / 255d : 1);
                    }
                }
                continue;
            }
            if (paint is NativeMarkerPaint marker)
            {
                double centerX = marker.CenterX * scaleX, centerY = marker.CenterY * scaleY;
                double radiusX = marker.Radius * scaleX, radiusY = marker.Radius * scaleY;
                if (!double.IsFinite(centerX) || !double.IsFinite(centerY)
                    || !double.IsFinite(radiusX) || radiusX <= 0 || !double.IsFinite(radiusY) || radiusY <= 0
                    || !double.IsFinite(centerX - radiusX) || !double.IsFinite(centerX + radiusX)
                    || !double.IsFinite(centerY - radiusY) || !double.IsFinite(centerY + radiusY))
                    throw new InvalidDataException("The native marker placement is invalid.");
                int x0 = (int)Math.Clamp(Math.Floor(centerX - radiusX), regionLeft, regionRight);
                int y0 = (int)Math.Clamp(Math.Floor(centerY - radiusY), regionTop, regionBottom);
                int x1 = (int)Math.Clamp(Math.Ceiling(centerX + radiusX), regionLeft, regionRight);
                int y1 = (int)Math.Clamp(Math.Ceiling(centerY + radiusY), regionTop, regionBottom);
                int pageX0 = (int)Math.Clamp(Math.Floor(centerX - radiusX), 0, width);
                int pageY0 = (int)Math.Clamp(Math.Floor(centerY - radiusY), 0, height);
                int pageX1 = (int)Math.Clamp(Math.Ceiling(centerX + radiusX), 0, width);
                int pageY1 = (int)Math.Clamp(Math.Ceiling(centerY + radiusY), 0, height);
                markerWork += (long)(pageX1 - pageX0) * (pageY1 - pageY0) * 16;
                if (markerWork > markerLimit)
                    throw new InvalidDataException("The native page exceeds its marker paint work limit.");
                for (int y = y0; y < y1; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = x0; x < x1; x++)
                    {
                        int coverage = 0;
                        for (int sy = 0; sy < 4; sy++)
                            for (int sx = 0; sx < 4; sx++)
                            {
                                double dx = (x + (sx + .5) / 4 - centerX) / radiusX;
                                double dy = (y + (sy + .5) / 4 - centerY) / radiusY;
                                if (dx * dx + dy * dy <= 1) coverage++;
                            }
                        if (coverage != 0)
                            FloatRgbCompositor.SourceOver(pixels, ((y - regionTop) * regionWidth + x - regionLeft) * 4,
                                marker.Color.Red, marker.Color.Green, marker.Color.Blue, coverage / 16d);
                    }
                }
                continue;
            }
            var glyph = (NativeGlyphPaint)paint;
            PdfRgbColor foreground = glyph.Color ?? color;
            double left = glyph.Left * scaleX;
            double baseline = glyph.Baseline * scaleY;
            double glyphScaleX = glyph.Scale * scaleX;
            double glyphScaleY = glyph.Scale * scaleY;
            if (!double.IsFinite(left) || !double.IsFinite(baseline)
                || !double.IsFinite(glyphScaleX) || glyphScaleX <= 0
                || !double.IsFinite(glyphScaleY) || glyphScaleY <= 0)
                throw new InvalidDataException("The native glyph placement is invalid.");
            var mask = PdfPageRenderer.RasterizeNativeGlyph(glyph.Outline, left,
                baseline, glyphScaleX, glyphScaleY, width, height, cancellationToken, bounds);
            try
            {
                // A rectangular glyph comes back whole, so the region clips here too.
                int maskRight = Math.Min(mask.Right, regionRight);
                for (int y = Math.Max(mask.Top, regionTop), maskBottom = Math.Min(mask.Bottom, regionBottom); y < maskBottom; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = Math.Max(mask.Left, regionLeft); x < maskRight; x++)
                    {
                        double coverage = mask.At(x, y) / 255d;
                        if (coverage == 0) continue;
                        FloatRgbCompositor.SourceOver(pixels, ((y - regionTop) * regionWidth + x - regionLeft) * 4,
                            foreground.Red, foreground.Green, foreground.Blue, coverage);
                    }
                }
            }
            finally { mask.Return(); }
        }
        FloatRgbCompositor.DecodeSrgbInPlace(pixels, cancellationToken);
        return new(regionWidth, regionHeight, pixels, PdfFrameColorEncoding.LinearSrgbD65, MaximumWorkingPixelBytes);
    }
}
