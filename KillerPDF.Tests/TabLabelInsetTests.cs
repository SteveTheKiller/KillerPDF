using System;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// KillerPDF's tabs have no icon, so with the kit's 2px tab padding the title touched the tab's
// left edge. The title and the drag ghost take an app inset instead; 98SE already pads 8px.
public sealed class TabLabelInsetTests
{
    [Fact]
    public void TabTitlesAndTheDragGhostUseTheAppInset()
    {
        string root = FindRepositoryRoot();
        string viewer = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.xaml"));
        int label = viewer.IndexOf("x:Name=\"tabLbl\"", StringComparison.Ordinal);
        Assert.True(label >= 0);
        string labelTag = viewer[label..viewer.IndexOf("/>", label, StringComparison.Ordinal)];
        Assert.Contains("Margin=\"{DynamicResource TabLabelInset}\"", labelTag, StringComparison.Ordinal);

        string window = File.ReadAllText(Path.Combine(root, "Shell", "MainWindow.xaml"));
        int ghost = window.IndexOf("x:Name=\"TabDragGhostText\"", StringComparison.Ordinal);
        Assert.True(ghost >= 0);
        string ghostTag = window[ghost..window.IndexOf(">", ghost, StringComparison.Ordinal)];
        Assert.Contains("Margin=\"{DynamicResource TabLabelInset}\"", ghostTag, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryThemeButNinetyEightSeGetsTheInset()
    {
        string theme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Services", "AppTheme.cs"));
        Assert.Contains("d[\"TabLabelInset\"] = theme == Theme.SE98 ? new Thickness(0) : new Thickness(7, 0, 0, 0);",
            theme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
