using KillerPDF.Services;

namespace KillerPDF.Features;

/// <summary>The OCR commands that work on the open document.</summary>
internal enum OcrCommand { PageToClipboard, RegionToClipboard, SearchablePdf, ScanToText, ExtractAllText }

/// <summary>What each OCR command writes out of a protected document, and whether it may start.</summary>
internal static class OcrCommandGate
{
    /// <summary>The output a command writes, or null for one whose text goes only to the clipboard. A
    /// searchable PDF is a new PDF. Text saved to a file, as a PDF or as .txt and .md, is a text export.</summary>
    internal static PdfOutput? OutputOf(OcrCommand command) => command switch
    {
        OcrCommand.SearchablePdf => PdfOutput.Printable,
        OcrCommand.ScanToText or OcrCommand.ExtractAllText => PdfOutput.Data,
        _ => null
    };

    /// <summary>Whether a command may start: a clipboard command always does, and a command that
    /// writes a file asks the gate for its output first.</summary>
    internal static bool MayStart(OcrCommand command, Func<PdfOutput, bool> ensureMayWrite)
    {
        ArgumentNullException.ThrowIfNull(ensureMayWrite);
        return OutputOf(command) is not PdfOutput output || ensureMayWrite(output);
    }
}
