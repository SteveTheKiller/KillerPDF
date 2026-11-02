namespace KillerPDF.Services;

/// <summary>The copy a print job is made from. It is written to the app's temp folder, handed to the
/// print preview, and deleted when the preview closes, so it never stands in for the document.</summary>
internal static class PrintCopy
{
    /// <summary>Writes a tab's document to the copy, always plain. A tab that reads its encrypted
    /// original is decrypted under the key it opened with, so the preparation and the preview read the
    /// copy with no protection. The print permission was settled before this is called.</summary>
    internal static void Write(PdfWorkingDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.SavePlainThrowawayCopy(path);
    }

    /// <summary>Readies the copy for the printer off the UI thread. A damaged signed or usage-rights
    /// file is readied instead of refused, its signatures are cleared, and the overlays drawn by
    /// <paramref name="burn"/> go in between the two link strips, ahead of the form values and the
    /// page rotations.</summary>
    internal static void Ready(string path, Action<string>? burn,
        PdfEngineIntegration.FormEdits formValues, IReadOnlyDictionary<int, int> rotations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfEngineIntegration.PrepareThrowawayCopy(path);
        PdfEngineIntegration.ClearInvalidatedSignatures(path);
        PdfEngineIntegration.StripLinkAppearances(path);
        burn?.Invoke(path);
        PdfEngineIntegration.StripLinkAppearances(path);
        PdfEngineIntegration.ApplyFormValues(path, formValues);
        PdfEngineIntegration.ApplyPageRotations(path, rotations);
    }
}
