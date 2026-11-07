using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Editing;

/// <summary>An immutable binary pixel selection with a top-left origin.</summary>
/// <remarks>
/// Coverage has no feathering. Operations own their output and preserve source frames and input selections.
/// Combined working storage is limited to 256 MiB and includes retained input buffers and the output copy.
/// </remarks>
public sealed class PdfRasterSelection
{
    private const long MaximumWorkingBytes = 256L * 1024 * 1024;
    private const long BufferOverhead = 256;
    private readonly byte[] _coverage;

    private PdfRasterSelection(int width, int height, byte[] coverage, CancellationToken cancellationToken)
    {
        Width = width;
        Height = height;
        _coverage = new byte[coverage.Length];
        for (int i = 0; i < coverage.Length; i++)
        {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            _coverage[i] = coverage[i];
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Gets the pixel width.</summary>
    public int Width { get; }
    /// <summary>Gets the pixel height.</summary>
    public int Height { get; }
    /// <summary>Gets tightly packed coverage bytes, each zero or 255.</summary>
    public ReadOnlySpan<byte> Coverage => _coverage;

    /// <summary>Selects the four-connected positive-alpha region matching the seed RGB.</summary>
    /// <remarks>
    /// Maximum absolute RGB difference from the seed is compared inclusively with a finite, nonnegative tolerance
    /// in the frame's declared channel encoding. Alpha only determines eligibility; a zero-alpha seed selects nothing.
    /// Storage includes source floats, any gamut mask, coverage, visited flags, frontier and the owned output copy.
    /// </remarks>
    public static PdfRasterSelection SelectConnected(PdfRenderedFrame source, int seedX, int seedY, float tolerance,
        long maximumWorkingBytes = 256L * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if ((uint)seedX >= (uint)source.Width) throw new ArgumentOutOfRangeException(nameof(seedX));
        if ((uint)seedY >= (uint)source.Height) throw new ArgumentOutOfRangeException(nameof(seedY));
        if (!float.IsFinite(tolerance) || tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        // Input RGBA floats, coverage, visited flags, integer frontier and owned output copy.
        int count = ValidateBudget(source.Width, source.Height,
            source.PrinterGamutWarnings is null ? 23 : 24, maximumWorkingBytes);
        var coverage = new byte[count];
        var visited = new byte[count];
        var frontier = new int[count];
        int seed = checked(seedY * source.Width + seedX), head = 0, tail = 0;
        float red = source.Pixels[seed * 4], green = source.Pixels[seed * 4 + 1], blue = source.Pixels[seed * 4 + 2];
        Visit(seed);
        while (head < tail)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pixel = frontier[head++], x = pixel % source.Width;
            if (x > 0) Visit(pixel - 1);
            if (x + 1 < source.Width) Visit(pixel + 1);
            if (pixel >= source.Width) Visit(pixel - source.Width);
            if (pixel < count - source.Width) Visit(pixel + source.Width);
        }
        return new PdfRasterSelection(source.Width, source.Height, coverage, cancellationToken);

        void Visit(int pixel)
        {
            if (visited[pixel] != 0) return;
            visited[pixel] = 1;
            var pixels = source.Pixels;
            int offset = pixel * 4;
            if (pixels[offset + 3] <= 0 || source.Pixels[seed * 4 + 3] <= 0
                || Math.Abs((double)pixels[offset] - red) > tolerance
                || Math.Abs((double)pixels[offset + 1] - green) > tolerance
                || Math.Abs((double)pixels[offset + 2] - blue) > tolerance) return;
            coverage[pixel] = 255;
            frontier[tail++] = pixel;
        }
    }

    /// <summary>Selects a positive-size rectangle wholly inside the pixel dimensions.</summary>
    public static PdfRasterSelection SelectRectangle(int width, int height, int left, int top,
        int selectionWidth, int selectionHeight, long maximumWorkingBytes = MaximumWorkingBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = ValidateBudget(width, height, 2, maximumWorkingBytes);
        if (left < 0 || left >= width) throw new ArgumentOutOfRangeException(nameof(left));
        if (top < 0 || top >= height) throw new ArgumentOutOfRangeException(nameof(top));
        if (selectionWidth <= 0 || (long)left + selectionWidth > width)
            throw new ArgumentOutOfRangeException(nameof(selectionWidth));
        if (selectionHeight <= 0 || (long)top + selectionHeight > height)
            throw new ArgumentOutOfRangeException(nameof(selectionHeight));
        var coverage = new byte[count];
        for (int y = top; y < top + selectionHeight; y++)
            for (int x = left; x < left + selectionWidth; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                coverage[y * width + x] = 255;
            }
        return new PdfRasterSelection(width, height, coverage, cancellationToken);
    }

    /// <summary>Returns an independently owned union with a selection of the same dimensions.</summary>
    public PdfRasterSelection Add(PdfRasterSelection other, long maximumWorkingBytes = MaximumWorkingBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(other);
        cancellationToken.ThrowIfCancellationRequested();
        if (other.Width != Width || other.Height != Height)
            throw new ArgumentException("Selection dimensions must match.", nameof(other));
        int count = ValidateBudget(Width, Height, 4, maximumWorkingBytes);
        var coverage = new byte[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            coverage[i] = (byte)(_coverage[i] | other._coverage[i]);
        }
        return new PdfRasterSelection(Width, Height, coverage, cancellationToken);
    }

    /// <summary>Returns an independently owned difference with a selection of the same dimensions.</summary>
    public PdfRasterSelection Subtract(PdfRasterSelection other, long maximumWorkingBytes = MaximumWorkingBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(other);
        cancellationToken.ThrowIfCancellationRequested();
        if (other.Width != Width || other.Height != Height)
            throw new ArgumentException("Selection dimensions must match.", nameof(other));
        int count = ValidateBudget(Width, Height, 4, maximumWorkingBytes);
        var coverage = new byte[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            coverage[i] = other._coverage[i] == 0 ? _coverage[i] : (byte)0;
        }
        return new PdfRasterSelection(Width, Height, coverage, cancellationToken);
    }

    private static int ValidateBudget(int width, int height, int bytesPerPixel, long maximumWorkingBytes)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (maximumWorkingBytes is <= 0 or > MaximumWorkingBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumWorkingBytes));
        long count = checked((long)width * height);
        if (maximumWorkingBytes < BufferOverhead || count > (maximumWorkingBytes - BufferOverhead) / bytesPerPixel)
            throw new ArgumentException("The selection exceeds its combined working storage limit.", nameof(maximumWorkingBytes));
        return checked((int)count);
    }
}
