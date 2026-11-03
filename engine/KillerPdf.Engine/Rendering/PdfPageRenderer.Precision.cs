namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>Renders a page into relative linear-sRGB float32 RGBA pixels.</summary>
    /// <remarks>
    /// Supports device RGB/Gray and calibrated Gray path fills, default-width strokes,
    /// opacity and aligned natural-size unmasked 8/16-bit images.
    /// Unported paint operations are rejected instead of returning quantized pixels.
    /// </remarks>
    public PdfRenderedFrame RenderFrame(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        cancellationToken.ThrowIfCancellationRequested();
        // During migration, encoded working pixels, the compatibility surface and the owned
        // returned copy coexist. Check their combined storage before allocating any of them.
        long pixelCount = (long)options.Width * options.Height;
        if (pixelCount > options.PixelByteLimit / (4 + 2 * 4 * sizeof(float)))
            throw new ArgumentException("The high-precision render exceeds its combined pixel storage limit.", nameof(options));
        var page = _tree.Pages[pageIndex].Dictionary;
        if (options.SeparationPlate is not null || page.ContainsKey(Name("Group"))
            || OutputProfile(null) is not null)
            throw new NotSupportedException("High-precision profile and group rendering is not implemented.");
        if ((options.IncludeAnnotations || options.IncludeFormFields)
            && page.ContainsKey(Name("Annots")))
            throw new NotSupportedException("High-precision annotation and form rendering is not implemented.");
        var pixels = new float[checked((int)pixelCount * 4)];
        PdfRenderedPage compatibility = RenderUncached(pageIndex, options, cancellationToken,
            preciseRgb: pixels);
        if (compatibility.Diagnostics.Count != 0)
            throw new NotSupportedException("The page contains rendering features unavailable to the high-precision path.");
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int channel = 0; channel < 3; channel++)
                pixels[offset + channel] = (float)DecodeSrgb(pixels[offset + channel]);
        }
        return new PdfRenderedFrame(options.Width, options.Height, pixels,
            PdfFrameColorEncoding.LinearSrgbD65, options.PixelByteLimit);
    }

    private static double DecodeSrgb(double value) => value <= 0.04045
        ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static bool PreciseOperationSupported(string operation) => operation is
        "q" or "Q" or "cm" or "rg" or "RG" or "g" or "G" or "gs"
        or "cs" or "CS" or "sc" or "SC" or "scn" or "SCN"
        or "m" or "l" or "c" or "v" or "y" or "h" or "re"
        or "f" or "F" or "f*" or "S" or "n" or "W" or "W*" or "Do";

    private static void PaintPrecisePixel(RasterSurface surface, int offset, in Color color,
        double opacity, RendererBlendMode blendMode, GraphicsSoftMask? softMask,
        KnockoutState? knockout)
    {
        if (blendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible)
            || softMask is not null || knockout is not null || surface.HasProfile
            || surface.GroupAlpha is not null || surface.GroupShape is not null)
            throw new NotSupportedException("This high-precision compositing operation is not implemented.");
        var source = color.PreciseRgb ?? (color.Connection is { } connection
            ? PreciseConnectionToRgb(connection.X, connection.Y, connection.Z)
            : throw new NotSupportedException("The paint does not retain its source color precision."));
        float[] pixels = surface.PreciseRgb!;
        double sourceAlpha = Math.Clamp(opacity, 0, 1);
        double targetWeight = pixels[offset + 3] * (1 - sourceAlpha);
        double outputAlpha = sourceAlpha + targetWeight;
        if (outputAlpha == 0) return;
        pixels[offset] = (float)((source.Red * sourceAlpha + pixels[offset] * targetWeight) / outputAlpha);
        pixels[offset + 1] = (float)((source.Green * sourceAlpha + pixels[offset + 1] * targetWeight) / outputAlpha);
        pixels[offset + 2] = (float)((source.Blue * sourceAlpha + pixels[offset + 2] * targetWeight) / outputAlpha);
        pixels[offset + 3] = (float)outputAlpha;
    }

    private static (double Red, double Green, double Blue) PreciseConnectionToRgb(double x, double y, double z)
    {
        // Bradford adaptation from the ICC D50 PCS to the frame's D65 sRGB primaries.
        double l = (0.8951 * x + 0.2664 * y - 0.1614 * z)
            * (0.8951 * 0.95047 + 0.2664 - 0.1614 * 1.08883)
            / (0.8951 * 0.9642 + 0.2664 - 0.1614 * 0.8249);
        double m = (-0.7502 * x + 1.7135 * y + 0.0367 * z)
            * (-0.7502 * 0.95047 + 1.7135 + 0.0367 * 1.08883)
            / (-0.7502 * 0.9642 + 1.7135 + 0.0367 * 0.8249);
        double s = (0.0389 * x - 0.0685 * y + 1.0296 * z)
            * (0.0389 * 0.95047 - 0.0685 + 1.0296 * 1.08883)
            / (0.0389 * 0.9642 - 0.0685 + 1.0296 * 0.8249);
        double adaptedX = 0.9869929 * l - 0.1470543 * m + 0.1599627 * s;
        double adaptedY = 0.4323053 * l + 0.5183603 * m + 0.0492912 * s;
        double adaptedZ = -0.0085287 * l + 0.0400428 * m + 0.9684867 * s;
        return (Encode(3.2404542 * adaptedX - 1.5371385 * adaptedY - 0.4985314 * adaptedZ),
            Encode(-0.969266 * adaptedX + 1.8760108 * adaptedY + 0.041556 * adaptedZ),
            Encode(0.0556434 * adaptedX - 0.2040259 * adaptedY + 1.0572252 * adaptedZ));

        static double Encode(double value) => value <= 0.0031308
            ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
    }

    private static void PaintPreciseCoverage(RasterSurface pixels, int width, CoverageMask mask,
        in Color color, double alpha, RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        int left, int top, int right, int bottom, bool perPixelClip, CancellationToken cancellationToken)
    {
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = left; x < right; x++)
            {
                double coverage = mask.Coverage is null ? 1
                    : mask.Coverage[mask.RowOffset(y) + x - mask.Left] / 255d;
                if (perPixelClip) coverage *= ClipAlpha(clips, x, y);
                if (coverage == 0) continue;
                SetPixel(pixels, width, x, y, color, alpha * coverage, blendMode,
                    graphicsSoftMask, knockout);
            }
        }
    }

    private static void PaintPreciseImage(RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, Matrix inverse, byte[] samples, int rowBytes,
        int sourceWidth, int sourceHeight, int components, int bits, double[] decode,
        ImageColorSpace colorSpace, double opacity, RendererBlendMode blendMode,
        IReadOnlyList<ClipRegion> clips, int left, int top, int right, int bottom,
        CancellationToken cancellationToken)
    {
        var converter = new ImageSampleConverter(samples, rowBytes, components, bits, decode,
            colorSpace, targetInk: false, targetProfile: null);
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = left; x < right; x++)
            {
                Point unit = inverse.Apply((x + 0.5) / scaleX, (targetHeight - y - 0.5) / scaleY);
                if (unit.X < 0 || unit.X >= 1 || unit.Y < 0 || unit.Y >= 1) continue;
                int sampleX = Math.Min((int)(unit.X * sourceWidth), sourceWidth - 1);
                int sampleY = Math.Min((int)((1 - unit.Y) * sourceHeight), sourceHeight - 1);
                Color color = converter.ConvertPrecise(sampleX, sampleY);
                SetPixel(target, targetWidth, x, y, color, opacity * ClipAlpha(clips, x, y), blendMode);
            }
        }
    }
}
