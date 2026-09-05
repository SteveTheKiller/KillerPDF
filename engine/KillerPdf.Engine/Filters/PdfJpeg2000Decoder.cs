using CoreJ2K;
using CoreJ2K.Configuration;
using CoreJ2K.Util;
using System.Buffers.Binary;

namespace KillerPdf.Engine.Filters;

internal static class PdfJpeg2000Decoder
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
            (int expectedWidth, int expectedHeight, int expectedComponents, int expectedBits) =
                (shape.Width, shape.Height, shape.Components, shape.Bits);
            if (resolutionLevel < -1 || resolutionLevel >= shape.ResolutionLevels)
                throw new PdfFilterException("JPEG 2000 resolution level is invalid.");
            int selectedLevel = resolutionLevel < 0
                ? shape.ResolutionLevels - 1 : resolutionLevel;
            int reduction = shape.ResolutionLevels - 1 - selectedLevel;
            int decodedWidth = ReducedDimension(shape.XSize, shape.XOrigin, reduction);
            int decodedHeight = ReducedDimension(shape.YSize, shape.YOrigin, reduction);
            int expectedRowBytes = checked(
                (decodedWidth * expectedComponents * expectedBits + 7) / 8);
            int expectedLength = checked(expectedRowBytes * decodedHeight);
            if (expectedLength > maximumDecodedBytes)
                throw new PdfFilterException("Decoded stream exceeds the configured safety limit.");
            long temporaryBytes = checked(
                (long)decodedWidth * decodedHeight * expectedComponents * sizeof(int));
            if (temporaryBytes > MaximumTemporarySampleBytes)
                throw new PdfFilterException("JPEG 2000 temporary samples exceed the configured safety limit.");

            var configuration = new J2KDecoderConfiguration
            {
                UseColorSpace = false,
                Verbose = false,
                ResolutionLevel = resolutionLevel
            };
            using InterleavedImage image = J2kImage.FromBytes(source, configuration);
            int components = image.NumberOfComponents;
            if (image.Width != decodedWidth || image.Height != decodedHeight
                || components != expectedComponents)
                throw new PdfFilterException("JPEG 2000 decoded dimensions do not match its codestream header.");
            if (image.BitDepths.Count != components || image.BitDepths.Any(bits => bits is < 1 or > 16)
                || image.BitDepths.Any(bits => bits != image.BitDepths[0]))
                throw new PdfFilterException("JPEG 2000 components must use one supported sample depth.");

            int bits = image.BitDepths[0];
            int rowBytes = checked((image.Width * components * bits + 7) / 8);
            int length = checked(rowBytes * image.Height);
            if (length > maximumDecodedBytes)
                throw new PdfFilterException("Decoded stream exceeds the configured safety limit.");

            int[] values = image.GetDataCopy();
            if (bits == 8)
            {
                var bytes = new byte[length];
                for (int index = 0; index < values.Length; index++)
                    bytes[index] = checked((byte)values[index]);
                return new Jpeg2000DecodedImage(
                    bytes, image.Width, image.Height, components, bits);
            }

            var packed = new byte[length];
            int maximum = (1 << bits) - 1;
            int samplesPerRow = image.Width * components;
            for (int row = 0; row < image.Height; row++)
            {
                int bitOffset = row * rowBytes * 8;
                int valueOffset = row * samplesPerRow;
                for (int sample = 0; sample < samplesPerRow; sample++)
                {
                    int value = values[valueOffset + sample];
                    if ((uint)value > maximum)
                        throw new PdfFilterException("JPEG 2000 data contains an out-of-range sample.");
                    for (int bit = bits - 1; bit >= 0; bit--, bitOffset++)
                        if ((value & (1 << bit)) != 0)
                            packed[bitOffset / 8] |= (byte)(1 << (7 - bitOffset % 8));
                }
            }
            return new Jpeg2000DecodedImage(
                packed, image.Width, image.Height, components, bits);
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
