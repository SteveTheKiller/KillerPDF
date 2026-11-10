using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuJb2Tests
{
    // A 56x32 page of glyph art. G2 is G1 with one more black pixel, so an encoder that matches shapes codes it by
    // refinement of G1 (lossless) or replaces it with a copy of G1 (lossy).
    private static readonly string[] G1 = ["..###..", ".#...#.", "#.....#", "#.....#", "#######", "#.....#", "#.....#", "#.....#", "#.....#"];
    private static readonly string[] G2 = ["..###..", ".#...#.", "#.....#", "#..#..#", "#######", "#.....#", "#.....#", "#.....#", "#.....#"];
    private static readonly string[] G3 = ["##.....", "##.....", "##.....", "##.....", "##.....", "##.....", "##.....", "#######", "#######"];
    private static readonly string[] Bar = [new string('#', 20), new string('#', 20)];

    // DjVuLibre 3.5.29 cjb2 -dpi 300 of the authored page, lossless; ddjvu renders it back to the authored page.
    private const string LosslessSjbz = "826C97EA17032BCDF26753F5D158491ECE4D21C50FA445251FF1C6B11C5102AAD66CC9ECF6202997";
    // The same with cjb2 -lossy; ddjvu renders the authored page with G2 drawn as G1.
    private const string LossySjbz = "826C97EA17032BCDF26753F5D158491EEC2A8B4F588E2660CCABD9989EC40530";

    [Fact]
    public void ALosslessJb2PageDrawsItsAuthoredBitmap()
    {
        var document = DjvuDocument.Open(Page(56, 32, 1, Chunk("Sjbz", Convert.FromHexString(LosslessSjbz))));
        AssertBitmap(Authored(G2), document.RenderPage(0));
    }

    [Fact]
    public void ALossyJb2PageDrawsCopiedShapesAtTheirPlaces()
    {
        var document = DjvuDocument.Open(Page(56, 32, 1, Chunk("Sjbz", Convert.FromHexString(LossySjbz))));
        AssertBitmap(Authored(G1), document.RenderPage(0));
    }

    // A bundle of a FORM:DJVI dictionary and a 64x40 page that includes it, built by a scratch test-stream writer and
    // rendered by DjVuLibre ddjvu to exactly the authored page. The dictionary holds G1 coded with a white border (so
    // only its black box enters the library), G2 refined from it and G3, with a comment; the page requires the three
    // shapes and uses a comment, two copies, a new image-only X, an image-only refinement of G3, a context reset,
    // non-symbol data (the bar), a new T added to the library and copied, and G1C refined from G1 and copied.
    private const string DictionaryBundle =
        "41542654464F524D000000D2444A564D4449524D000000288100020000004000000074FFFFE2BF8A1FE9A21937B167E2A403B19F7DBEA8FB6AF7C9" +
        "8AD3AF6ABF464F524D0000002C444A5649446A627A00000020E70D26C19A11FECC4BD37A4C122ED3FD142BFABF907143E9114965AE29659B4A464F" +
        "524D00000062444A5655494E464F0000000A0040002818002C011601494E434C000000086D6F72652E696666536A627A0000003414E012482EA3B6" +
        "F055ECFA22A213849C7E52028C32672378AD79C8429044E3C114256B61009A8B3E1B7A75061B54380AC8B9D0E5";

    // A bundle whose page requires two shapes from more.iff, a dictionary that itself requires base.iff's one shape (G1)
    // and adds G3. The page copies G1 to (2, 3) and G3 to (12, 3). The spec allows a dictionary to include another;
    // DjVuLibre ddjvu does not resolve that, so this expectation rests on the spec alone.
    private const string NestedDictionaryBundle =
        "41542654464F524D000000CE444A564D4449524D000000348100030000004C0000006C0000009AFFFFD5BF8A21687BC6D3F5A2C12D6011DCA18FDC" +
        "338C4D967688E590DE04BD95507D94A8F1464F524D00000018444A5649446A627A0000000CE70F7E06579BE4CEA7EBA17F464F524D00000026444A" +
        "5649494E434C00000008626173652E696666446A627A0000000A1590C03380AA2EC3BBDF464F524D00000038444A5655494E464F0000000A004000" +
        "2818002C011601494E434C000000086D6F72652E696666536A627A0000000915004920B9CC8112F700";

    private static readonly string[] X = ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"];
    private static readonly string[] T = ["#####", "..#..", "..#..", "..#..", "..#..", "..#.."];
    private static readonly string[] G3B = ["##.....", "##.....", "##..#..", "##.....", "##.....", "##.....", "##.....", "#######", "#######"];
    private static readonly string[] G1C = ["..###..", ".#...#.", "#.....#", "#.....#", "#######", "#.....#", "#.....#", "#.....#", "##...##"];

    [Fact]
    public void AJb2PageDrawsShapesFromTheSharedDictionaryItIncludesWithEveryRecordType()
    {
        var document = DjvuDocument.Open(Convert.FromHexString(DictionaryBundle));
        AssertBitmap(Draw(64, 40, (G1, 2, 3), (G2, 12, 3), (X, 22, 3), (G3B, 32, 3), (Bar, 10, 35), (T, 2, 18), (T, 12, 18),
            (G1C, 22, 18), (G1C, 32, 20)), document.RenderPage(0));
    }

    [Fact]
    public void ASharedDictionaryCanRequireTheDictionaryItsOwnComponentIncludes()
    {
        var document = DjvuDocument.Open(Convert.FromHexString(NestedDictionaryBundle));
        AssertBitmap(Draw(64, 40, (G1, 2, 3), (G3, 12, 3)), document.RenderPage(0));
    }

    [Fact]
    public void AnIncludedComponentOutsideTheBundleIsNeverResolved()
    {
        // The page's INCL names "zore.iff", which the bundle does not contain.
        var data = Convert.FromHexString(DictionaryBundle.Replace("494E434C000000086D6F7265", "494E434C000000087A6F7265", StringComparison.Ordinal));
        var document = DjvuDocument.Open(data);
        Assert.Contains(document.Diagnostics, d => d.Code == "IncludedComponentUnsupported");
        Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
    }

    [Fact]
    public void AnIncludedComponentInsideTheBundleIsNotReportedAsUnresolved()
    {
        var document = DjvuDocument.Open(Convert.FromHexString(DictionaryBundle));
        Assert.DoesNotContain(document.Diagnostics, d => d.Code is "IncludedComponentUnsupported" or "ImageDecodingUnsupported");
    }

    [Theory]
    // A 64x40 page whose first record is a new shape of 65535 x 65535 pixels: 512 MiB as bits, past the symbol storage.
    [InlineData("81FACCE2151276F6C4893B7F")]
    // A new shape of 4000 x 4000 pixels whose pixels the stream does not hold.
    [InlineData("81FACCE21634FF666A3F")]
    public void AShapeTheStreamCannotBackIsRefusedQuicklyWithoutAllocatingIt(string sjbz)
    {
        var document = DjvuDocument.Open(Page(64, 40, 1, Chunk("Sjbz", Convert.FromHexString(sjbz))));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 << 20, "allocated more than 8 MiB");
    }

    [Fact]
    public void MutatedJb2StreamsDrawOrFailAsInvalidDataWithinBoundedTimeAndMemory()
    {
        var random = new Random(472);
        // Each source with the chunk whose payload is damaged: a page's Sjbz, the bundle's page Sjbz and its Djbz
        // dictionary, and the FGbz palette, blit count and BZZ colors of DjvuCompoundTests' palette page.
        var bundle = Convert.FromHexString(DictionaryBundle);
        var palette = Convert.FromHexString(DjvuCompoundTests.PaletteOverGrayHex);
        (byte[] Data, string Chunk)[] sources = [(Page(56, 32, 1, Chunk("Sjbz", Convert.FromHexString(LosslessSjbz))), "Sjbz"),
            (bundle, "Sjbz"), (bundle, "Djbz"), (palette, "FGbz")];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long slowest = 0;
        for (int round = 0; round < 800; round++)
        {
            var (source, chunk) = sources[round % 4];
            int header = source.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(chunk));
            int first = header + 8, end = first + System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(header + 4));
            var data = (byte[])source.Clone();
            switch (round / 4 % 4)
            {
                case 0: data[first + random.Next(end - first)] ^= (byte)(1 << random.Next(8)); break;
                case 1: data[first + random.Next(end - first)] = (byte)random.Next(256); break;
                case 2: for (int i = first + random.Next(end - first); i < end; i++) data[i] = 0; break;
                default: for (int i = first + random.Next(end - first); i < end; i++) data[i] = (byte)random.Next(256); break;
            }
            var document = DjvuDocument.Open(data);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var one = System.Diagnostics.Stopwatch.StartNew();
            try { document.RenderPage(0); }
            catch (InvalidDataException) { }
            slowest = Math.Max(slowest, one.ElapsedMilliseconds);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 << 20, $"round {round} allocated more than 64 MiB");
        }
        Assert.True(slowest < 3000, $"slowest mutation took {slowest} ms");
        Assert.True(clock.ElapsedMilliseconds < 60_000, $"{clock.ElapsedMilliseconds} ms in all");
    }

    [Fact]
    public void AJb2PageWithASharedDictionaryConvertsToAPdfPageWithTheSamePixels()
    {
        var pdf = KillerPdf.Engine.Documents.PdfDocument.Open(DjvuDocument.Open(Convert.FromHexString(DictionaryBundle)).ConvertToPdf());
        AssertPdfPage(pdf, Draw(64, 40, (G1, 2, 3), (G2, 12, 3), (X, 22, 3), (G3B, 32, 3), (Bar, 10, 35), (T, 2, 18), (T, 12, 18),
            (G1C, 22, 18), (G1C, 32, 20)));
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)5)]
    public void AJb2PageConvertsInItsOrientation(byte orientation)
    {
        var pdf = KillerPdf.Engine.Documents.PdfDocument.Open(
            DjvuDocument.Open(Page(56, 32, orientation, Chunk("Sjbz", Convert.FromHexString(LosslessSjbz)))).ConvertToPdf());
        AssertPdfPage(pdf, DjvuCompoundTests.Turned(Authored(G2), orientation));
    }

    private static void AssertPdfPage(KillerPdf.Engine.Documents.PdfDocument pdf, bool[,] black)
    {
        int height = black.GetLength(0), width = black.GetLength(1);
        var geometry = Assert.Single(KillerPdf.Engine.Documents.PdfPageInformation.Read(pdf));
        // 300 dpi pixels at 72 points per inch.
        Assert.Equal(width * 72 / 300d, geometry.PhysicalWidth, 6);
        Assert.Equal(height * 72 / 300d, geometry.PhysicalHeight, 6);
        var rendered = new KillerPdf.Engine.Rendering.PdfPageRenderer(pdf).Render(0, new KillerPdf.Engine.Rendering.PdfRenderOptions(width, height));
        var pixels = rendered.Pixels.ToArray();
        var wrong = new List<string>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4] != (black[y, x] ? 0 : 255)) wrong.Add($"({x},{y})");
        Assert.True(wrong.Count == 0, $"{wrong.Count} pixels differ: {string.Join(" ", wrong.Take(20))}");
    }

    private static bool[,] Draw(int width, int height, params (string[] Art, int X, int Y)[] places)
    {
        var black = new bool[height, width];
        foreach (var (art, x0, y0) in places)
            for (int dy = 0; dy < art.Length; dy++)
                for (int dx = 0; dx < art[dy].Length; dx++)
                    if (art[dy][dx] == '#') black[y0 + dy, x0 + dx] = true;
        return black;
    }

    internal static bool[,] Authored(string[] third)
    {
        var black = new bool[32, 56];
        (string[] Art, int X, int Y)[] places = [(G1, 2, 3), (G1, 12, 3), (third, 22, 3), (G3, 32, 3), (G1, 2, 18), (G1, 42, 20), (Bar, 10, 29)];
        foreach (var (art, x0, y0) in places)
            for (int dy = 0; dy < art.Length; dy++)
                for (int dx = 0; dx < art[dy].Length; dx++)
                    if (art[dy][dx] == '#') black[y0 + dy, x0 + dx] = true;
        return black;
    }

    internal static void AssertBitmap(bool[,] black, KillerPdf.Engine.Rendering.PdfRenderedFrame frame)
    {
        int height = black.GetLength(0), width = black.GetLength(1);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        var pixels = frame.Pixels.ToArray();
        var wrong = new List<string>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float expected = black[y, x] ? 0f : 1f;
                int o = (y * width + x) * 4;
                if (pixels[o] != expected || pixels[o + 1] != expected || pixels[o + 2] != expected || pixels[o + 3] != 1f)
                    wrong.Add($"({x},{y})");
            }
        Assert.True(wrong.Count == 0, $"{wrong.Count} pixels differ: {string.Join(" ", wrong.Take(20))}");
    }

    internal static byte[] Page(int width, int height, byte orientation, params byte[][] chunks) => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8, .. Chunk("INFO", [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 24, 0, 0x2c, 1, 22, orientation]), .. chunks.SelectMany(c => c)])];

    internal static byte[] Chunk(string id, byte[] payload)
    {
        var length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
