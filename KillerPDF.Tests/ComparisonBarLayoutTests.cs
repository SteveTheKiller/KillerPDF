using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ComparisonBarLayoutTests
{
    [Fact]
    public void ComparisonBarUsesReservedBottomRowAndDetailsOpenUpward()
    {
        string root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement splitHost = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "SplitHost");
        XElement rowDefinitions = splitHost.Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions");
        Assert.Equal(new[] { "*", "Auto" }, rowDefinitions.Elements()
            .Select(element => (string?)element.Attribute("Height")));

        XElement comparisonBar = splitHost.Elements()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonBar");
        Assert.Equal("1", (string?)comparisonBar.Attribute("Grid.Row"));
        Assert.Equal("Stretch", (string?)comparisonBar.Attribute("HorizontalAlignment"));
        Assert.Null(comparisonBar.Attribute("VerticalAlignment"));
        Assert.Equal("0", (string?)comparisonBar.Attribute("Margin"));
        Assert.Equal("0", (string?)comparisonBar.Attribute("BorderThickness"));
        Assert.Equal("0", (string?)comparisonBar.Attribute("CornerRadius"));
        Assert.DoesNotContain(comparisonBar.Elements(), element => element.Name.LocalName == "Border.Effect");

        XElement dividerStem = splitHost.Elements()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonDividerStem");
        Assert.Equal("0", (string?)dividerStem.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)dividerStem.Attribute("Grid.Column"));
        Assert.Equal("4", (string?)dividerStem.Attribute("Width"));
        Assert.Equal("24", (string?)dividerStem.Attribute("Height"));
        Assert.Equal("0,0,0,-2", (string?)dividerStem.Attribute("Margin"));
        Assert.Equal("{DynamicResource ComparisonBarBrush}", (string?)dividerStem.Attribute("Background"));
        Assert.Equal("{Binding Visibility, ElementName=ComparisonBar}", (string?)dividerStem.Attribute("Visibility"));

        XElement dockContents = comparisonBar.Elements().Single(element => element.Name.LocalName == "Grid");
        XElement accentFace = dockContents.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonAccentFace");
        Assert.Equal("{DynamicResource ComparisonBarBrush}", (string?)accentFace.Attribute("Background"));
        Assert.Single(comparisonBar.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{DynamicResource Str_Compare_Bar}");

        XElement comparisonButtonStyle = document.Descendants()
            .Single(element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute(x + "Key") == "ComparisonBarButton");
        Assert.DoesNotContain(comparisonButtonStyle.Descendants(), element =>
            element.Name.LocalName == "DropShadowEffect");
        XElement comparisonHoverTrigger = comparisonButtonStyle.Descendants()
            .Single(element => element.Name.LocalName == "Trigger" &&
                (string?)element.Attribute("Property") == "IsMouseOver");
        Assert.Contains(comparisonHoverTrigger.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Background" &&
            (string?)element.Attribute("Value") == "{StaticResource ComparisonBarHoverBrush}");

        XElement comparisonCloseStyle = document.Descendants()
            .Single(element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute(x + "Key") == "ComparisonCloseButton");
        Assert.Equal("{StaticResource OverlayCloseButton}", (string?)comparisonCloseStyle.Attribute("BasedOn"));

        XElement comparisonClose = comparisonBar.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonCloseButtonControl");
        Assert.Equal("{StaticResource ComparisonCloseButton}", (string?)comparisonClose.Attribute("Style"));
        Assert.Equal("10,0,0,0", (string?)comparisonClose.Attribute("Margin"));
        Assert.Contains(comparisonClose.Descendants(), element =>
            element.Name.LocalName == "Path" &&
            (string?)element.Attribute("Data") == "M 1,1 L 10,10 M 10,1 L 1,10");
        Assert.Contains(comparisonClose.Descendants(), element =>
            element.Name.LocalName == "Run" &&
            (string?)element.Attribute("Text") == " (Esc)");

        XElement comparisonTextStyle = comparisonBar.Descendants()
            .Single(element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute("TargetType") == "TextBlock");
        Assert.Contains(comparisonTextStyle.Elements(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Effect" &&
            (string?)element.Attribute("Value") == "{DynamicResource ComparisonBarTextEffect}");

        Assert.DoesNotContain(comparisonBar.Descendants(), element =>
            (string?)element.Attribute("Foreground") == "White" ||
            (string?)element.Attribute("Foreground") == "{StaticResource ComparisonBarForegroundBrush}");

        string themeManager = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));
        Assert.Contains("ApplyComparisonBarPalette(liveResources, theme);", themeManager);
        Assert.Contains("case Theme.Blood:", themeManager);
        Assert.Contains("case Theme.Greed:", themeManager);
        Assert.Contains("case Theme.Cyanotic:", themeManager);
        Assert.Contains("case Theme.Ectoplasm:", themeManager);

        XElement detailsPopup = splitHost.Elements()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonDetailsPopup");
        Assert.Equal("Top", (string?)detailsPopup.Attribute("Placement"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
