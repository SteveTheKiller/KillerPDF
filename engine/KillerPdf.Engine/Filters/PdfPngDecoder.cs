using System.Buffers.Binary;
using System.IO.Compression;

namespace KillerPdf.Engine.Filters;

internal sealed record PngDecodedImage(
    byte[] Samples, byte[]? Alpha, int Width, int Height, int Components, int Bits);

internal static class PdfPngDecoder
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static bool HasSignature(ReadOnlySpan<byte> source) =>
        source.StartsWith(Signature);

    internal static PngDecodedImage Decode(
        ReadOnlyMemory<byte> source, int maximumDecodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDecodedBytes);
        ReadOnlySpan<byte> bytes = source.Span;
        if (!HasSignature(bytes)) throw Error("PNG signature is missing.");

        int width = 0, height = 0, bits = 0, colorType = -1, interlace = -1;
        byte[]? palette = null, transparency = null;
        using var compressed = new MemoryStream();
        bool sawHeader = false, sawEnd = false;
        for (int offset = Signature.Length; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 12) throw Error("PNG chunk header is truncated.");
            uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (lengthValue > int.MaxValue) throw Error("PNG chunk is too large.");
            int length = (int)lengthValue;
            if (length > bytes.Length - offset - 12) throw Error("PNG chunk is truncated.");
            uint type = BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 4)..]);
            ReadOnlySpan<byte> data = bytes.Slice(offset + 8, length);
            offset = checked(offset + 12 + length);
            switch (type)
            {
                case 0x49484452: // IHDR
                    if (sawHeader || length != 13) throw Error("PNG header is invalid.");
                    width = PositiveDimension(data, 0);
                    height = PositiveDimension(data, 4);
                    bits = data[8];
                    colorType = data[9];
                    if (data[10] != 0 || data[11] != 0) throw Error("PNG compression method is unsupported.");
                    interlace = data[12];
                    sawHeader = true;
                    break;
                case 0x504C5445: // PLTE
                    palette = data.ToArray();
                    break;
                case 0x74524E53: // tRNS
                    transparency = data.ToArray();
                    break;
                case 0x49444154: // IDAT
                    if (!sawHeader) throw Error("PNG image data precedes its header.");
                    if (compressed.Length + length > source.Length)
                        throw Error("PNG image data is too large.");
                    compressed.Write(data);
                    break;
                case 0x49454E44: // IEND
                    sawEnd = true;
                    offset = bytes.Length;
                    break;
            }
        }
        if (!sawHeader || !sawEnd || compressed.Length == 0)
            throw Error("PNG structure is incomplete.");
        if (interlace != 0) throw Error("Interlaced PNG data is unsupported.");

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw Error("PNG color type is unsupported.")
        };
        bool validBits = colorType switch
        {
            0 => bits is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => bits is 8 or 16,
            3 => bits is 1 or 2 or 4 or 8,
            _ => false
        };
        if (!validBits) throw Error("PNG sample depth is unsupported.");
        if (colorType == 3 && (palette is null || palette.Length == 0
            || palette.Length % 3 != 0 || palette.Length > 768))
            throw Error("PNG palette is invalid.");

        int rowBytes = checked((int)(((long)width * channels * bits + 7) / 8));
        int inflatedLength = checked((rowBytes + 1) * height);
        int components = colorType is 0 or 4 ? 1 : 3;
        int sampleLength = checked(width * height * components);
        if (inflatedLength > maximumDecodedBytes || sampleLength > maximumDecodedBytes)
            throw Error("PNG decoded data exceeds the safety limit.");

        byte[] filtered = new byte[inflatedLength];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            ReadExactly(zlib, filtered);
            if (zlib.ReadByte() != -1) throw Error("PNG decoded data exceeds its dimensions.");
        }
        byte[] rows = Unfilter(filtered, width, height, channels, bits, rowBytes);
        byte[] samples = new byte[sampleLength];
        byte[]? alpha = null;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rows.AsSpan(y * rowBytes, rowBytes);
            for (int x = 0; x < width; x++)
            {
                int pixel = y * width + x;
                int sample = x * channels;
                int a = 255;
                if (colorType == 0)
                {
                    int gray = RawSample(row, sample, bits);
                    samples[pixel] = ScaleSample(gray, bits);
                    if (transparency is { Length: >= 2 }
                        && gray == BinaryPrimitives.ReadUInt16BigEndian(transparency)) a = 0;
                }
                else if (colorType == 2)
                {
                    int output = pixel * 3;
                    int red = RawSample(row, sample, bits);
                    int green = RawSample(row, sample + 1, bits);
                    int blue = RawSample(row, sample + 2, bits);
                    samples[output] = ScaleSample(red, bits);
                    samples[output + 1] = ScaleSample(green, bits);
                    samples[output + 2] = ScaleSample(blue, bits);
                    if (transparency is { Length: >= 6 }
                        && red == BinaryPrimitives.ReadUInt16BigEndian(transparency)
                        && green == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2))
                        && blue == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4))) a = 0;
                }
                else if (colorType == 3)
                {
                    int index = RawSample(row, sample, bits);
                    if (index >= palette!.Length / 3) throw Error("PNG palette index is invalid.");
                    int output = pixel * 3;
                    samples[output] = palette[index * 3];
                    samples[output + 1] = palette[index * 3 + 1];
                    samples[output + 2] = palette[index * 3 + 2];
                    if (transparency is not null && index < transparency.Length) a = transparency[index];
                }
                else if (colorType == 4)
                {
                    samples[pixel] = ScaleSample(RawSample(row, sample, bits), bits);
                    a = ScaleSample(RawSample(row, sample + 1, bits), bits);
                }
                else
                {
                    int output = pixel * 3;
                    samples[output] = ScaleSample(RawSample(row, sample, bits), bits);
                    samples[output + 1] = ScaleSample(RawSample(row, sample + 1, bits), bits);
                    samples[output + 2] = ScaleSample(RawSample(row, sample + 2, bits), bits);
                    a = ScaleSample(RawSample(row, sample + 3, bits), bits);
                }
                if (a != 255)
                {
                    alpha ??= Enumerable.Repeat((byte)255, width * height).ToArray();
                    alpha[pixel] = (byte)a;
                }
            }
        }
        return new PngDecodedImage(samples, alpha, width, height, components, 8);
    }

    private static byte[] Unfilter(byte[] filtered, int width, int height,
        int channels, int bits, int rowBytes)
    {
        byte[] rows = new byte[checked(rowBytes * height)];
        int bytesPerPixel = Math.Max(1, (channels * bits + 7) / 8);
        for (int y = 0; y < height; y++)
        {
            int input = y * (rowBytes + 1);
            int output = y * rowBytes;
            int filter = filtered[input];
            for (int x = 0; x < rowBytes; x++)
            {
                int raw = filtered[input + 1 + x];
                int left = x >= bytesPerPixel ? rows[output + x - bytesPerPixel] : 0;
                int up = y > 0 ? rows[output - rowBytes + x] : 0;
                int upperLeft = y > 0 && x >= bytesPerPixel
                    ? rows[output - rowBytes + x - bytesPerPixel] : 0;
                rows[output + x] = filter switch
                {
                    0 => (byte)raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + (left + up) / 2),
                    4 => (byte)(raw + Paeth(left, up, upperLeft)),
                    _ => throw Error("PNG row filter is invalid.")
                };
            }
        }
        return rows;
    }

    private static int RawSample(ReadOnlySpan<byte> row, int index, int bits) => bits switch
    {
        16 => BinaryPrimitives.ReadUInt16BigEndian(row[(index * 2)..]),
        8 => row[index],
        _ => row[index * bits / 8] >> (8 - bits - index * bits % 8) & (1 << bits) - 1
    };

    private static byte ScaleSample(int value, int bits) => bits switch
    {
        16 => (byte)(value >> 8),
        8 => (byte)value,
        _ => (byte)(value * 255 / ((1 << bits) - 1))
    };

    private static int Paeth(int left, int up, int upperLeft)
    {
        int estimate = left + up - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int upDistance = Math.Abs(estimate - up);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance
            ? left : upDistance <= upperLeftDistance ? up : upperLeft;
    }

    private static int PositiveDimension(ReadOnlySpan<byte> data, int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        return value is > 0 and <= int.MaxValue
            ? (int)value : throw Error("PNG dimensions are invalid.");
    }

    private static void ReadExactly(Stream source, Span<byte> destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = source.Read(destination[offset..]);
            if (read == 0) throw Error("PNG image data is truncated.");
            offset += read;
        }
    }

    private static PdfFilterException Error(string message) => new(message);
}
