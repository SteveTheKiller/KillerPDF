using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ToolbarSelectedVisualTests
{
    [Fact]
    public void SelectedEditingToolSuppressesToolbarContentShadow()
    {
        string root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "Shell", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement toolbarStyle = document.Descendants()
            .Single(element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute(x + "Key") == "ToolbarButton");
        Assert.DoesNotContain(toolbarStyle.Descendants(), element =>
            element.Name.LocalName is "DropShadowEffect" or "BlurEffect" ||
            (element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Effect" &&
                (string?)element.Attribute("Value") != "{x:Null}") ||
            element.Name.LocalName.EndsWith(".Effect", StringComparison.Ordinal));

        string selectionSource = File.ReadAllText(Path.Combine(root, "Shell", "ToolSelection.cs"));
        Assert.Contains("btn.Tag = t == tool ? \"selected\" : null;", selectionSource, StringComparison.Ordinal);
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
