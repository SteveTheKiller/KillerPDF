using System.Windows;
using KillerPDF.Services;

namespace KillerPDF;

public partial class MainWindow
{
    private static bool KeyboardShortcutsEnabled
        => ShortcutTogglePolicy.IsEnabled(App.GetSetting(ShortcutTogglePolicy.Setting));

    private void SetKeyboardShortcutsEnabled(bool enabled)
    {
        App.SetSetting(ShortcutTogglePolicy.Setting, enabled ? "1" : "0");
        KeyboardShortcutsCheck.IsChecked = enabled;
        SetStatus(Loc(enabled ? "Str_KS_EnabledStatus" : "Str_KS_DisabledStatus"));
    }

    private void KeyboardShortcutsCheck_Click(object sender, RoutedEventArgs e)
        => SetKeyboardShortcutsEnabled(KeyboardShortcutsCheck.IsChecked == true);
}
