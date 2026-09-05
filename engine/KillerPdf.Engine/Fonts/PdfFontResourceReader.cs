using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Fonts;

/// <summary>Resolves PDF font encodings, embedded metrics, and character widths for extraction.</summary>
public static class PdfFontResourceReader
{
    /// <summary>Reads a page font resource without loading a platform font or rendering library.</summary>
    public static PdfExtractionFont Read(PdfDocument document, PdfDictionary font)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(font);
        var reader = new Reader(document);
        return reader.Read(font);
    }

    private sealed class Reader(PdfDocument document)
    {
        internal PdfExtractionFont Read(PdfDictionary font)
        {
            string fontName = Name(Get(font, "BaseFont")) ?? "Unknown";
            string metricsName = fontName.Length > 7 && fontName[6] == '+' ? fontName[7..] : fontName;
            string? subtype = Name(Get(font, "Subtype"));
            bool composite = subtype == "Type0";
            PdfDictionary metrics = font;
            if (composite)
            {
                if (Get(font, "DescendantFonts") is not PdfArray descendants || descendants.Count != 1
                    || Resolve(descendants[0]) is not PdfDictionary descendant)
                    throw new FormatException("A Type0 font must have one descendant font.");
                metrics = descendant;
            }
            var descriptor = Get(metrics, "FontDescriptor") as PdfDictionary;
            byte[]? embeddedData = (Get(descriptor, "FontFile2") ?? Get(descriptor, "FontFile3")) is PdfStream embeddedStream
                ? Decode(embeddedStream) : null;
            TrueTypeFont? embedded = ReadEmbedded(embeddedData);
            PdfCffGlyphReader? cff = embeddedData is null ? null : PdfCffGlyphReader.TryRead(embeddedData);
            PdfType1GlyphReader? type1 = Get(descriptor, "FontFile") is PdfStream type1Stream
                ? PdfType1GlyphReader.TryRead(Decode(type1Stream), checked((int)Number(Get(type1Stream.Dictionary, "Length1"), 0)),
                    checked((int)Number(Get(type1Stream.Dictionary, "Length2"), 0))) : null;
            TrueTypeFont? substitute = subtype is not null and not "Type3"
                ? PdfStandardFontSubstitutes.Find(metricsName) : null;
            var embeddedOutlines = embedded is null ? null : new OutlineReader(embedded);
            var substituteOutlines = substitute is null ? null : new OutlineReader(substitute);
            var widths = new Dictionary<uint, double>();
            var simpleText = new Dictionary<uint, string>();
            string[] glyphNames = [];
            Func<uint, uint>? cidSelector = null;
            int codeLength = composite ? 2 : 1;
            PdfToUnicodeMap? encodingSpaces = null;
            PdfPredefinedCMaps? predefined = null;
            string? encodingName = Name(Get(font, "Encoding"));
            bool vertical = encodingName?.EndsWith("-V", StringComparison.Ordinal) == true;
            if (composite)
            {
                ReadCidWidths(Get(metrics, "W") as PdfArray, widths);
                if (Get(font, "Encoding") is PdfStream encodingStream)
                {
                    var (Map, Spaces, Vertical) = ReadCidMap(Decode(encodingStream));
                    cidSelector = c => Map.GetValueOrDefault(c, 0u);
                    encodingSpaces = PdfToUnicodeMap.CreateCodeSpaces(Spaces);
                    vertical = Vertical;
                }
                else if (encodingName is not "Identity-H" and not "Identity-V")
                {
                    predefined = (encodingName is null ? null : PdfPredefinedCMaps.Find(encodingName)) ?? throw new NotSupportedException($"Predefined composite encoding /{encodingName} is not supported.");
                    cidSelector = predefined.Cid;
                }
            }
            else
            {
                PdfObject? encoding = Get(font, "Encoding");
                var encodingDictionary = encoding as PdfDictionary;
                encodingName ??= encodingDictionary is null ? null : Name(Get(encodingDictionary, "BaseEncoding"));
                string[]? builtInEncoding = encodingName is null ? type1?.EncodingNames?.ToArray() : null;
                encodingName ??= metricsName is "Symbol" ? "SymbolEncoding"
                    : metricsName is "ZapfDingbats" ? "ZapfDingbatsEncoding" : "StandardEncoding";
                glyphNames = builtInEncoding ?? PdfFontTables.EncodingNames(encodingName)
                    ?? (Get(font, "ToUnicode") is PdfStream ? Enumerable.Repeat(string.Empty, 256).ToArray()
                        : throw new NotSupportedException($"Font encoding /{encodingName} is not supported."));
                if (encodingDictionary is not null && Get(encodingDictionary, "Differences") is PdfArray differences)
                {
                    int index = -1;
                    foreach (PdfObject raw in differences)
                    {
                        PdfObject value = Resolve(raw);
                        if (value is PdfInteger number) index = checked((int)number.Value);
                        else if (value is PdfName name && index is >= 0 and < 256) glyphNames[index++] = name.ValueAsLatin1();
                        else throw new FormatException("Invalid simple-font encoding difference.");
                    }
                }
                for (uint code = 0; code < 256; code++)
                {
                    string? text = PdfFontTables.GlyphText(glyphNames[code]);
                    if (text is null && Name(Get(font, "Subtype")) == "Type3") text = ((char)code).ToString();
                    if (text is not null) simpleText[code] = text;
                    double? standardWidth = PdfStandardGlyphBounds.Width(metricsName, glyphNames[code]);
                    if (standardWidth is double width) widths[code] = width;
                }
                if (Get(font, "Widths") is PdfArray values)
                {
                    int first = checked((int)Number(Get(font, "FirstChar"), 0));
                    if (first < 0 || first + (long)values.Count > 256) throw new FormatException("Invalid simple-font width range.");
                    for (int i = 0; i < values.Count; i++) widths[(uint)(first + i)] = Number(Resolve(values[i]));
                }
            }

            PdfToUnicodeMap unicode = Get(font, "ToUnicode") is PdfStream unicodeStream
                ? PdfToUnicodeMap.ParseFont(Decode(unicodeStream), !composite)
                : encodingSpaces ?? PdfToUnicodeMap.Create(simpleText, codeLength);
            byte[]? cidToGid = Get(metrics, "CIDToGIDMap") is PdfStream gidStream ? Decode(gidStream) : null;
            ushort Glyph(uint code)
            {
                if (composite)
                {
                    uint cid = cidSelector?.Invoke(code) ?? code;
                    if (cidToGid is not null)
                        return cid < cidToGid.Length / 2 ? BinaryPrimitives.ReadUInt16BigEndian(cidToGid.AsSpan((int)cid * 2, 2)) : (ushort)0;
                    return cid <= ushort.MaxValue ? (ushort)cid : (ushort)0;
                }
                string? text = unicode.Lookup(code, codeLength)
                    ?? simpleText.GetValueOrDefault(code);
                if (embedded is null || string.IsNullOrEmpty(text)) return 0;
                int scalar = char.ConvertToUtf32(text, 0);
                ushort glyph = embedded.GetGlyphId(scalar);
                if (glyph == 0 && code < 256) glyph = embedded.GetGlyphId((int)code);
                if (glyph == 0 && code < 256) glyph = embedded.GetGlyphId((int)code + 0xF000);
                return glyph;
            }
            ushort SubstituteGlyph(uint code)
            {
                string? text = unicode.Lookup(code, codeLength)
                    ?? simpleText.GetValueOrDefault(code);
                if (substitute is null || string.IsNullOrEmpty(text)) return 0;
                int scalar = char.ConvertToUtf32(text, 0);
                ushort glyph = substitute.GetGlyphId(scalar);
                if (glyph == 0 && code < 256) glyph = substitute.GetGlyphId((int)code);
                return glyph;
            }
            if (!composite && embedded is not null)
                for (uint code = 0; code < 256; code++)
                    if (!widths.ContainsKey(code) && Glyph(code) is ushort glyph && glyph < embedded.GlyphCount)
                        widths[code] = embedded.GetPdfAdvanceWidth(glyph);
            var reverse = new Lazy<Dictionary<ushort, string>>(() => ReverseUnicode(embedded));
            var systemInfo = Get(metrics, "CIDSystemInfo") as PdfDictionary;
            string registry = Get(systemInfo, "Registry") is PdfString registryText ? Encoding.Latin1.GetString(registryText.Bytes.Span) : "Adobe";
            string ordering = Get(systemInfo, "Ordering") is PdfString orderingText ? Encoding.Latin1.GetString(orderingText.Bytes.Span) : "Identity";
            string? Fallback(uint code) => !composite ? simpleText.GetValueOrDefault(code)
                : PdfPredefinedCMaps.Unicode(registry + "-" + ordering, vertical, cidSelector?.Invoke(code) ?? code)
                    ?? (embedded is null ? null : reverse.Value.GetValueOrDefault(Glyph(code)));
            int CffGlyph(uint code)
            {
                if (cff is null) return -1;
                if (embedded is not null)
                {
                    ushort glyph = Glyph(code);
                    return glyph == 0 ? -1 : glyph;
                }
                return composite ? cff.FindCid(cidSelector?.Invoke(code) ?? code)
                    : code < glyphNames.Length ? cff.FindGlyph(glyphNames[code]) : -1;
            }
            var verticalMetrics = new Dictionary<uint, PdfVerticalGlyphMetrics>();
            ReadVerticalWidths(Get(metrics, "W2") as PdfArray, verticalMetrics);
            var defaultVertical = Get(metrics, "DW2") as PdfArray;
            double originY = defaultVertical is { Count: >= 2 } ? Number(Resolve(defaultVertical[0])) : 880;
            double advanceY = defaultVertical is { Count: >= 2 } ? Number(Resolve(defaultVertical[1])) : -1000;
            double defaultWidth = composite ? Number(Get(metrics, "DW"), 1000) : Number(Get(descriptor, "MissingWidth"), 0);
            double ascent = Number(Get(descriptor, "Ascent"), embedded is null ? 800 : embedded.Ascender * 1000d / embedded.UnitsPerEm);
            double descent = Number(Get(descriptor, "Descent"), embedded is null ? -200 : embedded.Descender * 1000d / embedded.UnitsPerEm);
            if (ascent <= descent)
            {
                if (Get(descriptor, "FontBBox") is PdfArray { Count: 4 } box)
                {
                    ascent = Number(Resolve(box[3]));
                    descent = Number(Resolve(box[1]));
                }
                if (ascent <= descent) { ascent = 800; descent = -200; }
            }
            return new PdfExtractionFont(unicode, widths,
                defaultWidth)
            {
                FontName = fontName,
                Ascent = ascent,
                Descent = descent,
                IsVertical = vertical,
                CidSelector = cidSelector,
                UnicodeFallback = Fallback,
                CharacterDecoder = predefined is null ? null : input => predefined.Decode(input, unicode, Fallback),
                VerticalMetricsReader = code =>
                {
                    uint cid = cidSelector?.Invoke(code) ?? code;
                    return verticalMetrics.GetValueOrDefault(cid,
                        new PdfVerticalGlyphMetrics(advanceY, widths.GetValueOrDefault(cid, defaultWidth) / 2, originY));
                },
                BoundsReader = code =>
                {
                    ushort glyph = Glyph(code);
                    var box = (glyph == 0 ? null : embeddedOutlines?.Bounds(glyph))
                        ?? cff?.GetBounds(CffGlyph(code))
                        ?? (!composite && code < glyphNames.Length ? type1?.GetBounds(glyphNames[code]) : null)
                        ?? (!composite && code < glyphNames.Length ? PdfStandardGlyphBounds.Get(metricsName, glyphNames[code]) : null);
                    return box is { } b && b.Right > b.Left && b.Top > b.Bottom ? box : null;
                },
                OutlineReader = code => embeddedOutlines?.Outline(Glyph(code))
                    ?? cff?.GetOutline(CffGlyph(code))
                    ?? (!composite && code < glyphNames.Length
                        ? type1?.GetOutline(glyphNames[code]) : null)
                    ?? (SubstituteGlyph(code) is ushort substituteGlyph && substituteGlyph != 0
                        ? substituteOutlines?.Outline(substituteGlyph) : null)
            };
        }

        private static Dictionary<ushort, string> ReverseUnicode(TrueTypeFont? font)
        {
            var result = new Dictionary<ushort, string>();
            if (font is null) return result;
            for (int scalar = 0; scalar <= 0xFFFF; scalar++)
            {
                if (scalar is >= 0xD800 and <= 0xDFFF) continue;
                ushort glyph = font.GetGlyphId(scalar);
                if (glyph != 0) result.TryAdd(glyph, char.ConvertFromUtf32(scalar));
            }
            return result;
        }

        private static TrueTypeFont? ReadEmbedded(byte[]? data)
        {
            if (data is null) return null;
            try { return TrueTypeFont.LoadForExtraction(data); }
            catch (FormatException) { return null; } // CFF-only programs do not contain sfnt tables.
        }

        private void ReadCidWidths(PdfArray? array, Dictionary<uint, double> widths)
        {
            if (array is null) return;
            long expanded = 0;
            for (int i = 0; i < array.Count;)
            {
                uint first = Cid(Resolve(array[i++]));
                if (i >= array.Count) throw new FormatException("Truncated CID width entry.");
                PdfObject second = Resolve(array[i++]);
                if (second is PdfArray values)
                {
                    if (first + (long)values.Count > 65536) throw new FormatException("CID width range exceeds 65535.");
                    if ((expanded += values.Count) > 1_000_000) throw new FormatException("CID width expansion limit exceeded.");
                    for (int j = 0; j < values.Count; j++) widths[first + (uint)j] = Number(Resolve(values[j]));
                }
                else
                {
                    uint last = Cid(second);
                    if (last < first || i >= array.Count) throw new FormatException("Invalid CID width range.");
                    if ((expanded += last - first + 1L) > 1_000_000) throw new FormatException("CID width expansion limit exceeded.");
                    double width = Number(Resolve(array[i++]));
                    for (uint cid = first; cid <= last; cid++) widths[cid] = width;
                }
            }
        }

        private void ReadVerticalWidths(PdfArray? array, Dictionary<uint, PdfVerticalGlyphMetrics> widths)
        {
            if (array is null) return;
            long expanded = 0;
            for (int i = 0; i < array.Count;)
            {
                uint first = Cid(Resolve(array[i++]));
                if (i >= array.Count) throw new FormatException("Truncated vertical CID metrics.");
                PdfObject next = Resolve(array[i++]);
                if (next is PdfArray values)
                {
                    if (values.Count % 3 != 0 || first + (long)values.Count / 3 > 65536)
                        throw new FormatException("Invalid vertical CID metric range.");
                    if ((expanded += values.Count / 3) > 1_000_000) throw new FormatException("Vertical CID metric expansion limit exceeded.");
                    for (int j = 0; j < values.Count; j += 3)
                        widths[first + (uint)(j / 3)] = new PdfVerticalGlyphMetrics(Number(Resolve(values[j])),
                            Number(Resolve(values[j + 1])), Number(Resolve(values[j + 2])));
                }
                else
                {
                    uint last = Cid(next);
                    if (last < first || i + 3 > array.Count) throw new FormatException("Invalid vertical CID metric range.");
                    if ((expanded += last - first + 1L) > 1_000_000) throw new FormatException("Vertical CID metric expansion limit exceeded.");
                    var value = new PdfVerticalGlyphMetrics(Number(Resolve(array[i++])), Number(Resolve(array[i++])), Number(Resolve(array[i++])));
                    for (uint cid = first; cid <= last; cid++) widths[cid] = value;
                }
            }
        }

        private PdfObject Resolve(PdfObject value)
        {
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 32) throw new FormatException("Font resource reference cycle.");
                value = document.Resolve(reference);
            }
            return value;
        }

        private PdfObject? Get(PdfDictionary? dictionary, string key) => dictionary is not null
            && dictionary.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)), out var value) ? Resolve(value) : null;
        private byte[] Decode(PdfStream stream) => PdfStreamDecoder.Decode(stream, document.Resolve, 32 * 1024 * 1024);
    }

    private static string? Name(PdfObject? value) => (value as PdfName)?.ValueAsLatin1();
    private static double Number(PdfObject? value, double fallback = double.NaN) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        null when double.IsFinite(fallback) => fallback,
        _ => throw new FormatException("Expected a numeric font metric.")
    };
    private static uint Cid(PdfObject value) => value is PdfInteger number && number.Value is >= 0 and <= 65535
        ? (uint)number.Value : throw new FormatException("CID is outside the valid range.");

    private static (Dictionary<uint, uint> Map, List<(uint Low, uint High, int Length)> Spaces, bool Vertical) ReadCidMap(byte[] data)
    {
        var map = new Dictionary<uint, uint>();
        var spaces = new List<(uint Low, uint High, int Length)>();
        long expanded = 0;
        bool vertical = false;
        foreach (var instruction in PdfContentStreamReader.Read(PdfCMapMetadata.WithoutDictionaries(data)))
        {
            var values = instruction.Operands;
            if (instruction.Operator == "usecmap") throw new NotSupportedException("Inherited font encoding CMaps are not supported.");
            if (instruction.Operator == "def" && values.Count == 2 && Name(values[0]) == "WMode") vertical = Number(values[1]) == 1;
            if (instruction.Operator == "endcodespacerange")
            {
                if (values.Count % 2 != 0 || values.Count > 512) throw new FormatException("Invalid CID code spaces.");
                for (int i = 0; i < values.Count; i += 2)
                {
                    var (Code, Length) = ReadCode(values[i]);
                    var high = ReadCode(values[i + 1]);
                    if (Length != high.Length || high.Code < Code) throw new FormatException("Invalid CID code space range.");
                    spaces.Add((Code, high.Code, Length));
                }
            }
            if (instruction.Operator is not "endcidchar" and not "endcidrange") continue;
            int stride = instruction.Operator == "endcidchar" ? 2 : 3;
            if (values.Count % stride != 0) throw new FormatException("Invalid CID mapping block.");
            for (int i = 0; i < values.Count; i += stride)
            {
                (uint first, int bytes) = ReadCode(values[i]);
                var high = stride == 2 ? (first, bytes) : ReadCode(values[i + 1]);
                if (high.Item2 != bytes) throw new FormatException("CID range character widths differ.");
                uint last = high.Item1;
                uint cid = Cid(values[i + stride - 1]);
                if (last < first || last - first + (ulong)cid > 65535) throw new FormatException("Invalid CID mapping range.");
                if ((expanded += last - first + 1L) > 1_000_000) throw new FormatException("CID mapping expansion limit exceeded.");
                for (uint offset = 0; offset <= last - first; offset++)
                {
                    if (map.Count >= 65536) throw new FormatException("CID mapping limit exceeded.");
                    map[first + offset] = cid + offset;
                }
            }
        }
        return (map, spaces, vertical);
    }

    private static (uint Code, int Length) ReadCode(PdfObject value)
    {
        if (value is not PdfString text || text.Bytes.Length is < 1 or > 4) throw new FormatException("Invalid font character code.");
        uint code = 0;
        foreach (byte b in text.Bytes.Span) code = (code << 8) | b;
        return (code, text.Bytes.Length);
    }

    private sealed class OutlineReader
    {
        private readonly TrueTypeFont _font;
        private readonly int _loca;
        private readonly int _locaLength;
        private readonly int _glyf;
        private readonly int _glyfLength;
        private readonly bool _longLocations;
        private readonly Dictionary<ushort, PdfGlyphOutline?> _outlineCache = [];

        internal OutlineReader(TrueTypeFont font)
        {
            _font = font;
            var data = font.FontData.Span;
            int head = 0;
            for (int i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(data[4..]); i++)
            {
                int record = 12 + 16 * i;
                uint tag = BinaryPrimitives.ReadUInt32BigEndian(data[record..]);
                int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[(record + 8)..]));
                int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[(record + 12)..]));
                if (tag == 0x6c6f6361) { _loca = offset; _locaLength = size; }
                if (tag == 0x676c7966) { _glyf = offset; _glyfLength = size; }
                if (tag == 0x68656164 && size >= 54) head = offset;
            }
            _longLocations = head != 0 && BinaryPrimitives.ReadInt16BigEndian(data[(head + 50)..]) == 1;
        }

        internal PdfGlyphBounds? Bounds(ushort glyph)
        {
            int stride = _longLocations ? 4 : 2;
            if (_loca == 0 || _glyf == 0 || glyph >= _font.GlyphCount || (glyph + 2) * stride > _locaLength) return null;
            var data = _font.FontData.Span;
            int address = _loca + glyph * stride;
            long start = _longLocations ? BinaryPrimitives.ReadUInt32BigEndian(data[address..])
                : BinaryPrimitives.ReadUInt16BigEndian(data[address..]) * 2L;
            long end = _longLocations ? BinaryPrimitives.ReadUInt32BigEndian(data[(address + stride)..])
                : BinaryPrimitives.ReadUInt16BigEndian(data[(address + stride)..]) * 2L;
            if (start == end) return new PdfGlyphBounds(0, 0, 0, 0);
            if (start < 0 || end < start + 10 || end > _glyfLength) return null;
            var box = data.Slice(_glyf + (int)start + 2, 8);
            double scale = 1000d / _font.UnitsPerEm;
            return new PdfGlyphBounds(BinaryPrimitives.ReadInt16BigEndian(box) * scale,
                BinaryPrimitives.ReadInt16BigEndian(box[2..]) * scale,
                BinaryPrimitives.ReadInt16BigEndian(box[4..]) * scale,
                BinaryPrimitives.ReadInt16BigEndian(box[6..]) * scale);
        }

        internal PdfGlyphOutline? Outline(ushort glyph)
        {
            lock (_outlineCache)
            {
                if (_outlineCache.TryGetValue(glyph, out PdfGlyphOutline? cached))
                    return cached;
                PdfGlyphOutline? outline = Outline(glyph, [], 0);
                _outlineCache.Add(glyph, outline);
                return outline;
            }
        }

        private PdfGlyphOutline? Outline(ushort glyph, HashSet<ushort> active, int depth)
        {
            if (depth > 32 || !active.Add(glyph))
                throw new FormatException("A compound TrueType glyph is cyclic or too deeply nested.");
            try
            {
                if (!TryGlyphRange(glyph, out ReadOnlySpan<byte> data)) return null;
                if (data.Length == 0) return new PdfGlyphOutline([]);
                int contourCount = BinaryPrimitives.ReadInt16BigEndian(data);
                if (contourCount < 0) return CompoundOutline(data, active, depth);
                if (contourCount == 0) return new PdfGlyphOutline([]);
                if (contourCount > 4_096 || data.Length < 10 + contourCount * 2 + 2)
                    throw new FormatException("A TrueType simple glyph is invalid.");
                int position = 10;
                var endPoints = new ushort[contourCount];
                for (int contour = 0; contour < contourCount; contour++)
                {
                    endPoints[contour] = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                    if (contour > 0 && endPoints[contour] <= endPoints[contour - 1])
                        throw new FormatException("TrueType contour endpoints are invalid.");
                    position += 2;
                }
                int pointCount = endPoints[^1] + 1;
                if (pointCount > 1_000_000 || position + 2 > data.Length)
                    throw new FormatException("A TrueType glyph point count is invalid.");
                int instructionLength = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                position += 2;
                if (instructionLength > data.Length - position)
                    throw new FormatException("TrueType glyph instructions are truncated.");
                position += instructionLength;
                var flags = new byte[pointCount];
                for (int point = 0; point < pointCount;)
                {
                    if (position >= data.Length)
                        throw new FormatException("TrueType glyph flags are truncated.");
                    byte flag = data[position++];
                    int repeat = 1;
                    if ((flag & 0x08) != 0)
                    {
                        if (position >= data.Length)
                            throw new FormatException("A TrueType glyph flag repeat is truncated.");
                        repeat += data[position++];
                    }
                    if (repeat > pointCount - point)
                        throw new FormatException("A TrueType glyph flag repeat is invalid.");
                    Array.Fill(flags, flag, point, repeat);
                    point += repeat;
                }
                var x = new int[pointCount];
                var y = new int[pointCount];
                ReadCoordinates(data, ref position, x, flags, shortMask: 0x02, sameMask: 0x10);
                ReadCoordinates(data, ref position, y, flags, shortMask: 0x04, sameMask: 0x20);
                double scale = 1000d / _font.UnitsPerEm;
                var contours = new PdfGlyphContour[contourCount];
                int start = 0;
                for (int contour = 0; contour < contourCount; contour++)
                {
                    int end = endPoints[contour];
                    var points = new PdfGlyphPoint[end - start + 1];
                    for (int point = start; point <= end; point++)
                        points[point - start] = new PdfGlyphPoint(
                            x[point] * scale, y[point] * scale, (flags[point] & 1) != 0);
                    contours[contour] = new PdfGlyphContour(Array.AsReadOnly(points));
                    start = end + 1;
                }
                return new PdfGlyphOutline(Array.AsReadOnly(contours));
            }
            finally
            {
                active.Remove(glyph);
            }
        }

        private PdfGlyphOutline CompoundOutline(
            ReadOnlySpan<byte> data, HashSet<ushort> active, int depth)
        {
            const ushort ArgumentsAreWords = 0x0001;
            const ushort ArgumentsAreCoordinates = 0x0002;
            const ushort HasScale = 0x0008;
            const ushort MoreComponents = 0x0020;
            const ushort HasXYScale = 0x0040;
            const ushort HasMatrix = 0x0080;
            const ushort HasInstructions = 0x0100;
            const ushort ScaledOffset = 0x0800;
            int position = 10;
            int componentCount = 0;
            ushort flags;
            var contours = new List<PdfGlyphContour>();
            do
            {
                if (++componentCount > 4_096 || position + 4 > data.Length)
                    throw new FormatException("A compound TrueType glyph is invalid.");
                flags = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                ushort componentGlyph = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
                position += 4;
                int firstArgument, secondArgument;
                if ((flags & ArgumentsAreWords) != 0)
                {
                    if (position + 4 > data.Length)
                        throw new FormatException("Compound TrueType glyph arguments are truncated.");
                    if ((flags & ArgumentsAreCoordinates) != 0)
                    {
                        firstArgument = BinaryPrimitives.ReadInt16BigEndian(data[position..]);
                        secondArgument = BinaryPrimitives.ReadInt16BigEndian(data[(position + 2)..]);
                    }
                    else
                    {
                        firstArgument = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                        secondArgument = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
                    }
                    position += 4;
                }
                else
                {
                    if (position + 2 > data.Length)
                        throw new FormatException("Compound TrueType glyph arguments are truncated.");
                    if ((flags & ArgumentsAreCoordinates) != 0)
                    {
                        firstArgument = (sbyte)data[position];
                        secondArgument = (sbyte)data[position + 1];
                    }
                    else
                    {
                        firstArgument = data[position];
                        secondArgument = data[position + 1];
                    }
                    position += 2;
                }
                double a = 1, b = 0, c = 0, d = 1;
                if ((flags & HasScale) != 0)
                    a = d = ReadF2Dot14(data, ref position);
                else if ((flags & HasXYScale) != 0)
                {
                    a = ReadF2Dot14(data, ref position);
                    d = ReadF2Dot14(data, ref position);
                }
                else if ((flags & HasMatrix) != 0)
                {
                    a = ReadF2Dot14(data, ref position);
                    b = ReadF2Dot14(data, ref position);
                    c = ReadF2Dot14(data, ref position);
                    d = ReadF2Dot14(data, ref position);
                }
                PdfGlyphOutline component = Outline(componentGlyph, active, depth + 1)
                    ?? new PdfGlyphOutline([]);
                double offsetX, offsetY;
                double unitScale = 1000d / _font.UnitsPerEm;
                if ((flags & ArgumentsAreCoordinates) != 0)
                {
                    offsetX = firstArgument * unitScale;
                    offsetY = secondArgument * unitScale;
                    if ((flags & ScaledOffset) != 0)
                        (offsetX, offsetY) = (a * offsetX + c * offsetY,
                            b * offsetX + d * offsetY);
                }
                else
                {
                    PdfGlyphPoint[] parentPoints = [.. contours.SelectMany(item => item.Points)];
                    PdfGlyphPoint[] componentPoints = [.. component.Contours.SelectMany(item => item.Points)];
                    if (firstArgument >= parentPoints.Length || secondArgument >= componentPoints.Length)
                        throw new FormatException("Compound TrueType glyph point matching is invalid.");
                    PdfGlyphPoint parent = parentPoints[firstArgument];
                    PdfGlyphPoint child = componentPoints[secondArgument];
                    offsetX = parent.X - (a * child.X + c * child.Y);
                    offsetY = parent.Y - (b * child.X + d * child.Y);
                }
                foreach (PdfGlyphContour contour in component.Contours)
                    contours.Add(new PdfGlyphContour(Array.AsReadOnly([
                        .. contour.Points.Select(point => new PdfGlyphPoint(
                            a * point.X + c * point.Y + offsetX,
                            b * point.X + d * point.Y + offsetY, point.OnCurve))])));
            }
            while ((flags & MoreComponents) != 0);
            if ((flags & HasInstructions) != 0)
            {
                if (position + 2 > data.Length)
                    throw new FormatException("Compound TrueType glyph instructions are truncated.");
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
                position += 2;
                if (length > data.Length - position)
                    throw new FormatException("Compound TrueType glyph instructions are truncated.");
            }
            return new PdfGlyphOutline(contours.AsReadOnly());
        }

        private static double ReadF2Dot14(ReadOnlySpan<byte> data, ref int position)
        {
            if (position + 2 > data.Length)
                throw new FormatException("A compound TrueType glyph transform is truncated.");
            double value = BinaryPrimitives.ReadInt16BigEndian(data[position..]) / 16384d;
            position += 2;
            return value;
        }

        private static void ReadCoordinates(ReadOnlySpan<byte> data, ref int position,
            int[] coordinates, byte[] pointFlags, byte shortMask, byte sameMask)
        {
            int current = 0;
            for (int point = 0; point < coordinates.Length; point++)
            {
                byte flag = pointFlags[point];
                int delta;
                if ((flag & shortMask) != 0)
                {
                    if (position >= data.Length)
                        throw new FormatException("TrueType glyph coordinates are truncated.");
                    int magnitude = data[position++];
                    delta = (flag & sameMask) != 0 ? magnitude : -magnitude;
                }
                else if ((flag & sameMask) != 0) delta = 0;
                else
                {
                    if (position + 2 > data.Length)
                        throw new FormatException("TrueType glyph coordinates are truncated.");
                    delta = BinaryPrimitives.ReadInt16BigEndian(data[position..]);
                    position += 2;
                }
                current = checked(current + delta);
                coordinates[point] = current;
            }
        }

        private bool TryGlyphRange(ushort glyph, out ReadOnlySpan<byte> glyphData)
        {
            glyphData = default;
            int stride = _longLocations ? 4 : 2;
            if (_loca == 0 || _glyf == 0 || glyph >= _font.GlyphCount
                || (glyph + 2) * stride > _locaLength) return false;
            ReadOnlySpan<byte> data = _font.FontData.Span;
            int address = _loca + glyph * stride;
            long start = _longLocations ? BinaryPrimitives.ReadUInt32BigEndian(data[address..])
                : BinaryPrimitives.ReadUInt16BigEndian(data[address..]) * 2L;
            long end = _longLocations ? BinaryPrimitives.ReadUInt32BigEndian(data[(address + stride)..])
                : BinaryPrimitives.ReadUInt16BigEndian(data[(address + stride)..]) * 2L;
            if (start == end)
            {
                glyphData = [];
                return true;
            }
            if (start < 0 || end < start + 10 || end > _glyfLength) return false;
            glyphData = data.Slice(_glyf + (int)start, (int)(end - start));
            return true;
        }
    }
}
