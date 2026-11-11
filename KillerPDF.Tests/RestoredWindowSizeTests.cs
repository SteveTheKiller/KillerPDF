using System.Windows;
using KillerFamily.Windowing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A window size the user saved is kept whole when the saved position is no longer on any monitor. The
// window then opens centered, which keeps a 10 percent margin unless the fit is told a size was restored.
public sealed class RestoredWindowSizeTests
{
    // Work area 1920 by 1040: with a margin the cap is floor(1920 * 0.9) = 1728 by floor(1040 * 0.9) = 936.
    [Theory]
    [InlineData(true, 1728, 936)]
    [InlineData(false, 1900, 1000)]
    public void ASavedSizeIsTrimmedOnlyWhenTheFitKeepsAMargin(bool keepMargin, double width, double height)
    {
        var settings = new Dictionary<string, string> { ["WindowWidth"] = "1900", ["WindowHeight"] = "1000",
            ["WindowLeft"] = "5000", ["WindowTop"] = "5000" };
        var placement = StartupWindowPlacement.Decide(key => settings.GetValueOrDefault(key), 1100, 700,
            0, 0, 1920, 1080, tornOut: false);
        // Saved size kept, saved position dropped: the window opens centered at that size.
        Assert.Equal(1900, placement.Width);
        Assert.Equal(1000, placement.Height);
        Assert.Null(placement.Left);

        Rect fitted = WorkAreaFit.FitRect(new Rect(0, 0, placement.Width!.Value, placement.Height!.Value),
            new Rect(0, 0, 1920, 1040), 640, 480, keepMargin, centered: true);
        Assert.Equal(width, fitted.Width);
        Assert.Equal(height, fitted.Height);
    }

    [Fact]
    public void TheWindowRefitsWithoutAMarginWhenASavedSizeWasRestoredAndNoPositionWas()
    {
        string window = ShellSource.Read("Shell", "MainWindow.xaml.cs");
        string restore = ShellSource.Between(window, "private void RestoreWindowBounds()", "private void RestoreWindowSettings()");
        string opened = ShellSource.Between(window, "private void ChromeExtras_SourceInitialized(", "private void");

        Assert.Contains("_restoredWindowSize = new Size(width, height);", restore, StringComparison.Ordinal);
        Assert.Contains("WorkAreaFit.Fit(this, keepMargin: false);", opened, StringComparison.Ordinal);
        Assert.Contains("_restoredWindowSize is { } restored", opened, StringComparison.Ordinal);
    }
}