using System.Buffers.Binary;
using KillerPdf.Engine.Filters;

namespace KillerPdf.Engine.Documents;

internal static class PdfImageAssetJpegValidator
{
    private const int MaximumBytes = 5 * 1024 * 1024;

    internal static void Validate(ReadOnlyMemory<byte> encoded, int width, int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width <= 0 || height <= 0 || (long)width * height > MaximumBytes / 3
            || encoded.Length > MaximumBytes)
            throw new NotSupportedException("The JPEG exceeds the detached asset size limit.");
        ReadOnlySpan<byte> data = encoded.Span;
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
            throw new FormatException("The detached JPEG has no SOI marker.");
        int position = 2, restartInterval = 0, expectedRestarts = 0;
        bool frame = false, scan = false, complete = false;
        int maxHorizontal = 0, maxVertical = 0;
        while (position < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data[position++] != 0xff)
                throw new FormatException("The detached JPEG marker envelope is invalid.");
            while (position < data.Length && data[position] == 0xff)
            {
                if ((position & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                position++;
            }
            if (position >= data.Length) throw new FormatException("The detached JPEG marker is truncated.");
            byte marker = data[position++];
            if (marker == 0xd9)
            {
                if (!scan || position != data.Length)
                    throw new FormatException("The detached JPEG requires one complete scan and a final EOI marker.");
                complete = true;
                break;
            }
            if (marker is not (0xc0 or 0xc4 or 0xdb or 0xdd or 0xda))
                throw new NotSupportedException("Detached JPEG assets require baseline RGB without application metadata.");
            if (position + 2 > data.Length)
                throw new FormatException("The detached JPEG segment length is truncated.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || length > data.Length - position)
                throw new FormatException("The detached JPEG segment is truncated.");
            int payload = position + 2, end = position + length;
            if (marker == 0xc0)
            {
                if (frame || scan || length != 17 || data[payload] != 8 || data[payload + 5] != 3
                    || data[payload + 6] != (byte)'R' || data[payload + 9] != (byte)'G'
                    || data[payload + 12] != (byte)'B')
                    throw new NotSupportedException("Detached JPEG assets require one eight-bit direct RGB frame.");
                if (BinaryPrimitives.ReadUInt16BigEndian(data[(payload + 1)..]) != height
                    || BinaryPrimitives.ReadUInt16BigEndian(data[(payload + 3)..]) != width)
                    throw new FormatException("The JPEG frame dimensions do not match the PDF image.");
                for (int component = 0; component < 3; component++)
                {
                    int sampling = data[payload + 7 + component * 3];
                    int horizontal = sampling >> 4, vertical = sampling & 15;
                    if (horizontal is < 1 or > 4 || vertical is < 1 or > 4)
                        throw new FormatException("The detached JPEG sampling factors are invalid.");
                    maxHorizontal = Math.Max(maxHorizontal, horizontal);
                    maxVertical = Math.Max(maxVertical, vertical);
                }
                frame = true;
            }
            else if (marker == 0xdd)
            {
                if (length != 4 || scan)
                    throw new FormatException("The detached JPEG restart interval is invalid.");
                restartInterval = BinaryPrimitives.ReadUInt16BigEndian(data[payload..]);
            }
            else if (marker == 0xda)
            {
                if (!frame || scan || length != 12 || data[payload] != 3
                    || data[payload + 1] != (byte)'R' || data[payload + 3] != (byte)'G'
                    || data[payload + 5] != (byte)'B' || data[payload + 7] != 0
                    || data[payload + 8] != 63 || data[payload + 9] != 0)
                    throw new NotSupportedException("Detached JPEG assets require one full interleaved baseline scan.");
                scan = true;
                int mcus = ((width + maxHorizontal * 8 - 1) / (maxHorizontal * 8))
                    * ((height + maxVertical * 8 - 1) / (maxVertical * 8));
                expectedRestarts = restartInterval == 0 ? 0 : (mcus - 1) / restartInterval;
            }
            position = end;
            if (!scan) continue;
            int restarts = 0;
            while (position < data.Length)
            {
                if ((position & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (data[position] != 0xff) { position++; continue; }
                int markerStart = position++;
                while (position < data.Length && data[position] == 0xff)
                {
                    if ((position & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    position++;
                }
                if (position >= data.Length) throw new FormatException("The detached JPEG scan marker is truncated.");
                byte next = data[position];
                if (next == 0 && position == markerStart + 1) { position++; continue; }
                if (next is >= 0xd0 and <= 0xd7)
                {
                    if (restartInterval == 0 || next != 0xd0 + restarts % 8 || ++restarts > expectedRestarts)
                        throw new FormatException("The detached JPEG restart sequence is invalid.");
                    position++;
                    continue;
                }
                position = markerStart;
                break;
            }
            if (restarts != expectedRestarts)
                throw new FormatException("The detached JPEG restart sequence is incomplete.");
        }
        if (!complete) throw new FormatException("The detached JPEG is missing its final EOI marker.");
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = PdfJpegDecoder.DecodeImage(encoded, MaximumBytes, 1, compatibilityRecovery: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.Width != width || decoded.Height != height || decoded.SourceWidth != width
            || decoded.SourceHeight != height || decoded.Components != 3
            || decoded.Samples.Length != (long)width * height * 3)
            throw new FormatException("The decoded JPEG samples do not match the PDF image.");
    }
}
