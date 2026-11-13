using System.Windows.Input;

namespace KillerPDF.Services;

internal static class ShortcutTogglePolicy
{
    internal const string Setting = "KeyboardShortcutsEnabled";

    internal static bool IsEnabled(string? value) => value != "0";

    internal static bool IsToggle(Key key, ModifierKeys modifiers, Key systemKey = Key.None)
        => (key == Key.System ? systemKey : key) == Key.K
            && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

    /// <summary>Keys that still act while shortcuts are off: bare F1 and F12, which open the
    /// shortcuts list and About, and Esc while one of those is showing so it can be closed.</summary>
    internal static bool StaysOn(Key key, ModifierKeys modifiers, Key systemKey, bool overlayOpen)
    {
        if (modifiers != ModifierKeys.None) return false;
        key = key == Key.System ? systemKey : key;
        return key is Key.F1 or Key.F12 || key == Key.Escape && overlayOpen;
    }

    internal static bool SuppressWhenDisabled(Key key, bool textInput, Key systemKey = Key.None)
    {
        if (textInput) return false;
        key = key == Key.System ? systemKey : key;
        return key is not (Key.Tab or Key.Space or Key.Return
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin);
    }
}
