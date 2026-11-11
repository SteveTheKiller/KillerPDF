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
