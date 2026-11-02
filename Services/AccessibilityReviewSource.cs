using System.IO;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>The document the accessibility review reads: a snapshot of the tab with its typed form
/// values, opened under the tab's protection.</summary>
internal static class AccessibilityReviewSource
{
    /// <summary>The tab's document with its pending form values, as bytes. The copy written on the way
    /// lives at <paramref name="tempPath"/> and is deleted before this returns.</summary>
    internal static byte[] Snapshot(PdfWorkingDocument document, PdfEngineIntegration.FormEdits formValues,
        OpenedProtection? protection, string tempPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        try
        {
            document.Save(tempPath);
            PdfEngineIntegration.ApplyFormValues(tempPath, formValues, protection);
            return File.ReadAllBytes(tempPath);
        }
        finally { File.Delete(tempPath); }
    }

    /// <summary>Opens a snapshot for the review. A plain snapshot opens strictly, as it always has, and
    /// one that is still encrypted opens under the tab's protection: its empty password for an
    /// owner-only file, or the key the tab holds for a signed file that needs a password.</summary>
    internal static PdfDocument Open(byte[] source, OpenedProtection? protection)
    {
        PdfDocument document = PdfDocument.Open(source);
        return document.IsEncrypted && !document.IsDecrypted
            ? PdfEngineIntegration.OpenForReading(source, protection) : document;
    }

    /// <summary>Inspects a snapshot for the review.</summary>
    internal static PdfAccessibilityReport Inspect(byte[] source, OpenedProtection? protection) =>
        PdfAccessibilityInspector.Inspect(Open(source, protection));
}
