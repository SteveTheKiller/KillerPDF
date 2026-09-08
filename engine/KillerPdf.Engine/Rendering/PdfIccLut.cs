using System.Buffers.Binary;

namespace KillerPdf.Engine.Rendering;

// ICC lut8Type and lut16Type operate on normalized channel values. PCS encoding
// and profile connection belong to the caller, outside this table evaluator.
internal sealed class PdfIccLut : PdfIccTable
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _sampleBytes;
    private readonly int _grid;
    private readonly int _inputEntries;
    private readonly int _outputEntries;
    private readonly int _inputOffset;
    private readonly int _gridOffset;
    private readonly int _outputOffset;
    private readonly double[] _matrix;

    internal override int InputChannels { get; }
    internal override int OutputChannels { get; }
    internal override bool UsesLegacyLabEncoding => _sampleBytes == 2;
    internal override double XyzEncodingScale => _sampleBytes == 1 ? 255d / 128 : 65535d / 32768;

    internal PdfIccLut(ReadOnlyMemory<byte> data)
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
        _inputOffset = _sampleBytes == 1 ? 48 : 52;
        long gridOffset = _inputOffset + (long)_inputEntries * InputChannels * _sampleBytes;
        long outputOffset = gridOffset + cells * OutputChannels * _sampleBytes;
        long end = outputOffset + (long)_outputEntries * OutputChannels * _sampleBytes;
        if (end > bytes.Length) throw new FormatException("An ICC lookup table is truncated.");
        _gridOffset = (int)gridOffset;
        _outputOffset = (int)outputOffset;
        _data = data[..(int)end];
        _matrix = new double[9];
        for (int index = 0; index < _matrix.Length; index++)
            _matrix[index] = BinaryPrimitives.ReadInt32BigEndian(bytes[(12 + index * 4)..]) / 65536d;
    }

    internal override void Transform(ReadOnlySpan<double> input, Span<double> output, bool applyXyzMatrix = false)
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
        Span<int> lower = stackalloc int[4];
        Span<double> fraction = stackalloc double[4];
        for (int channel = 0; channel < InputChannels; channel++)
        {
            double value = Curve(_inputOffset + channel * _inputEntries * _sampleBytes,
                _inputEntries, values[channel]) * (_grid - 1);
            lower[channel] = Math.Min((int)value, _grid - 2);
            fraction[channel] = value - lower[channel];
        }
        output.Clear();
        int corners = 1 << InputChannels;
        for (int corner = 0; corner < corners; corner++)
        {
            int cell = 0;
            double weight = 1;
            for (int channel = 0; channel < InputChannels; channel++)
            {
                bool upper = (corner & (1 << channel)) != 0;
                cell = cell * _grid + lower[channel] + (upper ? 1 : 0);
                weight *= upper ? fraction[channel] : 1 - fraction[channel];
            }
            if (weight == 0) continue;
            int offset = _gridOffset + cell * OutputChannels * _sampleBytes;
            for (int channel = 0; channel < OutputChannels; channel++)
                output[channel] += weight * Sample(offset + channel * _sampleBytes);
        }
        for (int channel = 0; channel < OutputChannels; channel++)
            output[channel] = Curve(_outputOffset + channel * _outputEntries * _sampleBytes,
                _outputEntries, Math.Clamp(output[channel], 0, 1));
    }

    private double Curve(int offset, int count, double value)
    {
        double position = value * (count - 1);
        int lower = Math.Min((int)position, count - 2);
        double fraction = position - lower;
        double first = Sample(offset + lower * _sampleBytes);
        return first + fraction * (Sample(offset + (lower + 1) * _sampleBytes) - first);
    }

    private double Sample(int offset) => _sampleBytes == 1 ? _data.Span[offset] / 255d
        : BinaryPrimitives.ReadUInt16BigEndian(_data.Span[offset..]) / 65535d;
}
