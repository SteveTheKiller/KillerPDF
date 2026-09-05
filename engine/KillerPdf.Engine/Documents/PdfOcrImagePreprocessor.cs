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

/// <summary>Prepares rendered BGRA32 pages for engine-owned OCR recognition.</summary>
public static class PdfOcrImagePreprocessor
{
    private const int MaximumDimension = 32_768;
    private const long MaximumInputBytes = 512L * 1024 * 1024;

    /// <summary>Converts and optionally cleans one rendered page without platform APIs.</summary>
    public static PdfOcrPreparedImage PrepareBgra(ReadOnlyMemory<byte> bgra, int width,
        int height, PdfOcrOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (width <= 0 || width > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0 || height > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(height));
        long expected = checked((long)width * height * 4);
        if (expected > MaximumInputBytes || bgra.Length != expected)
            throw new ArgumentException("The BGRA image length or dimensions are invalid.", nameof(bgra));

        byte[] gray = GC.AllocateUninitializedArray<byte>(checked(width * height));
        ReadOnlySpan<byte> source = bgra.Span;
        for (int pixel = 0; pixel < gray.Length; pixel++)
        {
            if ((pixel & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            int offset = pixel * 4;
            int alpha = source[offset + 3];
            int blue = (source[offset] * alpha + 255 * (255 - alpha) + 127) / 255;
            int green = (source[offset + 1] * alpha + 255 * (255 - alpha) + 127) / 255;
            int red = (source[offset + 2] * alpha + 255 * (255 - alpha) + 127) / 255;
            gray[pixel] = (byte)((29 * blue + 150 * green + 77 * red + 128) >> 8);
        }

        bool binary = false;
        if (options.RemoveBackground)
        {
            gray = AdaptiveThreshold(gray, width, height, cancellationToken);
            binary = true;
        }
        if (options.RemoveNoise)
            gray = Median3x3(gray, width, height, cancellationToken);
        if (options.Deskew)
            gray = Deskew(gray, width, height, cancellationToken);

        var diagnostics = new List<string>();
        if (options.CorrectOrientation)
            diagnostics.Add("OCR orientation detection is not implemented.");
        return new PdfOcrPreparedImage(width, height, gray, binary, diagnostics);
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
