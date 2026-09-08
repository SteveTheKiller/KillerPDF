using System.Buffers.Binary;

namespace KillerPdf.Engine.Rendering;

internal sealed class PdfIccMultiProcessLut : PdfIccTable
{
    private readonly bool _reverse;
    private readonly PdfIccCurve[]? _a;
    private readonly PdfIccCurve[] _b;
    private readonly PdfIccCurve[]? _m;
    private readonly double[]? _matrix;
    // CLUT samples are decoded to normalized doubles once at construction so evaluation
    // indexes an array instead of decoding big-endian bytes per corner.
    private readonly double[]? _clut;
    private readonly int[]? _grid;
    private readonly int[]? _cornerOffsets;
    internal override int InputChannels { get; }
    internal override int OutputChannels { get; }

    internal PdfIccMultiProcessLut(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 32) throw new FormatException("An ICC processing table is truncated.");
        ReadOnlySpan<byte> bytes = data.Span;
        _reverse = bytes[..4].SequenceEqual("mBA "u8);
        if (!_reverse && !bytes[..4].SequenceEqual("mAB "u8))
            throw new NotSupportedException("The ICC processing-table type is not supported.");
        InputChannels = bytes[8];
        OutputChannels = bytes[9];
        if (InputChannels is < 1 or > 4 || OutputChannels is < 1 or > 4)
            throw new FormatException("An ICC processing table has invalid channel counts.");
        int[] offsets = new int[5];
        for (int index = 0; index < offsets.Length; index++)
        {
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(bytes[(12 + index * 4)..]);
            if (offset != 0 && (offset < 32 || offset >= data.Length || (offset & 3) != 0))
                throw new FormatException("An ICC processing element has an invalid offset.");
            offsets[index] = (int)offset;
        }
        ReadOnlyMemory<byte> Section(int index)
        {
            int start = offsets[index];
            if (start == 0) return default;
            int end = data.Length;
            foreach (int candidate in offsets) if (candidate > start && candidate < end) end = candidate;
            return data.Slice(start, end - start);
        }
        if (offsets[0] == 0 || (offsets[1] == 0) != (offsets[2] == 0)
            || (offsets[3] == 0) != (offsets[4] == 0)
            || offsets[3] == 0 && InputChannels != OutputChannels)
            throw new FormatException("An ICC processing table has an invalid element combination.");
        int matrixChannels = _reverse ? InputChannels : OutputChannels;
        _b = ReadCurves(Section(0), matrixChannels);
        if (offsets[4] != 0) _a = ReadCurves(Section(4), _reverse ? OutputChannels : InputChannels);
        if (offsets[2] != 0) _m = ReadCurves(Section(2), matrixChannels);
        if (offsets[1] != 0)
        {
            ReadOnlySpan<byte> matrix = Section(1).Span;
            if (matrixChannels != 3 || matrix.Length < 48)
                throw new FormatException("An ICC processing matrix is invalid.");
            _matrix = new double[12];
            for (int index = 0; index < 12; index++)
                _matrix[index] = BinaryPrimitives.ReadInt32BigEndian(matrix[(index * 4)..]) / 65536d;
        }
        if (offsets[3] != 0)
        {
            ReadOnlyMemory<byte> clut = Section(3);
            if (clut.Length < 20) throw new FormatException("An ICC processing CLUT is truncated.");
            int sampleBytes = clut.Span[16];
            if (sampleBytes is not (1 or 2)) throw new FormatException("An ICC CLUT precision is invalid.");
            _grid = new int[InputChannels];
            long cells = 1;
            for (int channel = 0; channel < InputChannels; channel++)
            {
                _grid[channel] = clut.Span[channel];
                if (_grid[channel] < 2) throw new FormatException("An ICC CLUT grid is invalid.");
                cells *= _grid[channel];
            }
            long samples = cells * OutputChannels;
            long length = samples * sampleBytes;
            if (length > clut.Length - 20) throw new FormatException("An ICC processing CLUT is truncated.");
            ReadOnlySpan<byte> table = clut.Span.Slice(20, (int)length);
            _clut = new double[samples];
            if (sampleBytes == 1)
            {
                for (int index = 0; index < _clut.Length; index++)
                    _clut[index] = table[index] / 255d;
            }
            else
            {
                for (int index = 0; index < _clut.Length; index++)
                    _clut[index] = BinaryPrimitives.ReadUInt16BigEndian(table[(index * 2)..]) / 65535d;
            }
            _cornerOffsets = new int[1 << InputChannels];
            for (int corner = 0; corner < _cornerOffsets.Length; corner++)
            {
                int cell = 0;
                for (int channel = 0; channel < InputChannels; channel++)
                    cell = cell * _grid[channel] + ((corner >> channel) & 1);
                _cornerOffsets[corner] = cell * OutputChannels;
            }
        }
    }

    private static PdfIccCurve[] ReadCurves(ReadOnlyMemory<byte> data, int count)
    {
        var curves = new PdfIccCurve[count];
        long offset = 0;
        for (int index = 0; index < count; index++)
        {
            if (offset >= data.Length) throw new FormatException("An ICC curve sequence is truncated.");
            curves[index] = new PdfIccCurve(data[(int)offset..]);
            offset += ((long)curves[index].EncodedLength + 3) & ~3L;
        }
        return curves;
    }

    internal override void Transform(ReadOnlySpan<double> input, Span<double> output, bool applyXyzMatrix = false)
    {
        if (input.Length != InputChannels || output.Length != OutputChannels)
            throw new ArgumentException("ICC processing-table channel counts do not match.");
        Span<double> values = stackalloc double[4];
        for (int channel = 0; channel < InputChannels; channel++)
        {
            if (!double.IsFinite(input[channel])) throw new ArgumentException("ICC inputs must be finite.");
            values[channel] = Math.Clamp(input[channel], 0, 1);
        }
        if (_reverse)
        {
            Curves(_b, values);
            Matrix(values);
            Curves(_m, values);
            Clut(values);
            Curves(_a, values);
        }
        else
        {
            Curves(_a, values);
            Clut(values);
            Curves(_m, values);
            Matrix(values);
            Curves(_b, values);
        }
        values[..OutputChannels].CopyTo(output);
    }

    private static void Curves(PdfIccCurve[]? curves, Span<double> values)
    {
        if (curves is null) return;
        for (int channel = 0; channel < curves.Length; channel++) values[channel] = curves[channel].Evaluate(values[channel]);
    }

    private void Matrix(Span<double> values)
    {
        if (_matrix is null) return;
        double x = values[0], y = values[1], z = values[2];
        for (int row = 0; row < 3; row++) values[row] = Math.Clamp(_matrix[row * 3] * x
            + _matrix[row * 3 + 1] * y + _matrix[row * 3 + 2] * z + _matrix[row + 9], 0, 1);
    }

    private void Clut(Span<double> values)
    {
        if (_grid is null) return;
        int baseCell = 0;
        Span<double> fraction = stackalloc double[4];
        for (int channel = 0; channel < InputChannels; channel++)
        {
            double position = values[channel] * (_grid[channel] - 1);
            int lower = Math.Min((int)position, _grid[channel] - 2);
            baseCell = baseCell * _grid[channel] + lower;
            fraction[channel] = position - lower;
        }
        values.Clear();
        double[] clut = _clut!;
        int outputs = OutputChannels;
        int baseOffset = baseCell * outputs;
        for (int corner = 0; corner < 1 << InputChannels; corner++)
        {
            double weight = 1;
            for (int channel = 0; channel < InputChannels; channel++)
            {
                bool upper = (corner & (1 << channel)) != 0;
                weight *= upper ? fraction[channel] : 1 - fraction[channel];
            }
            if (weight == 0) continue;
            int cornerOffset = baseOffset + _cornerOffsets![corner];
            for (int channel = 0; channel < outputs; channel++)
                values[channel] += weight * clut[cornerOffset + channel];
        }
    }
}
