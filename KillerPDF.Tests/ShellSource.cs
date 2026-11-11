using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// Reads the app's own source files for the shell code the test project does not link: the window
// and viewer classes need a real window to construct, so what they must do is pinned here.
internal static class ShellSource
{
    internal static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    /// <summary>The text from the first <paramref name="start"/> up to the next <paramref name="end"/>.</summary>
    internal static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Not found: " + start);
        int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, "Not found after it: " + end);
        return source[from..to];
    }
}