using System.Buffers.Binary;
using System.Text;

namespace KillerPdf.Engine.Rendering;

// One profile and intent form a stable color-space identity. Device components
// remain separate from the D50 XYZ connection values used between profiles.
internal sealed class PdfIccProfileTransform : PdfColorTransform
{
    private readonly PdfIccTable? _forward;
    private readonly PdfIccTable? _reverse;
    private readonly PdfIccCurve[]? _curves;
    private readonly double[]? _matrix;
    private readonly double[]? _inverseMatrix;
    private double[]? _absoluteScale;
    private readonly double[]? _mediaWhiteScale;
    private readonly bool _invalidWhite;
    private readonly ReadOnlyMemory<byte> _tableData;
    private readonly Dictionary<ReadOnlyMemory<byte>, PdfIccTable>? _tables;
    private static readonly Lock IntentGate = new();
    private PdfIccProfileTransform? _intentRoot;
    private PdfIccProfileTransform?[]? _intents;
    private byte _attemptedIntents;
    private int _intent;
    private readonly bool _lab;
    internal bool IsLabInput { get; }
    internal override int Components { get; }
    internal override bool SupportsBlending => !IsLabInput;
    internal override double ClampComponent(int component, double value) => IsLabInput
        ? Math.Clamp(value, component == 0 ? 0 : -128, component == 0 ? 100 : 127)
        : Math.Clamp(value, 0, 1);

    internal PdfIccProfileTransform(ReadOnlyMemory<byte> data, int intent = 1)
        : this(data, intent, null) { }

    private PdfIccProfileTransform(ReadOnlyMemory<byte> data, int intent,
        Dictionary<ReadOnlyMemory<byte>, PdfIccTable>? tables)
    {
        if (intent is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(intent));
        _intent = intent;
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes.Length < 132 || !bytes.Slice(36, 4).SequenceEqual("acsp"u8))
            throw new FormatException("An ICC profile header is invalid.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes[128..]);
        long tableEnd = 132L + 12L * count;
        if (length > bytes.Length || tableEnd > length)
            throw new FormatException("An ICC profile tag table is truncated.");
        string deviceSpace = Encoding.ASCII.GetString(bytes.Slice(16, 4));
        IsLabInput = deviceSpace == "Lab ";
        Components = deviceSpace switch
        {
            "GRAY" => 1, "RGB " or "Lab " => 3, "CMYK" => 4,
            _ => throw new NotSupportedException("The ICC device color space is not supported.")
        };
        string pcs = Encoding.ASCII.GetString(bytes.Slice(20, 4));
        if (pcs is not ("XYZ " or "Lab "))
            throw new NotSupportedException("The ICC connection space is not supported.");
        _lab = pcs == "Lab ";
        var tags = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            int entry = 132 + index * 12;
            string name = Encoding.ASCII.GetString(bytes.Slice(entry, 4));
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(bytes[(entry + 4)..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes[(entry + 8)..]);
            if ((offset & 3) != 0 || offset < tableEnd || size < 8 || offset > length || size > length - offset
                || !tags.TryAdd(name, data.Slice((int)offset, (int)size)))
                throw new FormatException("An ICC profile tag has invalid bounds or a duplicate signature.");
        }
        if (tags.ContainsKey("A2B0") || tags.ContainsKey("A2B1") || tags.ContainsKey("A2B2"))
        {
            _tableData = data[..(int)length];
            _tables = tables ?? new();
        }
        if (tags.TryGetValue("wtpt", out ReadOnlyMemory<byte> whitePoint))
        {
            ReadOnlySpan<byte> white = whitePoint.Span;
            if (white.Length < 20 || !white[..4].SequenceEqual("XYZ "u8))
                _invalidWhite = true;
            else
            {
                _mediaWhiteScale = new double[3];
                ReadOnlySpan<double> reference = [0.9642, 1, 0.8249];
                for (int channel = 0; channel < 3; channel++)
                {
                    double value = BinaryPrimitives.ReadInt32BigEndian(white[(8 + channel * 4)..]) / 65536d;
                    if (value <= 0) _invalidWhite = true;
                    _mediaWhiteScale[channel] = value / reference[channel];
                }
            }
        }
        if (intent == 3)
        {
            if (_invalidWhite) throw new FormatException("The ICC media white point is invalid.");
            _absoluteScale = _mediaWhiteScale;
        }
        int tableIntent = intent == 3 ? 1 : intent;
        if (tags.TryGetValue($"A2B{tableIntent}", out ReadOnlyMemory<byte> forward)
            || tags.TryGetValue("A2B0", out forward))
        {
            _forward = ReadTable(forward);
            if (_forward.InputChannels != Components || _forward.OutputChannels != 3)
                throw new FormatException("An ICC forward transform has invalid channel counts.");
            if (tags.TryGetValue($"B2A{tableIntent}", out ReadOnlyMemory<byte> reverse)
                || tags.TryGetValue("B2A0", out reverse))
            {
                _reverse = ReadTable(reverse);
                if (_reverse.InputChannels != 3 || _reverse.OutputChannels != Components)
                    throw new FormatException("An ICC reverse transform has invalid channel counts.");
            }
            return;
        }
        if (_lab || IsLabInput || Components == 4)
            throw new NotSupportedException("The ICC profile has no supported device transform.");
        ReadOnlyMemory<byte> Required(string name) => tags.TryGetValue(name, out var value)
            ? value : throw new FormatException($"The ICC profile is missing {name}.");
        if (Components == 1)
        {
            _curves = [new PdfIccCurve(Required("kTRC"))];
            _matrix = [0.9642, 1, 0.8249];
        }
        else
        {
            _curves = [new PdfIccCurve(Required("rTRC")), new PdfIccCurve(Required("gTRC")),
                new PdfIccCurve(Required("bTRC"))];
            _matrix = new double[9];
            string[] columns = ["rXYZ", "gXYZ", "bXYZ"];
            for (int column = 0; column < 3; column++)
            {
                ReadOnlySpan<byte> xyz = Required(columns[column]).Span;
                if (xyz.Length < 20 || !xyz[..4].SequenceEqual("XYZ "u8))
                    throw new FormatException("An ICC matrix column is invalid.");
                for (int row = 0; row < 3; row++)
                    _matrix[row * 3 + column] = BinaryPrimitives.ReadInt32BigEndian(xyz[(8 + row * 4)..]) / 65536d;
            }
            double a = _matrix[0], b = _matrix[1], c = _matrix[2], d = _matrix[3], e = _matrix[4],
                f = _matrix[5], g = _matrix[6], h = _matrix[7], i = _matrix[8];
            double determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(determinant) > 1e-15)
            {
                _inverseMatrix = [e * i - f * h, c * h - b * i, b * f - c * e,
                    f * g - d * i, a * i - c * g, c * d - a * f,
                    d * h - e * g, b * g - a * h, a * e - b * d];
                for (int index = 0; index < 9; index++) _inverseMatrix[index] /= determinant;
            }
        }
    }

    internal static PdfIccProfileTransform ReadAvailable(ReadOnlyMemory<byte> data)
    {
        try { return new(data); }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            try
            {
                var perceptual = new PdfIccProfileTransform(data, 0);
                perceptual._attemptedIntents = 1 << 1;
                return perceptual;
            }
            catch (Exception alternate) when (alternate is FormatException or NotSupportedException)
            {
                var saturation = new PdfIccProfileTransform(data, 2);
                saturation._attemptedIntents = (1 << 0) | (1 << 1);
                return saturation;
            }
        }
    }

    internal PdfIccProfileTransform? ForIntent(int intent)
    {
        if (intent is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(intent));
        PdfIccProfileTransform root = _intentRoot ?? this;
        if (intent == root._intent || root._tableData.IsEmpty && intent != 3 && root._intent != 3) return root;
        lock (IntentGate)
        {
            root._intents ??= new PdfIccProfileTransform?[4];
            byte bit = (byte)(1 << intent);
            if ((root._attemptedIntents & bit) != 0) return root._intents[intent];
            try
            {
                PdfIccProfileTransform variant;
                if (root._tableData.IsEmpty)
                {
                    if (intent == 3 && root._invalidWhite) return null;
                    variant = (PdfIccProfileTransform)root.MemberwiseClone();
                    variant._intent = intent;
                    variant._absoluteScale = intent == 3 ? root._mediaWhiteScale : null;
                }
                else variant = new PdfIccProfileTransform(root._tableData, intent, root._tables);
                variant._intentRoot = root;
                return root._intents[intent] = variant;
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException)
            {
                return null;
            }
            finally
            {
                root._attemptedIntents |= bit;
            }
        }
    }

    private PdfIccTable ReadTable(ReadOnlyMemory<byte> data)
    {
        // Intent tags can reference the same byte slice. Share only those immutable
        // tables within this profile; variant construction holds IntentGate.
        if (_tables!.TryGetValue(data, out PdfIccTable? table)) return table;
        table = PdfIccTable.Read(data);
        _tables.Add(data, table);
        return table;
    }

    internal override void ToXyz(ReadOnlySpan<double> device, Span<double> xyz)
    {
        if (device.Length != Components || xyz.Length != 3)
            throw new ArgumentException("ICC transform channel counts do not match.");
        if (_forward is not null)
        {
            if (IsLabInput)
            {
                for (int channel = 0; channel < 3; channel++)
                    if (!double.IsFinite(device[channel]))
                        throw new ArgumentException("ICC device values must be finite.");
                Span<double> encoded = stackalloc double[3];
                encoded[0] = ClampComponent(0, device[0]) / 100;
                encoded[1] = (ClampComponent(1, device[1]) + 128) / 255;
                encoded[2] = (ClampComponent(2, device[2]) + 128) / 255;
                if (_lab && _forward.UsesLegacyLabEncoding)
                    for (int channel = 0; channel < 3; channel++) encoded[channel] *= 65280d / 65535;
                _forward.Transform(encoded, xyz);
            }
            else _forward.Transform(device, xyz);
            if (_lab)
            {
                double encoding = _forward.UsesLegacyLabEncoding ? 65535d / 65280 : 1;
                double lightness = Math.Clamp(xyz[0] * encoding, 0, 1) * 100;
                double a = xyz[1] * encoding * 255 - 128;
                double b = xyz[2] * encoding * 255 - 128;
                double y = (lightness + 16) / 116;
                xyz[0] = 0.9642 * LabInverse(y + a / 500);
                xyz[1] = LabInverse(y);
                xyz[2] = 0.8249 * LabInverse(y - b / 200);
            }
            else for (int channel = 0; channel < 3; channel++) xyz[channel] *= _forward.XyzEncodingScale;
            if (_absoluteScale is not null)
                for (int channel = 0; channel < 3; channel++) xyz[channel] *= _absoluteScale[channel];
            return;
        }
        Span<double> values = stackalloc double[3];
        for (int channel = 0; channel < Components; channel++) values[channel] = _curves![channel].Evaluate(device[channel]);
        for (int row = 0; row < 3; row++)
            xyz[row] = Components == 1 ? _matrix![row] * values[0]
                : _matrix![row * 3] * values[0] + _matrix[row * 3 + 1] * values[1]
                    + _matrix[row * 3 + 2] * values[2];
        if (_absoluteScale is not null)
            for (int channel = 0; channel < 3; channel++) xyz[channel] *= _absoluteScale[channel];
    }

    internal bool HasReverseLut => _reverse is not null;
    internal override bool CanConvertFromXyz => _reverse is not null
        || _curves is not null && (Components == 1 || _inverseMatrix is not null)
            && _curves[0].CanInvert && (Components == 1 || _curves[1].CanInvert && _curves[2].CanInvert);

    internal override void FromXyz(ReadOnlySpan<double> xyz, Span<double> device)
    {
        if (xyz.Length != 3 || device.Length != Components)
            throw new ArgumentException("ICC transform channel counts do not match.");
        for (int channel = 0; channel < 3; channel++)
            if (!double.IsFinite(xyz[channel])) throw new ArgumentException("ICC connection values must be finite.");
        Span<double> relative = stackalloc double[3];
        if (_absoluteScale is not null)
        {
            for (int channel = 0; channel < 3; channel++) relative[channel] = xyz[channel] / _absoluteScale[channel];
            FromRelativeXyz(relative, device);
        }
        else FromRelativeXyz(xyz, device);
    }

    private void FromRelativeXyz(ReadOnlySpan<double> xyz, Span<double> device)
    {
        if (_reverse is null)
        {
            if (!CanConvertFromXyz) throw new NotSupportedException("The ICC profile has no reverse transform.");
            double x = xyz[0], y = xyz[1], z = xyz[2];
            for (int channel = 0; channel < Components; channel++)
            {
                double linear = Components == 1 ? y : _inverseMatrix![channel * 3] * x
                    + _inverseMatrix[channel * 3 + 1] * y + _inverseMatrix[channel * 3 + 2] * z;
                device[channel] = _curves![channel].Inverse(linear);
            }
            return;
        }
        Span<double> encoded = stackalloc double[3];
        if (_lab)
        {
            double x = LabForward(xyz[0] / 0.9642), y = LabForward(xyz[1]), z = LabForward(xyz[2] / 0.8249);
            double encoding = _reverse.UsesLegacyLabEncoding ? 65280d / 65535 : 1;
            encoded[0] = (116 * y - 16) / 100 * encoding;
            encoded[1] = (500 * (x - y) + 128) / 255 * encoding;
            encoded[2] = (200 * (y - z) + 128) / 255 * encoding;
        }
        else for (int channel = 0; channel < 3; channel++) encoded[channel] = xyz[channel] / _reverse.XyzEncodingScale;
        _reverse.Transform(encoded, device, applyXyzMatrix: !_lab);
        if (IsLabInput)
        {
            if (_lab && _reverse.UsesLegacyLabEncoding)
                for (int channel = 0; channel < 3; channel++) device[channel] *= 65535d / 65280;
            device[0] *= 100;
            device[1] = device[1] * 255 - 128;
            device[2] = device[2] * 255 - 128;
        }
    }

    private static double LabInverse(double value) => value >= 6d / 29 ? value * value * value
        : 108d / 841 * (value - 4d / 29);
    private static double LabForward(double value) => value > 216d / 24389 ? Math.Cbrt(value)
        : value * (841d / 108) + 4d / 29;
}
