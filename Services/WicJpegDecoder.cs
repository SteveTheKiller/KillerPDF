using KillerPdf.Engine.Filters;

namespace KillerPDF.Services;

internal sealed class WicJpegDecoder : IPdfJpegDecoder
{
    internal static readonly WicJpegDecoder Instance = new();

    private WicJpegDecoder() { }

    public bool TryDecode(ReadOnlyMemory<byte> encoded, int maximumDecodedBytes, int reduction,
        int? colorTransform, out JpegDecodedImage image)
    {
        image = default;
        if (reduction is not (1 or 2 or 4 or 8) || encoded.Length < 16_384
            || !TryReadFrame(encoded.Span, out int width, out int height, out int components)
            || (components == 4 ? encoded.Length < 300_000 || colorTransform is not null and not 2
                : (long)width * height < 1_048_576 || colorTransform is not null and not 1))
            return false;

        int reducedWidth = (width + reduction - 1) / reduction;
        int reducedHeight = (height + reduction - 1) / reduction;
        long sampleLength = (long)reducedWidth * reducedHeight * components;
        if (sampleLength > maximumDecodedBytes || sampleLength > int.MaxValue)
            return false;

        try
        {
            byte[] samples;
            bool decoded = components == 4
                ? NativeWicJpegDecoder.TryDecode(encoded, reducedWidth, reducedHeight, out samples)
                : NativeWicJpegDecoder.TryDecodeRgb(encoded, reducedWidth, reducedHeight, out samples);
            if (!decoded) return false;
            if (components == 4)
            {
                for (int index = 0; index < samples.Length; index++)
                    samples[index] = (byte)(255 - samples[index]);
            }
            else
            {
                for (int index = 0; index < samples.Length; index += 3)
                    (samples[index], samples[index + 2]) = (samples[index + 2], samples[index]);
            }
            image = new JpegDecodedImage(samples, reducedWidth, reducedHeight,
                components, width, height);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool TryReadFrame(ReadOnlySpan<byte> encoded, out int width, out int height, out int components)
    {
        width = height = components = 0;
        if (encoded.Length < 4 || encoded[0] != 0xFF || encoded[1] != 0xD8)
            return false;

        int? adobeTransform = null;
        bool jfif = false;
        bool hasScan = false;
        int position = 2;
        while (position + 4 <= encoded.Length)
        {
            if (encoded[position++] != 0xFF) return false;
            while (position < encoded.Length && encoded[position] == 0xFF) position++;
            if (position >= encoded.Length) return false;
            byte marker = encoded[position++];
            if (marker == 0xDA)
            {
                hasScan = true;
                break;
            }
            if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7
                || position + 2 > encoded.Length)
                return false;
            int length = encoded[position] << 8 | encoded[position + 1];
            if (length < 2 || length > encoded.Length - position) return false;
            int payload = position + 2;
            int end = position + length;
            if (marker == 0xEE && end - payload >= 12
                && encoded.Slice(payload, 5).SequenceEqual("Adobe"u8))
                adobeTransform = encoded[payload + 11];
            if (marker == 0xE0 && end - payload >= 5
                && encoded.Slice(payload, 5).SequenceEqual("JFIF\0"u8)) jfif = true;
            if (marker is 0xC0 or 0xC1)
            {
                if (end - payload < 6 || encoded[payload] != 8
                    || encoded[payload + 5] is not (3 or 4))
                    return false;
                components = encoded[payload + 5];
                height = encoded[payload + 1] << 8 | encoded[payload + 2];
                width = encoded[payload + 3] << 8 | encoded[payload + 4];
            }
            else if (marker is 0xC2 or 0xC3 or >= 0xC5 and <= 0xCF)
                return false;
            position = end;
        }
        return hasScan && width > 0 && height > 0 && (components == 4
            ? adobeTransform == 2 : adobeTransform == 1 || (adobeTransform is null && jfif));
    }
}
