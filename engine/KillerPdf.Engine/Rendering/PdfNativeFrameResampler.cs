namespace KillerPdf.Engine.Rendering;

/// <summary>How a compact raster stores its samples.</summary>
internal enum PdfNativeSampleLayout
{
    /// <summary>One bit per pixel, most significant bit first, each row padded to whole bytes.</summary>
    Bits,
    /// <summary>One byte per pixel.</summary>
    Gray,
    /// <summary>Three bytes per pixel, red, green and blue.</summary>
    Rgb,
    /// <summary>A one-bit mask over foreground and background layers, each pixel's color worked out when it is read.</summary>
    Compound
}

/// <summary>
/// A gray or RGB image stored at the page size divided by a whole reduction factor, rounded up, and read at page
/// resolution by linear interpolation between the nearest stored samples, centers aligned.
/// </summary>
internal sealed class PdfNativeImageLayer
{
    private readonly byte[] _samples;
    private readonly int _channels, _stride;
    private readonly int[] _column0, _column1, _row0, _row1;
    private readonly float[] _columnWeight, _rowWeight;

    internal PdfNativeImageLayer(byte[] samples, int width, int height, int channels, int reduction,
        int pageWidth, int pageHeight)
    {
        if (channels is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(channels));
        if (samples.Length < (long)width * height * channels) throw new ArgumentException("The samples do not cover the layer.", nameof(samples));
        _samples = samples;
        _channels = channels;
        _stride = width * channels;
        Width = width;
        Height = height;
        Reduction = reduction;
        (_column0, _column1, _columnWeight) = Axis(pageWidth, width, reduction);
        (_row0, _row1, _rowWeight) = Axis(pageHeight, height, reduction, height * reduction - pageHeight);
    }

    /// <summary>The stored samples, top row first, one or three bytes per pixel.</summary>
    internal ReadOnlySpan<byte> Samples => _samples.AsSpan(0, Width * Height * _channels);
    internal int Width { get; }
    internal int Height { get; }
    internal int Channels => _channels;
    /// <summary>Each stored sample covers this many page pixels across and down.</summary>
    internal int Reduction { get; }

    /// <summary>The bytes a layer of this size holds, with its interpolation tables.</summary>
    internal static long StorageBytes(int width, int height, int channels, int pageWidth, int pageHeight)
        => (long)width * height * channels + 12L * (pageWidth + pageHeight);

    private static (int[], int[], float[]) Axis(int page, int stored, int reduction, int offset = 0)
    {
        int[] first = new int[page], second = new int[page];
        float[] weight = new float[page];
        for (int i = 0; i < page; i++)
        {
            double position = (i + .5 + offset) / reduction - .5;
            int left = (int)Math.Floor(position);
            weight[i] = (float)(position - left);
            first[i] = Math.Clamp(left, 0, stored - 1);
            second[i] = Math.Clamp(left + 1, 0, stored - 1);
        }
        return (first, second, weight);
    }

    internal void Read(int x, int y, float[] linear, out double r, out double g, out double b)
    {
        float wx = _columnWeight[x], wy = _rowWeight[y];
        int top = _row0[y] * _stride, bottom = _row1[y] * _stride;
        int left = _column0[x] * _channels, right = _column1[x] * _channels;
        double Mix(int channel) =>
            (1 - wy) * ((1 - wx) * linear[_samples[top + left + channel]] + wx * linear[_samples[top + right + channel]])
            + wy * ((1 - wx) * linear[_samples[bottom + left + channel]] + wx * linear[_samples[bottom + right + channel]]);
        r = Mix(0);
        if (_channels == 1) { g = b = r; return; }
        g = Mix(1);
        b = Mix(2);
    }
}

/// <summary>
/// What a compound page's mask selects between: a background image (white when absent) and either a foreground image
/// or solid foreground colors, one color for the whole mask or one per pixel through a packed palette index plane.
/// </summary>
internal sealed class PdfNativeCompoundLayers
{
    internal PdfNativeCompoundLayers(PdfNativeImageLayer? background, PdfNativeImageLayer? foreground, byte[] palette,
        byte[]? indexes, int indexBits, int pageWidth)
    {
        if (palette.Length < 3 || palette.Length % 3 != 0) throw new ArgumentException("The palette needs whole RGB colors.", nameof(palette));
        if (indexes is not null && indexBits is not (1 or 2 or 4 or 8 or 16)) throw new ArgumentOutOfRangeException(nameof(indexBits));
        Background = background;
        Foreground = foreground;
        Palette = palette;
        Indexes = indexes;
        IndexBits = indexBits;
        IndexStride = (int)(((long)pageWidth * indexBits + 7) / 8);
    }

    internal PdfNativeImageLayer? Background { get; }
    internal PdfNativeImageLayer? Foreground { get; }
    /// <summary>RGB colors; the first is the foreground color when there is no index plane.</summary>
    internal byte[] Palette { get; }
    internal byte[]? Indexes { get; }
    internal int IndexBits { get; }
    internal int IndexStride { get; }

    /// <summary>The bytes an index plane of this many bits per pixel holds for a page.</summary>
    internal static long IndexBytes(int pageWidth, int pageHeight, int indexBits)
        => indexBits == 0 ? 0 : ((long)pageWidth * indexBits + 7) / 8 * pageHeight;

    internal int Index(int x, int y)
    {
        if (Indexes is null) return 0;
        long bit = (long)y * IndexStride * 8 + (long)x * IndexBits;
        if (IndexBits == 16) return (Indexes[bit >> 3] << 8) | Indexes[(bit >> 3) + 1];
        return (Indexes[bit >> 3] >> (8 - IndexBits - (int)(bit & 7))) & ((1 << IndexBits) - 1);
    }

    internal void SetIndex(int x, int y, int value)
    {
        long bit = (long)y * IndexStride * 8 + (long)x * IndexBits;
        if (IndexBits == 16)
        {
            Indexes![bit >> 3] = (byte)(value >> 8);
            Indexes[(bit >> 3) + 1] = (byte)value;
            return;
        }
        int shift = 8 - IndexBits - (int)(bit & 7), mask = ((1 << IndexBits) - 1) << shift;
        Indexes![bit >> 3] = (byte)((Indexes[bit >> 3] & ~mask) | (value << shift));
    }
}

/// <summary>
/// A decoded page kept at natural size in the form it was coded in, with a table giving each stored sample value its
/// linear channel value. The page's intrinsic quarter or half turn is applied when it is read, never by a copy.
/// </summary>
internal sealed class PdfNativeCompactRaster
{
    internal PdfNativeCompactRaster(PdfNativeSampleLayout layout, int storedWidth, int storedHeight, byte[] samples,
        float[] linear, int rotation)
        : this(layout, storedWidth, storedHeight, samples, linear, rotation, 1, storedWidth, storedHeight)
    {
    }

    /// <summary>
    /// A page decoded at a whole <paramref name="reduction"/>: each stored sample covers that many page pixels across
    /// and down from the page's stored top left corner, the last row and column only what is left of the page.
    /// </summary>
    internal PdfNativeCompactRaster(PdfNativeSampleLayout layout, int storedWidth, int storedHeight, byte[] samples,
        float[] linear, int rotation, int reduction, int pageWidth, int pageHeight)
    {
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation));
        if (reduction < 1 || (pageWidth + reduction - 1) / reduction != storedWidth
            || (pageHeight + reduction - 1) / reduction != storedHeight)
            throw new ArgumentOutOfRangeException(nameof(reduction));
        if (reduction > 1 && layout != PdfNativeSampleLayout.Rgb) throw new ArgumentOutOfRangeException(nameof(layout));
        Layout = layout;
        StoredWidth = storedWidth;
        StoredHeight = storedHeight;
        Reduction = reduction;
        PageWidth = rotation is 90 or 270 ? pageHeight : pageWidth;
        PageHeight = rotation is 90 or 270 ? pageWidth : pageHeight;
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

    /// <summary>A compound page: <paramref name="mask"/> holds one bit per pixel, rows padded to whole bytes.</summary>
    internal PdfNativeCompactRaster(int storedWidth, int storedHeight, byte[] mask, float[] linear, int rotation,
        PdfNativeCompoundLayers layers)
    {
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation));
        if (mask.Length < (long)(storedWidth + 7) / 8 * storedHeight)
            throw new ArgumentException("The mask does not cover the raster.", nameof(mask));
        if (linear.Length != 256) throw new ArgumentException("The linear table does not match the sample layout.", nameof(linear));
        Layout = PdfNativeSampleLayout.Compound;
        StoredWidth = storedWidth;
        StoredHeight = storedHeight;
        // A power of two, so a position splits back into its column and row with a mask and a shift.
        _rowShift = 0;
        while (1 << _rowShift < storedWidth) _rowShift++;
        RowUnits = 1 << _rowShift;
        Samples = mask;
        Linear = linear;
        Rotation = rotation;
        Layers = layers;
        Reduction = 1;
        PageWidth = rotation is 90 or 270 ? storedHeight : storedWidth;
        PageHeight = rotation is 90 or 270 ? storedWidth : storedHeight;
    }

    private readonly int _rowShift;
    internal PdfNativeCompoundLayers? Layers { get; }

    /// <summary>A compound pixel's linear color: the foreground where the mask is black, the background elsewhere.</summary>
    internal void Color(int position, out double r, out double g, out double b)
    {
        int x = position & (RowUnits - 1), y = position >> _rowShift;
        var layers = Layers!;
        int maskBit = y * ((StoredWidth + 7) / 8) * 8 + x;
        if ((Samples[maskBit >> 3] & (0x80 >> (maskBit & 7))) != 0)
        {
            if (layers.Foreground is { } foreground) { foreground.Read(x, y, Linear, out r, out g, out b); return; }
            int color = layers.Index(x, y) * 3;
            r = Linear[layers.Palette[color]];
            g = Linear[layers.Palette[color + 1]];
            b = Linear[layers.Palette[color + 2]];
            return;
        }
        if (layers.Background is { } background) { background.Read(x, y, Linear, out r, out g, out b); return; }
        r = g = b = 1;
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
    /// <summary>How many page pixels each stored sample covers across and down; 1 for a natural-size decode.</summary>
    internal int Reduction { get; }
    /// <summary>The displayed page size in pixels, which a reduced raster's samples cover.</summary>
    internal int PageWidth { get; }
    internal int PageHeight { get; }
    // Displayed columns run against stored ones for a half turn and a clockwise quarter turn (stored rows, bottom
    // up); displayed rows run against them for a half turn and a counterclockwise quarter turn (Offsets).
    internal bool ColumnsReversed => Rotation is 90 or 180;
    internal bool RowsReversed => Rotation is 180 or 270;

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
        => new(Width, Height, ToPixels(token), PdfFrameColorEncoding.LinearSrgbD65);

    /// <summary>The natural-size linear RGBA channels, so a caller can let the raster go before copying them.</summary>
    internal float[] ToPixels(CancellationToken token)
    {
        if (Reduction != 1) throw new InvalidOperationException("A reduced raster is drawn only by resampling.");
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
                if (Layout == PdfNativeSampleLayout.Compound)
                {
                    Color(position, out double r, out double g, out double b);
                    pixels[o] = (float)r;
                    pixels[o + 1] = (float)g;
                    pixels[o + 2] = (float)b;
                }
                else if (Layout == PdfNativeSampleLayout.Rgb)
                {
                    pixels[o] = Linear[Samples[position]];
                    pixels[o + 1] = Linear[Samples[position + 1]];
                    pixels[o + 2] = Linear[Samples[position + 2]];
                }
                else pixels[o] = pixels[o + 1] = pixels[o + 2] = Channel(position);
                pixels[o + 3] = 1;
            }
        }
        return pixels;
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
        => new(regionWidth, regionHeight, ResizePixels(source, width, height, left, top, regionWidth, regionHeight, token),
            PdfFrameColorEncoding.LinearSrgbD65);

    /// <summary>The region's linear RGBA channels from <see cref="Resize(PdfNativeCompactRaster, int, int, int, int, int, int, CancellationToken)"/>, before they are copied into a frame.</summary>
    internal static float[] ResizePixels(PdfNativeCompactRaster source, int width, int height,
        int left, int top, int regionWidth, int regionHeight, CancellationToken token)
    {
        var xs = source.Reduction == 1 ? MakeTaps(source.Width, width, left, regionWidth, token)
            : MakeCellTaps(source.Width, source.PageWidth, source.Reduction, source.ColumnsReversed, width, left, regionWidth, token);
        var ys = source.Reduction == 1 ? MakeTaps(source.Height, height, top, regionHeight, token)
            : MakeCellTaps(source.Height, source.PageHeight, source.Reduction, source.RowsReversed, height, top, regionHeight, token);
        var (columnOffsets, rowOffsets) = source.Offsets(Indexes(xs), Indexes(ys));
        int[][] columns = Positions(xs, columnOffsets), rows = Positions(ys, rowOffsets);
        bool rgb = source.Layout == PdfNativeSampleLayout.Rgb;
        bool compound = source.Layout == PdfNativeSampleLayout.Compound;
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
                    if (compound)
                    {
                        source.Color(position, out double cr, out double cg, out double cb);
                        r += cr * weight;
                        g += cg * weight;
                        b += cb * weight;
                    }
                    else if (rgb)
                    {
                        r += linear[samples[position]] * weight;
                        g += linear[samples[position + 1]] * weight;
                        b += linear[samples[position + 2]] * weight;
                    }
                    else r += source.Channel(position) * weight;
                }
                int o = (y * regionWidth + x) * 4;
                output[o] = (float)r;
                output[o + 1] = rgb || compound ? (float)g : (float)r;
                output[o + 2] = rgb || compound ? (float)b : (float)r;
                output[o + 3] = 1;
            }
        }
        return output;
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

    // Taps over a reduced raster's samples, by the page pixels each covers: output i spans page [i P / t, (i + 1) P / t)
    // and reads every sample cell it overlaps, weighted by the overlap. Cell k spans stored page pixels [k r,
    // min((k + 1) r, P)), so the last cell is partial; on a reversed axis displayed cell j is stored cell n - 1 - j,
    // measured from the far edge. Only reduction is supported: the decode is chosen with at least as many samples as
    // the output.
    private static Tap[][] MakeCellTaps(int cells, int page, int reduction, bool reversed, int target, int first,
        int count, CancellationToken token)
    {
        var result = new Tap[count][];
        for (int n = 0; n < count; n++)
        {
            token.ThrowIfCancellationRequested();
            var (start, end, low, high) = CellSpan(cells, page, reduction, reversed, target, first + n);
            var taps = new Tap[high - low];
            for (int k = low; k < high; k++)
            {
                double cellStart = (double)k * reduction, cellEnd = Math.Min((double)(k + 1) * reduction, page);
                taps[k - low] = new(reversed ? cells - 1 - k : k,
                    (Math.Min(end, cellEnd) - Math.Max(start, cellStart)) / (end - start));
            }
            result[n] = taps;
        }
        return result;
    }

    // Output i's span in stored page pixels and the stored cells [low, high) it overlaps.
    private static (double Start, double End, int Low, int High) CellSpan(int cells, int page, int reduction,
        bool reversed, int target, int i)
    {
        double start = (double)i * page / target, end = (double)(i + 1) * page / target;
        if (reversed) (start, end) = (page - end, page - start);
        int low = Math.Clamp((int)Math.Floor(start / reduction), 0, cells - 1);
        int high = Math.Clamp((int)Math.Ceiling(end / reduction), low + 1, cells);
        return (start, end, low, high);
    }

    /// <summary>
    /// <see cref="CheckWork(int, int, int, int, int, int, int, int)"/> for a raster decoded at a reduction, counting
    /// exactly the taps <see cref="MakeCellTaps"/> builds. Width and height are the displayed page's pixels.
    /// </summary>
    internal static void CheckReducedWork(int width, int height, int reduction, int rotation, int targetWidth,
        int targetHeight, int regionWidth, int regionHeight, int left = 0, int top = 0)
    {
        bool columnsReversed = rotation is 90 or 180, rowsReversed = rotation is 180 or 270;
        int cellsX = (width + reduction - 1) / reduction, cellsY = (height + reduction - 1) / reduction;
        long Taps(int cells, int page, bool reversed, int target, int first, int count)
        {
            long taps = 0;
            for (int i = first; i < first + count; i++)
            {
                var (_, _, low, high) = CellSpan(cells, page, reduction, reversed, target, i);
                taps += high - low;
            }
            return taps;
        }
        if (checked(Taps(cellsX, width, columnsReversed, targetWidth, left, regionWidth)
                * Taps(cellsY, height, rowsReversed, targetHeight, top, regionHeight)) > 64_000_000)
            throw new InvalidDataException("Native resampling exceeds the weighted work limit.");
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
