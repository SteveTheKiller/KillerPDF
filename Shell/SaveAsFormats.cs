namespace KillerPDF.Shell;

/// <summary>
/// The formats Save as offers. PDF saves the tab; EPUB writes the same reflowable book File > Export does and
/// leaves the tab on its PDF. The dialog requires the chosen filter's extension, so the file name decides.
/// </summary>
internal static class SaveAsFormats
{
    internal static string Filter(string pdfLabel, string epubLabel, bool offerEpub) =>
        offerEpub ? $"{pdfLabel}|*.pdf|{epubLabel}|*.epub" : $"{pdfLabel}|*.pdf";

    internal static bool IsEpub(string fileName) =>
        string.Equals(System.IO.Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase);
}
