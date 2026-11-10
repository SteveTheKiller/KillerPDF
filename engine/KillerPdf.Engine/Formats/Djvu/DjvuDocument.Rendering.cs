using System.Buffers.Binary;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Renders supported bitonal, grayscale and initial color content at natural dimensions, limited to a 128 MiB RGBA32F frame.</summary>
    /// <remarks>
    /// Grayscale IW44 requires full-resolution version 1.2 background chunks. The stored INFO device gamma,
    /// clamped to 0.3 through 5.0 like DjVuLibre, maps samples to linear RGB. Image working buffers are limited to 256 MiB.
    /// Initial color IW44 support requires one version 1.2 full-resolution background chunk, upright block-aligned
    /// dimensions, and full chroma with zero delay. Its eight-bit RGB matches DjVuLibre 3.5.29 output.
    /// </remarks>
    public PdfRenderedFrame RenderPage(int pageIndex, CancellationToken cancellationToken = default)
    {
        var (page, chunks) = ReadSupportedPage(pageIndex, cancellationToken);
        return RenderSupportedPage(page, chunks, cancellationToken);
    }

    /// <summary>
    /// Decodes a page into the form it was coded in: Smmr as one bit per pixel, grayscale IW44 as one byte and color
    /// IW44 as three, at natural size and in stored orientation, with the page's turn applied when it is read.
    /// </summary>
    internal PdfNativeCompactRaster DecodePage(int pageIndex, CancellationToken cancellationToken)
    {
        var (page, chunks) = ReadPageChunks(pageIndex, cancellationToken);
        return DecodeSupportedPage(page, chunks, cancellationToken, callerBytesPerPixel: 0);
    }

    /// <summary>The bytes <see cref="DecodePage"/> keeps for a page, found from its chunks without decoding it.</summary>
    internal long DecodedPageBytes(int pageIndex, CancellationToken cancellationToken)
    {
        var (page, chunks) = ReadPageChunks(pageIndex, cancellationToken);
        var layout = PdfNativeSampleLayout.Bits;
        if (chunks.Any(c => c.Id == 0x42473434))
        {
            var first = chunks.First(c => c.Id == 0x42473434);
            layout = first.Length >= 3 && (_data[first.Offset + 2] & 0x80) == 0
                ? PdfNativeSampleLayout.Rgb : PdfNativeSampleLayout.Gray;
        }
        return PdfNativeCompactRaster.StorageBytes(layout, page.Width, page.Height);
    }

    private (DjvuPageInfo Page, List<Chunk> Chunks) ReadSupportedPage(int pageIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Pages[pageIndex];
        // A whole page as a float frame is kept to 128 MiB; viewers resample the compact decode instead.
        if ((long)page.Width * page.Height > 128L * 1024 * 1024 / (4 * sizeof(float))
            && page.MajorVersion == 0 && page.MinorVersion <= 26)
            throw new InvalidDataException("The DjVu frame exceeds the 128 MiB storage limit.");
        return ReadPageChunks(pageIndex, cancellationToken);
    }

    private (DjvuPageInfo Page, List<Chunk> Chunks) ReadPageChunks(int pageIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Pages[pageIndex];
        if (page.MajorVersion != 0 || page.MinorVersion > 26) throw new NotSupportedException("Rendering this DjVu page version is not supported.");
        var form = _pageForms[pageIndex];
        var budget = 65_535;
        var chunks = ReadChunks(_data, form.Offset + 4, form.Offset + form.Length, cancellationToken, ref budget);
        return (page, chunks);
    }

    private PdfRenderedFrame RenderSupportedPage(DjvuPageInfo page, List<Chunk> chunks, CancellationToken cancellationToken)
        => DecodeSupportedPage(page, chunks, cancellationToken).ToFrame(cancellationToken);

    // callerBytesPerPixel is what the caller builds per pixel from the decode: 32 for a float frame and its copy.
    private PdfNativeCompactRaster DecodeSupportedPage(DjvuPageInfo page, List<Chunk> chunks, CancellationToken cancellationToken,
        int callerBytesPerPixel = 32)
    {
        if (chunks.Any(c => c.Id == 0x42473434)) return DecodeIw44Raster(page, chunks, cancellationToken, callerBytesPerPixel);
        if (chunks.Any(c => c.Id is not (0x494e464f or 0x536d6d72 or 0x54585461 or 0x5458547a)))
            throw new NotSupportedException("Rendering this page requires unsupported layers, annotations or component content.");
        if (chunks.Count(c => c.Id == 0x536d6d72) > 1) throw new InvalidDataException("A page contains duplicate Smmr masks.");
        var mask = chunks.FirstOrDefault(c => c.Id == 0x536d6d72);
        if (mask.Id == 0) throw new NotSupportedException("This page has no supported Smmr image content.");
        var payload = _data.AsSpan(mask.Offset, mask.Length);
        if (payload.Length < 8 || !payload[..3].SequenceEqual("MMR"u8)) throw new InvalidDataException("Invalid Smmr MMR header.");
        if ((payload[3] & 0xfe) != 0) throw new NotSupportedException("Striped Smmr data and reserved MMR flag variants are not supported.");
        if (BinaryPrimitives.ReadUInt16BigEndian(payload[4..]) != page.Width || BinaryPrimitives.ReadUInt16BigEndian(payload[6..]) != page.Height)
            throw new InvalidDataException("Smmr dimensions do not match INFO dimensions.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] packed;
        try
        {
            packed = PdfCcittFaxDecoder.Decode(payload[8..], new PdfCcittFaxOptions(-1, page.Width, page.Height, false, false, true), 16 * 1024 * 1024);
        }
        catch (PdfFilterException exception)
        {
            throw new InvalidDataException("Smmr G4 decoding failed.", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // CCITT white is a zero bit; the MMR invert flag swaps the two values.
        float[] linear = (payload[3] & 1) != 0 ? [0f, 1f] : [1f, 0f];
        return new PdfNativeCompactRaster(PdfNativeSampleLayout.Bits, page.Width, page.Height, packed, linear,
            (int)page.Orientation);
    }

    private PdfNativeCompactRaster DecodeIw44Raster(DjvuPageInfo page, List<Chunk> chunks, CancellationToken token,
        int callerBytesPerPixel)
    {
        var firstBackground = chunks.First(c => c.Id == 0x42473434);
        if (firstBackground.Length >= 3 && (_data[firstBackground.Offset + 2] & 0x80) == 0)
            return DecodeColorIw44Raster(page, chunks, token);
        var (gray, gamma) = DecodeIw44Page(page, chunks, token, callerBytesPerPixel);
        return new PdfNativeCompactRaster(PdfNativeSampleLayout.Gray, page.Width, page.Height, gray, LinearTable(gamma),
            (int)page.Orientation);
    }

    private PdfNativeCompactRaster DecodeColorIw44Raster(DjvuPageInfo page, List<Chunk> chunks, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (chunks.Any(c => c.Id is not (0x494e464f or 0x42473434 or 0x54585461 or 0x5458547a)))
            throw new NotSupportedException("Color IW44 rendering requires a background without other visual layers.");
        if (page.Orientation != DjvuOrientation.Upright)
            throw new NotSupportedException("Color IW44 currently requires upright orientation.");
        var gamma = InfoGamma(chunks);
        var backgrounds = chunks.Where(c => c.Id == 0x42473434).Take(2)
            .Select(c => (ReadOnlyMemory<byte>)_data.AsMemory(c.Offset, c.Length)).ToArray();
        var rgb = DjvuIw44Decoder.DecodeColor(backgrounds, page.Width, page.Height, token);
        token.ThrowIfCancellationRequested();
        return new PdfNativeCompactRaster(PdfNativeSampleLayout.Rgb, page.Width, page.Height, rgb, LinearTable(gamma), 0);
    }

    // Each eight-bit sample at the INFO gamma, as a linear channel value.
    private static float[] LinearTable(double gamma)
    {
        var table = new float[256];
        for (int value = 0; value < 256; value++) table[value] = (float)Math.Pow(value / 255d, gamma);
        return table;
    }

    // Like DjVuInfo::decode, a missing gamma byte means 2.2 and stored values clamp to 0.3 through 5.0.
    private double InfoGamma(List<Chunk> chunks)
        => chunks[0].Length < 9 ? 2.2 : Math.Clamp(_data[chunks[0].Offset + 8] / 10d, 0.3, 5.0);

    private (byte[] Gray, double Gamma) DecodeIw44Page(DjvuPageInfo page, List<Chunk> chunks, CancellationToken token,
        int callerBytesPerPixel = 32)
    {
        if (chunks.Any(c => c.Id is not (0x494e464f or 0x42473434 or 0x54585461 or 0x5458547a)))
            throw new NotSupportedException("IW44 rendering requires a grayscale background without other visual layers.");
        var gamma = InfoGamma(chunks);
        var backgrounds = chunks.Where(c => c.Id == 0x42473434).Take(257)
            .Select(c => (ReadOnlyMemory<byte>)_data.AsMemory(c.Offset, c.Length)).ToArray();
        var gray = DjvuIw44Decoder.DecodeGrayscale(backgrounds, page.Width, page.Height, token, callerBytesPerPixel);
        token.ThrowIfCancellationRequested();
        return (gray, gamma);
    }
}
