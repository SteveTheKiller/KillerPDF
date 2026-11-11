using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// Shared reader for the source-pin tests. The viewer is not part of this test project, so these tests read its
// source text; each one names the exact expression a finding changed.
internal static class ViewerSourcePin
{
    public static string Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    public static string Viewer(string file) => Source("Controls", "Viewer", file);

    public static int Count(string source, string text)
    {
        int count = 0;
        for (int at = source.IndexOf(text, StringComparison.Ordinal); at >= 0; at = source.IndexOf(text, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    public static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Not found: " + start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "Not found: " + end);
        return source[from..to];
    }
}
