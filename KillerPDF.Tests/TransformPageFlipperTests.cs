using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// Transform and Color correction flip through every page of the document, and Apply changes the page on screen (or
// the opening selection while its box is ticked) without closing the window (Steve, 2026-11-12). The behavior itself
// is checked in a real window on the lab VM; these pin the wiring.
public sealed class TransformPageFlipperTests
{
    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        return text[from..(to < 0 ? text.Length : to)];
    }

    [Fact]
    public void TheWindowFlipsThroughEveryPageOfTheDocument()
    {
        string rotate = Read("Shell", "Rotate.cs");
        Assert.Contains("new TransformWindow(this, _doc.PageCount, pageIndices, first, RenderPreview,", rotate);
        string window = Read("Controls", "TransformWindow.cs");
        Assert.Contains("if (_pageCount > 1)", window);
        Assert.Contains("_pageCounter.Text = $\"{_previewPageIndex + 1} / {_pageCount}\";", window);
    }

    // Opening Color correction focuses the Exposure slider, and a tooltip that only repeats a visible label popped up
    // over the Auto exposure box. Only a slider with its own hint carries a tooltip; the label is its automation name.
    [Fact]
    public void ColorSlidersCarryATooltipOnlyWhenTheyHaveAHint()
    {
        string slider = Between(Read("Controls", "TransformWindow.cs"), "private FrameworkElement AddColorSlider(", "slider.ValueChanged");
        Assert.Contains("ToolTip = hintKey is null ? null : S(hintKey)", slider);
        Assert.Contains("AutomationProperties.SetName(slider, S(labelKey))", slider);
    }

    [Fact]
    public void ApplyChangesThePageAndKeepsTheWindowOpen()
    {
        string apply = Between(Read("Controls", "TransformWindow.cs"), "private async Task ApplyCurrentAsync()", "\n        }\n");
        Assert.DoesNotContain("Close()", apply);
        Assert.Contains("[_previewPageIndex]", apply);
        Assert.Contains("_resetAll();", apply);
        Assert.Contains("ShowPreviewPage(_previewPageIndex, force: true);", apply);
        Assert.Contains("private async Task<bool> ApplyPageTransformsAsync(", Read("Shell", "Rotate.cs"));
    }
}
