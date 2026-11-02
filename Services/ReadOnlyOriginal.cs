namespace KillerPDF.Services;

/// <summary>A protected file that cannot be rewritten opens read-only where it lies. The tab holds the
/// protection it opened with, and every reopen of that file goes through here.</summary>
internal static class ReadOnlyOriginal
{
    /// <summary>Opens the file read-only. A protection already read for this file is kept, and one not
    /// read yet is read from the file.</summary>
    internal static (PdfWorkingDocument Document, OpenedProtection Protection) Open(string path, OpenedProtection? known) =>
        (PdfWorkingDocument.Open(path, isReadOnly: true), known ?? OpenedProtection.Capture(path, null));

    /// <summary>Puts the read-only original back after a restricted fill was saved into it or an
    /// unlock failed.</summary>
    internal static (PdfWorkingDocument Document, OpenedProtection Protection) Restore(string path, OpenedProtection tabProtection)
    {
        _ = tabProtection;
        return Open(path, null);
    }
}
