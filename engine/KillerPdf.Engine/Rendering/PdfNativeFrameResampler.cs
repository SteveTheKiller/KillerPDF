namespace KillerPdf.Engine.Rendering;

/// <summary>How a compact raster stores its samples.</summary>
internal enum PdfNativeSampleLayout
{
    /// <summary>One bit per pixel, most significant bit first, each row padded to whole bytes.</summary>
    Bits,
    /// <summary>One byte per pixel.</summary>
    Gray,
    /// <summary>Three bytes per pixel, red, green and blue.</summary>
    Rgb
}

/// <summary>
/// A decoded page kept at natural size in the form it was coded in, with a table giving each stored sample value its
/// linear channel value. The page's intrinsic quarter or half turn is applied when it is read, never by a copy.
/// </summary>
internal sealed class PdfNativeCompactRaster
{
    internal PdfNativeCompactRaster(PdfNativeSampleLayout layout, int storedWidth, int storedHeight, byte[] samples,
        float[] linear, int rotation)
    {
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation));
        Layout = layout;
        StoredWidth = storedWidth;
        StoredHeight = storedHeight;
        RowUnits = layout switch
        {
            PdfNativeSampleLayout.Bits => (storedWidth + 7) / 8 * 8,
            PdfNativeSampleLayout.Gray => storedWidth,
            _ => storedWidth * 3
        };
        if (samples.Length < (layout == PdfNativeSampleLayout.Bits ? (long)RowUnits / 8 : RowUnits) * storedHeight)
            throw new ArgumentException("The samples do not cover the raster.", nameof(samples));
        if (linear.Length != (layout == PdfNativeSampleLayout.Bits ? 2 : 256))
            throw new ArgumentException("The linear table does not match the sample layout.", nameof(linear));
        Samples = samples;
        Linear = linear;
        Rotation = rotation;
    }

    internal PdfNativeSampleLayout Layout { get; }
    internal int StoredWidth { get; }
    internal int StoredHeight { get; }
    // Distance between rows in sample units: bits for Bits, bytes otherwise.
    internal int RowUnits { get; }
    internal byte[] Samples { get; }
    internal float[] Linear { get; }
    /// <summary>The clockwise turn from stored to displayed orientation, as the DjVu INFO flags give it.</summary>
    internal int Rotation { get; }
    internal int Width => Rotation is 90 or 270 ? StoredHeight : StoredWidth;
    internal int Height => Rotation is 90 or 270 ? StoredWidth : StoredHeight;

    /// <summary>The bytes the raster holds.</summary>
    internal static long StorageBytes(PdfNativeSampleLayout layout, int storedWidth, int storedHeight) => layout switch
    {
        PdfNativeSampleLayout.Bits => (long)(storedWidth + 7) / 8 * storedHeight + 2 * sizeof(float),
        PdfNativeSampleLayout.Gray => (long)storedWidth * storedHeight + 256 * sizeof(float),
        _ => 3L * storedWidth * storedHeight + 256 * sizeof(float)
    };

    // A displayed column (or row) adds a fixed amount to the stored sample position, so positions split by axis.
    internal (int[] Column, int[] Row) Offsets(int[] columns, int[] rows)
    {
        int unit = Layout == PdfNativeSampleLayout.Rgb ? 3 : 1;
        int Stored(int x, int y) => y * RowUnits + x * unit;
        int[] column = new int[columns.Length], row = new int[rows.Length];
        for (int n = 0; n < columns.Length; n++)
        {
            int x = columns[n];
            column[n] = Rotation switch
            {
                // Clockwise: displayed (x, y) shows stored (y, H - 1 - x).
                90 => Stored(0, StoredHeight - 1 - x),
                // Counterclockwise: displayed (x, y) shows stored (W - 1 - y, x).
                270 => Stored(0, x),
                180 => Stored(StoredWidth - 1 - x, 0),
                _ => Stored(x, 0)
            };
        }
        for (int n = 0; n < rows.Length; n++)
        {
            int y = rows[n];
            row[n] = Rotation switch
            {
                90 => Stored(y, 0),
                270 => Stored(StoredWidth - 1 - y, 0),
                180 => Stored(0, StoredHeight - 1 - y),
                _ => Stored(0, y)
            };
        }
        return (column, row);
    }

    internal float Channel(int position) => Layout == PdfNativeSampleLayout.Bits
        ? Linear[(Samples[position >> 3] >> (7 - (position & 7))) & 1]
        : Linear[Samples[position]];

    /// <summary>Expands the raster to an opaque linear RGBA float frame at natural size.</summary>
    internal PdfRenderedFrame ToFrame(CancellationToken token)
    {
        int width = Width, height = Height;
        int[] columns = new int[width], rows = new int[height];
        for (int x = 0; x < width; x++) columns[x] = x;
        for (int y = 0; y < height; y++) rows[y] = y;
        var (column, row) = Offsets(columns, rows);
        var pixels = new float[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int position = row[y] + column[x], o = (y * width + x) * 4;
                if (Layout == PdfNativeSampleLayout.Rgb)
                {
                    pixels[o] = Linear[Samples[position]];
                    pixels[o + 1] = Linear[Samples[position + 1]];
                    pixels[o + 2] = Linear[Samples[position + 2]];
                }
                else pixels[o] = pixels[o + 1] = pixels[o + 2] = Channel(position);
                pixels[o + 3] = 1;
            }
        }
        return new PdfRenderedFrame(width, height, pixels, PdfFrameColorEncoding.LinearSrgbD65);
    }
}

internal static class PdfNativeFrameResampler
{
    private readonly record struct Tap(int Index, double Weight);

    internal static void CheckWork(int width, int height, int targetWidth, int targetHeight)
        => CheckWork(width, height, targetWidth, targetHeight, targetWidth, targetHeight);

    // A region of the target costs only its own rows and columns. Region renders pass the region's first row and
    // column; without them the count covers the region at the target's origin.
    internal static void CheckWork(int width, int height, int targetWidth, int targetHeight,
        int regionWidth, int regionHeight, int left = 0, int top = 0)
    {
        // Exactly the taps MakeTaps builds: two per output when enlarging, every overlapped source pixel when reducing.
        static long Taps(int source, int target, int first, int count)
        {
            if (source <= target) return 2L * count;
            long taps = 0;
            for (int i = first; i < first + count; i++)
                taps += (long)Math.Ceiling((double)(i + 1) * source / target) - (long)Math.Floor((double)i * source / target);
            return taps;
        }
        if (checked(Taps(width, targetWidth, left, regionWidth) * Taps(height, targetHeight, top, regionHeight)) > 64_000_000)
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

    /// <summary>
    /// Resamples a compact raster to width x height and keeps only the given region, with the same taps, weights and
    /// summation order as the float overload, so it gives the float overload's output for the expanded raster.
    /// </summary>
    internal static PdfRenderedFrame Resize(PdfNativeCompactRaster source, int width, int height,
        int left, int top, int regionWidth, int regionHeight, CancellationToken token)
    {
        var xs = MakeTaps(source.Width, width, left, regionWidth, token);
        var ys = MakeTaps(source.Height, height, top, regionHeight, token);
        var (columnOffsets, rowOffsets) = source.Offsets(Indexes(xs), Indexes(ys));
        int[][] columns = Positions(xs, columnOffsets), rows = Positions(ys, rowOffsets);
        bool rgb = source.Layout == PdfNativeSampleLayout.Rgb;
        var linear = source.Linear;
        var samples = source.Samples;
        var output = new float[checked(regionWidth * regionHeight * 4)];
        for (int y = 0; y < regionHeight; y++)
        {
            var yTaps = ys[y];
            var yPositions = rows[y];
            for (int x = 0; x < regionWidth; x++)
            {
                token.ThrowIfCancellationRequested();
                var xTaps = xs[x];
                var xPositions = columns[x];
                double r = 0, g = 0, b = 0;
                for (int j = 0; j < yTaps.Length; j++)
                for (int k = 0; k < xTaps.Length; k++)
                {
                    int position = yPositions[j] + xPositions[k];
                    double weight = yTaps[j].Weight * xTaps[k].Weight;
                    if (rgb)
                    {
                        r += linear[samples[position]] * weight;
                        g += linear[samples[position + 1]] * weight;
                        b += linear[samples[position + 2]] * weight;
                    }
                    else r += source.Channel(position) * weight;
                }
                int o = (y * regionWidth + x) * 4;
                output[o] = (float)r;
                output[o + 1] = rgb ? (float)g : (float)r;
                output[o + 2] = rgb ? (float)b : (float)r;
                output[o + 3] = 1;
            }
        }
        return new PdfRenderedFrame(regionWidth, regionHeight, output, PdfFrameColorEncoding.LinearSrgbD65);
    }

    // The distinct source rows or columns the taps read, so their stored positions are computed once each.
    private static int[] Indexes(Tap[][] taps)
    {
        int count = 0;
        foreach (var list in taps) count += list.Length;
        var indexes = new int[count];
        int n = 0;
        foreach (var list in taps) foreach (var tap in list) indexes[n++] = tap.Index;
        return indexes;
    }

    private static int[][] Positions(Tap[][] taps, int[] offsets)
    {
        var result = new int[taps.Length][];
        int n = 0;
        for (int i = 0; i < taps.Length; i++)
        {
            result[i] = new int[taps[i].Length];
            for (int j = 0; j < taps[i].Length; j++) result[i][j] = offsets[n++];
        }
        return result;
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
