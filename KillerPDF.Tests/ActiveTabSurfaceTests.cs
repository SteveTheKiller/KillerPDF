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

    [Fact]
    public void RetroTabEdgesStackGrayOutsideWhiteLikeThePane()
    {
        // 98SE above the page. Rendered 2026-11-20 with three tabs, active first, middle and last, plus split view:
        // the first tab's left edge is #808080 then white in the pane's own columns (it was white over the pane's gray),
        // the active tab's top is gray then white (it was white, a strip-colored gap, then gray), inactive tabs end a row
        // above the gray seam, and the full-width active tab no longer leaves a 1px gap beside its right edge.
        string theme = File.ReadAllText(Path.Combine(RepoRoot(), "Themes", "App", "98SE.xaml"));
        foreach (var (key, value) in new[]
                 {
                     ("RetroTabActiveBevelLightMargin", "-7,-3,-4,-4"), ("RetroTabInactiveMargin", "0,5,0,1"),
                     ("RetroTabInactivePadding", "8,6,5,1"), ("RetroTabInactiveBevelLightMargin", "-8,-5,-5,-1"),
                     ("RetroTabInactiveFirstBevelLightMargin", "-7,-5,-5,-1"), ("RetroTabInactiveBevelDarkMargin", "-8,-6,-5,-1"),
                     ("RetroTabInactiveOutlineMargin", "-8,-6,-6,-1"),
                 })
            Assert.Contains($"x:Key=\"{key}\">{value}</Thickness>", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"RetroTabActiveMaxWidth\">262</sys:Double>", theme, StringComparison.Ordinal);

        var viewer = XDocument.Load(Path.Combine(RepoRoot(), "Controls", "Viewer", "PdfViewer.xaml"));
        var triggers = viewer.Descendants(Wpf + "MultiDataTrigger").ToList();
        XElement Trigger(params (string Binding, string Value)[] conditions) => triggers.Single(t =>
        {
            var have = t.Descendants(Wpf + "Condition").Select(c => ((string?)c.Attribute("Binding"), (string?)c.Attribute("Value"))).ToList();
            return have.Count == conditions.Length && conditions.All(c => have.Contains(c));
        });
        const string Top = "{Binding RetroBottomEdges, ElementName=tabBd}";
        var active = Trigger(("{Binding UseRetroTabChrome}", "True"), ("{Binding IsActive}", "True"), (Top, "False"));
        var inactive = Trigger(("{Binding UseRetroTabChrome}", "True"), ("{Binding IsActive}", "False"), (Top, "False"));
        var first = Trigger(("{Binding UseRetroTabChrome}", "True"), ("{Binding IsActive}", "False"), ("{Binding IsFirst}", "True"), (Top, "False"));
        string? Set(XElement t, string target, string property) => t.Elements(Wpf + "Setter").SingleOrDefault(s =>
            (string?)s.Attribute("TargetName") == target && (string?)s.Attribute("Property") == property)?.Attribute("Value")?.Value;
        Assert.Equal("{DynamicResource RetroTabActiveBevelLightMargin}", Set(active, "tabBevelLight", "Margin"));
        Assert.Equal("{DynamicResource RetroTabActiveMaxWidth}", Set(active, "tabBd", "MaxWidth"));
        Assert.Equal("{DynamicResource RetroTabInactiveMargin}", Set(inactive, "tabBd", "Margin"));
        Assert.Equal("{DynamicResource RetroTabInactivePadding}", Set(inactive, "tabBd", "Padding"));
        Assert.Equal("{DynamicResource RetroTabInactiveOutlineMargin}", Set(inactive, "tabInactiveRetroOuterOutline", "Margin"));
        Assert.Equal("{DynamicResource RetroTabInactiveFirstBevelLightMargin}", Set(first, "tabBevelLight", "Margin"));
        // Only the active tab stands on the pane's left edge; an inactive first tab starts a pixel in.
        Assert.Equal("{DynamicResource RetroTabInactiveFirstMargin}", Set(first, "tabBd", "Margin"));
        Assert.Contains("x:Key=\"RetroTabInactiveFirstMargin\">1,5,0,1</Thickness>", theme, StringComparison.Ordinal);
        // The first-tab override must come after the general inactive one, so it wins when both match.
        Assert.True(triggers.IndexOf(first) > triggers.IndexOf(inactive));

        // The pane drops the kit's #dfdfdf second highlight, which read as a smudge on the #d4d4d4 face.
        string strip = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "Viewer", "PdfViewer.TabStrip.cs"));
        Assert.Contains("if (retroTheme) PaneBevelInnerDark.BorderThickness = new Thickness(0);", strip, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
    }
}
