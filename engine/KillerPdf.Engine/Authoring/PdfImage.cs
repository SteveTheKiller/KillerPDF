using System.IO.Compression;

namespace KillerPdf.Engine.Authoring;

/// <summary>An image prepared for use as a PDF image XObject.</summary>
public sealed class PdfImage
{
    private PdfImage(
        int width, int height, int bitsPerComponent, PdfImageColorSpace colorSpace,
        string filter, byte[] data, bool invertComponents,
        int pngPredictorColors = 0, PdfImage? softMask = null)
    {
        Width = width;
        Height = height;
        BitsPerComponent = bitsPerComponent;
        ColorSpace = colorSpace;
        Filter = filter;
        Data = data;
        InvertComponents = invertComponents;
        PngPredictorColors = pngPredictorColors;
        SoftMask = softMask;
    }

    /// <summary>Gets the image width in pixels.</summary>
    public int Width { get; }
    /// <summary>Gets the image height in pixels.</summary>
    public int Height { get; }
    /// <summary>Gets the number of bits used for each color component.</summary>
    public int BitsPerComponent { get; }
    /// <summary>Gets the device color space used by the image samples.</summary>
    public PdfImageColorSpace ColorSpace { get; }
    /// <summary>Gets the encoded image payload.</summary>
    public ReadOnlyMemory<byte> Data { get; }
    internal string Filter { get; }
    internal bool InvertComponents { get; }
    internal int PngPredictorColors { get; }
    internal PdfImage? SoftMask { get; }

    /// <summary>Wraps a JPEG without recompressing its pixels.</summary>
    public static PdfImage FromJpeg(ReadOnlyMemory<byte> source)
    {
        byte[] data = source.ToArray();
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            throw new FormatException("The image does not begin with a JPEG SOI marker.");
        int position = 2;
        int width = 0;
        int height = 0;
        int bits = 0;
        int components = 0;
        PdfImageColorSpace colorSpace = default;
        bool sawScan = false;
        HashSet<byte>? frameComponentIds = null;
        while (position < data.Length)
        {
            while (position < data.Length && data[position] != 0xFF) position++;
            while (position < data.Length && data[position] == 0xFF) position++;
            if (position >= data.Length) break;
            byte marker = data[position++];
            if (marker == 0xD9)
            {
                if (width == 0)
                    throw new FormatException("The JPEG has no supported start-of-frame marker.");
                if (!sawScan)
                    throw new FormatException("The JPEG has no scan data.");
                return new PdfImage(width, height, bits, colorSpace, "DCTDecode", data,
                    invertComponents: components == 4);
            }
            if (marker is 0x00 or 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (position + 2 > data.Length)
                throw new FormatException("A JPEG marker length is truncated.");
            int length = (data[position] << 8) | data[position + 1];
            if (length < 2 || position + length > data.Length)
                throw new FormatException("A JPEG marker points outside the image.");
            if (IsAnyStartOfFrame(marker) && !IsSupportedStartOfFrame(marker))
                throw new NotSupportedException(
                    $"JPEG start-of-frame process 0x{marker:X2} is not supported for PDF DCT passthrough.");
            if (IsSupportedStartOfFrame(marker))
            {
                if (width != 0)
                    throw new NotSupportedException(
                        "JPEG images containing multiple frames are not supported.");
                if (length < 8)
                    throw new FormatException("The JPEG frame header is truncated.");
                bits = data[position + 2];
                height = (data[position + 3] << 8) | data[position + 4];
                width = (data[position + 5] << 8) | data[position + 6];
                components = data[position + 7];
                if (width == 0 || height == 0)
                    throw new FormatException("The JPEG frame dimensions are invalid.");
                if (bits != 8)
                    throw new NotSupportedException(
                        $"JPEG images with {bits}-bit samples are not supported.");
                if (components == 0 || length != 8 + components * 3)
                    throw new FormatException(
                        "The JPEG frame component table is malformed or truncated.");
                frameComponentIds = [];
                for (int component = 0; component < components; component++)
                {
                    int componentOffset = position + 8 + component * 3;
                    byte identifier = data[componentOffset];
                    byte sampling = data[componentOffset + 1];
                    int horizontalSampling = sampling >> 4;
                    int verticalSampling = sampling & 0x0F;
                    if (!frameComponentIds.Add(identifier)
                        || horizontalSampling is < 1 or > 4
                        || verticalSampling is < 1 or > 4
                        || data[componentOffset + 2] > 3)
                        throw new FormatException(
                            "The JPEG frame contains invalid component identifiers or sampling factors.");
                }
                colorSpace = components switch
                {
                    1 => PdfImageColorSpace.Gray,
                    3 => PdfImageColorSpace.Rgb,
                    4 => PdfImageColorSpace.Cmyk,
                    _ => throw new NotSupportedException($"JPEG images with {components} components are not supported.")
                };
            }
            if (marker == 0xDA)
            {
                if (width == 0)
                    throw new FormatException("The JPEG scan precedes its frame header.");
                if (length < 6)
                    throw new FormatException("The JPEG scan header is truncated.");
                int scanComponents = data[position + 2];
                if (scanComponents == 0 || length != 6 + scanComponents * 2)
                    throw new FormatException("The JPEG scan component table is malformed.");
                var scanComponentIds = new HashSet<byte>();
                for (int component = 0; component < scanComponents; component++)
                {
                    byte identifier = data[position + 3 + component * 2];
                    if (!scanComponentIds.Add(identifier)
                        || frameComponentIds is null
                        || !frameComponentIds.Contains(identifier))
                        throw new FormatException(
                            "The JPEG scan references a duplicate or undefined frame component.");
                }
                sawScan = true;
                position += length;
                while (position + 1 < data.Length)
                {
                    if (data[position] != 0xFF)
                    {
                        position++;
                        continue;
                    }
                    byte next = data[position + 1];
                    if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
                    {
                        position += 2;
                        continue;
                    }
                    break;
                }
                continue;
            }
            position += length;
        }
        throw new FormatException(width == 0
            ? "The JPEG has no supported start-of-frame marker."
            : "The JPEG is missing its EOI marker.");
    }

    /// <summary>Compresses interleaved 8-bit RGB pixels with the PDF Flate filter.</summary>
    public static PdfImage FromRgb(
        int width, int height, ReadOnlyMemory<byte> pixels,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ValidateDimensions(width, height);
        int required = RequiredBytes(width, height, 3);
        if (pixels.Length != required)
            throw new ArgumentException($"An {width} by {height} RGB image requires {required} bytes.", nameof(pixels));
        return FromInterleaved(width, height, pixels, PdfImageColorSpace.Rgb,
            compressionLevel);
    }

    /// <summary>Compresses 8-bit grayscale pixels with the PDF Flate filter.</summary>
    public static PdfImage FromGray(
        int width, int height, ReadOnlyMemory<byte> pixels,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ValidateDimensions(width, height);
        int required = RequiredBytes(width, height, 1);
        if (pixels.Length != required)
            throw new ArgumentException(
                $"A {width} by {height} grayscale image requires {required} bytes.",
                nameof(pixels));
        return FromInterleaved(width, height, pixels, PdfImageColorSpace.Gray,
            compressionLevel);
    }

    /// <summary>Packs black-and-white samples into a one-bit grayscale image.</summary>
    public static PdfImage FromBitonal(
        int width, int height, ReadOnlyMemory<byte> pixels,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ValidateDimensions(width, height);
        int required = RequiredBytes(width, height, 1);
        if (pixels.Length != required)
            throw new ArgumentException(
                $"A {width} by {height} bitonal image requires {required} samples.",
                nameof(pixels));
        int rowBytes = checked((width + 7) / 8);
        byte[] packed = new byte[checked(rowBytes * height)];
        ReadOnlySpan<byte> source = pixels.Span;
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * width;
            int targetRow = y * rowBytes;
            for (int x = 0; x < width; x++)
                if (source[sourceRow + x] >= 128)
                    packed[targetRow + x / 8] |= (byte)(0x80 >> (x & 7));
        }
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, compressionLevel, leaveOpen: true))
            zlib.Write(packed);
        byte[] flate = output.ToArray();
        byte[] ccitt = PdfCcittGroup4Encoder.Encode(width, height, source);
        if (ccitt.Length < flate.Length)
            return new PdfImage(width, height, 1, PdfImageColorSpace.Gray,
                "CCITTFaxDecode", ccitt, invertComponents: false);
        return new PdfImage(width, height, 1, PdfImageColorSpace.Gray,
            "FlateDecode", flate, invertComponents: false);
    }

    /// <summary>Compresses interleaved 8-bit CMYK pixels with the PDF Flate filter.</summary>
    public static PdfImage FromCmyk(
        int width, int height, ReadOnlyMemory<byte> pixels,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ValidateDimensions(width, height);
        int required = RequiredBytes(width, height, 4);
        if (pixels.Length != required)
            throw new ArgumentException(
                $"A {width} by {height} CMYK image requires {required} bytes.",
                nameof(pixels));
        return FromInterleaved(width, height, pixels, PdfImageColorSpace.Cmyk,
            compressionLevel);
    }

    /// <summary>Compresses interleaved 8-bit RGBA pixels and preserves alpha as a soft mask.</summary>
    public static PdfImage FromRgba(
        int width, int height, ReadOnlyMemory<byte> pixels,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ValidateDimensions(width, height);
        int required = RequiredBytes(width, height, 4);
        int pixelCount = required / 4;
        if (pixels.Length != required)
            throw new ArgumentException($"An {width} by {height} RGBA image requires {required} bytes.", nameof(pixels));
        byte[] rgb = new byte[pixelCount * 3];
        byte[] alpha = new byte[pixelCount];
        ReadOnlySpan<byte> source = pixels.Span;
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            rgb[pixel * 3] = source[pixel * 4];
            rgb[pixel * 3 + 1] = source[pixel * 4 + 1];
            rgb[pixel * 3 + 2] = source[pixel * 4 + 2];
            alpha[pixel] = source[pixel * 4 + 3];
        }
        PdfImage color = FromRgb(width, height, rgb, compressionLevel);
        PdfImage mask = FromGray(width, height, alpha, compressionLevel);
        return new PdfImage(width, height, 8, PdfImageColorSpace.Rgb,
            color.Filter, color.Data.ToArray(), invertComponents: false,
            color.PngPredictorColors, mask);
    }

    /// <summary>Compresses interleaved 8-bit grayscale and alpha pixels.</summary>
    public static PdfImage FromGrayAlpha(
        int width, int height, ReadOnlyMemory<byte> pixels,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ValidateDimensions(width, height);
        int required = RequiredBytes(width, height, 2);
        if (pixels.Length != required)
            throw new ArgumentException(
                $"A {width} by {height} grayscale-alpha image requires {required} bytes.",
                nameof(pixels));
        int pixelCount = required / 2;
        byte[] gray = new byte[pixelCount];
        byte[] alpha = new byte[pixelCount];
        ReadOnlySpan<byte> source = pixels.Span;
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            gray[pixel] = source[pixel * 2];
            alpha[pixel] = source[pixel * 2 + 1];
        }
        PdfImage color = FromGray(width, height, gray, compressionLevel);
        PdfImage mask = FromGray(width, height, alpha, compressionLevel);
        return new PdfImage(width, height, 8, PdfImageColorSpace.Gray,
            color.Filter, color.Data.ToArray(), invertComponents: false,
            color.PngPredictorColors, mask);
    }

    /// <summary>Compresses interleaved 8-bit CMYK and alpha pixels.</summary>
    public static PdfImage FromCmyka(
        int width, int height, ReadOnlyMemory<byte> pixels,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ValidateDimensions(width, height);
        int required = RequiredBytes(width, height, 5);
        if (pixels.Length != required)
            throw new ArgumentException(
                $"A {width} by {height} CMYK-alpha image requires {required} bytes.",
                nameof(pixels));
        int pixelCount = required / 5;
        byte[] cmyk = new byte[pixelCount * 4];
        byte[] alpha = new byte[pixelCount];
        ReadOnlySpan<byte> source = pixels.Span;
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            source.Slice(pixel * 5, 4).CopyTo(cmyk.AsSpan(pixel * 4, 4));
            alpha[pixel] = source[pixel * 5 + 4];
        }
        PdfImage color = FromCmyk(width, height, cmyk, compressionLevel);
        PdfImage mask = FromGray(width, height, alpha, compressionLevel);
        return new PdfImage(width, height, 8, PdfImageColorSpace.Cmyk,
            color.Filter, color.Data.ToArray(), invertComponents: false,
            color.PngPredictorColors, mask);
    }

    private static PdfImage FromInterleaved(
        int width, int height, ReadOnlyMemory<byte> pixels,
        PdfImageColorSpace colorSpace, CompressionLevel compressionLevel)
    {
        int colors = colorSpace switch
        {
            PdfImageColorSpace.Gray => 1,
            PdfImageColorSpace.Rgb => 3,
            PdfImageColorSpace.Cmyk => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace))
        };
        byte[] plain = Compress(pixels.Span, compressionLevel);
        byte[] predicted = Compress(PdfPngPredictorEncoder.Encode(
            width, height, colors, pixels.Span), compressionLevel);
        if (predicted.Length < plain.Length)
            return new PdfImage(width, height, 8, colorSpace,
                "FlateDecode", predicted, invertComponents: false, colors);
        return new PdfImage(width, height, 8, colorSpace,
            "FlateDecode", plain, invertComponents: false);
    }

    private static byte[] Compress(ReadOnlySpan<byte> source, CompressionLevel compressionLevel)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, compressionLevel, leaveOpen: true))
            zlib.Write(source);
        return output.ToArray();
    }

    private static bool IsSupportedStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC9 or 0xCA;

    private static bool IsAnyStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static void ValidateDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
    }

    private static int RequiredBytes(int width, int height, int components)
    {
        long pixels = (long)width * height;
        if (pixels > int.MaxValue / components)
            throw new ArgumentOutOfRangeException(nameof(width),
                "The image pixel buffer exceeds the supported in-memory size.");
        return (int)pixels * components;
    }
}

/// <summary>The device color space used by a PDF image.</summary>
public enum PdfImageColorSpace
{
    /// <summary>One-component grayscale samples.</summary>
    Gray,
    /// <summary>Three-component red, green, and blue samples.</summary>
    Rgb,
    /// <summary>Four-component cyan, magenta, yellow, and black samples.</summary>
    Cmyk
}
