using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Full screen hides the sidebar and zeroes its column. F9 and the side keys still reached the sidebar
/// toggle and the side switch, which opened a blank column over the page and flipped the collapsed
/// state behind the full-screen view, so leaving full screen restored an inconsistent layout until
/// F9 was pressed again (BF-S007). Both are ignored while full screen is on; the side keys are
/// Ctrl+Alt+Shift+arrows now (Steve 2026-11-19).
/// </summary>
public sealed class SidebarFullScreenShortcutTests
{
    [Fact]
    public void TheSidebarKeysAreIgnoredInFullScreen()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "KeyboardShortcuts.cs"));
        Match toggle = Regex.Match(source, @"else if \(key == Key\.F9 && mods == ModifierKeys\.None([^)]*)\)");
        Assert.True(toggle.Success, "The F9 branch was not found.");
        Assert.Contains("&& !_fullScreen", toggle.Groups[1].Value);
        Assert.Contains("if (mods == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) && !_fullScreen", source);
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
