using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// 98SE caption buttons follow the Win98 rule: (band - 4) tall and (band - 2) wide, so a 20px dialog
// band gets an 18x16 X, centered. The caption glyph is 10px on every theme: Win98's 8px glyph on its
// 16x14 face scales to 10px on the 20x18 face, and 8px made the X and minimize line too small.
public sealed class CaptionGlyphTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void ChromeButtonTakesItsGlyphSizeFromTheTheme()
    {
        var xaml = XDocument.Load(Path.Combine(Root(), "Controls", "Controls.xaml"));
        var style = Assert.Single(xaml.Descendants(), e => e.Name.LocalName == "Style"
            && (string?)e.Attribute(X + "Key") == "ChromeButton");
        var size = Assert.Single(style.Elements(), e => e.Name.LocalName == "Setter"
            && (string?)e.Attribute("Property") == "FontSize");
        Assert.Equal("{DynamicResource CaptionGlyphSize}", (string?)size.Attribute("Value"));
    }

    [Fact]
    public void FlatCaptionGetsTheSmallerGlyphAndBandDerivedClose()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "Services", "ThemeManager.cs"));
        Assert.Contains("combined[\"CaptionGlyphSize\"] = 10.0;", source);
        Assert.Contains("SetIfAbsent(\"DialogCloseWidth\", pickerCaption ? bar - 2 : 28.0);", source);
        Assert.Contains("SetIfAbsent(\"DialogCloseHeight\", pickerCaption ? bar - 4 : 26.0);", source);
    }

    // Every caption-band close (not the modern corner closes, which sit on AboutCloseMargin)
    // takes the band-derived DialogCloseWidth/Height and is centered in the band.
    [Theory]
    [InlineData("Shell", "MainWindow.xaml")]
    [InlineData("Controls", "ConfirmDialog.xaml")]
    [InlineData("Controls", "FileDialog.xaml")]
    public void DialogCaptionCloseTakesTheBandDerivedSize(string folder, string file)
    {
        var xaml = XDocument.Load(Path.Combine(Root(), folder, file));
        var closes = xaml.Descendants().Where(e => e.Name.LocalName == "Button"
            && (string?)e.Attribute("Content") == "{DynamicResource AboutCloseGlyph}"
            && (string?)e.Attribute("Margin") != "{DynamicResource AboutCloseMargin}").ToList();
        Assert.NotEmpty(closes);
        foreach (var close in closes)
        {
            Assert.Equal("{DynamicResource DialogCloseWidth}", (string?)close.Attribute("Width"));
            Assert.Equal("{DynamicResource DialogCloseHeight}", (string?)close.Attribute("Height"));
            Assert.Equal("Center", (string?)close.Attribute("VerticalAlignment"));
        }
    }

    // The main caption follows the same rule on its 22px title bar: 20x18.
    [Fact]
    public void MainCaptionButtonsFitTheTitleBar()
    {
        var theme = XDocument.Load(Path.Combine(Root(), "Themes", "98SE.xaml"));
        string Value(string key) => theme.Descendants().Single(e => (string?)e.Attribute(X + "Key") == key).Value;
        Assert.Equal("22", Value("TitleBarHeight"));
        Assert.Equal("20", Value("CaptionButtonWidth"));
        Assert.Equal("18", Value("CaptionButtonHeight"));
    }

    [Fact]
    public void CodeBuiltDialogClosesTakeTheBandDerivedSize()
    {
        var frame = File.ReadAllText(Path.Combine(Root(), "Controls", "DialogFrame.cs"));
        Assert.Contains("close.SetResourceReference(FrameworkElement.WidthProperty, \"DialogCloseWidth\");", frame);
        Assert.Contains("close.SetResourceReference(FrameworkElement.HeightProperty, \"DialogCloseHeight\");", frame);
    }
}
