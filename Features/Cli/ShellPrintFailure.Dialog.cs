using System.Windows;

namespace KillerPDF.Services;

internal static partial class ShellPrintFailure
{
    /// <summary>
    /// Shows why a print started from Windows failed. It runs from the command-line check, before the app's own
    /// startup, so it prepares what that startup would, as the crash preview does: closing the message must not end
    /// the process with a success code, and the message uses the user's theme and language. With no user to close it
    /// (a service or a print server printing attachments) nothing is shown, so the print still ends with its code.
    /// </summary>
    internal static void Show(string? path, string reason)
    {
        if (!Environment.UserInteractive) return;
        try
        {
            if (Application.Current is { } app) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppTheme.Configure();
            ThemeManager.Initialize();
            LocaleManager.Initialize();
            Controls.GrainTexture.Publish();
            string? format = Application.Current?.TryFindResource("Str_Print_ShellFailed") as string;
            KillerDialog.Show(null, Message(path, reason, format), "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* a print that already failed must not also crash on its message */ }
    }
}
