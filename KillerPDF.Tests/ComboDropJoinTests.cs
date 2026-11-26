using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using KillerFamily.Layout;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The dark combo's open list hangs straight off its box: the list's edge overlaps the box's edge by 1px, the corners
/// where they meet are square on both shapes, and the shadow has room on the three free sides only. Before, the list
/// sat under the box with both shapes fully rounded and a 6px shadow room that cut a 22px blur off square. The Macros
/// step picker sits at the foot of its window, so its list opens upward instead.
/// </summary>
public sealed class ComboDropJoinTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheJoinedSideOfEachShapeIsSquare()
    {
        var radius = new CornerRadius(3, 4, 5, 6);
        Assert.Equal(new CornerRadius(0, 0, 5, 6), ComboDrop.SquareTop.Convert(radius, typeof(CornerRadius), null!, CultureInfo.InvariantCulture));
        Assert.Equal(new CornerRadius(3, 4, 0, 0), ComboDrop.SquareBottom.Convert(radius, typeof(CornerRadius), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheDarkComboListJoinsItsBox()
    {
        var template = DarkComboTemplate();
        var popup = template.Descendants(Wpf + "Popup").Single();
        var list = popup.Elements(Wpf + "Border").Single();
        Assert.Equal("Bottom", (string?)popup.Attribute("Placement"));
        Assert.Equal("-1", (string?)popup.Attribute("VerticalOffset"));
        Assert.Null(popup.Attribute("HorizontalOffset"));
        Assert.Equal("22,0,22,26", (string?)list.Attribute("Margin"));

        var triggers = template.Element(Wpf + "ControlTemplate.Triggers")!.Elements().ToList();
        var up = triggers.Single(t => (string?)t.Attribute("Property") == "kl:ComboDrop.OpensUp");
        Assert.Equal("Top", Setter(up, "PART_Popup", "Placement"));
        Assert.Equal("1", Setter(up, "PART_Popup", "VerticalOffset"));
        Assert.Equal("22,18,22,0", Setter(up, "PopupBd", "Margin"));

        var open = triggers.Single(t => t.Name == Wpf + "Trigger" && (string?)t.Attribute("Property") == "IsDropDownOpen"
            && Setter(t, "Bd", "CornerRadius") is not null);
        Assert.Contains("ComboDrop.SquareBottom", Setter(open, "Bd", "CornerRadius"));
        Assert.Contains("ComboDrop.SquareTop", Setter(open, "PopupBd", "CornerRadius"));
        var openUp = triggers.Single(t => t.Name == Wpf + "MultiTrigger"
            && t.Descendants(Wpf + "Condition").Any(c => (string?)c.Attribute("Property") == "kl:ComboDrop.OpensUp"));
        Assert.Contains("ComboDrop.SquareTop", Setter(openUp, "Bd", "CornerRadius"));
        Assert.Contains("ComboDrop.SquareBottom", Setter(openUp, "PopupBd", "CornerRadius"));

        // The Win98 field stays square and shadowless: its trigger comes after the join triggers, so it wins.
        int win98 = triggers.FindIndex(t => t.Name == Wpf + "DataTrigger" && Setter(t, "PopupBd", "Effect") == "{x:Null}");
        Assert.True(win98 > triggers.IndexOf(openUp));
    }

    [Fact]
    public void ThePlainComboListJoinsItsBox()
    {
        var controls = XDocument.Load(Path.Combine(RepoRoot(), "Controls", "Controls.xaml"));
        var template = controls.Descendants(Wpf + "Style")
            .Single(s => (string?)s.Attribute("TargetType") == "ComboBox" && s.Attribute(Xaml + "Key") is null)
            .Descendants(Wpf + "ControlTemplate").First();
        var popup = template.Descendants(Wpf + "Popup").Single();
        var list = popup.Elements(Wpf + "Border").Single();
        Assert.Equal("Bottom", (string?)popup.Attribute("Placement"));
        Assert.Equal("-1", (string?)popup.Attribute("VerticalOffset"));
        Assert.Equal("PopupBd", (string?)list.Attribute(Xaml + "Name"));
        Assert.Equal("22,0,22,26", (string?)list.Attribute("Margin"));

        var triggers = template.Element(Wpf + "ControlTemplate.Triggers")!.Elements().ToList();
        var up = triggers.Single(t => (string?)t.Attribute("Property") == "kl:ComboDrop.OpensUp");
        Assert.Equal("Top", Setter(up, "PART_Popup", "Placement"));
        Assert.Equal("22,18,22,0", Setter(up, "PopupBd", "Margin"));
        var open = triggers.Single(t => t.Name == Wpf + "Trigger" && (string?)t.Attribute("Property") == "IsDropDownOpen");
        Assert.Contains("ComboDrop.SquareBottom", Setter(open, "border", "CornerRadius"));
        Assert.Contains("ComboDrop.SquareTop", Setter(open, "PopupBd", "CornerRadius"));
        var openUp = triggers.Single(t => t.Name == Wpf + "MultiTrigger");
        Assert.Contains("ComboDrop.SquareTop", Setter(openUp, "border", "CornerRadius"));
        Assert.Contains("ComboDrop.SquareBottom", Setter(openUp, "PopupBd", "CornerRadius"));
    }

    [Fact]
    public void TheFooterZoomListOpensUpwardOffItsBox()
    {
        var main = XDocument.Load(Path.Combine(RepoRoot(), "Shell", "MainWindow.xaml"));
        var template = main.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "FooterZoomCombo")
            .Descendants(Wpf + "ControlTemplate").First();
        var popup = template.Descendants(Wpf + "Popup").Single(p => (string?)p.Attribute(Xaml + "Name") == "PART_Popup");
        var list = popup.Elements(Wpf + "Border").Single();
        Assert.Equal("Top", (string?)popup.Attribute("Placement"));
        Assert.Equal("1", (string?)popup.Attribute("VerticalOffset"));
        Assert.Equal("22,18,22,0", (string?)list.Attribute("Margin"));
        Assert.Equal("{DynamicResource ComboPopupShadow}", (string?)list.Attribute("Effect"));

        var open = template.Element(Wpf + "ControlTemplate.Triggers")!.Elements()
            .Single(t => (string?)t.Attribute("Property") == "IsDropDownOpen");
        Assert.Contains("ComboDrop.SquareTop", Setter(open, "Bd", "CornerRadius"));
        Assert.Contains("ComboDrop.SquareBottom", Setter(open, "PopupBd", "CornerRadius"));
    }

    [Fact]
    public void TheRecentLocationsListJoinsThePathBox()
    {
        var dialog = XDocument.Load(Path.Combine(RepoRoot(), "Controls", "FileDialog.xaml"));
        var popup = dialog.Descendants(Wpf + "Popup").Single(p => (string?)p.Attribute(Xaml + "Name") == "RecentsPopup");
        var list = popup.Elements(Wpf + "Border").Single();
        Assert.Equal("-1", (string?)popup.Attribute("VerticalOffset"));
        Assert.Equal("RecentsPopup_Opened", (string?)popup.Attribute("Opened"));
        Assert.Equal("RecentsPopup_Closed", (string?)popup.Attribute("Closed"));
        Assert.Equal("22,0,22,26", (string?)list.Attribute("Margin"));

        string code = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "FileDialog.xaml.cs"));
        Assert.Contains("ComboDrop.SquareBottom.Convert(box.CornerRadius", code);
        Assert.Contains("box.ClearValue(Border.CornerRadiusProperty);", code);
    }

    [Fact]
    public void TheMacroStepPickerOpensUpward()
    {
        string macro = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "MacroDialog.cs"));
        Assert.Contains("ComboDrop.SetOpensUp(_operation, true);", macro);
    }

    private static string? Setter(XElement trigger, string target, string property)
        => (string?)trigger.Elements(Wpf + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("TargetName") == target && (string?)s.Attribute("Property") == property)
            ?.Attribute("Value");

    private static XElement DarkComboTemplate()
    {
        var controls = XDocument.Load(Path.Combine(RepoRoot(), "Controls", "Controls.xaml"));
        return controls.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "DarkComboBox")
            .Descendants(Wpf + "ControlTemplate").First();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Controls", "Controls.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
