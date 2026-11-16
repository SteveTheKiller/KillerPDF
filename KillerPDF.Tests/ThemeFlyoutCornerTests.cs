using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ThemeFlyoutCornerTests
{
    [Fact]
    public void ModernThemesRoundEveryFlyoutCornerAndSquareThemesStaySquare()
    {
        // 98SE and Trash Polka are square by design; every other theme rounds its flyouts.
        string[] square = ["98SE.xaml", "TrashPolka.xaml"];
        string root = FindRepositoryRoot();
        string themeDirectory = Path.Combine(root, "Themes");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string themePath in Directory.EnumerateFiles(themeDirectory, "*.xaml"))
        {
            var document = XDocument.Load(themePath);
            var radius = document.Descendants()
                .Single(element => (string?)element.Attribute(x + "Key") == "FlyoutCornerRadius")
                .Value.Trim();

            if (square.Contains(Path.GetFileName(themePath), StringComparer.OrdinalIgnoreCase))
                Assert.Equal("0", radius);
            else
                Assert.Equal("4", radius);
        }
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
