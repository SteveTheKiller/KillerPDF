using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Fonts;

/// <summary>A bounded reader for OpenType fonts with TrueType or CFF outlines.</summary>
public sealed class TrueTypeFont
{
    private const int MaximumTableCount = 4_096;
    private readonly byte[] _data;
    private readonly int _directoryOffset;
    private readonly Dictionary<uint, Table> _tables;
    private readonly ICmap _cmap;
    private readonly VariationCmap? _variationCmap;
    private readonly ushort[] _advanceWidths;

    internal bool HasUnicodeCharacterMap { get; private set; }

    private TrueTypeFont(byte[] data, Dictionary<uint, Table> tables, bool hasCffOutlines, bool extraction = false,
        int directoryOffset = 0)
    {
        _data = data;
        _directoryOffset = directoryOffset;
        _tables = tables;
        HasCffOutlines = hasCffOutlines;
        Table head = Required("head");
        Table hhea = Required("hhea");
        Table maxp = Required("maxp");
        Table hmtx = Required("hmtx");
        UnitsPerEm = U16(head, 18);
        if (UnitsPerEm is < 16 or > 16_384)
            throw Error("head.unitsPerEm is outside the TrueType range");
        Bounds = new TrueTypeBounds(S16(head, 36), S16(head, 38), S16(head, 40), S16(head, 42));
        Ascender = S16(hhea, 4);
        Descender = S16(hhea, 6);
        GlyphCount = U16(maxp, 4);
        int horizontalMetricCount = U16(hhea, 34);
        if (extraction && horizontalMetricCount > GlyphCount) horizontalMetricCount = GlyphCount;
        if (GlyphCount == 0 || horizontalMetricCount is < 1 || horizontalMetricCount > GlyphCount)
            throw Error("The horizontal metric count is invalid");
        _advanceWidths = ReadWidths(hmtx, GlyphCount, horizontalMetricCount, extraction);
        if (extraction && !TryTable("cmap", out _)) _cmap = new EmptyCmap();
        else
        {
            Table cmap = Required("cmap");
            try { _cmap = ReadCmap(cmap, extraction); }
            catch (NotSupportedException) when (extraction) { _cmap = new EmptyCmap(); }
            _variationCmap = ReadVariationCmap(cmap);
        }
        PostScriptName = extraction && !TryTable("name", out _) ? "EmbeddedFont" : ReadPostScriptName(Required("name"));

        if (TryTable("OS/2", out Table os2) && os2.Length >= 10)
            EmbeddingFlags = U16(os2, 8);
        if (TryTable("post", out Table post) && post.Length >= 8)
            ItalicAngle = S32(post, 4) / 65536d;
        DeclaredStyle = ReadDeclaredStyle(head);
    }

    private TrueTypeFontStyle ReadDeclaredStyle(Table head)
    {
        if (!TryTable("OS/2", out Table os2) || os2.Length < 64 || head.Length < 46)
            return TrueTypeFontStyle.Unknown;
        ushort version = U16(os2, 0);
        if (version > 5) return TrueTypeFontStyle.Unknown;
        ushort selection = U16(os2, 62);
        ushort macStyle = U16(head, 44);
        bool italic = (selection & 0x0001) != 0;
        bool bold = (selection & 0x0020) != 0;
        if (italic != ((macStyle & 0x0002) != 0) || bold != ((macStyle & 0x0001) != 0)
            || ((selection & 0x0040) != 0 && (italic || bold)))
            return TrueTypeFontStyle.Unknown;
        if (version >= 4 && (selection & 0x0200) != 0) return TrueTypeFontStyle.Oblique;
        return italic ? TrueTypeFontStyle.Italic : TrueTypeFontStyle.Normal;
    }

    /// <summary>Gets the font's PostScript name.</summary>
    public string PostScriptName { get; }
    /// <summary>Gets the number of font design units per em.</summary>
    public int UnitsPerEm { get; }
    /// <summary>Gets the number of glyphs declared by the font.</summary>
    public int GlyphCount { get; }
    /// <summary>Gets the horizontal-layout ascender in font design units.</summary>
    public int Ascender { get; }
    /// <summary>Gets the horizontal-layout descender in font design units.</summary>
    public int Descender { get; }
    /// <summary>Gets the global glyph bounding box in font design units.</summary>
    public TrueTypeBounds Bounds { get; }
    /// <summary>Gets the raw OS/2 embedding-permission flags.</summary>
    public ushort EmbeddingFlags { get; }
    /// <summary>Gets the italic angle in counterclockwise degrees from vertical.</summary>
    public double ItalicAngle { get; }
    /// <summary>Gets the style declared by the font's OS/2 and head flags.</summary>
    public TrueTypeFontStyle DeclaredStyle { get; }
    /// <summary>Gets whether the font permits embedding.</summary>
    public bool EmbeddingAllowed => (EmbeddingFlags & 0x0202) == 0;
    /// <summary>Gets whether the font permits subset embedding.</summary>
    public bool SubsettingAllowed => (EmbeddingFlags & 0x0100) == 0;
    /// <summary>Gets whether the OpenType font uses CFF or CFF2 outlines.</summary>
    public bool HasCffOutlines { get; }
    /// <summary>Gets the validated original OpenType font bytes.</summary>
    public ReadOnlyMemory<byte> FontData => _data;

    /// <summary>Loads and validates an OpenType font with TrueType or CFF outlines.</summary>
    public static TrueTypeFont Load(ReadOnlyMemory<byte> source)
        => LoadCore(source.ToArray(), false);

    internal static TrueTypeFont LoadForExtraction(ReadOnlyMemory<byte> source, string? preferredName = null)
        => LoadCore(source.ToArray(), true, preferredName);

    internal static TrueTypeFont LoadOwnedForExtraction(byte[] source)
        => LoadCore(source, true);

    private static TrueTypeFont LoadCore(byte[] data, bool extraction, string? preferredName = null)
    {
        if (data.Length < 12)
            throw Error("The font is shorter than an sfnt header");
        if (extraction && ReadU32(data, 0) == Tag("ttcf"))
            return LoadCollectionFace(data, preferredName);
        return LoadFace(data, 0, extraction);
    }

    // A TrueType collection embedded as one font program holds several faces whose table offsets
    // are relative to the whole file. Use the face whose PostScript name matches the PDF font,
    // otherwise the first face that loads.
    private static TrueTypeFont LoadCollectionFace(byte[] data, string? preferredName)
    {
        uint count = ReadU32(data, 8);
        if (count is 0 or > 1024 || 12L + count * 4L > data.Length)
            throw Error("The font collection header is invalid");
        string? wanted = FaceKey(preferredName);
        TrueTypeFont? first = null;
        FormatException? failure = null;
        for (int index = 0; index < (int)count; index++)
        {
            uint directory = ReadU32(data, 12 + index * 4);
            if (directory > data.Length - 12) continue;
            try
            {
                TrueTypeFont face = LoadFace(data, (int)directory, true);
                if (wanted is null || FaceKey(face.PostScriptName) == wanted) return face;
                first ??= face;
            }
            catch (FormatException exception) { failure ??= exception; }
        }
        return first ?? throw (failure ?? Error("The font collection has no readable face"));
    }

    private static string? FaceKey(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Length > 7 && name[6] == '+') name = name[7..];
        var key = new StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsAsciiLetterOrDigit(c)) key.Append(char.ToUpperInvariant(c));
        return key.Length == 0 ? null : key.ToString();
    }

    private static TrueTypeFont LoadFace(byte[] data, int directory, bool extraction)
    {
        uint scaler = ReadU32(data, directory);
        bool hasCffOutlines = scaler == Tag("OTTO");
        if (!hasCffOutlines && scaler is not 0x00010000 && scaler != Tag("true"))
            throw Error("The file is not a supported OpenType font");

        int tableCount = ReadU16(data, directory + 4);
        if (tableCount is < 1 or > MaximumTableCount || directory + 12L + tableCount * 16L > data.Length)
            throw Error("The sfnt table directory is invalid");
        var tables = new Dictionary<uint, Table>();
        for (int index = 0; index < tableCount; index++)
        {
            int record = directory + 12 + index * 16;
            uint tag = ReadU32(data, record);
            uint offset = ReadU32(data, record + 8);
            uint length = ReadU32(data, record + 12);
            if (offset > data.Length || length > data.Length - offset)
            {
                // Damaged embedded directories can end in a garbage record. Skip it; a required
                // table that is really missing still fails when the face reads it.
                if (extraction) continue;
                throw Error($"Font table {TagText(tag)} points outside the file");
            }
            if (!tables.TryAdd(tag, new Table((int)offset, (int)length)))
                throw Error($"Font table {TagText(tag)} is duplicated");
        }
        if (hasCffOutlines && !tables.ContainsKey(Tag("CFF "))
            && !tables.ContainsKey(Tag("CFF2")))
            throw Error("An OTTO font has no CFF or CFF2 outline table");
        return new TrueTypeFont(data, tables, hasCffOutlines, extraction, directory);
    }

    /// <summary>Maps a valid Unicode scalar to a glyph identifier, or zero when unmapped.</summary>
    public ushort GetGlyphId(int unicodeScalar)
    {
        if (!Rune.IsValid(unicodeScalar))
            throw new ArgumentOutOfRangeException(nameof(unicodeScalar));
        return _cmap.Map(unicodeScalar);
    }

    /// <summary>
    /// Maps a PostScript glyph name to a glyph identifier through the font's 'post' table
    /// (formats 1.0 and 2.0), or returns zero when the name is absent or the table has no names.
    /// </summary>
    internal ushort GetGlyphIdByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        _postNames ??= ReadPostNames();
        return _postNames.TryGetValue(name, out ushort glyph) ? glyph : (ushort)0;
    }

    private Dictionary<string, ushort>? _postNames;

    private Dictionary<string, ushort> ReadPostNames()
    {
        var names = new Dictionary<string, ushort>(StringComparer.Ordinal);
        if (!TryTable("post", out Table post) || post.Length < 32) return names;
        try
        {
            uint format = U32(post, 0);
            if (format == 0x00010000)
            {
                for (int glyph = 0; glyph < Math.Min(GlyphCount, MacGlyphNames.Length); glyph++)
                    names.TryAdd(MacGlyphNames[glyph], (ushort)glyph);
            }
            else if (format == 0x00020000 && post.Length >= 34)
            {
                int count = Math.Min((int)U16(post, 32), GlyphCount);
                int indexStart = 34;
                if (indexStart + count * 2 > post.Length) return names;
                var custom = new List<string>();
                int position = indexStart + U16(post, 32) * 2;
                while (position < post.Length)
                {
                    int length = _data[post.Offset + position];
                    if (position + 1 + length > post.Length) break;
                    custom.Add(Encoding.Latin1.GetString(_data, post.Offset + position + 1, length));
                    position += 1 + length;
                }
                for (int glyph = 0; glyph < count; glyph++)
                {
                    int index = U16(post, indexStart + glyph * 2);
                    string? name = index < MacGlyphNames.Length ? MacGlyphNames[index]
                        : index - MacGlyphNames.Length < custom.Count ? custom[index - MacGlyphNames.Length] : null;
                    if (name is not null) names.TryAdd(name, (ushort)glyph);
                }
            }
        }
        catch (FormatException)
        {
            // A truncated table keeps the names that were read before the damage.
        }
        return names;
    }

    // The 258 standard Macintosh glyph names that 'post' formats 1.0 and 2.0 index into.
    private static readonly string[] MacGlyphNames =
    [
        ".notdef", ".null", "nonmarkingreturn", "space", "exclam", "quotedbl", "numbersign",
        "dollar", "percent", "ampersand", "quotesingle", "parenleft", "parenright", "asterisk",
        "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four",
        "five", "six", "seven", "eight", "nine", "colon", "semicolon", "less", "equal",
        "greater", "question", "at", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L",
        "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "bracketleft",
        "backslash", "bracketright", "asciicircum", "underscore", "grave", "a", "b", "c", "d",
        "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v",
        "w", "x", "y", "z", "braceleft", "bar", "braceright", "asciitilde", "Adieresis",
        "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute", "agrave",
        "acircumflex", "adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave",
        "ecircumflex", "edieresis", "iacute", "igrave", "icircumflex", "idieresis", "ntilde",
        "oacute", "ograve", "ocircumflex", "odieresis", "otilde", "uacute", "ugrave",
        "ucircumflex", "udieresis", "dagger", "degree", "cent", "sterling", "section",
        "bullet", "paragraph", "germandbls", "registered", "copyright", "trademark", "acute",
        "dieresis", "notequal", "AE", "Oslash", "infinity", "plusminus", "lessequal",
        "greaterequal", "yen", "mu", "partialdiff", "summation", "product", "pi", "integral",
        "ordfeminine", "ordmasculine", "Omega", "ae", "oslash", "questiondown", "exclamdown",
        "logicalnot", "radical", "florin", "approxequal", "Delta", "guillemotleft",
        "guillemotright", "ellipsis", "nonbreakingspace", "Agrave", "Atilde", "Otilde", "OE",
        "oe", "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright",
        "divide", "lozenge", "ydieresis", "Ydieresis", "fraction", "currency",
        "guilsinglleft", "guilsinglright", "fi", "fl", "daggerdbl", "periodcentered",
        "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex",
        "Aacute", "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave",
        "Oacute", "Ocircumflex", "apple", "Ograve", "Uacute", "Ucircumflex", "Ugrave",
        "dotlessi", "circumflex", "tilde", "macron", "breve", "dotaccent", "ring", "cedilla",
        "hungarumlaut", "ogonek", "caron", "Lslash", "lslash", "Scaron", "scaron", "Zcaron",
        "zcaron", "brokenbar", "Eth", "eth", "Yacute", "yacute", "Thorn", "thorn", "minus",
        "multiply", "onesuperior", "twosuperior", "threesuperior", "onehalf", "onequarter",
        "threequarters", "franc", "Gbreve", "gbreve", "Idotaccent", "Scedilla", "scedilla",
        "Cacute", "cacute", "Ccaron", "ccaron", "dcroat"
    ];

    /// <summary>Maps a Unicode variation sequence, returning zero when the sequence is unsupported.</summary>
    public ushort GetGlyphId(int unicodeScalar, int variationSelector)
    {
        if (!Rune.IsValid(unicodeScalar))
            throw new ArgumentOutOfRangeException(nameof(unicodeScalar));
        if (!IsVariationSelector(variationSelector))
            throw new ArgumentOutOfRangeException(nameof(variationSelector));
        return _variationCmap?.Map(unicodeScalar, variationSelector, _cmap) ?? 0;
    }

    internal IReadOnlyList<FontGlyphMapping> MapText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Rune[] runes = [.. text.EnumerateRunes()];
        var result = new List<FontGlyphMapping>(runes.Length);
        for (int index = 0; index < runes.Length; index++)
        {
            Rune current = runes[index];
            if (index + 1 < runes.Length && IsVariationSelector(runes[index + 1].Value))
            {
                Rune selector = runes[++index];
                result.Add(new FontGlyphMapping(
                    GetGlyphId(current.Value, selector.Value), current.ToString() + selector));
            }
            else
                result.Add(new FontGlyphMapping(GetGlyphId(current.Value), current.ToString()));
        }
        return result;
    }

    /// <summary>Gets a glyph's horizontal advance in the original font design units.</summary>
    public int GetAdvanceWidth(ushort glyphId)
    {
        if (glyphId >= _advanceWidths.Length)
            throw new ArgumentOutOfRangeException(nameof(glyphId));
        return _advanceWidths[glyphId];
    }

    /// <summary>Gets a glyph advance width rounded to 1,000 PDF text units.</summary>
    public int GetPdfAdvanceWidth(ushort glyphId)
    {
        if (glyphId >= _advanceWidths.Length)
            throw new ArgumentOutOfRangeException(nameof(glyphId));
        return (int)Math.Round(
            _advanceWidths[glyphId] * 1000d / UnitsPerEm,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>Builds a deterministic TrueType subset while retaining original glyph identifiers.</summary>
    public byte[] CreateSubset(IEnumerable<ushort> glyphIds) => CreateSubset(glyphIds, hinting: true);

    // hinting false leaves the TrueType instructions out of the subset, for text whose exact
    // fit to a low-resolution pixel grid matters less than the file's size.
    internal byte[] CreateSubset(IEnumerable<ushort> glyphIds, bool hinting)
    {
        ArgumentNullException.ThrowIfNull(glyphIds);
        if (HasCffOutlines)
            throw new NotSupportedException(
                "CFF OpenType fonts are embedded in full because CFF subsetting is not yet available.");
        if (!SubsettingAllowed)
            throw new InvalidOperationException($"The embedding permissions in {PostScriptName} prohibit subsetting.");
        if (_directoryOffset != 0)
            throw new NotSupportedException("Faces read from a font collection are not subset.");
        return TrueTypeSubsetter.Create(_data, GlyphCount, glyphIds, hinting);
    }

    private ushort[] ReadWidths(Table table, int glyphCount, int metricCount, bool extraction)
    {
        // Only the advances are read. Embedded subsets often cut the trailing side-bearing
        // array short, which viewers accept, so extraction requires just the full metrics.
        int required = extraction ? checked(metricCount * 4)
            : checked(metricCount * 4 + (glyphCount - metricCount) * 2);
        if (table.Length < required)
            throw Error("The hmtx table is truncated");
        var widths = new ushort[glyphCount];
        for (int index = 0; index < metricCount; index++)
            widths[index] = U16(table, index * 4);
        ushort last = widths[metricCount - 1];
        for (int index = metricCount; index < glyphCount; index++)
            widths[index] = last;
        return widths;
    }

    private ICmap ReadCmap(Table cmap, bool extraction = false)
    {
        if (cmap.Length < 4)
            throw Error("The cmap table is truncated");
        int count = U16(cmap, 2);
        if (4L + count * 8L > cmap.Length)
            throw Error("The cmap encoding records are truncated");

        (int Score, int Offset, int Format, bool Unicode)? best = null;
        for (int index = 0; index < count; index++)
        {
            int record = 4 + index * 8;
            int platform = U16(cmap, record);
            int encoding = U16(cmap, record + 2);
            uint relative = U32(cmap, record + 4);
            if (relative > cmap.Length - 2)
                continue;
            int subtable = checked(cmap.Offset + (int)relative);
            int format = ReadU16(_data, subtable);
            int score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 500,
                (3, 10, 13) => 490,
                (3, 10, 10) => 480,
                (3, 10, 8) => 470,
                (0, _, 12) => 450,
                (0, _, 13) => 440,
                (0, _, 10) => 430,
                (0, _, 8) => 420,
                (3, 1, 4) => 400,
                // Some embedded subsets file a format 12 table under the BMP record instead of 3,10.
                (3, 1, 12) when extraction => 395,
                (0, _, 4) => 350,
                (3, 1, 6) => 340,
                (0, _, 6) => 330,
                (0, _, 0) => 320,
                (0, _, 2) => 310,
                (3, 0, 4) => 200,
                (1, 0, 0) when extraction => 100,
                (1, 0, 6) when extraction => 110,
                _ => 0
            };
            if (score > 0 && (!best.HasValue || score > best.Value.Score))
                best = (score, subtable, format, platform == 0 || platform == 3 && encoding is 1 or 10);
        }
        if (!best.HasValue)
            throw new NotSupportedException(
                "The font has no supported Unicode cmap format 0, 2, 4, 6, 8, 10, 12, or 13 subtable.");
        HasUnicodeCharacterMap = best.Value.Unicode;
        return best.Value.Format switch
        {
            0 => ReadFormat0(best.Value.Offset, cmap),
            2 => ReadFormat2(best.Value.Offset, cmap),
            4 => ReadFormat4(best.Value.Offset, cmap, extraction),
            6 => ReadFormat6(best.Value.Offset, cmap),
            8 => ReadFormat8(best.Value.Offset, cmap),
            10 => ReadFormat10(best.Value.Offset, cmap),
            12 => ReadFormat12(best.Value.Offset, cmap),
            13 => ReadFormat13(best.Value.Offset, cmap),
            _ => throw new InvalidOperationException()
        };
    }

    private VariationCmap? ReadVariationCmap(Table cmap)
    {
        int count = U16(cmap, 2);
        for (int index = 0; index < count; index++)
        {
            int record = 4 + index * 8;
            if (U16(cmap, record) != 0 || U16(cmap, record + 2) != 5)
                continue;
            uint relative = U32(cmap, record + 4);
            if (relative > cmap.Length - 10) continue;
            int offset = checked(cmap.Offset + (int)relative);
            if (ReadU16(_data, offset) == 14)
                return ReadFormat14(offset, cmap);
        }
        return null;
    }

    private VariationCmap ReadFormat14(int offset, Table parent)
    {
        if (offset + 10 > parent.End || ReadU16(_data, offset) != 14)
            throw Error("The cmap format 14 header is truncated");
        uint length = ReadU32(_data, offset + 2);
        uint recordCount = ReadU32(_data, offset + 6);
        if (length < 10 || length > parent.End - offset
            || recordCount > (length - 10) / 11 || recordCount > 1_000_000)
            throw Error("The cmap format 14 selector records are invalid");
        var records = new VariationSelectorRecord[recordCount];
        uint minimumDataOffset = checked(10u + recordCount * 11u);
        int previousSelector = -1;
        for (int index = 0; index < records.Length; index++)
        {
            int position = offset + 10 + index * 11;
            int selector = ReadU24(_data, position);
            uint defaultOffset = ReadU32(_data, position + 3);
            uint nonDefaultOffset = ReadU32(_data, position + 7);
            if (selector <= previousSelector || !IsVariationSelector(selector)
                || defaultOffset != 0 && (defaultOffset < minimumDataOffset || defaultOffset >= length)
                || nonDefaultOffset != 0 && (nonDefaultOffset < minimumDataOffset || nonDefaultOffset >= length))
                throw Error("The cmap format 14 selector records are not ordered or bounded");
            UnicodeRange[] defaults = defaultOffset == 0 ? []
                : ReadDefaultVariationRanges(offset, checked((int)defaultOffset), checked((int)length));
            VariationMapping[] mappings = nonDefaultOffset == 0 ? []
                : ReadNonDefaultVariationMappings(offset, checked((int)nonDefaultOffset), checked((int)length));
            records[index] = new VariationSelectorRecord(selector, defaults, mappings);
            previousSelector = selector;
        }
        return new VariationCmap(records);
    }

    private UnicodeRange[] ReadDefaultVariationRanges(int offset, int relative, int length)
    {
        if (relative > length - 4)
            throw Error("A cmap format 14 default UVS table is truncated");
        uint count = ReadU32(_data, offset + relative);
        if (count > (length - relative - 4) / 4 || count > 1_000_000)
            throw Error("A cmap format 14 default UVS table is invalid");
        var ranges = new UnicodeRange[count];
        int previousEnd = -1;
        for (int index = 0; index < ranges.Length; index++)
        {
            int position = offset + relative + 4 + index * 4;
            int start = ReadU24(_data, position);
            int end = start + _data[position + 3];
            if (start <= previousEnd || end > 0x10FFFF
                || !Rune.IsValid(start) || !Rune.IsValid(end)
                || start <= 0xDFFF && end >= 0xD800)
                throw Error("Cmap format 14 default UVS ranges overlap or exceed Unicode");
            ranges[index] = new UnicodeRange(start, end);
            previousEnd = end;
        }
        return ranges;
    }

    private VariationMapping[] ReadNonDefaultVariationMappings(
        int offset, int relative, int length)
    {
        if (relative > length - 4)
            throw Error("A cmap format 14 non-default UVS table is truncated");
        uint count = ReadU32(_data, offset + relative);
        if (count > (length - relative - 4) / 5 || count > 1_000_000)
            throw Error("A cmap format 14 non-default UVS table is invalid");
        var mappings = new VariationMapping[count];
        int previous = -1;
        for (int index = 0; index < mappings.Length; index++)
        {
            int position = offset + relative + 4 + index * 5;
            int scalar = ReadU24(_data, position);
            int glyph = ReadU16(_data, position + 3);
            if (scalar <= previous || !Rune.IsValid(scalar) || glyph >= GlyphCount)
                throw Error("Cmap format 14 non-default UVS mappings are invalid");
            mappings[index] = new VariationMapping(scalar, checked((ushort)glyph));
            previous = scalar;
        }
        return mappings;
    }

    private ByteCmap ReadFormat0(int offset, Table parent)
    {
        if (offset + 262 > parent.End || ReadU16(_data, offset) != 0
            || ReadU16(_data, offset + 2) < 262)
            throw Error("The cmap format 0 glyph array is truncated");
        return new ByteCmap(_data, offset + 6, GlyphCount);
    }

    private Format2Cmap ReadFormat2(int offset, Table parent)
    {
        const int keysLength = 512;
        const int subHeadersOffset = 6 + keysLength;
        if (offset + subHeadersOffset + 8 > parent.End || ReadU16(_data, offset) != 2)
            throw Error("The cmap format 2 header is truncated");
        int length = ReadU16(_data, offset + 2);
        if (length < subHeadersOffset + 8 || offset + length > parent.End)
            throw Error("The cmap format 2 length is invalid");
        int maximumKey = 0;
        for (int index = 0; index < 256; index++)
        {
            int key = ReadU16(_data, offset + 6 + index * 2);
            if (key % 8 != 0)
                throw Error("The cmap format 2 subheader keys are invalid");
            maximumKey = Math.Max(maximumKey, key);
        }
        int subHeaderCount = maximumKey / 8 + 1;
        if (subHeadersOffset + subHeaderCount * 8L > length)
            throw Error("The cmap format 2 subheaders are truncated");
        for (int index = 0; index < subHeaderCount; index++)
        {
            int position = offset + subHeadersOffset + index * 8;
            int first = ReadU16(_data, position);
            int count = ReadU16(_data, position + 2);
            int range = ReadU16(_data, position + 6);
            long glyphStart = (long)position + 6 + range;
            if (first + count > 256 || count > 0
                && (glyphStart < offset || glyphStart + count * 2L > offset + length))
                throw Error("The cmap format 2 glyph ranges are invalid");
        }
        return new Format2Cmap(_data, offset, length, GlyphCount);
    }

    private Format12Cmap ReadFormat12(int offset, Table parent)
    {
        if (offset + 16 > parent.End || ReadU16(_data, offset) != 12)
            throw Error("The cmap format 12 header is truncated");
        uint length = ReadU32(_data, offset + 4);
        uint groupCount = ReadU32(_data, offset + 12);
        if (length < 16 || length > parent.End - offset
            || groupCount > (length - 16) / 12 || groupCount > 1_000_000)
            throw Error("The cmap format 12 groups are invalid");
        var groups = new Format12Group[groupCount];
        uint previousEnd = 0;
        for (int index = 0; index < groups.Length; index++)
        {
            int position = offset + 16 + index * 12;
            uint start = ReadU32(_data, position);
            uint end = ReadU32(_data, position + 4);
            uint glyph = ReadU32(_data, position + 8);
            if (start > end || end > 0x10FFFF || (index > 0 && start <= previousEnd)
                || glyph >= GlyphCount || end - start >= GlyphCount - glyph)
                throw Error("The cmap format 12 groups are not ordered valid glyph ranges");
            groups[index] = new Format12Group(start, end, glyph);
            previousEnd = end;
        }
        return new Format12Cmap(groups);
    }

    private Format4Cmap ReadFormat4(int offset, Table parent, bool extraction)
    {
        if (offset + 14 > parent.End || ReadU16(_data, offset) != 4)
            throw Error("The cmap format 4 header is truncated");
        int length = ReadU16(_data, offset + 2);
        int segmentCount = ReadU16(_data, offset + 6) / 2;
        int minimumLength = checked(16 + segmentCount * 8);
        if (length < 16 || segmentCount < 1 || minimumLength > length
            || offset + Math.Min(length, minimumLength) > parent.End)
            throw Error("The cmap format 4 segments are invalid");
        if (offset + length > parent.End)
        {
            if (!extraction) throw Error("The cmap format 4 segments are invalid");
            length = parent.End - offset;
        }
        return new Format4Cmap(_data, offset, length, segmentCount, GlyphCount);
    }

    private TrimmedCmap ReadFormat6(int offset, Table parent)
    {
        if (offset + 10 > parent.End || ReadU16(_data, offset) != 6)
            throw Error("The cmap format 6 header is truncated");
        int length = ReadU16(_data, offset + 2);
        int first = ReadU16(_data, offset + 6);
        int count = ReadU16(_data, offset + 8);
        if (length < 10 || offset + length > parent.End || 10L + count * 2L > length)
            throw Error("The cmap format 6 glyph array is invalid");
        return new TrimmedCmap(_data, offset + 10, first, count, GlyphCount);
    }

    private Format8Cmap ReadFormat8(int offset, Table parent)
    {
        const int groupsOffset = 8_208;
        if (offset + groupsOffset > parent.End || ReadU16(_data, offset) != 8
            || ReadU16(_data, offset + 2) != 0)
            throw Error("The cmap format 8 header is truncated or invalid");
        uint length = ReadU32(_data, offset + 4);
        uint groupCount = ReadU32(_data, offset + 8_204);
        if (length < groupsOffset || length > parent.End - offset
            || groupCount > (length - groupsOffset) / 12 || groupCount > 1_000_000)
            throw Error("The cmap format 8 groups are invalid");
        var groups = new Format12Group[groupCount];
        uint previousEnd = 0;
        for (int index = 0; index < groups.Length; index++)
        {
            int position = offset + groupsOffset + index * 12;
            uint start = ReadU32(_data, position);
            uint end = ReadU32(_data, position + 4);
            uint glyph = ReadU32(_data, position + 8);
            if (start > end || index > 0 && start <= previousEnd
                || glyph >= GlyphCount || end - start >= GlyphCount - glyph
                || !ValidFormat8Range(offset, start, end))
                throw Error("The cmap format 8 groups are not ordered valid character ranges");
            groups[index] = new Format12Group(start, end, glyph);
            previousEnd = end;
        }
        return new Format8Cmap(_data, offset, groups);
    }

    private bool ValidFormat8Range(int offset, uint start, uint end)
    {
        if (end <= ushort.MaxValue)
        {
            for (uint value = start; value <= end; value++)
                if (IsFormat8Start(_data, offset, checked((int)value))) return false;
            return true;
        }
        int startHigh = (int)(start >> 16);
        int endHigh = (int)(end >> 16);
        int startLow = (int)(start & 0xFFFF);
        int endLow = (int)(end & 0xFFFF);
        return startHigh == endHigh && startHigh is >= 0xD800 and <= 0xDBFF
            && startLow is >= 0xDC00 and <= 0xDFFF
            && endLow is >= 0xDC00 and <= 0xDFFF
            && IsFormat8Start(_data, offset, startHigh);
    }

    private static bool IsFormat8Start(byte[] data, int offset, int value) =>
        (data[offset + 12 + value / 8] & (1 << (7 - value % 8))) != 0;

    private TrimmedCmap ReadFormat10(int offset, Table parent)
    {
        if (offset + 20 > parent.End || ReadU16(_data, offset) != 10)
            throw Error("The cmap format 10 header is truncated");
        uint length = ReadU32(_data, offset + 4);
        uint first = ReadU32(_data, offset + 12);
        uint count = ReadU32(_data, offset + 16);
        if (length < 20 || length > parent.End - offset || count > (length - 20) / 2
            || count > 1_000_000 || first > 0x10FFFF
            || count > 0 && first + count - 1 > 0x10FFFF)
            throw Error("The cmap format 10 glyph array is invalid");
        return new TrimmedCmap(
            _data, offset + 20, checked((int)first), checked((int)count), GlyphCount);
    }

    private Format13Cmap ReadFormat13(int offset, Table parent)
    {
        if (offset + 16 > parent.End || ReadU16(_data, offset) != 13)
            throw Error("The cmap format 13 header is truncated");
        uint length = ReadU32(_data, offset + 4);
        uint groupCount = ReadU32(_data, offset + 12);
        if (length < 16 || length > parent.End - offset
            || groupCount > (length - 16) / 12 || groupCount > 1_000_000)
            throw Error("The cmap format 13 groups are invalid");
        var groups = new Format13Group[groupCount];
        uint previousEnd = 0;
        for (int index = 0; index < groups.Length; index++)
        {
            int position = offset + 16 + index * 12;
            uint start = ReadU32(_data, position);
            uint end = ReadU32(_data, position + 4);
            uint glyph = ReadU32(_data, position + 8);
            if (start > end || end > 0x10FFFF || index > 0 && start <= previousEnd
                || glyph >= GlyphCount)
                throw Error("The cmap format 13 groups are not ordered valid glyph ranges");
            groups[index] = new Format13Group(start, end, checked((ushort)glyph));
            previousEnd = end;
        }
        return new Format13Cmap(groups);
    }

    private string ReadPostScriptName(Table name)
    {
        if (name.Length < 6)
            throw Error("The name table is truncated");
        int count = U16(name, 2);
        int strings = U16(name, 4);
        if (6L + count * 12L > name.Length || strings > name.Length)
            throw Error("The name table records are invalid");
        string? fallback = null;
        for (int index = 0; index < count; index++)
        {
            int record = 6 + index * 12;
            int platform = U16(name, record);
            int nameId = U16(name, record + 6);
            int length = U16(name, record + 8);
            int relative = U16(name, record + 10);
            if (nameId != 6 || strings + relative > name.Length || length > name.Length - strings - relative)
                continue;
            ReadOnlySpan<byte> bytes = _data.AsSpan(name.Offset + strings + relative, length);
            string value = platform is 0 or 3
                ? DecodeBigEndianUnicode(bytes)
                : Encoding.Latin1.GetString(bytes);
            if (platform == 3 && value.Length > 0)
                return value;
            if (value.Length > 0)
                fallback ??= value;
        }
        return fallback ?? "UnnamedTrueTypeFont";
    }

    private static string DecodeBigEndianUnicode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0)
            return string.Empty;
        try
        {
            return PdfUnicodeEncoding.DecodeBigEndian(bytes, "A TrueType name record");
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private Table Required(string tag) =>
        TryTable(tag, out Table table) ? table : throw Error($"Required font table {tag} is missing");
    private bool TryTable(string tag, out Table table) => _tables.TryGetValue(Tag(tag), out table);

    /// <summary>
    /// Gets where a glyph's outline starts and ends horizontally, in font design units, from its
    /// TrueType glyph header. False for an empty glyph, for CFF outlines and for a face read from
    /// a collection, whose extents are not read here.
    /// </summary>
    internal bool TryGetGlyphHorizontalExtent(ushort glyphId, out int xMin, out int xMax)
    {
        (xMin, xMax) = (0, 0);
        if (HasCffOutlines || _directoryOffset != 0 || glyphId >= GlyphCount
            || !TryTable("head", out Table head) || head.Length < 54
            || !TryTable("loca", out Table loca) || !TryTable("glyf", out Table glyf))
            return false;
        bool longOffsets = S16(head, 50) != 0;
        int entry = longOffsets ? 4 : 2;
        if ((glyphId + 2) * entry > loca.Length) return false;
        long start = longOffsets ? U32(loca, glyphId * 4) : U16(loca, glyphId * 2) * 2L;
        long end = longOffsets ? U32(loca, glyphId * 4 + 4) : U16(loca, glyphId * 2 + 2) * 2L;
        if (end - start < 10 || end > glyf.Length) return false;
        xMin = S16(glyf, (int)start + 2);
        xMax = S16(glyf, (int)start + 6);
        return xMax > xMin;
    }

    /// <summary>Gets the absolute byte range of a table in <see cref="FontData"/>.</summary>
    internal bool TryGetTableRange(string tag, out int offset, out int length)
    {
        bool found = TryTable(tag, out Table table);
        (offset, length) = (table.Offset, table.Length);
        return found;
    }
    private ushort U16(Table table, int offset) =>
        offset >= 0 && offset + 2 <= table.Length ? ReadU16(_data, table.Offset + offset) : throw Error("A font table is truncated");
    private short S16(Table table, int offset) => unchecked((short)U16(table, offset));
    private uint U32(Table table, int offset) =>
        offset >= 0 && offset + 4 <= table.Length ? ReadU32(_data, table.Offset + offset) : throw Error("A font table is truncated");
    private int S32(Table table, int offset) => unchecked((int)U32(table, offset));

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
    private static int ReadU24(ReadOnlySpan<byte> data, int offset) =>
        data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2];
    private static bool IsVariationSelector(int scalar) =>
        scalar is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF;
    private static uint Tag(string value) =>
        value.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(value)) : throw new ArgumentException("A font tag has four bytes.");
    private static string TagText(uint value) => Encoding.ASCII.GetString([
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    private static FormatException Error(string message) => new(message);

    private readonly record struct Table(int Offset, int Length) { public int End => Offset + Length; }
    private interface ICmap { ushort Map(int scalar); }
    private sealed class EmptyCmap : ICmap { public ushort Map(int scalar) => 0; }
    private readonly record struct Format12Group(uint Start, uint End, uint StartGlyph);
    private readonly record struct Format13Group(uint Start, uint End, ushort Glyph);
    private readonly record struct UnicodeRange(int Start, int End);
    private readonly record struct VariationMapping(int Scalar, ushort Glyph);
    private sealed record VariationSelectorRecord(
        int Selector, UnicodeRange[] Defaults, VariationMapping[] Mappings);

    private sealed class VariationCmap(VariationSelectorRecord[] records)
    {
        public ushort Map(int scalar, int selector, ICmap baseCmap)
        {
            int recordIndex = BinarySearch(records.Length,
                index => selector.CompareTo(records[index].Selector));
            if (recordIndex < 0) return 0;
            VariationSelectorRecord record = records[recordIndex];
            int mappingIndex = BinarySearch(record.Mappings.Length,
                index => scalar.CompareTo(record.Mappings[index].Scalar));
            if (mappingIndex >= 0) return record.Mappings[mappingIndex].Glyph;
            int low = 0;
            int high = record.Defaults.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                UnicodeRange range = record.Defaults[middle];
                if (scalar < range.Start) high = middle - 1;
                else if (scalar > range.End) low = middle + 1;
                else return baseCmap.Map(scalar);
            }
            return 0;
        }

        private static int BinarySearch(int count, Func<int, int> compare)
        {
            int low = 0;
            int high = count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                int result = compare(middle);
                if (result < 0) high = middle - 1;
                else if (result > 0) low = middle + 1;
                else return middle;
            }
            return -1;
        }
    }

    private sealed class Format12Cmap(Format12Group[] groups) : ICmap
    {
        public ushort Map(int scalar)
        {
            int low = 0;
            int high = groups.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                Format12Group group = groups[middle];
                if ((uint)scalar < group.Start) high = middle - 1;
                else if ((uint)scalar > group.End) low = middle + 1;
                else return checked((ushort)(group.StartGlyph + (uint)scalar - group.Start));
            }
            return 0;
        }
    }

    private sealed class Format8Cmap(
        byte[] data, int offset, Format12Group[] groups) : ICmap
    {
        public ushort Map(int scalar)
        {
            if ((uint)scalar > 0x10FFFF || scalar is >= 0xD800 and <= 0xDFFF)
                return 0;
            uint code;
            if (scalar <= ushort.MaxValue)
            {
                if (IsFormat8Start(data, offset, scalar)) return 0;
                code = (uint)scalar;
            }
            else
            {
                int value = scalar - 0x10000;
                int high = 0xD800 + (value >> 10);
                int low = 0xDC00 + (value & 0x3FF);
                if (!IsFormat8Start(data, offset, high)) return 0;
                code = ((uint)high << 16) | (uint)low;
            }
            int left = 0;
            int right = groups.Length - 1;
            while (left <= right)
            {
                int middle = left + ((right - left) / 2);
                Format12Group group = groups[middle];
                if (code < group.Start) right = middle - 1;
                else if (code > group.End) left = middle + 1;
                else return checked((ushort)(group.StartGlyph + code - group.Start));
            }
            return 0;
        }
    }

    private sealed class Format13Cmap(Format13Group[] groups) : ICmap
    {
        public ushort Map(int scalar)
        {
            int low = 0;
            int high = groups.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                Format13Group group = groups[middle];
                if ((uint)scalar < group.Start) high = middle - 1;
                else if ((uint)scalar > group.End) low = middle + 1;
                else return group.Glyph;
            }
            return 0;
        }
    }

    private sealed class TrimmedCmap(
        byte[] data, int glyphOffset, int firstScalar, int count, int glyphCount) : ICmap
    {
        public ushort Map(int scalar)
        {
            int index = scalar - firstScalar;
            if (index < 0 || index >= count) return 0;
            ushort glyph = ReadU16(data, glyphOffset + index * 2);
            return glyph < glyphCount ? glyph : (ushort)0;
        }
    }

    private sealed class ByteCmap(byte[] data, int glyphOffset, int glyphCount) : ICmap
    {
        public ushort Map(int scalar)
        {
            if ((uint)scalar > byte.MaxValue) return 0;
            byte glyph = data[glyphOffset + scalar];
            return glyph < glyphCount ? glyph : (ushort)0;
        }
    }

    private sealed class Format2Cmap(
        byte[] data, int offset, int length, int glyphCount) : ICmap
    {
        private const int SubHeadersOffset = 518;

        public ushort Map(int scalar)
        {
            if ((uint)scalar > ushort.MaxValue) return 0;
            int high = scalar >> 8;
            int low = scalar & 0xFF;
            int key = ReadU16(data, offset + 6 + high * 2);
            int subHeader = offset + SubHeadersOffset + key;
            int first = ReadU16(data, subHeader);
            int count = ReadU16(data, subHeader + 2);
            if (low < first || low - first >= count) return 0;
            int delta = unchecked((short)ReadU16(data, subHeader + 4));
            int range = ReadU16(data, subHeader + 6);
            int address = subHeader + 6 + range + (low - first) * 2;
            if (address < offset || address + 2 > offset + length) return 0;
            int glyph = ReadU16(data, address);
            if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
            return glyph < glyphCount ? (ushort)glyph : (ushort)0;
        }
    }

    private sealed class Format4Cmap(
        byte[] data, int offset, int length, int segmentCount, int glyphCount) : ICmap
    {
        public ushort Map(int scalar)
        {
            if (scalar > ushort.MaxValue)
                return 0;
            int startCodes = offset + 16 + segmentCount * 2;
            int deltas = startCodes + segmentCount * 2;
            int ranges = deltas + segmentCount * 2;
            for (int index = 0; index < segmentCount; index++)
            {
                int end = ReadU16(data, offset + 14 + index * 2);
                if (scalar > end)
                    continue;
                int start = ReadU16(data, startCodes + index * 2);
                if (scalar < start)
                    return 0;
                int delta = unchecked((short)ReadU16(data, deltas + index * 2));
                int range = ReadU16(data, ranges + index * 2);
                int glyph = range == 0
                    ? (scalar + delta) & 0xFFFF
                    : ReadGlyph(index, scalar, start, delta, range, ranges);
                return glyph >= glyphCount ? (ushort)0 : (ushort)glyph;
            }
            return 0;
        }

        private int ReadGlyph(int index, int scalar, int start, int delta, int range, int ranges)
        {
            int address = ranges + index * 2 + range + (scalar - start) * 2;
            if (address < offset || address + 2 > offset + length)
                return 0;
            int glyph = ReadU16(data, address);
            return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
        }
    }
}

/// <summary>A font-wide glyph bounding box expressed in design units.</summary>
/// <param name="XMin">The minimum horizontal coordinate.</param>
/// <param name="YMin">The minimum vertical coordinate.</param>
/// <param name="XMax">The maximum horizontal coordinate.</param>
/// <param name="YMax">The maximum vertical coordinate.</param>
public readonly record struct TrueTypeBounds(int XMin, int YMin, int XMax, int YMax);
internal readonly record struct FontGlyphMapping(ushort Glyph, string UnicodeSequence);
