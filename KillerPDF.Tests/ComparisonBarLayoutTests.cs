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
        Assert.Equal("0,4,0,0", (string?)comparisonBar.Attribute("Margin"));
        Assert.Equal("0", (string?)comparisonBar.Attribute("BorderThickness"));
        Assert.Equal("0", (string?)comparisonBar.Attribute("CornerRadius"));
        Assert.DoesNotContain(comparisonBar.Elements(), element => element.Name.LocalName == "Border.Effect");

        XElement dockContents = comparisonBar.Elements().Single(element => element.Name.LocalName == "Grid");
        XElement dockColumns = dockContents.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions");
        Assert.Equal(new[] { "*", "Auto", "Auto", "*" }, dockColumns.Elements()
            .Select(element => (string?)element.Attribute("Width")));

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
