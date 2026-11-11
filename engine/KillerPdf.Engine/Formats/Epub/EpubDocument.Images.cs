using System.Buffers.Binary;
using System.IO.Compression;
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
    // Pages keep only image keys; decoded rasters live in a bounded store and are decoded again when drawn
    // (item A: Monte Cristo lost 234 illustrations to the old book-wide budget). Images are still downsampled
    // to twice the content box.
    // A single decode may briefly hold the full-size image; this bounds that transient allocation.
    private const int MaximumDecodeBytes = 96 * 1024 * 1024;
    private const long MaximumSourcePixels = 40_000_000;

    /// <summary>Resolves an image reference to its packaged path, refusing references the reader cannot show.</summary>
    private string ResolveNativeImage(string chapter, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new EpubImageUnavailableException("Native EPUB image has no usable source.", EpubOmissionReason.MissingResource);
        string target;
        try { target = NavigationTarget(chapter, reference.Trim(' ', '\t', '\r', '\n')); }
        catch (InvalidDataException exception)
        {
            throw new EpubImageUnavailableException("Native EPUB image is not packaged: " + reference + ".",
                EpubOmissionReason.MissingResource, exception);
        }
        // A picture left out of the package for its size or its damaged entry is reported as that, once.
        if (_oversizedResources.Contains(target))
            throw _damagedResources.Contains(target)
                ? new EpubImageUnavailableException(UnavailableResource(target).Message, EpubOmissionReason.ImageUnreadable)
                : new EpubImageUnavailableException(OversizedResource(target).Message, EpubOmissionReason.ImageTooLarge);
        var item = Manifest.FirstOrDefault(entry => entry.Href == target);
        if (target.Contains('#') || item?.MediaType is not ("image/png" or "image/jpeg" or "image/jpg"))
            throw new EpubImageUnavailableException("Native EPUB image format is not supported: " + target
                + (item is null ? "." : " (" + item.MediaType + ")."));
        if (_encryptedResources.Contains(target))
            throw new EpubImageUnavailableException("Native EPUB image is encrypted: " + target + ".", EpubOmissionReason.Encrypted);
        return target;
    }

    /// <summary>Decodes a resolved image at most twice the given box, then applies the key's crop.</summary>
    private PngDecodedImage DecodeNativeImage(EpubImageKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = key.Target;
        var box = (Width: key.BoxWidth, Height: key.BoxHeight);
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
        image = Orient(image, key.TurnA, key.TurnB, key.TurnC, key.TurnD);
        if (key.Warp is { } warp) image = Warp(image, warp, cancellationToken);
        return key.CropWidth < 0 ? image : Crop(image, key.CropLeft, key.CropTop, key.CropWidth, key.CropHeight);
    }

    /// <summary>
    /// Resamples the raster through the warp by inverse mapping: each output pixel center maps back into the source,
    /// which is sampled bilinearly from its four nearest pixels (edge pixels repeat). Output pixels whose center maps
    /// outside the source or its clip rectangle stay transparent, so the drawing keeps its turned outline.
    /// </summary>
    private static PngDecodedImage Warp(PngDecodedImage image, EpubImageWarp warp, CancellationToken cancellationToken)
    {
        double determinant = warp.A * warp.D - warp.B * warp.C;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
            throw new EpubImageUnavailableException("Native EPUB SVG image transform collapses the image.", EpubOmissionReason.UnsupportedLayout);
        // Inverse of the linear part: x = (D (u - E) - C (v - F)) / det, y = (A (v - F) - B (u - E)) / det.
        double ia = warp.D / determinant, ic = -warp.C / determinant, ib = -warp.B / determinant, id = warp.A / determinant;
        int width = warp.Width, height = warp.Height, components = image.Components;
        double left = Math.Max(0, warp.ClipLeft), top = Math.Max(0, warp.ClipTop);
        double right = Math.Min(image.Width, warp.ClipRight), bottom = Math.Min(image.Height, warp.ClipBottom);
        byte[] samples = new byte[checked(width * height * components)];
        byte[] alpha = new byte[checked(width * height)];
        var source = image.Samples;
        var sourceAlpha = image.Alpha;
        for (int v = 0; v < height; v++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int u = 0; u < width; u++)
            {
                double du = u + .5 - warp.E, dv = v + .5 - warp.F;
                double x = ia * du + ic * dv, y = ib * du + id * dv;
                if (x < left || x > right || y < top || y > bottom) continue;
                // Pixel centers sit at half coordinates, so the four neighbors of (x, y) start at (x - 0.5, y - 0.5).
                double sx = Math.Clamp(x - .5, 0, image.Width - 1), sy = Math.Clamp(y - .5, 0, image.Height - 1);
                int x0 = (int)sx, y0 = (int)sy;
                int x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
                double fx = sx - x0, fy = sy - y0;
                double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
                int p00 = y0 * image.Width + x0, p10 = y0 * image.Width + x1, p01 = y1 * image.Width + x0, p11 = y1 * image.Width + x1;
                int to = v * width + u;
                for (int channel = 0; channel < components; channel++)
                    samples[to * components + channel] = (byte)Math.Round(w00 * source[p00 * components + channel]
                        + w10 * source[p10 * components + channel] + w01 * source[p01 * components + channel]
                        + w11 * source[p11 * components + channel]);
                alpha[to] = sourceAlpha is null ? (byte)255 : (byte)Math.Round(w00 * sourceAlpha[p00] + w10 * sourceAlpha[p10]
                    + w01 * sourceAlpha[p01] + w11 * sourceAlpha[p11]);
            }
        }
        return image with { Samples = samples, Alpha = alpha, Width = width, Height = height };
    }

    /// <summary>
    /// Turns or flips the raster by an axis-keeping linear map whose entries are -1, 0 or 1 (an SVG image transform's
    /// signs): source column x and row y land at u = a x + c y and v = b x + d y, shifted to start at zero.
    /// </summary>
    private static PngDecodedImage Orient(PngDecodedImage image, int a, int b, int c, int d)
    {
        if (a == 1 && b == 0 && c == 0 && d == 1) return image;
        int width = Math.Abs(a) * image.Width + Math.Abs(c) * image.Height;
        int height = Math.Abs(b) * image.Width + Math.Abs(d) * image.Height;
        int components = image.Components;
        byte[] samples = new byte[checked(width * height * components)];
        byte[]? alpha = image.Alpha is null ? null : new byte[checked(width * height)];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int u = Index(a, x, image.Width) + Index(c, y, image.Height);
                int v = Index(b, x, image.Width) + Index(d, y, image.Height);
                int from = y * image.Width + x, to = v * width + u;
                Array.Copy(image.Samples, from * components, samples, to * components, components);
                if (alpha is not null) alpha[to] = image.Alpha![from];
            }
        return image with { Samples = samples, Alpha = alpha, Width = width, Height = height };

        static int Index(int sign, int value, int extent) => sign switch { 1 => value, -1 => extent - 1 - value, _ => 0 };
    }

    internal static PngDecodedImage ReadNativePng(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
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
        bool foreignProfile = false, srgbChunk = false, profileSeen = false;
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
                // PNG allows one iCCP chunk. Refusing a second keeps the inflated profile bytes bounded for the whole
                // image, however many tiny chunks a hostile file packs into its entry.
                if (profileSeen)
                    throw new EpubImageUnavailableException("Native EPUB PNG has more than one iCCP chunk.",
                        EpubOmissionReason.ImageUnreadable);
                profileSeen = true;
                // The profile's own description decides; editors often label the standard sRGB profile 'ICC profile'.
                // The name is only a fallback for a profile that cannot be inflated. An sRGB chunk (checked below)
                // also vouches for the image.
                var payload = data.Slice(offset + 8, (int)length);
                int nul = payload.IndexOf((byte)0);
                string name = Encoding.Latin1.GetString(nul < 0 ? payload : payload[..nul]);
                bool srgb = InflateIccProfile(nul < 0 ? default : payload[(nul + 1)..]) is { } profile
                    ? IsSrgbProfile(profile) : name.Contains("sRGB", StringComparison.OrdinalIgnoreCase);
                if (!srgb) foreignProfile = true;
            }
            if (type.SequenceEqual("sRGB"u8) && length == 1) srgbChunk = true;
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
        if (foreignProfile && !srgbChunk)
            throw new EpubImageUnavailableException("Native EPUB PNG embedded color profiles (iCCP) other than sRGB are not implemented.",
                EpubOmissionReason.ImageColorProfile);
        var image = PdfPngDecoder.Decode(bytes, MaximumDecodeBytes);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }

    // Counts the profile bytes inflated on this thread, so a test can prove the work stays bounded per image.
    [ThreadStatic] internal static long IccInflatedBytes;

    // The bytes of an iCCP profile (a compression method byte, then a zlib stream), or null when it cannot be inflated
    // within 4 MiB.
    private static byte[]? InflateIccProfile(ReadOnlySpan<byte> afterName)
    {
        const int limit = 4 * 1024 * 1024;
        if (afterName.Length < 2 || afterName[0] != 0) return null;
        try
        {
            using var input = new MemoryStream(afterName[1..].ToArray(), false);
            using var inflater = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = inflater.Read(buffer)) != 0)
            {
                if (output.Length + count > limit) return null;
                IccInflatedBytes += count;
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        catch (Exception error) when (error is InvalidDataException or IOException) { return null; }
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
