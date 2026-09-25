using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TitleBarLayoutTests
{
    [Fact]
    public void PlainAppTitleUsesThemeAlignmentOffset()
    {
        string root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        var theme = XDocument.Load(Path.Combine(root, "Themes", "98SE.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement title = document.Descendants()
            .Single(element => element.Name.LocalName == "TextBlock" &&
                (string?)element.Attribute("Text") == "KillerPDF" &&
                (string?)element.Attribute("Visibility") == "{DynamicResource PlainTitleVisibility}");
        Assert.Equal("{DynamicResource TitleTextMargin}", (string?)title.Attribute("Margin"));

        XElement margin = theme.Descendants()
            .Single(element => (string?)element.Attribute(x + "Key") == "TitleTextMargin");
        Assert.Equal("0,-1,0,0", margin.Value);
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
