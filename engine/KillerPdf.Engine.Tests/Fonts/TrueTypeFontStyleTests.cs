using System.Buffers.Binary;
using System.Text;
using System.Runtime.InteropServices;
using KillerPdf.Engine.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Fonts;

public sealed class TrueTypeFontStyleTests
{
    [Fact]
    public void Load_ReportsDeclaredNormalStyle()
    {
        byte[] bytes = BuildFont(4, 0x0040, 0);
        byte[] original = bytes.ToArray();

        Assert.Equal(TrueTypeFontStyle.Normal, TrueTypeFont.Load(bytes).DeclaredStyle);
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(4, 0x0001, 0x0002, TrueTypeFontStyle.Italic)]
    [InlineData(4, 0x0021, 0x0003, TrueTypeFontStyle.Italic)]
    [InlineData(4, 0x0200, 0x0000, TrueTypeFontStyle.Oblique)]
    [InlineData(4, 0x0201, 0x0002, TrueTypeFontStyle.Oblique)]
    [InlineData(5, 0x0220, 0x0001, TrueTypeFontStyle.Oblique)]
    [InlineData(5, 0x0221, 0x0003, TrueTypeFontStyle.Oblique)]
    [InlineData(0, 0x0000, 0x0000, TrueTypeFontStyle.Normal)]
    [InlineData(1, 0x0020, 0x0001, TrueTypeFontStyle.Normal)]
    [InlineData(2, 0xFF80, 0x0000, TrueTypeFontStyle.Normal)]
    [InlineData(3, 0xFF81, 0x0002, TrueTypeFontStyle.Italic)]
    [InlineData(4, 0xFD9E, 0xFFFC, TrueTypeFontStyle.Normal)]
    [InlineData(4, 0x0001, 0x0000, TrueTypeFontStyle.Unknown)]
    [InlineData(4, 0x0000, 0x0002, TrueTypeFontStyle.Unknown)]
    [InlineData(4, 0x0020, 0x0000, TrueTypeFontStyle.Unknown)]
    [InlineData(4, 0x0000, 0x0001, TrueTypeFontStyle.Unknown)]
    [InlineData(4, 0x0041, 0x0002, TrueTypeFontStyle.Unknown)]
    [InlineData(4, 0x0060, 0x0001, TrueTypeFontStyle.Unknown)]
    [InlineData(5, 0x0040, 0x0000, TrueTypeFontStyle.Normal)]
    public void Load_ClassifiesConsistentDeclaredFlags(ushort version, ushort selection,
        ushort macStyle, TrueTypeFontStyle expected)
    {
        byte[] bytes = BuildFont(version, selection, macStyle);
        byte[] original = bytes.ToArray();

        Assert.Equal(expected, TrueTypeFont.Load(bytes).DeclaredStyle);
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(4, 0, 54, true)]
    [InlineData(4, 1, 54, true)]
    [InlineData(4, 2, 54, true)]
    [InlineData(4, 10, 54, true)]
    [InlineData(4, 62, 54, true)]
    [InlineData(4, 63, 54, true)]
    [InlineData(4, 64, 44, true)]
    [InlineData(4, 64, 45, true)]
    [InlineData(4, 64, 54, false)]
    [InlineData(6, 64, 54, true)]
    [InlineData(65535, 64, 54, true)]
    public void Load_LeavesIncompleteOrUnsupportedMetadataUnknown(ushort version,
        int os2Length, int headLength, bool includeOs2)
    {
        byte[] bytes = BuildFont(version, 0x0040, 0, os2Length, headLength, includeOs2);
        byte[] original = bytes.ToArray();
        TrueTypeFont font = TrueTypeFont.Load(bytes);

        Assert.Equal(TrueTypeFontStyle.Unknown, font.DeclaredStyle);
        Assert.Equal((ushort)1, font.GetGlyphId('A'));
        Assert.Equal(600, font.GetPdfAdvanceWidth(1));
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(true, TrueTypeFontStyle.Normal)]
    [InlineData(false, TrueTypeFontStyle.Unknown)]
    public void Load_DoesNotInferStyleFromNameOrAngle(bool includeOs2, TrueTypeFontStyle expected)
    {
        TrueTypeFont font = TrueTypeFont.Load(BuildFont(4, 0x0040, 0,
            includeOs2: includeOs2, misleadingNameAndAngle: true));

        Assert.Equal("ObliqueFoo", font.PostScriptName);
        Assert.Equal(-12, font.ItalicAngle);
        Assert.Equal(expected, font.DeclaredStyle);
    }

    [Fact]
    public void Load_CapturesDeclaredStyleIndependentlyOfLaterByteMutations()
    {
        byte[] bytes = BuildFont(4, 0x0001, 0x0002);
        TrueTypeFont font = TrueTypeFont.Load(bytes);
        bytes.AsSpan().Clear();
        Assert.Equal(TrueTypeFontStyle.Italic, font.DeclaredStyle);
        Assert.Equal((ushort)1, font.GetGlyphId('A'));
        Assert.True(MemoryMarshal.TryGetArray(font.FontData, out ArraySegment<byte> owned));
        owned.AsSpan().Clear();
        Assert.Equal(TrueTypeFontStyle.Italic, font.DeclaredStyle);
    }

    [Fact]
    public void Load_StillRejectsFontWithoutRequiredHeadTable()
    {
        byte[] bytes = BuildFont(4, 0x0040, 0);
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        for (int record = 12; record < 12 + count * 16; record += 16)
            if (Encoding.ASCII.GetString(bytes, record, 4) == "head")
                Encoding.ASCII.GetBytes("xxxx").CopyTo(bytes, record);

        Assert.Throws<FormatException>(() => TrueTypeFont.Load(bytes));
    }

    private static byte[] BuildFont(ushort version, ushort selection, ushort macStyle,
        int os2Length = 64, int headLength = 54, bool includeOs2 = true,
        bool misleadingNameAndAngle = false)
    {
        byte[]? post = null;
        if (misleadingNameAndAngle)
        {
            post = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
            BinaryPrimitives.WriteInt32BigEndian(post.AsSpan(4), -12 * 65536);
        }
        byte[] source = TrueTypeFontTests.BuildTestFont(format12: false, post: post);
        if (misleadingNameAndAngle) TrueTypeFontTests.RenameFace(source, "ObliqueFoo");
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        int count = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(4));
        for (int record = 12; record < 12 + count * 16; record += 16)
        {
            string tag = Encoding.ASCII.GetString(source, record, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(record + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(record + 12));
            tables[tag] = source.AsSpan(offset, length).ToArray();
        }
        byte[] head = tables["head"][..headLength];
        if (head.Length >= 46) U16(head, 44, macStyle);
        tables["head"] = head;
        if (includeOs2)
        {
            byte[] os2 = new byte[os2Length];
            if (os2.Length >= 2) U16(os2, 0, version);
            if (os2.Length >= 64) U16(os2, 62, selection);
            tables["OS/2"] = os2;
        }
        int position = 12 + tables.Count * 16;
        byte[] result = new byte[position + tables.Values.Sum(table => (table.Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32BigEndian(result, 0x00010000);
        U16(result, 4, (ushort)tables.Count);
        int directory = 12;
        foreach ((string tag, byte[] table) in tables)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(result, directory);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(directory + 8), (uint)position);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(directory + 12), (uint)table.Length);
            table.CopyTo(result, position);
            position += (table.Length + 3) & ~3;
            directory += 16;
        }
        return result;
    }

    private static void U16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);
}
