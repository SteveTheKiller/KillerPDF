using System.Buffers.Binary;
using System.Globalization;
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
    public static PdfExtractionFont Read(PdfDocument document, PdfDictionary font,
        IPdfFontResolver? fontResolver = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(font);
        var reader = new Reader(document, fontResolver);
        return reader.Read(font);
    }

    private sealed class Reader(PdfDocument document, IPdfFontResolver? fontResolver)
    {
        private bool _ignoredUnicodeMap;
        private bool _recoveredType1Lengths;
        private bool _ignoredInvalidDifferences;
        private bool _clampedSimpleWidths;
        private bool _recoveredNullMetric;
        private static readonly string[] array = ["Invalid Type 1 font lengths were recovered from the embedded program."];
        private static readonly string[] array0 = ["An unreadable ToUnicode map was ignored; embedded font mappings were used."];
        private static readonly string[] invalidDifferencesDiagnostic = ["Invalid simple-font differences were ignored."];
        private static readonly string[] simpleWidthsDiagnostic = ["Simple-font widths beyond code 255 were ignored."];
        private static readonly string[] nullMetricDiagnostic = ["Null font metrics used their default values."];

        internal PdfExtractionFont Read(PdfDictionary font)
        {
            string? subtype = Name(Get(font, "Subtype"));
            bool composite = subtype == "Type0";
            PdfDictionary metrics = font;
            if (composite)
            {
                PdfObject? descendants = Get(font, "DescendantFonts");
                PdfObject? descendant = descendants is PdfArray { Count: 1 } array
                    ? Resolve(array[0]) : null;
                if (descendant is PdfDictionary dictionary) metrics = dictionary;
                else if (document.UsesCompatibilityRecovery
                    && (descendants is null or PdfNull or PdfArray { Count: 0 }
                        || descendants is PdfArray { Count: 1 } && descendant is PdfNull)
                    && Name(Get(font, "Encoding")) is "Identity-H" or "Identity-V"
                    && Get(font, "ToUnicode") is PdfStream)
                    metrics = new PdfDictionary([]);
                else
                    throw new FormatException("A Type0 font must have one descendant font.");
            }
            var descriptor = Get(metrics, "FontDescriptor") as PdfDictionary;
            string fontName = Name(Get(font, "BaseFont"))
                ?? Name(Get(metrics, "BaseFont"))
                ?? Name(Get(descriptor, "FontName"))
                ?? (document.UsesCompatibilityRecovery && subtype == "Type1" ? "Times-Roman" : "Unknown");
            string metricsName = fontName.Length > 7 && fontName[6] == '+' ? fontName[7..] : fontName;
            string standardMetricsName = PdfStandardFontSubstitutes.CanonicalMetricsName(metricsName);
            var systemInfo = Get(metrics, "CIDSystemInfo") as PdfDictionary;
            string registry = Get(systemInfo, "Registry") is PdfString registryText
                ? Encoding.Latin1.GetString(registryText.Bytes.Span) : "Adobe";
            string ordering = Get(systemInfo, "Ordering") is PdfString orderingText
                ? Encoding.Latin1.GetString(orderingText.Bytes.Span) : "Identity";
            byte[]? embeddedData = (Get(descriptor, "FontFile2") ?? Get(descriptor, "FontFile3")) is PdfStream embeddedStream
                ? DecodeEmbeddedFont(embeddedStream) : null;
            PdfStream? type1Stream = Get(descriptor, "FontFile") as PdfStream;
            byte[]? type1Data = type1Stream is null ? null : Decode(type1Stream);
            // Some writers put a bare CFF program in FontFile instead of FontFile3.
            if (document.UsesCompatibilityRecovery && embeddedData is null && type1Data is
                { Length: >= 4 } candidate && candidate[0] == 1 && candidate[1] == 0
                && candidate[2] >= 4 && candidate[3] is >= 1 and <= 4)
            {
                embeddedData = type1Data;
                type1Stream = null;
            }
            PdfCffGlyphReader? standardSymbols = !composite && subtype != "Type3"
                && embeddedData is null && type1Stream is null
                ? PdfStandardSymbolFonts.Create(standardMetricsName) : null;
            bool requestedVertical = Name(Get(font, "Encoding"))?.EndsWith(
                "-V", StringComparison.Ordinal) == true;
            byte[]? resolvedData = embeddedData is null && type1Stream is null
                && subtype != "Type3" && standardSymbols is null
                ? fontResolver?.Resolve(new PdfFontRequest(
                    metricsName, composite ? registry : "Adobe",
                    composite ? ordering : "Standard", requestedVertical))
                : null;
            byte[]? outlineData = embeddedData ?? resolvedData;
            TrueTypeFont? embedded = embeddedData is not null
                ? ReadEmbedded(embeddedData, fontName) : ReadResolved(resolvedData);
            PdfCffGlyphReader? cff = outlineData is null ? standardSymbols : PdfCffGlyphReader.TryRead(outlineData);
            PdfType1GlyphReader? type1 = type1Stream is not null
                ? ReadType1(type1Stream, type1Data!) : null;
            TrueTypeFont? substitute = subtype is not null and not "Type3"
                ? PdfStandardFontSubstitutes.Find(metricsName,
                    Get(descriptor, "Flags") is PdfInteger fallbackFlags ? fallbackFlags.Value : 0) : null;
            var embeddedOutlines = embedded is null ? null : new TrueTypeOutlineReader(embedded);
            var substituteOutlines = substitute is null ? null : new TrueTypeOutlineReader(substitute);
            var widths = new Dictionary<uint, double>();
            var simpleText = new Dictionary<uint, string>();
            string[] glyphNames = [];
            Func<uint, uint>? cidSelector = null;
            int codeLength = composite ? 2 : 1;
            PdfToUnicodeMap? encodingSpaces = null;
            PdfPredefinedCMaps? predefined = null;
            List<(uint Low, uint High, int Length)>? customCidSpaces = null;
            PdfPredefinedCMaps? inheritedEncoding = null;
            string? encodingName = Name(Get(font, "Encoding"));
            bool vertical = encodingName?.EndsWith("-V", StringComparison.Ordinal) == true;
            if (composite)
            {
                ReadCidWidths(Get(metrics, "W") as PdfArray, widths);
                if (Get(font, "Encoding") is PdfStream encodingStream)
                {
                    var (Map, Spaces, Vertical, Base) = ReadCidMap(encodingStream);
                    customCidSpaces = Spaces;
                    inheritedEncoding = Base;
                    cidSelector = c => Map.TryGetValue(c, out uint cid) ? cid : Base?.Cid(c) ?? 0;
                    encodingSpaces = Spaces.Count > 0
                        ? PdfToUnicodeMap.CreateCodeSpaces(Spaces)
                        : Base is null ? null : PdfToUnicodeMap.Create(new Dictionary<uint, string>(), 2);
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
                if (document.UsesCompatibilityRecovery && encodingName == "WinAnsiEcoding")
                    encodingName = "WinAnsiEncoding";
                // An unrecognized encoding name (such as /NULL) carries no mapping; recover it as
                // an absent encoding so the font's built-in or the standard encoding applies.
                if (document.UsesCompatibilityRecovery && encodingName is not null
                    && PdfFontTables.EncodingNames(encodingName) is null)
                    encodingName = null;
                // Without a named encoding the font program's own encoding applies: the Type 1
                // encoding vector, or a CFF program's Encoding table (TeX symbol subsets put
                // glyphs such as "element" at codes StandardEncoding names as digits).
                string[]? builtInEncoding = encodingName is null
                    ? type1?.EncodingNames?.ToArray()
                        ?? (embedded is null && embeddedData is not null ? cff?.EncodingNames?.ToArray() : null)
                    : null;
                encodingName ??= standardMetricsName is "Symbol" ? "SymbolEncoding"
                    : standardMetricsName is "ZapfDingbats" ? "ZapfDingbatsEncoding" : "StandardEncoding";
                glyphNames = builtInEncoding ?? PdfFontTables.EncodingNames(encodingName)
                    ?? (Get(font, "ToUnicode") is PdfStream ? Enumerable.Repeat(string.Empty, 256).ToArray()
                        : throw new NotSupportedException($"Font encoding /{encodingName} is not supported."));
                // Differences that rename a code to .notdef keep the base encoding's width.
                string[] baseGlyphNames = [.. glyphNames];
                if (encodingDictionary is not null && Get(encodingDictionary, "Differences") is PdfArray differences)
                {
                    int index = -1;
                    string[] changedGlyphNames = [.. glyphNames];
                    foreach (PdfObject raw in differences)
                    {
                        PdfObject value = Resolve(raw);
                        if (value is PdfInteger number && number.Value is >= 0 and < 256)
                            index = (int)number.Value;
                        else if (value is PdfName name && index is >= 0 and < 256)
                            changedGlyphNames[index++] = name.ValueAsLatin1();
                        else if (document.UsesCompatibilityRecovery)
                        {
                            _ignoredInvalidDifferences = true;
                            index = -1;
                            break;
                        }
                        else
                            throw new FormatException("Invalid simple-font encoding difference.");
                    }
                    if (!_ignoredInvalidDifferences) glyphNames = changedGlyphNames;
                }
                for (uint code = 0; code < 256; code++)
                {
                    string? text = PdfFontTables.GlyphText(glyphNames[code]);
                    if (text is null && Name(Get(font, "Subtype")) == "Type3") text = ((char)code).ToString();
                    if (text is not null) simpleText[code] = text;
                    double? standardWidth = PdfStandardGlyphBounds.Width(standardMetricsName,
                        glyphNames[code] == ".notdef" ? baseGlyphNames[code] : glyphNames[code]);
                    if (standardWidth is double width) widths[code] = width;
                }
                if (Get(font, "Widths") is PdfArray values)
                {
                    int first = checked((int)Metric(Get(font, "FirstChar"), 0));
                    if (first < 0 || first >= 256)
                    {
                        if (!document.UsesCompatibilityRecovery)
                            throw new FormatException("Invalid simple-font width range.");
                        _clampedSimpleWidths = true;
                    }
                    else
                    {
                        int count = Math.Min(values.Count, 256 - first);
                        if (count != values.Count)
                        {
                            if (!document.UsesCompatibilityRecovery)
                                throw new FormatException("Invalid simple-font width range.");
                            _clampedSimpleWidths = true;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            PdfObject width = Resolve(values[i]);
                            if (document.UsesCompatibilityRecovery && width is PdfNull)
                            {
                                _recoveredNullMetric = true;
                                continue;
                            }
                            widths[(uint)(first + i)] = Number(width);
                        }
                    }
                }
            }

            PdfToUnicodeMap unicode = Get(font, "ToUnicode") is PdfStream unicodeStream
                ? ReadToUnicode(unicodeStream, !composite,
                    document.UsesCompatibilityRecovery && composite && embeddedData is not null
                    && embedded is { HasUnicodeCharacterMap: true }
                    && encodingName is "Identity-H" or "Identity-V",
                    composite && encodingName is "Identity-H" or "Identity-V")
                : encodingSpaces ?? PdfToUnicodeMap.Create(simpleText, codeLength);
            string? UnicodeText(uint code) => unicode.Lookup(code, codeLength)
                ?? Enumerable.Range(1, 4).Where(length => length != codeLength)
                    .Select(length => unicode.Lookup(code, length))
                    .FirstOrDefault(text => text is not null);
            string? CharacterText(uint code)
            {
                string? text = UnicodeText(code);
                if (text == "\uFFFD" && document.UsesCompatibilityRecovery) text = null;
                if (text is not null) return text;
                if (!composite) return simpleText.GetValueOrDefault(code);
                uint cid = cidSelector?.Invoke(code) ?? code;
                text = PdfPredefinedCMaps.Unicode(
                    registry + "-" + ordering, vertical, cid);
                if (text is not null) return text;
                return document.UsesCompatibilityRecovery && ordering == "Identity"
                    && cid <= 0x10FFFF && cid is not (>= 0xD800 and <= 0xDFFF)
                    ? char.ConvertFromUtf32((int)cid) : null;
            }
            byte[]? cidToGid = Get(metrics, "CIDToGIDMap") is PdfStream gidStream ? Decode(gidStream) : null;
            // An Identity CID without a Unicode map identifies a glyph in the requested font.
            bool useHostCidGlyphs = resolvedData is not null && composite
                && Name(Get(metrics, "Subtype")) == "CIDFontType2"
                && registry == "Adobe" && ordering == "Identity"
                && Get(font, "ToUnicode") is not PdfStream;
            bool useEncodedSymbol = embeddedData is not null
                && embedded is { HasUnicodeCharacterMap: false }
                && Get(font, "Encoding") is null
                && Get(descriptor, "Flags") is PdfInteger flags && (flags.Value & 4) != 0;
            // A symbolic font without an Encoding entry has no meaningful character names.
            bool encodingNamesGlyphs = Get(font, "Encoding") is not null
                || Get(descriptor, "Flags") is not PdfInteger symbolFlags || (symbolFlags.Value & 4) == 0;
            ushort Glyph(uint code)
            {
                if (composite)
                {
                    if (resolvedData is not null && !useHostCidGlyphs)
                    {
                        string? mappedText = CharacterText(code);
                        return embedded is not null && !string.IsNullOrEmpty(mappedText)
                            ? embedded.GetGlyphId(char.ConvertToUtf32(mappedText, 0)) : (ushort)0;
                    }
                    uint cid = cidSelector?.Invoke(code) ?? code;
                    if (cidToGid is not null)
                        return cid < cidToGid.Length / 2 ? BinaryPrimitives.ReadUInt16BigEndian(cidToGid.AsSpan((int)cid * 2, 2)) : (ushort)0;
                    return cid <= ushort.MaxValue ? (ushort)cid : (ushort)0;
                }
                string? text = CharacterText(code);
                if (embedded is null) return 0;
                if (resolvedData is not null && string.IsNullOrEmpty(text)) return 0;
                if (useEncodedSymbol && code < 256)
                {
                    ushort encodedGlyph = embedded.GetGlyphId((int)code);
                    if (encodedGlyph == 0) encodedGlyph = embedded.GetGlyphId((int)code + 0xF000);
                    if (encodedGlyph != 0) return encodedGlyph;
                }
                // The encoding selects the glyph. ToUnicode describes extracted text and can
                // differ from what is drawn (a caps style over lowercase text), so its text is
                // only consulted when the encoding's character is not in the font.
                string? encodedText = encodingNamesGlyphs ? simpleText.GetValueOrDefault(code) : null;
                ushort glyph = string.IsNullOrEmpty(encodedText)
                    ? (ushort)0 : embedded.GetGlyphId(char.ConvertToUtf32(encodedText, 0));
                if (glyph == 0 && !string.IsNullOrEmpty(text) && text != encodedText)
                    glyph = embedded.GetGlyphId(char.ConvertToUtf32(text, 0));
                if (glyph == 0 && code < 256) glyph = embedded.GetGlyphId((int)code);
                if (glyph == 0 && code < 256) glyph = embedded.GetGlyphId((int)code + 0xF000);
                if (glyph == 0 && resolvedData is null && code < glyphNames.Length
                    && glyphNames[code] is { Length: > 0 } glyphName)
                    glyph = NamedGlyph(glyphName);
                return glyph;
            }
            // Embedded TrueType glyphs can be named by the encoding instead of reachable through
            // the cmap: first through the font's own 'post' names, then through generated names
            // ("g24", "g0024") that writers emit for subsets whose glyphs have no standard name.
            bool? generatedNamesAreHex = null;
            ushort NamedGlyph(string glyphName)
            {
                ushort named = embedded!.GetGlyphIdByName(glyphName);
                if (named != 0) return named;
                if (glyphName.Length < 2 || glyphName[0] != 'g') return 0;
                generatedNamesAreHex ??= GeneratedGlyphNamesAreHex(glyphNames);
                ReadOnlySpan<char> digits = glyphName.AsSpan(1);
                int value = 0;
                bool parsed = generatedNamesAreHex.Value
                    ? digits.Length == 4 && int.TryParse(digits, NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out value)
                    : int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
                return parsed && value > 0 && value < embedded.GlyphCount ? (ushort)value : (ushort)0;
            }
            ushort SubstituteGlyph(uint code)
            {
                string? text = CharacterText(code);
                if (substitute is null || string.IsNullOrEmpty(text)) return 0;
                int scalar = char.ConvertToUtf32(text, 0);
                ushort glyph = substitute.GetGlyphId(scalar);
                if (glyph == 0 && !composite && code < 256) glyph = substitute.GetGlyphId((int)code);
                return glyph;
            }
            if (!composite && embedded is not null)
                for (uint code = 0; code < 256; code++)
                    if (!widths.ContainsKey(code) && Glyph(code) is ushort glyph && glyph < embedded.GlyphCount)
                        widths[code] = embedded.GetPdfAdvanceWidth(glyph);
            var reverse = new Lazy<Dictionary<ushort, string>>(() => ReverseUnicode(embedded));
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
                if (!composite)
                    return code < glyphNames.Length ? cff.FindGlyph(glyphNames[code]) : -1;
                int cffGlyph = cff.FindCid(cidSelector?.Invoke(code) ?? code);
                if (cffGlyph >= 0) return cffGlyph;
                string? text = UnicodeText(code);
                return string.IsNullOrEmpty(text) ? -1
                    : cff.FindUnicode(char.ConvertToUtf32(text, 0));
            }
            var verticalMetrics = new Dictionary<uint, PdfVerticalGlyphMetrics>();
            ReadVerticalWidths(Get(metrics, "W2") as PdfArray, verticalMetrics);
            var defaultVertical = Get(metrics, "DW2") as PdfArray;
            double originY = defaultVertical is { Count: >= 2 } ? Metric(Resolve(defaultVertical[0]), 880) : 880;
            double advanceY = defaultVertical is { Count: >= 2 } ? Metric(Resolve(defaultVertical[1]), -1000) : -1000;
            double defaultWidth = composite ? Metric(Get(metrics, "DW"), 1000) : Metric(Get(descriptor, "MissingWidth"), 0);
            TrueTypeFont? metricFont = embedded ?? (cff is null && type1 is null ? substitute : null);
            double ascent = Metric(Get(descriptor, "Ascent"), metricFont is null ? 800 : metricFont.Ascender * 1000d / metricFont.UnitsPerEm);
            double descent = Metric(Get(descriptor, "Descent"), metricFont is null ? -200 : metricFont.Descender * 1000d / metricFont.UnitsPerEm);
            if (ascent <= descent)
            {
                if (Get(descriptor, "FontBBox") is PdfArray { Count: 4 } box)
                {
                    ascent = Metric(Resolve(box[3]), 800);
                    descent = Metric(Resolve(box[1]), -200);
                }
                if (ascent <= descent) { ascent = 800; descent = -200; }
            }
            PdfGlyphOutline? CompatibilityOutline(uint code)
            {
                if (composite || cff is null && type1 is null) return null;
                string? text = CharacterText(code)?.Normalize(NormalizationForm.FormKD);
                if (string.IsNullOrEmpty(text)) return null;
                Rune[] scalars = [.. text.EnumerateRunes()];
                if (scalars.Length < 2) return null;
                double targetWidth = widths.GetValueOrDefault(code, defaultWidth);
                if (targetWidth <= 0) return null;
                var components = new List<(PdfGlyphOutline Outline, double Advance)>();
                foreach (Rune scalar in scalars)
                {
                    int sourceCode = Enumerable.Range(0, glyphNames.Length)
                        .FirstOrDefault(index =>
                            simpleText.GetValueOrDefault((uint)index) == scalar.ToString(), -1);
                    PdfGlyphOutline? outline = sourceCode >= 0
                        ? cff?.GetOutline(cff.FindGlyph(glyphNames[sourceCode])) : null;
                    outline ??= cff?.GetOutline(cff.FindUnicode(scalar.Value))
                        ?? (sourceCode >= 0 ? type1?.GetOutline(glyphNames[sourceCode]) : null);
                    if (outline is null && sourceCode >= 0
                        && SubstituteGlyph((uint)sourceCode) is ushort substituteGlyph
                        && substituteGlyph != 0)
                        outline = substituteOutlines?.Outline(substituteGlyph);
                    if (outline is null) return null;
                    double advance = sourceCode >= 0
                        ? widths.GetValueOrDefault((uint)sourceCode, defaultWidth)
                        : defaultWidth;
                    if (advance <= 0) advance = targetWidth / scalars.Length;
                    components.Add((outline, advance));
                }
                double naturalWidth = components.Sum(component => component.Advance);
                if (naturalWidth <= 0) return null;
                double position = 0;
                var contours = new List<PdfGlyphContour>();
                foreach ((PdfGlyphOutline outline, double advance) in components)
                {
                    double offset = position * targetWidth / naturalWidth;
                    contours.AddRange(outline.Contours.Select(contour => new PdfGlyphContour(
                        Array.AsReadOnly([.. contour.Points.Select(point => point with
                        {
                            X = point.X + offset
                        })]))));
                    position += advance;
                }
                return new PdfGlyphOutline(contours.AsReadOnly());
            }
            double HostCidOffset(uint code, ushort glyph)
            {
                uint cid = cidSelector?.Invoke(code) ?? code;
                return useHostCidGlyphs && embedded is not null && !widths.ContainsKey(cid)
                    ? (defaultWidth - embedded.GetPdfAdvanceWidth(glyph)) / 2 : 0;
            }
            var centeredHostOutlines = new Dictionary<uint, PdfGlyphOutline>();
            PdfGlyphOutline? TrueTypeOutline(uint code)
            {
                ushort glyph = Glyph(code);
                // A missing host glyph must allow the bundled font fallback, not paint .notdef.
                PdfGlyphOutline? outline = glyph == 0 && resolvedData is not null
                    ? null : embeddedOutlines?.Outline(glyph);
                if (outline is null) return null;
                double offset = HostCidOffset(code, glyph);
                if (offset == 0) return outline;
                lock (centeredHostOutlines)
                {
                    if (centeredHostOutlines.TryGetValue(code, out PdfGlyphOutline? centered))
                        return centered;
                    centered = new PdfGlyphOutline(Array.AsReadOnly([.. outline.Contours.Select(
                        contour => new PdfGlyphContour(Array.AsReadOnly([.. contour.Points.Select(
                            point => point with { X = point.X + offset })])))]));
                    centeredHostOutlines.Add(code, centered);
                    return centered;
                }
            }
            var fittedSubstitutes = new Dictionary<uint, PdfGlyphOutline>();
            PdfGlyphOutline? SubstituteOutline(uint code)
            {
                ushort glyph = SubstituteGlyph(code);
                PdfGlyphOutline? outline = glyph == 0 ? null : substituteOutlines?.Outline(glyph);
                if (outline is null || composite || code >= glyphNames.Length
                    || PdfStandardGlyphBounds.Width(standardMetricsName, glyphNames[code]) is not null
                    || !widths.TryGetValue(code, out double targetWidth) || targetWidth <= 0)
                    return outline;
                double naturalWidth = substitute!.GetPdfAdvanceWidth(glyph);
                if (naturalWidth <= 0 || targetWidth == naturalWidth) return outline;
                lock (fittedSubstitutes)
                {
                    if (fittedSubstitutes.TryGetValue(code, out PdfGlyphOutline? fitted)) return fitted;
                    double scale = targetWidth / naturalWidth;
                    fitted = new PdfGlyphOutline(Array.AsReadOnly([.. outline.Contours.Select(contour =>
                        new PdfGlyphContour(Array.AsReadOnly([.. contour.Points.Select(point =>
                            point with { X = point.X * scale })])))]));
                    fittedSubstitutes.Add(code, fitted);
                    return fitted;
                }
            }
            PdfGlyphOutline? BlankOutline(uint code) => !composite
                && code < glyphNames.Length
                && (glyphNames[code] == "space"
                    // Undefined control slots in the original standard Type 1 fonts are
                    // spacing, nonmarking .notdef glyphs, not unavailable visible outlines.
                    || code < 32 && glyphNames[code].Length == 0 && subtype == "Type1"
                        && type1Stream is null && embeddedData is null
                        && CharacterText(code) is null
                        && PdfStandardGlyphBounds.Width(standardMetricsName, "space") is not null)
                    ? new PdfGlyphOutline([]) : null;
            // Type 3 widths are in glyph space, which its own /FontMatrix defines.
            if (subtype == "Type3" && Get(font, "FontMatrix") is PdfArray { Count: 6 } fontMatrix
                && Resolve(fontMatrix[0]) is PdfInteger or PdfReal)
            {
                double scale = Number(Resolve(fontMatrix[0]), 0.001) * 1000;
                foreach (uint code in widths.Keys.ToArray()) widths[code] *= scale;
                defaultWidth *= scale;
            }
            return new PdfExtractionFont(unicode, widths,
                defaultWidth)
            {
                FontName = fontName,
                Diagnostics = RecoveryDiagnostics(),
                Ascent = ascent,
                Descent = descent,
                IsVertical = vertical,
                IsType3 = subtype == "Type3",
                CidSelector = cidSelector,
                UnicodeFallback = Fallback,
                CharacterDecoder = predefined is not null
                    ? input => predefined.Decode(input, unicode, Fallback)
                    : inheritedEncoding is null ? null
                    : input => inheritedEncoding.Decode(input, unicode, Fallback, customCidSpaces ?? []),
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
                        ?? (!composite && code < glyphNames.Length ? PdfStandardGlyphBounds.Get(standardMetricsName, glyphNames[code]) : null);
                    double offset = glyph == 0 ? 0 : HostCidOffset(code, glyph);
                    if (box is { } centered && offset != 0)
                        box = centered with
                        {
                            Left = centered.Left + offset,
                            Right = centered.Right + offset
                        };
                    return box is { } b && b.Right > b.Left && b.Top > b.Bottom ? box : null;
                },
                OutlineReader = code => TrueTypeOutline(code)
                    ?? cff?.GetOutline(CffGlyph(code))
                    ?? CompatibilityOutline(code)
                    ?? (!composite && code < glyphNames.Length
                        ? type1?.GetOutline(glyphNames[code]) : null)
                    ?? SubstituteOutline(code)
                    ?? BlankOutline(code)
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

        private PdfType1GlyphReader? ReadType1(PdfStream stream, byte[] data)
        {
            double length1 = Metric(Get(stream.Dictionary, "Length1"), 0);
            double length2 = Metric(Get(stream.Dictionary, "Length2"), 0);
            if (document.UsesCompatibilityRecovery
                && (length1 <= 0 || length1 >= data.Length || length1 != Math.Truncate(length1)
                    || length2 < 0 || length2 > data.Length - length1 || length2 != Math.Truncate(length2)))
            {
                PdfType1GlyphReader? recovered = PdfType1GlyphReader.TryReadWithRecoveredLengths(data);
                _recoveredType1Lengths = recovered is not null;
                return recovered;
            }
            return PdfType1GlyphReader.TryRead(data, checked((int)length1), checked((int)length2));
        }

        private static TrueTypeFont? ReadEmbedded(byte[]? data, string fontName)
        {
            if (data is null) return null;
            try { return TrueTypeFont.LoadForExtraction(data, fontName); }
            catch (FormatException) { return null; } // CFF-only programs do not contain sfnt tables.
        }

        // Writers that name subset glyphs by identifier use either zero-padded four-digit hex
        // ("g0024", "g003a") or plain decimal ("g24", "g1009"). Decide once per font from all of
        // its names so a four-digit decimal name is not misread as hex.
        private static bool GeneratedGlyphNamesAreHex(string[] names)
        {
            bool hex = false;
            foreach (string name in names)
            {
                if (name.Length < 2 || name[0] != 'g') continue;
                ReadOnlySpan<char> digits = name.AsSpan(1);
                bool allHex = true, hasLetter = false;
                foreach (char c in digits)
                {
                    if (char.IsAsciiDigit(c)) continue;
                    if (char.IsAsciiHexDigit(c)) { hasLetter = true; continue; }
                    allHex = false;
                    break;
                }
                if (!allHex) continue;
                if (digits.Length != 4) return false;
                if (hasLetter || digits[0] == '0') hex = true;
            }
            return hex;
        }

        // The host resolver owns these bytes and hands the same array to every font resource that
        // maps to one installed face. TrueTypeFont never writes to its data, so loading the shared
        // array directly keeps one copy per face instead of one per page that references it.
        private static TrueTypeFont? ReadResolved(byte[]? data)
        {
            if (data is null) return null;
            try { return TrueTypeFont.LoadOwnedForExtraction(data); }
            catch (FormatException) { return null; }
        }

        private PdfToUnicodeMap ReadToUnicode(PdfStream stream, bool simpleFont, bool allowUnreadableMap,
            bool identityFont)
        {
            var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
            return ReadToUnicode(stream, simpleFont, seen, 0, allowUnreadableMap, identityFont);
        }

        private PdfToUnicodeMap ReadToUnicode(PdfStream stream, bool simpleFont,
            HashSet<PdfStream> seen, int depth, bool allowUnreadableMap = false, bool identityFont = false)
        {
            if (depth >= 32 || !seen.Add(stream))
                throw new FormatException("ToUnicode map inheritance cycle.");
            PdfToUnicodeMap? inherited = Get(stream.Dictionary, "UseCMap") switch
            {
                null => null,
                PdfStream baseStream => ReadToUnicode(baseStream, simpleFont, seen, depth + 1,
                    identityFont: identityFont),
                PdfName name => PdfPredefinedCMaps.FindUnicodeMap(name.ValueAsLatin1())
                    ?? throw new NotSupportedException(
                        $"Inherited ToUnicode map /{name.ValueAsLatin1()} is not supported."),
                _ => throw new FormatException("Invalid inherited ToUnicode map reference.")
            };
            byte[] decoded = Decode(stream);
            if (document.UsesCompatibilityRecovery && Get(stream.Dictionary, "Filter") is null
                && decoded.Length >= 2 && (decoded[0] & 15) == 8 && decoded[0] >> 4 <= 7
                && ((decoded[0] << 8) + decoded[1]) % 31 == 0)
            {
                var compressed = new PdfStream(new PdfDictionary([
                    new KeyValuePair<PdfName, PdfObject>(new PdfName("Filter"u8), new PdfName("FlateDecode"u8))
                ]), decoded);
                decoded = PdfStreamDecoder.Decode(compressed, 32 * 1024 * 1024);
            }
            PdfToUnicodeMap map;
            try
            {
                map = PdfToUnicodeMap.ParseFont(decoded, simpleFont,
                    document.UsesCompatibilityRecovery, inherited, identityFont);
            }
            catch (KillerPdf.Engine.Syntax.PdfSyntaxException) when (allowUnreadableMap
                && inherited is null && decoded.AsSpan().IndexOf("begin"u8) < 0
                && decoded.AsSpan().IndexOf("end"u8) < 0
                && decoded.AsSpan().IndexOf("usecmap"u8) < 0)
            {
                _ignoredUnicodeMap = true;
                map = PdfToUnicodeMap.Create(new Dictionary<uint, string>(), 2);
            }
            seen.Remove(stream);
            return map;
        }

        private (Dictionary<uint, uint> Map,
            List<(uint Low, uint High, int Length)> Spaces, bool Vertical, PdfPredefinedCMaps? Base)
            ReadCidMap(PdfStream stream)
        {
            var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
            return ReadCidMap(stream, seen, 0);
        }

        private (Dictionary<uint, uint> Map,
            List<(uint Low, uint High, int Length)> Spaces, bool Vertical, PdfPredefinedCMaps? Base)
            ReadCidMap(PdfStream stream, HashSet<PdfStream> seen, int depth)
        {
            if (depth >= 32 || !seen.Add(stream))
                throw new FormatException("Font encoding CMap inheritance cycle.");
            (Dictionary<uint, uint> Map,
                List<(uint Low, uint High, int Length)> Spaces, bool Vertical, PdfPredefinedCMaps? Base)? inherited =
                Get(stream.Dictionary, "UseCMap") switch
                {
                    null => null,
                    PdfStream baseStream => ReadCidMap(baseStream, seen, depth + 1),
                    PdfName name => PdfPredefinedCMaps.Find(name.ValueAsLatin1()) is { } map
                        ? (new Dictionary<uint, uint>(), [], name.ValueAsLatin1().EndsWith("-V", StringComparison.Ordinal), map)
                        : throw new NotSupportedException(
                            $"Inherited font encoding CMap /{name.ValueAsLatin1()} is not supported."),
                    _ => throw new FormatException("Invalid inherited font encoding CMap reference.")
                };
            var result = ParseCidMap(Decode(stream), inherited, document.UsesCompatibilityRecovery);
            seen.Remove(stream);
            return result;
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
                    for (int j = 0; j < values.Count; j++) widths[first + (uint)j] = Metric(Resolve(values[j]), 1000);
                }
                else
                {
                    uint last = Cid(second);
                    if (last < first || i >= array.Count) throw new FormatException("Invalid CID width range.");
                    if ((expanded += last - first + 1L) > 1_000_000) throw new FormatException("CID width expansion limit exceeded.");
                    double width = Metric(Resolve(array[i++]), 1000);
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
        private double Metric(PdfObject? value, double fallback)
        {
            if (document.UsesCompatibilityRecovery && value is PdfNull)
            {
                _recoveredNullMetric = true;
                return fallback;
            }
            return Number(value, fallback);
        }
        private IReadOnlyList<string> RecoveryDiagnostics()
        {
            var diagnostics = new List<string>();
            if (_recoveredType1Lengths) diagnostics.AddRange(array);
            if (_ignoredUnicodeMap) diagnostics.AddRange(array0);
            if (_ignoredInvalidDifferences) diagnostics.AddRange(invalidDifferencesDiagnostic);
            if (_clampedSimpleWidths) diagnostics.AddRange(simpleWidthsDiagnostic);
            if (_recoveredNullMetric) diagnostics.AddRange(nullMetricDiagnostic);
            return diagnostics.AsReadOnly();
        }
        private byte[] DecodeEmbeddedFont(PdfStream stream)
        {
            if (stream.Dictionary.TryGetValue(new PdfName("Length1"u8), out PdfObject hint)
                && hint is PdfInteger { Value: >= 16384 and <= 33554432 } length
                && length.Value <= Math.Max(65536L, stream.EncodedData.Length * 1024L))
                return document.DecodeFontStream(stream, checked((int)length.Value));
            return Decode(stream);
        }

        private byte[] Decode(PdfStream stream) =>
            document.DecodeStream(stream, 32 * 1024 * 1024);
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

    private static (Dictionary<uint, uint> Map, List<(uint Low, uint High, int Length)> Spaces,
        bool Vertical, PdfPredefinedCMaps? Base) ParseCidMap(
        byte[] data, (Dictionary<uint, uint> Map,
            List<(uint Low, uint High, int Length)> Spaces, bool Vertical, PdfPredefinedCMaps? Base)? inherited,
        bool compatibilityRecovery)
    {
        var map = inherited is null
            ? [] : new Dictionary<uint, uint>(inherited.Value.Map);
        var spaces = inherited is null
            ? [] : new List<(uint Low, uint High, int Length)>(inherited.Value.Spaces);
        long expanded = 0;
        bool vertical = inherited?.Vertical ?? false;
        PdfPredefinedCMaps? baseMap = inherited?.Base;
        foreach (var instruction in PdfContentStreamReader.Read(
            PdfCMapMetadata.WithoutDictionaries(data, compatibilityRecovery)))
        {
            var values = instruction.Operands;
            if (instruction.Operator == "usecmap")
            {
                if (values.Count != 1 || values[0] is not PdfName name)
                    throw new FormatException("Invalid inherited font encoding CMap reference.");
                if (inherited is null)
                {
                    baseMap = PdfPredefinedCMaps.Find(name.ValueAsLatin1())
                        ?? throw new NotSupportedException(
                            $"Inherited font encoding CMap /{name.ValueAsLatin1()} is not supported.");
                    if (name.ValueAsLatin1().EndsWith("-V", StringComparison.Ordinal))
                        vertical = true;
                }
                continue;
            }
            if (instruction.Operator == "def" && values.Count == 2 && Name(values[0]) == "WMode") vertical = Number(values[1]) == 1;
            if (instruction.Operator == "endcodespacerange")
            {
                if (values.Count % 2 != 0 || values.Count > 512) throw new FormatException("Invalid CID code spaces.");
                for (int i = 0; i < values.Count; i += 2)
                {
                    var (Code, Length) = ReadCode(values[i]);
                    var high = ReadCode(values[i + 1]);
                    if (Length != high.Length || high.Code < Code) throw new FormatException("Invalid CID code space range.");
                    var space = (Code, high.Code, Length);
                    if (!spaces.Contains(space)) spaces.Add(space);
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
        return (map, spaces, vertical, baseMap);
    }

    private static (uint Code, int Length) ReadCode(PdfObject value)
    {
        if (value is not PdfString text || text.Bytes.Length is < 1 or > 4) throw new FormatException("Invalid font character code.");
        uint code = 0;
        foreach (byte b in text.Bytes.Span) code = (code << 8) | b;
        return (code, text.Bytes.Length);
    }


}
