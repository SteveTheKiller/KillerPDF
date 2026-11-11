using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A compound page draws each foreground color through its own soft mask image. A page with many colors would make
// hundreds of masks, each as large as the box its color's pixels span, so past a small number of colors the
// foreground is one image under one mask.
public sealed class DjvuManyColorPdfConversionTests
{
    private const int Size = 48;

    [Fact]
    public void AFewColorsKeepOneMaskEach()
    {
        var notices = new List<DjvuDiagnostic>();
        var bytes = DjvuDocument.Open(DjvuTestStreams.ManyColorPage(3)).ConvertToPdf(notices);

        Assert.Equal(3, SoftMasks(bytes));
        Assert.Empty(notices);
        AssertColors(bytes, 3);
    }

    [Fact]
    public void AColoredPageWithHundredsOfColorsConvertsWithACappedNumberOfMasksAndKeepsEveryColor()
    {
        var notices = new List<DjvuDiagnostic>();
        var bytes = DjvuDocument.Open(DjvuTestStreams.ManyColorPage(500)).ConvertToPdf(notices);

        int masks = SoftMasks(bytes);
        Assert.True(masks <= 16, $"{masks} soft masks for 500 colors");
        var notice = Assert.Single(notices);
        Assert.Equal("ForegroundColorsMerged", notice.Code);
        Assert.Equal(0, notice.PageIndex);
        AssertColors(bytes, 500);
    }

    // The merged foreground image holds black where no shape lies, and a viewer that smooths it while scaling averages
    // that black into a glyph edge. Every pixel must hold a real palette color: the nearest shape in its row (the left
    // one on a tie), or the nearest row that has a shape.
    [Fact]
    public void TheMergedForegroundImageHoldsNoBlackNextToAShape()
    {
        // 6 x 4 pixels: rank 0 (yellow) at (2,1) and rank 1 (blue) at (4,1); rows 0, 2 and 3 have no shape.
        byte[] palette = [255, 255, 0, 0, 0, 255];
        byte[] identity = [.. Enumerable.Range(0, 256).Select(value => (byte)value)];
        var shapes = new Dictionary<(int, int), int> { [(2, 1)] = 0, [(4, 1)] = 1 };

        var rgb = DjvuDocument.BuildPaletteRgb(6, 4, (x, y) => shapes.ContainsKey((x, y)),
            (x, y) => shapes[(x, y)], palette, identity, CancellationToken.None);

        byte[] yellow = [255, 255, 0], blue = [0, 0, 255];
        // Row 1: left of and at the first shape and the tie between the two shapes are yellow; the rest are blue.
        byte[][] row = [yellow, yellow, yellow, yellow, blue, blue];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 6; x++)
                Assert.True(rgb.AsSpan(3 * (y * 6 + x), 3).SequenceEqual(row[x]),
                    $"pixel ({x},{y}) is {rgb[3 * (y * 6 + x)]},{rgb[3 * (y * 6 + x) + 1]},{rgb[3 * (y * 6 + x) + 2]}, expected {string.Join(",", row[x])}");
    }

    // Each soft mask is an ExtGState with /S /Luminosity; the builder writes its objects uncompressed.
    private static int SoftMasks(byte[] pdf)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        int count = 0;
        for (int at = text.IndexOf("/Luminosity", StringComparison.Ordinal); at >= 0;
            at = text.IndexOf("/Luminosity", at + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    // Every color's two pixels, drawn at one PDF pixel per DjVu pixel, hold its palette color through the page's
    // gamma of 2.2 (INFO byte 22) and an eight-bit sRGB round trip.
    private static void AssertColors(byte[] bytes, int colors)
    {
        var pdf = PdfDocument.Open(bytes);
        var page = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(Size, Size));
        var pixels = page.Pixels.ToArray();
        var wrong = new List<string>();
        for (int k = 0; k < colors; k++)
        {
            var (r, g, b) = DjvuTestStreams.PaletteColor(k);
            int x = k % Size, y = k / Size;
            foreach (var (px, py) in new[] { (x, y), (Size - 1 - x, Size - 1 - y) })
            {
                int o = (py * Size + px) * 4;
                if (Math.Abs(pixels[o + 2] - Expected(r)) > 1 || Math.Abs(pixels[o + 1] - Expected(g)) > 1
                    || Math.Abs(pixels[o] - Expected(b)) > 1)
                    wrong.Add($"color {k} at ({px},{py}): {pixels[o + 2]},{pixels[o + 1]},{pixels[o]} want {Expected(r)},{Expected(g)},{Expected(b)}");
            }
        }
        Assert.True(wrong.Count == 0, $"{wrong.Count} pixels differ: {string.Join("; ", wrong.Take(5))}");
    }

    private static int Expected(byte stored)
    {
        double linear = Math.Pow(stored / 255d, 2.2);
        double encoded = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (int)Math.Round(encoded * 255);
    }
}
