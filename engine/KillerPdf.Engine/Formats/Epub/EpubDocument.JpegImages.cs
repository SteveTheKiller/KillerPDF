using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Filters;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    /// <summary>
    /// Decodes a baseline or progressive JPEG at the smallest DCT reduction that still covers the
    /// target box. Comments, JFIF, XMP, Photoshop and other application segments are ignored;
    /// EXIF orientation is applied; an embedded profile other than sRGB refuses only this image.
    /// </summary>
    private static PngDecodedImage ReadNativeJpeg(ReadOnlyMemory<byte> bytes, int maximumWidth, int maximumHeight,
        CancellationToken cancellationToken)
    {
        var data = bytes.Span;
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
            throw new InvalidDataException("Native EPUB JPEG signature is invalid.");
        int width = 0, height = 0, components = 0, offset = 2, orientation = 1;
        var icc = new List<byte>();
        bool scan = false, iccSeen = false;
        while (offset < data.Length && !scan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data[offset++] != 0xff) throw new InvalidDataException("Invalid native EPUB JPEG marker.");
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset == data.Length) throw new InvalidDataException("Truncated native EPUB JPEG marker.");
            byte marker = data[offset++];
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (marker is 0 or 0xd8 or 0xd9) throw new InvalidDataException("Invalid native EPUB JPEG marker order.");
            if (data.Length - offset < 2) throw new InvalidDataException("Truncated native EPUB JPEG segment.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (length < 2 || length > data.Length - offset) throw new InvalidDataException("Truncated native EPUB JPEG segment.");
            var payload = data.Slice(offset + 2, length - 2);
            offset += length;
            switch (marker)
            {
                case 0xc0 or 0xc1 or 0xc2:
                    if (components != 0 || payload.Length < 6) throw new InvalidDataException("Invalid native EPUB JPEG frame.");
                    components = payload[5];
                    if (payload[0] != 8 || components is not (1 or 3))
                        throw new EpubImageUnavailableException("Native EPUB JPEG requires eight-bit Gray or RGB color.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(payload[3..]);
                    if (width <= 0 || height <= 0 || (long)width * height > MaximumSourcePixels)
                        throw new EpubImageUnavailableException("Native EPUB JPEG exceeds the decodable size.", EpubOmissionReason.ImageTooLarge);
                    break;
                case >= 0xc3 and <= 0xcf when marker is not (0xc4 or 0xc8 or 0xcc):
                    throw new EpubImageUnavailableException("Native EPUB JPEG coding process is not implemented.");
                case 0xda:
                    if (components == 0) throw new InvalidDataException("Invalid native EPUB JPEG scan header.");
                    scan = true;
                    break;
                case 0xe1 when payload.Length >= 14 && payload[..6].SequenceEqual("Exif\0\0"u8):
                    orientation = ExifOrientation(payload[6..]);
                    break;
                case 0xe2 when payload.Length >= 12 && payload[..12].SequenceEqual("ICC_PROFILE\0"u8):
                    iccSeen = true;
                    if (payload.Length > 14) icc.AddRange(payload[14..].ToArray());
                    break;
                default:
                    // DHT, DQT, DRI, COM, JFIF and other application segments carry no rendering change here;
                    // the decoder reads the Adobe transform flag itself.
                    break;
            }
        }
        if (!scan || components == 0) throw new InvalidDataException("Native EPUB JPEG requires image data.");
        if (iccSeen && !IsSrgbProfile(icc))
            throw new EpubImageUnavailableException("Native EPUB JPEG embedded color profiles other than sRGB are not implemented.",
                EpubOmissionReason.ImageColorProfile);
        bool quarterTurn = orientation is >= 5 and <= 8;
        int targetWidth = quarterTurn ? maximumHeight : maximumWidth, targetHeight = quarterTurn ? maximumWidth : maximumHeight;
        int reduction = 1;
        while (reduction < 8 && (width + 2 * reduction - 1) / (2 * reduction) >= targetWidth
            && (height + 2 * reduction - 1) / (2 * reduction) >= targetHeight) reduction *= 2;
        var decoded = PdfJpegDecoder.DecodeImage(bytes, MaximumDecodeBytes, reduction);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.Components != components)
            throw new InvalidDataException("Native EPUB JPEG decoded components differ from its frame.");
        var image = new PngDecodedImage(decoded.Samples, null, decoded.Width, decoded.Height, components, 8);
        return orientation == 1 ? image : Orient(ToRgb(image), orientation, cancellationToken);
    }

    // TIFF tag 0x0112 in IFD0; any malformed structure leaves the default upright orientation.
    private static int ExifOrientation(ReadOnlySpan<byte> span)
    {
        byte[] tiff = span.ToArray();
        if (tiff.Length < 8) return 1;
        bool little = tiff[0] == 'I' && tiff[1] == 'I';
        if (!little && !(tiff[0] == 'M' && tiff[1] == 'M')) return 1;
        int U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(at));
        long ifd = little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(4));
        if (ifd < 8 || ifd + 2 > tiff.Length) return 1;
        int count = U16((int)ifd);
        for (int entry = 0; entry < count; entry++)
        {
            int at = (int)ifd + 2 + entry * 12;
            if (at + 12 > tiff.Length) return 1;
            if (U16(at) == 0x0112) return U16(at + 8) is var value and >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }

    // An ICC profile is honored here only when its description names sRGB, since the reader renders in sRGB.
    private static bool IsSrgbProfile(List<byte> profile) => IsSrgbProfile(profile.ToArray());

    private static bool IsSrgbProfile(byte[] profile)
    {
        if (profile.Length < 132) return false;
        string text = Encoding.Latin1.GetString(profile);
        return text.Contains("sRGB", StringComparison.OrdinalIgnoreCase)
            || Encoding.BigEndianUnicode.GetString(profile).Contains("sRGB", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies an EXIF orientation (TIFF 6.0) to RGB samples so the image displays upright.</summary>
    private static PngDecodedImage Orient(PngDecodedImage image, int orientation, CancellationToken cancellationToken)
    {
        int width = image.Width, height = image.Height;
        bool swap = orientation >= 5;
        int outWidth = swap ? height : width, outHeight = swap ? width : height;
        byte[] output = new byte[image.Samples.Length];
        for (int y = 0; y < outHeight; y++)
        {
            if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < outWidth; x++)
            {
                (int sx, int sy) = orientation switch
                {
                    2 => (width - 1 - x, y),
                    3 => (width - 1 - x, height - 1 - y),
                    4 => (x, height - 1 - y),
                    5 => (y, x),
                    6 => (y, height - 1 - x),
                    7 => (width - 1 - y, height - 1 - x),
                    8 => (width - 1 - y, x),
                    _ => (x, y)
                };
                int source = (sy * width + sx) * 3, target = (y * outWidth + x) * 3;
                output[target] = image.Samples[source];
                output[target + 1] = image.Samples[source + 1];
                output[target + 2] = image.Samples[source + 2];
            }
        }
        return new PngDecodedImage(output, null, outWidth, outHeight, 3, 8);
    }
}
