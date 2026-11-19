using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Translation parity proves the twenty locale files agree with each other, not that the code's
/// keys exist. A key missing from en-US shows the raw key name on screen (Loc returns the key) or
/// leaves an accessible name empty, in every language at once.
/// </summary>
public sealed class StringKeyCoverageTests
{
    [Fact]
    public void EveryStringKeyTheAppNamesIsDefinedInEnglish()
    {
        string root = FindRepositoryRoot();
        var defined = Regex.Matches(File.ReadAllText(Path.Combine(root, "Strings", "en-US.xaml")), @"x:Key=""(Str_\w+)""")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in AppSources(root))
        {
            // Keys built from a prefix ("Str_Color_" + accent) end in an underscore and are skipped.
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""(Str_\w*[^_\W])""|Resource (Str_\w*[^_\W])\}"))
            {
                string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!defined.Contains(key)) missing.Add($"{key} ({Path.GetRelativePath(root, file)})");
            }
        }
        Assert.True(missing.Count == 0, "String keys used but not defined in Strings/en-US.xaml:\n" + string.Join("\n", missing));
    }

    // #506: a XAML attribute the user reads or hears, written as literal words instead of a string
    // resource, shows English in every language. Icon glyphs, the wordmark pieces and web addresses
    // are not words to translate.
    [Fact]
    public void NoVisibleXamlTextIsWrittenAsLiteralWords()
    {
        string root = FindRepositoryRoot();
        var visible = new Regex(@"(?<![\w.:])(Text|Content|Header|ToolTip|Title|Label|Watermark|PlaceholderText|AutomationProperties\.Name|AutomationProperties\.HelpText|InputGestureText)=""(?!\{)([^""]*)""");
        string[] brand = ["Killer", "PDF", "KillerPDF", "App"];
        var literal = new List<string>();
        foreach (string file in AppSources(root).Where(file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
                foreach (Match m in visible.Matches(lines[index]))
                {
                    string value = Regex.Replace(m.Groups[2].Value, "&#x[0-9A-Fa-f]+;", "");
                    if (!Regex.IsMatch(value, "[A-Za-z]{2,}") || brand.Contains(value)
                        || Regex.IsMatch(value, @"^(https?://\S+|[\w.-]+\.(net|com|org))$")) continue;
                    literal.Add($"{Path.GetRelativePath(root, file)}:{index + 1}: {m.Value}");
                }
        }
        Assert.True(literal.Count == 0, "Visible XAML text written as literal words; use a Str_ resource:\n" + string.Join("\n", literal));
    }

    // The theme menu names each accent pill for screen readers with "Str_Color_" + the accent, a
    // prefix-built key the scan above skips, so every accent is checked here.
    [Fact]
    public void EveryAccentHasAColorNameInEveryLanguage()
    {
        string root = FindRepositoryRoot();
        Match declaration = Regex.Match(File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs")),
            @"enum Accent\s*\{([^}]*)\}");
        Assert.True(declaration.Success, "The Accent enum was not found in Services/ThemeManager.cs.");
        string[] accents = [.. declaration.Groups[1].Value.Split(',').Select(name => name.Trim()).Where(name => name.Length > 0)];
        Assert.Equal(8, accents.Length);
        foreach (string locale in Directory.EnumerateFiles(Path.Combine(root, "Strings"), "*.xaml"))
        {
            string text = File.ReadAllText(locale);
            foreach (string accent in accents)
                Assert.True(text.Contains($"x:Key=\"Str_Color_{accent}\"", StringComparison.Ordinal),
                    $"{Path.GetFileName(locale)} has no Str_Color_{accent}.");
        }
    }

    private static IEnumerable<string> AppSources(string root)
    {
        string[] skipped = ["bin", "obj", "engine", "third_party", "tests", "tools", "KillerPDF.Tests", "Strings", "pdf-landing", "validation", "Packaging"];
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
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
