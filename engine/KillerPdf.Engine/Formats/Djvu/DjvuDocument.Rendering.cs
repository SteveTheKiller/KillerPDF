using System.Buffers.Binary;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Renders supported bitonal content at natural dimensions, limited to a 128 MiB RGBA32F frame.</summary>
    public PdfRenderedFrame RenderPage(int pageIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Pages[pageIndex];
        if (page.MajorVersion != 0 || page.MinorVersion > 26) throw new NotSupportedException("Rendering this DjVu page version is not supported.");
        if ((long)page.Width * page.Height > 128L * 1024 * 1024 / (4 * sizeof(float)))
            throw new InvalidDataException("The DjVu frame exceeds the 128 MiB storage limit.");
        var form = _pageForms[pageIndex];
        var budget = 65_535;
        var chunks = ReadChunks(_data, form.Offset + 4, form.Offset + form.Length, cancellationToken, ref budget);
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
        var pixels = new float[page.Width * page.Height * 4];
        var quarterTurn = page.Orientation is DjvuOrientation.Clockwise90 or DjvuOrientation.CounterClockwise90;
        var width = quarterTurn ? page.Height : page.Width;
        var height = quarterTurn ? page.Width : page.Height;
        var stride = (page.Width + 7) / 8;
        for (var y = 0; y < page.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < page.Width; x++)
            {
                var (targetX, targetY) = page.Orientation switch
                {
                    DjvuOrientation.Clockwise90 => (page.Height - 1 - y, x),
                    DjvuOrientation.CounterClockwise90 => (y, page.Width - 1 - x),
                    DjvuOrientation.UpsideDown => (page.Width - 1 - x, page.Height - 1 - y),
                    _ => (x, y)
                };
                var offset = (targetY * width + targetX) * 4;
                var value = (packed[y * stride + x / 8] & (1 << (7 - x % 8))) == 0 ? 1f : 0f;
                if ((payload[3] & 1) != 0) value = 1 - value;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                pixels[offset + 3] = 1;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var frame = new PdfRenderedFrame(width, height, pixels, PdfFrameColorEncoding.LinearSrgbD65);
        cancellationToken.ThrowIfCancellationRequested();
        return frame;
    }
}
