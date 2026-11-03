namespace KillerPdf.Engine.Rendering;

internal static class PdfNativeFrameResampler
{
    private readonly record struct Tap(int Index, double Weight);

    internal static void CheckWork(int width, int height, int targetWidth, int targetHeight)
    {
        static long Bound(int source, int target) => source > target
            ? checked((long)target * (((long)source + target - 1) / target + 1))
            : checked(2L * target);
        if (checked(Bound(width, targetWidth) * Bound(height, targetHeight)) > 64_000_000)
            throw new InvalidDataException("Native resampling exceeds the weighted work limit.");
    }

    internal static PdfRenderedFrame Resize(PdfRenderedFrame source, int width, int height, CancellationToken token)
    {
        if (source.ColorEncoding != PdfFrameColorEncoding.LinearSrgbD65)
            throw new NotSupportedException("Native resampling requires linear sRGB channels.");
        var input = source.Pixels;
        for (int i = 3; i < input.Length; i += 4)
        {
            if ((i & 4095) == 3) token.ThrowIfCancellationRequested();
            if (input[i] != 1f) throw new NotSupportedException("Native resampling requires an opaque frame.");
        }
        var xs = MakeTaps(source.Width, width, token);
        var ys = MakeTaps(source.Height, height, token);
        var output = new float[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
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
            int o = (y * width + x) * 4;
            output[o] = (float)r; output[o + 1] = (float)g; output[o + 2] = (float)b; output[o + 3] = 1;
        }
        return new PdfRenderedFrame(width, height, output, PdfFrameColorEncoding.LinearSrgbD65);
    }

    private static Tap[][] MakeTaps(int source, int target, CancellationToken token)
    {
        var result = new Tap[target][];
        for (int i = 0; i < target; i++)
        {
            token.ThrowIfCancellationRequested();
            if (source <= target)
            {
                double position = (i + .5) * source / target - .5;
                int left = (int)Math.Floor(position);
                double fraction = position - left;
                result[i] = [new(Math.Clamp(left, 0, source - 1), 1 - fraction),
                    new(Math.Clamp(left + 1, 0, source - 1), fraction)];
            }
            else
            {
                double start = (double)i * source / target, end = (double)(i + 1) * source / target;
                int first = (int)Math.Floor(start), last = (int)Math.Ceiling(end);
                var taps = new Tap[last - first];
                for (int j = first; j < last; j++)
                {
                    token.ThrowIfCancellationRequested();
                    taps[j - first] = new(j, (Math.Min(end, j + 1) - Math.Max(start, j)) / (end - start));
                }
                result[i] = taps;
            }
        }
        return result;
    }
}
