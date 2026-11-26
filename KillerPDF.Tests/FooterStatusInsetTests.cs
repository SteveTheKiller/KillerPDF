using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// KillerPDF has no status light in the footer corner. The kit's FooterPadding is 0, so on the twelve
/// rounded themes the text needs its own inset: 3px, which lines it up with the sidebar above it.
/// FooterStatusInset is KillerPDF's own key, completed in AppTheme, and 98SE states its cell padding
/// in KillerPDF's own 98SE values so its sunken status cell keeps the spacing it had.
/// </summary>
public sealed class FooterStatusInsetTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void RoundedThemesLineTheStatusUpWithTheSidebar()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Services", "AppTheme.cs"));
        Assert.Matches(new Regex(@"Default\(""FooterStatusInset"",\s*new Thickness\(3,\s*0,\s*0,\s*0\)\)"), source);
    }

    [Fact]
    public void The98SEStatusCellKeepsItsCellPadding()
    {
        Assert.Equal(ThicknessValue("98SE.xaml", "FooterCellPadding"),
            ThicknessValue(Path.Combine("App", "98SE.xaml"), "FooterStatusInset"));
    }

    [Fact]
    public void TheKitPaletteDoesNotDefineTheInset()
    {
        // A kit key would outrank KillerPDF's value; the inset must stay an app key.
        string kit = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Services", "ThemeManager.cs"));
        Assert.DoesNotContain("FooterStatusInset", kit);
    }

    [Fact]
    public void TheFooterStatusTextTakesTheInset()
    {
        string window = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "MainWindow.xaml"));
        Match status = Regex.Match(window, @"<TextBlock x:Name=""StatusText""[^>]*>", RegexOptions.Singleline);
        Assert.True(status.Success, "MainWindow.xaml has no StatusText.");
        Assert.Contains(@"Margin=""{DynamicResource FooterStatusInset}""", status.Value);
    }

    private static string ThicknessValue(string themeFile, string key)
    {
        var element = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Themes", themeFile)).Descendants()
            .SingleOrDefault(e => (string?)e.Attribute(Xaml + "Key") == key);
        Assert.True(element is not null, $"{themeFile} does not declare {key}.");
        return element!.Value.Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
