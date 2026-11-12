using System.Windows;

namespace KillerPDF.Services;

internal static partial class ShellPrintFailure
{
    /// <summary>Shows why a print started from Windows failed. It runs before the main window, like setup, so the
    /// sentence falls back to English when the language dictionary has not loaded.</summary>
    internal static void Show(string? path, string reason)
    {
        try
        {
            string? format = Application.Current?.TryFindResource("Str_Print_ShellFailed") as string;
            KillerDialog.Show(null, Message(path, reason, format), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* a print that already failed must not also crash on its message */ }
    }
}
