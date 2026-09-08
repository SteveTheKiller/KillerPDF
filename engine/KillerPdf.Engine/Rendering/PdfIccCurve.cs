using System.Buffers.Binary;

namespace KillerPdf.Engine.Rendering;

internal sealed class PdfIccCurve
{
    private readonly ReadOnlyMemory<byte> _samples;
    private readonly double[] _parameters;
    private readonly int _type;
    private readonly bool _monotonic = true;
    internal int EncodedLength { get; }
    internal bool CanInvert => _monotonic;

    internal PdfIccCurve(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes.Length < 12) throw new FormatException("An ICC curve is truncated.");
        if (bytes[..4].SequenceEqual("curv"u8))
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
            long length = 12L + count * 2L;
            if (length > bytes.Length) throw new FormatException("An ICC curve is truncated.");
            EncodedLength = (int)length;
            _type = -1;
            if (count < 2)
            {
                _parameters = [count == 0 ? 1 : BinaryPrimitives.ReadUInt16BigEndian(bytes[12..]) / 256d];
                if (_parameters[0] <= 0) throw new FormatException("An ICC curve gamma must be positive.");
            }
            else
            {
                _parameters = [];
                _samples = data.Slice(12, (int)count * 2);
                for (int index = 1; index < count; index++)
                    if (BinaryPrimitives.ReadUInt16BigEndian(_samples.Span[(index * 2)..])
                        < BinaryPrimitives.ReadUInt16BigEndian(_samples.Span[((index - 1) * 2)..]))
                        _monotonic = false;
            }
            return;
        }
        if (!bytes[..4].SequenceEqual("para"u8))
            throw new NotSupportedException("The ICC curve type is not supported.");
        _type = BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]);
        int parameterCount = _type switch
        {
            0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7,
            _ => throw new NotSupportedException("The ICC parametric curve function is not supported.")
        };
        EncodedLength = 12 + parameterCount * 4;
        if (bytes.Length < EncodedLength) throw new FormatException("An ICC curve is truncated.");
        _parameters = new double[parameterCount];
        for (int index = 0; index < parameterCount; index++)
            _parameters[index] = BinaryPrimitives.ReadInt32BigEndian(bytes[(12 + index * 4)..]) / 65536d;
        if (_parameters[0] <= 0 || (_type > 0 && _parameters[1] <= 0))
            throw new FormatException("An ICC parametric curve has invalid power parameters.");
        if (_type >= 3 && _parameters[4] < -_parameters[2] / _parameters[1])
            throw new FormatException("An ICC parametric curve has an undefined power segment.");
        if (_type >= 3)
        {
            double d = _parameters[4];
            double lower = _parameters[3] * d + (_type == 4 ? _parameters[6] : 0);
            double upper = Math.Pow(Math.Max(0, _parameters[1] * d + _parameters[2]), _parameters[0])
                + (_type == 4 ? _parameters[5] : 0);
            _monotonic = _parameters[3] >= 0 && upper >= lower - 1d / 65535;
        }
    }

    internal double Inverse(double output)
    {
        if (!double.IsFinite(output)) throw new ArgumentException("ICC curve values must be finite.");
        if (!_monotonic) throw new NotSupportedException("A nonmonotonic ICC curve cannot be inverted.");
        double y = Math.Clamp(output, 0, 1);
        if (y <= Evaluate(0)) return 0;
        if (y >= Evaluate(1)) return 1;
        if (!_samples.IsEmpty)
        {
            int lower = 0, upper = _samples.Length / 2 - 1;
            while (upper - lower > 1)
            {
                int middle = lower + (upper - lower) / 2;
                double value = BinaryPrimitives.ReadUInt16BigEndian(_samples.Span[(middle * 2)..]) / 65535d;
                if (value >= y) upper = middle; else lower = middle;
            }
            double first = BinaryPrimitives.ReadUInt16BigEndian(_samples.Span[(lower * 2)..]) / 65535d;
            double last = BinaryPrimitives.ReadUInt16BigEndian(_samples.Span[(upper * 2)..]) / 65535d;
            double fraction = last == first ? 0 : (y - first) / (last - first);
            return (lower + fraction) / (_samples.Length / 2 - 1);
        }
        double g = _parameters[0];
        if (_type <= 0) return Math.Pow(y, 1 / g);
        if (_type >= 3 && y < Evaluate(_parameters[4]))
        {
            double lowerOffset = _type == 4 ? _parameters[6] : 0;
            return _parameters[3] == 0 ? Math.Clamp(_parameters[4], 0, 1)
                : Math.Clamp((y - lowerOffset) / _parameters[3], 0, 1);
        }
        double upperOffset = _type == 2 ? _parameters[3] : _type == 4 ? _parameters[5] : 0;
        return Math.Clamp((Math.Pow(Math.Max(0, y - upperOffset), 1 / g) - _parameters[2]) / _parameters[1], 0, 1);
    }

    internal double Evaluate(double input)
    {
        if (!double.IsFinite(input)) throw new ArgumentException("ICC curve inputs must be finite.");
        double x = Math.Clamp(input, 0, 1);
        if (!_samples.IsEmpty)
        {
            int count = _samples.Length / 2;
            double position = x * (count - 1);
            int lower = Math.Min((int)position, count - 2);
            double first = BinaryPrimitives.ReadUInt16BigEndian(_samples.Span[(lower * 2)..]) / 65535d;
            double second = BinaryPrimitives.ReadUInt16BigEndian(_samples.Span[((lower + 1) * 2)..]) / 65535d;
            return first + (position - lower) * (second - first);
        }
        double g = _parameters[0];
        if (_type <= 0) return Math.Pow(x, g);
        double a = _parameters[1], b = _parameters[2];
        double threshold = _type < 3 ? -b / a : _parameters[4];
        double result;
        if (x >= threshold)
        {
            result = Math.Pow(Math.Max(0, a * x + b), g);
            if (_type == 2) result += _parameters[3];
            else if (_type == 4) result += _parameters[5];
        }
        else result = _type switch
        {
            1 => 0,
            2 => _parameters[3],
            3 => _parameters[3] * x,
            _ => _parameters[3] * x + _parameters[6]
        };
        return Math.Clamp(result, 0, 1);
    }
}
