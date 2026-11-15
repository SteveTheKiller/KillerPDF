using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// The About card's settings stack in two independent columns, so a label that wraps on one side never changes the
// spacing on the other, and Clear all Data sits in the bottom-right corner outside the rows.
public sealed class AboutOptionsLayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Window()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return XDocument.Load(Path.Combine(dir!.FullName, "Shell", "MainWindow.xaml"));
    }

    private static XElement Named(XDocument xaml, string name) =>
        xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Theory]
    [InlineData("AboutStartupUpdateCheck", "0")]
    [InlineData("AboutCloseToTray", "0")]
    [InlineData("LinkConfirmCheck", "0")]
    [InlineData("NoRecentCheck", "2")]
    public void EachColumnStacksOnItsOwn(string name, string column)
    {
        XElement check = Named(Window(), name);
        XElement stack = check.Parent!;
        Assert.Equal("StackPanel", stack.Name.LocalName);
        Assert.Equal(column, (string?)stack.Attribute("Grid.Column") ?? "0");
        Assert.Null(check.Attribute("Grid.Row"));
    }

    [Fact]
    public void ClearAllDataSitsInTheBottomRightCornerOutsideTheRows()
    {
        XElement link = Window().Descendants().Single(e => e.Name.LocalName == "Hyperlink"
            && (string?)e.Attribute("Click") == "AboutClearData_Click");
        XElement block = link.Parent!;
        Assert.Equal("Grid", block.Parent!.Name.LocalName);
        Assert.Equal("Right", (string?)block.Attribute("HorizontalAlignment"));
        Assert.Equal("Bottom", (string?)block.Attribute("VerticalAlignment"));
        Assert.Null(block.Attribute("Grid.Row"));
    }
}
