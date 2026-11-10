using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuCompoundTests
{
    // An authored 96x64 page: light gray paper (200, 200, 200), red (220, 30, 20) and blue (20, 40, 210) glyphs and a
    // black bar. DjVuLibre cpaldjvu coded its mask (Sjbz) and per-shape colors (FGbz); the background is c44's 8x6
    // grayscale IW44 of value 200 (subsample 12) in three chunks. ddjvu renders this page to exactly the authored image.
    private const string PaletteOverGray =
        "41542654464F524D0000008E444A5655494E464F0000000A0060004018002C011601536A627A00000020817B4FC3A17032BCDF26753F5D125A81" +
        "4BB297F70B21C3F4061A686F5971159F4647627A0000001C800004000000D22814141EDCC8C8C8000007FFFFF081ED9BA2CB83BF4247343400" +
        "00000E004A81020008000600963C2AFD7F4247343400000003010FBF004247343400000002020A";

    internal const string PaletteOverGrayHex = PaletteOverGray;

    internal static readonly (byte R, byte G, byte B) Paper = (200, 200, 200), Red = (220, 30, 20), Blue = (20, 40, 210), Black = (0, 0, 0);
    private static readonly string[] Glyph = ["..###..", ".#...#.", "#.....#", "#.....#", "#######", "#.....#", "#.....#", "#.....#", "#.....#"];

    internal static (byte R, byte G, byte B)[,] AuthoredColors()
    {
        var page = new (byte, byte, byte)[64, 96];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 96; x++) page[y, x] = Paper;
        foreach (var (color, x0, y0) in new[] { (Red, 4, 4), (Red, 14, 4), (Blue, 24, 4), (Blue, 34, 4), (Red, 4, 30), (Blue, 60, 40) })
            for (int dy = 0; dy < Glyph.Length; dy++)
                for (int dx = 0; dx < Glyph[dy].Length; dx++)
                    if (Glyph[dy][dx] == '#') page[y0 + dy, x0 + dx] = color;
        for (int y = 56; y < 60; y++) for (int x = 8; x < 88; x++) page[y, x] = Black;
        return page;
    }

    [Fact]
    public void ACompoundPageDrawsEachShapeInItsPaletteColorOverTheBackground()
    {
        var frame = DjvuDocument.Open(Convert.FromHexString(PaletteOverGray)).RenderPage(0);
        AssertColors(AuthoredColors(), frame, 1);
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)5)]
    [InlineData((byte)6)]
    public void ACompoundPageTurnsWithItsOrientation(byte orientation)
    {
        var frame = DjvuDocument.Open(Oriented(orientation)).RenderPage(0);
        AssertColors(Turned(AuthoredColors(), orientation), frame, 1);
    }

    [Theory]
    [InlineData((byte)1, 3.0)]
    [InlineData((byte)2, 3.0)]
    [InlineData((byte)5, 3.0)]
    [InlineData((byte)6, 0.7)]
    [InlineData((byte)1, 0.45)]
    public void ACompoundRegionIsTheCropOfTheWholeResizedPage(byte orientation, double scale)
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Oriented(orientation));
        bool turned = orientation is 5 or 6;
        int width = (int)Math.Round((turned ? 64 : 96) * scale), height = (int)Math.Round((turned ? 96 : 64) * scale);
        var options = new PdfRenderOptions(width, height);
        var whole = snapshot.RenderPage(0, options).Pixels.ToArray();
        int left = width / 5, top = height / 3, regionWidth = width / 2, regionHeight = height / 2;
        var region = snapshot.RenderPageRegion(0, options, left, top, regionWidth, regionHeight);
        Assert.Equal(regionWidth, region.Width);
        Assert.Equal(regionHeight, region.Height);
        var pixels = region.Pixels.ToArray();
        for (int y = 0; y < regionHeight; y++)
            for (int x = 0; x < regionWidth; x++)
                for (int c = 0; c < 4; c++)
                    Assert.Equal(whole[((top + y) * width + left + x) * 4 + c], pixels[(y * regionWidth + x) * 4 + c]);
        // The resize itself shows the page: red strokes, blended with the paper when reduced, survive at every scale.
        Assert.Contains(Enumerable.Range(0, width * height), i => whole[i * 4] - whole[i * 4 + 1] > 0.1f);
    }

    // A 600 dpi letter page, 5100x6600: a JB2 mask of 24 glyphs in rows (a scratch test-stream writer coded it) and a
    // 4000x8 bar of non-symbol data, FGbz colors black and red (220, 30, 20) per shape, and a 1700x2200 grayscale BG44
    // (subsample 3) with no coded slices, which decodes to gray 127. ddjvu draws the glyph at (2100, 300) and the bar
    // at (500, 6000) red, the glyph at (300, 300) black and the paper 127.
    private const string Compound600Dpi =
        "41542654464F524D00000086444A5655494E464F0000000A13EC19C8180058021601536A627A00000036800704BD09322217032BCDF26753F5D09B" +
        "BA1A3D20FD107A91E8BFFFFFECD3944C0FFF278652C8BAF518F5C9FA86DC1003E98FCDC8BF4647627A00000017800002000000141EDC000019FF" +
        "FFCCDFF00530942DB9260042473434000000090000810206A40898FF00";

    [Fact]
    public void A600DpiCompoundPageDrawsAThumbnailAFittedPageAndAZoomedRegionInsideTheViewerBudget()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Convert.FromHexString(Compound600Dpi));
        // Kept: the mask (4.2 MB), the 1-bit color plane (4.2 MB) and the gray background (3.9 MB with its tables).
        // A fitted page adds 1583 x 2048 x 32 bytes (103.7 MB), 116 MB in all, under 128 MiB. Expanding the page to
        // RGB bytes (101 MB) or a float frame (538 MB) would not fit, and neither would its allocation.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var thumbnail = snapshot.RenderPage(0, new PdfRenderOptions(396, 512, maximumPixelBytes: 128L << 20));
        long thumbnailBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(Level(thumbnail.Pixels[0]), 126, 128);
        before = GC.GetAllocatedBytesForCurrentThread();
        snapshot.RenderPage(0, new PdfRenderOptions(1583, 2048, maximumPixelBytes: 128L << 20));
        long fitBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(thumbnailBytes < 64L << 20, $"thumbnail allocated {thumbnailBytes >> 20} MiB");
        Assert.True(fitBytes < 160L << 20, $"fitted page allocated {fitBytes >> 20} MiB");
        // At natural size, the solid fifth rows (y 304) of the black glyph at x 300 and the red glyph at x 2100, with
        // paper beside them.
        var natural = new PdfRenderOptions(5100, 6600, maximumPixelBytes: 512L << 20);
        var left = snapshot.RenderPageRegion(0, natural, 296, 304, 8, 1).Pixels.ToArray();
        Assert.InRange(Level(left[4 * 4]), 0, 0.01);
        Assert.InRange(Level(left[3 * 4]), 126, 128);
        var right = snapshot.RenderPageRegion(0, natural, 2100, 304, 1, 1).Pixels.ToArray();
        Assert.Equal(220, Level(right[0]), 1.0);
        Assert.Equal(30, Level(right[1]), 1.0);
        Assert.Equal(20, Level(right[2]), 1.0);
        // 800% at 96 px per inch (6528 x 8448), a 2560 x 1400 tile over the red bar (rows 6000 to 6007 from x 500):
        // output (1280, 7685) reads only bar pixels.
        before = GC.GetAllocatedBytesForCurrentThread();
        var tile = snapshot.RenderPageRegion(0, new PdfRenderOptions(6528, 8448, maximumPixelBytes: 512L << 20),
            100, 7000, 2560, 1400);
        long tileBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        int at = ((7685 - 7000) * 2560 + 1280 - 100) * 4;
        Assert.Equal(220, Level(tile.Pixels[at]), 1.0);
        Assert.Equal(30, Level(tile.Pixels[at + 1]), 1.0);
        Assert.Equal(20, Level(tile.Pixels[at + 2]), 1.0);
        Assert.True(tileBytes < 192L << 20, $"tile allocated {tileBytes >> 20} MiB");
    }

    [Fact]
    public void A600DpiGrayPageFitsAt2048BecauseItsDecodeIsReleasedBeforeTheOwnedCopy()
    {
        // A 5100x6600 grayscale IW44 page with no coded slices (gray 127). Kept gray bytes are 33.66 MB. The render
        // holds the decode and the 1583 x 2048 float output (51.9 MB), then the output and its owned copy (103.7 MB)
        // once the decode is released: 103.7 MB at most, under 128 MiB (134.2 MB). Charging the decode beside both
        // float buffers (137.4 MB) refuses it.
        byte[] page = DjvuJb2Tests.Page(5100, 6600, 1, DjvuJb2Tests.Chunk("BG44", [0, 0, 0x81, 2, 0x13, 0xec, 0x19, 0xc8, 0xff]));
        page[30] = 0x58;
        page[31] = 0x02;
        var snapshot = PdfReadSnapshot.OpenDjvu(page);
        var frame = snapshot.RenderPage(0, new PdfRenderOptions(1583, 2048, maximumPixelBytes: 128L << 20));
        Assert.InRange(Level(frame.Pixels[0]), 126, 128);
    }

    [Fact]
    public void A600DpiCompoundPageIsRefusedBeforeDecodingWhenTheLimitCannotHoldItsMaskDecode()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Convert.FromHexString(Compound600Dpi));
        long before = GC.GetAllocatedBytesForCurrentThread();
        // The JB2 decode may hold 32 MiB of symbols beside the kept layers, so 32 MiB cannot hold it.
        Assert.Throws<ArgumentException>(() => snapshot.RenderPage(0, new PdfRenderOptions(396, 512, maximumPixelBytes: 32L << 20)));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20, "the refusal decoded the page");
    }

    // A 384x384 page: cjb2's mask of four 96x80 blocks, a 32x32 color FG44 (subsample 12) that c44 coded from a red
    // (200, 40, 30) left half and a blue (30, 60, 190) right half in one 100-slice chunk with full chroma, and a white
    // 32x32 grayscale BG44. ddjvu draws the left blocks (199, 41, 34), the right blocks (0, 67, 242), the rest white.
    private const string ImageForeground =
        "41542654464F524D000000CA444A5655494E464F0000000A0180018018002C011601536A627A00000017805FBF698B9FC62537E98816595A8BE2" +
        "CA8FEFF7D95ABF004647343400000078006401020020002080F5D0181E4B09A13E9FEE33FFCC74260DAB0AC1C720A9BD704DD61E9F9E40B914" +
        "7FF396F6AA3EABD768F3439BF2E29C45C5FDF6886F859FE167F859FAF0621F5BD8E8C62BB04BA3218B679D5D85A85480403FD4D7F42DA02DB7" +
        "CAD8B5FAAF388B8BD7D945D7E28D6F63A318AEC1F43F424734340000000C00648102002000200083C52F";

    [Fact]
    public void AnFg44ForegroundColorsTheMaskOverTheBackground()
    {
        var frame = DjvuDocument.Open(Convert.FromHexString(ImageForeground)).RenderPage(0);
        var pixels = frame.Pixels.ToArray();
        void At(int x, int y, int r, int g, int b)
        {
            int o = (y * 384 + x) * 4;
            Assert.Equal(r, Level(pixels[o]), 2.0);
            Assert.Equal(g, Level(pixels[o + 1]), 2.0);
            Assert.Equal(b, Level(pixels[o + 2]), 2.0);
        }
        At(60, 80, 199, 41, 34);
        At(60, 300, 199, 41, 34);
        At(300, 80, 0, 67, 242);
        At(350, 330, 0, 67, 242);
        At(200, 200, 255, 255, 255);
        At(10, 10, 255, 255, 255);
    }

    // The palette page's mask and FGbz colors without its background. ddjvu draws such a page as a bilevel image,
    // black on white: 7.1.3 makes the background a compound page's other layer.
    private const string PaletteWithoutBackground =
        "41542654464F524D00000062444A5655494E464F0000000A0060004018002C011601536A627A00000020817B4FC3A17032BCDF26753F5D125A" +
        "814BB297F70B21C3F4061A686F5971159F4647627A0000001C800004000000D22814141EDCC8C8C8000007FFFFF081ED9BA2CB83BF";

    [Fact]
    public void AMaskWithColorsButNoBackgroundDrawsBlackOnWhite()
    {
        var frame = DjvuDocument.Open(Convert.FromHexString(PaletteWithoutBackground)).RenderPage(0);
        var authored = AuthoredColors();
        var expected = new (byte, byte, byte)[64, 96];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 96; x++)
                expected[y, x] = authored[y, x] == Paper ? ((byte)255, (byte)255, (byte)255) : Black;
        AssertColors(expected, frame, 0);
    }

    // A 64x48 color photo page as c44 codes it by default: three BG44 chunks (74, 15 and 10 slices), normal chroma.
    // Without its third chunk ddjvu's output differs in 5080 of 9216 samples by more than three levels.
    private const string ThreeChunkColorPage =
        "41542654464F524D0000014F444A5655494E464F0000000A004000301800640016014247343400000078004A0102004000308AFFF1BA4CDD1B01" +
        "A824750EFFF29332B4EC543D39143779BC72428EF9430D7FFFFFFEAA63ACFC7D6824C430EB0090F64CA5753448F791DF39F2B1064BD9E079E10F" +
        "B1E8ED804FFCCF2480AB59CC971C98605EFBA8CE00B05EB982FF2DC95322215E9C839EBA9B2697EBF1A5C54505B5424734340000003B010FFFFC" +
        "838D8C4D86EFE98D826F207B30AF15EC2D6B25FC58C117AC69907B5BECCCBBE2A494B0D74C76B633931BEC068494C59DE104E66AE5507F004247" +
        "34340000006D020A4EDA9DB995410A7EAB17B94E0550767686B45797AE4656606584A739E692E8DE2B8BEA27CEB8D2D1CF192C06ED8FD83D68E3" +
        "ECB39FD20E6CAA4E6B39D141C885950D2CC08F4777842595867C6CB4EB3CEF2F2CCCF81959335A39D51E6BB41515994414B58DA87C5DBA602F";

    [Theory]
    // SHA-256 of ddjvu's RGB output for the page upright, and turned by INFO flag 6 (48x64).
    [InlineData((byte)1, 64, 48, "A226686169102F1254225F3FA4AE7EF75F75C0E78B107ACDD16D3D3CB2B77D6C")]
    [InlineData((byte)6, 48, 64, "70E2259CCE422DF6D64A2D850DF206B69B5B47BE1DAA06720B519CC6B3A52881")]
    public void AColorPhotoPageDrawsFromAllItsChunksInItsOrientation(byte orientation, int width, int height, string rgbSha256)
    {
        var data = Convert.FromHexString(ThreeChunkColorPage);
        data[33] = orientation;
        var frame = DjvuDocument.Open(data).RenderPage(0);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        var pixels = frame.Pixels.ToArray();
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
            for (int c = 0; c < 3; c++) rgb[i * 3 + c] = (byte)Math.Round(Level(pixels[i * 4 + c]));
        Assert.Equal(rgbSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rgb)));
    }

    [Theory]
    [InlineData("Sjbz")]
    [InlineData("FGbz")]
    [InlineData("FG44")]
    public void MasksAndForegroundColorsAreNotReportedAsUndecodedCodecs(string layer)
    {
        var document = DjvuDocument.Open(DjvuJb2Tests.Page(8, 8, 1, DjvuJb2Tests.Chunk(layer, [0])));
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == "ImageDecodingUnsupported");
    }

    [Theory]
    [InlineData("BGjp")]
    [InlineData("FGjp")]
    public void JpegLayersAreStillReportedAsUndecodedCodecs(string layer)
    {
        var document = DjvuDocument.Open(DjvuJb2Tests.Page(8, 8, 1, DjvuJb2Tests.Chunk(layer, [0])));
        Assert.Contains(document.Diagnostics, d => d.Code == "ImageDecodingUnsupported");
    }

    [Fact]
    public void ACompoundPageIsNotConvertedToPdfYet()
    {
        var error = Assert.Throws<NotSupportedException>(() => DjvuDocument.Open(Convert.FromHexString(PaletteOverGray)).ConvertToPdf());
        Assert.Contains("compound", error.Message, StringComparison.Ordinal);
    }

    private static double Level(float linear) => Math.Pow(linear, 1 / 2.2) * 255;

    // The palette page with another INFO orientation flag (byte 9 of INFO, file offset 33).
    private static byte[] Oriented(byte orientation)
    {
        var data = Convert.FromHexString(PaletteOverGray);
        data[33] = orientation;
        return data;
    }

    // As DjVuLibre ddjvu shows these pages: flag 2 turns the stored page half way, flag 5 shows stored
    // (y, H - 1 - x) at displayed (x, y), and flag 6 shows stored (W - 1 - y, x).
    internal static T[,] Turned<T>(T[,] stored, byte orientation)
    {
        int h = stored.GetLength(0), w = stored.GetLength(1);
        bool quarter = orientation is 5 or 6;
        var shown = new T[quarter ? w : h, quarter ? h : w];
        for (int y = 0; y < shown.GetLength(0); y++)
            for (int x = 0; x < shown.GetLength(1); x++)
                shown[y, x] = orientation switch
                {
                    2 => stored[h - 1 - y, w - 1 - x],
                    5 => stored[h - 1 - x, y],
                    6 => stored[x, w - 1 - y],
                    _ => stored[y, x]
                };
        return shown;
    }

    // Compares a linear frame, at the page's INFO gamma of 2.2, with eight-bit colors, within a number of levels.
    internal static void AssertColors((byte R, byte G, byte B)[,] expected, PdfRenderedFrame frame, int levels)
    {
        int height = expected.GetLength(0), width = expected.GetLength(1);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        var pixels = frame.Pixels.ToArray();
        var wrong = new List<string>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = expected[y, x];
                int o = (y * width + x) * 4;
                double Level(float linear) => Math.Pow(linear, 1 / 2.2) * 255;
                if (Math.Abs(Level(pixels[o]) - r) > levels + 0.01 || Math.Abs(Level(pixels[o + 1]) - g) > levels + 0.01
                    || Math.Abs(Level(pixels[o + 2]) - b) > levels + 0.01 || pixels[o + 3] != 1f)
                    wrong.Add($"({x},{y}) {Level(pixels[o]):0.#},{Level(pixels[o + 1]):0.#},{Level(pixels[o + 2]):0.#} for {r},{g},{b}");
            }
        Assert.True(wrong.Count == 0, $"{wrong.Count} pixels differ: {string.Join("; ", wrong.Take(8))}");
    }
}
