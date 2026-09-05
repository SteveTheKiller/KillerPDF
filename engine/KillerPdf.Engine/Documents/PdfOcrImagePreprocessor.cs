using System.Numerics;
using System.Runtime.InteropServices;

namespace KillerPdf.Engine.Documents;

/// <summary>A bounded grayscale raster prepared for OCR recognition.</summary>
public sealed class PdfOcrPreparedImage
{
    internal PdfOcrPreparedImage(int width, int height, byte[] pixels, bool binary,
        IEnumerable<string> diagnostics)
    {
        Width = width;
        Height = height;
        Pixels = new ReadOnlyMemory<byte>(pixels);
        IsBinary = binary;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>Gets the image width.</summary>
    public int Width { get; }
    /// <summary>Gets the image height.</summary>
    public int Height { get; }
    /// <summary>Gets tightly packed eight-bit grayscale samples.</summary>
    public ReadOnlyMemory<byte> Pixels { get; }
    /// <summary>Gets whether every sample is black or white.</summary>
    public bool IsBinary { get; }
    /// <summary>Gets requested preprocessing operations that remain incomplete.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>A bounded BGRA32 image prepared for an OCR pipeline stage.</summary>
public sealed class PdfOcrBgraImage
{
    internal PdfOcrBgraImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = new ReadOnlyMemory<byte>(pixels);
    }

    /// <summary>Gets the image width.</summary>
    public int Width { get; }
    /// <summary>Gets the image height.</summary>
    public int Height { get; }
    /// <summary>Gets tightly packed BGRA32 samples.</summary>
    public ReadOnlyMemory<byte> Pixels { get; }
}

/// <summary>Prepares rendered BGRA32 pages for engine-owned OCR recognition.</summary>
public static class PdfOcrImagePreprocessor
{
    private const int MaximumDimension = 32_768;
    private const long MaximumInputBytes = 512L * 1024 * 1024;

    /// <summary>Converts and optionally cleans one rendered page without platform APIs.</summary>
    public static PdfOcrPreparedImage PrepareBgra(ReadOnlyMemory<byte> bgra, int width,
        int height, PdfOcrOptions options, CancellationToken cancellationToken = default)
        => PrepareBgra(bgra, width, height, 0, options, cancellationToken);

    /// <summary>Rotates, converts, and optionally cleans one rendered page.</summary>
    public static PdfOcrPreparedImage PrepareBgraRotated(ReadOnlyMemory<byte> bgra,
        int width, int height, int degrees, PdfOcrOptions options,
        CancellationToken cancellationToken = default)
        => PrepareBgra(bgra, width, height, degrees, options, cancellationToken);

    private static PdfOcrPreparedImage PrepareBgra(ReadOnlyMemory<byte> bgra, int width,
        int height, int degrees, PdfOcrOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateBgra(bgra, width, height);
        degrees = NormalizeRotation(degrees);
        int preparedWidth = degrees is 90 or 270 ? height : width;
        int preparedHeight = degrees is 90 or 270 ? width : height;

        byte[] gray = GC.AllocateUninitializedArray<byte>(checked(width * height));
        if (degrees == 0)
            ConvertBgraToGray(bgra.Span, gray, cancellationToken);
        else
            ConvertBgraToRotatedGray(
                bgra.Span, width, height, degrees, gray, cancellationToken);

        bool binary = false;
        if (options.RemoveBackground)
        {
            gray = AdaptiveThreshold(
                gray, preparedWidth, preparedHeight, cancellationToken);
            binary = true;
        }
        if (options.RemoveNoise)
            gray = Median3x3(gray, preparedWidth, preparedHeight, cancellationToken);
        if (options.Deskew)
            gray = Deskew(gray, preparedWidth, preparedHeight, cancellationToken);

        var diagnostics = new List<string>();
        if (options.CorrectOrientation)
            diagnostics.Add("OCR orientation detection is not implemented.");
        return new PdfOcrPreparedImage(
            preparedWidth, preparedHeight, gray, binary, diagnostics);
    }

    private static void ConvertBgraToGray(ReadOnlySpan<byte> source, Span<byte> gray,
        CancellationToken cancellationToken)
    {
        int pixel = 0;
        int batch = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            ReadOnlySpan<uint> packed = MemoryMarshal.Cast<byte, uint>(source);
            var alpha = new Vector<uint>(255);
            for (; pixel <= gray.Length - batch; pixel += batch)
            {
                if ((pixel & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                int step = Vector<uint>.Count;
                var first = new Vector<uint>(packed.Slice(pixel, step));
                var second = new Vector<uint>(packed.Slice(pixel + step, step));
                var third = new Vector<uint>(packed.Slice(pixel + 2 * step, step));
                var fourth = new Vector<uint>(packed.Slice(pixel + 3 * step, step));
                if (!Opaque(first) || !Opaque(second) || !Opaque(third) || !Opaque(fourth))
                {
                    for (int index = 0; index < batch; index++)
                        gray[pixel + index] = ConvertPixel(source, pixel + index);
                    continue;
                }
                Vector<ushort> firstHalf = Vector.Narrow(Luma(first), Luma(second));
                Vector<ushort> secondHalf = Vector.Narrow(Luma(third), Luma(fourth));
                Vector.Narrow(firstHalf, secondHalf).CopyTo(gray.Slice(pixel, batch));
            }

            bool Opaque(Vector<uint> values) => Vector.EqualsAll(values >> 24, alpha);
            static Vector<uint> Luma(Vector<uint> values)
            {
                var mask = new Vector<uint>(255);
                Vector<uint> blue = values & mask;
                Vector<uint> green = (values >> 8) & mask;
                Vector<uint> red = (values >> 16) & mask;
                return (29 * blue + 150 * green + 77 * red + new Vector<uint>(128)) >> 8;
            }
        }
        for (; pixel < gray.Length; pixel++)
        {
            if ((pixel & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            gray[pixel] = ConvertPixel(source, pixel);
        }

    }

    private static void ConvertBgraToRotatedGray(ReadOnlySpan<byte> source,
        int width, int height, int degrees, Span<byte> gray,
        CancellationToken cancellationToken)
    {
        int preparedWidth = degrees is 90 or 270 ? height : width;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                (int destinationX, int destinationY) = degrees switch
                {
                    90 => (height - 1 - y, x),
                    180 => (width - 1 - x, height - 1 - y),
                    _ => (y, width - 1 - x)
                };
                gray[destinationY * preparedWidth + destinationX] =
                    ConvertPixel(source, y * width + x);
            }
        }
    }

    private static byte ConvertPixel(ReadOnlySpan<byte> pixels, int index)
    {
        int offset = index * 4;
        int alpha = pixels[offset + 3];
        int blue = alpha == 255 ? pixels[offset]
            : (pixels[offset] * alpha + 255 * (255 - alpha) + 127) / 255;
        int green = alpha == 255 ? pixels[offset + 1]
            : (pixels[offset + 1] * alpha + 255 * (255 - alpha) + 127) / 255;
        int red = alpha == 255 ? pixels[offset + 2]
            : (pixels[offset + 2] * alpha + 255 * (255 - alpha) + 127) / 255;
        return (byte)((29 * blue + 150 * green + 77 * red + 128) >> 8);
    }

    private static int NormalizeRotation(int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        return degrees is 0 or 90 or 180 or 270
            ? degrees : throw new ArgumentOutOfRangeException(nameof(degrees));
    }

    /// <summary>Crops a rendered BGRA32 image with bounds clamped to its source.</summary>
    public static PdfOcrBgraImage CropBgra(ReadOnlyMemory<byte> bgra, int sourceWidth,
        int sourceHeight, int left, int top, int width, int height,
        CancellationToken cancellationToken = default)
    {
        ValidateBgra(bgra, sourceWidth, sourceHeight);
        left = Math.Clamp(left, 0, sourceWidth - 1);
        top = Math.Clamp(top, 0, sourceHeight - 1);
        width = Math.Clamp(width, 1, sourceWidth - left);
        height = Math.Clamp(height, 1, sourceHeight - top);
        byte[] result = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        ReadOnlySpan<byte> source = bgra.Span;
        for (int row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Slice(((top + row) * sourceWidth + left) * 4, width * 4)
                .CopyTo(result.AsSpan(row * width * 4));
        }
        return new PdfOcrBgraImage(width, height, result);
    }

    /// <summary>Rotates a rendered BGRA32 image clockwise by a right angle.</summary>
    public static PdfOcrBgraImage RotateBgra(ReadOnlyMemory<byte> bgra, int width,
        int height, int degrees, CancellationToken cancellationToken = default)
    {
        ValidateBgra(bgra, width, height);
        degrees = NormalizeRotation(degrees);
        int rotatedWidth = degrees is 90 or 270 ? height : width;
        int rotatedHeight = degrees is 90 or 270 ? width : height;
        byte[] result = GC.AllocateUninitializedArray<byte>(bgra.Length);
        ReadOnlySpan<byte> source = bgra.Span;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                (int destinationX, int destinationY) = degrees switch
                {
                    90 => (height - 1 - y, x),
                    180 => (width - 1 - x, height - 1 - y),
                    270 => (y, width - 1 - x),
                    _ => (x, y)
                };
                source.Slice((y * width + x) * 4, 4).CopyTo(
                    result.AsSpan((destinationY * rotatedWidth + destinationX) * 4));
            }
        }
        return new PdfOcrBgraImage(rotatedWidth, rotatedHeight, result);
    }

    private static void ValidateBgra(ReadOnlyMemory<byte> bgra, int width, int height)
    {
        if (width <= 0 || width > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0 || height > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(height));
        long expected = checked((long)width * height * 4);
        if (expected > MaximumInputBytes || bgra.Length != expected)
            throw new ArgumentException(
                "The BGRA image length or dimensions are invalid.", nameof(bgra));
    }

    private static byte[] AdaptiveThreshold(byte[] source, int width, int height,
        CancellationToken cancellationToken)
    {
        byte[] result = new byte[source.Length];
        int radius = Math.Clamp(Math.Min(width, height) / 32, 4, 32);
        var columns = new int[width];
        int initialBottom = Math.Min(height, radius + 1);
        for (int row = 0; row < initialBottom; row++)
            for (int x = 0; x < width; x++)
                columns[x] += source[row * width + x];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (y > 0)
            {
                int removed = y - radius - 1;
                if (removed >= 0)
                    for (int x = 0; x < width; x++)
                        columns[x] -= source[removed * width + x];
                int added = y + radius;
                if (added < height)
                    for (int x = 0; x < width; x++)
                        columns[x] += source[added * width + x];
            }
            int top = Math.Max(0, y - radius), bottom = Math.Min(height, y + radius + 1);
            int right = Math.Min(width, radius + 1);
            long sum = 0;
            for (int column = 0; column < right; column++) sum += columns[column];
            for (int x = 0; x < width; x++)
            {
                int left = Math.Max(0, x - radius);
                int mean = (int)(sum / ((right - left) * (bottom - top)));
                result[y * width + x] = source[y * width + x] < mean - 12 ? (byte)0 : (byte)255;
                if (x - radius >= 0) sum -= columns[x - radius];
                if (right < width) sum += columns[right++];
            }
        }
        return result;
    }

    private static byte[] Deskew(byte[] source, int width, int height,
        CancellationToken cancellationToken)
    {
        double angle = EstimateSkew(source, width, height, cancellationToken);
        if (Math.Abs(angle) < 0.25) return source;
        double radians = angle * Math.PI / 180;
        double cosine = Math.Cos(radians), sine = Math.Sin(radians);
        double centerX = (width - 1) / 2d, centerY = (height - 1) / 2d;
        byte[] result = Enumerable.Repeat(byte.MaxValue, source.Length).ToArray();
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double dy = y - centerY;
            for (int x = 0; x < width; x++)
            {
                double dx = x - centerX;
                int sourceX = (int)Math.Round(centerX + cosine * dx - sine * dy);
                int sourceY = (int)Math.Round(centerY + sine * dx + cosine * dy);
                if (sourceX >= 0 && sourceX < width && sourceY >= 0 && sourceY < height)
                    result[y * width + x] = source[sourceY * width + sourceX];
            }
        }
        return result;
    }

    private static double EstimateSkew(byte[] source, int width, int height,
        CancellationToken cancellationToken)
    {
        int stride = Math.Max(1, source.Length / 250_000);
        var dark = new List<(int X, int Y)>();
        for (int index = 0; index < source.Length; index += stride)
        {
            if ((index & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (source[index] < 192) dark.Add((index % width, index / width));
        }
        if (dark.Count < 8) return 0;
        double bestAngle = 0, bestScore = double.NegativeInfinity;
        var rows = new int[height + width / 3 + 4];
        int offset = width / 6 + 2;
        for (int step = -20; step <= 20; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(rows);
            double angle = step * 0.5;
            double tangent = Math.Tan(angle * Math.PI / 180);
            foreach ((int x, int y) in dark)
            {
                int row = (int)Math.Round(y - x * tangent) + offset;
                if ((uint)row < (uint)rows.Length) rows[row]++;
            }
            double score = rows.Sum(count => (double)count * count);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }
        return bestAngle;
    }

    private static byte[] Median3x3(byte[] source, int width, int height,
        CancellationToken cancellationToken)
    {
        byte[] result = source.ToArray();
        Span<byte> values = stackalloc byte[9];
        for (int y = 1; y < height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 1; x < width - 1; x++)
            {
                int count = 0;
                for (int yy = y - 1; yy <= y + 1; yy++)
                    for (int xx = x - 1; xx <= x + 1; xx++)
                        values[count++] = source[yy * width + xx];
                values.Sort();
                result[y * width + x] = values[4];
            }
        }
        return result;
    }
}
