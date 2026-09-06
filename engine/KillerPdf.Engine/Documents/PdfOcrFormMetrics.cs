namespace KillerPdf.Engine.Documents;

/// <summary>Measures OCR text accuracy inside populated form fields.</summary>
public sealed record PdfOcrFormMetrics(
    int ExpectedFieldCount,
    int RecognizedFieldCount,
    PdfOcrTextMetrics Text)
{
    /// <summary>Compares recognized form-region words with populated widget values.</summary>
    public static PdfOcrFormMetrics Compare(IReadOnlyList<PdfFormWidgetInfo> widgets,
        PdfOcrResult recognized, int width, int height, int additionalRotation = 0)
    {
        ArgumentNullException.ThrowIfNull(widgets);
        ArgumentNullException.ThrowIfNull(recognized);
        IReadOnlyList<PdfOcrFormRegion> regions = PdfOcrFormLayout.MapRegions(
            widgets, width, height, additionalRotation);
        int expectedFields = 0, recognizedFields = 0;
        int expectedCharacters = 0, recognizedCharacters = 0, characterEdits = 0;
        int expectedWords = 0, recognizedWords = 0, wordEdits = 0;
        for (int index = 0; index < widgets.Count; index++)
        {
            PdfFormWidgetInfo widget = widgets[index];
            if (widget.FieldKind is not (PdfFormFieldKind.Text or PdfFormFieldKind.Choice)
                || string.IsNullOrWhiteSpace(widget.Value)) continue;
            expectedFields++;
            PdfOcrFormRegion region = regions[index];
            string actual = recognized.Words.FirstOrDefault(word =>
                word.Left == region.Left && word.Top == region.Top
                && word.Right == region.Right && word.Bottom == region.Bottom)?.Text ?? "";
            if (actual.Length > 0) recognizedFields++;
            string expected = widget.FieldKind == PdfFormFieldKind.Choice
                ? widget.Options.FirstOrDefault(option => string.Equals(
                    option.ExportValue, widget.Value, StringComparison.Ordinal))?.DisplayValue
                    ?? widget.Value
                : widget.Value;
            PdfOcrTextMetrics metrics = PdfOcrTextMetrics.Compare(expected, actual);
            expectedCharacters = checked(expectedCharacters + metrics.ExpectedCharacterCount);
            recognizedCharacters = checked(recognizedCharacters
                + metrics.RecognizedCharacterCount);
            characterEdits = checked(characterEdits + metrics.CharacterEditCount);
            expectedWords = checked(expectedWords + metrics.ExpectedWordCount);
            recognizedWords = checked(recognizedWords + metrics.RecognizedWordCount);
            wordEdits = checked(wordEdits + metrics.WordEditCount);
        }
        return new PdfOcrFormMetrics(expectedFields, recognizedFields,
            new PdfOcrTextMetrics(expectedCharacters, recognizedCharacters,
                characterEdits, expectedWords, recognizedWords, wordEdits));
    }
}
