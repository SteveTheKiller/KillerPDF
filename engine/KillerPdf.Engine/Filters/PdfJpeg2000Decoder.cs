using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace KillerPdf.Engine.Filters;

internal static partial class PdfJpeg2000Decoder
{
    private const long MaximumTemporarySampleBytes = 256L * 1024 * 1024;

    internal static byte[] Decode(ReadOnlyMemory<byte> source, int maximumDecodedBytes)
        => DecodeImage(source, maximumDecodedBytes, -1).Samples;

    internal static Jpeg2000DecodedImage DecodeImage(
        ReadOnlyMemory<byte> source, int maximumDecodedBytes, int resolutionLevel)
    {
        try
        {
            Jpeg2000Shape shape = ReadShape(source.Span);
            if (resolutionLevel < -1 || resolutionLevel >= shape.ResolutionLevels)
                throw new PdfFilterException("JPEG 2000 resolution level is invalid.");
            int selectedLevel = resolutionLevel < 0
                ? shape.ResolutionLevels - 1 : resolutionLevel;
            int reduction = shape.ResolutionLevels - 1 - selectedLevel;
            int width = ReducedDimension(shape.XSize, shape.XOrigin, reduction);
            int height = ReducedDimension(shape.YSize, shape.YOrigin, reduction);
            int rowBytes = checked((width * shape.Components * shape.Bits + 7) / 8);
            int length = checked(rowBytes * height);
            if (length > maximumDecodedBytes)
                throw new PdfFilterException("Decoded stream exceeds the configured safety limit.");
            return DecodePixels(source, shape, resolutionLevel, width, height, rowBytes, length);
        }
        catch (PdfFilterException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PdfFilterException("JPEG 2000 data is malformed or unsupported.", ex);
        }
    }

    private static MemoryStream CreateReadStream(ReadOnlyMemory<byte> source)
    {
        if (MemoryMarshal.TryGetArray(source, out ArraySegment<byte> segment)
            && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count,
                writable: false, publiclyVisible: true);
        }
        return new MemoryStream(source.ToArray(), writable: false);
    }

    internal static int ReducedDimension(uint size, uint origin, int reduction)
    {
        long divisor = 1L << Math.Min(reduction, 32);
        long reducedSize = ((long)size + divisor - 1) / divisor;
        long reducedOrigin = ((long)origin + divisor - 1) / divisor;
        return checked((int)Math.Max(1, reducedSize - reducedOrigin));
    }

    internal static Jpeg2000Shape ReadShape(
        ReadOnlySpan<byte> source)
    {
        ReadOnlySpan<byte> codestream = LocateCodestream(source);
        if (codestream.Length < 42 || codestream[0] != 0xFF || codestream[1] != 0x4F
            || codestream[2] != 0xFF || codestream[3] != 0x51)
            throw new PdfFilterException("JPEG 2000 data has no valid codestream size header.");
        int markerLength = BinaryPrimitives.ReadUInt16BigEndian(codestream[4..]);
        if (markerLength < 41 || markerLength + 4 > codestream.Length)
            throw new PdfFilterException("JPEG 2000 data has a truncated codestream size header.");
        uint xSize = BinaryPrimitives.ReadUInt32BigEndian(codestream[8..]);
        uint ySize = BinaryPrimitives.ReadUInt32BigEndian(codestream[12..]);
        uint xOrigin = BinaryPrimitives.ReadUInt32BigEndian(codestream[16..]);
        uint yOrigin = BinaryPrimitives.ReadUInt32BigEndian(codestream[20..]);
        int components = BinaryPrimitives.ReadUInt16BigEndian(codestream[40..]);
        if (xSize <= xOrigin || ySize <= yOrigin || components is < 1 or > 16
            || markerLength < 38 + components * 3)
            throw new PdfFilterException("JPEG 2000 data has invalid codestream dimensions.");
        int bits = (codestream[42] & 0x7F) + 1;
        for (int component = 1; component < components; component++)
            if ((codestream[42 + component * 3] & 0x7F) + 1 != bits)
                throw new PdfFilterException("JPEG 2000 components must use one supported sample depth.");
        if (bits is < 1 or > 16 || xSize - xOrigin > int.MaxValue || ySize - yOrigin > int.MaxValue)
            throw new PdfFilterException("JPEG 2000 data has unsupported codestream dimensions.");
        return new Jpeg2000Shape(
            (int)(xSize - xOrigin), (int)(ySize - yOrigin), components, bits,
            ReadResolutionLevels(codestream, markerLength + 4),
            xSize, ySize, xOrigin, yOrigin);
    }

    private static int ReadResolutionLevels(ReadOnlySpan<byte> codestream, int offset)
    {
        while (offset <= codestream.Length - 2)
        {
            if (codestream[offset] != 0xFF) { offset++; continue; }
            byte marker = codestream[offset + 1];
            if (marker is 0x90 or 0x93 or 0xD9) break;
            if (marker == 0x52)
            {
                if (offset > codestream.Length - 10)
                    throw new PdfFilterException("JPEG 2000 data has a truncated coding-style header.");
                int length = BinaryPrimitives.ReadUInt16BigEndian(codestream[(offset + 2)..]);
                if (length < 10 || length > codestream.Length - offset - 2)
                    throw new PdfFilterException("JPEG 2000 data has an invalid coding-style header.");
                return checked(codestream[offset + 9] + 1);
            }
            if (marker is 0x4F or 0x92) { offset += 2; continue; }
            if (offset > codestream.Length - 4)
                throw new PdfFilterException("JPEG 2000 data has a truncated marker segment.");
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(codestream[(offset + 2)..]);
            if (segmentLength < 2 || segmentLength > codestream.Length - offset - 2)
                throw new PdfFilterException("JPEG 2000 data has an invalid marker segment.");
            offset += checked(segmentLength + 2);
        }
        throw new PdfFilterException("JPEG 2000 data has no coding-style header.");
    }

    private static ReadOnlySpan<byte> LocateCodestream(ReadOnlySpan<byte> source)
    {
        if (source.Length >= 2 && source[0] == 0xFF && source[1] == 0x4F)
            return source;
        int offset = 0;
        while (offset <= source.Length - 8)
        {
            uint boxLength = BinaryPrimitives.ReadUInt32BigEndian(source[offset..]);
            uint boxType = BinaryPrimitives.ReadUInt32BigEndian(source[(offset + 4)..]);
            int headerLength = 8;
            long length = boxLength;
            if (boxLength == 1)
            {
                if (offset > source.Length - 16)
                    throw new PdfFilterException("JPEG 2000 data has a truncated extended box header.");
                ulong extended = BinaryPrimitives.ReadUInt64BigEndian(source[(offset + 8)..]);
                if (extended > int.MaxValue) throw new PdfFilterException("JPEG 2000 box is too large.");
                length = (long)extended;
                headerLength = 16;
            }
            else if (boxLength == 0)
                length = source.Length - offset;
            if (length < headerLength || length > source.Length - offset)
                throw new PdfFilterException("JPEG 2000 data has an invalid box length.");
            if (boxType == 0x6A703263)
                return source.Slice(offset + headerLength, (int)length - headerLength);
            offset += (int)length;
        }
        throw new PdfFilterException("JPEG 2000 data has no codestream box.");
    }

}

internal readonly record struct Jpeg2000Shape(
    int Width, int Height, int Components, int Bits, int ResolutionLevels,
    uint XSize, uint YSize, uint XOrigin, uint YOrigin);

internal readonly record struct Jpeg2000DecodedImage(
    byte[] Samples, int Width, int Height, int Components, int Bits);
