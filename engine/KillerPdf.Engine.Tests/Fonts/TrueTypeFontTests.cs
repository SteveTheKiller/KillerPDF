using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Fonts;

public sealed class TrueTypeFontTests
{
    [Fact]
    public void Load_ReadsNameMetricsWidthsAndFormat4Cmap()
    {
        TrueTypeFont font = TrueTypeFont.Load(BuildTestFont(format12: false));

        Assert.Equal("KillerTest", font.PostScriptName);
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal(2, font.GlyphCount);
        Assert.Equal(800, font.Ascender);
        Assert.Equal(-200, font.Descender);
        Assert.Equal(new TrueTypeBounds(-10, -200, 900, 800), font.Bounds);
        Assert.Equal((ushort)1, font.GetGlyphId('A'));
        Assert.Equal((ushort)0, font.GetGlyphId('B'));
        Assert.Equal(600, font.GetPdfAdvanceWidth(1));
        Assert.True(font.EmbeddingAllowed);
        Assert.True(font.SubsettingAllowed);
    }

    [Fact]
    public void Load_ReadsFormat12SupplementaryPlaneMapping()
    {
        TrueTypeFont font = TrueTypeFont.Load(BuildTestFont(format12: true));

        Assert.Equal((ushort)1, font.GetGlyphId(0x1F600));
        Assert.Equal((ushort)0, font.GetGlyphId('A'));
    }

    [Theory]
    [InlineData(0, 0x0041)]
    [InlineData(2, 0x1234)]
    [InlineData(6, 0x0041)]
    [InlineData(8, 0x1F600)]
    [InlineData(10, 0x1F600)]
    [InlineData(13, 0x1F600)]
    public void Load_ReadsAdditionalUnicodeCmapFormats(int format, int scalar)
    {
        byte[] cmap = format switch
        {
            0 => Cmap0(),
            2 => Cmap2(),
            6 => Cmap6(),
            8 => Cmap8(),
            10 => Cmap10(),
            13 => Cmap13(),
            _ => throw new InvalidOperationException()
        };
        TrueTypeFont font = TrueTypeFont.Load(
            BuildTestFont(format12: false, cmap: cmap));

        Assert.Equal((ushort)1, font.GetGlyphId(scalar));
        Assert.Equal((ushort)0, font.GetGlyphId(scalar + 1));
    }

    [Fact]
    public void Load_RejectsInvalidFormat2SubheaderKeyAndGlyphRange()
    {
        byte[] invalidKey = Cmap2();
        U16(invalidKey, 12 + 6 + 0x12 * 2, 7);
        Assert.Throws<FormatException>(() => TrueTypeFont.Load(
            BuildTestFont(format12: false, cmap: invalidKey)));

        byte[] invalidRange = Cmap2();
        U16(invalidRange, 12 + 518 + 14, ushort.MaxValue);
        Assert.Throws<FormatException>(() => TrueTypeFont.Load(
            BuildTestFont(format12: false, cmap: invalidRange)));
    }

    [Fact]
    public void Load_ReadsFormat8MixedBmpAndSupplementaryMappings()
    {
        TrueTypeFont font = TrueTypeFont.Load(
            BuildTestFont(format12: false, cmap: Cmap8()));

        Assert.Equal((ushort)1, font.GetGlyphId('A'));
        Assert.Equal((ushort)1, font.GetGlyphId(0x1F600));
    }

    [Fact]
    public void Load_RejectsFormat8SupplementaryGroupWithoutIs32Marker()
    {
        byte[] cmap = Cmap8();
        int high = 0xD83D;
        cmap[12 + 12 + high / 8] = 0;

        Assert.Throws<FormatException>(() => TrueTypeFont.Load(
            BuildTestFont(format12: false, cmap: cmap)));
    }

    [Fact]
    public void Load_ReadsFormat14DefaultAndNonDefaultVariationSequences()
    {
        TrueTypeFont font = TrueTypeFont.Load(
            BuildTestFont(format12: false, cmap: Cmap14()));

        Assert.Equal((ushort)1, font.GetGlyphId('A', 0xFE0F));
        Assert.Equal((ushort)1, font.GetGlyphId('B', 0xFE0F));
        Assert.Equal((ushort)0, font.GetGlyphId('C', 0xFE0F));
        Assert.Equal((ushort)0, font.GetGlyphId('A', 0xFE0E));
        Assert.Throws<ArgumentOutOfRangeException>(() => font.GetGlyphId('A', 'B'));
    }

    [Fact]
    public void Load_RejectsOutOfBoundsFormat14VariationTable()
    {
        byte[] cmap = Cmap14();
        U32(cmap, 52 + 13, 1_000);

        Assert.Throws<FormatException>(() => TrueTypeFont.Load(
            BuildTestFont(format12: false, cmap: cmap)));
    }

    [Fact]
    public void Load_ReadsCffFlavouredOpenTypeMetricsAndMappings()
    {
        TrueTypeFont font = TrueTypeFont.Load(
            BuildTestFont(format12: false, cffOutlines: true));

        Assert.True(font.HasCffOutlines);
        Assert.Equal("KillerTest", font.PostScriptName);
        Assert.Equal((ushort)1, font.GetGlyphId('A'));
        Assert.Equal(600, font.GetPdfAdvanceWidth(1));
        Assert.Throws<NotSupportedException>(() => font.CreateSubset([1]));
    }

    [Fact]
    public void Load_RejectsTableThatPointsOutsideTheFile()
    {
        byte[] font = BuildTestFont(format12: false);
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(20, 4), uint.MaxValue);

        Assert.Throws<FormatException>(() => TrueTypeFont.Load(font));
    }

    [Fact]
    public void Load_ExposesRestrictedEmbeddingPermissions()
    {
        TrueTypeFont font = TrueTypeFont.Load(BuildTestFont(format12: false, embeddingFlags: 0x0002));

        Assert.False(font.EmbeddingAllowed);
    }

    [Fact]
    public void CreateSubset_PreservesGlyphIdsAndProducesDeterministicFont()
    {
        TrueTypeFont font = TrueTypeFont.Load(BuildTestFont(format12: false, includeOutlines: true));

        byte[] first = font.CreateSubset([1]);
        byte[] second = font.CreateSubset([1]);
        TrueTypeFont reopened = TrueTypeFont.Load(first);

        Assert.Equal(first, second);
        Assert.Equal((ushort)1, reopened.GetGlyphId('A'));
        Assert.Equal(600, reopened.GetPdfAdvanceWidth(1));
    }

    [Fact]
    public void Read_ExposesSimpleQuadraticGlyphContours()
    {
        TrueTypeFont embedded = TrueTypeFont.Load(
            BuildTestFont(format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder().BeginText()
            .SetFont(embedded, 12).ShowUnicodeText("A").EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, content).Build());
        PdfDictionary catalog = ResolveDictionary(document,
            document.Trailer[new PdfName("Root"u8)]);
        PdfDictionary pages = ResolveDictionary(document, catalog[new PdfName("Pages"u8)]);
        PdfDictionary page = ResolveDictionary(document,
            Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)])[0]);
        PdfDictionary resources = ResolveDictionary(document, page[new PdfName("Resources"u8)]);
        PdfDictionary fonts = ResolveDictionary(document, resources[new PdfName("Font"u8)]);
        PdfDictionary dictionary = ResolveDictionary(document, Assert.Single(fonts).Value);
        PdfExtractionFont font = PdfFontResourceReader.Read(document, dictionary);

        PdfGlyphOutline? outline = font.GetGlyphOutline(1);
        Assert.NotNull(outline);
        PdfGlyphContour contour = Assert.Single(outline.Contours);
        Assert.Equal([
            new PdfGlyphPoint(0, 0, true),
            new PdfGlyphPoint(1000, 0, true),
            new PdfGlyphPoint(500, 1000, true)], contour.Points);
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference
            ? document.Resolve(reference) : value);

    internal static byte[] BuildTestFont(
        bool format12, ushort embeddingFlags = 0, bool includeOutlines = false,
        bool cffOutlines = false, byte[]? cmap = null)
    {
        var tables = new Dictionary<string, byte[]>
        {
            ["cmap"] = cmap ?? (format12 ? Cmap12() : Cmap4()),
            ["head"] = Bytes(54, bytes =>
            {
                U16(bytes, 18, 1000);
                S16(bytes, 36, -10);
                S16(bytes, 38, -200);
                S16(bytes, 40, 900);
                S16(bytes, 42, 800);
            }),
            ["hhea"] = Bytes(36, bytes =>
            {
                S16(bytes, 4, 800);
                S16(bytes, 6, -200);
                U16(bytes, 34, 2);
            }),
            ["hmtx"] = Bytes(8, bytes =>
            {
                U16(bytes, 0, 500);
                U16(bytes, 4, 600);
            }),
            ["maxp"] = Bytes(6, bytes => U16(bytes, 4, 2)),
            ["name"] = NameTable()
        };
        if (cffOutlines)
            tables["CFF "] = [1, 0, 4, 1];
        if (embeddingFlags != 0)
            tables["OS/2"] = Bytes(10, bytes => U16(bytes, 8, embeddingFlags));
        if (includeOutlines)
        {
            tables["glyf"] = Bytes(24, bytes =>
            {
                S16(bytes, 0, 1);
                S16(bytes, 6, 1000);
                S16(bytes, 8, 1000);
                U16(bytes, 10, 2);
                bytes[14] = 0x31;
                bytes[15] = 0x21;
                bytes[16] = 0x01;
                S16(bytes, 17, 1000);
                S16(bytes, 19, -500);
                S16(bytes, 21, 1000);
            });
            tables["loca"] = Bytes(12, bytes =>
            {
                U32(bytes, 0, 0);
                U32(bytes, 4, 0);
                U32(bytes, 8, 24);
            });
            S16(tables["head"], 50, 1);
        }
        int directoryLength = 12 + tables.Count * 16;
        int totalLength = directoryLength + tables.Values.Sum(value => Align4(value.Length));
        byte[] result = new byte[totalLength];
        if (cffOutlines) Encoding.ASCII.GetBytes("OTTO").CopyTo(result, 0);
        else U32(result, 0, 0x00010000);
        U16(result, 4, tables.Count);
        int record = 12;
        int offset = directoryLength;
        foreach ((string tag, byte[] value) in tables.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(result, record);
            U32(result, record + 8, offset);
            U32(result, record + 12, value.Length);
            value.CopyTo(result, offset);
            record += 16;
            offset += Align4(value.Length);
        }
        return result;
    }

    private static byte[] Cmap4()
    {
        byte[] result = new byte[44];
        U16(result, 2, 1);
        U16(result, 4, 3);
        U16(result, 6, 1);
        U32(result, 8, 12);
        int subtable = 12;
        U16(result, subtable, 4);
        U16(result, subtable + 2, 32);
        U16(result, subtable + 6, 4);
        U16(result, subtable + 8, 4);
        U16(result, subtable + 10, 1);
        U16(result, subtable + 14, 0x0041);
        U16(result, subtable + 16, 0xFFFF);
        U16(result, subtable + 20, 0x0041);
        U16(result, subtable + 22, 0xFFFF);
        U16(result, subtable + 24, 0xFFC0);
        U16(result, subtable + 26, 1);
        return result;
    }

    private static byte[] Cmap12()
    {
        byte[] result = new byte[40];
        U16(result, 2, 1);
        U16(result, 4, 3);
        U16(result, 6, 10);
        U32(result, 8, 12);
        int subtable = 12;
        U16(result, subtable, 12);
        U32(result, subtable + 4, 28);
        U32(result, subtable + 12, 1);
        U32(result, subtable + 16, 0x1F600);
        U32(result, subtable + 20, 0x1F600);
        U32(result, subtable + 24, 1);
        return result;
    }

    private static byte[] Cmap6()
    {
        byte[] result = new byte[24];
        U16(result, 2, 1);
        U16(result, 4, 3);
        U16(result, 6, 1);
        U32(result, 8, 12);
        U16(result, 12, 6);
        U16(result, 14, 12);
        U16(result, 18, 0x0041);
        U16(result, 20, 1);
        U16(result, 22, 1);
        return result;
    }

    private static byte[] Cmap0()
    {
        byte[] result = new byte[274];
        U16(result, 2, 1);
        U16(result, 4, 0);
        U16(result, 6, 0);
        U32(result, 8, 12);
        U16(result, 12, 0);
        U16(result, 14, 262);
        result[18 + 0x41] = 1;
        return result;
    }

    private static byte[] Cmap2()
    {
        const int subtable = 12;
        const int length = 536;
        byte[] result = new byte[subtable + length];
        U16(result, 2, 1);
        U16(result, 4, 0);
        U16(result, 6, 3);
        U32(result, 8, subtable);
        U16(result, subtable, 2);
        U16(result, subtable + 2, length);
        U16(result, subtable + 6 + 0x12 * 2, 8);
        int subHeaders = subtable + 518;
        U16(result, subHeaders + 8, 0x34);
        U16(result, subHeaders + 10, 1);
        U16(result, subHeaders + 14, 2);
        U16(result, subHeaders + 16, 1);
        return result;
    }

    private static byte[] Cmap10()
    {
        byte[] result = new byte[34];
        U16(result, 2, 1);
        U16(result, 4, 3);
        U16(result, 6, 10);
        U32(result, 8, 12);
        U16(result, 12, 10);
        U32(result, 16, 22);
        U32(result, 24, 0x1F600);
        U32(result, 28, 1);
        U16(result, 32, 1);
        return result;
    }

    private static byte[] Cmap8()
    {
        const int subtable = 12;
        const int length = 8_232;
        byte[] result = new byte[subtable + length];
        U16(result, 2, 1);
        U16(result, 4, 3);
        U16(result, 6, 10);
        U32(result, 8, subtable);
        U16(result, subtable, 8);
        U32(result, subtable + 4, length);
        int high = 0xD83D;
        result[subtable + 12 + high / 8] |= (byte)(1 << (7 - high % 8));
        U32(result, subtable + 8_204, 2);
        U32(result, subtable + 8_208, 0x0041);
        U32(result, subtable + 8_212, 0x0041);
        U32(result, subtable + 8_216, 1);
        U32(result, subtable + 8_220, 0xD83DDE00);
        U32(result, subtable + 8_224, 0xD83DDE00);
        U32(result, subtable + 8_228, 1);
        return result;
    }

    private static byte[] Cmap13()
    {
        byte[] result = Cmap12();
        U16(result, 12, 13);
        return result;
    }

    internal static byte[] Cmap14()
    {
        byte[] format4 = Cmap4()[12..];
        const int baseOffset = 20;
        int variationOffset = baseOffset + 32;
        const int variationLength = 38;
        byte[] result = new byte[variationOffset + variationLength];
        U16(result, 2, 2);
        U16(result, 4, 3);
        U16(result, 6, 1);
        U32(result, 8, baseOffset);
        U16(result, 12, 0);
        U16(result, 14, 5);
        U32(result, 16, variationOffset);
        format4.CopyTo(result, baseOffset);
        U16(result, variationOffset, 14);
        U32(result, variationOffset + 2, variationLength);
        U32(result, variationOffset + 6, 1);
        U24(result, variationOffset + 10, 0xFE0F);
        U32(result, variationOffset + 13, 21);
        U32(result, variationOffset + 17, 29);
        U32(result, variationOffset + 21, 1);
        U24(result, variationOffset + 25, 0x0041);
        result[variationOffset + 28] = 0;
        U32(result, variationOffset + 29, 1);
        U24(result, variationOffset + 33, 0x0042);
        U16(result, variationOffset + 36, 1);
        return result;
    }

    private static byte[] NameTable()
    {
        byte[] name = Encoding.BigEndianUnicode.GetBytes("KillerTest");
        byte[] result = new byte[18 + name.Length];
        U16(result, 2, 1);
        U16(result, 4, 18);
        U16(result, 6, 3);
        U16(result, 8, 1);
        U16(result, 10, 0x0409);
        U16(result, 12, 6);
        U16(result, 14, name.Length);
        name.CopyTo(result, 18);
        return result;
    }

    private static byte[] Bytes(int length, Action<byte[]> initialize)
    {
        byte[] result = new byte[length];
        initialize(result);
        return result;
    }

    private static int Align4(int value) => (value + 3) & ~3;
    private static void U16(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset, 2), checked((ushort)value));
    private static void U24(byte[] target, int offset, int value)
    {
        target[offset] = checked((byte)(value >> 16));
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)value;
    }
    private static void S16(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteInt16BigEndian(target.AsSpan(offset, 2), checked((short)value));
    private static void U32(byte[] target, int offset, int value) => U32(target, offset, checked((uint)value));
    private static void U32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), value);
}
