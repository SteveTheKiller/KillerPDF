namespace KillerPdf.Engine.Rendering;

/// <summary>Controls one bounded page rasterization.</summary>
public sealed record PdfRenderOptions
{
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
    /// <summary>Gets the caller-supplied output pixel-buffer limit.</summary>
    public long PixelByteLimit { get; }
}
