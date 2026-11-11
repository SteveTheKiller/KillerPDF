using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class NativeGlyphPathCacheTests
{
    private static PdfGlyphOutline Square() => new([new PdfGlyphContour([
        new(0, 0, true), new(1000, 0, true), new(1000, 1000, true), new(0, 1000, true)])]);

    [Fact]
    public void RepeatedGlyphsFlattenTheirOutlineOnceAndKeepTheirCoverage()
    {
        var outline = Square();
        long before = PdfPageRenderer.NativeFlattenCalls;

        // The same letter at 40 positions and two sizes: a 10 pixel square, then a 20 pixel one.
        for (int index = 0; index < 40; index++)
        {
            double size = index % 2 == 0 ? .01 : .02;
            var mask = PdfPageRenderer.RasterizeNativeGlyph(outline, 20 + index * 3, 100, size, size, 200, 200,
                CancellationToken.None);
            try
            {
                int side = index % 2 == 0 ? 10 : 20;
                int left = 20 + index * 3;
                // A glyph unit of 1 is 1/1000 of the font size, so the square spans side pixels up from the baseline.
                Assert.Equal(255, mask.At(left + side / 2, 100 - side / 2));
                Assert.Equal(0, mask.At(left + side, 100 - side / 2));
                Assert.Equal(0, mask.At(left + side / 2, 100 - side - 1));
            }
            finally { mask.Return(); }
        }

        Assert.Equal(1, PdfPageRenderer.NativeFlattenCalls - before);
    }
}
