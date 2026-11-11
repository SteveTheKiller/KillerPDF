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
    /// Axis-aligned reductions, including fractional and mixed-axis scaling, average encoded device
    /// samples with Decode in [0,1] at aligned image edges. Packed one-bit images retain their
    /// binary samples on enlargement and average source coverage on reduction.
    /// Axis-aligned stencils retain precise coverage through reduction or bounded supersampling.
    /// Raw color-key masks are supported with natural-size or eligible nearest-sampled images.
    /// Matching DeviceGray 8/16-bit image soft masks retain source alpha precision on those images.
    /// Ordinary DeviceRGB/Gray natural-size or eligible nearest-enlarged soft-masked images
    /// also multiply source alpha by fractional rectangular pixel-area coverage.
    /// Their finite device-range Matte values are removed before color conversion and compositing.
    /// Direct device images also accept independent-size one-, eight- or sixteen-bit image masks
    /// with normalized coordinates and retained alpha precision. Reduced masked images retain the
    /// compatibility renderer's nearest source grid and interpolated mask sampling grid.
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
    /// aligned enlargement or axis-aligned reduction with aligned edges, including fractional and mixed-axis
    /// scaling, averaging encoded source samples before ICC conversion.
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
    /// Line cap, join and miter operators and parameters, stroke adjustment, flatness, marked points,
    /// an overprint mode while overprint is off, device-dependent parameters and unknown
    /// graphics-state keys draw as byte rendering does. A qualified page of unprofiled DeviceCMYK Normal
    /// path fills and strokes also retains native process plates for OPM0/1 overprint, opacity and printer proofing.
    /// Mixed sources, profiles, groups and other overprinted paints remain unsupported.
    /// Other overprint, alpha as shape, text knockout off,
    /// transfer functions, black-point compensation and graphics-state rendering intents are refused.
    /// Graphics-state alpha and luminosity soft masks, with backdrop and transfer function, take
    /// eight-bit mask values from the same mask group render as byte rendering; the masked paint,
    /// image or transparency group keeps its float color and composites in float. Mask samples and
    /// render tiles count against the frame storage budget while a graphics state can reach them.
    /// A page whose soft masks or transparency groups do not fit beside the frame in
    /// <see cref="PdfRenderOptions.PixelByteLimit"/> is refused with <see cref="NotSupportedException"/>,
    /// as other content the float path cannot draw is.
    /// Rendering-intent operators update graphics state while each paint retains its profile intent limits.
    /// Eligible isolated and non-isolated non-knockout DeviceRGB Form groups support outer Normal
    /// or Multiply or Screen compositing, with independent source alpha when needed. Internal Normal, Multiply or Screen paints
    /// retain precision with rectangular clips and no effective group profile. Active nested group pixel arrays
    /// share the render storage budget. Other group semantics remain explicitly unsupported.
    /// Unprofiled non-knockout DeviceRGB page groups retain precision through final imposition
    /// on white for opaque output; transparent output preserves their alpha.
    /// Initial printer proof rendering accepts pages without explicit groups and unprofiled DeviceRGB page groups with direct
    /// DeviceRGB/Gray paths, ordinary Form XObjects containing supported paints,
    /// supported direct, calibrated gray and ICC RGB/Gray images, marked content and embedded TrueType text,
    /// including supported DefaultGray calibration,
    /// all four intents with an actual selected inverse,
    /// optional source BPC and independent paper/black simulation,
    /// with separate gamut-warning masks. Paper-only viewing preserves paper tint while mapping estimated printer black to reference zero.
    /// Compensation uses bounded ICC estimates against reference zero black.
    /// It uses actual colorimetric printer transforms and preserves extended reference RGB output.
    /// Transparent output retains straight color and alpha; zero-alpha pixels are not gamut-evaluated.
    /// Intent fallback is diagnosed, and absolute source conversion requires explicit valid media white.
    /// Qualified process-only overprint pages retain their original inks until the composed source appearance
    /// is converted for proofing. Other proof requests are diagnosed.
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
        if (options.Proof is { Enabled: true } requested) PdfProofGate.Validate(requested, options.ForPrinting);
        // During migration, encoded working pixels, the compatibility surface and the owned
        // returned copy coexist. Check their combined storage before allocating any of them.
        int width = region is { } bounds ? bounds.Right - bounds.Left : options.Width;
        int height = region is { } verticalBounds ? verticalBounds.Bottom - verticalBounds.Top : options.Height;
        long pixelCount = (long)width * height;
        if (options.SeparationPlate is not null)
            throw new NotSupportedException("High-precision profile and group rendering is not implemented.");
        PreciseProcessPlan? processPlan = PreparePreciseProcessPlan(pageIndex, options, cancellationToken, out bool composeInk);
        bool processPlates = processPlan is not null;
        // A composed-ink proof holds the byte ink surface with its alpha and spot base plate beside the float frame.
        int pixelStorage = composeInk ? 4 + 4 + 4 + 2 * 4 * sizeof(float)
            : 4 + 2 * 4 * sizeof(float) + (processPlates ? 16 : 0);
        pixelStorage += options.Proof is { Enabled: true, GamutWarnings: true } ? 2 : 0;
        if (pixelCount > options.PixelByteLimit / pixelStorage)
            throw new ArgumentException("The high-precision render exceeds its combined pixel storage limit.", nameof(options));
        var page = _tree.Pages[pageIndex].Dictionary;
        if (composeInk) return RenderComposedInkFrame(pageIndex, options, region, width, height,
            options.PixelByteLimit - pixelCount * pixelStorage, cancellationToken);
        if (page.TryGetValue(Name("Group"), out PdfObject? groupValue)
            && (Resolve(groupValue) is not PdfDictionary group
                || !IsName(group, "S", "Transparency") || !IsName(group, "CS", "DeviceRGB")
                || ReadColorSpace(Name("DeviceRGB"), _pageResources[pageIndex], 0).IsDefault
                || group.TryGetValue(Name("K"), out PdfObject? knockoutValue)
                    && Resolve(knockoutValue) is PdfBoolean { Value: true }))
            throw new NotSupportedException("This high-precision page group is not implemented.");
        var pixels = new float[checked((int)pixelCount * 4)];
        float[]? nativeProcess = processPlates ? new float[pixels.Length] : null;
        var workingCmyk = new PreciseCmykWorkingSpace(options.WorkingCmykProfile);
        PdfRenderedPage compatibility = RenderUncached(pageIndex, options, cancellationToken,
            region: region, preciseRgb: pixels, preciseCmyk: workingCmyk, preciseProcess: nativeProcess,
            preciseProcessPlan: processPlan);
        return FinishPreciseFrame(pageIndex, options, compatibility, workingCmyk, pixels, width, height, cancellationToken);
    }

    // The byte renderer composes the page in its native ink surface, spot plates included, exactly as a
    // separation preview does. Only the composed ink, eight bits a plate, is converted precisely.
    private PdfRenderedFrame RenderComposedInkFrame(int pageIndex, PdfRenderOptions options,
        (int Left, int Top, int Right, int Bottom)? region, int width, int height, long spotPlateBudget,
        CancellationToken cancellationToken)
    {
        var pixels = new float[checked(width * height * 4)];
        var workingCmyk = new PreciseCmykWorkingSpace(options.WorkingCmykProfile);
        // Each named colorant the page paints adds a plate beyond the one the frame's charge counts.
        PdfRenderedPage compatibility = RenderUncached(pageIndex, options, cancellationToken,
            region: region, preciseCmyk: workingCmyk, composedInk: pixels, composedSpotPlateBudget: spotPlateBudget);
        return FinishPreciseFrame(pageIndex, options, compatibility, workingCmyk, pixels, width, height, cancellationToken);
    }

    private PdfRenderedFrame FinishPreciseFrame(int pageIndex, PdfRenderOptions options, PdfRenderedPage compatibility,
        PreciseCmykWorkingSpace workingCmyk, float[] pixels, int width, int height, CancellationToken cancellationToken)
    {
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
        var chain = new PdfProofPixelChain(printer, options);
        Span<double> xyz = stackalloc double[3];
        Span<double> device = stackalloc double[printer.ComponentCount];
        // Warnings follow the proof's own black-point compensation.
        var gamutCheck = gamutWarnings ? PdfPrinterGamutCheck.For(printer, options.BlackPointCompensation
            && options.RenderingIntent != Authoring.PdfRenderingIntent.AbsoluteColorimetric) : null;
        byte[]? classifications = gamutWarnings ? new byte[pixels.Length / 4] : null;
        // Neighboring pixels of one fill share a color, so the last classification is reused for them.
        double lastX = double.NaN, lastY = double.NaN, lastZ = double.NaN;
        byte lastClassification = 0;
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
            {
                if (xyz[0] != lastX || xyz[1] != lastY || xyz[2] != lastZ)
                {
                    (lastX, lastY, lastZ) = (xyz[0], xyz[1], xyz[2]);
                    lastClassification = (byte)gamutCheck.ClassifyXyz(xyz);
                }
                classifications![offset / 4] = lastClassification;
            }
            chain.Apply(xyz, Math.Min(pixels[offset], Math.Min(pixels[offset + 1], pixels[offset + 2])), device);
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
        // Calibrated gray converts through its own whitepoint and gamma; the precise image path
        // accepts only the forms it reproduces exactly and refuses the rest.
        if (Resolve(value) is PdfArray { Count: 2 } calibrated && Resolve(calibrated[0]) is PdfName calGray
            && calGray.ValueAsLatin1() == "CalGray") return true;
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
        // Marked content only decides visibility, which the float path honors before proofing.
        or "BMC" or "BDC" or "EMC"
        // Line styles shape the rasterized stroke; flatness and marked points paint nothing.
        or "J" or "j" or "M" or "i" or "MP" or "DP"
        or "m" or "l" or "c" or "v" or "y" or "h" or "re"
        or "f" or "F" or "f*" or "S" or "s" or "B" or "B*" or "b" or "b*"
        or "n" or "W" or "W*"
        or "BT" or "ET" or "Tf" or "Tm" or "Tj" or "TJ" or "Tr" or "Tc" or "Tw" or "Tz" or "Ts"
        or "Td" or "TD" or "T*" or "TL" or "'" or "\"" or "Do";

    private static bool PreciseOperationSupported(string operation) => operation is
        "q" or "Q" or "cm" or "rg" or "RG" or "g" or "G" or "k" or "K" or "gs" or "w" or "d"
        or "BMC" or "BDC" or "EMC" or "ri"
        // Line styles shape the stroke both renderers rasterize; flatness, marked points and
        // Type 3 glyph metrics paint nothing in either.
        or "J" or "j" or "M" or "i" or "MP" or "DP" or "d0" or "d1"
        or "cs" or "CS" or "sc" or "SC" or "scn" or "SCN"
        or "m" or "l" or "c" or "v" or "y" or "h" or "re"
        or "f" or "F" or "f*" or "S" or "s" or "B" or "B*" or "b" or "b*"
        or "n" or "W" or "W*" or "Do" or "sh"
        or "BT" or "ET" or "Tf" or "Tm" or "Td" or "TD" or "T*" or "TL" or "Tc" or "Tw" or "Tz" or "Ts"
        or "Tj" or "TJ" or "Tr" or "'" or "\"";

    // Only parameters that change paint the float path cannot reproduce are refused. Line styles
    // and stroke adjustment shape the stroke both renderers rasterize; overprint mode matters only
    // while overprint is on; smoothness, flatness, halftones, black generation, undercolor removal
    // and keys the renderer does not know change nothing either renderer draws.
    private bool PreciseGraphicsStateSupported(PdfDictionary settings, bool allowProcessOverprint = false)
    {
        foreach (var entry in settings)
        {
            string name = entry.Key.ValueAsLatin1();
            if (name is not ("op" or "OP" or "AIS" or "TK" or "TR" or "TR2" or "RI" or "UseBlackPtComp"))
                continue;
            PdfObject value = Resolve(entry.Value);
            bool neutral = name switch
            {
                "op" or "OP" => value is PdfBoolean boolean && (!boolean.Value || allowProcessOverprint),
                "AIS" => value is PdfBoolean { Value: false },
                "UseBlackPtComp" => value is PdfName policy && policy.ValueAsLatin1() is "OFF" or "Default",
                "TK" => value is PdfBoolean { Value: true },
                // The byte path applies transfer functions as eight-bit tables.
                "TR" or "TR2" => value is PdfName function && function.ValueAsLatin1() is "Identity" or "Default",
                _ => false
            };
            if (!neutral) return false;
        }
        return true;
    }

    // The graphics-state soft masks one content stream set in a float render, with the save level
    // each was set at. A mask set at level n is reachable only from the current state and states
    // saved after it, so restoring below n, setting another mask at n or ending the stream makes it
    // unreachable and returns its storage to the frame budget.
    private sealed class PreciseMaskScope : IDisposable
    {
        private List<(GraphicsSoftMask Mask, int Level)>? _masks;

        internal void Replace(GraphicsSoftMask? mask, int level)
        {
            ReleaseFrom(level);
            if (mask is not null) (_masks ??= []).Add((mask, level));
        }

        internal void Restore(int level) => ReleaseFrom(level + 1);

        public void Dispose() => ReleaseFrom(0);

        private void ReleaseFrom(int level)
        {
            if (_masks is null) return;
            for (int index = _masks.Count - 1; index >= 0; index--)
            {
                if (_masks[index].Level < level) continue;
                _masks[index].Mask.Release();
                _masks.RemoveAt(index);
            }
        }
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

    // The caller has already multiplied opacity by any graphics-state soft mask value.
    private static void PaintPrecisePixel(RasterSurface surface, int offset, in Color color,
        double opacity, RendererBlendMode blendMode, KnockoutState? knockout)
    {
        if (surface.PreciseProcess is not null)
        {
            if (color.PreciseProcess is null || blendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible)
                || knockout is not null || surface.HasProfile || surface.PreciseGroupAlpha is not null
                || surface.GroupAlpha is not null || surface.GroupShape is not null)
                throw new NotSupportedException("This paint cannot supply precise native process plates.");
            PaintPreciseProcessPixel(surface, offset, color.PreciseProcess, opacity);
            return;
        }
        if (blendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible or RendererBlendMode.Multiply or RendererBlendMode.Screen)
            || knockout is not null || surface.HasProfile
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

    internal static (double Red, double Green, double Blue) PreciseConnectionToRgb(double x, double y, double z)
    {
        var (red, green, blue) = PreciseConnectionToLinearRgb(x, y, z);
        return (EncodeSrgb(red), EncodeSrgb(green), EncodeSrgb(blue));
    }

    internal static double EncodeSrgb(double value) => value <= 0.0031308
        ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;

    // Linear D65 sRGB, before encoding, for the ICC D50 PCS.
    internal static (double Red, double Green, double Blue) PreciseConnectionToLinearRgb(double x, double y, double z)
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
        return (3.2404542 * adaptedX - 1.5371385 * adaptedY - 0.4985314 * adaptedZ,
            -0.969266 * adaptedX + 1.8760108 * adaptedY + 0.041556 * adaptedZ,
            0.0556434 * adaptedX - 0.2040259 * adaptedY + 1.0572252 * adaptedZ);
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

    private static bool TryPaintPreciseReducedImage(RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, Matrix inverse, byte[] samples, int rowBytes,
        int sourceWidth, int sourceHeight, int components, int bits, double[] decode,
        ImageColorSpace colorSpace, bool imageMask, Color stencilColor, bool stencilPaintsOne,
        SoftMask? softMask, bool explicitMask, bool smooth,
        double opacity, RendererBlendMode blendMode,
        IReadOnlyList<ClipRegion> clips, Point[] corners, int left, int top, int right, int bottom,
        GraphicsSoftMask? graphicsSoftMask, CancellationToken cancellationToken)
    {
        if (imageMask) decode = [0, 1];
        bool device = colorSpace is { Components: 1 or 3, Profile: null, Converter: null,
                MultiConverter: null, Palette: null, IsIccBased: false, HasIccSource: false,
                IsDefault: false, ComponentRange: null, HasSpotColorants: false,
                BlackPointCompensationEnabled: false };
        bool matrixIcc = colorSpace is { IsIccBased: true, ComponentRange: null,
            Profile: PdfIccProfileTransform { IsLutBased: false, IsLabInput: false,
                Components: 1 or 3, RenderingIntent: 1 }, Intent: 1,
            BlackPointCompensationEnabled: false };
        if (softMask is not null && (!device || imageMask || smooth
            || softMask.Bits is not (1 or 8 or 16)
            || !double.IsFinite(softMask.DecodeStart) || !double.IsFinite(softMask.DecodeEnd)
            || softMask.DecodeStart is < 0 or > 1 || softMask.DecodeEnd is < 0 or > 1)) return false;
        bool binary = device && components == 1 && bits == 1;
        if (inverse.B != 0 || inverse.C != 0 || bits is not (1 or 8 or 16) || bits == 1 && !binary
            || !device && !matrixIcc
            || decode.Length != 2 * components || decode.Any(value => !double.IsFinite(value) || value < 0 || value > 1)
            || !binary && corners.Any(point => Math.Abs(point.X * scaleX - Math.Round(point.X * scaleX)) > 1e-9
                || Math.Abs(point.Y * scaleY - Math.Round(point.Y * scaleY)) > 1e-9)) return false;
        double footprintX = Math.Abs(inverse.A / scaleX) * sourceWidth;
        double footprintY = Math.Abs(inverse.D / scaleY) * sourceHeight;
        if (!double.IsFinite(footprintX) || !double.IsFinite(footprintY)
            || !binary && softMask is null && footprintX <= 1 && footprintY <= 1) return false;
        bool average = softMask is null && (footprintX > 1 && footprintY > 1
            || !binary && (footprintX > 1 || footprintY > 1));
        double imageLeft = corners.Min(point => point.X) * scaleX;
        double imageRight = corners.Max(point => point.X) * scaleX;
        double imageTop = targetHeight - corners.Max(point => point.Y) * scaleY;
        double imageBottom = targetHeight - corners.Min(point => point.Y) * scaleY;
        int factor = Math.Max(1, (int)Math.Floor(Math.Min(sourceWidth / Math.Max(1, Math.Floor(imageRight - imageLeft)),
            sourceHeight / Math.Max(1, Math.Floor(imageBottom - imageTop)))));
        int planeWidth = (sourceWidth + factor - 1) / factor;
        int planeHeight = (sourceHeight + factor - 1) / factor;
        int maskWidth = softMask is null ? 1 : explicitMask ? softMask.Width
            : (int)Math.Clamp(Math.Ceiling((imageRight - imageLeft) * 2), 1, softMask.Width);
        int maskHeight = softMask is null ? 1 : explicitMask ? softMask.Height
            : (int)Math.Clamp(Math.Ceiling((imageBottom - imageTop) * 2), 1, softMask.Height);
        while ((long)maskWidth * maskHeight > 4_000_000)
        {
            maskWidth = Math.Max(1, maskWidth / 2);
            maskHeight = Math.Max(1, maskHeight / 2);
        }
        int stencilSamples = imageMask && !average && Math.Max(footprintX, footprintY) > 1
            ? Math.Clamp((int)Math.Ceiling(Math.Max(footprintX, footprintY)), 4, 8) : 0;
        long pixelWork = (long)(right - left) * (bottom - top);
        long perPixelWork = stencilSamples == 0 ? 1 : stencilSamples * stencilSamples;
        if (softMask is not null)
            perPixelWork += 4L * ((softMask.Width + maskWidth - 1L) / maskWidth)
                * ((softMask.Height + maskHeight - 1L) / maskHeight);
        if (perPixelWork > 64_000_000 / Math.Max(1, pixelWork))
            throw new NotSupportedException("Precise image sampling exceeds its weighted work limit.");
        int firstColumn = (int)Math.Ceiling(imageLeft - .5 + 1e-9);
        int lastColumn = (int)Math.Floor(imageRight - .5 - 1e-9);
        int firstRow = (int)Math.Ceiling(imageTop - .5 + 1e-9);
        int lastRow = (int)Math.Floor(imageBottom - .5 - 1e-9);
        ImageSampleConverter.AreaSpan Column(int x)
        {
            if (!binary || !average) return ImageSampleConverter.AreaSpan.FromFootprint(
                inverse.Apply((x + .5) / scaleX, 0).X * sourceWidth, footprintX, sourceWidth);
            double a = inverse.Apply(x / scaleX, 0).X * sourceWidth;
            double b = inverse.Apply((x + 1) / scaleX, 0).X * sourceWidth;
            return ImageSampleConverter.AreaSpan.FromRange(
                x == (inverse.A < 0 ? lastColumn : firstColumn) ? 0 : Math.Min(a, b),
                x == (inverse.A < 0 ? firstColumn : lastColumn) ? sourceWidth : Math.Max(a, b), sourceWidth);
        }
        ImageSampleConverter.AreaSpan Row(int y)
        {
            if (!binary || !average) return ImageSampleConverter.AreaSpan.FromFootprint(
                (1 - inverse.Apply(0, (targetHeight - y - .5) / scaleY).Y) * sourceHeight, footprintY, sourceHeight);
            double a = (1 - inverse.Apply(0, (targetHeight - y) / scaleY).Y) * sourceHeight;
            double b = (1 - inverse.Apply(0, (targetHeight - y - 1) / scaleY).Y) * sourceHeight;
            return ImageSampleConverter.AreaSpan.FromRange(
                y == (inverse.D < 0 ? lastRow : firstRow) ? 0 : Math.Min(a, b),
                y == (inverse.D < 0 ? firstRow : lastRow) ? sourceHeight : Math.Max(a, b), sourceHeight);
        }
        long columns = 0, rows = 0;
        for (int x = left; x < right; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var span = Column(x);
            columns += span.End - span.First;
        }
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var span = Row(y);
            rows += span.End - span.First;
        }
        if (rows != 0 && columns > 64_000_000 / rows)
            throw new NotSupportedException("Precise image sampling exceeds its weighted work limit.");
        var converter = new ImageSampleConverter(samples, rowBytes, components, bits, decode,
            colorSpace, targetInk: false, targetProfile: null);
        var values = new double[components];
        double maximum = (1u << bits) - 1;
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = Row(y);
            if (row.Length <= 0) continue;
            for (int x = left; x < right; x++)
            {
                Point unit = inverse.Apply((x + .5) / scaleX, (targetHeight - y - .5) / scaleY);
                if (unit.X < 0 || unit.X >= 1 || unit.Y < 0 || unit.Y >= 1) continue;
                var column = Column(x);
                if (column.Length <= 0) continue;
                if (!average)
                {
                    int sourceX = Math.Min((int)(unit.X * sourceWidth), sourceWidth - 1);
                    int sourceY = Math.Min((int)((1 - unit.Y) * sourceHeight), sourceHeight - 1);
                    if (softMask is not null)
                    {
                        sourceX = Math.Min((int)(unit.X * planeWidth), planeWidth - 1) * factor;
                        sourceY = Math.Min((int)((1 - unit.Y) * planeHeight), planeHeight - 1) * factor;
                    }
                    double alpha = imageMask && ((converter.Raw(sourceX, sourceY, 0) != 0) != stencilPaintsOne) ? 0 : 1;
                    if (stencilSamples != 0)
                    {
                        int painted = 0;
                        for (int sy = 0; sy < stencilSamples; sy++)
                            for (int sx = 0; sx < stencilSamples; sx++)
                            {
                                double u = unit.X + ((sx + .5) / stencilSamples - .5) * inverse.A / scaleX;
                                double v = unit.Y - ((sy + .5) / stencilSamples - .5) * inverse.D / scaleY;
                                if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
                                bool one = converter.Raw(Math.Min((int)(u * sourceWidth), sourceWidth - 1),
                                    Math.Min((int)((1 - v) * sourceHeight), sourceHeight - 1), 0) != 0;
                                if (one == stencilPaintsOne) painted++;
                            }
                        alpha = painted / (double)(stencilSamples * stencilSamples);
                    }
                    if (softMask is not null)
                        alpha *= SamplePreciseImageMask(softMask, maskWidth, maskHeight, unit.X, 1 - unit.Y,
                            footprintX > 1 || footprintY > 1, cancellationToken);
                    SetPixel(target, targetWidth, x, y, imageMask ? stencilColor : converter.ConvertPrecise(sourceX, sourceY),
                        opacity * alpha * ClipAlpha(clips, x, y), blendMode, graphicsSoftMask);
                    continue;
                }
                var sampleRow = row;
                Array.Clear(values);
                for (int sampleY = sampleRow.First; sampleY < sampleRow.End; sampleY++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int sampleX = column.First; sampleX < column.End; sampleX++)
                    {
                        double weight = sampleRow.Weight(sampleY) * column.Weight(sampleX);
                        for (int component = 0; component < components; component++)
                            values[component] += weight * (decode[component * 2]
                                + converter.Raw(sampleX, sampleY, component) / maximum
                                    * (decode[component * 2 + 1] - decode[component * 2]));
                    }
                }
                double area = column.Length * sampleRow.Length;
                for (int component = 0; component < components; component++) values[component] /= area;
                double stencilCoverage = imageMask ? stencilPaintsOne ? values[0] : 1 - values[0] : 1;
                SetPixel(target, targetWidth, x, y, imageMask ? stencilColor : colorSpace.Convert(values),
                    opacity * stencilCoverage * ClipAlpha(clips, x, y), blendMode, graphicsSoftMask);
            }
        }
        return true;
    }

    private static double SamplePreciseImageMask(SoftMask mask, int width, int height,
        double unitX, double unitY, bool smooth, CancellationToken cancellationToken)
    {
        double Raw(int x, int y)
        {
            x = Math.Clamp(x, 0, mask.Width - 1);
            y = Math.Clamp(y, 0, mask.Height - 1);
            int rowBytes = (mask.Width * mask.Bits + 7) / 8;
            int offset = y * rowBytes + x * mask.Bits / 8;
            int raw = mask.Bits == 16 ? mask.Samples[offset] * 256 + mask.Samples[offset + 1]
                : mask.Bits == 8 ? mask.Samples[offset]
                : (mask.Samples[offset] >> (7 - (x & 7))) & 1;
            return Math.Clamp(mask.DecodeStart + raw / (double)((1u << mask.Bits) - 1)
                * (mask.DecodeEnd - mask.DecodeStart), 0, 1);
        }
        double Sample(int x, int y)
        {
            x = Math.Clamp(x, 0, width - 1);
            y = Math.Clamp(y, 0, height - 1);
            int startX = (int)((long)x * mask.Width / width), endX = (int)((long)(x + 1) * mask.Width / width);
            int startY = (int)((long)y * mask.Height / height), endY = (int)((long)(y + 1) * mask.Height / height);
            double sum = 0;
            for (int sy = startY; sy < endY; sy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int sx = startX; sx < endX; sx++) sum += Raw(sx, sy);
            }
            return sum / ((endX - startX) * (double)(endY - startY));
        }
        if (!smooth) return Sample((int)(unitX * width), (int)(unitY * height));
        double x = unitX * width - .5, y = unitY * height - .5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double wx = x - x0, wy = y - y0;
        return (Sample(x0, y0) * (1 - wx) + Sample(x0 + 1, y0) * wx) * (1 - wy)
            + (Sample(x0, y0 + 1) * (1 - wx) + Sample(x0 + 1, y0 + 1) * wx) * wy;
    }

    private static void PaintPreciseImage(RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, Matrix inverse, byte[] samples, int rowBytes,
        int sourceWidth, int sourceHeight, int components, int bits, double[] decode,
        ImageColorSpace colorSpace, double opacity, RendererBlendMode blendMode,
        IReadOnlyList<ClipRegion> clips, int left, int top, int right, int bottom,
        bool smooth, int reductionColumns, int reductionRows, int[]? colorKeyMask, SoftMask? softMask,
        GraphicsSoftMask? graphicsSoftMask, double[]? matte, bool fractionalImageEdges, double imageLeft, double imageRight,
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
                SetPixel(target, targetWidth, x, y, color, opacity * maskAlpha * coverage * ClipAlpha(clips, x, y), blendMode,
                    graphicsSoftMask);
            }
        }
    }
}
