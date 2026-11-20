using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #508: the shortcut card grows with the main window, so longer translations get room on a large
// window, and it never shrinks below the size that fit English on a small one.
public sealed class ShortcutCardSizeTests
{
    [Fact]
    public void SmallWindowKeepsTheBaseCard()
    {
        var (w, h) = ShortcutCardSize.Caps(800, 600);
        Assert.Equal(920, w);
        Assert.Equal(620, h);
    }

    [Fact]
    public void MidSizeWindowGivesTheCardEightyFivePercent()
    {
        var (w, h) = ShortcutCardSize.Caps(1280, 1000);
        Assert.Equal(1088, w);
        Assert.Equal(850, h);
    }

    [Fact]
    public void LargeWindowStopsAtTheCeiling()
    {
        var (w, h) = ShortcutCardSize.Caps(3840, 2160);
        Assert.Equal(1240, w);
        Assert.Equal(900, h);
    }
}
