using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace KillerPdf.Engine.Rendering;

// ICC lut8Type and lut16Type operate on normalized channel values. PCS encoding
// and profile connection belong to the caller, outside this table evaluator.
internal sealed class PdfIccLut : PdfIccTable
{
    private readonly ushort[] _samples;
    private readonly double[] _normalized;
    private readonly int _sampleBytes;
    private readonly int _grid;
    private readonly int _inputEntries;
    private readonly int _outputEntries;
    private readonly int _inputOffset;
    private readonly int _gridOffset;
    private readonly int _outputOffset;
    private readonly double[] _matrix;
    private readonly int[] _cornerOffsets;

    internal override int InputChannels { get; }
    internal override int OutputChannels { get; }
    internal override bool UsesLegacyLabEncoding => _sampleBytes == 2;
    internal override double XyzEncodingScale => _sampleBytes == 1 ? 255d / 128 : 65535d / 32768;

    internal PdfIccLut(ReadOnlyMemory<byte> data, PdfIccStorageBudget? storage = null)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes.Length < 48) throw new FormatException("An ICC lookup table is truncated.");
        _sampleBytes = bytes[..4].SequenceEqual("mft1"u8) ? 1
            : bytes[..4].SequenceEqual("mft2"u8) ? 2
            : throw new NotSupportedException("The ICC lookup-table type is not supported.");
        if (_sampleBytes == 2 && bytes.Length < 52)
            throw new FormatException("An ICC lookup table is truncated.");
        InputChannels = bytes[8];
        OutputChannels = bytes[9];
        _grid = bytes[10];
        if (InputChannels is < 1 or > 4 || OutputChannels is < 1 or > 4 || _grid < 2)
            throw new FormatException("An ICC lookup table has invalid channel or grid counts.");
        _inputEntries = _sampleBytes == 1 ? 256 : BinaryPrimitives.ReadUInt16BigEndian(bytes[48..]);
        _outputEntries = _sampleBytes == 1 ? 256 : BinaryPrimitives.ReadUInt16BigEndian(bytes[50..]);
        if (_inputEntries is < 2 or > 4096 || _outputEntries is < 2 or > 4096)
            throw new FormatException("An ICC lookup table has invalid curve lengths.");
        long cells = 1;
        for (int channel = 0; channel < InputChannels; channel++) cells *= _grid;
        int tableStart = _sampleBytes == 1 ? 48 : 52;
        long inputSamples = (long)_inputEntries * InputChannels;
        long gridSamples = cells * OutputChannels;
        long outputSamples = (long)_outputEntries * OutputChannels;
        long totalSamples = inputSamples + gridSamples + outputSamples;
        long end = tableStart + totalSamples * _sampleBytes;
        if (end > bytes.Length) throw new FormatException("An ICC lookup table is truncated.");
        _inputOffset = 0;
        _gridOffset = (int)inputSamples;
        _outputOffset = (int)(inputSamples + gridSamples);
        storage?.Reserve(totalSamples * sizeof(ushort));
        _samples = new ushort[totalSamples];
        _normalized = _sampleBytes == 1 ? NormalizedSamples.EightBit : NormalizedSamples.SixteenBit;
        ReadOnlySpan<byte> table = bytes[tableStart..(int)end];
        if (_sampleBytes == 1)
        {
            for (int index = 0; index < _samples.Length; index++)
                _samples[index] = table[index];
        }
        else
        {
            for (int index = 0; index < _samples.Length; index++)
                _samples[index] = BinaryPrimitives.ReadUInt16BigEndian(table[(index * 2)..]);
        }
        _cornerOffsets = new int[1 << InputChannels];
        for (int corner = 0; corner < _cornerOffsets.Length; corner++)
        {
            int cell = 0;
            for (int channel = 0; channel < InputChannels; channel++)
                cell = cell * _grid + ((corner >> channel) & 1);
            _cornerOffsets[corner] = cell * OutputChannels;
        }
        _matrix = new double[9];
        for (int index = 0; index < _matrix.Length; index++)
            _matrix[index] = BinaryPrimitives.ReadInt32BigEndian(bytes[(12 + index * 4)..]) / 65536d;
    }

    internal override void Transform(ReadOnlySpan<double> input, Span<double> output, bool applyXyzMatrix = false)
        => TransformCore(input, output, applyXyzMatrix, tetrahedral: false);

    internal void TransformTetrahedral(ReadOnlySpan<double> input, Span<double> output)
    {
        if (InputChannels != 3)
            throw new ArgumentException("Tetrahedral ICC interpolation requires three input channels.");
        TransformCore(input, output, applyXyzMatrix: false, tetrahedral: true);
    }

    private void TransformCore(ReadOnlySpan<double> input, Span<double> output, bool applyXyzMatrix, bool tetrahedral)
    {
        if (input.Length != InputChannels || output.Length != OutputChannels)
            throw new ArgumentException("ICC lookup-table channel counts do not match.");
        if (applyXyzMatrix && InputChannels != 3)
            throw new ArgumentException("An ICC XYZ matrix requires three input channels.");
        Span<double> values = stackalloc double[4];
        for (int channel = 0; channel < InputChannels; channel++)
        {
            if (!double.IsFinite(input[channel]))
                throw new ArgumentException("ICC lookup-table inputs must be finite.");
            values[channel] = Math.Clamp(input[channel], 0, 1);
        }
        if (applyXyzMatrix)
        {
            double x = values[0], y = values[1], z = values[2];
            for (int row = 0; row < 3; row++)
                values[row] = Math.Clamp(_matrix[row * 3] * x + _matrix[row * 3 + 1] * y
                    + _matrix[row * 3 + 2] * z, 0, 1);
        }
        int baseCell = 0;
        Span<double> fraction = stackalloc double[4];
        for (int channel = 0; channel < InputChannels; channel++)
        {
            double value = Curve(_inputOffset + channel * _inputEntries,
                _inputEntries, values[channel]) * (_grid - 1);
            int lower = Math.Min((int)value, _grid - 2);
            baseCell = baseCell * _grid + lower;
            fraction[channel] = value - lower;
        }
        Interpolate(baseCell, fraction, output, tetrahedral);
    }

    /// <summary>Per-channel grid positions for every 8-bit value of a four-channel table's inputs.</summary>
    internal sealed class ByteInput(int[] cells, double[] fractions, double[] grid, int[] cornerOffsets, double[] outputCurves)
    {
        internal int[] Cells { get; } = cells;
        internal double[] Fractions { get; } = fractions;
        // Normalized grid samples, the same doubles Transform reads through the shared
        // normalization table, four per grid point (the fourth is zero) for vector loads.
        internal double[] Grid { get; } = grid;
        internal int[] CornerOffsets { get; } = cornerOffsets;
        internal double[] OutputCurves { get; } = outputCurves;
    }

    // The input curves of an 8-bit CMYK table resolved once per channel and value, with the same
    // arithmetic as Transform, so TransformBytes returns exactly what Transform returns.
    internal ByteInput? PrepareByteInput()
    {
        if (InputChannels != 4 || OutputChannels != 3 || _outputOffset - _gridOffset > 1 << 20) return null;
        int points = (_outputOffset - _gridOffset) / 3;
        var grid = new double[points * 4];
        for (int point = 0; point < points; point++)
            for (int channel = 0; channel < 3; channel++)
                grid[point * 4 + channel] = _normalized[_samples[_gridOffset + point * 3 + channel]];
        var cornerOffsets = new int[16];
        for (int corner = 0; corner < 16; corner++) cornerOffsets[corner] = _cornerOffsets[corner] / 3 * 4;
        var outputCurves = new double[_outputEntries * OutputChannels];
        for (int index = 0; index < outputCurves.Length; index++) outputCurves[index] = _normalized[_samples[_outputOffset + index]];
        var cells = new int[4 * 256];
        var fractions = new double[4 * 256];
        int stride = _grid * _grid * _grid;
        for (int channel = 0; channel < 4; channel++)
        {
            for (int sample = 0; sample < 256; sample++)
            {
                double value = Curve(_inputOffset + channel * _inputEntries,
                    _inputEntries, Math.Clamp(sample / 255d, 0, 1)) * (_grid - 1);
                int lower = Math.Min((int)value, _grid - 2);
                cells[channel * 256 + sample] = lower * stride;
                fractions[channel * 256 + sample] = value - lower;
            }
            stride /= _grid;
        }
        return new ByteInput(cells, fractions, grid, cornerOffsets, outputCurves);
    }

    /// <summary>
    /// Transforms packed C, M, Y, K bytes (low to high) through a prepared table, with the same
    /// operations in the same order as Transform, so the result is identical.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void TransformBytes(ByteInput input, uint ink, Span<double> output)
    {
        int[] cells = input.Cells;
        double[] fractions = input.Fractions;
        int i0 = (byte)ink, i1 = 256 + (byte)(ink >> 8), i2 = 512 + (byte)(ink >> 16), i3 = 768 + (byte)(ink >> 24);
        int baseOffset = (cells[i0] + cells[i1] + cells[i2] + cells[i3]) * 3;
        double high0 = fractions[i0], high1 = fractions[i1], high2 = fractions[i2], high3 = fractions[i3];
        double low0 = 1 - high0, low1 = 1 - high1, low2 = 1 - high2, low3 = 1 - high3;
        // Products associate left to right as in Transform, so shared partial products give the
        // same weights. A zero weight adds exactly zero, so no corner needs skipping.
        double c00 = low0 * low1, c10 = high0 * low1, c01 = low0 * high1, c11 = high0 * high1;
        double d0 = c00 * low2, d1 = c10 * low2, d2 = c01 * low2, d3 = c11 * low2;
        double d4 = c00 * high2, d5 = c10 * high2, d6 = c01 * high2, d7 = c11 * high2;
        double[] grid = input.Grid;
        ref double cell = ref grid[baseOffset / 3 * 4];
        int[] corners = input.CornerOffsets;
        Vector256<double> sum = Vector256<double>.Zero;
        sum = Add(sum, d0 * low3, ref cell, corners[0]);
        sum = Add(sum, d1 * low3, ref cell, corners[1]);
        sum = Add(sum, d2 * low3, ref cell, corners[2]);
        sum = Add(sum, d3 * low3, ref cell, corners[3]);
        sum = Add(sum, d4 * low3, ref cell, corners[4]);
        sum = Add(sum, d5 * low3, ref cell, corners[5]);
        sum = Add(sum, d6 * low3, ref cell, corners[6]);
        sum = Add(sum, d7 * low3, ref cell, corners[7]);
        sum = Add(sum, d0 * high3, ref cell, corners[8]);
        sum = Add(sum, d1 * high3, ref cell, corners[9]);
        sum = Add(sum, d2 * high3, ref cell, corners[10]);
        sum = Add(sum, d3 * high3, ref cell, corners[11]);
        sum = Add(sum, d4 * high3, ref cell, corners[12]);
        sum = Add(sum, d5 * high3, ref cell, corners[13]);
        sum = Add(sum, d6 * high3, ref cell, corners[14]);
        sum = Add(sum, d7 * high3, ref cell, corners[15]);
        double x = sum.GetElement(0), y = sum.GetElement(1), z = sum.GetElement(2);

        // Multiply, then add: never fused, so each lane rounds as the scalar Transform does.
        static Vector256<double> Add(Vector256<double> sum, double weight, ref double cell, int offset) =>
            sum + Vector256.Create(weight) * Vector256.LoadUnsafe(ref cell, (nuint)offset);
        double[] curves = input.OutputCurves;
        output[0] = Curve(curves, 0, _outputEntries, Math.Clamp(x, 0, 1));
        output[1] = Curve(curves, _outputEntries, _outputEntries, Math.Clamp(y, 0, 1));
        output[2] = Curve(curves, 2 * _outputEntries, _outputEntries, Math.Clamp(z, 0, 1));

        static double Curve(double[] samples, int offset, int count, double value)
        {
            double position = value * (count - 1);
            int lower = Math.Min((int)position, count - 2);
            double fraction = position - lower;
            double first = samples[offset + lower];
            return first + fraction * (samples[offset + lower + 1] - first);
        }
    }

    private void Interpolate(int baseCell, ReadOnlySpan<double> fraction, Span<double> output, bool tetrahedral)
    {
        output.Clear();
        ushort[] samples = _samples;
        double[] normalized = _normalized;
        int baseOffset = _gridOffset + baseCell * OutputChannels;
        int corners = 1 << InputChannels;
        int outputs = OutputChannels;
        Span<double> cmykWeights = stackalloc double[16];
        if (InputChannels == 4)
        {
            double low0 = 1 - fraction[0], low1 = 1 - fraction[1];
            double low2 = 1 - fraction[2], low3 = 1 - fraction[3];
            double high0 = fraction[0], high1 = fraction[1];
            double high2 = fraction[2], high3 = fraction[3];
            cmykWeights[0] = low0 * low1 * low2 * low3;
            cmykWeights[1] = high0 * low1 * low2 * low3;
            cmykWeights[2] = low0 * high1 * low2 * low3;
            cmykWeights[3] = high0 * high1 * low2 * low3;
            cmykWeights[4] = low0 * low1 * high2 * low3;
            cmykWeights[5] = high0 * low1 * high2 * low3;
            cmykWeights[6] = low0 * high1 * high2 * low3;
            cmykWeights[7] = high0 * high1 * high2 * low3;
            cmykWeights[8] = low0 * low1 * low2 * high3;
            cmykWeights[9] = high0 * low1 * low2 * high3;
            cmykWeights[10] = low0 * high1 * low2 * high3;
            cmykWeights[11] = high0 * high1 * low2 * high3;
            cmykWeights[12] = low0 * low1 * high2 * high3;
            cmykWeights[13] = high0 * low1 * high2 * high3;
            cmykWeights[14] = low0 * high1 * high2 * high3;
            cmykWeights[15] = high0 * high1 * high2 * high3;
        }
        if (tetrahedral)
        {
            Span<int> order = [0, 1, 2];
            if (fraction[order[0]] < fraction[order[1]]) (order[0], order[1]) = (order[1], order[0]);
            if (fraction[order[1]] < fraction[order[2]]) (order[1], order[2]) = (order[2], order[1]);
            if (fraction[order[0]] < fraction[order[1]]) (order[0], order[1]) = (order[1], order[0]);
            double high = fraction[order[0]], middle = fraction[order[1]], low = fraction[order[2]];
            Span<double> weights = [1 - high, high - middle, middle - low, low];
            int corner = 0;
            for (int vertex = 0; vertex < 4; vertex++)
            {
                if (vertex > 0) corner |= 1 << order[vertex - 1];
                double weight = weights[vertex];
                if (weight == 0) continue;
                int offset = baseOffset + _cornerOffsets[corner];
                for (int channel = 0; channel < outputs; channel++)
                    output[channel] += weight * normalized[samples[offset + channel]];
            }
        }
        else
        {
            for (int corner = 0; corner < corners; corner++)
            {
                double weight = InputChannels == 4 ? cmykWeights[corner] : 1;
                if (InputChannels != 4)
                    for (int channel = 0; channel < InputChannels; channel++)
                    {
                        bool upper = (corner & (1 << channel)) != 0;
                        weight *= upper ? fraction[channel] : 1 - fraction[channel];
                    }
                if (weight == 0) continue;
                int offset = baseOffset + _cornerOffsets[corner];
                for (int channel = 0; channel < outputs; channel++)
                {
                    output[channel] += weight * normalized[samples[offset + channel]];
                }
            }
        }
        for (int channel = 0; channel < outputs; channel++)
            output[channel] = Curve(_outputOffset + channel * _outputEntries,
                _outputEntries, Math.Clamp(output[channel], 0, 1));
    }

    private double Curve(int offset, int count, double value)
    {
        double position = value * (count - 1);
        int lower = Math.Min((int)position, count - 2);
        double fraction = position - lower;
        double first = _normalized[_samples[offset + lower]];
        return first + fraction * (_normalized[_samples[offset + lower + 1]] - first);
    }

    private static class NormalizedSamples
    {
        internal static readonly double[] EightBit = Build(256, 255d);
        internal static readonly double[] SixteenBit = Build(65536, 65535d);

        private static double[] Build(int count, double maximum)
        {
            var samples = new double[count];
            for (int index = 0; index < count; index++) samples[index] = index / maximum;
            return samples;
        }
    }
}
