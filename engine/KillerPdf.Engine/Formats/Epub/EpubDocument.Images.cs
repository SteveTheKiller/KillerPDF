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
            && item.MediaType == "image/png" && string.IsNullOrWhiteSpace(item.Properties)))
            throw new NotSupportedException("Native EPUB images require a packaged PNG manifest resource.");
        if (cache.TryGetValue(target, out var cached)) return cached;
        ReadOnlyMemory<byte> bytes = _resources[target];
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
        for (int offset = 8; offset < data.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length - offset < 12) throw new InvalidDataException("Truncated native EPUB PNG chunk.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (length > data.Length - offset - 12) throw new InvalidDataException("Truncated native EPUB PNG chunk.");
            var type = data.Slice(offset + 4, 4);
            if (!type.SequenceEqual("IHDR"u8) && !type.SequenceEqual("IDAT"u8) && !type.SequenceEqual("IEND"u8))
                throw new NotSupportedException("Native EPUB PNG metadata and additional chunk types are not implemented.");
            offset = checked(offset + (int)length + 12);
        }
        var image = PdfPngDecoder.Decode(bytes, 4 * 1024 * 1024);
        cancellationToken.ThrowIfCancellationRequested();
        retainedBytes += image.Samples.LongLength + (image.Alpha?.LongLength ?? 0);
        cache.Add(target, image);
        return image;
    }
}
