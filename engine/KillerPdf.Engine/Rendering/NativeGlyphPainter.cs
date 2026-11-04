using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Rendering;

internal sealed record NativeGlyphPaint(PdfGlyphOutline Outline, double Left, double Baseline, double Scale, PdfRgbColor? Color = null);

internal static class NativeGlyphPainter
{
    private const long MaximumWorkingPixelBytes = 128L * 1024 * 1024;

    internal static PdfRenderedFrame Render(int width, int height, IReadOnlyList<NativeGlyphPaint> glyphs,
        PdfRgbColor color, CancellationToken cancellationToken, double scaleX = 1, double scaleY = 1)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(scaleX) || scaleX <= 0 || !double.IsFinite(scaleY) || scaleY <= 0)
            throw new InvalidDataException("The native page scale must be finite and positive.");
        long pixelCount = (long)width * height;
        if (pixelCount > MaximumWorkingPixelBytes / (2 * 4 * sizeof(float)))
            throw new InvalidDataException("The native page exceeds its combined 128 MiB pixel storage limit.");
        if (glyphs.Count > 100_000)
            throw new InvalidDataException("The native page exceeds its glyph limit.");
        var pixels = new float[checked((int)pixelCount * 4)];
        Array.Fill(pixels, 1f);
        foreach (var glyph in glyphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                baseline, glyphScaleX, glyphScaleY, width, height, cancellationToken);
            try
            {
                for (int y = mask.Top; y < mask.Bottom; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = mask.Left; x < mask.Right; x++)
                    {
                        double coverage = mask.At(x, y) / 255d;
                        if (coverage == 0) continue;
                        FloatRgbCompositor.SourceOver(pixels, (y * width + x) * 4,
                            foreground.Red, foreground.Green, foreground.Blue, coverage);
                    }
                }
            }
            finally { mask.Return(); }
        }
        FloatRgbCompositor.DecodeSrgbInPlace(pixels, cancellationToken);
        return new(width, height, pixels, PdfFrameColorEncoding.LinearSrgbD65, MaximumWorkingPixelBytes);
    }
}
