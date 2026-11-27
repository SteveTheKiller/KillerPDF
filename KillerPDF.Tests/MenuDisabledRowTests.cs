using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A disabled menu row must look disabled: the IsEnabled=False trigger has to dim an element that holds the row's
/// words, not just the row's background. MainWindow's own menu row template dimmed MenuBd, a background-only border
/// beside the content, so every disabled row in the page and markup cards drew at full strength (#504: the Arrange
/// rows a single text box can't use looked available).
/// </summary>
public sealed class MenuDisabledRowTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Shell", "MainWindow.xaml")]
    [InlineData("Controls", "Controls.xaml")]
    public void ADisabledMenuRowDimsItsWords(string folder, string file)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), folder, file));
        XElement template = doc.Descendants(Wpf + "Style")
            .First(s => (string?)s.Attribute("TargetType") == "MenuItem" && s.Attribute(X + "Key") is null)
            .Descendants(Wpf + "ControlTemplate").First();

        XElement disabled = template.Descendants(Wpf + "Trigger")
            .Single(t => (string?)t.Attribute("Property") == "IsEnabled" && (string?)t.Attribute("Value") == "False");
        string target = (string)disabled.Elements(Wpf + "Setter")
            .Single(s => (string?)s.Attribute("Property") == "Opacity").Attribute("TargetName")!;

        XElement dimmed = template.Descendants().Single(e => (string?)e.Attribute(X + "Name") == target);
        Assert.Contains(dimmed.Descendants(Wpf + "ContentPresenter"),
            p => (string?)p.Attribute("ContentSource") == "Header");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shell", "MainWindow.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
