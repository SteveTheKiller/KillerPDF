using System.Buffers.Binary;

namespace KillerPdf.Engine.Formats.Djvu;

/// <summary>Metadata from a DjVu container. Opening does not decode page images.</summary>
public sealed partial class DjvuDocument
{
    /// <summary>Pages in directory offset order for bundled documents.</summary>
    public IReadOnlyList<DjvuPageInfo> Pages { get; }

    /// <summary>Bounded notices about unavailable features.</summary>
    public IReadOnlyList<DjvuDiagnostic> Diagnostics { get; }

    private readonly byte[] _data;
    private readonly Chunk[] _pageForms;

    private DjvuDocument(List<DjvuPageInfo> pages, List<DjvuDiagnostic> diagnostics, byte[] data, List<Chunk> pageForms)
    {
        Pages = Array.AsReadOnly(pages.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        _data = data;
        _pageForms = pageForms.ToArray();
    }

    /// <summary>Opens metadata with limits of 64 MiB, 65536 chunks, 4096 pages, 100 million pixels per page, and 25 to 6000 DPI.</summary>
    public static DjvuDocument Open(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > 64 * 1024 * 1024) throw new InvalidDataException("DjVu input exceeds the 64 MiB limit.");
        var ownedData = data.ToArray();
        var span = ownedData.AsSpan();
        if (span.Length < 16 || !span[..8].SequenceEqual("AT&TFORM"u8))
            throw new InvalidDataException("Expected an AT&T FORM container.");
        var rootLength = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        if (rootLength < 4 || rootLength > (uint)(span.Length - 12))
            throw new InvalidDataException("FORM length does not match the input.");
        var rootEnd = 12 + (int)rootLength;
        if (rootEnd != span.Length && !((rootEnd & 1) != 0 && rootEnd + 1 == span.Length && span[rootEnd] == 0))
            throw new InvalidDataException("Unexpected bytes after the root FORM.");
        List<DjvuPageInfo> pages = [];
        List<Chunk> pageForms = [];
        List<DjvuDiagnostic> diagnostics = [];
        var chunkBudget = 65_535;
        var chunks = ReadChunks(span, 16, rootEnd, cancellationToken, ref chunkBudget);
        DiagnoseFeatures(chunks, diagnostics);
        if (span[12..16].SequenceEqual("DJVU"u8))
        {
            pages.Add(ReadPage(span, chunks));
            pageForms.Add(new(0x464f524d, 12, (int)rootLength));
        }
        else if (span[12..16].SequenceEqual("DJVM"u8))
        {
            if (chunks.Count == 0 || chunks[0].Id != 0x4449524d || chunks[0].Length < 3)
                throw new InvalidDataException("A multipage document must begin with DIRM.");
            var directory = span.Slice(chunks[0].Offset, chunks[0].Length);
            if ((directory[0] & 0x7f) != 1)
            {
                diagnostics.Add(new("DirectoryVersionUnsupported", "The directory version is unsupported. Its index and component ordering are not interpreted."));
                return new DjvuDocument(pages, diagnostics, ownedData, pageForms);
            }
            if ((directory[0] & 0x80) == 0)
            {
                diagnostics.Add(new("ExternalComponentsUnsupported", "Indirect DjVu component files and URLs are not opened or fetched."));
                return new DjvuDocument(pages, diagnostics, ownedData, pageForms);
            }
            var componentCount = BinaryPrimitives.ReadUInt16BigEndian(directory[1..]);
            if (directory.Length < 3 + componentCount * 4)
                throw new InvalidDataException("Truncated bundled directory offset table.");
            var componentOffsets = chunks.Where(c => c.Id == 0x464f524d).Select(c => (uint)(c.Offset - 8)).ToHashSet();
            List<Chunk> orderedComponents = [];
            for (var i = 0; i < componentCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = BinaryPrimitives.ReadUInt32BigEndian(directory[(3 + i * 4)..]);
                if (!componentOffsets.Remove(offset)) throw new InvalidDataException("Directory offset does not identify a unique component start.");
                orderedComponents.Add(chunks.First(c => c.Id == 0x464f524d && c.Offset - 8 == offset));
            }
            if (componentOffsets.Count != 0) throw new InvalidDataException("Directory does not cover every bundled component.");
            var metadata = ReadDirectoryMetadata(directory[(3 + componentCount * 4)..], orderedComponents, cancellationToken);
            if (metadata is null)
                diagnostics.Add(new("BundledDirectoryIndexUnsupported", "The compressed directory metadata is missing. Pages follow the uncompressed offsets, but component flags, sizes and IDs are not verified."));
            for (var componentIndex = 0; componentIndex < orderedComponents.Count; componentIndex++)
            {
                var chunk = orderedComponents[componentIndex];
                cancellationToken.ThrowIfCancellationRequested();
                if (chunk.Id != 0x464f524d) continue;
                if (chunk.Length < 4) throw new InvalidDataException("Truncated FORM type.");
                var componentType = BinaryPrimitives.ReadUInt32BigEndian(span[chunk.Offset..]);
                if (componentType is not (0x444a5655 or 0x444a5649 or 0x5448554d))
                    throw new InvalidDataException("Unsupported bundled component FORM type.");
                if (metadata is not null && metadata[componentIndex].Kind != (componentType == 0x444a5655 ? 1 : componentType == 0x5448554d ? 2 : 0))
                    throw new InvalidDataException("Directory component kind does not match its FORM type.");
                var children = ReadChunks(span, chunk.Offset + 4, chunk.Offset + chunk.Length, cancellationToken, ref chunkBudget);
                if (children.Any(c => c.Id == 0x464f524d)) throw new InvalidDataException("Nested FORM is not allowed inside a component.");
                if (componentType != 0x444a5655 && children.Any(c => c.Id == 0x494e464f))
                    throw new InvalidDataException("INFO is only allowed inside a page.");
                DiagnoseFeatures(children, diagnostics);
                if (span.Slice(chunk.Offset, 4).SequenceEqual("DJVU"u8))
                {
                    if (pages.Count == 4096) throw new InvalidDataException("DjVu page count exceeds the 4096 page limit.");
                    pages.Add(ReadPage(span, children) with { ComponentId = metadata?[componentIndex].Id });
                    pageForms.Add(chunk);
                }
            }
        }
        else throw new InvalidDataException("Unsupported DjVu FORM type.");
        if (pages.Any(p => p.MajorVersion != 0 || p.MinorVersion > 26))
            diagnostics.Add(new("PageVersionUnsupported", "Page metadata uses a version newer than the November 2005 format reference. Full compatibility is not established."));
        return new DjvuDocument(pages, diagnostics, ownedData, pageForms);
    }

    private static void DiagnoseFeatures(List<Chunk> chunks, List<DjvuDiagnostic> diagnostics)
    {
        foreach (var chunk in chunks)
        {
            DjvuDiagnostic? diagnostic = chunk.Id switch
            {
                0x494e434c => new("IncludedComponentUnsupported", "Included components are not resolved. External files and URLs are never fetched."),
                0x536d6d72 => new("SmmrRenderingLimited", "Only nonstriped bitonal MMR rendering is supported. Other variants and layers require additional decoding."),
                0x42473434 => new("Iw44RenderingLimited", "Only a full-resolution grayscale background in one IW44 chunk is currently rendered. Other variants and layers require additional decoding."),
                0x46473434 or 0x536a627a or 0x4667627a or 0x42476a70 or 0x46476a70 or 0x54483434
                    => new("ImageDecodingUnsupported", "Page image codecs are not decoded by the container reader."),
                _ => null
            };
            if (diagnostic is not null && diagnostics.All(d => d.Code != diagnostic.Code)) diagnostics.Add(diagnostic);
        }
    }

    private static List<Chunk> ReadChunks(ReadOnlySpan<byte> span, int start, int end, CancellationToken cancellationToken, ref int chunkBudget)
    {
        List<Chunk> chunks = [];
        var position = start;
        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunkBudget-- == 0) throw new InvalidDataException("DjVu chunk count exceeds the 65536 chunk limit.");
            if (end - position < 8) throw new InvalidDataException("Truncated chunk header.");
            var id = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(span[(position + 4)..]);
            if (length > (uint)(end - position - 8)) throw new InvalidDataException("Chunk extends beyond its container.");
            chunks.Add(new(id, position + 8, (int)length));
            position += 8 + (int)length;
            if ((position & 1) != 0 && position < end)
            {
                if (span[position] != 0) throw new InvalidDataException("Invalid chunk padding.");
                position++;
            }
        }
        return chunks;
    }

    private static DjvuPageInfo ReadPage(ReadOnlySpan<byte> span, List<Chunk> chunks)
    {
        if (chunks.Any(c => c.Id == 0x464f524d)) throw new InvalidDataException("Nested FORM is not allowed inside a page.");
        if (chunks.Count == 0 || chunks[0].Id != 0x494e464f || chunks[0].Length < 10)
            throw new InvalidDataException("A page must begin with a complete INFO chunk.");
        if (chunks.Skip(1).Any(c => c.Id == 0x494e464f)) throw new InvalidDataException("Duplicate INFO chunk.");
        var info = span.Slice(chunks[0].Offset, chunks[0].Length);
        var width = BinaryPrimitives.ReadUInt16BigEndian(info);
        var height = BinaryPrimitives.ReadUInt16BigEndian(info[2..]);
        var dpi = BinaryPrimitives.ReadUInt16LittleEndian(info[6..]);
        if (width == 0 || height == 0 || (long)width * height > 100_000_000 || dpi is < 25 or > 6000)
            throw new InvalidDataException("Page dimensions or resolution exceed supported limits.");
        return new DjvuPageInfo(
            width, height, dpi, info[4], info[5], (info[9] & 7) switch
            {
                1 => DjvuOrientation.Upright,
                2 => DjvuOrientation.UpsideDown,
                5 => DjvuOrientation.Clockwise90,
                6 => DjvuOrientation.CounterClockwise90,
                _ => throw new InvalidDataException("Unrecognized page orientation.")
            });
    }

    private readonly record struct Chunk(uint Id, int Offset, int Length);
}

/// <summary>A stable feature code and a bounded explanation.</summary>
public sealed record DjvuDiagnostic(string Code, string Message);

/// <summary>Stored page information from an INFO chunk.</summary>
public sealed record DjvuPageInfo(int Width, int Height, int Dpi, byte MinorVersion, byte MajorVersion, DjvuOrientation Orientation)
{
    /// <summary>The bundled directory component ID, or null when it is unavailable.</summary>
    public string? ComponentId { get; init; }
}

/// <summary>Clockwise page rotation in degrees.</summary>
public enum DjvuOrientation
{
    /// <summary>Unrotated.</summary>
    Upright = 0,
    /// <summary>Clockwise quarter turn.</summary>
    Clockwise90 = 90,
    /// <summary>Half turn.</summary>
    UpsideDown = 180,
    /// <summary>Counterclockwise quarter turn.</summary>
    CounterClockwise90 = 270
}
