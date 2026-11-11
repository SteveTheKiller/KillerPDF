using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// A color photo page decoded at a subsample keeps IW44's anchoring: samples cover whole reductions from the page's
// bottom left corner, so when the stored height is not a multiple of the reduction the partial cell is the first one
// down (the top of the page), not the last. The page here is 4 wide and 5 high at reduction 2: stored 2 x 3 cells, the
// rows covering page rows [0,1), [1,3) and [3,5). Every stored row holds one gray level (linear 0.2, 0.4, 0.8).
public sealed class PdfNativeCompactRasterRowOffsetTests
{
    private static readonly float[] Linear = Enumerable.Range(0, 256).Select(v => v / 255f).ToArray();

    private static PdfNativeCompactRaster Raster(int rotation)
    {
        byte[] levels = [51, 102, 204];
        var samples = new byte[2 * 3 * 3];
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 2; column++)
                for (int channel = 0; channel < 3; channel++)
                    samples[(row * 2 + column) * 3 + channel] = levels[row];
        return new PdfNativeCompactRaster(PdfNativeSampleLayout.Rgb, 2, 3, samples, Linear, rotation, reduction: 2,
            pageWidth: 4, pageHeight: 5);
    }

    // The stored row axis shown across the displayed rows (natural and half turn) or columns (quarter turns).
    [Theory]
    [InlineData(0, false, new[] { 0.2f, 0.4f, 0.4f, 0.8f, 0.8f })]
    [InlineData(180, false, new[] { 0.8f, 0.8f, 0.4f, 0.4f, 0.2f })]
    [InlineData(90, true, new[] { 0.8f, 0.8f, 0.4f, 0.4f, 0.2f })]
    [InlineData(270, true, new[] { 0.2f, 0.4f, 0.4f, 0.8f, 0.8f })]
    public void TheShortCellIsTheFirstOneDownTheStoredRowsAtFullSize(int rotation, bool acrossColumns, float[] expected)
    {
        var raster = Raster(rotation);
        int width = acrossColumns ? 5 : 4, height = acrossColumns ? 4 : 5;

        var pixels = PdfNativeFrameResampler.ResizePixels(raster, width, height, 0, 0, width, height, CancellationToken.None);

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Assert.Equal(expected[acrossColumns ? x : y], pixels[(y * width + x) * 4], 5);
    }

    [Fact]
    public void ReducedOutputWeighsEachCellByTheRowsItActuallyCovers()
    {
        // Two output rows over five page rows: the first spans [0, 2.5), the second [2.5, 5). Cells cover [0,1), [1,3),
        // [3,5) in page rows, so row 0 is 1/2.5 of 0.2 and 1.5/2.5 of 0.4, and row 1 is 0.5/2.5 of 0.4 and 2/2.5 of 0.8.
        var pixels = PdfNativeFrameResampler.ResizePixels(Raster(0), 4, 2, 0, 0, 4, 2, CancellationToken.None);

        Assert.Equal(0.4 * 0.2 + 0.6 * 0.4, pixels[0], 5);
        Assert.Equal(0.2 * 0.4 + 0.8 * 0.8, pixels[4 * 4], 5);
    }
}
