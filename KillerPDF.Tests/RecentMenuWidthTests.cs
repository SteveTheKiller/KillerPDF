using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// The Open menu's recent-file rows had a fixed 348 px width, so the menu stayed that wide even when every name was
// short. A row sizes to its name up to that cap (a long name still trims) and stretches, so the remove X still lines up.
public sealed class RecentMenuWidthTests
{
    [Fact]
    public void RecentRowsSizeToTheirNamesUpToACap()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.csproj"))) dir = dir.Parent;
        string source = File.ReadAllText(Path.Combine(dir!.FullName, "Shell", "FileOperations.cs"));
        int start = source.IndexOf("private void OpenRecent_Click(", StringComparison.Ordinal);
        string menu = source[start..source.IndexOf("internal void RecentClearAll_Click(", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("new Grid { Width = 348", menu);
        Assert.Contains("MaxWidth = 348", menu);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Stretch", menu);
    }
}
