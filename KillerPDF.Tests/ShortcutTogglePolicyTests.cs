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
    [InlineData(Key.F1)]
    [InlineData(Key.F12)]
    [InlineData(Key.Delete)]
    [InlineData(Key.Left)]
    [InlineData(Key.PageDown)]
    public void DisabledAppKeysCannotReachTheViewerOrSidebar(Key key)
        => Assert.True(ShortcutTogglePolicy.SuppressWhenDisabled(key, textInput: false));

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
}
