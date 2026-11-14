using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// The kit check box drew its box in CardBorderBrush (#c0c0c0 on Light's #cccccc dialog, about 1.15:1), so on Light it
// showed no box at all. It now draws the ring the theme radios draw, DimTextBrush at 1.5 px, and follows a theme switch.
// The sign window's 26 px Browse buttons cut off the bottom of their caption; their padding fits that height.
public sealed class KitCheckBoxContrastTests
{
    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.csproj"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    [Fact]
    public void TheCheckBoxBoxUsesTheRadioRingBrushAndFollowsTheTheme()
    {
        string kit = Read("Controls", "UiKit.cs");
        int start = kit.IndexOf("private static ControlTemplate CheckTemplate()", StringComparison.Ordinal);
        string template = kit[start..kit.IndexOf("return ct;", start, StringComparison.Ordinal)];
        Assert.Contains("box.SetResourceReference(Border.BorderBrushProperty, \"DimTextBrush\")", template);
        Assert.Contains("box.SetResourceReference(Border.BackgroundProperty, \"RadioWellBrush\")", template);
        Assert.Contains("new Thickness(1.5)", template);
        Assert.DoesNotContain("Brush(\"CardBorderBrush\")", template);
    }

    [Fact]
    public void TheSignWindowsBrowseButtonsFitTheirCaption()
    {
        string dialog = Read("Controls", "SignDocumentDialog.cs");
        Assert.Equal(2, CountOf(dialog, "Padding = SignLayout.CompactButtonPadding"));
        Assert.Contains("CompactButtonPadding", Read("Controls", "SignDialogLayout.cs"));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
