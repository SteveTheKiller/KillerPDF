using System.Buffers.Binary;
using System.Text;

namespace KillerPdf.Engine.Tests.Rendering;

/// <summary>Builds small ICC v2 profiles and raw PDF files for color-management tests.</summary>
internal static class TestIccProfiles
{
    // D50 sRGB primaries as s15Fixed16 columns, and the D50 media white.
    internal static readonly int[][] SrgbColumns = [[28578, 14581, 912], [25241, 47027, 6366], [9374, 3972, 46811]];
    internal static readonly int[] D50White = [63190, 65536, 54061];

    /// <summary>A matrix/TRC RGB profile with identity curves.</summary>
    internal static byte[] MatrixRgb(string profileClass = "mntr", int[][]? columns = null, int[]? white = null)
    {
        columns ??= SrgbColumns;
        var tags = new List<(string Name, byte[] Data)>
        {
            ("rXYZ", Xyz(columns[0])), ("gXYZ", Xyz(columns[1])), ("bXYZ", Xyz(columns[2])),
            ("rTRC", IdentityCurve()), ("gTRC", IdentityCurve()), ("bTRC", IdentityCurve()),
            ("wtpt", Xyz(white ?? D50White))
        };
        return Profile(profileClass, "RGB ", "XYZ ", tags);
    }

    internal static byte[] Xyz(int[] values)
    {
        var data = new byte[20];
        "XYZ "u8.CopyTo(data);
        for (int index = 0; index < 3; index++)
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + index * 4), values[index]);
        return data;
    }

    internal static byte[] IdentityCurve() => [.. "curv"u8, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Assembles a profile header, tag table and four-byte aligned tag data.</summary>
    internal static byte[] Profile(string profileClass, string deviceSpace, string connectionSpace,
        IReadOnlyList<(string Name, byte[] Data)> tags)
    {
        int offset = 132 + 12 * tags.Count;
        var bytes = new List<byte>(new byte[offset]);
        var table = new byte[12 * tags.Count];
        for (int index = 0; index < tags.Count; index++)
        {
            while (bytes.Count % 4 != 0) bytes.Add(0);
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(table, index * 12);
            BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(index * 12 + 4), bytes.Count);
            BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(index * 12 + 8), tags[index].Data.Length);
            bytes.AddRange(tags[index].Data);
        }
        while (bytes.Count % 4 != 0) bytes.Add(0);
        byte[] profile = [.. bytes];
        BinaryPrimitives.WriteInt32BigEndian(profile, profile.Length);
        profile[8] = 2;
        Encoding.ASCII.GetBytes(profileClass).CopyTo(profile, 12);
        Encoding.ASCII.GetBytes(deviceSpace).CopyTo(profile, 16);
        Encoding.ASCII.GetBytes(connectionSpace).CopyTo(profile, 20);
        "acsp"u8.CopyTo(profile.AsSpan(36));
        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(128), tags.Count);
        table.CopyTo(profile, 132);
        return profile;
    }

    /// <summary>Writes a classic-xref PDF from object bodies; byte streams are kept exact through Latin-1.</summary>
    internal static byte[] Pdf(params string[] objects)
    {
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(source.Length);
            source.Append($"{index + 1} 0 obj {objects[index]} endobj\n");
        }
        int xrefOffset = source.Length;
        source.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f\n");
        foreach (int offset in offsets) source.Append($"{offset:0000000000} 00000 n\n");
        source.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(source.ToString());
    }

    /// <summary>A stream object body holding exact bytes.</summary>
    internal static string Stream(string dictionaryEntries, byte[] data) =>
        $"<< {dictionaryEntries} /Length {data.Length} >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream";

    /// <summary>A one-page PDF whose page paints content with an ICCBased RGB space named CS0.</summary>
    internal static byte[] IccRgbPage(byte[] profile, string content, int size = 1) => Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        $"<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {size} {size}] >>",
        "<< /Type /Page /Parent 2 0 R /Resources << /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >> /Contents 4 0 R >>",
        Stream("", Encoding.ASCII.GetBytes(content)),
        Stream("/N 3 /Alternate /DeviceRGB", profile));
}
