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
        var document = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement toolbarStyle = document.Descendants()
            .Single(element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute(x + "Key") == "ToolbarButton");
        XElement selectedTrigger = toolbarStyle.Descendants()
            .Single(element => element.Name.LocalName == "Trigger" &&
                (string?)element.Attribute("Property") == "Tag" &&
                (string?)element.Attribute("Value") == "selected");
        Assert.Contains(selectedTrigger.Elements(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("TargetName") == "ToolbarButtonContent" &&
            (string?)element.Attribute("Property") == "Effect" &&
            (string?)element.Attribute("Value") == "{x:Null}");

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
