using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Filters;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>A single image the native reader cannot show; the image is omitted and the chapter continues.</summary>
internal sealed class EpubImageUnavailableException(string message,
    EpubOmissionReason reason = EpubOmissionReason.UnsupportedImageFormat, Exception? inner = null) : NotSupportedException(message, inner)
{
    internal EpubOmissionReason Reason { get; } = reason;
}

public sealed partial class EpubDocument
{
    // Decoded images stay resident in their pages, so the book shares one budget. Images are
    // downsampled to twice the content box before they count, so a novel's illustrations fit.
    private const long RetainedImageBudget = 256L << 20;
    // A single decode may briefly hold the full-size image; this bounds that transient allocation.
    private const int MaximumDecodeBytes = 96 * 1024 * 1024;
    private const long MaximumSourcePixels = 40_000_000;

    private PngDecodedImage ReadNativeImage(string chapter, string reference,
        Dictionary<string, PngDecodedImage> cache, ref long retainedBytes, (double Width, double Height) box,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target;
        try { target = NavigationTarget(chapter, reference.Trim(' ', '\t', '\r', '\n')); }
        catch (InvalidDataException exception)
        {
            throw new EpubImageUnavailableException("Native EPUB image is not packaged: " + reference + ".",
                EpubOmissionReason.MissingResource, exception);
        }
        if (cache.TryGetValue(target, out var cached)) return cached;
        var item = Manifest.FirstOrDefault(entry => entry.Href == target);
        if (target.Contains('#') || item?.MediaType is not ("image/png" or "image/jpeg" or "image/jpg"))
            throw new EpubImageUnavailableException("Native EPUB image format is not supported: " + target
                + (item is null ? "." : " (" + item.MediaType + ")."));
        if (_encryptedResources.Contains(target))
            throw new EpubImageUnavailableException("Native EPUB image is encrypted: " + target + ".", EpubOmissionReason.Encrypted);
        int maximumWidth = (int)Math.Clamp(Math.Ceiling(2 * box.Width), 1, 32768);
        int maximumHeight = (int)Math.Clamp(Math.Ceiling(2 * box.Height), 1, 32768);
        PngDecodedImage image;
        try
        {
            ReadOnlyMemory<byte> bytes = _resources[target];
            image = bytes.Span.Length >= 2 && bytes.Span[0] == 0xff && bytes.Span[1] == 0xd8
                ? ReadNativeJpeg(bytes, maximumWidth, maximumHeight, cancellationToken)
                : ReadNativePng(bytes, cancellationToken);
            image = FitSamples(ToRgb(image), maximumWidth, maximumHeight, cancellationToken);
        }
        catch (EpubImageUnavailableException) { throw; }
        catch (Exception error) when (error is PdfFilterException or InvalidDataException or NotSupportedException)
        {
            throw new EpubImageUnavailableException("Native EPUB image could not be decoded: " + target + ". " + error.Message,
                EpubOmissionReason.ImageUnreadable, error);
        }
        long size = image.Samples.LongLength + (image.Alpha?.LongLength ?? 0);
        if (retainedBytes + size > RetainedImageBudget)
            throw new EpubImageUnavailableException("Native EPUB images exceed the book's decoded image budget at " + target + ".",
                EpubOmissionReason.ImageTooLarge);
        retainedBytes += size;
        cache.Add(target, image);
        return image;
    }

    private static PngDecodedImage ReadNativePng(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var data = bytes.Span;
        if (data.Length < 33 || !PdfPngDecoder.HasSignature(data)
            || BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new EpubImageUnavailableException("Native EPUB PNG header is invalid.", EpubOmissionReason.ImageUnreadable);
        long width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        long height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        if (width is <= 0 or > 32768 || height is <= 0 or > 32768 || width * height > MaximumSourcePixels)
            throw new EpubImageUnavailableException("Native EPUB PNG exceeds the decodable size.", EpubOmissionReason.ImageTooLarge);
        if (data[28] != 0)
            throw new EpubImageUnavailableException("Native EPUB interlaced PNG images are not implemented.");
        for (int offset = 8; offset < data.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length - offset < 12) throw new InvalidDataException("Truncated native EPUB PNG chunk.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (length > data.Length - offset - 12) throw new InvalidDataException("Truncated native EPUB PNG chunk.");
            var type = data.Slice(offset + 4, 4);
            // A color profile that is not sRGB cannot be honored without color management, so that image is refused.
            if (type.SequenceEqual("iCCP"u8))
            {
                var payload = data.Slice(offset + 8, (int)length);
                int nul = payload.IndexOf((byte)0);
                string name = Encoding.Latin1.GetString(nul < 0 ? payload : payload[..nul]);
                if (!name.Contains("sRGB", StringComparison.OrdinalIgnoreCase))
                    throw new EpubImageUnavailableException("Native EPUB PNG embedded color profiles (iCCP) other than sRGB are not implemented.",
                        EpubOmissionReason.ImageColorProfile);
            }
            // Coding-independent code points select another transfer or primaries, which an sRGB reader cannot honor.
            if (type.SequenceEqual("cICP"u8))
                throw new EpubImageUnavailableException("Native EPUB PNG cICP color signaling is not implemented.",
                    EpubOmissionReason.ImageColorProfile);
            // Critical chunks other than the four the decoder understands change meaning; ancillary ones (lowercase first letter) are ignored.
            if ((type[0] & 0x20) == 0 && !type.SequenceEqual("IHDR"u8) && !type.SequenceEqual("PLTE"u8)
                && !type.SequenceEqual("IDAT"u8) && !type.SequenceEqual("IEND"u8))
                throw new EpubImageUnavailableException("Native EPUB PNG critical chunk is not implemented.");
            offset = checked(offset + (int)length + 12);
        }
        var image = PdfPngDecoder.Decode(bytes, MaximumDecodeBytes);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }

    // Keeps a whole-pixel rectangle of a decoded RGB image (and its alpha), for SVG slice and clipping.
    private static PngDecodedImage Crop(PngDecodedImage image, int left, int top, int width, int height)
    {
        if (left == 0 && top == 0 && width == image.Width && height == image.Height) return image;
        int components = image.Components;
        byte[] samples = new byte[checked(width * height * components)];
        byte[]? alpha = image.Alpha is null ? null : new byte[checked(width * height)];
        for (int row = 0; row < height; row++)
        {
            Array.Copy(image.Samples, ((top + row) * image.Width + left) * components, samples, row * width * components, width * components);
            if (alpha is not null) Array.Copy(image.Alpha!, (top + row) * image.Width + left, alpha, row * width, width);
        }
        return image with { Samples = samples, Alpha = alpha, Width = width, Height = height };
    }

    private static PngDecodedImage ToRgb(PngDecodedImage image)
    {
        if (image.Components == 3) return image;
        if (image.Components != 1) throw new EpubImageUnavailableException("Native EPUB image color components are not implemented.");
        byte[] rgb = new byte[checked(image.Samples.Length * 3)];
        for (int index = 0; index < image.Samples.Length; index++)
            rgb[index * 3] = rgb[index * 3 + 1] = rgb[index * 3 + 2] = image.Samples[index];
        return image with { Samples = rgb, Components = 3 };
    }

    /// <summary>Box-filters samples down to fit the given pixel box, keeping the aspect ratio.</summary>
    private static PngDecodedImage FitSamples(PngDecodedImage image, int maximumWidth, int maximumHeight,
        CancellationToken cancellationToken)
    {
        double scale = Math.Min(1, Math.Min(maximumWidth / (double)image.Width, maximumHeight / (double)image.Height));
        if (scale >= 1) return image;
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        byte[] samples = new byte[checked(width * height * 3)];
        byte[]? alpha = image.Alpha is null ? null : new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int top = (int)((long)y * image.Height / height), bottom = Math.Max(top + 1, (int)((long)(y + 1) * image.Height / height));
            for (int x = 0; x < width; x++)
            {
                int left = (int)((long)x * image.Width / width), right = Math.Max(left + 1, (int)((long)(x + 1) * image.Width / width));
                long red = 0, green = 0, blue = 0, opacity = 0;
                for (int sy = top; sy < bottom; sy++)
                    for (int sx = left; sx < right; sx++)
                    {
                        int source = sy * image.Width + sx;
                        red += image.Samples[source * 3];
                        green += image.Samples[source * 3 + 1];
                        blue += image.Samples[source * 3 + 2];
                        if (image.Alpha is { } sourceAlpha) opacity += sourceAlpha[source];
                    }
                long count = (long)(bottom - top) * (right - left);
                int output = y * width + x;
                samples[output * 3] = (byte)((red + count / 2) / count);
                samples[output * 3 + 1] = (byte)((green + count / 2) / count);
                samples[output * 3 + 2] = (byte)((blue + count / 2) / count);
                if (alpha is not null) alpha[output] = (byte)((opacity + count / 2) / count);
            }
        }
        return new PngDecodedImage(samples, alpha, width, height, 3, 8);
    }
}
