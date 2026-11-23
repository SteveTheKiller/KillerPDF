using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #509: KillerPDF needs Windows 10 or later. On Windows 7, 8 and 8.1 its icon font (Segoe MDL2
// Assets, Windows 10 and later only) is missing, so the windowed app says so once per launch.
public sealed class LegacyWindowsNoticeTests
{
    [Theory]
    [InlineData(6, 1, 7601)]   // Windows 7 SP1
    [InlineData(6, 2, 9200)]   // Windows 8
    [InlineData(6, 3, 9600)]   // Windows 8.1
    public void WindowedAppShowsTheNoticeBelowWindows10(int major, int minor, int build)
        => Assert.True(LegacyWindowsNotice.ShouldShow(new Version(major, minor, build), LaunchSurface.Window));

    [Theory]
    [InlineData(10, 0, 10240)]   // Windows 10 1507, the first build with Segoe MDL2 Assets
    [InlineData(10, 0, 19045)]   // Windows 10 22H2
    [InlineData(10, 0, 26100)]   // Windows 11 24H2
    public void WindowedAppStaysQuietOnWindows10AndLater(int major, int minor, int build)
        => Assert.False(LegacyWindowsNotice.ShouldShow(new Version(major, minor, build), LaunchSurface.Window));

    [Theory]
    [InlineData(nameof(LaunchSurface.CommandLine))]
    [InlineData(nameof(LaunchSurface.PreviewHandler))]
    [InlineData(nameof(LaunchSurface.StartupSmoke))]
    public void OnlyTheWindowedAppEverShowsTheNotice(string surface)
        => Assert.False(LegacyWindowsNotice.ShouldShow(new Version(6, 1, 7601), Enum.Parse<LaunchSurface>(surface)));

    // A torn-out tab opens a second main window in the same process. The notice belongs to the
    // launch, not to each window, so only the first claim on an old Windows gets it.
    [Fact]
    public void TheNoticeIsClaimedOncePerProcess()
    {
        var windows7 = new Version(6, 1, 7601);
        Assert.False(LegacyWindowsNotice.TryClaim(new Version(10, 0, 19045), LaunchSurface.Window));
        Assert.True(LegacyWindowsNotice.TryClaim(windows7, LaunchSurface.Window));
        Assert.False(LegacyWindowsNotice.TryClaim(windows7, LaunchSurface.Window));
    }

    // The notice warns about missing icon glyphs, so its text must not carry one, and every
    // locale must say it in its own language.
    [Fact]
    public void EveryLocaleStatesTheNoticeWithoutIconGlyphs()
    {
        string strings = Path.Combine(FindRepositoryRoot(), "Strings");
        string english = Read(Path.Combine(strings, "en-US.xaml"));
        Assert.Contains("Windows 10", english);

        string[] files = Directory.GetFiles(strings, "*.xaml");
        Assert.Equal(22, files.Length);
        foreach (string file in files)
        {
            string text = Read(file);
            string name = Path.GetFileName(file);
            Assert.False(string.IsNullOrWhiteSpace(text), name);
            Assert.DoesNotContain(text, c => c >= 0xE000 && c <= 0xF8FF);
            Assert.DoesNotContain(text, c => c == 8211 || c == 8212);
            Assert.Contains("10", text);
            if (name != "en-US.xaml") Assert.NotEqual(english, text);
        }
    }

    // The startup notice is raised from one place only: the main window, after it has loaded.
    // The command line, image verb, installer and preview handler never construct that window.
    [Fact]
    public void OnlyTheMainWindowRaisesTheNotice()
    {
        string root = FindRepositoryRoot();
        string[] callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "KillerPDF.Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("LegacyWindowsNotice.TryClaim", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();
        Assert.Equal([Path.Combine("Shell", "MainWindow.xaml.cs")], callers);
    }

    private static string Read(string file)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(file).Root!.Elements()
            .SingleOrDefault(e => (string?)e.Attribute(x + "Key") == "Str_LegacyWindowsNotice")?.Value ?? "";
    }

    // This file's own location, so the check reads the checkout it was built from.
    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(path))!;
}
