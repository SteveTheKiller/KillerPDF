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
            PdfEngineIntegration.ApplyFormValues(tempPath, formValues);
            return File.ReadAllBytes(tempPath);
        }
        finally { File.Delete(tempPath); }
    }

    /// <summary>Opens a snapshot for the review.</summary>
    internal static PdfDocument Open(byte[] source, OpenedProtection? protection) => PdfDocument.Open(source);

    /// <summary>Inspects a snapshot for the review.</summary>
    internal static PdfAccessibilityReport Inspect(byte[] source, OpenedProtection? protection) =>
        PdfAccessibilityInspector.Inspect(Open(source, protection));
}
