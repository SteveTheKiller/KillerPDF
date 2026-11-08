using Microsoft.Win32;

namespace KillerPDF.Services;

/// <summary>
/// Writes and removes the per-user or machine registrations that put KillerPDF in Open with and
/// Default apps. Registration only advertises the handler; it never changes a user's chosen default.
/// </summary>
internal static class FileAssociationRegistry
{
    /// <summary>A file type KillerPDF advertises, with the ProgID that opens it.</summary>
    internal sealed record FileType(string Extension, string ProgId, string Description);

    /// <summary>The file types KillerPDF opens.</summary>
    internal static readonly IReadOnlyList<FileType> Types =
    [
        new(".pdf", "KillerPDF.pdf", "PDF Document"),
        new(".epub", "KillerPDF.epub", "EPUB Book"),
        new(".djvu", "KillerPDF.djvu", "DjVu Document"),
        new(".djv", "KillerPDF.djvu", "DjVu Document")
    ];

    /// <summary>Registers every type under <paramref name="root"/> (HKCU or HKLM, or a test key).</summary>
    /// <param name="pdfIconReference">The icon for PDF documents; other types use the app icon.</param>
    internal static void Register(RegistryKey root, string exePath, string pdfIconReference,
        string appName, string urlScheme)
    {
        string command = $"\"{exePath}\" \"%1\"";
        foreach (FileType type in Types)
        {
            using (var k = root.CreateSubKey(@"Software\Classes\" + type.ProgId))
                k.SetValue("", type.Description);
            using (var k = root.CreateSubKey(@"Software\Classes\" + type.ProgId + @"\DefaultIcon"))
                k.SetValue("", type.Extension == ".pdf" ? pdfIconReference : $"{exePath},0");
            using (var k = root.CreateSubKey(@"Software\Classes\" + type.ProgId + @"\shell\open\command"))
                k.SetValue("", command);
            // Adds KillerPDF to the extension's Open with list.
            using (var k = root.CreateSubKey(@"Software\Classes\" + type.Extension + @"\OpenWithProgids"))
                k.SetValue(type.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Existing Open with choices can retain the launcher name after an upgrade.
        // Both application identities must launch the installed payload, not setup.
        foreach (string application in new[] { "KillerPDF.App.exe", "KillerPDF.exe" })
        {
            string applicationKey = @"Software\Classes\Applications\" + application;
            using (var k = root.CreateSubKey(applicationKey))
                k.SetValue("FriendlyAppName", appName);
            using (var k = root.CreateSubKey(applicationKey + @"\shell\open\command"))
                k.SetValue("", command);
            using (var k = root.CreateSubKey(applicationKey + @"\SupportedTypes"))
                foreach (FileType type in Types) k.SetValue(type.Extension, "");
        }

        // RegisteredApplications capability (used by Default Programs UI)
        using (var k = root.CreateSubKey(@"Software\KillerPDF\Capabilities"))
        {
            k.SetValue("ApplicationName", appName);
            k.SetValue("ApplicationDescription", "Lightweight PDF viewer and editor");
        }
        using (var k = root.CreateSubKey(@"Software\KillerPDF\Capabilities\FileAssociations"))
            foreach (FileType type in Types) k.SetValue(type.Extension, type.ProgId);

        // #183: without this the killerpdf:// handler never appears in Windows Settings >
        // Default apps > Choose defaults by link type. Maps the scheme to its ProgID.
        using (var k = root.CreateSubKey(@"Software\KillerPDF\Capabilities\UrlAssociations"))
            k.SetValue(urlScheme, urlScheme);

        using (var k = root.CreateSubKey(@"Software\RegisteredApplications"))
            k.SetValue(appName, @"Software\KillerPDF\Capabilities");
    }

    /// <summary>Removes only KillerPDF's own registrations, leaving other applications' entries intact.</summary>
    internal static void Unregister(RegistryKey root, string appName)
    {
        foreach (FileType type in Types)
        {
            try { root.DeleteSubKeyTree(@"Software\Classes\" + type.ProgId, false); } catch { }
            try
            {
                using var k = root.OpenSubKey(@"Software\Classes\" + type.Extension + @"\OpenWithProgids", writable: true);
                k?.DeleteValue(type.ProgId, throwOnMissingValue: false);
            }
            catch { }
        }
        try { root.DeleteSubKeyTree(@"Software\Classes\Applications\KillerPDF.App.exe", false); } catch { }
        try { root.DeleteSubKeyTree(@"Software\Classes\Applications\KillerPDF.exe", false); } catch { }
        try { root.DeleteSubKeyTree(@"Software\KillerPDF\Capabilities", false); } catch { }
        try
        {
            using var k = root.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            k?.DeleteValue(appName, throwOnMissingValue: false);
        }
        catch { }
    }
}
