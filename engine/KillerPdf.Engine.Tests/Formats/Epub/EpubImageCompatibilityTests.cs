using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Images degrade per image: they shrink to fit, harmless metadata is ignored, and an image the
// reader cannot show is omitted with a diagnostic while its chapter keeps laying out.
public sealed class EpubImageCompatibilityTests
{
    [Fact]
    public void LargeCoversShrinkToFitTheContentBoxKeepingTheirAspectRatio()
    {
        // A 1600x2400 Project Gutenberg-style cover in a 396x596 content box (400x600 page, 2px margins).
        var book = new Book { Css = "img{display:block}", Chapters = { Chapter("<div><img src='../images/cover.png' alt='Cover'/></div>") },
            ExtraManifest = "<item id='cover' href='images/cover.png' media-type='image/png' properties='cover-image'/>" };
        book.Resources.Add(("images/cover.png", Png(1600, 2400, colorType: 2, ancillary: ["tEXt", "zTXt", "tIME"])));
        var layout = Layout(book);
        Assert.Empty(layout.ChapterDiagnostics);
        var image = Assert.Single(layout.Pages.SelectMany(page => page.Glyphs.OfType<NativeImagePaint>()));
        // Scale = min(396/1600, 596/2400) = 0.2475, so the cover paints at 396 x 594 CSS pixels.
        Assert.Equal(396, image.Width, 6);
        Assert.Equal(594, image.Height, 6);
    }

    /// <summary>The image is omitted with one diagnostic naming it, and its chapter still lays out.</summary>
    internal static void AssertImageOmitted(EpubLayoutDocument layout, string? reason = null)
    {
        Assert.Empty(layout.ChapterDiagnostics);
        var diagnostic = Assert.Single(layout.ContentDiagnostics);
        if (reason is not null) Assert.Contains(reason, diagnostic.Message);
        Assert.Empty(layout.Pages.SelectMany(page => page.Glyphs.OfType<NativeImagePaint>()));
    }

    internal static byte[] Png(int width, int height, byte colorType, string[]? ancillary = null)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; header[9] = colorType;
        Chunk("IHDR", header);
        if (colorType == 3) Chunk("PLTE", [200, 30, 40, 10, 20, 250]);
        foreach (string type in ancillary ?? []) Chunk(type, Encoding.ASCII.GetBytes("Comment\0ignored"));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            byte[] row = new byte[1 + width * channels];
            for (int x = 0; x < width * channels; x++) row[1 + x] = (byte)(colorType == 3 ? x % 2 : 128);
            for (int y = 0; y < height; y++) zlib.Write(row);
        }
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return output.ToArray();

        void Chunk(string type, byte[] data)
        {
            Span<byte> integer = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length);
            output.Write(integer);
            byte[] name = Encoding.ASCII.GetBytes(type);
            output.Write(name);
            output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
            output.Write(integer);
        }
    }
}
