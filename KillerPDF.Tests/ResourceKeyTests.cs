using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// A DynamicResource or SetResourceReference to a key nothing defines never fails: WPF quietly falls back to an
// inherited or default value. The picture card's header icon asked for "AccentBrush" and drew black on dark menus.
public sealed class ResourceKeyTests
{
    private static readonly Regex Used = new(@"SetResourceReference\([^,]+,\s*""([A-Za-z0-9_]+)""\)|\{DynamicResource ([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
    private static readonly Regex[] Defined =
    [
        new(@"x:Key=""([A-Za-z0-9_]+)""", RegexOptions.Compiled),
        new(@"\[\s*""([A-Za-z0-9_]+)""\s*\]\s*=", RegexOptions.Compiled),
        new(@"\b(?:Mirror|SetIfAbsent|Default|Alias)\(\s*(?:[A-Za-z_]+\s*,\s*)?""([A-Za-z0-9_]+)""", RegexOptions.Compiled),
        new(@"Resources\.Add\(\s*""([A-Za-z0-9_]+)""", RegexOptions.Compiled),
    ];

    [Fact]
    public void EveryResourceTheAppAsksForIsDefined()
    {
        string root = RepoRoot();
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj|tests|KillerPDF\.Tests|engine|tools|PreviewHandler)[\\/]"))
            .ToList();
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (var pattern in Defined)
                foreach (Match m in pattern.Matches(text)) defined.Add(m.Groups[1].Value);
            foreach (Match m in Used.Matches(text))
                used.TryAdd(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, Path.GetRelativePath(root, file));
        }
        var missing = used.Where(u => !defined.Contains(u.Key)).Select(u => $"{u.Key} ({u.Value})").OrderBy(s => s).ToList();
        Assert.True(missing.Count == 0, "Undefined resource keys: " + string.Join(", ", missing));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Controls", "Viewer", "PdfViewer.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
