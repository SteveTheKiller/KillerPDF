using System.Buffers.Binary;

namespace KillerPdf.Engine.Filters;

internal static partial class PdfJpeg2000Decoder
{
    internal static int ReadOpacityChannel(ReadOnlySpan<byte> source, int components)
    {
        if (source.Length >= 2 && source[0] == 0xFF && source[1] == 0x4F) return -1;
        if (!TryFindBox(source, 0x6A703268, out ReadOnlySpan<byte> header)
            || !TryFindBox(header, 0x63646566, out ReadOnlySpan<byte> definitions)) return -1;
        if (definitions.Length < 2)
            throw new PdfFilterException("JPEG 2000 channel definitions are truncated.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(definitions);
        if (definitions.Length != 2 + count * 6)
            throw new PdfFilterException("JPEG 2000 channel definitions have an invalid length.");
        int opacity = -1;
        var seen = new HashSet<int>();
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = definitions[(2 + index * 6)..];
            int channel = BinaryPrimitives.ReadUInt16BigEndian(entry);
            int type = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
            int association = BinaryPrimitives.ReadUInt16BigEndian(entry[4..]);
            if (channel >= components || !seen.Add(channel))
                throw new PdfFilterException("JPEG 2000 channel definitions are invalid.");
            if (type is not (1 or 2)) continue;
            if (opacity >= 0 || association != 0)
                throw new PdfFilterException("JPEG 2000 requires one global opacity channel.");
            opacity = channel;
        }
        return opacity;
    }

    private static bool TryFindBox(ReadOnlySpan<byte> source, uint wanted,
        out ReadOnlySpan<byte> payload)
    {
        while (!source.IsEmpty)
        {
            if (source.Length < 8)
                throw new PdfFilterException("JPEG 2000 data has a truncated box header.");
            ulong length = BinaryPrimitives.ReadUInt32BigEndian(source);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
            int headerLength = 8;
            if (length == 1)
            {
                if (source.Length < 16)
                    throw new PdfFilterException("JPEG 2000 data has a truncated extended box header.");
                length = BinaryPrimitives.ReadUInt64BigEndian(source[8..]);
                headerLength = 16;
            }
            else if (length == 0) length = (ulong)source.Length;
            if (length < (ulong)headerLength || length > (ulong)source.Length)
                throw new PdfFilterException("JPEG 2000 data has an invalid box length.");
            if (type == wanted)
            {
                payload = source.Slice(headerLength, (int)length - headerLength);
                return true;
            }
            source = source[(int)length..];
        }
        payload = default;
        return false;
    }
}
