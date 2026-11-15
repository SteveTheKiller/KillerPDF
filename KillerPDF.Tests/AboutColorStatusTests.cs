using System.IO;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// About shows the version, release date, publisher, thumbprint and hash, as in every Killer Tools app. The version
/// opens the release notes, which is where work in progress is described, so the card carries no status line.
/// </summary>
public sealed class AboutColorStatusTests
{
    private const string Key = "Str_About_ColorStatus";

    [Fact]
    public void AboutHasNoStatusLineUnderTheVersion()
    {
        string root = RepositoryRoot();
        Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(root, "Shell", "MainWindow.xaml")), StringComparison.Ordinal);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Strings"), "*.xaml"))
            Assert.DoesNotContain(Key, File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionOpensTheReleaseNotes()
    {
        string root = RepositoryRoot();
        Assert.Contains("MouseLeftButtonDown=\"AboutVersion_Click\"", File.ReadAllText(Path.Combine(root, "Shell", "MainWindow.xaml")), StringComparison.Ordinal);
        Assert.Contains("AboutVersion_Click(object sender, MouseButtonEventArgs e) => _about.OpenReleaseNotes();",
            File.ReadAllText(Path.Combine(root, "Shell", "About.cs")), StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }
}
