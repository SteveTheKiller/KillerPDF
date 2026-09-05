using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Fonts;

/// <summary>A decoded source character, preserving its code for font-width lookup.</summary>
public readonly record struct PdfDecodedCharacter(uint Code, int ByteLength, string Text);

/// <summary>Reads explicit ToUnicode mappings without relying on a platform font library.</summary>
/// <remarks>Missing mappings are reported, not guessed.</remarks>
public sealed class PdfToUnicodeMap
{
    private static readonly Encoding Utf16 = new UnicodeEncoding(true, false, true);
    private readonly Dictionary<(uint Code, int Length), string> _characters = [];
    private readonly List<(uint Low, uint High, int Length)> _spaces = [];
    private PdfToUnicodeMap() { }

    internal static PdfToUnicodeMap Create(IReadOnlyDictionary<uint, string> characters, int byteLength)
    {
        var map = new PdfToUnicodeMap();
        map._spaces.Add((0, byteLength == 4 ? uint.MaxValue : (1u << (byteLength * 8)) - 1, byteLength));
        foreach (var pair in characters) map._characters.Add((pair.Key, byteLength), pair.Value);
        return map;
    }

    internal string? Lookup(uint code, int length) => _characters.GetValueOrDefault((code, length));

    internal static PdfToUnicodeMap CreateCodeSpaces(IEnumerable<(uint Low, uint High, int Length)> spaces)
    {
        var map = new PdfToUnicodeMap();
        map._spaces.AddRange(spaces);
        if (map._spaces.Count == 0) throw new FormatException("Font encoding has no character code space.");
        map.ValidateSpaces();
        return map;
    }

    /// <summary>Parses decoded CMap bytes with a bound on expanded mappings.</summary>
    public static PdfToUnicodeMap Parse(ReadOnlyMemory<byte> source, int maximumMappings = 65536)
        => ParseCore(source, maximumMappings, false);

    /// <summary>Parses a CMap while replacing malformed UTF-16 destinations.</summary>
    public static PdfToUnicodeMap ParseWithCompatibilityRecovery(
        ReadOnlyMemory<byte> source, int maximumMappings = 65536)
        => ParseCore(source, maximumMappings, false, compatibilityRecovery: true);

    internal static PdfToUnicodeMap ParseSimpleFont(ReadOnlyMemory<byte> source)
        => ParseFont(source, true);

    internal static PdfToUnicodeMap ParseFont(ReadOnlyMemory<byte> source, bool simpleFont,
        bool compatibilityRecovery = false, PdfToUnicodeMap? inherited = null)
        => ParseCore(PdfCMapMetadata.WithoutDictionaries(source), 65536, simpleFont,
            compatibilityRecovery, inherited);

    private static PdfToUnicodeMap ParseCore(ReadOnlyMemory<byte> source, int maximumMappings,
        bool simpleFont, bool compatibilityRecovery = false, PdfToUnicodeMap? inherited = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMappings);
        var map = new PdfToUnicodeMap();
        if (inherited is not null)
        {
            map._characters.EnsureCapacity(inherited._characters.Count);
            foreach (var pair in inherited._characters) map._characters.Add(pair.Key, pair.Value);
            map._spaces.AddRange(inherited._spaces);
        }
        var localMappings = new HashSet<(uint Code, int Length)>();
        string? block = null;
        int count = 0;
        foreach (var instruction in PdfContentStreamReader.Read(source))
        {
            string op = instruction.Operator;
            var operands = instruction.Operands;
            if (op == "usecmap")
            {
                if (inherited is null)
                    throw new NotSupportedException("Named inherited ToUnicode maps are not supported.");
                if (operands.Count != 1 || operands[0] is not PdfName)
                    throw new FormatException("Invalid inherited ToUnicode map reference.");
                continue;
            }
            if (op is "begincodespacerange" or "beginbfchar" or "beginbfrange")
            {
                if (block is not null || operands.Count != 1 || operands[0] is not PdfInteger number ||
                    number.Value < 0 || number.Value > maximumMappings)
                    throw new FormatException("Invalid ToUnicode block count.");
                block = op;
                count = (int)number.Value;
                continue;
            }
            if (block is null)
            {
                if (op is "endcodespacerange" or "endbfchar" or "endbfrange")
                    throw new FormatException("ToUnicode block end has no matching start.");
                continue;
            }
            if (op != "end" + block[5..]) throw new FormatException("Unterminated ToUnicode block.");
            int stride = block == "beginbfrange" ? 3 : 2;
            if ((long)count * stride != operands.Count) throw new FormatException("ToUnicode block count does not match its entries.");
            for (int i = 0; i < operands.Count; i += stride)
            {
                var low = Code(operands[i]);
                if (block == "beginbfchar")
                {
                    map.Add(low, Unicode(Bytes(operands[i + 1]), compatibilityRecovery),
                        maximumMappings, compatibilityRecovery, localMappings);
                    continue;
                }
                var high = Code(operands[i + 1]);
                if (low.Length != high.Length || high.Code < low.Code)
                    throw new FormatException("Invalid ToUnicode source range.");
                if (block == "begincodespacerange")
                {
                    if (map._spaces.Count >= 256) throw new FormatException("Too many ToUnicode code spaces.");
                    var space = (low.Code, high.Code, low.Length);
                    if (!map._spaces.Contains(space)) map._spaces.Add(space);
                    continue;
                }
                ulong length = (ulong)high.Code - low.Code + 1;
                if (length > (ulong)maximumMappings) throw new FormatException("ToUnicode range exceeds the mapping limit.");
                if (operands[i + 2] is PdfArray destinations)
                {
                    if ((ulong)destinations.Count != length) throw new FormatException("ToUnicode destination array has the wrong length.");
                    for (int j = 0; j < destinations.Count; j++)
                        map.Add((low.Code + (uint)j, low.Length),
                            Unicode(Bytes(destinations[j]), compatibilityRecovery), maximumMappings,
                            compatibilityRecovery, localMappings);
                }
                else
                {
                    byte[] destination = Bytes(operands[i + 2]);
                    for (ulong j = 0; j < length; j++)
                    {
                        map.Add((low.Code + (uint)j, low.Length),
                            Unicode(destination, compatibilityRecovery), maximumMappings,
                            compatibilityRecovery, localMappings);
                        if (j + 1 < length) Increment(destination);
                    }
                }
            }
            block = null;
        }
        if (block is not null) throw new FormatException("Unterminated ToUnicode block.");
        if (simpleFont)
        {
            // Simple-font strings contain one-byte codes even when a producer declares a two-byte CMap code space.
            var normalized = map._characters.Where(p => p.Key.Code <= 255).ToArray();
            map._characters.Clear();
            foreach (var pair in normalized) map._characters[(pair.Key.Code, 1)] = pair.Value;
            map._spaces.Clear();
            map._spaces.Add((0, 255, 1));
        }
        if (map._spaces.Count == 0) throw new FormatException("ToUnicode map has no code space.");
        map.ValidateSpaces();
        foreach (var key in map._characters.Keys)
            if (!map._spaces.Any(s => s.Length == key.Length && key.Code >= s.Low && key.Code <= s.High))
                throw new FormatException("ToUnicode mapping lies outside its code space.");
        return map;
    }

    /// <summary>Decodes character codes, retaining ligatures and supplementary Unicode characters.</summary>
    public IReadOnlyList<PdfDecodedCharacter> Decode(ReadOnlySpan<byte> source)
        => DecodeCore(source, null, false, false);

    /// <summary>Decodes character codes while replacing invalid or incomplete codes.</summary>
    public IReadOnlyList<PdfDecodedCharacter> DecodeWithCompatibilityRecovery(
        ReadOnlySpan<byte> source)
        => DecodeCore(source, null, true, true);

    internal IReadOnlyList<PdfDecodedCharacter> DecodeWithFallback(ReadOnlySpan<byte> source, Func<uint, string?>? fallback)
        => DecodeCore(source, fallback, true, false);

    internal IReadOnlyList<PdfDecodedCharacter> DecodeWithCompatibilityRecovery(
        ReadOnlySpan<byte> source, Func<uint, string?>? fallback)
        => DecodeCore(source, fallback, true, true);

    private List<PdfDecodedCharacter> DecodeCore(ReadOnlySpan<byte> source,
        Func<uint, string?>? fallback, bool replaceMissing, bool recoverInvalidCodes)
    {
        var result = new List<PdfDecodedCharacter>();
        int offset = 0;
        while (offset < source.Length)
        {
            uint code = 0;
            bool matched = false;
            for (int length = 1; length <= 4 && offset + length <= source.Length; length++)
            {
                code = (code << 8) | source[offset + length - 1];
                if (!_spaces.Any(s => s.Length == length && code >= s.Low && code <= s.High)) continue;
                if (!_characters.TryGetValue((code, length), out string? text))
                {
                    if (!replaceMissing) throw new NotSupportedException($"No Unicode mapping for character code {code:X}.");
                    text = fallback?.Invoke(code) ?? "\uFFFD";
                }
                result.Add(new PdfDecodedCharacter(code, length, text));
                offset += length;
                matched = true;
                break;
            }
            if (!matched)
            {
                if (!recoverInvalidCodes)
                    throw new FormatException(
                        "Text contains an incomplete or invalid character code.");
                code = source[offset++];
                result.Add(new PdfDecodedCharacter(
                    code, 1, fallback?.Invoke(code) ?? "\uFFFD"));
            }
        }
        return result;
    }

    private void ValidateSpaces()
    {
        for (int i = 0; i < _spaces.Count; i++)
        for (int j = i + 1; j < _spaces.Count; j++)
        {
            var (Low, High, Length) = _spaces[i];
            var b = _spaces[j];
            int length = Math.Min(Length, b.Length);
            uint al = Low >> (8 * (Length - length)), ah = High >> (8 * (Length - length));
            uint bl = b.Low >> (8 * (b.Length - length)), bh = b.High >> (8 * (b.Length - length));
            if (al <= bh && bl <= ah) throw new FormatException("Overlapping or ambiguous ToUnicode code spaces.");
        }
    }

    private void Add((uint Code, int Length) code, string text, int maximum,
        bool compatibilityRecovery, HashSet<(uint Code, int Length)>? localMappings = null)
    {
        if (localMappings is not null && !localMappings.Add(code))
        {
            if (compatibilityRecovery) return;
            throw new FormatException("Duplicate ToUnicode source mapping.");
        }
        if (!_characters.ContainsKey(code) && _characters.Count >= maximum)
            throw new FormatException("ToUnicode mapping limit exceeded.");
        _characters[code] = text;
    }

    private static (uint Code, int Length) Code(PdfObject value)
    {
        byte[] bytes = Bytes(value);
        if (bytes.Length is < 1 or > 4) throw new FormatException("Character codes must contain one to four bytes.");
        uint code = 0;
        foreach (byte b in bytes) code = (code << 8) | b;
        return (code, bytes.Length);
    }

    private static byte[] Bytes(PdfObject value) => value is PdfString text && text.Form == PdfStringForm.Hexadecimal
        ? text.Bytes.ToArray() : throw new FormatException("Expected a CMap hexadecimal string.");

    private static string Unicode(byte[] bytes, bool compatibilityRecovery)
    {
        if (bytes.Length == 0)
        {
            if (compatibilityRecovery) return "\uFFFD";
            throw new FormatException("Expected nonempty UTF-16BE text.");
        }
        if (bytes.Length % 2 != 0)
        {
            if (compatibilityRecovery) return Encoding.BigEndianUnicode.GetString(bytes);
            throw new FormatException("Expected nonempty UTF-16BE text.");
        }
        try { return Utf16.GetString(bytes); }
        catch (DecoderFallbackException e)
        {
            if (compatibilityRecovery)
                return Encoding.BigEndianUnicode.GetString(bytes);
            throw new FormatException("Invalid UTF-16BE mapping.", e);
        }
    }

    private static void Increment(byte[] bytes)
    {
        for (int i = bytes.Length - 1; i >= 0; i--)
            if (++bytes[i] != 0) return;
        throw new FormatException("ToUnicode destination range overflow.");
    }
}
