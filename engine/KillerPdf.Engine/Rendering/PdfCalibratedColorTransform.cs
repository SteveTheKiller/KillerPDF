namespace KillerPdf.Engine.Rendering;

internal sealed class PdfCalibratedColorTransform : PdfColorTransform
{
    private readonly double[] _gamma;
    private readonly double[] _matrix;
    private readonly double[]? _inverse;
    internal override int Components => _gamma.Length;
    internal override bool CanConvertFromXyz => Components == 1 || _inverse is not null;

    internal PdfCalibratedColorTransform(double[] white, double[] gamma, double[]? matrix = null)
    {
        if (white.Length != 3 || white.Any(value => !double.IsFinite(value))
            || white[0] <= 0 || Math.Abs(white[1] - 1) > 1e-9 || white[2] <= 0
            || gamma.Length is not (1 or 3) || gamma.Any(value => !double.IsFinite(value) || value <= 0)
            || gamma.Length == 3 && (matrix is not { Length: 9 } || matrix.Any(value => !double.IsFinite(value))))
            throw new FormatException("A calibrated color transform is invalid.");
        _gamma = (double[])gamma.Clone();
        _matrix = new double[Components * 3];
        var source = Bradford(white[0], white[1], white[2]);
        var target = Bradford(0.9642, 1, 0.8249);
        if (source.L == 0 || source.M == 0 || source.S == 0)
            throw new FormatException("A calibrated white point cannot be adapted.");
        for (int column = 0; column < Components; column++)
        {
            var cone = Components == 1 ? source
                : Bradford(matrix![column * 3], matrix[column * 3 + 1], matrix[column * 3 + 2]);
            double l = cone.L * target.L / source.L, m = cone.M * target.M / source.M,
                s = cone.S * target.S / source.S;
            _matrix[column] = 0.9869929 * l - 0.1470543 * m + 0.1599627 * s;
            _matrix[Components + column] = 0.4323053 * l + 0.5183603 * m + 0.0492912 * s;
            _matrix[Components * 2 + column] = -0.0085287 * l + 0.0400428 * m + 0.9684867 * s;
        }
        if (_matrix.Any(value => !double.IsFinite(value)))
            throw new FormatException("A calibrated color matrix is invalid.");
        if (Components == 1) return;
        double a = _matrix[0], b = _matrix[1], c = _matrix[2], d = _matrix[3], e = _matrix[4],
            f = _matrix[5], g = _matrix[6], h = _matrix[7], i = _matrix[8];
        double determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) <= 1e-15) return;
        double[] inverse = [e * i - f * h, c * h - b * i, b * f - c * e,
            f * g - d * i, a * i - c * g, c * d - a * f,
            d * h - e * g, b * g - a * h, a * e - b * d];
        for (int index = 0; index < inverse.Length; index++) inverse[index] /= determinant;
        if (inverse.All(double.IsFinite)) _inverse = inverse;
    }

    internal override void ToXyz(ReadOnlySpan<double> device, Span<double> xyz)
    {
        if (device.Length != Components || xyz.Length != 3)
            throw new ArgumentException("Calibrated transform channel counts do not match.");
        Span<double> values = stackalloc double[3];
        for (int channel = 0; channel < Components; channel++)
        {
            if (!double.IsFinite(device[channel])) throw new ArgumentException("Color values must be finite.");
            values[channel] = Math.Pow(Math.Clamp(device[channel], 0, 1), _gamma[channel]);
        }
        for (int row = 0; row < 3; row++)
            xyz[row] = Components == 1 ? _matrix[row] * values[0]
                : _matrix[row * 3] * values[0] + _matrix[row * 3 + 1] * values[1] + _matrix[row * 3 + 2] * values[2];
    }

    internal override void FromXyz(ReadOnlySpan<double> xyz, Span<double> device)
    {
        if (xyz.Length != 3 || device.Length != Components)
            throw new ArgumentException("Calibrated transform channel counts do not match.");
        for (int channel = 0; channel < 3; channel++)
            if (!double.IsFinite(xyz[channel])) throw new ArgumentException("Connection values must be finite.");
        if (!CanConvertFromXyz) throw new NotSupportedException("The calibrated matrix cannot be inverted.");
        double x = xyz[0], y = xyz[1], z = xyz[2];
        for (int channel = 0; channel < Components; channel++)
        {
            double linear = Components == 1 ? y / _matrix[1]
                : _inverse![channel * 3] * x + _inverse[channel * 3 + 1] * y + _inverse[channel * 3 + 2] * z;
            device[channel] = Math.Pow(Math.Clamp(linear, 0, 1), 1 / _gamma[channel]);
        }
    }

    private static (double L, double M, double S) Bradford(double x, double y, double z) => (
        0.8951 * x + 0.2664 * y - 0.1614 * z,
        -0.7502 * x + 1.7135 * y + 0.0367 * z,
        0.0389 * x - 0.0685 * y + 1.0296 * z);
}
