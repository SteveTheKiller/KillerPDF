using System.IO;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The version and copyright at the right end of the footer are the KillerUI kit's FooterCorner, so their spacing
/// from the corner grip (12 px after the version, 20 px after the copyright) cannot drift from the family again.
/// Each app used to carry its own copy of that markup, and the copies moved apart one hand edit at a time.
/// </summary>
public sealed class FooterCornerTests
{
    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.csproj"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj"), .. parts]));
    }

    [Fact]
    public void TheFooterUsesTheKitCornerInsteadOfItsOwnCopy()
    {
        string window = Read("Shell", "MainWindow.xaml");
        Assert.Contains("<kfc:FooterCorner x:Name=\"FooterVersionCell\"", window);
        Assert.DoesNotContain("x:Name=\"VersionLabel\"", window);
        Assert.DoesNotContain("Steve the Killer", window);
        Assert.Contains("public const double CornerGap = 20;", Read("Controls", "FooterCorner.cs"));
    }
}
