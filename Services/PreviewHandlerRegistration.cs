using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace KillerPDF.Services;

/// <summary>
/// PDF previews for File Explorer and Outlook (#410). The installed edition registers the
/// KillerPDF.PreviewHandler COM server when the user chooses it in setup. It runs in its own
/// prevhost.exe surrogate so no other preview handler shares its .NET runtime. The .pdf preview
/// it replaces is remembered and put back when it is removed. The portable edition never calls this.
/// </summary>
internal static class PreviewHandlerRegistration
{
    /// <summary>Matches PdfPreviewHandler.ClassId in the PreviewHandler project.</summary>
    internal const string ClassId = "{0EB8F20D-6E18-4B80-9CDF-FF663C52B5BD}";
    internal const string AppId = "{CC8F3FF2-266A-4827-9CFA-964310EB6676}";
    internal const string HostFileName = "KillerPDF.PreviewHandler.comhost.dll";
    private const string PreviewExtension = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const string DisplayName = "KillerPDF Preview Handler";
    private const string StateKey = @"Software\KillerPDF\PreviewHandler";
    private const string PreviousValue = "PreviousPdfPreview";

    private static string ClassKey => @"Software\Classes\CLSID\" + ClassId;
    private static string ExtensionKey => @"Software\Classes\.pdf\shellex\" + PreviewExtension;
    private static string ProgIdKey => @"Software\Classes\KillerPDF.pdf\shellex\" + PreviewExtension;

    internal static bool IsRegistered(RegistryKey root)
    {
        try
        {
            using RegistryKey? server = root.OpenSubKey(ClassKey + @"\InprocServer32");
            return server?.GetValue("") is string path && path.Length > 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private const string HandlerListKey = @"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers";

    /// <summary>
    /// The 32-bit view of HKLM for an all-users install, or null for a per-user one. Classic Outlook lists only the
    /// handlers in HKLM, and 32-bit Outlook reads its own view of it (#410). A per-user install writes nothing
    /// machine-wide.
    /// </summary>
    internal static RegistryKey? ThirtyTwoBitMachineRoot(RegistryKey root) =>
        root.Name == Registry.LocalMachine.Name
            ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            : null;

    internal static void Register(RegistryKey root, string installDirectory)
    {
        using RegistryKey? thirtyTwoBit = ThirtyTwoBitMachineRoot(root);
        Register(root, installDirectory, thirtyTwoBit);
    }

    internal static void Unregister(RegistryKey root)
    {
        using RegistryKey? thirtyTwoBit = ThirtyTwoBitMachineRoot(root);
        Unregister(root, thirtyTwoBit);
    }

    internal static void Register(RegistryKey root, string installDirectory, RegistryKey? thirtyTwoBitRoot)
    {
        string host = Path.Combine(installDirectory, HostFileName);
        if (!File.Exists(host))
            throw new FileNotFoundException("The preview handler is missing from the installation.", host);

        using (RegistryKey key = root.CreateSubKey(ClassKey))
        {
            key.SetValue("", DisplayName);
            key.SetValue("DisplayName", DisplayName);
            key.SetValue("AppID", AppId);
            // The pane keeps Windows' low-integrity preview sandbox. It only reads the font index
            // and settings, which low integrity allows, and an older install's opt-out is removed.
            key.DeleteValue("DisableLowILProcessIsolation", throwOnMissingValue: false);
        }
        using (RegistryKey key = root.CreateSubKey(ClassKey + @"\InprocServer32"))
        {
            key.SetValue("", host);
            key.SetValue("ThreadingModel", "Apartment");
        }
        using (RegistryKey key = root.CreateSubKey(@"Software\Classes\AppID\" + AppId))
        {
            key.SetValue("", DisplayName);
            key.SetValue("DllSurrogate", @"%SystemRoot%\system32\prevhost.exe", RegistryValueKind.ExpandString);
        }
        using (RegistryKey key = root.CreateSubKey(ExtensionKey))
        {
            if (key.GetValue("") is string previous && previous.Length > 0
                && !previous.Equals(ClassId, StringComparison.OrdinalIgnoreCase))
            {
                using RegistryKey state = root.CreateSubKey(StateKey);
                state.SetValue(PreviousValue, previous);
            }
            key.SetValue("", ClassId);
        }
        using (RegistryKey key = root.CreateSubKey(ProgIdKey))
            key.SetValue("", ClassId);
        using (RegistryKey key = root.CreateSubKey(HandlerListKey))
            key.SetValue(ClassId, DisplayName);
        if (thirtyTwoBitRoot is not null)
        {
            using RegistryKey key = thirtyTwoBitRoot.CreateSubKey(HandlerListKey);
            key.SetValue(ClassId, DisplayName);
        }
    }

    internal static void Unregister(RegistryKey root, RegistryKey? thirtyTwoBitRoot)
    {
        Try(() =>
        {
            string? previous;
            using (RegistryKey? state = root.OpenSubKey(StateKey))
                previous = state?.GetValue(PreviousValue) as string;
            bool restore;
            using (RegistryKey? key = root.OpenSubKey(ExtensionKey, writable: true))
            {
                if (key?.GetValue("") is not string current || !current.Equals(ClassId, StringComparison.OrdinalIgnoreCase)) return;
                restore = !string.IsNullOrEmpty(previous);
                if (restore) key.SetValue("", previous!);
            }
            if (!restore) root.DeleteSubKeyTree(ExtensionKey, throwOnMissingSubKey: false);
        });
        Try(() => root.DeleteSubKeyTree(ProgIdKey, throwOnMissingSubKey: false));
        Try(() => root.DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false));
        Try(() => root.DeleteSubKeyTree(@"Software\Classes\AppID\" + AppId, throwOnMissingSubKey: false));
        foreach (RegistryKey? hive in new[] { root, thirtyTwoBitRoot })
            Try(() =>
            {
                using RegistryKey? key = hive?.OpenSubKey(HandlerListKey, writable: true);
                key?.DeleteValue(ClassId, throwOnMissingValue: false);
            });
        Try(() => root.DeleteSubKeyTree(StateKey, throwOnMissingSubKey: false));
    }

    /// <summary>
    /// Ends the preview surrogates that loaded the handler from an installation folder, so the
    /// folder can be replaced or removed. The shell starts a new one for the next preview.
    /// </summary>
    internal static void StopHosts(string installDirectory)
    {
        string root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (Process process in Process.GetProcessesByName("prevhost"))
        {
            using (process)
            {
                try
                {
                    bool ours = process.Modules.Cast<ProcessModule>().Any(module =>
                        module.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                    if (!ours) continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                    or NotSupportedException)
                {
                    // Another account's surrogate, or one that already exited.
                }
            }
        }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException
            or ArgumentException) { }
    }
}
