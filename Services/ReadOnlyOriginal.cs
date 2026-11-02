namespace KillerPDF.Services;

/// <summary>A protected file that cannot be rewritten opens read-only where it lies. The tab holds the
/// protection it opened with, and every reopen of that file goes through here.</summary>
internal static class ReadOnlyOriginal
{
    /// <summary>Opens the file read-only under the protection the tab already holds for it. The
    /// protection is required and is what the tab holds afterwards; it is never read again from the
    /// file, so a file put in the original's place cannot change what the tab allows.</summary>
    internal static (PdfWorkingDocument Document, OpenedProtection Protection) Open(string path, OpenedProtection known) =>
        (PdfWorkingDocument.Open(path, known, isReadOnly: true), known);

    /// <summary>Puts the read-only original back after a restricted fill was saved into it or an
    /// unlock failed. It reopens under the tab's own protection, the key a signed file is read with
    /// included, and that protection is what the tab holds afterwards.</summary>
    internal static (PdfWorkingDocument Document, OpenedProtection Protection) Restore(string path, OpenedProtection tabProtection) =>
        Open(path, tabProtection);
}
