using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The What's new card (Shell/WhatsNew.cs) shows the running version's changelog once after an
/// update, read from the CHANGELOG.md the app embeds. These pin the parser against the real
/// changelog and the card's markup and strings.
/// </summary>
public sealed class WhatsNewTests
{
    [Fact]
    public void TheCurrentVersionHasNotesInTheChangelog()
    {
        string root = RepositoryRoot();
        string version = Regex.Match(File.ReadAllText(Path.Combine(root, "KillerPDF.csproj")), @"<Version>([^<]+)</Version>").Groups[1].Value;
        ReleaseNotes? notes = ReleaseNotes.FromChangelog(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")), version);
        Assert.NotNull(notes);
        Assert.NotEmpty(notes!.Sections);
        Assert.All(notes.Sections, s => Assert.NotNull(ReleaseNotes.HeadingKey(s.Heading)));
        // Two bullets run together ("shows it.- While") would read as one in the card.
        Assert.All(notes.Sections.SelectMany(s => s.Items), item => Assert.DoesNotMatch(@"\.- \S", item));
    }

    [Fact]
    public void TheChangelogIsEmbedded()
    {
        Assert.Contains("<EmbeddedResource Include=\"CHANGELOG.md\" LogicalName=\"CHANGELOG.md\" />",
            File.ReadAllText(Path.Combine(RepositoryRoot(), "KillerPDF.csproj")), StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesSummarySectionsAndContinuationLines()
    {
        const string log = "# Log\n\n## [2.0.0] - Unreleased\n\nA summary\nover two lines.\n\n### Fixed\n\n- One [link](http://x) and **bold**.\n  carried on.\n- Two `code`.\n\n### Added\n\n- Three.\n\n## [1.0.0] - 2026-01-01\n\n### Fixed\n\n- Old.\n";
        ReleaseNotes notes = ReleaseNotes.FromChangelog(log, "v2.0.0-pre+abc")!;
        Assert.Equal("2.0.0", notes.Version);
        Assert.Equal("A summary over two lines.", notes.Summary);
        Assert.Equal(new[] { "Fixed", "Added" }, notes.Sections.Select(s => s.Heading));
        Assert.Equal(new[] { "One link and bold. carried on.", "Two code." }, notes.Sections[0].Items);
        Assert.Null(ReleaseNotes.FromChangelog(log, "3.0.0"));
    }

    [Fact]
    public void AnyOtherLevelTwoHeadingEndsTheVersion()
    {
        const string log = "## [1.0.0] - 2026-01-01\n\n### Fixed\n\n- New.\n\n## Previously released as KillerFind\n\n- Old.\n";
        Assert.Equal(new[] { "New." }, ReleaseNotes.FromChangelog(log, "1.0.0")!.Sections.Single().Items);
    }

    [Theory]
    [InlineData("1.9.6", "1.9.5", true)]
    [InlineData("1.9.6", "1.9.6", false)]
    [InlineData("1.9.5", "1.9.6", false)]
    [InlineData("1.10.0", "1.9.6", true)]
    [InlineData("1.9.6", null, true)]
    [InlineData("1.9.6", "garbage", true)]
    public void NewerComparesVersionsNotText(string current, string? seen, bool newer)
        => Assert.Equal(newer, ReleaseNotes.IsNewer(current, seen));

    [Fact]
    public void TheCardAndItsStringsAreThere()
    {
        string root = RepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "Shell", "MainWindow.xaml"));
        foreach (string name in new[] { "WhatsNewOverlay", "WhatsNewTitle", "WhatsNewList", "WhatsNewScroller", "WhatsNewShowCheck", "AboutWhatsNewCheck" })
            Assert.Contains($"x:Name=\"{name}\"", xaml, StringComparison.Ordinal);
        string[] keys = { "Str_WhatsNew_Title", "Str_WhatsNew_GotIt", "Str_WhatsNew_ShowAfterUpdates", "Str_WhatsNew_Security",
            "Str_WhatsNew_Fixed", "Str_WhatsNew_Changed", "Str_WhatsNew_Added", "Str_WhatsNew_Removed", "Str_WhatsNew_Deprecated" };
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Strings"), "*.xaml"))
        {
            string text = File.ReadAllText(file);
            foreach (string key in keys)
                Assert.True(text.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal), $"{Path.GetFileName(file)} lacks {key}");
            Assert.Contains("{0}", Regex.Match(text, "x:Key=\"Str_WhatsNew_Title\">([^<]*)<").Groups[1].Value, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }
}
