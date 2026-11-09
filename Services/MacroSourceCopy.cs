using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

// A macro starts from the saved copy, where text added with the Text tool is an editable annotation. These steps
// rebuild pages from their content or read only page content, so the annotation would not reach their output.
internal static class MacroSourceCopy
{
    internal static bool NeedsAddedTextInPageContent(PdfMacro macro) => macro.Steps.Any(step => step.Operation
        is PdfMacroOperation.ImposeNUp or PdfMacroOperation.ImposeBooklet or PdfMacroOperation.ImposeStepAndRepeat
        or PdfMacroOperation.ImposeCutStack or PdfMacroOperation.ImposeManualSequence or PdfMacroOperation.ImposePoster
        or PdfMacroOperation.Export);
}
