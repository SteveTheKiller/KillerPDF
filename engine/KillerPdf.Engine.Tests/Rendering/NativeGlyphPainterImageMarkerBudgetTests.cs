using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Image and marker work is charged on the page, against a limit that follows the page raster as the rectangle
// work does (32 page areas, 16M at the least), so a page is accepted or refused the same way whole or in tiles.
public sealed class NativeGlyphPainterImageMarkerBudgetTests
{
    private static readonly PdfRgbColor Ink = new(0, 0, 0);

    private static List<NativePagePaint> Images(int side, int count)
    {
        var image = new PngDecodedImage([200, 100, 50], null, 1, 1, 3, 8);
        return [.. Enumerable.Range(0, count).Select(_ => (NativePagePaint)new NativeImagePaint(image, 0, 0)
        {
            Width = side,
            Height = side
        })];
    }

    // A marker whose circle bounds are the whole page: radius 150 centered on a 300 x 300 page.
    private static List<NativePagePaint> Markers(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => (NativePagePaint)new NativeMarkerPaint(150, 150, 150,
            new PdfRgbColor(.5, .5, .5)))];

    private static void AssertSameVerdict(int side, List<NativePagePaint> paints, bool refused)
    {
        Action whole = () => NativeGlyphPainter.Render(side, side, paints, Ink, CancellationToken.None);
        Action tile = () => NativeGlyphPainter.Render(side, side, paints, Ink, CancellationToken.None,
            region: (0, 0, 100, 100));
        if (refused)
        {
            Assert.Throws<InvalidDataException>(whole);
            Assert.Throws<InvalidDataException>(tile);
        }
        else
        {
            whole();
            tile();
        }
    }

    // 1000 x 1000 allows 32 page areas of image pixels, 32 million: 33 page-sized images exceed it.
    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void ATileOfAnImagePageIsRefusedExactlyWhenTheWholePageIs(int images, bool refused) =>
        AssertSameVerdict(1000, Images(1000, images), refused);

    // 300 x 300 allows 32 page areas of marker pixels at 16 samples each, 46,080,000 samples: a marker
    // covering the page costs 1,440,000, so 32 fit and 33 do not.
    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void ATileOfAMarkerPageIsRefusedExactlyWhenTheWholePageIs(int markers, bool refused) =>
        AssertSameVerdict(300, Markers(markers), refused);
}
