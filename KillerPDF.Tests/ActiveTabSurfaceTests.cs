using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The front document tab is the pane's own color with a shadow every theme can see. On the menu tier it was darker
/// than the inactive tabs in Dark and Cyanotic, and Light's softened toolbar shadow left it no edge (Steve, 2026-11-20).
/// </summary>
public sealed class ActiveTabSurfaceTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void ActiveTabTakesThePaneColorAndItsOwnShadow()
    {
        var viewer = XDocument.Load(Path.Combine(RepoRoot(), "Controls", "Viewer", "PdfViewer.xaml"));
        var active = viewer.Descendants(Wpf + "DataTrigger").Single(t =>
            (string?)t.Attribute("Binding") == "{Binding IsActive}" && (string?)t.Attribute("Value") == "True"
            && t.Elements(Wpf + "Setter").Any(s => (string?)s.Attribute("Property") == "local:ThemeMotif.Part"));
        string? Set(string property) => active.Elements(Wpf + "Setter").SingleOrDefault(s =>
            (string?)s.Attribute("TargetName") == "tabBd" && (string?)s.Attribute("Property") == property)?.Attribute("Value")?.Value;
        Assert.Equal("{DynamicResource ContentPaneBrush}", Set("Background"));
        Assert.Equal("{DynamicResource DocumentTabShadowEffect}", Set("Effect"));
    }

    [Fact]
    public void TabShadowNeverDropsBelowTheFamilyStrength()
    {
        // Light keeps its soft 0.18 toolbar shadow; the tab takes at least the 0.38 the other themes use, and a flat
        // theme (BarShadowOpacity 0, 98SE) stays shadowless. Rendered before/after on 2026-11-20: against a strip of 198,
        // the Light row just above the tab went from 184 to 167.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "AppTheme.cs"));
        int at = source.IndexOf("if (!d.Contains(\"DocumentTabShadowEffect\"))", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string block = source.Substring(at, 900);
        Assert.Contains("if (bar > 0)", block, StringComparison.Ordinal);
        Assert.Contains("Opacity = Math.Max(bar, 0.38)", block, StringComparison.Ordinal);
        Assert.Contains("else d[\"DocumentTabShadowEffect\"] = null;", block, StringComparison.Ordinal);
        Assert.DoesNotContain("d[\"BarShadowOpacity\"] =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SinglePaneOn98SEKeepsTheFaceItsActiveTabIsDrawnIn()
    {
        // 98SE dims an unfocused pane to #9f9f9f as its split-view focus cue. With one pane that left the #c0c0c0
        // active tab sitting on the dimmed gray whenever focus was on the toolbar or sidebar.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "Viewer", "PdfViewer.TabStrip.cs"));
        Assert.Contains("bool paneActive = split ? PaneHasFocus : retro;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PaneHasFocus && (split || retro)", source, StringComparison.Ordinal);
        // The live pane and its tab are a step lighter than the #c0c0c0 face, inactive tabs a step darker.
        Assert.Contains("retro ? (paneActive ? \"RetroDocumentFaceBrush\" : \"TabInactiveBrush\")", source, StringComparison.Ordinal);
        string theme = File.ReadAllText(Path.Combine(RepoRoot(), "Themes", "App", "98SE.xaml"));
        Assert.Contains("x:Key=\"RetroDocumentFaceBrush\" Color=\"#d4d4d4\"", theme, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
    }
}
