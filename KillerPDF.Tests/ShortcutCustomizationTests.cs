using System.Windows.Input;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ShortcutCustomizationTests
{
    private static (ShortcutCustomization.Store Store, Dictionary<string, string> Backing) NewStore()
    {
        var backing = new Dictionary<string, string>();
        var store = new ShortcutCustomization.Store(
            name => backing.TryGetValue(name, out string? value) ? value : null,
            (name, value) => backing[name] = value,
            name => backing.Remove(name));
        return (store, backing);
    }

    [Fact]
    public void FormatChord_OrdersModifiersCanonically()
    {
        Assert.Equal("Ctrl+Shift+P",
            ShortcutCustomization.FormatChord(Key.P, ModifierKeys.Shift | ModifierKeys.Control));
        Assert.Equal("Delete",
            ShortcutCustomization.FormatChord(Key.Delete, ModifierKeys.None));
    }

    [Theory]
    [InlineData("Ctrl+Shift+P", Key.P, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("ctrl+p", Key.P, ModifierKeys.Control)]
    [InlineData("Delete", Key.Delete, ModifierKeys.None)]
    [InlineData("Del", Key.Delete, ModifierKeys.None)]
    public void TryParseChord_RoundTrips(string text, Key key, ModifierKeys mods)
    {
        Assert.True(ShortcutCustomization.TryParseChord(text, out Key parsedKey, out ModifierKeys parsedMods));
        Assert.Equal(key, parsedKey);
        Assert.Equal(mods, parsedMods);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+LWin")]
    [InlineData("Ctrl+NotAKey")]
    public void TryParseChord_RejectsJunk(string text)
    {
        Assert.False(ShortcutCustomization.TryParseChord(text, out _, out _));
    }

    [Theory]
    [InlineData("P", true, false, false, "Ctrl+P")]
    [InlineData("P", true, true, false, "Ctrl+Shift+P")]
    [InlineData("Delete", false, false, false, "Delete")]
    [InlineData("LeftCtrl", true, false, false, null)]
    [InlineData("LWin", true, false, false, null)]
    [InlineData("", true, false, false, null)]
    public void Canonicalize_FormsChordsOrRejects(string key, bool ctrl, bool shift, bool alt,
        string? expected)
    {
        Assert.Equal(expected, ShortcutCustomization.Canonicalize(key, ctrl, shift, alt));
    }

    [Fact]
    public void EffectiveChord_FallsBackToDefault()
    {
        var (store, _) = NewStore();
        var effective = ShortcutCustomization.EffectiveChord(store, "Print");
        Assert.Equal(Key.P, effective.Key);
        Assert.Equal(ModifierKeys.Control, effective.Mods);
        Assert.False(ShortcutCustomization.IsRemapped(store, "Print"));
    }

    [Fact]
    public void TryResolve_HitsOverrideAndSwallowsAbandonedDefault()
    {
        var (store, _) = NewStore();
        Assert.True(ShortcutCustomization.TrySet(store, "Print", Key.R,
            ModifierKeys.Control | ModifierKeys.Shift, out _));

        Assert.True(ShortcutCustomization.TryResolve(store, "Ctrl+Shift+R", out string? hit));
        Assert.Equal("Print", hit);

        // The old Ctrl+P must not reach the built-in branch anymore.
        Assert.True(ShortcutCustomization.TryResolve(store, "Ctrl+P", out string? swallowed));
        Assert.Null(swallowed);

        Assert.False(ShortcutCustomization.TryResolve(store, "Ctrl+Q", out _));
    }

    [Fact]
    public void TrySet_BackToDefault_ClearsOverride()
    {
        var (store, backing) = NewStore();
        Assert.True(ShortcutCustomization.TrySet(store, "Print", Key.R,
            ModifierKeys.Control | ModifierKeys.Shift, out _));
        Assert.True(ShortcutCustomization.IsRemapped(store, "Print"));

        Assert.True(ShortcutCustomization.TrySet(store, "Print", Key.P, ModifierKeys.Control, out _));
        Assert.False(ShortcutCustomization.IsRemapped(store, "Print"));
        Assert.Empty(backing);
    }

    [Fact]
    public void TrySet_RequiresCtrlOrAlt()
    {
        var (store, _) = NewStore();
        Assert.False(ShortcutCustomization.TrySet(store, "Print", Key.R, ModifierKeys.None, out _));
        Assert.False(ShortcutCustomization.TrySet(store, "Print", Key.R, ModifierKeys.Shift, out _));
    }

    [Fact]
    public void TrySet_RejectsClashes_WithNames()
    {
        var (store, _) = NewStore();
        // Ctrl+S belongs to Save.
        Assert.False(ShortcutCustomization.TrySet(store, "Print", Key.S, ModifierKeys.Control,
            out string clash));
        Assert.Equal("Str_Lbl_Save", clash);

        // So does another remapped action's new chord.
        Assert.True(ShortcutCustomization.TrySet(store, "Save", Key.Q, ModifierKeys.Alt, out _));
        Assert.False(ShortcutCustomization.TrySet(store, "Print", Key.Q, ModifierKeys.Alt,
            out clash));
        Assert.Equal("Str_Lbl_Save", clash);
    }

    [Fact]
    public void EffectiveTokens_UsesOverlayTokenStyle()
    {
        var (store, _) = NewStore();
        Assert.Equal("%ctrl%+P", ShortcutCustomization.EffectiveTokens(store, "Print"));
        Assert.Equal("%del%", ShortcutCustomization.EffectiveTokens(store, "DeleteAnnot"));

        Assert.True(ShortcutCustomization.TrySet(store, "Print", Key.R,
            ModifierKeys.Control | ModifierKeys.Shift, out _));
        Assert.Equal("%ctrl%+%shift%+R", ShortcutCustomization.EffectiveTokens(store, "Print"));
    }

    [Fact]
    public void EffectiveKeysForRow_RewritesOnlyTheDefaultRow()
    {
        var (store, _) = NewStore();
        Assert.True(ShortcutCustomization.TrySet(store, "Redo", Key.R,
            ModifierKeys.Control | ModifierKeys.Shift, out _));

        // The Ctrl+Y row follows the remap...
        Assert.Equal("%ctrl%+%shift%+R",
            ShortcutCustomization.EffectiveKeysForRow(store, "Str_Ctx_Redo", "%ctrl%+Y"));
        // ...while the Ctrl+Shift+Z alias row keeps showing.
        Assert.Equal("%ctrl%+%shift%+Z",
            ShortcutCustomization.EffectiveKeysForRow(store, "Str_Ctx_Redo", "%ctrl%+%shift%+Z"));
        // Untouched actions pass through.
        Assert.Equal("%ctrl%+P",
            ShortcutCustomization.EffectiveKeysForRow(store, "Str_KS_Print", "%ctrl%+P"));
    }

    [Fact]
    public void ResetAll_DropsEveryOverride()
    {
        var (store, _) = NewStore();
        Assert.True(ShortcutCustomization.TrySet(store, "Print", Key.R,
            ModifierKeys.Control | ModifierKeys.Shift, out _));
        ShortcutCustomization.ResetAll(store);
        Assert.False(ShortcutCustomization.IsRemapped(store, "Print"));
        Assert.False(ShortcutCustomization.TryResolve(store, "Ctrl+Shift+R", out _));
    }
}
