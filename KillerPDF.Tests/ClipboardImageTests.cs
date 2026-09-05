using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ClipboardImageTests
{
    [Fact]
    public void EnsureVisibleClipboardAlpha_MakesAllZeroAlphaOpaque()
    {
        byte[] pixels = [10, 20, 30, 0, 40, 50, 60, 0];

        BitmapHelpers.EnsureVisibleClipboardAlpha(pixels);

        Assert.Equal([10, 20, 30, 255, 40, 50, 60, 255], pixels);
    }

    [Fact]
    public void EnsureVisibleClipboardAlpha_PreservesMeaningfulTransparency()
    {
        byte[] pixels = [10, 20, 30, 0, 40, 50, 60, 128];

        BitmapHelpers.EnsureVisibleClipboardAlpha(pixels);

        Assert.Equal([10, 20, 30, 0, 40, 50, 60, 128], pixels);
    }
}
