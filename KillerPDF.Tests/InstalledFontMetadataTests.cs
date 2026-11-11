using System.Buffers.Binary;
using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;
using Xunit.Abstractions;

namespace KillerPDF.Tests;

public sealed class InstalledFontMetadataTests(ITestOutputHelper output)
{
    [Fact]
    public void MetadataReadsDoNotCopyTheUnusedFontPayload()
    {
        byte[] metadata = Font(0x40, 0, (3, 1, 0x409, 1, "Small Metadata"));
        byte[] font = new byte[8 * 1024 * 1024];
        metadata.CopyTo(font, 0);
        using var input = new CountingStream(font);
        Assert.Equal("Small Metadata", Assert.Single(InstalledFontMetadata.Read(input)).Family);
        output.WriteLine($"Metadata bytes read: {input.BytesRead}; source bytes: {input.Length}.");
        Assert.InRange(input.BytesRead, 1, 4096);
    }

    [Fact]
    public void UnicodeFamilyRetainsCjkAndSupplementaryCharacters()
    {
        const string family = "Family \u6f22\u5b57 \U0001d504";
        using var input = new MemoryStream(Font(0x40, 0, (3, 10, 0x409, 1, family)));
        Assert.Equal(family, Assert.Single(InstalledFontMetadata.Read(input)).Family);
    }

    [Fact]
    public void RepeatedCollectionOffsetsCannotMultiplyMetadataReadWork()
    {
        var names = Enumerable.Range(0, 256).Select(_ => ((ushort)3, (ushort)1, (ushort)0x409,
            (ushort)1, "Bounded Family")).ToArray();
        byte[] face = Font(0x40, 0, names);
        const int count = 4096;
        int start = 12 + 4 * count;
        byte[] collection = new byte[start + face.Length];
        "ttcf"u8.CopyTo(collection); U32(collection, 4, 0x10000); U32(collection, 8, count);
        for (int index = 0; index < count; index++) U32(collection, 12 + 4 * index, (uint)start);
        face.CopyTo(collection, start);
        for (int table = 0; table < 3; table++)
        {
            int position = start + 12 + 16 * table + 8;
            U32(collection, position, BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(position)) + (uint)start);
        }
        using var input = new CountingStream(collection);
        var result = InstalledFontMetadata.Read(input);
        output.WriteLine($"Repeated collection bytes read: {input.BytesRead}.");
        Assert.True(input.BytesRead < 2 * 1024 * 1024, $"Metadata read {input.BytesRead} bytes for repeated offsets.");
        Assert.Empty(result);
    }

    [Fact]
    public void LegacyMacRomanFamilyDecodesItsAccentedBytes()
    {
        byte[] font = Font(0x40, 0, (3, 1, 0x409, 1, "Placeholder"));
        int name = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(52));
        U16(font, name + 6, 1); U16(font, name + 8, 0); U16(font, name + 10, 0);
        U16(font, name + 14, 4);
        new byte[] { 0x43, 0x61, 0x66, 0x8e }.CopyTo(font, name + 18);
        using var input = new MemoryStream(font);
        Assert.Equal("Caf\u00e9", Assert.Single(InstalledFontMetadata.Read(input)).Family);
    }

    [Fact]
    public void WindowsFamilyPrecedesUnicodeFallbackLikeTheExistingCatalog()
    {
        using var input = new MemoryStream(Font(0x40, 0,
            (0, 3, 0, 1, "Unicode Family"), (3, 1, 0x411, 1, "Japanese Family")));
        Assert.Equal("Japanese Family", Assert.Single(InstalledFontMetadata.Read(input)).Family);
    }

    [Theory]
    [InlineData("head-missing")]
    [InlineData("head-magic")]
    [InlineData("head-version")]
    [InlineData("head-short")]
    [InlineData("table-outside")]
    [InlineData("selection-short")]
    [InlineData("name-overlap")]
    [InlineData("name-odd")]
    [InlineData("name-surrogate")]
    [InlineData("count-overflow")]
    [InlineData("cff")]
    public void MalformedRequiredMetadataDoesNotRegisterAFace(string scenario)
    {
        byte[] font = Font(0x40, 0, (3, 1, 0x409, 1, "Safety Family"));
        int head = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(20));
        int name = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(52));
        switch (scenario)
        {
            case "head-missing": "skip"u8.CopyTo(font.AsSpan(12)); break;
            case "head-magic": U32(font, head + 12, 0); break;
            case "head-version": U32(font, head, 0); break;
            case "head-short": U32(font, 24, 46); break;
            case "table-outside": U32(font, 20, uint.MaxValue); break;
            case "selection-short": U32(font, 40, 63); break;
            case "name-overlap": U16(font, name + 4, 6); break;
            case "name-odd": U16(font, name + 14, 1); break;
            case "name-surrogate": U16(font, name + 18, 0xd800); break;
            case "count-overflow": U16(font, 4, ushort.MaxValue); break;
            case "cff": "OTTO"u8.CopyTo(font); break;
        }
        using var input = new MemoryStream(font);
        Assert.Empty(InstalledFontMetadata.Read(input));
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, false, true)]
    [InlineData(3, true, true)]
    public void HeadStyleIsTheFallbackWhenOs2IsAbsent(ushort macStyle, bool bold, bool italic)
    {
        byte[] font = Font(0, macStyle, (3, 1, 0x409, 1, "Fallback Family"));
        "skip"u8.CopyTo(font.AsSpan(28));
        using var input = new MemoryStream(font);
        var face = Assert.Single(InstalledFontMetadata.Read(input));
        Assert.Equal((bold, italic), (face.Bold, face.Italic));
    }

    [Fact]
    public void CollectionKeepsOriginalIndexesWhenOneFaceIsCorrupt()
    {
        byte[] first = Font(0x20, 0, (3, 1, 0x409, 1, "First Family"));
        byte[] corrupt = Font(0x40, 0, (3, 1, 0x409, 1, "Unreadable Family"));
        U32(corrupt, 0, 0);
        byte[] last = Font(1, 0, (3, 1, 0x409, 1, "Last Family"));
        using var input = new MemoryStream(Collection(first, corrupt, last));
        var faces = InstalledFontMetadata.Read(input);
        Assert.Equal(new[] { "First Family", "Last Family" }, faces.Select(f => f.Family));
        Assert.Equal(new[] { 0, 2 }, faces.Select(f => f.FaceIndex));
        Assert.True(faces[0].Bold);
        Assert.True(faces[1].Italic);
    }

    [Theory]
    [InlineData(0x20, 0, true, false)]
    [InlineData(1, 0, false, true)]
    [InlineData(0x21, 0, true, true)]
    [InlineData(0x40, 3, false, false)]
    public void Os2SelectionControlsTheFourStyles(ushort selection, ushort macStyle, bool bold, bool italic)
    {
        using var input = new MemoryStream(Font(selection, macStyle, (3, 1, 0x409, 1, "Style Family")));
        var face = Assert.Single(InstalledFontMetadata.Read(input));
        Assert.Equal((bold, italic), (face.Bold, face.Italic));
    }

    [Fact]
    public void EnglishLegacyFamilyKeepsTheFourStyleCatalogGrouping()
    {
        byte[] font = Font(0x40, 0,
            (3, 1, 0x411, 1, "Localized Family"),
            (3, 1, 0x409, 16, "Typographic Family"),
            (3, 1, 0x409, 1, "Legacy Family"));
        using var input = new MemoryStream(font, writable: false);
        var face = Assert.Single(InstalledFontMetadata.Read(input));
        Assert.Equal("Legacy Family", face.Family);
        Assert.Equal((false, false, 0), (face.Bold, face.Italic, face.FaceIndex));
        Assert.True(input.CanRead);
    }

    private static byte[] Font(ushort selection, ushort macStyle,
        params (ushort Platform, ushort Encoding, ushort Language, ushort Id, string Text)[] names)
    {
        byte[] head = new byte[54];
        U32(head, 0, 0x00010000); U32(head, 12, 0x5f0f3cf5); U16(head, 18, 1000); U16(head, 44, macStyle);
        byte[] os2 = new byte[78];
        U16(os2, 4, 400); U16(os2, 62, selection);
        int storage = 6 + 12 * names.Length;
        var strings = names.Select(n => Encoding.BigEndianUnicode.GetBytes(n.Text)).ToArray();
        byte[] name = new byte[storage + strings.Sum(s => s.Length)];
        U16(name, 2, (ushort)names.Length); U16(name, 4, (ushort)storage);
        int position = storage;
        for (int i = 0; i < names.Length; i++)
        {
            int record = 6 + 12 * i;
            U16(name, record, names[i].Platform); U16(name, record + 2, names[i].Encoding);
            U16(name, record + 4, names[i].Language); U16(name, record + 6, names[i].Id);
            U16(name, record + 8, (ushort)strings[i].Length); U16(name, record + 10, (ushort)(position - storage));
            strings[i].CopyTo(name, position); position += strings[i].Length;
        }
        return Sfnt(("head", head), ("OS/2", os2), ("name", name));
    }

    private static byte[] Sfnt(params (string Tag, byte[] Bytes)[] tables)
    {
        int position = 12 + 16 * tables.Length;
        byte[] font = new byte[position + tables.Sum(t => (t.Bytes.Length + 3) & ~3)];
        U32(font, 0, 0x00010000); U16(font, 4, (ushort)tables.Length);
        for (int i = 0; i < tables.Length; i++)
        {
            int record = 12 + 16 * i;
            Encoding.ASCII.GetBytes(tables[i].Tag).CopyTo(font, record);
            U32(font, record + 8, (uint)position); U32(font, record + 12, (uint)tables[i].Bytes.Length);
            tables[i].Bytes.CopyTo(font, position); position += (tables[i].Bytes.Length + 3) & ~3;
        }
        return font;
    }

    private static byte[] Collection(params byte[][] faces)
    {
        int position = 12 + 4 * faces.Length;
        byte[] result = new byte[position + faces.Sum(f => f.Length)];
        "ttcf"u8.CopyTo(result); U32(result, 4, 0x00010000); U32(result, 8, (uint)faces.Length);
        for (int index = 0; index < faces.Length; index++)
        {
            U32(result, 12 + index * 4, (uint)position);
            faces[index].CopyTo(result, position);
            int tables = BinaryPrimitives.ReadUInt16BigEndian(faces[index].AsSpan(4));
            for (int table = 0; table < tables; table++)
            {
                int offset = position + 12 + 16 * table + 8;
                uint original = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(offset));
                U32(result, offset, original + (uint)position);
            }
            position += faces[index].Length;
        }
        return result;
    }

    private static void U16(byte[] bytes, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);
    private static void U32(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long BytesRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            int count = base.Read(buffer);
            BytesRead += count;
            return count;
        }
    }
}
