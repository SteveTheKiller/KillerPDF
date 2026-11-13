using System.IO;
using System.Windows.Input;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ShortcutTogglePolicyTests
{
    [Fact]
    public void NewAndExistingSettingsPreserveTheUsersChoice()
    {
        Assert.True(ShortcutTogglePolicy.IsEnabled(null));
        Assert.True(ShortcutTogglePolicy.IsEnabled("1"));
        Assert.False(ShortcutTogglePolicy.IsEnabled("0"));
    }

    [Fact]
    public void RecoveryChordWorksWithEitherWpfKeyRepresentation()
    {
        var modifiers = ModifierKeys.Control | ModifierKeys.Shift;
        Assert.True(ShortcutTogglePolicy.IsToggle(Key.K, modifiers));
        Assert.True(ShortcutTogglePolicy.IsToggle(Key.System, modifiers, Key.K));
        Assert.False(ShortcutTogglePolicy.IsToggle(Key.K, ModifierKeys.Control));
        Assert.False(ShortcutTogglePolicy.IsToggle(Key.K, modifiers | ModifierKeys.Alt));
        Assert.False(ShortcutTogglePolicy.IsToggle(Key.K, ModifierKeys.Control | ModifierKeys.Alt));
    }

    [Theory]
    [InlineData(Key.S)]
    [InlineData(Key.Delete)]
    [InlineData(Key.Left)]
    [InlineData(Key.PageDown)]
    public void DisabledAppKeysCannotReachTheViewerOrSidebar(Key key)
        => Assert.True(ShortcutTogglePolicy.SuppressWhenDisabled(key, textInput: false));

    [Theory]
    [InlineData(Key.F1)]
    [InlineData(Key.F12)]
    public void DisabledShortcutsStillOpenHelpAndAbout(Key key)
    {
        Assert.True(ShortcutTogglePolicy.StaysOn(key, ModifierKeys.None, Key.None, overlayOpen: false));
        Assert.True(ShortcutTogglePolicy.StaysOn(Key.System, ModifierKeys.None, key, overlayOpen: false));
        Assert.False(ShortcutTogglePolicy.StaysOn(key, ModifierKeys.Shift, Key.None, overlayOpen: false));
        Assert.False(ShortcutTogglePolicy.StaysOn(key, ModifierKeys.Control, Key.None, overlayOpen: true));
    }

    [Fact]
    public void DisabledShortcutsLetEscCloseTheOverlayOrAboutOnly()
    {
        Assert.True(ShortcutTogglePolicy.StaysOn(Key.Escape, ModifierKeys.None, Key.None, overlayOpen: true));
        Assert.False(ShortcutTogglePolicy.StaysOn(Key.Escape, ModifierKeys.None, Key.None, overlayOpen: false));
        Assert.False(ShortcutTogglePolicy.StaysOn(Key.Escape, ModifierKeys.Shift, Key.None, overlayOpen: true));
    }

    [Theory]
    [InlineData(Key.S)]
    [InlineData(Key.F2)]
    [InlineData(Key.F11)]
    [InlineData(Key.Delete)]
    [InlineData(Key.Left)]
    public void DisabledShortcutsKeepEveryOtherKeySuppressedEvenWithTheOverlayOpen(Key key)
    {
        Assert.False(ShortcutTogglePolicy.StaysOn(key, ModifierKeys.None, Key.None, overlayOpen: true));
        Assert.True(ShortcutTogglePolicy.SuppressWhenDisabled(key, textInput: false));
    }

    [Theory]
    [InlineData(Key.S)]
    [InlineData(Key.C)]
    [InlineData(Key.Back)]
    [InlineData(Key.Left)]
    public void DisabledShortcutsStillAllowTextEditing(Key key)
        => Assert.False(ShortcutTogglePolicy.SuppressWhenDisabled(key, textInput: true));

    [Theory]
    [InlineData(Key.Tab)]
    [InlineData(Key.Space)]
    [InlineData(Key.Return)]
    public void DisabledShortcutsStillAllowNormalControlOperation(Key key)
        => Assert.False(ShortcutTogglePolicy.SuppressWhenDisabled(key, textInput: false));

    // The window is not built here, so the dispatch line is pinned in the source.
    [Fact]
    public void TheWindowAnswersTheStayOnKeysBeforeItSuppressesAnything()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "KeyboardShortcuts.cs"));
        int stayOn = source.IndexOf("ShortcutTogglePolicy.StaysOn(e.Key, Keyboard.Modifiers, e.SystemKey,", StringComparison.Ordinal);
        Assert.True(stayOn >= 0, "The window never asks whether a key still works while shortcuts are off.");
        Assert.True(stayOn < source.IndexOf("ShortcutTogglePolicy.SuppressWhenDisabled(", StringComparison.Ordinal));
        Assert.True(stayOn > source.IndexOf("!KeyboardShortcutsEnabled", StringComparison.Ordinal));
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
