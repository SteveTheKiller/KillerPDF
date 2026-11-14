namespace KillerPDF.Services;

/// <summary>
/// The Open dialog's file types. KillerPDF opens PDFs and, natively, EPUB and DjVu books, so the first choice shows all
/// of them together and each format can also be picked on its own. The extensions match the ones OpenFile sends to
/// the native reader (.epub, .djvu, .djv).
/// </summary>
internal static class OpenDialogFilter
{
    internal static string For(Func<string, string> loc) => string.Join('|',
        loc("Str_Filter_Openable"), "*.pdf;*.epub;*.djvu;*.djv",
        loc("Str_Filter_Pdf"), "*.pdf",
        loc("Str_Filter_Epub"), "*.epub",
        loc("Str_Filter_Djvu"), "*.djvu;*.djv");
}
