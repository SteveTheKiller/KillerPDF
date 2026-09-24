using System.Globalization;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Authoring;

internal static class PdfEmbeddedTrueTypeFontFactory
{
    internal static EmbeddedTrueTypeFontObjects Create(
        TrueTypeFont font, IReadOnlyDictionary<ushort, EmbeddedCharacterMapping> mappings,
        PdfIndirectReference _, PdfIndirectReference cidFontReference,
        PdfIndirectReference descriptorReference, PdfIndirectReference fontFileReference,
        PdfIndirectReference toUnicodeReference, PdfIndirectReference encodingReference)
    {
        byte[] fontProgram = font.FontData.ToArray();
        bool subset = false;
        if (!font.HasCffOutlines && font.SubsettingAllowed && mappings.Count > 0)
        {
            try
            {
                fontProgram = font.CreateSubset(mappings.Values.Select(mapping => mapping.Glyph));
                subset = true;
            }
            catch (NotSupportedException)
            {
                // Full embedding remains valid for fonts without subsettable glyf/loca outlines.
            }
        }
        string baseName = SanitizeFontName(font.PostScriptName);
        if (subset)
            baseName = $"{TrueTypeSubsetter.Prefix(font.FontData.ToArray(), mappings.Values.Select(mapping => mapping.Glyph))}+{baseName}";
        PdfName baseFont = Name(baseName);
        PdfDictionary fontFileDictionary = font.HasCffOutlines
            ? Dictionary(("Subtype", Name("OpenType")), ("Filter", Name("FlateDecode")))
            : Dictionary(("Length1", new PdfInteger(fontProgram.Length)),
                ("Filter", Name("FlateDecode")));
        var fontFile = new PdfStream(fontFileDictionary, Compress(fontProgram));

        int flags = 32;
        if (font.ItalicAngle != 0) flags |= 64;
        var descriptorEntries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("FontDescriptor")), ("FontName", baseFont),
            ("Flags", new PdfInteger(flags)),
            ("FontBBox", new PdfArray([
                new PdfInteger(Scale(font.Bounds.XMin, font.UnitsPerEm)),
                new PdfInteger(Scale(font.Bounds.YMin, font.UnitsPerEm)),
                new PdfInteger(Scale(font.Bounds.XMax, font.UnitsPerEm)),
                new PdfInteger(Scale(font.Bounds.YMax, font.UnitsPerEm))])),
            ("ItalicAngle", Number(font.ItalicAngle)),
            ("Ascent", new PdfInteger(Scale(font.Ascender, font.UnitsPerEm))),
            ("Descent", new PdfInteger(Scale(font.Descender, font.UnitsPerEm))),
            ("CapHeight", new PdfInteger(Scale(font.Ascender, font.UnitsPerEm))),
            ("StemV", new PdfInteger(80)),
            (font.HasCffOutlines ? "FontFile3" : "FontFile2", fontFileReference)
        };
        PdfDictionary descriptor = Dictionary([.. descriptorEntries]);

        var widths = new List<PdfObject>();
        foreach (ushort glyph in mappings.Values.Select(mapping => mapping.Glyph).Distinct().Order())
        {
            widths.Add(new PdfInteger(glyph));
            widths.Add(new PdfArray([new PdfInteger(font.GetPdfAdvanceWidth(glyph))]));
        }
        var cidEntries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Font")),
            ("Subtype", Name(font.HasCffOutlines ? "CIDFontType0" : "CIDFontType2")),
            ("BaseFont", baseFont),
            ("CIDSystemInfo", Dictionary(
                ("Registry", Latin1String("Adobe")), ("Ordering", Latin1String("Identity")),
                ("Supplement", new PdfInteger(0)))),
            ("FontDescriptor", descriptorReference), ("DW", new PdfInteger(1000)),
            ("W", new PdfArray(widths))
        };
        if (!font.HasCffOutlines)
            cidEntries.Add(("CIDToGIDMap", Name("Identity")));
        PdfDictionary cidFont = Dictionary([.. cidEntries]);
        var toUnicode = CompressedStream(BuildToUnicodeMap(mappings));
        var encoding = CompressedStream(BuildEncodingMap(mappings));
        PdfObject type0Encoding = mappings.All(mapping => mapping.Key == mapping.Value.Glyph)
            ? Name("Identity-H") : encodingReference;
        PdfDictionary type0 = Dictionary(
            ("Type", Name("Font")), ("Subtype", Name("Type0")),
            ("BaseFont", baseFont), ("Encoding", type0Encoding),
            ("DescendantFonts", new PdfArray([cidFontReference])),
            ("ToUnicode", toUnicodeReference));
        return new EmbeddedTrueTypeFontObjects(type0, cidFont, descriptor, fontFile, toUnicode, encoding);
    }

    private static byte[] BuildToUnicodeMap(IReadOnlyDictionary<ushort, EmbeddedCharacterMapping> mappings)
    {
        var text = new StringBuilder(
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (KeyValuePair<ushort, EmbeddedCharacterMapping>[] chunk in mappings.Chunk(100))
        {
            text.Append(chunk.Length).Append(" beginbfchar\n");
            foreach ((ushort code, EmbeddedCharacterMapping mapping) in chunk)
            {
                text.Append('<').Append(code.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (byte value in PdfUnicodeEncoding.EncodeBigEndian(mapping.UnicodeSequence))
                    text.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                text.Append(">\n");
            }
            text.Append("endbfchar\n");
        }
        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static byte[] BuildEncodingMap(
        IReadOnlyDictionary<ushort, EmbeddedCharacterMapping> mappings)
    {
        var text = new StringBuilder(
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> def\n" +
            "/CMapName /KillerPDF-Identity def\n/CMapType 1 def\n/WMode 0 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (KeyValuePair<ushort, EmbeddedCharacterMapping>[] chunk in mappings.Chunk(100))
        {
            text.Append(chunk.Length).Append(" begincidchar\n");
            foreach ((ushort code, EmbeddedCharacterMapping mapping) in chunk)
                text.Append('<').Append(code.ToString("X4", CultureInfo.InvariantCulture))
                    .Append("> ").Append(mapping.Glyph).Append('\n');
            text.Append("endcidchar\n");
        }
        text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static int Scale(int value, int unitsPerEm) =>
            (int)Math.Round(value * 1000d / unitsPerEm, MidpointRounding.AwayFromZero);
    private static PdfStream CompressedStream(byte[] data) =>
        new(Dictionary(("Filter", Name("FlateDecode"))), Compress(data));
    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }
    private static string SanitizeFontName(string value)
    {
        string cleaned = new([.. value.Where(character => character is >= '!' and <= '~'
            && character is not '(' and not ')' and not '<' and not '>' and not '[' and not ']'
            && character is not '{' and not '}' and not '/' and not '%' and not '#')]);
        return cleaned.Length == 0 ? "EmbeddedTrueTypeFont" : cleaned;
    }
    private static PdfObject Number(double value) => value == Math.Truncate(value)
        ? new PdfInteger(checked((long)value)) : new PdfReal(value);
    private static PdfString Latin1String(string value) =>
        new(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal);
    private static PdfDictionary Dictionary(params (string Name, PdfObject Value)[] entries) =>
        new(entries.Select(entry => new KeyValuePair<PdfName, PdfObject>(Name(entry.Name), entry.Value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

internal sealed record EmbeddedTrueTypeFontObjects(
    PdfDictionary Type0, PdfDictionary CidFont, PdfDictionary Descriptor,
    PdfStream FontFile, PdfStream ToUnicode, PdfStream Encoding);
