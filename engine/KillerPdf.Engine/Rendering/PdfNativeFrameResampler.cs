namespace KillerPdf.Engine.Rendering;

internal static class PdfNativeFrameResampler
{
    private readonly record struct Tap(int Index, double Weight);

    internal static void CheckWork(int width, int height, int targetWidth, int targetHeight)
        => CheckWork(width, height, targetWidth, targetHeight, targetWidth, targetHeight);

    // A region of the target costs only its own rows and columns.
    internal static void CheckWork(int width, int height, int targetWidth, int targetHeight,
        int regionWidth, int regionHeight)
    {
        static long Bound(int source, int target, int region) => source > target
            ? checked((long)region * (((long)source + target - 1) / target + 1))
            : checked(2L * region);
        if (checked(Bound(width, targetWidth, regionWidth) * Bound(height, targetHeight, regionHeight)) > 64_000_000)
            throw new InvalidDataException("Native resampling exceeds the weighted work limit.");
    }

    internal static PdfRenderedFrame Resize(PdfRenderedFrame source, int width, int height, CancellationToken token)
        => Resize(source, width, height, 0, 0, width, height, token);

    /// <summary>
    /// Resamples the source to width x height and keeps only the given region. Each output pixel's taps depend only on
    /// its whole-target position, so the region is exactly that crop of the whole resize.
    /// </summary>
    internal static PdfRenderedFrame Resize(PdfRenderedFrame source, int width, int height,
        int left, int top, int regionWidth, int regionHeight, CancellationToken token)
    {
        if (source.ColorEncoding != PdfFrameColorEncoding.LinearSrgbD65)
            throw new NotSupportedException("Native resampling requires linear sRGB channels.");
        var input = source.Pixels;
        for (int i = 3; i < input.Length; i += 4)
        {
            if ((i & 4095) == 3) token.ThrowIfCancellationRequested();
            if (input[i] != 1f) throw new NotSupportedException("Native resampling requires an opaque frame.");
        }
        var xs = MakeTaps(source.Width, width, left, regionWidth, token);
        var ys = MakeTaps(source.Height, height, top, regionHeight, token);
        var output = new float[checked(regionWidth * regionHeight * 4)];
        for (int y = 0; y < regionHeight; y++)
        for (int x = 0; x < regionWidth; x++)
        {
            token.ThrowIfCancellationRequested();
            double r = 0, g = 0, b = 0;
            foreach (var yt in ys[y])
            foreach (var xt in xs[x])
            {
                token.ThrowIfCancellationRequested();
                int i = (yt.Index * source.Width + xt.Index) * 4;
                double weight = yt.Weight * xt.Weight;
                r += input[i] * weight; g += input[i + 1] * weight; b += input[i + 2] * weight;
            }
            int o = (y * regionWidth + x) * 4;
            output[o] = (float)r; output[o + 1] = (float)g; output[o + 2] = (float)b; output[o + 3] = 1;
        }
        return new PdfRenderedFrame(regionWidth, regionHeight, output, PdfFrameColorEncoding.LinearSrgbD65);
    }

    private static Tap[][] MakeTaps(int source, int target, int first, int count, CancellationToken token)
    {
        var result = new Tap[count][];
        for (int n = 0; n < count; n++)
        {
            token.ThrowIfCancellationRequested();
            int i = first + n;
            if (source <= target)
            {
                double position = (i + .5) * source / target - .5;
                int left = (int)Math.Floor(position);
                double fraction = position - left;
                result[n] = [new(Math.Clamp(left, 0, source - 1), 1 - fraction),
                    new(Math.Clamp(left + 1, 0, source - 1), fraction)];
            }
            else
            {
                double start = (double)i * source / target, end = (double)(i + 1) * source / target;
                int firstSource = (int)Math.Floor(start), last = (int)Math.Ceiling(end);
                var taps = new Tap[last - firstSource];
                for (int j = firstSource; j < last; j++)
                {
                    token.ThrowIfCancellationRequested();
                    taps[j - firstSource] = new(j, (Math.Min(end, j + 1) - Math.Max(start, j)) / (end - start));
                }
                result[n] = taps;
            }
        }
        return result;
    }
}
