using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FooterContrastTests
{
    [Fact]
    public void StatusAndControlsUseNormalTextWhileMetadataStaysDim()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string key in new[] { "FooterNavButton", "FooterModeButton" })
        {
            XElement style = document.Descendants().Single(element =>
                element.Name.LocalName == "Style" &&
                (string?)element.Attribute(x + "Key") == key);
            Assert.Contains(style.Elements(), element =>
                element.Name.LocalName == "Setter" &&
                (string?)element.Attribute("Property") == "Foreground" &&
                (string?)element.Attribute("Value") == "{DynamicResource TextBrush}");
        }

        XElement status = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "StatusText");
        Assert.Equal("{DynamicResource TextBrush}", (string?)status.Attribute("Foreground"));

        XElement version = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "VersionLabel");
        Assert.Equal("{DynamicResource TextFooter}", (string?)version.Attribute("Foreground"));
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
