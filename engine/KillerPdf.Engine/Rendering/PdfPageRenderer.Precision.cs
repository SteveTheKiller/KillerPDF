using KillerPdf.Engine.Objects;
using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    /// <summary>Renders a page into relative linear-sRGB float32 RGBA pixels.</summary>
    /// <remarks>
    /// Supports device RGB/Gray and calibrated Gray path fills, untagged CMYK path fills and strokes
    /// through <see cref="PdfRenderOptions.WorkingCmykProfile"/>, the bundled perceptual source viewing
    /// profile by default, which the frame reports when untagged CMYK was painted, variable-width strokes,
    /// dashed and closed strokes, combined fill/stroke operations with either fill rule, opacity
    /// and aligned natural-size unmasked 8/16-bit images or axis-aligned enlargements
    /// with nearest or bilinear sampling. Bilinear device samples require Decode values in [0,1].
    /// Unmasked ordinary DeviceRGB/Gray natural-size images and axis-aligned enlargements
    /// support fractional edges with pixel-area coverage, including reflected images.
    /// Integer-factor reductions in both axes average encoded device samples with Decode in [0,1].
    /// Raw color-key masks are supported with natural-size or eligible nearest-sampled images.
    /// Matching DeviceGray 8/16-bit image soft masks retain source alpha precision on those images.
    /// Ordinary DeviceRGB/Gray natural-size or eligible nearest-enlarged soft-masked images
    /// also multiply source alpha by fractional rectangular pixel-area coverage.
    /// Their finite device-range Matte values are removed before color conversion and compositing.
    /// Direct zero-black 8/16-bit CalGray images retain calibration at aligned natural size or axis-aligned enlargement,
    /// including diagonal reflections and rectangular clips, with default Decode and relative intent.
    /// Unmasked CalGray natural-size images and enlargements also support fractional pixel-area coverage.
    /// Natural-size CalGray also supports reversed Decode [1,0].
    /// Unmasked bilinear enlargement interpolates source samples before calibration.
    /// Raw color-key masks and matching DeviceGray 8/16-bit soft masks retain source alpha with natural or nearest sampling.
    /// Natural-size CalGray soft-mask Matte retains source precision before calibration. Enlarged Matte,
    /// reductions, masked bilinear sampling, default substitutions, enlarged reversed Decode,
    /// and other decode or black-point requests remain unsupported.
    /// Unmasked RGB/Gray ICC matrix/TRC images support relative colorimetric PCS conversion at natural size,
    /// aligned enlargement or integer-factor reduction in both axes, averaging encoded source samples before ICC conversion.
    /// Supported resource DefaultRGB and DefaultGray profiles use the same image conversion.
    /// Direct matrix RGB/Gray ICC images at aligned natural size support matching DeviceGray 8/16-bit
    /// soft masks with precise alpha, default Decode, and relative intent, without Matte.
    /// Unmasked CMYK ICC LUT images with an actual colorimetric forward table support
    /// relative colorimetric conversion at aligned natural size or eligible nearest enlargement,
    /// including DeviceCMYK images mapped through a supported resource DefaultCMYK profile.
    /// Untagged CMYK 8/16-bit images use the same working CMYK profile
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
    /// Annotation appearances, including saved widget appearances, use the same supported paint path,
    /// including region rendering, visibility filtering and printer proof conversion.
    /// Requested form-field regeneration remains unsupported.
    /// Explicit neutral graphics-state defaults, including a None soft-mask reset, retain precision.
    /// Rendering-intent operators update graphics state while each paint retains its profile intent limits.
    /// Eligible isolated and non-isolated non-knockout DeviceRGB Form groups support outer Normal
    /// or Multiply or Screen compositing, with independent source alpha when needed. Internal Normal, Multiply or Screen paints
    /// retain precision with rectangular clips and no effective group profile. Active nested group pixel arrays
    /// share the render storage budget. Other group semantics remain explicitly unsupported.
    /// Unprofiled non-knockout DeviceRGB page groups retain precision through final imposition
    /// on white for opaque output; transparent output preserves their alpha.
    /// Initial printer proof rendering accepts pages without explicit groups and unprofiled DeviceRGB page groups with direct
    /// DeviceRGB/Gray paths, ordinary Form XObjects containing supported paints,
    /// supported direct and ICC RGB/Gray images and embedded TrueType text,
    /// including supported DefaultGray calibration,
    /// all four intents with an actual selected inverse,
    /// optional source BPC and independent paper/black simulation,
    /// with separate gamut-warning masks. Paper-only viewing preserves paper tint while mapping estimated printer black to reference zero.
    /// Compensation uses bounded ICC estimates against reference zero black.
    /// It uses actual colorimetric printer transforms and preserves extended reference RGB output.
    /// Transparent output retains straight color and alpha; zero-alpha pixels are not gamut-evaluated.
    /// Intent fallback is diagnosed, and absolute source conversion requires explicit valid media white.
    /// Other proof requests are diagnosed; original device ink preservation requires a source-aware path.
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
        if (options.Proof is { Enabled: true } requested
            && (options.ForPrinting
                || requested.Options.RenderingIntent is not (Authoring.PdfRenderingIntent.RelativeColorimetric
                    or Authoring.PdfRenderingIntent.Perceptual or Authoring.PdfRenderingIntent.Saturation
                    or Authoring.PdfRenderingIntent.AbsoluteColorimetric)))
            throw new NotSupportedException("The requested printer proof controls are not implemented for this render path.");
        // During migration, encoded working pixels, the compatibility surface and the owned
        // returned copy coexist. Check their combined storage before allocating any of them.
        int width = region is { } bounds ? bounds.Right - bounds.Left : options.Width;
        int height = region is { } verticalBounds ? verticalBounds.Bottom - verticalBounds.Top : options.Height;
        long pixelCount = (long)width * height;
        int pixelStorage = 4 + 2 * 4 * sizeof(float)
            + (options.Proof is { Enabled: true, GamutWarnings: true } ? 2 : 0);
        if (pixelCount > options.PixelByteLimit / pixelStorage)
            throw new ArgumentException("The high-precision render exceeds its combined pixel storage limit.", nameof(options));
        var page = _tree.Pages[pageIndex].Dictionary;
        if (options.Proof is { Enabled: true } proofRequest)
        {
            if (proofRequest.PrinterProfile is not { ProfileClassSignature: "prtr",
                HasColorimetricForward: true, HasColorimetricReverse: true })
                throw new NotSupportedException("Printer proof rendering requires an output profile with actual colorimetric transforms in both directions.");
            if (proofRequest.Options.RenderingIntent == Authoring.PdfRenderingIntent.AbsoluteColorimetric
                && !proofRequest.PrinterProfile.HasExplicitValidMediaWhite)
                throw new NotSupportedException("Absolute printer proof conversion requires explicit valid media-white data.");
            var inverse = proofRequest.PrinterProfile.GetTransformSelection(proofRequest.Options.RenderingIntent,
                PdfColorTransformDirection.ConnectionToDevice);
            if (!inverse.IsAvailable || inverse.UsesIntentFallback)
                throw new NotSupportedException("Printer proof rendering requires an actual inverse for the selected intent without fallback.");
        }
        if (options.SeparationPlate is not null)
            throw new NotSupportedException("High-precision profile and group rendering is not implemented.");
        if (page.TryGetValue(Name("Group"), out PdfObject? groupValue)
            && (Resolve(groupValue) is not PdfDictionary group
                || !IsName(group, "S", "Transparency") || !IsName(group, "CS", "DeviceRGB")
                || ReadColorSpace(Name("DeviceRGB"), _pageResources[pageIndex], 0).IsDefault
                || group.TryGetValue(Name("K"), out PdfObject? knockoutValue)
                    && Resolve(knockoutValue) is PdfBoolean { Value: true }))
            throw new NotSupportedException("This high-precision page group is not implemented.");
        var pixels = new float[checked((int)pixelCount * 4)];
        var workingCmyk = new PreciseCmykWorkingSpace(options.WorkingCmykProfile);
        PdfRenderedPage compatibility = RenderUncached(pageIndex, options, cancellationToken,
            region: region, preciseRgb: pixels, preciseCmyk: workingCmyk);
        HashSet<string> recovery = DocumentRecoveryDiagnostics(pageIndex);
        var frameDiagnostics = new List<string>();
        foreach (string diagnostic in compatibility.Diagnostics)
        {
            if (recovery.Contains(diagnostic))
            {
                frameDiagnostics.Add(diagnostic);
                continue;
            }
            throw new NotSupportedException("The page contains rendering features unavailable to the high-precision path. "
                + diagnostic[..Math.Min(diagnostic.Length, 512)]);
        }
        string? assumedCmyk = workingCmyk.Used ? workingCmyk.ContentSha256 : null;
        if (assumedCmyk is not null)
            frameDiagnostics.Add(assumedCmyk == PdfCmykViewingDefault.SharedProfile.ContentSha256
                ? $"Untagged DeviceCMYK was converted through the bundled {PdfCmykViewingDefault.ProfileName} working CMYK profile (SHA256 {assumedCmyk})."
                : $"Untagged DeviceCMYK was converted through the configured working CMYK profile (SHA256 {assumedCmyk}).");
        PdfGamutWarningMask? gamutWarnings = null;
        if (options.Proof is { Enabled: true } proof)
            gamutWarnings = ApplyPreciseProof(pixels, width, height, proof.PrinterProfile
                ?? throw new NotSupportedException("The requested printer proof profile is unavailable."),
                proof.Options, proof.GamutWarnings, cancellationToken);
        FloatRgbCompositor.DecodeSrgbInPlace(pixels, cancellationToken);
        return new PdfRenderedFrame(width, height, pixels,
            PdfFrameColorEncoding.LinearSrgbD65, options.PixelByteLimit, gamutWarnings)
        {
            Diagnostics = frameDiagnostics,
            AssumedCmykProfileSha256 = assumedCmyk
        };
    }

    private static PdfGamutWarningMask? ApplyPreciseProof(float[] pixels, int width, int height,
        PdfColorProfile printer, PdfProofOptions options, bool gamutWarnings, CancellationToken cancellationToken)
    {
        Span<double> paperBlack = stackalloc double[3];
        Span<double> paperScale = stackalloc double[3];
        if (options.SimulatePaper)
        {
            if (!printer.HasExplicitValidMediaWhite)
                throw new NotSupportedException("Printer proof paper simulation requires explicit valid media-white data.");
            Span<double> paper = stackalloc double[3];
            printer.ReadPaperWhite(paper);
            if (!options.SimulateBlack)
            {
                printer.ReadPaperBlack(paperBlack);
                for (int channel = 0; channel < 3; channel++)
                {
                    double range = paper[channel] - paperBlack[channel];
                    if (!double.IsFinite(range) || range <= 1e-9 || !double.IsFinite(paper[channel]) || paper[channel] <= 0)
                        throw new NotSupportedException("Printer proof paper-only viewing requires distinct finite paper and black endpoints.");
                    paperScale[channel] = paper[channel] / range;
                }
            }
        }
        PdfBlackPointCompensation sourceCompensation = options.BlackPointCompensation
            && options.RenderingIntent != Authoring.PdfRenderingIntent.AbsoluteColorimetric
            ? printer.ReferenceBlackToDeviceCompensation(options.RenderingIntent) : new(1);
        PdfBlackPointCompensation viewingCompensation = options.SimulateBlack || options.SimulatePaper ? new(1)
            : printer.DeviceToReferenceBlackCompensation(Authoring.PdfRenderingIntent.RelativeColorimetric);
        Span<double> xyz = stackalloc double[3];
        Span<double> device = stackalloc double[printer.ComponentCount];
        var gamutCheck = gamutWarnings ? new PdfPrinterGamutCheck(printer) : null;
        byte[]? classifications = gamutWarnings ? new byte[pixels.Length / 4] : null;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (pixels[offset + 3] == 0)
            {
                if (classifications is not null)
                    classifications[offset / 4] = (byte)PdfGamutClassification.NotEvaluated;
                continue;
            }
            double r = Linear(pixels[offset]), g = Linear(pixels[offset + 1]), b = Linear(pixels[offset + 2]);
            xyz[0] = .4360747 * r + .3850649 * g + .1430804 * b;
            xyz[1] = .2225045 * r + .7168786 * g + .0606169 * b;
            xyz[2] = .0139322 * r + .0971045 * g + .7141733 * b;
            if (gamutCheck is not null)
                classifications![offset / 4] = (byte)gamutCheck.EvaluateXyz(xyz).Classification;
            sourceCompensation.Apply(xyz);
            printer.FromXyz(xyz, device, options.RenderingIntent);
            printer.ToXyz(device, xyz, options.SimulatePaper
                ? Authoring.PdfRenderingIntent.AbsoluteColorimetric : Authoring.PdfRenderingIntent.RelativeColorimetric);
            if (options.SimulatePaper && !options.SimulateBlack)
                for (int channel = 0; channel < 3; channel++)
                    xyz[channel] = (xyz[channel] - paperBlack[channel]) * paperScale[channel];
            viewingCompensation.Apply(xyz);
            var color = PreciseConnectionToRgb(xyz[0], xyz[1], xyz[2]);
            pixels[offset] = (float)color.Red;
            pixels[offset + 1] = (float)color.Green;
            pixels[offset + 2] = (float)color.Blue;
        }
        return classifications is null ? null : new PdfGamutWarningMask(width, height, classifications, printer.ContentSha256);

        static double Linear(double value) => value <= .04045
            ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
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

    private bool PreciseProofImageSupported(PdfStream image, PdfDictionary resources)
    {
        if (!IsName(image.Dictionary, "Subtype", "Image")
            || image.Dictionary.TryGetValue(Name("ImageMask"), out PdfObject? mask)
                && Resolve(mask) is PdfBoolean { Value: true }) return false;
        if (IsName(image.Dictionary, "ColorSpace", "DeviceRGB")
            || IsName(image.Dictionary, "ColorSpace", "DeviceGray")) return true;
        if (!image.Dictionary.TryGetValue(Name("ColorSpace"), out PdfObject? value)) return false;
        bool defaultCmyk = Resolve(value) is PdfName device && device.ValueAsLatin1() == "DeviceCMYK";
        if (defaultCmyk
            && (!resources.TryGetValue(Name("ColorSpace"), out PdfObject? defaultsValue)
                || Resolve(defaultsValue) is not PdfDictionary defaults
                || !defaults.TryGetValue(Name("DefaultCMYK"), out value))) return false;
        if (Resolve(value) is not PdfArray { Count: 2 } space
            || Resolve(space[0]) is not PdfName name || name.ValueAsLatin1() != "ICCBased"
            || Resolve(space[1]) is not PdfStream profile
            || !profile.Dictionary.TryGetValue(Name("N"), out PdfObject? channels)) return false;
        // The existing precise image path validates profile transforms and sampling.
        return Resolve(channels) is PdfInteger { Value: 1 or 3 or 4 } count
            && (!defaultCmyk || count.Value == 4);
    }

    private static bool PreciseProofOperationSupported(string operation) => operation is
        "q" or "Q" or "cm" or "rg" or "RG" or "g" or "G" or "gs" or "w" or "d"
        or "m" or "l" or "c" or "v" or "y" or "h" or "re"
        or "f" or "F" or "f*" or "S" or "s" or "B" or "B*" or "b" or "b*"
        or "n" or "W" or "W*"
        or "BT" or "ET" or "Tf" or "Tm" or "Tj" or "TJ" or "Tr" or "Tc" or "Tw" or "Tz" or "Ts"
        or "Td" or "TD" or "T*" or "TL" or "'" or "\"" or "Do";

    private static bool PreciseOperationSupported(string operation) => operation is
        "q" or "Q" or "cm" or "rg" or "RG" or "g" or "G" or "k" or "K" or "gs" or "w" or "d"
        or "BMC" or "BDC" or "EMC" or "ri"
        or "cs" or "CS" or "sc" or "SC" or "scn" or "SCN"
        or "m" or "l" or "c" or "v" or "y" or "h" or "re"
        or "f" or "F" or "f*" or "S" or "s" or "B" or "B*" or "b" or "b*"
        or "n" or "W" or "W*" or "Do" or "sh"
        or "BT" or "ET" or "Tf" or "Tm" or "Td" or "TD" or "T*" or "TL" or "Tc" or "Tw" or "Tz" or "Ts"
        or "Tj" or "TJ" or "Tr" or "'" or "\"";

    private bool PreciseGraphicsStateSupported(PdfDictionary settings)
    {
        foreach (var entry in settings)
        {
            string name = entry.Key.ValueAsLatin1();
            if (name is "Type" or "ca" or "CA" or "BM") continue;
            PdfObject value = Resolve(entry.Value);
            bool neutral = name switch
            {
                "op" or "OP" or "AIS" => value is PdfBoolean { Value: false },
                "TK" => value is PdfBoolean { Value: true },
                "OPM" => value is PdfInteger { Value: 0 },
                "SMask" => value is PdfName mask && mask.ValueAsLatin1() == "None",
                _ => false
            };
            if (!neutral) return false;
        }
        return true;
    }

    // The working CMYK profile one float render assumes for untagged DeviceCMYK. Surfaces of the
    // same render share it, and it records whether any untagged CMYK color was converted.
    private sealed class PreciseCmykWorkingSpace
    {
        private readonly PdfColorProfile _profile;
        private readonly Authoring.PdfRenderingIntent _intent;
        private volatile bool _used;

        internal PreciseCmykWorkingSpace(PdfColorProfile profile)
        {
            _profile = profile;
            _intent = profile.WorkingCmykForwardIntent
                ?? throw new NotSupportedException("The working CMYK profile has no usable forward transform.");
            Convert = ConvertColor;
        }

        internal Func<double, double, double, double, Color> Convert { get; }
        internal bool Used => _used;
        internal string ContentSha256 => _profile.ContentSha256;

        private Color ConvertColor(double cyan, double magenta, double yellow, double black)
        {
            _used = true;
            Span<double> device = [cyan, magenta, yellow, black];
            Span<double> xyz = stackalloc double[3];
            _profile.ToXyz(device, xyz, _intent);
            return IccXyzToDisplay(xyz[0], xyz[1], xyz[2]) with { Connection = (xyz[0], xyz[1], xyz[2]) };
        }
    }

    private static void PaintPrecisePixel(RasterSurface surface, int offset, in Color color,
        double opacity, RendererBlendMode blendMode, GraphicsSoftMask? softMask,
        KnockoutState? knockout)
    {
        if (blendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible or RendererBlendMode.Multiply or RendererBlendMode.Screen)
            || softMask is not null || knockout is not null || surface.HasProfile
            || surface.GroupAlpha is not null || surface.GroupShape is not null
            || color.SpotName is not null || color.AdditionalSpot is not null)
            throw new NotSupportedException("This high-precision compositing operation is not implemented.");
        var source = color.PreciseRgb ?? (color.Connection is { } connection
            ? PreciseConnectionToRgb(connection.X, connection.Y, connection.Z)
            : throw new NotSupportedException("The paint does not retain its source color precision."));
        if (blendMode == RendererBlendMode.Multiply)
            FloatRgbCompositor.Multiply(surface.PreciseRgb!, offset,
                source.Red, source.Green, source.Blue, opacity);
        else if (blendMode == RendererBlendMode.Screen)
            FloatRgbCompositor.Screen(surface.PreciseRgb!, offset,
                source.Red, source.Green, source.Blue, opacity);
        else
            FloatRgbCompositor.SourceOver(surface.PreciseRgb!, offset,
                source.Red, source.Green, source.Blue, opacity);
        AccumulatePreciseGroupAlpha(surface, offset, opacity);
    }

    private static void AccumulatePreciseGroupAlpha(RasterSurface surface, int offset, double opacity)
    {
        if (surface.PreciseGroupAlpha is not { } alpha) return;
        double source = Math.Clamp(opacity, 0, 1);
        int pixel = offset / 4;
        alpha[pixel] = (float)(source + alpha[pixel] * (1 - source));
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
        double[]? matte, bool fractionalImageEdges, double imageLeft, double imageRight,
        double imageTop, double imageBottom, CancellationToken cancellationToken)
    {
        var converter = new ImageSampleConverter(samples, rowBytes, components, bits, decode,
            colorSpace, targetInk: false, targetProfile: null);
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = left; x < right; x++)
            {
                double coverage = fractionalImageEdges
                    ? Math.Max(0, Math.Min(x + 1, imageRight) - Math.Max(x, imageLeft))
                        * Math.Max(0, Math.Min(y + 1, imageBottom) - Math.Max(y, imageTop))
                    : 1;
                if (coverage == 0) continue;
                Point unit = inverse.Apply((x + 0.5) / scaleX, (targetHeight - y - 0.5) / scaleY);
                if (fractionalImageEdges)
                    unit = new Point(Math.Clamp(unit.X, 0, Math.BitDecrement(1d)),
                        Math.Clamp(unit.Y, 0, Math.BitDecrement(1d)));
                else if (unit.X < 0 || unit.X >= 1 || unit.Y < 0 || unit.Y >= 1) continue;
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
                SetPixel(target, targetWidth, x, y, color, opacity * maskAlpha * coverage * ClipAlpha(clips, x, y), blendMode);
            }
        }
    }
}
