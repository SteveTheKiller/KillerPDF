using System.Buffers.Binary;
using KillerPdf.Engine.Filters;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private static PngDecodedImage ReadNativeJpeg(ReadOnlyMemory<byte> bytes, long retainedBytes,
        CancellationToken cancellationToken)
    {
        var data = bytes.Span;
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
            throw new InvalidDataException("Native EPUB JPEG signature is invalid.");
        int width = 0, height = 0, components = 0, offset = 2;
        int? adobeTransform = null;
        bool jfif = false, jfifComponents = false, scan = false, entropy = false, ended = false;
        while (offset < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entropy && data[offset] != 0xff) { offset++; continue; }
            if (data[offset++] != 0xff)
                throw new InvalidDataException("Invalid native EPUB JPEG marker.");
            while (offset < data.Length && data[offset] == 0xff)
            {
                cancellationToken.ThrowIfCancellationRequested();
                offset++;
            }
            if (offset == data.Length) throw new InvalidDataException("Truncated native EPUB JPEG marker.");
            byte marker = data[offset++];
            if (entropy && (marker == 0 || marker is >= 0xd0 and <= 0xd7)) continue;
            entropy = false;
            if (marker == 0xd9) { ended = true; break; }
            if (marker is 0 or 0xd8 or >= 0xd0 and <= 0xd7)
                throw new InvalidDataException("Invalid native EPUB JPEG marker order.");
            if (data.Length - offset < 2) throw new InvalidDataException("Truncated native EPUB JPEG segment.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (length < 2 || length > data.Length - offset)
                throw new InvalidDataException("Truncated native EPUB JPEG segment.");
            var payload = data.Slice(offset + 2, length - 2);
            offset += length;
            switch (marker)
            {
                case 0xc0 or 0xc1 or 0xc2:
                    if (components != 0 || payload.Length < 6)
                        throw new InvalidDataException("Invalid native EPUB JPEG frame.");
                    components = payload[5];
                    if (payload[0] != 8 || components is not (1 or 3))
                        throw new NotSupportedException("Native EPUB JPEG requires eight-bit Gray or RGB color.");
                    if (payload.Length != 6 + 3 * components)
                        throw new InvalidDataException("Invalid native EPUB JPEG component table.");
                    jfifComponents = payload[6] == 1
                        && (components == 1 || payload[9] == 2 && payload[12] == 3);
                    height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                    if (width is <= 0 or > 32768 || height is <= 0 or > 32768
                        || (long)width * height > 1_048_576
                        || retainedBytes + (long)width * height * 12 + height * 2 > 32 * 1024 * 1024)
                        throw new InvalidDataException("Native EPUB images exceed their decoded storage limit.");
                    int maxHorizontal = 0, maxVertical = 0;
                    for (int component = 0; component < components; component++)
                    {
                        int sampling = payload[7 + component * 3];
                        int horizontalSampling = sampling >> 4, verticalSampling = sampling & 15;
                        if (horizontalSampling is < 1 or > 4 || verticalSampling is < 1 or > 4)
                            throw new InvalidDataException("Invalid native EPUB JPEG component sampling.");
                        maxHorizontal = Math.Max(maxHorizontal, horizontalSampling);
                        maxVertical = Math.Max(maxVertical, verticalSampling);
                    }
                    long mcuColumns = (width + maxHorizontal * 8 - 1) / (maxHorizontal * 8);
                    long mcuRows = (height + maxVertical * 8 - 1) / (maxVertical * 8);
                    long paddedSamples = 0;
                    for (int component = 0; component < components; component++)
                    {
                        int sampling = payload[7 + component * 3];
                        int horizontalSampling = sampling >> 4, verticalSampling = sampling & 15;
                        if (maxHorizontal % horizontalSampling != 0 || maxVertical % verticalSampling != 0)
                            throw new InvalidDataException("Incompatible native EPUB JPEG component sampling.");
                        paddedSamples += mcuColumns * horizontalSampling * mcuRows * verticalSampling * 64;
                    }
                    // Bound full padded planes, progressive coefficients, decoded output and Gray expansion.
                    // A fixed reserve covers tables and other small decoder arrays.
                    long peakBytes = retainedBytes + paddedSamples * (marker == 0xc2 ? 5 : 1)
                        + (long)width * height * (components == 1 ? 4 : 3) + 65536;
                    if (peakBytes > 32 * 1024 * 1024)
                        throw new InvalidDataException("Native EPUB images exceed their decoded storage limit.");
                    break;
                case 0xda:
                    if (components == 0 || payload.Length < 4 || payload[0] == 0
                        || payload[0] > components || payload.Length != 4 + 2 * payload[0])
                        throw new InvalidDataException("Invalid native EPUB JPEG scan header.");
                    scan = entropy = true;
                    break;
                case 0xe0:
                    if (scan || jfif || payload.Length < 14 || !payload[..5].SequenceEqual("JFIF\0"u8)
                        || payload[5] != 1 || payload[6] > 2 || payload[7] > 2
                        || payload.Length != 14 + 3 * payload[12] * payload[13])
                        throw new NotSupportedException("Native EPUB JPEG JFIF metadata is unsupported or malformed.");
                    jfif = true;
                    if (payload[12] != 0 || payload[13] != 0)
                        throw new NotSupportedException("Native EPUB JPEG JFIF thumbnails are not implemented.");
                    int horizontal = BinaryPrimitives.ReadUInt16BigEndian(payload[8..]);
                    int vertical = BinaryPrimitives.ReadUInt16BigEndian(payload[10..]);
                    if (horizontal == 0 || horizontal != vertical)
                        throw new NotSupportedException("Native EPUB JPEG JFIF density requires a defined square pixel aspect ratio.");
                    break;
                case 0xee:
                    if (scan || adobeTransform is not null || payload.Length != 12
                        || !payload[..5].SequenceEqual("Adobe"u8)
                        || BinaryPrimitives.ReadUInt16BigEndian(payload[5..]) != 100
                        || BinaryPrimitives.ReadUInt16BigEndian(payload[7..]) != 0
                        || BinaryPrimitives.ReadUInt16BigEndian(payload[9..]) != 0 || payload[11] > 1)
                        throw new NotSupportedException("Native EPUB JPEG Adobe color metadata is not implemented for this image.");
                    adobeTransform = payload[11];
                    break;
                case 0xe1:
                    throw new NotSupportedException("Native EPUB JPEG EXIF and orientation metadata are not implemented.");
                case 0xe2:
                    throw new NotSupportedException("Native EPUB JPEG ICC color metadata is not implemented.");
                case 0xc4 or 0xdb or 0xdd:
                    break;
                default:
                    throw new NotSupportedException("Native EPUB JPEG marker or additional metadata is not implemented.");
            }
        }
        if (!ended || !scan || components == 0 || offset != data.Length)
            throw new InvalidDataException("Native EPUB JPEG requires complete image data and an EOI marker.");
        if (adobeTransform == 1 && components != 3
            || jfif && (!jfifComponents || adobeTransform == 0 && components == 3))
            throw new NotSupportedException("Native EPUB JPEG color metadata is inconsistent with its components.");
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = PdfJpegDecoder.DecodeImage(bytes, 4 * 1024 * 1024, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.Width != width || decoded.Height != height || decoded.Components != components)
            throw new InvalidDataException("Native EPUB JPEG decoded dimensions or components differ from its frame.");
        byte[] rgb = decoded.Samples;
        if (components == 1)
        {
            rgb = new byte[checked(width * height * 3)];
            for (int index = 0; index < decoded.Samples.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                rgb[index * 3] = rgb[index * 3 + 1] = rgb[index * 3 + 2] = decoded.Samples[index];
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new PngDecodedImage(rgb, null, width, height, 3, 8);
    }
}
