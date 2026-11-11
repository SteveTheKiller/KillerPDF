using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace KillerPdf.Engine.Rendering;

// One profile and intent form a stable color-space identity. Device components
// remain separate from the D50 XYZ connection values used between profiles.
internal sealed class PdfIccProfileTransform : PdfColorTransform
{
    private readonly PdfIccTable? _forward;
    private readonly PdfIccTable? _reverse;
    private readonly string? _forwardTag;
    private readonly string? _reverseTag;
    private readonly PdfIccCurve[]? _curves;
    private readonly double[]? _matrix;
    private readonly double[]? _inverseMatrix;
    private double[]? _absoluteScale;
    private readonly double[]? _mediaWhiteScale;
    private readonly bool _invalidWhite;
    private readonly bool _hasColorimetricForward;
    private readonly bool _hasColorimetricReverse;
    private readonly bool _hasPerceptualReverse;
    private readonly ReadOnlyMemory<byte> _tableData;
    private readonly Dictionary<ReadOnlyMemory<byte>, PdfIccTable>? _tables;
    private readonly PdfIccStorageBudget _storage;
    // One gate per root profile: variants of one profile share its table cache, while unrelated
    // profiles in other documents parse their intent variants in parallel.
    private readonly Lock _intentGate = new();
    private PdfIccProfileTransform? _intentRoot;
    private PdfIccProfileTransform?[]? _intents;
    private byte _attemptedIntents;
    private int _intent;
    private readonly bool _lab;
    private readonly byte[] _profileFingerprint;
    internal string ContentSha256 => Convert.ToHexString(_profileFingerprint);
    internal bool IsLabInput { get; }
    internal bool IsLutBased => _forward is not null;
    internal string? ForwardTableSignature => _forwardTag;
    internal string? ReverseTableSignature => _reverseTag;
    internal bool AppliesMediaWhiteScaling => _absoluteScale is not null;
    internal int RenderingIntent => _intent;
    internal bool SupportsBlackPointCompensation { get; }
    internal bool HasExplicitValidMediaWhite => _mediaWhiteScale is not null && !_invalidWhite;
    internal bool HasInvalidMediaWhite => _invalidWhite;
    internal bool HasColorimetricForward => _curves is not null || _hasColorimetricForward;
    internal bool HasColorimetricReverse => CanConvertFromXyz
        && (_curves is not null || _hasColorimetricReverse);
    internal bool HasD50MediaWhite => HasExplicitValidMediaWhite
        && Math.Abs(_mediaWhiteScale![0] - 1) * 0.9642 <= 2d / 65536
        && Math.Abs(_mediaWhiteScale[1] - 1) <= 2d / 65536
        && Math.Abs(_mediaWhiteScale[2] - 1) * 0.8249 <= 2d / 65536;
    internal override int Components { get; }
    internal override bool SupportsBlending => !IsLabInput;
    internal void ReadPaperBlack(Span<double> xyz)
    {
        var absolute = ForIntent(3)
            ?? throw new NotSupportedException("The ICC profile has no absolute colorimetric transform.");
        Span<double> deviceBlack = stackalloc double[Components];
        deviceBlack.Clear();
        if (Components == 4)
        {
            var perceptual = ForIntent(0);
            if (!_hasPerceptualReverse || perceptual is not { HasReverseLut: true })
                throw new NotSupportedException("CMYK paper-only viewing requires a perceptual reverse table for black estimation.");
            // ICC black estimation convention: request PCS zero through B2A0,
            // then measure those device coordinates with colorimetric forward.
            // This does not claim a physical darkest black or use 400% ink.
            perceptual.FromXyz([0, 0, 0], deviceBlack);
        }
        else if (IsLabInput)
            throw new NotSupportedException("The ICC profile has no supported paper-viewing black endpoint.");
        absolute.ToXyz(deviceBlack, xyz);
    }
    internal void ReadPaperWhite(Span<double> xyz)
    {
        if (IsLabInput || !HasExplicitValidMediaWhite)
            throw new NotSupportedException("The ICC profile has no supported paper-white endpoint.");
        var absolute = ForIntent(3)
            ?? throw new NotSupportedException("The ICC profile has no absolute colorimetric transform.");
        Span<double> deviceWhite = stackalloc double[Components];
        deviceWhite.Fill(Components == 4 ? 0 : 1);
        absolute.ToXyz(deviceWhite, xyz);
        ReadOnlySpan<double> reference = [0.9642, 1, 0.8249];
        // Paper simulation uses the measured absolute endpoint. A relative proof's white correction
        // must not replace that endpoint or relax the profile's reliability check.
        const double whiteEndpointTolerance = 0.001;
        for (int channel = 0; channel < 3; channel++)
            if (!double.IsFinite(xyz[channel])
                || Math.Abs(xyz[channel] - _mediaWhiteScale![channel] * reference[channel]) > whiteEndpointTolerance)
                throw new NotSupportedException("The ICC forward white endpoint disagrees with its declared media white.");
    }
    internal override bool HasSameIdentity(PdfColorTransform? other) => ReferenceEquals(this, other)
        || other is PdfIccProfileTransform profile && _intent == profile._intent
            && _profileFingerprint.AsSpan().SequenceEqual(profile._profileFingerprint);
    internal override double ClampComponent(int component, double value) => IsLabInput
        ? Math.Clamp(value, component == 0 ? 0 : -128, component == 0 ? 100 : 127)
        : Math.Clamp(value, 0, 1);

    internal PdfIccProfileTransform(ReadOnlyMemory<byte> data, int intent = 1)
        : this(data, intent, null, null) { }

    private PdfIccProfileTransform(ReadOnlyMemory<byte> data, int intent,
        Dictionary<ReadOnlyMemory<byte>, PdfIccTable>? tables, PdfIccStorageBudget? storage)
    {
        _storage = storage ?? new PdfIccStorageBudget(data.Length);
        if (intent is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(intent));
        _intent = intent;
        ReadOnlySpan<byte> bytes = data.Span;
        if (bytes.Length < 132 || !bytes.Slice(36, 4).SequenceEqual("acsp"u8))
            throw new FormatException("An ICC profile header is invalid.");
        SupportsBlackPointCompensation = Encoding.ASCII.GetString(bytes.Slice(12, 4))
            is "scnr" or "mntr" or "prtr" or "spac";
        uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes[128..]);
        long tableEnd = 132L + 12L * count;
        if (length > bytes.Length || tableEnd > length)
            throw new FormatException("An ICC profile tag table is truncated.");
        // PDF stream objects and compression do not define color-space identity.
        // Include the complete declared ICC data and keep rendering intent separate.
        _profileFingerprint = SHA256.HashData(bytes[..(int)length]);
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
        _hasColorimetricForward = tags.ContainsKey("A2B1");
        _hasColorimetricReverse = tags.ContainsKey("B2A1");
        _hasPerceptualReverse = tags.ContainsKey("B2A0");
        if (tags.ContainsKey("A2B0") || tags.ContainsKey("A2B1") || tags.ContainsKey("A2B2"))
        {
            _tableData = data[..(int)length];
            _tables = tables ?? [];
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
            _forwardTag = tags.ContainsKey($"A2B{tableIntent}") ? $"A2B{tableIntent}" : "A2B0";
            if (_forward.InputChannels != Components || _forward.OutputChannels != 3)
                throw new FormatException("An ICC forward transform has invalid channel counts.");
            if (tags.TryGetValue($"B2A{tableIntent}", out ReadOnlyMemory<byte> reverse)
                || tags.TryGetValue("B2A0", out reverse))
            {
                _reverse = ReadTable(reverse);
                _reverseTag = tags.ContainsKey($"B2A{tableIntent}") ? $"B2A{tableIntent}" : "B2A0";
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
            _curves = [_storage.ReadCurve(Required("kTRC"))];
            _matrix = [0.9642, 1, 0.8249];
        }
        else
        {
            _curves = [_storage.ReadCurve(Required("rTRC")), _storage.ReadCurve(Required("gTRC")),
                _storage.ReadCurve(Required("bTRC"))];
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
            // Compare against the rows' Hadamard bound, so the test follows the matrix scale.
            // Below one part in a million, s15Fixed16 encoding error dominates the inverse.
            double bound = Math.Sqrt(a * a + b * b + c * c) * Math.Sqrt(d * d + e * e + f * f)
                * Math.Sqrt(g * g + h * h + i * i);
            if (double.IsFinite(bound) && bound > 0 && Math.Abs(determinant) > 1e-6 * bound)
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
        var storage = new PdfIccStorageBudget(data.Length);
        Dictionary<ReadOnlyMemory<byte>, PdfIccTable> tables = [];
        try { return new(data, 1, tables, storage); }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            try
            {
                var perceptual = new PdfIccProfileTransform(data, 0, tables, storage)
                {
                    _attemptedIntents = 1 << 1
                };
                return perceptual;
            }
            catch (Exception alternate) when (alternate is FormatException or NotSupportedException)
            {
                var saturation = new PdfIccProfileTransform(data, 2, tables, storage)
                {
                    _attemptedIntents = (1 << 0) | (1 << 1)
                };
                return saturation;
            }
        }
    }

    internal PdfIccProfileTransform? ForIntent(int intent)
    {
        if (intent is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(intent));
        PdfIccProfileTransform root = _intentRoot ?? this;
        if (intent == root._intent || root._tableData.IsEmpty && intent != 3 && root._intent != 3) return root;
        lock (root._intentGate)
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
                else variant = new PdfIccProfileTransform(root._tableData, intent, root._tables, root._storage);
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
        // tables within this profile; variant construction holds the root's intent gate.
        if (_tables!.TryGetValue(data, out PdfIccTable? table)) return table;
        table = PdfIccTable.Read(data, _storage);
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
                Span<double> encoded =
                [
                    ClampComponent(0, device[0]) / 100,
                    (ClampComponent(1, device[1]) + 128) / 255,
                    (ClampComponent(2, device[2]) + 128) / 255,
                ];
                if (_lab && _forward.UsesLegacyLabEncoding)
                    for (int channel = 0; channel < 3; channel++) encoded[channel] *= 65280d / 65535;
                _forward.Transform(encoded, xyz);
            }
            else _forward.Transform(device, xyz);
            FinishForward(xyz);
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

    /// <summary>
    /// Prepares exact 8-bit CMYK evaluation when the forward table is a CMYK lookup table, or
    /// returns null when this transform has another shape.
    /// </summary>
    internal PdfIccLut.ByteInput? PrepareByteInput() =>
        _forward is PdfIccLut lut && !IsLabInput && Components == 4 ? lut.PrepareByteInput() : null;

    /// <summary>The same D50 XYZ as ToXyz for packed C, M, Y, K bytes (low to high).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void BytesToXyz(PdfIccLut.ByteInput input, uint ink, Span<double> xyz)
    {
        ((PdfIccLut)_forward!).TransformBytes(input, ink, xyz);
        FinishForward(xyz);
    }

    private void FinishForward(Span<double> xyz)
    {
        if (_lab)
        {
            double encoding = _forward!.UsesLegacyLabEncoding ? 65535d / 65280 : 1;
            double lightness = Math.Clamp(xyz[0] * encoding, 0, 1) * 100;
            double a = xyz[1] * encoding * 255 - 128;
            double b = xyz[2] * encoding * 255 - 128;
            double y = (lightness + 16) / 116;
            xyz[0] = 0.9642 * LabInverse(y + a / 500);
            xyz[1] = LabInverse(y);
            xyz[2] = 0.8249 * LabInverse(y - b / 200);
        }
        else for (int channel = 0; channel < 3; channel++) xyz[channel] *= _forward!.XyzEncodingScale;
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
        // Keep the legacy PCS encoding and forward tables unchanged. For a
        // Lab-to-CMYK reverse cube, preserve its piecewise linear diagonals.
        if (_lab && Components == 4 && _reverse is PdfIccLut { UsesLegacyLabEncoding: true } lut)
            lut.TransformTetrahedral(encoded, device);
        else _reverse.Transform(encoded, device, applyXyzMatrix: !_lab);
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
