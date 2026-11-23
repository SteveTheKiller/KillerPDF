using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// A language is a Strings\<code>.xaml file plus five places that have to know about it: the loader, the
// culture it formats with, the picker, the Explorer preview pane (two tables, and a missing row there is a
// KeyNotFoundException inside the shell) and the installer. These read the real Strings folder, so a new
// file fails here until every one of them has it.
public sealed class LocaleWiringTests
{
    private static string[] ShippedLocales() =>
        [.. Directory.GetFiles(Locate("Strings"), "*.xaml").Select(f => Path.GetFileNameWithoutExtension(f)!)
            .OrderBy(n => n, StringComparer.Ordinal)];

    private static string Locate(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return Path.Combine([directory!.FullName, .. parts]);
    }

    [Fact]
    public void EveryLanguageLoadsItsOwnFileAndCulture()
    {
        string manager = ShellSource.Read("Services", "LocaleManager.cs");
        string files = ShellSource.Between(manager, "internal static string? FileFor(Locale locale)", "_           => null,");
        string cultures = ShellSource.Between(manager, "internal static CultureInfo CultureFor(Locale locale)", "_           => \"en-US\",");
        string firstRun = ShellSource.Between(manager, "internal static Locale LocaleForCulture(CultureInfo culture)", "_ => Locale.EnUS,");
        foreach (string code in ShippedLocales().Where(c => c != "en-US"))
        {
            // A first start on Windows in this language picks it without being asked.
            Assert.Contains("\"" + code.Split('-')[0] + "\"", firstRun);
            Assert.Contains("=> \"" + code + "\",", files);
            Assert.Matches("=> \"" + Regex.Escape(code) + "(-[A-Z]{2})?\",", cultures);
        }
    }

    [Fact]
    public void EveryLanguageIsInThePicker()
    {
        string menu = ShellSource.Read("Controls", "LanguageMenu.cs");
        foreach (string code in ShippedLocales())
            Assert.Contains(", \"" + code + "\"),", menu);
    }

    [Fact]
    public void EveryLanguageIsInBothPreviewPaneTables()
    {
        string preview = ShellSource.Read("PreviewHandler", "PreviewStrings.cs");
        foreach (string code in ShippedLocales())
            Assert.Equal(2, Regex.Matches(preview, Regex.Escape("[\"" + code + "\"] = [")).Count);
    }

    [Fact]
    public void EveryLanguageHasInstallerStrings()
    {
        foreach (string code in ShippedLocales())
            Assert.True(File.Exists(Locate("Packaging", "KillerLauncher", "Strings." + code + ".xml")),
                "The installer has no Strings." + code + ".xml.");
    }

    // The start screen's Recent rows are built in code with Loc("Str_RecentMissing") baked in, so a live
    // language switch has to rebuild them or a moved file keeps saying "missing" in the old language.
    [Fact]
    public void ALanguageSwitchRebuildsTheRecentList()
    {
        string panel = ShellSource.Read("Shell", "SettingsPanel.cs");
        string relocalize = ShellSource.Between(panel, "partial void RelocalizeAppUi()", "private void RefreshOpenAnnotationBars()");
        Assert.Contains("PopulateRecentFilesList();", relocalize);
    }
}
