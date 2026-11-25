using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Writing;

/// <summary>
/// Folds an edited document's revisions into one, so content a destructive edit removed (a deleted
/// page, a removed link, text under a cover) is not left behind in an earlier revision of the file.
/// </summary>
/// <remarks>
/// An incremental update keeps every earlier revision, and anyone can recover one by cutting the file at
/// an earlier %%EOF. A full rewrite drops objects nothing references any more. A signed document, or one
/// with Reader usage rights, keeps its incremental form, because the rewrite would invalidate them.
/// </remarks>
public static class PdfRevisionConsolidation
{
    /// <summary>
    /// Returns a single-revision rewrite of <paramref name="data"/> without unreachable objects, or
    /// <paramref name="data"/> itself when the document is signed or cannot be rewritten.
    /// </summary>
    public static byte[] Consolidate(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return PdfDocumentWriter.Write(PdfDocument.Open(data),
                new PdfDocumentWriteOptions { PruneUnreachableObjects = true });
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException
            or NotSupportedException)
        {
            // Signed files and graphs the writer cannot rewrite (some damaged or encrypted files) keep the
            // incremental save, which still makes the edit itself.
            return data;
        }
    }
}
