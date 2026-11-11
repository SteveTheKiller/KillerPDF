using KillerPDF.Shell;
using Xunit;

namespace KillerPDF.Tests;

public sealed class WindowFloorTests
{
    // The preview grid sits 27 px inside the pane host (8 px host inset, 2 px card border and the viewer padding;
    // NativeNoticeFitTests puts it at 293 when the host is 320). At the minimum window it must still be 360.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.5)]
    public void TheSinglePaneFloorLeavesThePreviewGridAtLeast360(double appScale)
    {
        // Chrome is measured in screen pixels and the document pane is not scaled, so it passes a scale of 1.
        double chrome = 180 * appScale + 40;
        double floor = WindowFloor.Compute(chrome, WindowFloor.SinglePaneMinimum, 1.0, split: false, gutter: 8);

        double previewGrid = floor - chrome - WindowFloor.PreviewGridInset;
        Assert.True(previewGrid >= 360, $"preview grid {previewGrid}");
    }

    // Split windows keep the old 320 per pane: chrome 300 + gutter 8 + 2 x 320 = 948.
    [Fact]
    public void SplitViewKeepsTheOldPerPaneMinimum()
    {
        Assert.Equal(948, WindowFloor.Compute(300, WindowFloor.SplitPaneMinimum, 1.0, split: true, gutter: 8), 6);
    }

    // The scale argument multiplies the pane minimum: 2.5 x 387 = 967.5, plus chrome 500 = 1467.5.
    [Fact]
    public void AScaledPaneMinimumGrowsWithTheScale()
    {
        Assert.Equal(1467.5, WindowFloor.Compute(500, 387, 2.5, split: false, gutter: 8), 6);
    }

    // SOURCE PIN: the split pane is built in the window, which this project does not compile. The minimum the test
    // above pins must be the one the pane applies, not a second copy of the same number.
    [Fact]
    public void TheSplitPaneAppliesTheSharedMinimum()
    {
        string split = ViewerSourcePin.Source("Shell", "SplitPane.cs");

        Assert.Contains("private const double MinPaneWidth = WindowFloor.SplitPaneMinimum;", split);
        Assert.DoesNotContain("MinPaneWidth = 320", split);
    }
}
