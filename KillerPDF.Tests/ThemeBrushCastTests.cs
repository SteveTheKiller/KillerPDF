using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A theme may state any brush as a gradient (Blood, Greed and Cyanotic do for their selection
/// rows), and the theme engine builds gradient button fills. Code that casts a theme resource to
/// SolidColorBrush then throws InvalidCastException the moment that theme is active (#442, #443).
/// </summary>
public sealed class ThemeBrushCastTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NoThemeKeyThatCanBeAGradientIsCastToSolidColorBrush()
    {
        string root = FindRepositoryRoot();
        var gradients = GradientKeys(root);
        Assert.Contains("RowSelectedBrush", gradients);

        var offenders = new List<string>();
        foreach (string file in AppSources(root))
        {
            string text = File.ReadAllText(file);
            string relative = Path.GetRelativePath(root, file);
            foreach (Match m in Regex.Matches(text,
                         @"\(SolidColorBrush\)\s*(?:Application\.Current\.)?(?:TryFindResource|FindResource|Resources\[)\(?\s*""(?<key>\w+)"""))
                if (gradients.Contains(m.Groups["key"].Value)) offenders.Add($"{relative}: {m.Groups["key"].Value}");

            // A local R(key) helper that casts every lookup to SolidColorBrush.
            if (!Regex.IsMatch(text, @"static\s+SolidColorBrush\s+R\(\s*string\s+key\s*\)")) continue;
            foreach (Match m in Regex.Matches(text, @"\bR\(""(?<key>\w+)""\)"))
                if (gradients.Contains(m.Groups["key"].Value)) offenders.Add($"{relative}: R(\"{m.Groups["key"].Value}\")");
        }
        Assert.True(offenders.Count == 0, "Gradient-capable keys cast to SolidColorBrush:\n" + string.Join("\n", offenders.Distinct()));
    }

    // Any theme is free to state any surface as a gradient, not only the keys that are gradients
    // today, so no theme lookup is hard-cast to SolidColorBrush whatever its key.
    [Fact]
    public void NoThemeLookupIsCastToSolidColorBrush()
    {
        string root = FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (string file in AppSources(root))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"\(SolidColorBrush\)\s*(?:Application\.Current\.)?(?:\w+\.)?(?:TryFindResource|FindResource|Resources\s*\[)")
                    || Regex.IsMatch(lines[i], @"static\s+SolidColorBrush\s+R\(\s*string\s+key\s*\)"))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
        }
        Assert.True(offenders.Count == 0, "Theme lookups cast to SolidColorBrush:\n" + string.Join("\n", offenders));
    }

    private static HashSet<string> GradientKeys(string root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "Themes"), "*.xaml", SearchOption.AllDirectories))
            foreach (var element in XDocument.Load(path).Descendants()
                         .Where(e => e.Name.LocalName is "LinearGradientBrush" or "RadialGradientBrush"))
                if ((string?)element.Attribute(Xaml + "Key") is { } key) keys.Add(key);

        foreach (string engine in new[] { "ThemeManager.cs", "AppTheme.cs" })
        {
            string text = File.ReadAllText(Path.Combine(root, "Services", engine));
            foreach (Match m in Regex.Matches(text, @"\[""(?<key>\w+)""\]\s*=\s*(?:new\s+(?:Linear|Radial)GradientBrush|ButtonFill\()"))
                keys.Add(m.Groups["key"].Value);
        }
        return keys;
    }

    private static IEnumerable<string> AppSources(string root)
    {
        string[] skipped = ["bin", "obj", "engine", "third_party", "tests", "tools", "KillerPDF.Tests", "Packaging"];
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
                .Any(part => skipped.Contains(part, StringComparer.OrdinalIgnoreCase)));
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
