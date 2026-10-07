using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private static readonly PdfName InterpolateImageName = new("Interpolate"u8);

    private bool ReadImageInterpolation(PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(InterpolateImageName, out PdfObject? value)) return false;
        try { return Resolve(value) is PdfBoolean { Value: true }; }
        catch (Exception error) when (error is FormatException or InvalidOperationException)
        {
            // An invalid optional sampling hint must not prevent ordinary image painting.
            return false;
        }
    }

    private static bool TryPaintInterpolatedDeviceImage(RasterSurface target, int targetWidth,
        int targetHeight, double scaleX, double scaleY, Matrix inverse, byte[] samples,
        int sourceWidth, int sourceHeight, int components, int bits, double[] decode,
        ImageColorSpace colorSpace, SoftMask? softMask, IReadOnlyList<ClipRegion> clips,
        double opacity, RendererBlendMode blendMode, GraphicsSoftMask? graphicsSoftMask,
        KnockoutState? knockout, bool alphaIsShape, int left, int top, int right, int bottom,
        CancellationToken cancellationToken)
    {
        if (target.Ink is not null || target.RgbProfile is not null || target.HasRgbSpotShadow
            || target.GroupShape is not null || graphicsSoftMask is not null || knockout is not null
            || alphaIsShape || bits != 8 || components != colorSpace.Components
            || colorSpace.Palette is not null || colorSpace.Profile is not null
            || colorSpace.ComponentRange is not null || colorSpace.Converter is not null
            || colorSpace.MultiConverter is not null || colorSpace.ContainsSpotColorants
            || !(components == 1 && decode is [0, 1]
                || components == 3 && decode is [0, 1, 0, 1, 0, 1])
            || blendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible)
            || softMask is not null && (softMask.Bits != 8 || softMask.DecodeStart != 0
                || softMask.DecodeEnd != 1 || softMask.Width != sourceWidth
                || softMask.Height != sourceHeight)) return false;

        // Only magnification uses this path. Reduction keeps the existing area sampler.
        double footprintX = Math.Sqrt(Math.Pow(inverse.A * sourceWidth, 2)
            + Math.Pow(inverse.B * sourceHeight, 2)) / scaleX;
        double footprintY = Math.Sqrt(Math.Pow(inverse.C * sourceWidth, 2)
            + Math.Pow(inverse.D * sourceHeight, 2)) / scaleY;
        if (!double.IsFinite(footprintX) || !double.IsFinite(footprintY)
            || footprintX > 1 || footprintY > 1
            || footprintX == 1 && footprintY == 1) return false;

        opacity = Math.Clamp(opacity, 0, 1);
        ForEachRow(top, bottom, (long)(right - left) * (bottom - top), (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = left; x < right; x++)
                {
                    Point unit = inverse.Apply((x + .5) / scaleX, (targetHeight - y - .5) / scaleY);
                    if (unit.X < 0 || unit.X >= 1 || unit.Y < 0 || unit.Y >= 1) continue;
                    int coverage = ClipCoverage(clips, x, y);
                    if (coverage == 0) continue;
                    double sx = Math.Clamp(unit.X * sourceWidth - .5, 0, sourceWidth - 1d);
                    double sy = Math.Clamp((1 - unit.Y) * sourceHeight - .5, 0, sourceHeight - 1d);
                    int x0 = (int)sx, y0 = (int)sy;
                    int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                    int y1 = Math.Min(y0 + 1, sourceHeight - 1);
                    double fx = sx - x0, fy = sy - y0;
                    double alpha = 0, red = 0, green = 0, blue = 0;
                    Accumulate(x0, y0, (1 - fx) * (1 - fy));
                    Accumulate(x1, y0, fx * (1 - fy));
                    Accumulate(x0, y1, (1 - fx) * fy);
                    Accumulate(x1, y1, fx * fy);
                    if (alpha == 0) continue;
                    // Interpolate premultiplied colors so invisible mask samples cannot tint an edge.
                    Color color = new((byte)Math.Round(red / alpha), (byte)Math.Round(green / alpha),
                        (byte)Math.Round(blue / alpha));
                    SetPixel(target, targetWidth, x, y, color,
                        alpha / 255 * opacity * coverage / 255, blendMode);

                    void Accumulate(int column, int row, double weight)
                    {
                        double a = softMask is null ? 255 : softMask.Samples[row * sourceWidth + column];
                        double contribution = weight * a;
                        int offset = (row * sourceWidth + column) * components;
                        alpha += contribution;
                        red += contribution * samples[offset];
                        green += contribution * samples[offset + (components == 1 ? 0 : 1)];
                        blue += contribution * samples[offset + (components == 1 ? 0 : 2)];
                    }
                }
            }
        }, null, cancellationToken);
        return true;
    }
}
