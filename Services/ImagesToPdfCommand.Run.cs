using System.IO;
using System.Security.Principal;
using System.Windows;

namespace KillerPDF.Services;

internal static partial class ImagesToPdfCommand
{
    // Explorer starts the per-file processes together, but each needs a moment to start (a slow or busy
    // machine can take several seconds), so the collector waits this long after the last arrival.
    // The limit bounds a very large selection.
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>Runs the verb for one launch and returns the process exit code.</summary>
    internal static int Run(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            ShowFailure(files, () => NoImageText);
            return 2;
        }
        IReadOnlyList<string>? batch;
        try
        {
            var channel = new SingleInstanceChannel("KillerPDF.ImagesToPdf", WindowsIdentity.GetCurrent().User!.Value,
                System.Diagnostics.Process.GetCurrentProcess().SessionId);
            batch = new ImageLaunchCollector(channel, Quiet, Limit).Collect(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            batch = files;   // no channel: this launch converts its own files
        }
        if (batch is null) return 0;   // another launch is making the PDF with these files
        try
        {
            Convert(batch);
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowFailure(ImagesToPdfPlan.Order(batch), () => ReasonText(ex));
            return 1;
        }
    }

    // Runs before the app's own startup, so it prepares what that startup would, as a failed shell print
    // does: closing the message must not end the process early, and it uses the user's theme and language.
    // The reason is read after LocaleManager.Initialize, so the command's own reasons are translated.
    private static void ShowFailure(IReadOnlyList<string> files, Func<string> reason)
    {
        if (!Environment.UserInteractive) return;
        try
        {
            if (Application.Current is { } app) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppTheme.Configure();
            ThemeManager.Initialize();
            LocaleManager.Initialize();
            Controls.GrainTexture.Publish();
            string? format = Application.Current?.TryFindResource("Str_ImagesToPdf_Failed") as string;
            KillerDialog.Show(null, FailureMessage(files, reason(), format), "KillerPDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* a conversion that already failed must not also crash on its message */ }
    }
}
