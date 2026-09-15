using System.Windows.Input;

namespace KillerPDF.Services;

internal static class ShortcutTogglePolicy
{
    internal const string Setting = "KeyboardShortcutsEnabled";

    internal static bool IsEnabled(string? value) => value != "0";

    internal static bool IsToggle(Key key, ModifierKeys modifiers, Key systemKey = Key.None)
        => (key == Key.System ? systemKey : key) == Key.K
            && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

    internal static bool SuppressWhenDisabled(Key key, bool textInput, Key systemKey = Key.None)
    {
        if (textInput) return false;
        key = key == Key.System ? systemKey : key;
        return key is not (Key.Tab or Key.Space or Key.Return
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin);
    }
}
