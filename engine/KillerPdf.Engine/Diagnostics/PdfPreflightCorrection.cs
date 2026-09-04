using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>A safe correction that can be previewed before changing a document.</summary>
public enum PdfPreflightCorrectionKind
{
    /// <summary>Set the catalog's primary document language.</summary>
    SetDocumentLanguage
}

/// <summary>A previewed correction for one unambiguous document-language change.</summary>
public sealed class PdfPreflightCorrectionPlan
{
    private readonly PdfDocument _document;

    private PdfPreflightCorrectionPlan(PdfDocument document, string language, bool changesDocument)
    {
        _document = document;
        Language = language;
        ChangesDocument = changesDocument;
    }

    /// <summary>Gets the correction represented by this plan.</summary>
    public PdfPreflightCorrectionKind Kind => PdfPreflightCorrectionKind.SetDocumentLanguage;
    /// <summary>Gets the validated BCP 47 language selected by the caller.</summary>
    public string Language { get; }
    /// <summary>Gets whether applying the plan will change the document.</summary>
    public bool ChangesDocument { get; }

    /// <summary>Previews setting the caller-confirmed primary document language.</summary>
    public static PdfPreflightCorrectionPlan SetDocumentLanguage(
        PdfDocument document, string language)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        _ = new PdfIncrementalPageEditor(document).SetDocumentLanguage(language);
        bool changes = !string.Equals(
            PdfDocumentInformation.Read(document).Language, language,
            StringComparison.OrdinalIgnoreCase);
        return new PdfPreflightCorrectionPlan(document, language, changes);
    }

    /// <summary>Applies the previewed correction and verifies the saved language.</summary>
    public byte[] Apply()
    {
        if (!ChangesDocument) return _document.Source.ToArray();
        byte[] output = new PdfIncrementalPageEditor(_document)
            .SetDocumentLanguage(Language).Build();
        string? saved = PdfDocumentInformation.Read(PdfDocument.Open(output)).Language;
        if (!string.Equals(saved, Language, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The preflight correction did not preserve the requested document language.");
        return output;
    }
}
