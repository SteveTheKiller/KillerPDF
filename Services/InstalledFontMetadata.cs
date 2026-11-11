using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace KillerPDF.Services;

/// <summary>The name, four-style selection and original collection index of an installed face.</summary>
internal sealed record InstalledFontFace(string Family, bool Bold, bool Italic, int FaceIndex);

/// <summary>Reads only the metadata needed by the installed TrueType font catalog.</summary>
internal static class InstalledFontMetadata
{
    private static readonly Encoding Unicode = new UnicodeEncoding(true, false, true);
    private static readonly Encoding MacRoman = CodePagesEncodingProvider.Instance.GetEncoding(10000)!;
    private readonly record struct Table(long Offset, uint Length);

    private sealed class Budget
    {
        private int _records = 65_536, _nameBytes = 4 * 1024 * 1024;
        public void Records(int count) => Charge(ref _records, count);
        public void NameBytes(int count) => Charge(ref _nameBytes, count);
        private static void Charge(ref int remaining, int count)
        {
            if (count > remaining) throw new InvalidDataException("Font metadata exceeds its read limit.");
            remaining -= count;
        }
    }

    public static IReadOnlyList<InstalledFontFace> Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("Font metadata requires a readable, seekable stream.", nameof(input));
        try
        {
            var budget = new Budget();
            Span<byte> header = stackalloc byte[12];
            if (!ReadAt(input, 0, header)) return [];
            if (!header[..4].SequenceEqual("ttcf"u8))
            {
                var face = ReadFace(input, 0, 0, budget);
                return face is null ? [] : [face];
            }
            uint version = U32(header[4..]), count = U32(header[8..]);
            if (version is not (0x00010000 or 0x00020000) || count is 0 or > 4096) return [];
            long end = 12L + 4L * count + (version == 0x00020000 ? 12 : 0);
            if (end > input.Length) return [];
            var faces = new List<InstalledFontFace>();
            Span<byte> offset = stackalloc byte[4];
            for (int index = 0; index < count; index++)
            {
                if (!ReadAt(input, 12L + 4L * index, offset)) return [];
                long position = U32(offset);
                if (position < end) continue;
                if (ReadFace(input, position, index, budget) is { } face) faces.Add(face);
            }
            return faces;
        }
        catch (IOException) { return []; }
        catch (InvalidDataException) { return []; }
    }

    private static InstalledFontFace? ReadFace(Stream input, long faceOffset, int faceIndex, Budget budget)
    {
        Span<byte> header = stackalloc byte[12];
        if (!ReadAt(input, faceOffset, header)) return null;
        uint signature = U32(header);
        // CFF faces were not registered by the existing TrueType catalog.
        if (signature is not (0x00010000 or 0x74727565)) return null;
        int count = U16(header[4..]);
        if (count is < 1 or > 512 || faceOffset > input.Length - 12L - 16L * count) return null;
        budget.Records(count);
        Table? names = null, selection = null, fontHeader = null;
        Span<byte> record = stackalloc byte[16];
        for (int index = 0; index < count; index++)
        {
            if (!ReadAt(input, faceOffset + 12 + 16L * index, record)) return null;
            uint offset = U32(record[8..]), length = U32(record[12..]);
            if (offset > input.Length - length) return null;
            if (record[..4].SequenceEqual("head"u8))
            {
                if (fontHeader is not null || length < 54) return null;
                fontHeader = new Table(offset, length);
            }
            if (record[..4].SequenceEqual("OS/2"u8))
            {
                if (selection is not null || length < 64) return null;
                selection = new Table(offset, length);
            }
            if (!record[..4].SequenceEqual("name"u8)) continue;
            if (names is not null) return null;
            names = new Table(offset, length);
        }
        Span<byte> requiredHeader = stackalloc byte[16];
        if (fontHeader is not { } required || !ReadAt(input, required.Offset, requiredHeader)
            || U32(requiredHeader) != 0x00010000 || U32(requiredHeader[12..]) != 0x5f0f3cf5) return null;
        string? family = names is { } table ? ReadFamily(input, table, budget) : null;
        Span<byte> flags = stackalloc byte[2];
        ushort style = 0;
        if (selection is { } os2)
        {
            if (!ReadAt(input, os2.Offset + 62, flags)) return null;
            style = U16(flags);
        }
        else if (fontHeader is { } head)
        {
            if (!ReadAt(input, head.Offset + 44, flags)) return null;
            ushort macStyle = U16(flags);
            style = (ushort)(((macStyle & 1) != 0 ? 0x20 : 0) | ((macStyle & 2) != 0 ? 1 : 0));
        }
        return string.IsNullOrWhiteSpace(family) ? null
            : new InstalledFontFace(family, (style & 0x20) != 0, (style & 1) != 0, faceIndex);
    }

    private static string? ReadFamily(Stream input, Table table, Budget budget)
    {
        Span<byte> header = stackalloc byte[6];
        if (table.Length < 6 || !ReadAt(input, table.Offset, header)) return null;
        int version = U16(header), count = U16(header[2..]), storage = U16(header[4..]);
        if (version > 1 || count > 4096 || storage < 6L + 12L * count || storage > table.Length) return null;
        budget.Records(count);
        string? best = null;
        int bestRank = int.MaxValue;
        Span<byte> record = stackalloc byte[12];
        for (int index = 0; index < count; index++)
        {
            if (!ReadAt(input, table.Offset + 6 + 12L * index, record)) return null;
            int platform = U16(record), encoding = U16(record[2..]), language = U16(record[4..]);
            bool mac = platform == 1 && encoding == 0;
            if (U16(record[6..]) != 1 || !(mac || platform == 0 || platform == 3 && encoding is 0 or 1 or 10)) continue;
            int length = U16(record[8..]), offset = U16(record[10..]);
            if (length is 0 or > 4096 || !mac && (length & 1) != 0 || (long)storage + offset + length > table.Length) continue;
            int rank = platform == 3 ? (language == 0x409 ? 0 : 1) : mac ? (language == 0 ? 3 : 4) : 2;
            if (rank >= bestRank) continue;
            budget.NameBytes(length);
            byte[] bytes = new byte[length];
            if (!ReadAt(input, table.Offset + storage + offset, bytes)) continue;
            string family;
            try { family = (mac ? MacRoman : Unicode).GetString(bytes); }
            catch (DecoderFallbackException) { continue; }
            if (string.IsNullOrWhiteSpace(family) || family.Any(char.IsControl)) continue;
            best = family;
            bestRank = rank;
        }
        return best;
    }

    private static bool ReadAt(Stream input, long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > input.Length - destination.Length) return false;
        input.Position = offset;
        input.ReadExactly(destination);
        return true;
    }

    private static ushort U16(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16BigEndian(value);
    private static uint U32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);
}
