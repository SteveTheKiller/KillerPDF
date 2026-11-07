using System.Buffers.Binary;
using KillerPdf.Engine.Filters;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private PngDecodedImage ReadNativeImage(string chapter, string reference,
        Dictionary<string, PngDecodedImage> cache, ref long retainedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target;
        try { target = NavigationTarget(chapter, reference.Trim(' ', '\t', '\r', '\n')); }
        catch (InvalidDataException exception)
        {
            throw new NotSupportedException("Native EPUB images require a packaged source.", exception);
        }
        if (target.Contains('#') || !Manifest.Any(item => item.Href == target
            && item.MediaType is "image/png" or "image/jpeg"
            && Tokens(item.Properties).All(property => property == "cover-image")))
            throw new NotSupportedException("Native EPUB images require a packaged PNG or JPEG manifest resource.");
        if (cache.TryGetValue(target, out var cached)) return cached;
        ReadOnlyMemory<byte> bytes = _resources[target];
        if (Manifest.Any(item => item.Href == target && item.MediaType == "image/jpeg"))
        {
            var jpeg = ReadNativeJpeg(bytes, retainedBytes, cancellationToken);
            retainedBytes += jpeg.Samples.LongLength;
            cache.Add(target, jpeg);
            return jpeg;
        }
        var data = bytes.Span;
        if (data.Length < 33 || !PdfPngDecoder.HasSignature(data)
            || BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13
            || !data.Slice(12, 4).SequenceEqual("IHDR"u8)
            || data[24] != 8 || data[25] is not (2 or 6) || data[26] != 0 || data[27] != 0 || data[28] != 0)
            throw new NotSupportedException("Native EPUB PNG rendering currently requires noninterlaced eight-bit RGB or RGBA.");
        long width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        long height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        if (width is <= 0 or > 32768 || height is <= 0 or > 32768
            || width * height > 1_048_576 || retainedBytes + width * height * 12 + height * 2 > 32 * 1024 * 1024)
            throw new InvalidDataException("Native EPUB images exceed their decoded storage limit.");
        int seenMetadata = 0;
        bool imageDataStarted = false;
        for (int offset = 8; offset < data.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length - offset < 12) throw new InvalidDataException("Truncated native EPUB PNG chunk.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (length > data.Length - offset - 12) throw new InvalidDataException("Truncated native EPUB PNG chunk.");
            var type = data.Slice(offset + 4, 4);
            var payload = data.Slice(offset + 8, (int)length);
            if (type.SequenceEqual("sRGB"u8) && (length != 1 || payload[0] > 3)
                || type.SequenceEqual("gAMA"u8) && (length != 4 || BinaryPrimitives.ReadUInt32BigEndian(payload) == 0)
                || type.SequenceEqual("pHYs"u8) && (length != 9 || payload[8] > 1))
                throw new InvalidDataException("Invalid native EPUB PNG image metadata.");
            int metadata = type.SequenceEqual("sRGB"u8) ? 1 : type.SequenceEqual("gAMA"u8) ? 2
                : type.SequenceEqual("pHYs"u8) ? 4 : 0;
            if (metadata != 0)
            {
                if (imageDataStarted || (seenMetadata & metadata) != 0)
                    throw new InvalidDataException("Native EPUB PNG image metadata must appear once before image data.");
                uint crc = uint.MaxValue;
                foreach (byte value in data.Slice(offset + 4, (int)length + 4))
                {
                    crc ^= value;
                    for (int bit = 0; bit < 8; bit++)
                        crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
                }
                if (~crc != BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 8 + (int)length)..]))
                    throw new InvalidDataException("Native EPUB PNG image metadata checksum is invalid.");
                seenMetadata |= metadata;
                if (metadata == 4 && (BinaryPrimitives.ReadUInt32BigEndian(payload) == 0
                    || BinaryPrimitives.ReadUInt32BigEndian(payload) != BinaryPrimitives.ReadUInt32BigEndian(payload[4..])))
                    throw new NotSupportedException("Native EPUB PNG pHYs rendering requires a defined square pixel aspect ratio.");
            }
            if (type.SequenceEqual("IDAT"u8)) imageDataStarted = true;
            if (!type.SequenceEqual("IHDR"u8) && !type.SequenceEqual("IDAT"u8) && !type.SequenceEqual("IEND"u8)
                && !type.SequenceEqual("sRGB"u8) && !type.SequenceEqual("gAMA"u8) && !type.SequenceEqual("pHYs"u8))
                throw new NotSupportedException("Native EPUB PNG metadata and additional chunk types are not implemented.");
            offset = checked(offset + (int)length + 12);
        }
        if ((seenMetadata & 2) != 0 && (seenMetadata & 1) == 0)
            throw new NotSupportedException("Native EPUB PNG gAMA rendering without authoritative sRGB metadata is not implemented.");
        var image = PdfPngDecoder.Decode(bytes, 4 * 1024 * 1024);
        cancellationToken.ThrowIfCancellationRequested();
        retainedBytes += image.Samples.LongLength + (image.Alpha?.LongLength ?? 0);
        cache.Add(target, image);
        return image;
    }
}
