using System.Buffers.Binary;

namespace KillerPdf.Engine.Formats.Djvu;

internal static class DjvuIw44Decoder
{
    private const long MaximumWorkingBytes = 256L * 1024 * 1024;
    private const long MaximumCoefficientVisits = 512_000_000;

    internal static byte[] DecodeGrayscale(IReadOnlyList<ReadOnlyMemory<byte>> payloads, int width, int height, CancellationToken token)
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
        var blockColumns = (width + 31) / 32;
        var blockRows = (height + 31) / 32;
        var coefficientCount = (long)blockColumns * blockRows * 1024;
        var pixelCount = (long)width * height;
        // Includes coefficients, flags, reconstruction, gray output and both float-frame copies.
        var workingBytes = coefficientCount * 5 + pixelCount * 37 + payloadBytes + 2048 + payloads.Count * 128L;
        var visits = coefficientCount * (8L * slices + 1) + pixelCount * 20;
        if (slices > 4096 || workingBytes > MaximumWorkingBytes || visits > MaximumCoefficientVisits)
            throw new InvalidDataException("IW44 image exceeds the combined storage or coefficient-work limit.");
        var coefficients = new int[(int)coefficientCount];
        var flags = new byte[(int)coefficientCount];
        var contexts = new byte[98];
        int[] steps = [0x4000, 0x8000, 0x8000, 0x10000, 0x10000, 0x10000, 0x20000, 0x20000,
            0x20000, 0x40000, 0x40000, 0x40000, 0x80000, 0x40000, 0x40000, 0x80000];
        var firstSlice = 0;
        for (var serial = 0; serial < payloads.Count; serial++)
        {
            token.ThrowIfCancellationRequested();
            var chunk = payloads[serial].Span;
            var arithmetic = new DjvuZpDecoder(chunk[(serial == 0 ? 9 : 2)..].ToArray(), contexts, token, callerBoundsDecisions: true);
            DecodeSlices(coefficients, flags, steps, arithmetic, firstSlice, chunk[1], token);
            firstSlice += chunk[1];
        }
        return Reconstruct(coefficients, flags, steps, blockColumns, width, height, token);
    }

    private static void DecodeSlices(int[] coefficients, byte[] flags, int[] steps, DjvuZpDecoder arithmetic, int firstSlice, int slices, CancellationToken token)
    {
        ReadOnlySpan<int> firstBuckets = [0, 1, 2, 3, 4, 8, 12, 16, 32, 48];
        ReadOnlySpan<int> bucketCounts = [1, 1, 1, 1, 4, 4, 4, 16, 16, 16];
        Span<byte> bucketFlags = stackalloc byte[16];
        for (var slice = 0; slice < slices; slice++)
        {
            token.ThrowIfCancellationRequested();
            var band = (firstSlice + slice) % 10;
            var firstBucket = firstBuckets[band];
            var bucketCount = bucketCounts[band];
            for (var block = 0; block < coefficients.Length; block += 1024)
            {
                byte blockFlags = 0;
                bucketFlags.Clear();
                // Flags describe the state before any update in this block band.
                for (var bucket = 0; bucket < bucketCount; bucket++)
                {
                    for (var j = 0; j < 16; j++)
                    {
                        token.ThrowIfCancellationRequested();
                        var index = (firstBucket + bucket) * 16 + j;
                        var step = steps[StepIndex(index)];
                        var flag = step is > 0 and < 0x8000 ? coefficients[block + index] == 0 ? (byte)1 : (byte)2 : (byte)0;
                        flags[block + index] = flag;
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
                        token.ThrowIfCancellationRequested();
                        if ((bucketFlags[bucket] & 1) == 0) continue;
                        var activeParents = 0;
                        if (band != 0)
                        {
                            var parent = (firstBucket + bucket) * 4;
                            for (var j = 0; j < 4; j++)
                                if (coefficients[block + parent + j] != 0) activeParents++;
                            activeParents = Math.Min(3, activeParents);
                        }
                        var context = 1 + band * 8 + ((blockFlags & 2) != 0 ? 4 : 0) + activeParents;
                        if (arithmetic.Bit(context) != 0) bucketFlags[bucket] |= 4;
                    }
                    for (var bucket = 0; bucket < bucketCount; bucket++)
                    {
                        if ((bucketFlags[bucket] & 4) == 0) continue;
                        var potential = 0;
                        var start = block + (firstBucket + bucket) * 16;
                        for (var j = 0; j < 16; j++) if ((flags[start + j] & 1) != 0) potential++;
                        for (var j = 0; j < 16; j++)
                        {
                            token.ThrowIfCancellationRequested();
                            if ((flags[start + j] & 1) == 0) continue;
                            var context = 81 + ((bucketFlags[bucket] & 2) != 0 ? 8 : 0) + Math.Min(7, potential);
                            if (arithmetic.Bit(context) != 0)
                            {
                                var sign = arithmetic.Bit() == 0 ? 1 : -1;
                                coefficients[start + j] = sign * (3 * steps[StepIndex((firstBucket + bucket) * 16 + j)] / 2);
                                potential = 0;
                            }
                            if (potential > 0) potential--;
                        }
                    }
                }
                for (var i = firstBucket * 16; i < (firstBucket + bucketCount) * 16; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if ((flags[block + i] & 2) == 0) continue;
                    var step = steps[StepIndex(i)];
                    var coefficient = coefficients[block + i];
                    var magnitude = Math.Abs(coefficient);
                    var increase = arithmetic.Bit(magnitude <= 3 * step ? 97 : -1) != 0;
                    magnitude += step == 1 ? increase ? 0 : -1 : increase ? step / 2 : -step / 2;
                    coefficients[block + i] = coefficient < 0 ? -magnitude : magnitude;
                }
            }
            if (band == 0) for (var i = 0; i < 7; i++) steps[i] /= 2;
            else steps[6 + band] /= 2;
        }
    }

    private static int StepIndex(int coefficient) => coefficient < 4 ? coefficient :
        coefficient < 16 ? 4 + (coefficient - 4) / 4 :
        coefficient < 64 ? 7 + (coefficient - 16) / 16 :
        coefficient < 256 ? 10 + (coefficient - 64) / 64 : 13 + (coefficient - 256) / 256;

    private static byte[] Reconstruct(int[] coefficients, byte[] flags, int[] steps, int blockColumns, int width, int height, CancellationToken token)
    {
        var samples = new int[width * height];
        for (var block = 0; block < coefficients.Length / 1024; block++)
        {
            var blockX = block % blockColumns * 32;
            var blockY = block / blockColumns * 32;
            for (var i = 0; i < 1024; i++)
            {
                token.ThrowIfCancellationRequested();
                var x = 0;
                var y = 0;
                for (var bit = 0; bit < 5; bit++)
                {
                    x |= ((i >> (2 * bit)) & 1) << (4 - bit);
                    y |= ((i >> (2 * bit + 1)) & 1) << (4 - bit);
                }
                x += blockX;
                y += blockY;
                if (x < width && y < height)
                {
                    int index = block * 1024 + i;
                    int value = coefficients[index];
                    // Keep arithmetic interval centers unchanged. Newly significant samples
                    // reconstruct at 3/8 of their interval until their first refinement.
                    int bias = (flags[index] & 1) != 0 ? steps[StepIndex(i)] / 4 : 0;
                    samples[y * width + x] = value > 0 ? value - bias : value < 0 ? value + bias : 0;
                }
            }
        }
        for (var scale = 16; scale != 0; scale /= 2)
        {
            for (var x = 0; x < width; x += scale)
                TransformLine(samples, x, width * scale, (height - 1) / scale + 1, row: false, token);
            for (var y = 0; y < height; y += scale)
                TransformLine(samples, y * width, scale, (width - 1) / scale + 1, row: true, token);
        }
        var output = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var rounded = (samples[y * width + x] + 32L) >> 6;
                output[(height - 1 - y) * width + x] = (byte)(127 - Math.Clamp(rounded, -128, 127));
            }
        }
        return output;
    }

    private static void TransformLine(int[] samples, int origin, int stride, int count, bool row, CancellationToken token)
    {
        long At(int index) => index < 0 || index >= count ? 0 : samples[origin + index * stride];
        // Lifting reads the odd neighbors k-3, k-1, k+1 and k+3 through a sliding window.
        // Printed section 10.6.4 step 3 substitutes zero for every missing neighbor, and
        // columns follow it. DjVuLibre 3.5.29 output differs for rows: when the far neighbor
        // of lifted sample 2 or 4 is missing, the window keeps its previous value. Rows follow
        // that black-box compatibility finding, measured against the reference decoder.
        long previous3 = 0, previous1 = 0, next1 = At(1), next3 = At(3);
        for (var k = 0; k < count; k += 2)
        {
            token.ThrowIfCancellationRequested();
            if (k > 0)
            {
                previous3 = previous1;
                previous1 = next1;
                next1 = next3;
                if (!row || k >= 6 || k + 3 < count) next3 = At(k + 3);
            }
            var correction = (9 * (previous1 + next1) - previous3 - next3 + 16) >> 5;
            Store(k, At(k) - correction);
        }
        for (var k = 1; k < count; k += 2)
        {
            token.ThrowIfCancellationRequested();
            var value = k >= 3 && k + 3 < count
                ? At(k) + ((9 * (At(k - 1) + At(k + 1)) - At(k - 3) - At(k + 3) + 8) >> 4)
                : k + 1 < count ? At(k) + ((At(k - 1) + At(k + 1) + 1) >> 1) : At(k) + At(k - 1);
            Store(k, value);
        }

        void Store(int index, long value)
        {
            if (value is < int.MinValue or > int.MaxValue) throw new InvalidDataException("IW44 reconstruction overflow.");
            samples[origin + index * stride] = (int)value;
        }
    }
}
