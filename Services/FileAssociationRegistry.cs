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

    /// <summary>The ProgID that lists KillerPDF under Open with for images (#480). It opens them as a new PDF.</summary>
    internal const string ImageProgId = "KillerPDF.image";

    /// <summary>The Explorer verb that saves images as a PDF beside them without opening a window (#479).</summary>
    internal const string ImageVerb = "KillerPDF.ImagesToPdf";

    /// <summary>The English label of <see cref="ImageVerb"/>; setup passes the translated one.</summary>
    internal const string ImageVerbEnglishLabel = "Save as KillerPDF";

    /// <summary>Registers every type under <paramref name="root"/> (HKCU or HKLM, or a test key).</summary>
    /// <param name="pdfIconReference">The icon for PDF documents; other types use the app icon.</param>
    /// <param name="imageVerbLabel">The menu text of the Save as KillerPDF verb on images.</param>
    internal static void Register(RegistryKey root, string exePath, string pdfIconReference,
        string appName, string urlScheme, string imageVerbLabel = ImageVerbEnglishLabel)
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
            if (type.Extension == ".pdf") RegisterPrintVerbs(root, @"Software\Classes\" + type.ProgId, exePath, pdfOnly: false);
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
            RegisterPrintVerbs(root, applicationKey, exePath, pdfOnly: true);
            using (var k = root.CreateSubKey(applicationKey + @"\SupportedTypes"))
            {
                foreach (FileType type in Types) k.SetValue(type.Extension, "");
                foreach (string extension in ImagesToPdfPlan.Extensions) k.SetValue(extension, "");
            }
        }

        RegisterImages(root, exePath, command, imageVerbLabel);

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

    // Images only gain choices: KillerPDF joins their Open with list and their right-click menu, and the
    // extension's own default (Photos or the user's viewer) is never written, nor offered in Default apps.
    // For a command-line verb Explorer still starts one process per selected file; MultiSelectModel=Player
    // lets the entry show for up to 100 selected files instead of 15, and the app gathers the launches into one PDF.
    private static void RegisterImages(RegistryKey root, string exePath, string command, string verbLabel)
    {
        using (var k = root.CreateSubKey(@"Software\Classes\" + ImageProgId))
            k.SetValue("", "Image");
        using (var k = root.CreateSubKey(@"Software\Classes\" + ImageProgId + @"\DefaultIcon"))
            k.SetValue("", $"{exePath},0");
        using (var k = root.CreateSubKey(@"Software\Classes\" + ImageProgId + @"\shell\open"))
            k.SetValue("MultiSelectModel", "Player");
        using (var k = root.CreateSubKey(@"Software\Classes\" + ImageProgId + @"\shell\open\command"))
            k.SetValue("", command);
        foreach (string extension in ImagesToPdfPlan.Extensions)
        {
            using (var k = root.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids"))
                k.SetValue(ImageProgId, Array.Empty<byte>(), RegistryValueKind.None);
            string verbKey = @"Software\Classes\SystemFileAssociations\" + extension + @"\shell\" + ImageVerb;
            using (var k = root.CreateSubKey(verbKey))
            {
                k.SetValue("MUIVerb", string.IsNullOrWhiteSpace(verbLabel) ? ImageVerbEnglishLabel : verbLabel);
                k.SetValue("Icon", $"{exePath},0");
                k.SetValue("MultiSelectModel", "Player");
            }
            using (var k = root.CreateSubKey(verbKey + @"\command"))
                k.SetValue("", $"\"{exePath}\" {ImagesToPdfPlan.VerbSwitch} \"%1\"");
        }
    }

    // Windows asks these verbs how to print a file: "print" for right-click Print, Start-Process -Verb Print and mail
    // clients printing an attachment, and "printto" with the printer as %2 when a printer is named. Both run the
    // headless print, which reads PDFs only, so on an application key that also opens books they apply to PDFs alone.
    // --shell makes a failed print say why in a message, since a shell launch has no console. Unregister removes
    // these verbs with the keys they sit under.
    private static void RegisterPrintVerbs(RegistryKey root, string classKey, string exePath, bool pdfOnly)
    {
        foreach (var (verb, arguments) in new[] { ("print", "--print \"%1\" --shell"), ("printto", "--print \"%1\" --printer \"%2\" --shell") })
        {
            using (var k = root.CreateSubKey(classKey + @"\shell\" + verb))
                if (pdfOnly) k.SetValue("AppliesTo", "System.FileExtension:=.pdf");
            using (var k = root.CreateSubKey(classKey + @"\shell\" + verb + @"\command"))
                k.SetValue("", $"\"{exePath}\" {arguments}");
        }
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
        try { root.DeleteSubKeyTree(@"Software\Classes\" + ImageProgId, false); } catch { }
        foreach (string extension in ImagesToPdfPlan.Extensions)
        {
            string type = @"Software\Classes\" + extension;
            try
            {
                using (var k = root.OpenSubKey(type + @"\OpenWithProgids", writable: true))
                    k?.DeleteValue(ImageProgId, throwOnMissingValue: false);
                DeleteEmptyKeys(root, type + @"\OpenWithProgids", type);
            }
            catch { }
            string association = @"Software\Classes\SystemFileAssociations\" + extension;
            try
            {
                root.DeleteSubKeyTree(association + @"\shell\" + ImageVerb, false);
                DeleteEmptyKeys(root, association + @"\shell", association);
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

    // Removes keys this registration created, deepest first, while nothing is left in them. A key another
    // application also uses keeps its values or subkeys, so it stays, and so does every key above it.
    private static void DeleteEmptyKeys(RegistryKey root, params string[] deepestFirst)
    {
        foreach (string path in deepestFirst)
        {
            using (var k = root.OpenSubKey(path))
            {
                if (k is null) continue;
                if (k.SubKeyCount > 0 || k.ValueCount > 0) return;
            }
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }
}
