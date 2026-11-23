using System.IO;
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

    // The caps are limits, not a size: a stretched card filled them and left the list and the keyboard
    // floating in empty space on a large window (Steve 2026-11-23).
    [Fact]
    public void TheCardWrapsItsContentInsteadOfFillingTheCaps()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        string xaml = File.ReadAllText(Path.Combine(dir!.FullName, "Shell", "MainWindow.xaml"));
        int at = xaml.IndexOf("x:Name=\"ShortcutCardGrid\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string tag = xaml[at..xaml.IndexOf('>', at)];
        Assert.Contains("HorizontalAlignment=\"Center\"", tag);
    }
}
