using System.Windows.Input;

namespace KillerPDF.Services;

/// <summary>Separates text-editing gestures from window-level application shortcuts.</summary>
internal static class EditableTextShortcutPolicy
{
    internal static bool CommitsTextBox(Key key, ModifierKeys modifiers) =>
        key == Key.Enter && (modifiers & ModifierKeys.Control) == ModifierKeys.Control;

    internal static double TextBoxTop(double pointerY, double fontSize) =>
        Math.Max(0, pointerY - Math.Max(0, fontSize) / 2.0);

    internal static bool KeepInTextBox(Key key, ModifierKeys modifiers,
        Key systemKey = Key.None)
    {
        Key effectiveKey = key == Key.System ? systemKey : key;
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return false;
        if (effectiveKey is >= Key.F1 and <= Key.F24)
            return false;
        if ((modifiers & ModifierKeys.Control) == 0)
            return true;

        return effectiveKey is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y
            or Key.Back or Key.Delete or Key.Insert
            or Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.Space;
    }
}
