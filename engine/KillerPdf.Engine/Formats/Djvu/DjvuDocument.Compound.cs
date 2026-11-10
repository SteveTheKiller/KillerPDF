using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    private const uint InfoId = 0x494e464f, SjbzId = 0x536a627a, SmmrId = 0x536d6d72, DjbzId = 0x446a627a,
        InclId = 0x494e434c, Fg44Id = 0x46473434, FgbzId = 0x4647627a, Bg44Id = 0x42473434, TxtaId = 0x54585461,
        TxtzId = 0x5458547a;

    private enum PageLayers { Other, Jb2Mask, Compound }

    // A compound page (7.1.3) has a background: with a mask or foreground colors, or stored below page size, it is
    // composed here. Without one a JB2 mask is a bilevel image, black on white, whatever colors it carries, as the
    // reference decoder draws it. A lone Smmr mask and a lone full-size background keep their own paths.
    private PageLayers Classify(DjvuPageInfo page, List<Chunk> chunks)
    {
        bool jb2 = chunks.Any(c => c.Id == SjbzId), smmr = chunks.Any(c => c.Id == SmmrId);
        bool colors = chunks.Any(c => c.Id is Fg44Id or FgbzId);
        var background = chunks.FirstOrDefault(c => c.Id == Bg44Id);
        if (background.Id == 0) return jb2 ? PageLayers.Jb2Mask : PageLayers.Other;
        bool reduced = Iw44Header(background) is { } size && (size.Width != page.Width || size.Height != page.Height)
            && Enumerable.Range(2, 11).Any(r => (page.Width + r - 1) / r == size.Width && (page.Height + r - 1) / r == size.Height);
        return jb2 || smmr || colors || reduced ? PageLayers.Compound : PageLayers.Other;
    }

    // The width, height and channel count of an IW44 image's initial chunk, or null when it is not one.
    private (int Width, int Height, int Channels)? Iw44Header(Chunk chunk)
    {
        if (chunk.Length < 9 || _data[chunk.Offset] != 0) return null;
        return (BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(chunk.Offset + 4)),
            BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(chunk.Offset + 6)),
            (_data[chunk.Offset + 2] & 0x80) != 0 ? 1 : 3);
    }

    // A JB2 mask alone draws black on white, kept as one bit per pixel like Smmr.
    private PdfNativeCompactRaster DecodeJb2Raster(DjvuPageInfo page, List<Chunk> chunks, CancellationToken token)
    {
        if (chunks.Any(c => c.Id is not (InfoId or SjbzId or DjbzId or InclId or Fg44Id or FgbzId or TxtaId or TxtzId)))
            throw new NotSupportedException("Rendering this page requires unsupported layers, annotations or component content.");
        if (chunks.Count(c => c.Id == Fg44Id) > 1 || chunks.Count(c => c.Id == FgbzId) > 1)
            throw new InvalidDataException("A page contains duplicate foreground chunks.");
        var bits = DecodeJb2Mask(page, chunks, null, token);
        return new PdfNativeCompactRaster(PdfNativeSampleLayout.Bits, page.Width, page.Height, bits.Bits, [1f, 0f],
            (int)page.Orientation);
    }

    private DjvuJb2Bitmap DecodeJb2Mask(DjvuPageInfo page, List<Chunk> chunks, Action<DjvuJb2Bitmap, int, int>? blit,
        CancellationToken token)
    {
        if (chunks.Count(c => c.Id == SjbzId) > 1) throw new InvalidDataException("A page contains duplicate Sjbz masks.");
        var mask = chunks.First(c => c.Id == SjbzId);
        var budget = new DjvuJb2Budget((long)page.Width * page.Height);
        var bits = DjvuJb2Decoder.DecodeImage(_data.AsSpan(mask.Offset, mask.Length).ToArray(), page.Width, page.Height,
            () => SharedDictionary(chunks, budget, [], token), budget, blit, token);
        token.ThrowIfCancellationRequested();
        return bits;
    }

    // 7.1.3: the mask selects the foreground (per-shape FGbz colors, else the FG44 image, else black) over the
    // background (BG44, else white). Every layer keeps its stored size and the page turn applies when it is read.
    private PdfNativeCompactRaster DecodeCompoundRaster(DjvuPageInfo page, List<Chunk> chunks, CancellationToken token)
    {
        if (chunks.Any(c => c.Id is not (InfoId or SjbzId or SmmrId or DjbzId or InclId or Fg44Id or FgbzId or Bg44Id
                or TxtaId or TxtzId)))
            throw new NotSupportedException("Rendering this page requires unsupported layers, annotations or component content.");
        if (chunks.Count(c => c.Id is SjbzId or SmmrId) > 1) throw new InvalidDataException("A page contains more than one mask.");
        if (chunks.Count(c => c.Id == Fg44Id) > 1 || chunks.Count(c => c.Id == FgbzId) > 1)
            throw new InvalidDataException("A page contains duplicate foreground chunks.");
        var linear = LinearTable(InfoGamma(chunks));
        var background = DecodeLayer(page, chunks.Where(c => c.Id == Bg44Id).ToList(), token);
        var colors = chunks.FirstOrDefault(c => c.Id == FgbzId);
        var foreground = colors.Id == 0 ? DecodeLayer(page, chunks.Where(c => c.Id == Fg44Id).ToList(), token) : null;
        var palette = colors.Id == 0 ? null : ReadPalette(colors, token);
        int indexBits = palette is null ? 0 : IndexBits(palette.Used.Length);
        var indexes = indexBits == 0 ? null
            : new byte[PdfNativeCompoundLayers.IndexBytes(page.Width, page.Height, indexBits)];
        byte[] colorTable = palette is null ? [0, 0, 0] : palette.Used.Length == 0 ? [0, 0, 0]
            : palette.Used.SelectMany(i => palette.Colors.AsSpan(i * 3, 3).ToArray()).ToArray();
        var layers = new PdfNativeCompoundLayers(background, foreground, colorTable, indexes, indexBits, page.Width);
        byte[] mask;
        if (chunks.Any(c => c.Id == SjbzId))
        {
            int blits = 0;
            Action<DjvuJb2Bitmap, int, int>? paint = palette?.Blits is null ? null : (shape, left, top) =>
            {
                var blitColors = palette.Blits;
                if (blits >= blitColors.Length) throw new InvalidDataException("The page places more shapes than FGbz colors.");
                int value = palette.Dense[blitColors[blits++]];
                if (indexes is null) return;
                for (int y = Math.Max(0, -top); y < shape.Height && top + y < page.Height; y++)
                    for (int x = Math.Max(0, -left); x < shape.Width && left + x < page.Width; x++)
                        if (shape[x, y]) layers.SetIndex(left + x, top + y, value);
            };
            mask = DecodeJb2Mask(page, chunks, paint, token).Bits;
            if (palette?.Blits is { } all && blits != all.Length)
                throw new InvalidDataException("The FGbz colors do not match the shapes on the page.");
        }
        else if (chunks.FirstOrDefault(c => c.Id == SmmrId) is { Id: not 0 } smmr)
        {
            if (palette is not null) throw new NotSupportedException("Per-shape FGbz colors require a JB2 mask.");
            var (bits, inverted) = DecodeSmmr(page, smmr, token);
            // The mask's set bits are foreground; the MMR invert flag swaps black and white.
            if (inverted) for (int i = 0; i < bits.Length; i++) bits[i] = (byte)~bits[i];
            mask = bits;
        }
        else mask = new byte[(long)(page.Width + 7) / 8 * page.Height];
        return new PdfNativeCompactRaster(page.Width, page.Height, mask, linear, (int)page.Orientation, layers);
    }

    // An IW44 layer (BG44 chunks, or the single FG44 chunk) at its stored size, whose page-size quotient by a whole
    // subsample factor of 1 through 12, rounded up, must give that size.
    private PdfNativeImageLayer? DecodeLayer(DjvuPageInfo page, List<Chunk> layer, CancellationToken token)
    {
        if (layer.Count == 0) return null;
        var (width, height, channels) = Iw44Header(layer[0])
            ?? throw new InvalidDataException("An IW44 layer must begin with an initial chunk.");
        int reduction = Reduction(page, width, height);
        var payloads = layer.Take(257).Select(c => (ReadOnlyMemory<byte>)_data.AsMemory(c.Offset, c.Length)).ToArray();
        byte[] samples = channels == 1
            ? DjvuIw44Decoder.DecodeGrayscale(payloads, width, height, token, callerBytesPerPixel: 0)
            : DjvuIw44Decoder.DecodeColor(payloads, width, height, token, callerBytesPerPixel: 0);
        token.ThrowIfCancellationRequested();
        return new PdfNativeImageLayer(samples, width, height, channels, reduction, page.Width, page.Height);
    }

    private static int Reduction(DjvuPageInfo page, int width, int height)
    {
        for (int reduction = 1; reduction <= 12; reduction++)
            if ((page.Width + reduction - 1) / reduction == width && (page.Height + reduction - 1) / reduction == height)
                return reduction;
        throw new InvalidDataException("An IW44 layer's size is not the page size divided by a subsample factor of 1 to 12.");
    }

    private static int IndexBits(int used) => used <= 1 ? 0 : used <= 2 ? 1 : used <= 4 ? 2 : used <= 16 ? 4 : used <= 256 ? 8 : 16;

    // 8.3.10: a version byte (high bit: per-blit indices follow), a 16-bit palette size, BGR palette entries, then a
    // 24-bit blit count and that many 16-bit palette indices in a BZZ stream.
    private sealed record FgbzPalette(byte[] Colors, int[]? Blits, int[] Used, int[] Dense);

    private FgbzPalette ReadPalette(Chunk chunk, CancellationToken token)
    {
        var data = _data.AsSpan(chunk.Offset, chunk.Length);
        if (data.Length < 3 || (data[0] & 0x7f) != 0) throw new InvalidDataException("Unsupported FGbz version.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
        if (count == 0 || data.Length < 3 + 3 * count) throw new InvalidDataException("Truncated FGbz palette.");
        var colors = new byte[3 * count];
        for (int i = 0; i < count; i++)
        {
            colors[3 * i] = data[3 + 3 * i + 2];
            colors[3 * i + 1] = data[3 + 3 * i + 1];
            colors[3 * i + 2] = data[3 + 3 * i];
        }
        int[]? blits = null;
        if ((data[0] & 0x80) != 0)
        {
            var rest = data[(3 + 3 * count)..];
            if (rest.Length < 3) throw new InvalidDataException("Truncated FGbz blit colors.");
            int blitCount = (rest[0] << 16) | (rest[1] << 8) | rest[2];
            // No page can place more shapes than the JB2 record limit allows.
            if (blitCount > 4_000_000) throw new InvalidDataException("FGbz colors exceed the shape limit.");
            blits = new int[blitCount];
            if (blitCount > 0)
            {
                var indexes = DjvuBzzDecoder.Decode(rest[3..], 2 * blitCount, 2 * blitCount + 1, 64, token);
                if (indexes.Length != 2 * blitCount) throw new InvalidDataException("FGbz colors do not match their count.");
                for (int i = 0; i < blitCount; i++)
                {
                    blits[i] = BinaryPrimitives.ReadUInt16BigEndian(indexes.AsSpan(2 * i));
                    if (blits[i] >= count) throw new InvalidDataException("An FGbz color index is outside the palette.");
                }
            }
        }
        // The plane stores each used color's rank, so a page with few colors needs few bits per pixel.
        var used = blits is null ? [0] : blits.Distinct().Order().ToArray();
        var dense = new int[count];
        for (int i = 0; i < used.Length; i++) dense[used[i]] = i;
        return new FgbzPalette(colors, blits, used, dense);
    }

    // What DecodeCompoundRaster keeps, and the most it holds at once: the background decode, then the foreground
    // image decode beside the kept background, then the mask decode beside both kept layers.
    private (long Kept, long Decoding) CompoundPageBytes(DjvuPageInfo page, List<Chunk> chunks, CancellationToken token)
    {
        long mask = PdfNativeCompactRaster.StorageBytes(PdfNativeSampleLayout.Bits, page.Width, page.Height);
        var (backgroundKept, backgroundDecoding) = LayerBytes(page, chunks.Where(c => c.Id == Bg44Id).ToList());
        var colors = chunks.FirstOrDefault(c => c.Id == FgbzId);
        var (foregroundKept, foregroundDecoding) = colors.Id == 0
            ? LayerBytes(page, chunks.Where(c => c.Id == Fg44Id).ToList()) : (0, 0);
        long plane = 0, colorsDecoding = 0;
        if (colors.Id != 0)
        {
            var palette = ReadPalette(colors, token);
            plane = PdfNativeCompoundLayers.IndexBytes(page.Width, page.Height, IndexBits(palette.Used.Length))
                + 3L * palette.Used.Length;
            // The BZZ output, the per-blit and per-color tables.
            colorsDecoding = 2L * (palette.Blits?.Length ?? 0) + 4L * (palette.Blits?.Length ?? 0) + 8L * palette.Dense.Length;
        }
        long kept = mask + backgroundKept + foregroundKept + plane;
        long maskDecoding = chunks.Any(c => c.Id == SjbzId) ? Jb2WorkingBytes(chunks, token) : 0;
        long decoding = Math.Max(backgroundDecoding, Math.Max(backgroundKept + foregroundDecoding,
            kept + colorsDecoding + maskDecoding));
        return (kept, decoding);
    }

    private (long Kept, long Decoding) LayerBytes(DjvuPageInfo page, List<Chunk> layer)
    {
        if (layer.Count == 0) return (0, 0);
        var (width, height, channels) = Iw44Header(layer[0])
            ?? throw new InvalidDataException("An IW44 layer must begin with an initial chunk.");
        long kept = PdfNativeImageLayer.StorageBytes(width, height, channels, page.Width, page.Height);
        long payload = layer.Sum(c => (long)c.Length);
        long decoding = channels == 1
            ? DjvuIw44Decoder.GrayscaleWorkingBytes(width, height, payload, layer.Count, callerBytesPerPixel: 0)
            : DjvuIw44Decoder.ColorWorkingBytes(width, height, payload, callerBytesPerPixel: 0);
        return (kept, Math.Max(decoding, kept));
    }

    // A JB2 decode holds at most the symbol storage limit, its number contexts and copies of the coded chunks.
    private long Jb2WorkingBytes(List<Chunk> chunks, CancellationToken token)
    {
        long coded = chunks.Where(c => c.Id is SjbzId or DjbzId).Sum(c => (long)c.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<List<Chunk>>([chunks]);
        while (pending.Count > 0)
        {
            foreach (var include in pending.Dequeue().Where(c => c.Id == InclId))
            {
                string id = IncludeId(include);
                if (seen.Count == 8 || !seen.Add(id) || !_includes.TryGetValue(id, out var component)) continue;
                var budgetChunks = 65_535;
                var children = ReadChunks(_data, component.Offset + 4, component.Offset + component.Length, token, ref budgetChunks);
                coded += children.Where(c => c.Id == DjbzId).Sum(c => (long)c.Length);
                pending.Enqueue(children);
            }
        }
        return DjvuJb2Budget.StorageLimit + DjvuJb2Decoder.ContextBytes + coded;
    }

    private string IncludeId(Chunk include)
        => Encoding.UTF8.GetString(_data, include.Offset, include.Length).TrimEnd('\0', ' ', '\n', '\r', '\t');

    // The shape dictionary a page or component provides: its own Djbz chunk, or else the first one its INCL chunks
    // lead to inside this bundle. A dictionary may itself require a dictionary its own component includes.
    private DjvuJb2Dictionary? SharedDictionary(List<Chunk> chunks, DjvuJb2Budget budget, HashSet<string> visiting,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var own = chunks.Where(c => c.Id == DjbzId).ToList();
        if (own.Count > 1) throw new InvalidDataException("A component contains duplicate Djbz dictionaries.");
        if (own.Count == 1)
            return DjvuJb2Decoder.DecodeDictionary(_data.AsSpan(own[0].Offset, own[0].Length).ToArray(),
                () => Included(chunks, budget, visiting, token), budget, token);
        return Included(chunks, budget, visiting, token);
    }

    private DjvuJb2Dictionary? Included(List<Chunk> chunks, DjvuJb2Budget budget, HashSet<string> visiting,
        CancellationToken token)
    {
        foreach (var include in chunks.Where(c => c.Id == InclId))
        {
            string id = IncludeId(include);
            if (!_includes.TryGetValue(id, out var component))
                throw new InvalidDataException("An included component is not part of this bundled document.");
            if (visiting.Count == 8 || !visiting.Add(id))
                throw new InvalidDataException("Included components nest too deeply or include themselves.");
            var budgetChunks = 65_535;
            var children = ReadChunks(_data, component.Offset + 4, component.Offset + component.Length, token, ref budgetChunks);
            var found = SharedDictionary(children, budget, visiting, token);
            visiting.Remove(id);
            if (found is not null) return found;
        }
        return null;
    }
}
