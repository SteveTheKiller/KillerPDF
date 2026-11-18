using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Trash Polka paints a surface with its popup art only when that surface's Background is a brush ThemeMotif lists in
/// SurfaceParts. Every ComboBox dropdown in the shared control styles must paint ComboPopupBrush (listed as a Popup
/// part), or the open list stays a flat gray card that gets lost over the art. DarkComboBox's list painted PaneBrush,
/// which the motif never touches, so the ComboPopupBrush entry added for it never took effect.
/// </summary>
public sealed class ComboPopupMotifTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void EveryComboDropdownPaintsTheMotifPaintedPopupBrush()
    {
        var controls = XDocument.Load(Path.Combine(RepoRoot(), "Controls", "Controls.xaml"));
        var comboPopups = controls.Descendants(Wpf + "ControlTemplate")
            .Where(t => ((string?)t.Attribute("TargetType"))?.EndsWith("ComboBox", StringComparison.Ordinal) == true)
            .SelectMany(t => t.Descendants(Wpf + "Popup"))
            .Select(p => p.Elements(Wpf + "Border").Single())
            .ToList();

        Assert.True(comboPopups.Count >= 2, "expected the base ComboBox and DarkComboBox dropdowns");
        foreach (var border in comboPopups)
        {
            Assert.Equal("{DynamicResource ComboPopupBrush}", (string?)border.Attribute("Background"));
            Assert.Equal("{DynamicResource MenuBorderBrush}", (string?)border.Attribute("BorderBrush"));
        }

        string motif = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "ThemeMotif.cs"));
        Assert.Matches(new Regex(@"\(""ComboPopupBrush"",\s*MotifPart\.Popup\)"), motif);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Controls", "Controls.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
