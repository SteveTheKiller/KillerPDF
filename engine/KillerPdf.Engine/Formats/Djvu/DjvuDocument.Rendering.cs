using System.Buffers.Binary;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Renders bitonal, grayscale, color and compound pages at natural dimensions, limited to a 128 MiB RGBA32F frame.</summary>
    /// <remarks>
    /// Masks are Smmr or JB2 (with shared dictionaries inside a bundle); backgrounds and foreground images are
    /// version 1.2 IW44 in any number of chunks, at page size or a subsample of 1 to 12; foreground colors come from
    /// FGbz or FG44. The stored INFO device gamma, clamped to 0.3 through 5.0 like DjVuLibre, maps samples to linear
    /// RGB. IW44 working buffers are limited to 256 MiB and JB2 symbols to 32 MiB.
    /// </remarks>
    public PdfRenderedFrame RenderPage(int pageIndex, CancellationToken cancellationToken = default)
    {
        var (page, chunks) = ReadSupportedPage(pageIndex, cancellationToken);
        return RenderSupportedPage(page, chunks, cancellationToken);
    }

    /// <summary>
    /// Decodes a page into the form it was coded in: Smmr or JB2 masks as one bit per pixel, grayscale IW44 as one byte
    /// and color IW44 as three, compound pages as their mask over layers at their stored sizes, at natural size and in
    /// stored orientation, with the page's turn applied when it is read.
    /// </summary>
    /// <remarks>
    /// A color photo page decodes at <paramref name="subsample"/> (from <see cref="DecodeSubsample"/>): IW44's own
    /// samples at that wavelet scale, each covering subsample page pixels across and down.
    /// </remarks>
    internal PdfNativeCompactRaster DecodePage(int pageIndex, CancellationToken cancellationToken, int subsample = 1)
    {
        var (page, chunks) = ReadPageChunks(pageIndex, cancellationToken);
        return DecodeSupportedPage(page, chunks, cancellationToken, callerBytesPerPixel: 0,
            subsample: IsColorPhotoPage(page, chunks) ? subsample : 1);
    }

    /// <summary>
    /// The subsample a page decodes at to be drawn at <paramref name="width"/> by <paramref name="height"/> pixels in
    /// stored orientation: for a color photo page, the coarsest of 8, 4 and 2 whose samples still number at least the
    /// output's in both directions, so the drawing only ever reduces; 1 otherwise, and for every other page.
    /// </summary>
    internal int DecodeSubsample(int pageIndex, int width, int height, CancellationToken cancellationToken)
    {
        var (page, chunks) = ReadPageChunks(pageIndex, cancellationToken);
        if (!IsColorPhotoPage(page, chunks)) return 1;
        foreach (int subsample in (ReadOnlySpan<int>)[8, 4, 2])
            if ((page.Width + subsample - 1) / subsample >= width && (page.Height + subsample - 1) / subsample >= height)
                return subsample;
        return 1;
    }

    // A full-size color IW44 background with no mask or foreground: the page compound composition does not cover.
    private bool IsColorPhotoPage(DjvuPageInfo page, List<Chunk> chunks)
    {
        var background = chunks.FirstOrDefault(c => c.Id == Bg44Id);
        return background.Id != 0 && Classify(page, chunks) == PageLayers.Other
            && Iw44Header(background) is { Channels: 3 } header && header.Width == page.Width && header.Height == page.Height;
    }

    /// <summary>
    /// The bytes <see cref="DecodePage"/> keeps for a page, and the most it holds at once while decoding, found from
    /// the page's chunks without decoding it. Smmr decodes straight into the kept bits; IW44 needs its coefficients.
    /// </summary>
    internal (long Kept, long Decoding) DecodedPageBytes(int pageIndex, CancellationToken cancellationToken, int subsample = 1)
    {
        var (page, chunks) = ReadPageChunks(pageIndex, cancellationToken);
        switch (Classify(page, chunks))
        {
            case PageLayers.Compound: return CompoundPageBytes(page, chunks, cancellationToken);
            case PageLayers.Jb2Mask:
            {
                long mask = PdfNativeCompactRaster.StorageBytes(PdfNativeSampleLayout.Bits, page.Width, page.Height);
                return (mask, mask + Jb2WorkingBytes(chunks, cancellationToken));
            }
        }
        var backgrounds = chunks.Where(c => c.Id == 0x42473434).ToList();
        if (backgrounds.Count == 0)
        {
            long bits = PdfNativeCompactRaster.StorageBytes(PdfNativeSampleLayout.Bits, page.Width, page.Height);
            return (bits, bits);
        }
        if (backgrounds[0].Length >= 3 && (_data[backgrounds[0].Offset + 2] & 0x80) == 0)
        {
            if (!IsColorPhotoPage(page, chunks)) subsample = 1;
            return (PdfNativeCompactRaster.StorageBytes(PdfNativeSampleLayout.Rgb, (page.Width + subsample - 1) / subsample,
                    (page.Height + subsample - 1) / subsample),
                DjvuIw44Decoder.ColorWorkingBytes(page.Width, page.Height, backgrounds.Sum(c => (long)c.Length),
                    callerBytesPerPixel: 0, subsample));
        }
        return (PdfNativeCompactRaster.StorageBytes(PdfNativeSampleLayout.Gray, page.Width, page.Height),
            DjvuIw44Decoder.GrayscaleWorkingBytes(page.Width, page.Height, backgrounds.Sum(c => (long)c.Length),
                backgrounds.Count, callerBytesPerPixel: 0));
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
        int callerBytesPerPixel = 32, int subsample = 1)
    {
        chunks = WithoutAnnotations(chunks);
        switch (Classify(page, chunks))
        {
            case PageLayers.Compound: return DecodeCompoundRaster(page, chunks, cancellationToken);
            case PageLayers.Jb2Mask: return DecodeJb2Raster(page, chunks, cancellationToken);
        }
        if (chunks.Any(c => c.Id == 0x42473434))
            return DecodeIw44Raster(page, chunks, cancellationToken, callerBytesPerPixel, subsample);
        if (chunks.Any(c => c.Id is not (0x494e464f or 0x536d6d72 or 0x54585461 or 0x5458547a)))
            throw new NotSupportedException("Rendering this page requires unsupported layers, annotations or component content.");
        if (chunks.Count(c => c.Id == 0x536d6d72) > 1) throw new InvalidDataException("A page contains duplicate Smmr masks.");
        var mask = chunks.FirstOrDefault(c => c.Id == 0x536d6d72);
        if (mask.Id == 0) throw new NotSupportedException("This page has no supported Smmr image content.");
        var (packed, inverted) = DecodeSmmr(page, mask, cancellationToken);
        // CCITT white is a zero bit; the MMR invert flag swaps the two values.
        float[] linear = inverted ? [0f, 1f] : [1f, 0f];
        return new PdfNativeCompactRaster(PdfNativeSampleLayout.Bits, page.Width, page.Height, packed, linear,
            (int)page.Orientation);
    }

    // ANTa and ANTz hold view settings and overlaid links and highlights (8.2, Table 1), not page pixels; DjVuLibre
    // draws a page the same with or without them.
    private static List<Chunk> WithoutAnnotations(List<Chunk> chunks)
        => chunks.Where(c => c.Id is not (0x414e5461 or 0x414e547a)).ToList();

    private (byte[] Bits, bool Inverted) DecodeSmmr(DjvuPageInfo page, Chunk mask, CancellationToken cancellationToken)
    {
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
        return (packed, (payload[3] & 1) != 0);
    }

    private PdfNativeCompactRaster DecodeIw44Raster(DjvuPageInfo page, List<Chunk> chunks, CancellationToken token,
        int callerBytesPerPixel, int subsample = 1)
    {
        var firstBackground = chunks.First(c => c.Id == 0x42473434);
        if (firstBackground.Length >= 3 && (_data[firstBackground.Offset + 2] & 0x80) == 0)
            return DecodeColorIw44Raster(page, chunks, token, callerBytesPerPixel, subsample);
        var (gray, gamma) = DecodeIw44Page(page, chunks, token, callerBytesPerPixel);
        return new PdfNativeCompactRaster(PdfNativeSampleLayout.Gray, page.Width, page.Height, gray, LinearTable(gamma),
            (int)page.Orientation);
    }

    // Every BG44 chunk refines the image; the page turn applies when the RGB bytes are read, as for gray pages. At a
    // subsample the raster holds IW44's samples at that scale and covers the page through its reduction.
    private PdfNativeCompactRaster DecodeColorIw44Raster(DjvuPageInfo page, List<Chunk> chunks, CancellationToken token,
        int callerBytesPerPixel, int subsample = 1)
    {
        token.ThrowIfCancellationRequested();
        if (chunks.Any(c => c.Id is not (0x494e464f or 0x42473434 or 0x54585461 or 0x5458547a)))
            throw new NotSupportedException("Color IW44 rendering requires a background without other visual layers.");
        var gamma = InfoGamma(chunks);
        var backgrounds = chunks.Where(c => c.Id == 0x42473434).Take(257)
            .Select(c => (ReadOnlyMemory<byte>)_data.AsMemory(c.Offset, c.Length)).ToArray();
        var rgb = DjvuIw44Decoder.DecodeColor(backgrounds, page.Width, page.Height, token, callerBytesPerPixel, subsample);
        token.ThrowIfCancellationRequested();
        return new PdfNativeCompactRaster(PdfNativeSampleLayout.Rgb, (page.Width + subsample - 1) / subsample,
            (page.Height + subsample - 1) / subsample, rgb, LinearTable(gamma), (int)page.Orientation, subsample,
            page.Width, page.Height);
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
