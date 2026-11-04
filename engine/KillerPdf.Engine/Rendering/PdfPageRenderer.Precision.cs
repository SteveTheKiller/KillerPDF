using KillerPdf.Engine.Objects;
using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>Renders a page into relative linear-sRGB float32 RGBA pixels.</summary>
    /// <remarks>
    /// Supports device RGB/Gray and calibrated Gray path fills, untagged CMYK path fills and strokes
    /// through the bundled perceptual source viewing profile, variable-width strokes,
    /// dashed and closed strokes, combined fill/stroke operations with either fill rule, opacity
    /// and aligned natural-size unmasked 8/16-bit images or axis-aligned enlargements
    /// with nearest or bilinear sampling. Bilinear device samples require Decode values in [0,1].
    /// Integer-factor reductions in both axes average encoded device samples with Decode in [0,1].
    /// Raw color-key masks are supported with natural-size or eligible nearest-sampled images.
    /// Matching DeviceGray 8/16-bit image soft masks retain source alpha precision on those images.
    /// Their finite device-range Matte values are removed before color conversion and compositing.
    /// Unmasked RGB/Gray ICC matrix/TRC images support relative colorimetric PCS conversion at natural size,
    /// aligned enlargement or integer-factor reduction in both axes, averaging encoded source samples before ICC conversion.
    /// Unmasked CMYK ICC LUT images with an actual colorimetric forward table support
    /// relative colorimetric conversion at aligned natural size or eligible nearest enlargement,
    /// including DeviceCMYK images mapped through a supported resource DefaultCMYK profile.
    /// Untagged CMYK 8/16-bit images use the bundled perceptual source viewing profile
    /// at aligned natural size or eligible axis-aligned nearest or bilinear enlargement.
    /// Integer-factor reduction in both axes averages encoded source samples before conversion.
    /// Natural or nearest sampling also supports matching DeviceGray 8/16-bit soft masks
    /// with retained fractional alpha, or raw color-key masks.
    /// Supported CMYK Matte recovery removes the finite device-range matte before profile conversion.
    /// Device Gray/RGB axial and radial shadings
    /// support a single type 2 function. Embedded TrueType text fills and strokes
    /// support text matrices, line positioning, spacing, scaling, rise,
    /// strings, positioned text arrays and quote text-showing operators.
    /// Ordinary Form XObjects retain supported paint precision through their matrix and bounding-box clip.
    /// Eligible isolated and non-isolated non-knockout DeviceRGB Form groups support outer Normal
    /// compositing. Isolated groups also support outer Multiply. Internal Normal or Multiply paints
    /// retain precision with rectangular clips and no effective group profile. Active nested group pixel arrays
    /// share the render storage budget. Other group semantics remain explicitly unsupported.
    /// Unported paint operations are rejected instead of returning quantized pixels.
    /// </remarks>
    public PdfRenderedFrame RenderFrame(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken = default) =>
        RenderPreciseFrame(pageIndex, options, null, cancellationToken);

    private PdfRenderedFrame RenderPreciseFrame(int pageIndex, PdfRenderOptions options,
        (int Left, int Top, int Right, int Bottom)? region, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        cancellationToken.ThrowIfCancellationRequested();
        // During migration, encoded working pixels, the compatibility surface and the owned
        // returned copy coexist. Check their combined storage before allocating any of them.
        int width = region is { } bounds ? bounds.Right - bounds.Left : options.Width;
        int height = region is { } verticalBounds ? verticalBounds.Bottom - verticalBounds.Top : options.Height;
        long pixelCount = (long)width * height;
        if (pixelCount > options.PixelByteLimit / (4 + 2 * 4 * sizeof(float)))
            throw new ArgumentException("The high-precision render exceeds its combined pixel storage limit.", nameof(options));
        var page = _tree.Pages[pageIndex].Dictionary;
        if (options.SeparationPlate is not null || page.ContainsKey(Name("Group"))
            || OutputProfile(null) is not null)
            throw new NotSupportedException("High-precision profile and group rendering is not implemented.");
        if ((options.IncludeAnnotations || options.IncludeFormFields)
            && page.TryGetValue(Name("Annots"), out PdfObject? annotationValue)
            && Resolve(annotationValue) is PdfArray annotations)
        {
            foreach (PdfObject value in annotations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Resolve(value) is not PdfDictionary annotation) continue;
                bool widget = IsName(annotation, "Subtype", "Widget");
                bool included = widget
                    ? options.IncludeFormFields
                        || options.IncludeSignedSignatureFields && IsSignedSignatureWidget(annotation)
                        || options.IncludePushButtonFields && IsPushButtonWidget(annotation)
                    : options.IncludeAnnotations;
                if (included)
                    throw new NotSupportedException("High-precision annotation and form rendering is not implemented.");
            }
        }
        var pixels = new float[checked((int)pixelCount * 4)];
        PdfRenderedPage compatibility = RenderUncached(pageIndex, options, cancellationToken,
            region: region, preciseRgb: pixels);
        if (compatibility.Diagnostics.Count != 0)
        {
            string diagnostic = compatibility.Diagnostics[0];
            throw new NotSupportedException("The page contains rendering features unavailable to the high-precision path. "
                + diagnostic[..Math.Min(diagnostic.Length, 512)]);
        }
        FloatRgbCompositor.DecodeSrgbInPlace(pixels, cancellationToken);
        return new PdfRenderedFrame(width, height, pixels,
            PdfFrameColorEncoding.LinearSrgbD65, options.PixelByteLimit);
    }

    /// <summary>Renders a pixel region into an owned relative linear-sRGB float32 RGBA frame.</summary>
    /// <remarks>Coordinates use the full page raster. Precision support matches <see cref="RenderFrame"/>.</remarks>
    public PdfRenderedFrame RenderFrameRegion(int pageIndex, PdfRenderOptions options,
        int left, int top, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (left < 0 || top < 0 || width <= 0 || height <= 0
            || (long)left + width > options.Width || (long)top + height > options.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "The region must fit inside the page raster.");
        return RenderPreciseFrame(pageIndex, options, (left, top, left + width, top + height), cancellationToken);
    }

    private static bool PreciseOperationSupported(string operation) => operation is
        "q" or "Q" or "cm" or "rg" or "RG" or "g" or "G" or "k" or "K" or "gs" or "w" or "d"
        or "cs" or "CS" or "sc" or "SC" or "scn" or "SCN"
        or "m" or "l" or "c" or "v" or "y" or "h" or "re"
        or "f" or "F" or "f*" or "S" or "s" or "B" or "B*" or "b" or "b*"
        or "n" or "W" or "W*" or "Do" or "sh"
        or "BT" or "ET" or "Tf" or "Tm" or "Td" or "TD" or "T*" or "TL" or "Tc" or "Tw" or "Tz" or "Ts"
        or "Tj" or "TJ" or "Tr" or "'" or "\"";

    private static readonly Lazy<PdfColorProfile> PreciseCmykViewingProfile = new(PdfCmykViewingDefault.LoadProfile);

    private static Color PreciseCmykViewingColor(double cyan, double magenta, double yellow, double black)
    {
        Span<double> device = [cyan, magenta, yellow, black];
        Span<double> xyz = stackalloc double[3];
        PreciseCmykViewingProfile.Value.ToXyz(device, xyz, PdfCmykViewingDefault.EffectiveIntent);
        return IccXyzToDisplay(xyz[0], xyz[1], xyz[2]) with { Connection = (xyz[0], xyz[1], xyz[2]) };
    }

    private static void PaintPrecisePixel(RasterSurface surface, int offset, in Color color,
        double opacity, RendererBlendMode blendMode, GraphicsSoftMask? softMask,
        KnockoutState? knockout)
    {
        if (blendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible or RendererBlendMode.Multiply)
            || softMask is not null || knockout is not null || surface.HasProfile
            || surface.GroupAlpha is not null || surface.GroupShape is not null)
            throw new NotSupportedException("This high-precision compositing operation is not implemented.");
        var source = color.PreciseRgb ?? (color.Connection is { } connection
            ? PreciseConnectionToRgb(connection.X, connection.Y, connection.Z)
            : throw new NotSupportedException("The paint does not retain its source color precision."));
        if (blendMode == RendererBlendMode.Multiply)
            FloatRgbCompositor.Multiply(surface.PreciseRgb!, offset,
                source.Red, source.Green, source.Blue, opacity);
        else
            FloatRgbCompositor.SourceOver(surface.PreciseRgb!, offset,
                source.Red, source.Green, source.Blue, opacity);
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
        bool smooth, int reductionColumns, int reductionRows, int[]? colorKeyMask, SoftMask? softMask,
        double[]? matte, CancellationToken cancellationToken)
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
                if (colorKeyMask is not null && converter.MatchesColorKey(sampleX, sampleY, colorKeyMask))
                    continue;
                double maskAlpha = softMask?.SamplePrecise(sampleX, sampleY) ?? 1;
                if (maskAlpha == 0) continue;
                Color color = matte is not null
                    ? converter.ConvertPreciseMatte(sampleX, sampleY, matte, maskAlpha)
                    : reductionColumns > 1 && reductionRows > 1
                    ? converter.ConvertPreciseArea(unit.X * sourceWidth, (1 - unit.Y) * sourceHeight,
                        sourceWidth, sourceHeight, reductionColumns, reductionRows, cancellationToken)
                    : smooth
                    ? converter.ConvertPreciseBilinear(unit.X * sourceWidth - 0.5,
                        (1 - unit.Y) * sourceHeight - 0.5, sourceWidth, sourceHeight)
                    : converter.ConvertPrecise(sampleX, sampleY);
                SetPixel(target, targetWidth, x, y, color, opacity * maskAlpha * ClipAlpha(clips, x, y), blendMode);
            }
        }
    }
}
