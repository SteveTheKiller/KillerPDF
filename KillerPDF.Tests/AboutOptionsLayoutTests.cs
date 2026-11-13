using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// The About card's settings sit on shared rows, so a label that wraps in one column makes its own row taller instead
// of shifting the other column out of line and leaving a gap under the short item beside it.
public sealed class AboutOptionsLayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Named(XDocument xaml, string name) =>
        xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Theory]
    [InlineData("AboutStartupUpdateCheck", "0", "0")]
    [InlineData("AboutCloseToTray", "1", "0")]
    [InlineData("NoRecentCheck", "1", "2")]
    [InlineData("LinkConfirmCheck", "2", "0")]
    public void EachSettingSitsOnASharedRow(string name, string row, string column)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        var xaml = XDocument.Load(Path.Combine(dir!.FullName, "Shell", "MainWindow.xaml"));
        XElement check = Named(xaml, name);
        Assert.Equal("Grid", check.Parent!.Name.LocalName);
        Assert.Equal(row, (string?)check.Attribute("Grid.Row") ?? "0");
        Assert.Equal(column, (string?)check.Attribute("Grid.Column") ?? "0");
        Assert.Equal("Top", (string?)check.Attribute("VerticalAlignment"));
    }
}
