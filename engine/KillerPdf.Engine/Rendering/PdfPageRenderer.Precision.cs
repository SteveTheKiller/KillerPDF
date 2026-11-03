namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>Renders a page into relative linear-sRGB float32 RGBA pixels.</summary>
    /// <remarks>
    /// Supports device RGB/Gray path fills, opacity and aligned natural-size unmasked 8/16-bit images.
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
            || _pageResources[pageIndex].ContainsKey(Name("ColorSpace"))
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
        or "m" or "l" or "c" or "v" or "y" or "h" or "re"
        or "f" or "F" or "f*" or "n" or "W" or "W*" or "Do";

    private static void PaintPrecisePixel(RasterSurface surface, int offset, in Color color,
        double opacity, RendererBlendMode blendMode, GraphicsSoftMask? softMask,
        KnockoutState? knockout)
    {
        if (blendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible)
            || softMask is not null || knockout is not null || surface.HasProfile
            || surface.GroupAlpha is not null || surface.GroupShape is not null)
            throw new NotSupportedException("This high-precision compositing operation is not implemented.");
        if (color.PreciseRgb is not { } source)
            throw new NotSupportedException("The paint does not retain its source color precision.");
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
