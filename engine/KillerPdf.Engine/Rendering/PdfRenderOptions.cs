using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

/// <summary>Controls one bounded page rasterization.</summary>
public sealed record PdfRenderOptions
{
    /// <summary>Gets optional requested printer proof preferences for float frame rendering.</summary>
    /// <remarks>Supported PDF and native float renderers apply proof; unsupported requests and byte rendering are diagnosed.</remarks>
    public PdfDocumentProofSettings? Proof { get; init; }

    private readonly PdfColorProfile? _workingCmykProfile;

    /// <summary>Gets the working CMYK profile assumed for untagged DeviceCMYK on float frame renders.</summary>
    /// <remarks>
    /// Defaults to the bundled <see cref="PdfCmykViewingDefault"/> profile. Tagged CMYK, a resource
    /// DefaultCMYK and a usable document output intent remain authoritative. The profile converts
    /// through its perceptual forward table, or its relative colorimetric table when it has no
    /// perceptual one. Frames report the profile they assumed.
    /// </remarks>
    public PdfColorProfile WorkingCmykProfile
    {
        get => _workingCmykProfile ?? PdfCmykViewingDefault.SharedProfile;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.WorkingCmykForwardIntent is null)
                throw new ArgumentException("A working CMYK profile must describe CMYK with a perceptual or relative colorimetric forward table.",
                    nameof(value));
            _workingCmykProfile = value;
        }
    }

    /// <summary>Creates render options for an exact pixel size.</summary>
    public PdfRenderOptions(int width, int height, bool transparentBackground = false,
        bool includeAnnotations = true, bool includeFormFields = true,
        long maximumPixelBytes = MaximumPixelBytes)
    {
        if (width <= 0 || width > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0 || height > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (maximumPixelBytes is <= 0 or > MaximumPixelBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPixelBytes));
        long byteCount = checked((long)width * height * 4);
        if (byteCount > maximumPixelBytes)
            throw new ArgumentException("The requested page bitmap exceeds the render limit.");
        Width = width;
        Height = height;
        TransparentBackground = transparentBackground;
        IncludeAnnotations = includeAnnotations;
        IncludeFormFields = includeFormFields;
        PixelByteLimit = maximumPixelBytes;
    }

    /// <summary>Gets the largest accepted width or height.</summary>
    public const int MaximumDimension = 32_768;
    /// <summary>Gets the largest allocated pixel buffer.</summary>
    public const long MaximumPixelBytes = 512L * 1024 * 1024;
    /// <summary>Gets the exact output width.</summary>
    public int Width { get; }
    /// <summary>Gets the exact output height.</summary>
    public int Height { get; }
    /// <summary>Gets whether unpainted pixels retain zero alpha.</summary>
    public bool TransparentBackground { get; }
    /// <summary>Gets whether annotation appearances should be painted.</summary>
    public bool IncludeAnnotations { get; }
    /// <summary>Gets whether form-field appearances should be painted.</summary>
    public bool IncludeFormFields { get; }
    /// <summary>Gets whether annotation appearances use print visibility instead of screen visibility.</summary>
    public bool ForPrinting { get; init; }
    /// <summary>
    /// Gets whether unapplied redaction marks (/Redact annotations) are painted. A viewer that
    /// draws the marks itself turns this off so they are not shown twice.
    /// </summary>
    public bool IncludeRedactionMarks { get; init; } = true;
    /// <summary>
    /// Gets whether measurement annotations (dimension lines, polylines, and polygons, and any
    /// line, polyline, or polygon with a /Measure dictionary) are painted. A viewer that draws
    /// and edits measurements itself turns this off so they are not shown twice.
    /// </summary>
    public bool IncludeMeasurementAnnotations { get; init; } = true;
    /// <summary>
    /// Gets whether signature fields that already hold a signature, or that still carry a drawn
    /// appearance after their signature was cleared, are painted when
    /// <see cref="IncludeFormFields"/> is off. A viewer that draws the other fields as live controls
    /// turns this on, because nothing else shows a signed field's appearance.
    /// </summary>
    public bool IncludeSignedSignatureFields { get; init; }
    /// <summary>
    /// Gets whether push buttons are painted when <see cref="IncludeFormFields"/> is off. A viewer
    /// that draws the other fields as live controls turns this on, because it has no control for a
    /// push button.
    /// </summary>
    public bool IncludePushButtonFields { get; init; }
    /// <summary>Gets the caller-supplied output pixel-buffer limit.</summary>
    public long PixelByteLimit { get; }
    /// <summary>Gets whether the renderer may reuse and retain this finished bitmap.</summary>
    public bool CacheResult { get; init; } = true;
    /// <summary>
    /// Gets the largest number of threads the renderer may use for large row-independent
    /// operations such as opaque fills, image placement, and JPEG 2000 reconstruction. One keeps
    /// those operations on the calling thread. Output pixels are identical at any setting.
    /// </summary>
    public int MaximumParallelism { get; init; } = 1;
    /// <summary>Gets the separate row-worker cap for large reduced JPEG 2000 image paints.</summary>
    public int Jpeg2000PaintParallelism { get; init; } = 1;
    /// <summary>
    /// Gets one ink plate to render instead of the composite page: Cyan, Magenta, Yellow,
    /// Black or a spot colorant name. The page is drawn in ink and the plate's coverage comes
    /// out as gray, black for full ink and white for none. Plate renders are never cached.
    /// </summary>
    public string? SeparationPlate { get; init; }
}
