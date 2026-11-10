using System.Buffers.Binary;

namespace KillerPdf.Engine.Formats.Djvu;

internal static class DjvuIw44Decoder
{
    private const long MaximumWorkingBytes = 256L * 1024 * 1024;
    private const long MaximumWork = 2_000_000_000;
    private const int MaximumSlices = 4096;

    // Reconstruction work per padded pixel and component: the move out of block order, about 2.7 lifting steps
    // across the five scales, and the output sample.
    private const int ReconstructionWorkPerPixel = 8;

    // DjVu v3 section 10.4: the initial step of each of the 16 quantization groups. Groups 0 to 6 are the
    // coefficients of band 0; group 6 + b is band b.
    private static readonly int[] InitialSteps = [0x4000, 0x8000, 0x8000, 0x10000, 0x10000, 0x10000, 0x20000, 0x20000,
        0x20000, 0x40000, 0x40000, 0x40000, 0x80000, 0x40000, 0x40000, 0x80000];

    private static ReadOnlySpan<int> FirstBuckets => [0, 1, 2, 3, 4, 8, 12, 16, 32, 48];
    private static ReadOnlySpan<int> BucketCounts => [1, 1, 1, 1, 4, 4, 4, 16, 16, 16];

    // Where coefficient i of a 32 x 32 block sits: its bits alternate column and row, most significant last.
    private static readonly int[] BlockColumn = new int[1024], BlockRow = new int[1024];

    static DjvuIw44Decoder()
    {
        for (int i = 0; i < 1024; i++)
            for (int bit = 0; bit < 5; bit++)
            {
                BlockColumn[i] |= ((i >> (2 * bit)) & 1) << (4 - bit);
                BlockRow[i] |= ((i >> (2 * bit + 1)) & 1) << (4 - bit);
            }
    }

    /// <summary>
    /// The bytes a grayscale decode holds at once: 16-bit coefficients for the page padded to whole blocks, one
    /// block row of them while they move into raster order, the gray output and what the caller builds from it, plus
    /// the copied chunks and fixed overhead.
    /// </summary>
    internal static long GrayscaleWorkingBytes(int width, int height, long payloadBytes, int chunkCount, int callerBytesPerPixel)
        => CoefficientBytes(width, height) + BlockRowBytes(width) + (long)width * height * (1 + callerBytesPerPixel)
            + payloadBytes + 2048 + chunkCount * 128L;

    /// <summary>
    /// The bytes a color decode holds at once: three 16-bit coefficient sets and the first eight-bit plane while
    /// the luminance is reconstructed, or later the three planes and the RGB output, plus the copied chunks, what the
    /// caller builds per pixel, context banks and array overhead.
    /// </summary>
    internal static long ColorWorkingBytes(int width, int height, long payloadBytes, int callerBytesPerPixel = 32)
    {
        long pixels = (long)width * height;
        long peak = Math.Max(3 * CoefficientBytes(width, height) + BlockRowBytes(width) + pixels, 6 * pixels);
        return peak + pixels * callerBytesPerPixel + payloadBytes + 4096;
    }

    private static long CoefficientBytes(int width, int height) => (long)((width + 31) / 32) * ((height + 31) / 32) * 1024 * sizeof(short);

    private static long BlockRowBytes(int width) => (long)((width + 31) / 32) * 1024 * sizeof(short);

    /// <summary>
    /// The coefficient visits <paramref name="slices"/> slices make over <paramref name="blocks"/> blocks. A slice
    /// whose band has no step between 0 and 0x8000 decodes nothing and is skipped, and every step reaches zero
    /// after 200 slices, so later slices cost nothing either.
    /// </summary>
    private static long SliceWork(int slices, long blocks)
    {
        Span<int> steps = stackalloc int[16];
        InitialSteps.CopyTo(steps);
        long work = 0;
        for (int slice = 0; slice < Math.Min(slices, 200); slice++)
        {
            int band = slice % 10;
            if (!IsNullSlice(band, steps)) work += blocks * BucketCounts[band] * 16;
            HalveSteps(band, steps);
        }
        return work;
    }

    // callerBytesPerPixel is what the caller builds from the gray bytes per pixel, counted with the decoder's own
    // buffers: 32 for a float RGBA frame and its owned copy, 0 when the gray bytes are kept as they are.
    internal static byte[] DecodeGrayscale(IReadOnlyList<ReadOnlyMemory<byte>> payloads, int width, int height, CancellationToken token,
        int callerBytesPerPixel = 32)
    {
        token.ThrowIfCancellationRequested();
        if (payloads.Count is < 1 or > 256) throw new InvalidDataException("Invalid IW44 chunk count.");
        var payload = payloads[0].Span;
        if (payload.Length < 9) throw new InvalidDataException("Truncated IW44 initial chunk.");
        if (payload[0] != 0) throw new InvalidDataException("IW44 image must begin with serial zero.");
        if ((payload[2] & 0x80) == 0) throw new NotSupportedException("Color IW44 decoding is not supported.");
        if ((payload[2] & 0x7f) != 1 || payload[3] != 2)
            throw new NotSupportedException("This IW44 version is not supported.");
        if (BinaryPrimitives.ReadUInt16BigEndian(payload[4..]) != width ||
            BinaryPrimitives.ReadUInt16BigEndian(payload[6..]) != height)
            throw new NotSupportedException("Only full-resolution IW44 background images are supported.");
        var (slices, payloadBytes) = ScanChunks(payloads, token);
        int blockColumns = (width + 31) / 32, blockRows = (height + 31) / 32;
        long blocks = (long)blockColumns * blockRows;
        long workingBytes = GrayscaleWorkingBytes(width, height, payloadBytes, payloads.Count, callerBytesPerPixel);
        long work = SliceWork(slices, blocks) + blocks * 1024 * ReconstructionWorkPerPixel;
        if (slices > MaximumSlices || workingBytes > MaximumWorkingBytes || work > MaximumWork)
            throw new InvalidDataException("IW44 image exceeds the combined storage or coefficient-work limit.");
        var plane = new Plane(blocks);
        for (var serial = 0; serial < payloads.Count; serial++)
        {
            token.ThrowIfCancellationRequested();
            var chunk = payloads[serial].Span;
            var arithmetic = new DjvuZpDecoder(chunk[(serial == 0 ? 9 : 2)..].ToArray(), plane.Contexts, token, callerBoundsDecisions: true);
            for (int slice = 0; slice < chunk[1]; slice++) DecodeSlice(plane, arithmetic, token);
        }
        Reconstruct(plane, blockColumns, width, height, halfResolution: false, token);
        var output = new byte[width * height];
        var samples = plane.Coefficients!;
        int paddedWidth = blockColumns * 32;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int source = y * paddedWidth, target = (height - 1 - y) * width;
            for (var x = 0; x < width; x++)
                output[target + x] = (byte)(127 - Math.Clamp((samples[source + x] + 32) >> 6, -128, 127));
        }
        return output;
    }

    internal static byte[] DecodeColor(IReadOnlyList<ReadOnlyMemory<byte>> payloads, int width, int height, CancellationToken token,
        int callerBytesPerPixel = 32)
    {
        token.ThrowIfCancellationRequested();
        if (payloads.Count is < 1 or > 256) throw new InvalidDataException("Invalid IW44 chunk count.");
        var payload = payloads[0].Span;
        if (payload.Length < 9) throw new InvalidDataException("Truncated color IW44 initial chunk.");
        if (payload[0] != 0) throw new InvalidDataException("IW44 image must begin with serial zero.");
        if (payload[2] != 1 || payload[3] != 2)
            throw new NotSupportedException("This color IW44 version is not supported.");
        if (BinaryPrimitives.ReadUInt16BigEndian(payload[4..]) != width ||
            BinaryPrimitives.ReadUInt16BigEndian(payload[6..]) != height)
            throw new NotSupportedException("Only full-resolution color IW44 background images are supported.");
        if (width <= 0 || height <= 0) throw new InvalidDataException("A color IW44 image needs a width and a height.");
        // DjVu v3 section 10.3, version 1.2: the low seven bits of the last header byte are the number of slices
        // coded before chrominance starts, and a clear high bit codes chrominance at half resolution.
        int chromaDelay = payload[8] & 0x7f;
        bool halfChroma = (payload[8] & 0x80) == 0;
        var (slices, payloadBytes) = ScanChunks(payloads, token);
        int blockColumns = (width + 31) / 32, blockRows = (height + 31) / 32;
        long blocks = (long)blockColumns * blockRows;
        long pixelCount = (long)width * height;
        long workingBytes = ColorWorkingBytes(width, height, payloadBytes, callerBytesPerPixel);
        long work = SliceWork(slices, blocks) + 2 * SliceWork(Math.Max(0, slices - chromaDelay), blocks)
            + 3 * blocks * 1024 * ReconstructionWorkPerPixel;
        if (slices > MaximumSlices || workingBytes > MaximumWorkingBytes || work > MaximumWork)
            throw new InvalidDataException("IW44 color image exceeds the combined storage or coefficient-work limit.");
        var planes = new Plane[3];
        for (int component = 0; component < 3; component++) planes[component] = new Plane(blocks);
        var activeContexts = new byte[98];
        // Each slice codes Y, then Cb and Cr once the chroma delay has passed. Every chunk restarts the arithmetic
        // decoder; each component keeps its own context bank, and switching banks leaves the arithmetic interval and
        // input position unchanged.
        int codedSlices = 0;
        for (var serial = 0; serial < payloads.Count; serial++)
        {
            token.ThrowIfCancellationRequested();
            var chunk = payloads[serial].Span;
            var arithmetic = new DjvuZpDecoder(chunk[(serial == 0 ? 9 : 2)..].ToArray(), activeContexts, token, callerBoundsDecisions: true);
            for (int slice = 0; slice < chunk[1]; slice++, codedSlices++)
                for (int component = 0; component < (codedSlices >= chromaDelay ? 3 : 1); component++)
                {
                    planes[component].Contexts.CopyTo(activeContexts, 0);
                    DecodeSlice(planes[component], arithmetic, token);
                    activeContexts.CopyTo(planes[component].Contexts, 0);
                }
        }
        // One component at a time: reconstruct it in place, keep its signed eight-bit samples, drop its coefficients.
        var signed = new byte[3][];
        for (int component = 0; component < 3; component++)
        {
            token.ThrowIfCancellationRequested();
            bool half = component > 0 && halfChroma;
            Reconstruct(planes[component], blockColumns, width, height, half, token);
            signed[component] = SignedSamples(planes[component].Coefficients!, blockColumns * 32, width, height, half, token);
            planes[component].Coefficients = null;
        }
        var rgb = new byte[(int)pixelCount * 3];
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            if ((pixel & 0xffff) == 0) token.ThrowIfCancellationRequested();
            int y = (sbyte)signed[0][pixel], cb = (sbyte)signed[1][pixel], cr = (sbyte)signed[2][pixel];
            // Section 10.6.7 states R = Y + 1.5Cr, G = Y - Cb/4 - 0.75Cr and B = Y + 1.75Cb
            // without an integer rounding rule. DjVuLibre's integer form floors each
            // partial term, and its eight-bit output is the compatibility reference.
            int quarterBlue = cb >> 2, red = cr + (cr >> 1), shared = y + 128 - quarterBlue;
            rgb[pixel * 3] = (byte)Math.Clamp(y + 128 + red, 0, 255);
            rgb[pixel * 3 + 1] = (byte)Math.Clamp(shared - (red >> 1), 0, 255);
            rgb[pixel * 3 + 2] = (byte)Math.Clamp(shared + (cb << 1), 0, 255);
        }
        token.ThrowIfCancellationRequested();
        return rgb;
    }

    // Every chunk's two-byte header: consecutive serial numbers from zero and a slice count.
    private static (int Slices, long PayloadBytes) ScanChunks(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken token)
    {
        var slices = 0;
        long payloadBytes = 0;
        for (var serial = 0; serial < payloads.Count; serial++)
        {
            token.ThrowIfCancellationRequested();
            var chunk = payloads[serial].Span;
            if (chunk.Length < 2) throw new InvalidDataException("Truncated IW44 continuation header.");
            if (chunk[0] != serial) throw new InvalidDataException("IW44 chunk serial numbers must be consecutive.");
            slices += chunk[1];
            payloadBytes += chunk.Length;
        }
        return (slices, payloadBytes);
    }

    // One component's coefficients, kept as 16-bit values in block order (1024 per 32 x 32 block, row-major blocks)
    // until reconstruction moves them into raster order in place, with its quantization steps, context bank and
    // slice count.
    private sealed class Plane(long blocks)
    {
        internal short[]? Coefficients = new short[blocks * 1024];
        internal readonly int[] Steps = (int[])InitialSteps.Clone();
        internal readonly byte[] Contexts = new byte[98];
        internal int Slices;
    }

    private static bool IsNullSlice(int band, ReadOnlySpan<int> steps)
    {
        if (band != 0) return steps[6 + band] is <= 0 or >= 0x8000;
        for (int group = 0; group < 7; group++)
            if (steps[group] is > 0 and < 0x8000) return false;
        return true;
    }

    private static void HalveSteps(int band, Span<int> steps)
    {
        if (band == 0) for (var i = 0; i < 7; i++) steps[i] /= 2;
        else steps[6 + band] /= 2;
    }

    // Decodes the plane's next slice. A null slice codes no decisions and changes no coefficient, so only its steps move.
    private static void DecodeSlice(Plane plane, DjvuZpDecoder arithmetic, CancellationToken token)
    {
        int band = plane.Slices % 10;
        plane.Slices++;
        if (!IsNullSlice(band, plane.Steps)) DecodeBand(plane.Coefficients!, plane.Steps, band, arithmetic, token);
        HalveSteps(band, plane.Steps);
    }

    private static void DecodeBand(short[] coefficients, int[] steps, int band, DjvuZpDecoder arithmetic, CancellationToken token)
    {
        int firstBucket = FirstBuckets[band], bucketCount = BucketCounts[band];
        int first = firstBucket * 16, count = bucketCount * 16;
        Span<int> bandSteps = stackalloc int[count];
        for (int k = 0; k < count; k++) bandSteps[k] = steps[StepIndex(first + k)];
        Span<byte> flags = stackalloc byte[count];
        Span<byte> bucketFlags = stackalloc byte[16];
        for (var block = 0; block < coefficients.Length; block += 1024)
        {
            token.ThrowIfCancellationRequested();
            var values = coefficients.AsSpan(block + first, count);
            byte blockFlags = 0;
            bucketFlags.Clear();
            // Flags describe the state before any update in this block band.
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                for (var j = 0; j < 16; j++)
                {
                    int k = bucket * 16 + j;
                    var flag = bandSteps[k] is > 0 and < 0x8000 ? values[k] == 0 ? (byte)1 : (byte)2 : (byte)0;
                    flags[k] = flag;
                    bucketFlags[bucket] |= flag;
                }
                blockFlags |= bucketFlags[bucket];
            }
            var decodeBuckets = bucketCount < 16 || (blockFlags & 2) != 0 ||
                (blockFlags & 1) != 0 && arithmetic.Bit(0) != 0;
            if (decodeBuckets)
            {
                for (var bucket = 0; bucket < bucketCount; bucket++)
                {
                    if ((bucketFlags[bucket] & 1) == 0) continue;
                    var activeParents = 0;
                    if (band != 0)
                    {
                        var parent = block + (firstBucket + bucket) * 4;
                        for (var j = 0; j < 4; j++)
                            if (coefficients[parent + j] != 0) activeParents++;
                        activeParents = Math.Min(3, activeParents);
                    }
                    var context = 1 + band * 8 + ((blockFlags & 2) != 0 ? 4 : 0) + activeParents;
                    if (arithmetic.Bit(context) != 0) bucketFlags[bucket] |= 4;
                }
                for (var bucket = 0; bucket < bucketCount; bucket++)
                {
                    if ((bucketFlags[bucket] & 4) == 0) continue;
                    var potential = 0;
                    var start = bucket * 16;
                    for (var j = 0; j < 16; j++) if ((flags[start + j] & 1) != 0) potential++;
                    for (var j = 0; j < 16; j++)
                    {
                        if ((flags[start + j] & 1) == 0) continue;
                        var context = 81 + ((bucketFlags[bucket] & 2) != 0 ? 8 : 0) + Math.Min(7, potential);
                        if (arithmetic.Bit(context) != 0)
                        {
                            var sign = arithmetic.Bit() == 0 ? 1 : -1;
                            values[start + j] = (short)(sign * (3 * bandSteps[start + j] / 2));
                            potential = 0;
                        }
                        if (potential > 0) potential--;
                    }
                }
            }
            for (var k = 0; k < count; k++)
            {
                if ((flags[k] & 2) == 0) continue;
                var step = bandSteps[k];
                int coefficient = values[k];
                var magnitude = Math.Abs(coefficient);
                var increase = arithmetic.Bit(magnitude <= 3 * step ? 97 : -1) != 0;
                magnitude += step == 1 ? increase ? 0 : -1 : increase ? step / 2 : -step / 2;
                values[k] = (short)(coefficient < 0 ? -magnitude : magnitude);
            }
        }
    }

    private static int StepIndex(int coefficient) => coefficient < 4 ? coefficient :
        coefficient < 16 ? 4 + (coefficient - 4) / 4 :
        coefficient < 64 ? 7 + (coefficient - 16) / 16 :
        coefficient < 256 ? 10 + (coefficient - 64) / 64 : 13 + (coefficient - 256) / 256;

    // Moves the plane's coefficients from block order into raster order (rows padded to whole blocks) one block
    // row at a time, then runs the inverse wavelet transform in place in 16 bits, as DjVuLibre does.
    private static void Reconstruct(Plane plane, int blockColumns, int width, int height, bool halfResolution, CancellationToken token)
    {
        var data = plane.Coefficients!;
        int paddedWidth = blockColumns * 32, rowLength = blockColumns * 1024;
        Span<int> biasSteps = stackalloc int[1024];
        for (int i = 0; i < 1024; i++) biasSteps[i] = plane.Steps[StepIndex(i)];
        var row = new short[rowLength];
        for (int start = 0; start < data.Length; start += rowLength)
        {
            token.ThrowIfCancellationRequested();
            data.AsSpan(start, rowLength).CopyTo(row);
            for (int block = 0; block < blockColumns; block++)
            {
                int source = block * 1024, target = start + block * 32;
                for (int i = 0; i < 1024; i++)
                {
                    int value = row[source + i];
                    // Keep arithmetic interval centers unchanged. A sample that became significant in its band's
                    // last slice is the only one at exactly 3 times the current step (older ones exceed 4 times it),
                    // and it reconstructs at 3/8 of its interval until its first refinement.
                    if (value != 0 && Math.Abs(value) == 3 * biasSteps[i])
                        value = value > 0 ? value - biasSteps[i] / 4 : value + biasSteps[i] / 4;
                    data[target + BlockRow[i] * paddedWidth + BlockColumn[i]] = (short)value;
                }
            }
        }
        var line = new int[width];
        var evens = new int[(width + 1) / 2];
        for (var scale = 16; scale >= (halfResolution ? 2 : 1); scale /= 2)
        {
            TransformColumns(data, paddedWidth, scale, width, (height - 1) / scale + 1, token);
            for (var y = 0; y < height; y += scale)
                TransformRow(data, y * paddedWidth, scale, (width - 1) / scale + 1, line, evens, token);
        }
    }

    // Section 10.6.4 for every column at one scale, swept a row at a time. Lifting updates the even rows from the
    // odd rows k-3, k-1, k+1 and k+3, with zero for a missing neighbor, then interpolation updates the odd rows from
    // the lifted even rows. Values are stored in 16 bits like DjVuLibre's and wrap as they do there. At scale 1,
    // DjVuLibre 3.5.29's x86-64 build interpolates the generic rows of each whole group of four columns with MMX
    // code that saturates the correction to 16 bits before adding it, so those columns do the same; a correction
    // only leaves that range for data no encoder writes, and its output matches ddjvu's.
    private static void TransformColumns(short[] samples, int paddedWidth, int scale, int width, int count, CancellationToken token)
    {
        int rowStride = paddedWidth * scale, saturatedColumns = scale == 1 ? width & ~3 : 0;
        for (var k = 0; k < count; k += 2)
        {
            token.ThrowIfCancellationRequested();
            int at = k * rowStride;
            int previous3 = k >= 3 ? at - 3 * rowStride : -1, previous1 = k >= 1 ? at - rowStride : -1;
            int next1 = k + 1 < count ? at + rowStride : -1, next3 = k + 3 < count ? at + 3 * rowStride : -1;
            for (var x = 0; x < width; x += scale)
            {
                int a = (previous1 < 0 ? 0 : samples[previous1 + x]) + (next1 < 0 ? 0 : samples[next1 + x]);
                int b = (previous3 < 0 ? 0 : samples[previous3 + x]) + (next3 < 0 ? 0 : samples[next3 + x]);
                samples[at + x] = unchecked((short)(samples[at + x] - ((9 * a - b + 16) >> 5)));
            }
        }
        for (var k = 1; k < count; k += 2)
        {
            token.ThrowIfCancellationRequested();
            int at = k * rowStride, above = at - rowStride, below = at + rowStride;
            if (k >= 3 && k + 3 < count)
            {
                int above3 = at - 3 * rowStride, below3 = at + 3 * rowStride;
                for (var x = 0; x < width; x += scale)
                {
                    int correction = (9 * (samples[above + x] + samples[below + x]) - samples[above3 + x] - samples[below3 + x] + 8) >> 4;
                    if (x < saturatedColumns) correction = Math.Clamp(correction, short.MinValue, short.MaxValue);
                    samples[at + x] = unchecked((short)(samples[at + x] + correction));
                }
            }
            else if (k + 1 < count)
                for (var x = 0; x < width; x += scale)
                    samples[at + x] = unchecked((short)(samples[at + x] + ((samples[above + x] + samples[below + x] + 1) >> 1)));
            else
                for (var x = 0; x < width; x += scale)
                    samples[at + x] = unchecked((short)(samples[at + x] + samples[above + x]));
        }
    }

    // Section 10.6.4 along one row at one scale. Lifting reads the odd neighbors k-3, k-1, k+1 and k+3 through a
    // sliding window. Printed step 3 substitutes zero for every missing neighbor, as columns do. DjVuLibre 3.5.29
    // output differs for rows: when the far neighbor of lifted sample 2 or 4 is missing, the window keeps its
    // previous value. Rows follow that black-box compatibility finding, measured against the reference decoder.
    // DjVuLibre keeps a row's lifted even samples in full integers for the interpolation and only stores them in
    // 16 bits, so a lifted value outside that range wraps in the stored row but not in its odd neighbors.
    private static void TransformRow(short[] samples, int origin, int stride, int count, int[] line, int[] evens, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        for (int k = 0; k < count; k++) line[k] = samples[origin + k * stride];
        int previous3 = 0, previous1 = 0, next1 = count > 1 ? line[1] : 0, next3 = count > 3 ? line[3] : 0;
        for (var k = 0; k < count; k += 2)
        {
            if (k > 0)
            {
                previous3 = previous1;
                previous1 = next1;
                next1 = next3;
                if (k >= 6 || k + 3 < count) next3 = k + 3 < count ? line[k + 3] : 0;
            }
            int lifted = line[k] - ((9 * (previous1 + next1) - previous3 - next3 + 16) >> 5);
            evens[k / 2] = lifted;
            samples[origin + k * stride] = unchecked((short)lifted);
        }
        for (var k = 1; k < count; k += 2)
        {
            int half = k / 2;
            var value = k >= 3 && k + 3 < count
                ? line[k] + ((9 * (evens[half] + evens[half + 1]) - evens[half - 1] - evens[half + 2] + 8) >> 4)
                : k + 1 < count ? line[k] + ((evens[half] + evens[half + 1] + 1) >> 1) : line[k] + evens[half];
            samples[origin + k * stride] = unchecked((short)value);
        }
    }

    // A component's reconstructed samples rounded to signed eight bits, rows turned to top-down.
    private static byte[] SignedSamples(short[] samples, int paddedWidth, int width, int height, bool halfResolution, CancellationToken token)
    {
        var output = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int source = (halfResolution ? y & ~1 : y) * paddedWidth, target = (height - 1 - y) * width;
            for (var x = 0; x < width; x++)
            {
                int value = samples[source + (halfResolution ? x & ~1 : x)];
                output[target + x] = unchecked((byte)(sbyte)Math.Clamp((value + 32) >> 6, -128, 127));
            }
        }
        return output;
    }
}
