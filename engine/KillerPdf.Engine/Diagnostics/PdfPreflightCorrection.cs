using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>A safe correction that can be previewed before changing a document.</summary>
public enum PdfPreflightCorrectionKind
{
    /// <summary>Set the catalog's primary document language.</summary>
    SetDocumentLanguage,
    /// <summary>Remove one explicit invalid print-production page box.</summary>
    ClearProductionPageBox
}

/// <summary>A previewed correction for one unambiguous preflight change.</summary>
public sealed class PdfPreflightCorrectionPlan
{
    private readonly PdfDocument _document;

    private PdfPreflightCorrectionPlan(
        PdfDocument document, PdfPreflightCorrectionKind kind,
        bool changesDocument, string? language = null,
        int? pageIndex = null, PdfPageBox? pageBox = null)
    {
        _document = document;
        Kind = kind;
        Language = language;
        PageIndex = pageIndex;
        PageBox = pageBox;
        ChangesDocument = changesDocument;
    }

    /// <summary>Gets the correction represented by this plan.</summary>
    public PdfPreflightCorrectionKind Kind { get; }
    /// <summary>Gets the validated BCP 47 language for a language correction.</summary>
    public string? Language { get; }
    /// <summary>Gets the zero-based page index for a page-box correction.</summary>
    public int? PageIndex { get; }
    /// <summary>Gets the print-production boundary for a page-box correction.</summary>
    public PdfPageBox? PageBox { get; }
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
        return new PdfPreflightCorrectionPlan(document,
            PdfPreflightCorrectionKind.SetDocumentLanguage,
            changes, language: language);
    }

    /// <summary>Previews removing one caller-confirmed explicit print-production boundary.</summary>
    public static PdfPreflightCorrectionPlan ClearProductionPageBox(
        PdfDocument document, int pageIndex, PdfPageBox pageBox)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageBox is not (PdfPageBox.Bleed or PdfPageBox.Trim or PdfPageBox.Art))
            throw new ArgumentOutOfRangeException(nameof(pageBox),
                "Only bleed, trim, and art boxes are print-production boundaries.");
        PdfPageTree tree = PdfPageTree.Read(document);
        if ((uint)pageIndex >= (uint)tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfName name = ProductionBoxName(pageBox);
        bool changes = tree.Pages[pageIndex].Dictionary.ContainsKey(name);
        return new PdfPreflightCorrectionPlan(document,
            PdfPreflightCorrectionKind.ClearProductionPageBox,
            changes, pageIndex: pageIndex, pageBox: pageBox);
    }

    /// <summary>Applies the previewed correction and verifies the saved result.</summary>
    public byte[] Apply()
    {
        if (!ChangesDocument) return _document.Source.ToArray();
        return Kind switch
        {
            PdfPreflightCorrectionKind.SetDocumentLanguage => ApplyLanguage(),
            PdfPreflightCorrectionKind.ClearProductionPageBox => ApplyPageBox(),
            _ => throw new InvalidOperationException("The preflight correction kind is not supported.")
        };
    }

    private byte[] ApplyLanguage()
    {
        byte[] output = new PdfIncrementalPageEditor(_document)
            .SetDocumentLanguage(Language!).Build();
        string? saved = PdfDocumentInformation.Read(PdfDocument.Open(output)).Language;
        if (!string.Equals(saved, Language, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The preflight correction did not preserve the requested document language.");
        return output;
    }

    private byte[] ApplyPageBox()
    {
        byte[] output = new PdfIncrementalPageEditor(_document)
            .ClearPageBox(PageIndex!.Value, PageBox!.Value).Build();
        PdfPageTree tree = PdfPageTree.Read(PdfDocument.Open(output));
        if (tree.Pages[PageIndex.Value].Dictionary.ContainsKey(
                ProductionBoxName(PageBox.Value)))
            throw new InvalidOperationException(
                "The preflight correction did not remove the requested page boundary.");
        return output;
    }

    private static PdfName ProductionBoxName(PdfPageBox pageBox) =>
        new(System.Text.Encoding.ASCII.GetBytes(pageBox switch
        {
            PdfPageBox.Bleed => "BleedBox",
            PdfPageBox.Trim => "TrimBox",
            PdfPageBox.Art => "ArtBox",
            _ => throw new ArgumentOutOfRangeException(nameof(pageBox))
        }));
}
