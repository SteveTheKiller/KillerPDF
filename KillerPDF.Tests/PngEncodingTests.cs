using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PngEncodingTests
{
    [Fact]
    public void RenderToPng_ReadOnlySliceUsesItsOffsetAndPreservesSource()
    {
        byte[] source = [9, 8, 7, 6, 30, 60, 90, 255, 120, 150, 180, 255, 1, 2, 3, 4];
        byte[] original = source.ToArray();
        byte[] expected = BitmapHelpers.RenderToPng(source.AsSpan(4, 8).ToArray(), 2, 1);
        byte[] actual = BitmapHelpers.RenderToPng(source.AsMemory(4, 8), 2, 1);
        Assert.Equal(expected, actual);
        Assert.Equal(original, source);
    }

    [Fact]
    public void RenderToPng_RejectsShortReadOnlySlice()
    {
        Assert.Throws<ArgumentException>(() =>
            BitmapHelpers.RenderToPng(new byte[16].AsMemory(4, 4), 2, 1));
    }
}
