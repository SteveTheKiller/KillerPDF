using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class StartupWindowPlacementTests
{
    // One 1920 x 1080 screen at the origin; the window's own size is 1000 x 700 until a saved size replaces it.
    private static StartupWindowPlacement Decide(Dictionary<string, string> saved, bool tornOut = false) =>
        StartupWindowPlacement.Decide(name => saved.GetValueOrDefault(name), 1000, 700, 0, 0, 1920, 1080, tornOut);

    private static Dictionary<string, string> Saved(int left, int top, int width, int height, string state) => new()
    {
        ["WindowLeft"] = left.ToString(), ["WindowTop"] = top.ToString(),
        ["WindowWidth"] = width.ToString(), ["WindowHeight"] = height.ToString(), ["WindowState"] = state,
    };

    [Fact]
    public void ATornOutWindowTakesNothingFromTheSavedPlacementEvenWhenTheAppLastClosedMaximized()
    {
        StartupWindowPlacement placement = Decide(Saved(420, 160, 900, 600, "Maximized"), tornOut: true);

        Assert.False(placement.Maximized);
        Assert.Null(placement.Width);
        Assert.Null(placement.Height);
        Assert.Null(placement.Left);
        Assert.Null(placement.Top);
    }

    [Fact]
    public void ASavedSizeAndAPositionStillOnScreenAreBothRestored()
    {
        StartupWindowPlacement placement = Decide(Saved(420, 160, 900, 600, "Normal"));

        Assert.Equal(new StartupWindowPlacement(900, 600, 420, 160, false), placement);
    }

    [Fact]
    public void AWindowSavedOnAMonitorThatIsGoneKeepsItsSizeAndLeavesItsPositionToTheDefault()
    {
        // Saved on a second screen to the right of this one, which is no longer connected.
        StartupWindowPlacement placement = Decide(Saved(2400, 200, 900, 600, "Normal"));

        Assert.Equal(new StartupWindowPlacement(900, 600, null, null, false), placement);
    }

    [Fact]
    public void AWindowSavedOnAScreenToTheLeftIsRestoredWhenThatScreenIsPartOfTheDesktop()
    {
        // Two 1920 x 1080 screens side by side, the second one to the left of the primary.
        var saved = Saved(-1500, 100, 900, 600, "Normal");
        StartupWindowPlacement placement =
            StartupWindowPlacement.Decide(name => saved.GetValueOrDefault(name), 1000, 700, -1920, 0, 3840, 1080, tornOut: false);

        Assert.Equal(new StartupWindowPlacement(900, 600, -1500, 100, false), placement);
    }

    [Fact]
    public void AMaximizedCloseRestoresMaximizedOverTheSavedNormalPosition()
    {
        StartupWindowPlacement placement = Decide(Saved(420, 160, 900, 600, "Maximized"));

        Assert.Equal(new StartupWindowPlacement(900, 600, 420, 160, true), placement);
    }

    [Fact]
    public void WithNothingSavedTheWindowKeepsItsOwnSizeAndPlace()
    {
        Assert.Equal(StartupWindowPlacement.None, Decide([]));
    }

    [Fact]
    public void ASavedSizeTooSmallToUseIsIgnoredWhileThePositionStillApplies()
    {
        StartupWindowPlacement placement = Decide(Saved(420, 160, 150, 150, "Normal"));

        Assert.Equal(new StartupWindowPlacement(null, null, 420, 160, false), placement);
    }
}
