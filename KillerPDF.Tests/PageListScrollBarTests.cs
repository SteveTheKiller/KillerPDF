using System;
using System.IO;
using System.Windows;
using KillerPDF.Shell;
using Xunit;

namespace KillerPDF.Tests;

// Steve 2026-11-25: the gap between the selected thumbnail and the sidebar grip is the page list's
// scrollbar, sized to the fat hovered thumb plus 1px on either side.
public sealed class PageListScrollBarTests
{
    [Fact]
    public void TheModernBarLeavesOnePixelAroundTheHoveredThumb()
    {
        var sized = PageListScrollBar.Size(12, new Thickness(4, 0, 4, 0), new Thickness(0, 4, 0, 4), new Thickness(2));

        Assert.NotNull(sized);
        var (thickness, resting, restingH, fat) = sized!.Value;
        Assert.Equal(10, thickness);
        // The hovered thumb keeps its 8px width with 1 clear on each side, in either orientation.
        Assert.Equal(new Thickness(1), fat);
        Assert.Equal(8, thickness - fat.Left - fat.Right);
        // The resting thumb keeps its 4px width, and the strip's horizontal thumb its 4px height.
        Assert.Equal(4, thickness - resting.Left - resting.Right);
        Assert.Equal(4, thickness - restingH.Top - restingH.Bottom);
    }

    [Fact]
    public void ABarWhoseThumbFillsItKeepsItsOwnSize()
    {
        // 98SE: a 16px bar whose resting thumb already fills it.
        Assert.Null(PageListScrollBar.Size(16, new Thickness(0), new Thickness(0), new Thickness(2)));
    }

    // The app's own ScrollBar style is the one the page list gets (App.xaml outranks the merged kit
    // dictionary), so its hover margin has to be the key the page list overrides.
    [Fact]
    public void TheAppScrollBarHoverReadsTheFatMarginKey()
    {
        string app = File.ReadAllText(Path.Combine(Root(), "App.xaml"));
        Assert.Contains("<Thickness x:Key=\"ScrollThumbFatMargin\">2</Thickness>", app);
        Assert.Contains("Property=\"Margin\" Value=\"{DynamicResource ScrollThumbFatMargin}\"", app);
        Assert.DoesNotContain("Property=\"Margin\" Value=\"2,2\"", app);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("App.xaml");
    }
}
