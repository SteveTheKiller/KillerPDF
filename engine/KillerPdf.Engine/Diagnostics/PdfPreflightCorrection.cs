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
    ClearProductionPageBox,
    /// <summary>Set one missing descriptive metadata field.</summary>
    SetDocumentMetadata
}

/// <summary>A descriptive metadata field eligible for an unambiguous correction.</summary>
public enum PdfPreflightMetadataField
{
    /// <summary>The document title.</summary>
    Title,
    /// <summary>The document author.</summary>
    Author,
    /// <summary>The document subject.</summary>
    Subject,
    /// <summary>The document search keywords.</summary>
    Keywords
}

/// <summary>A previewed correction for one unambiguous preflight change.</summary>
public sealed class PdfPreflightCorrectionPlan
{
    private readonly PdfDocument _document;

    private PdfPreflightCorrectionPlan(
        PdfDocument document, PdfPreflightCorrectionKind kind,
        bool changesDocument, string? language = null,
        int? pageIndex = null, PdfPageBox? pageBox = null,
        PdfPreflightMetadataField? metadataField = null,
        string? metadataValue = null)
    {
        _document = document;
        Kind = kind;
        Language = language;
        PageIndex = pageIndex;
        PageBox = pageBox;
        MetadataField = metadataField;
        MetadataValue = metadataValue;
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
    /// <summary>Gets the descriptive metadata field for a metadata correction.</summary>
    public PdfPreflightMetadataField? MetadataField { get; }
    /// <summary>Gets the caller-confirmed descriptive metadata value.</summary>
    public string? MetadataValue { get; }
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

    /// <summary>Previews setting one caller-confirmed missing metadata field.</summary>
    public static PdfPreflightCorrectionPlan SetDocumentMetadata(
        PdfDocument document, PdfPreflightMetadataField field, string value)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(field)) throw new ArgumentOutOfRangeException(nameof(field));
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        PdfDocumentInformation information = PdfDocumentInformation.Read(document);
        string? current = ReadMetadataValue(information, field);
        if (current is not null && !string.Equals(current, value, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "A preflight metadata correction cannot overwrite an existing value.");
        return new PdfPreflightCorrectionPlan(document,
            PdfPreflightCorrectionKind.SetDocumentMetadata,
            current is null, metadataField: field, metadataValue: value);
    }

    /// <summary>Applies the previewed correction and verifies the saved result.</summary>
    public byte[] Apply()
    {
        if (!ChangesDocument) return _document.Source.ToArray();
        return Kind switch
        {
            PdfPreflightCorrectionKind.SetDocumentLanguage => ApplyLanguage(),
            PdfPreflightCorrectionKind.ClearProductionPageBox => ApplyPageBox(),
            PdfPreflightCorrectionKind.SetDocumentMetadata => ApplyMetadata(),
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

    private byte[] ApplyMetadata()
    {
        PdfDocumentInformation before = PdfDocumentInformation.Read(_document);
        PdfDocumentMetadata metadata = MetadataField switch
        {
            PdfPreflightMetadataField.Title => Metadata(before) with { Title = MetadataValue },
            PdfPreflightMetadataField.Author => Metadata(before) with { Author = MetadataValue },
            PdfPreflightMetadataField.Subject => Metadata(before) with { Subject = MetadataValue },
            PdfPreflightMetadataField.Keywords => Metadata(before) with { Keywords = MetadataValue },
            _ => throw new InvalidOperationException("The metadata correction field is missing.")
        };
        byte[] output = new PdfIncrementalPageEditor(_document).SetMetadata(metadata).Build();
        PdfDocumentInformation saved = PdfDocumentInformation.Read(PdfDocument.Open(output));
        if (!string.Equals(ReadMetadataValue(saved, MetadataField.Value),
                MetadataValue, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The preflight correction did not preserve the requested metadata value.");
        return output;
    }

    private static PdfDocumentMetadata Metadata(PdfDocumentInformation information) => new()
    {
        Title = information.Title,
        Author = information.Author,
        Subject = information.Subject,
        Keywords = information.Keywords,
        Creator = information.Creator,
        Producer = information.Producer,
        Language = information.Language,
        CreationDate = information.CreationDate,
        ModificationDate = information.ModificationDate,
        Trapped = information.Trapped
    };

    private static string? ReadMetadataValue(
        PdfDocumentInformation information, PdfPreflightMetadataField field) => field switch
    {
        PdfPreflightMetadataField.Title => information.Title,
        PdfPreflightMetadataField.Author => information.Author,
        PdfPreflightMetadataField.Subject => information.Subject,
        PdfPreflightMetadataField.Keywords => information.Keywords,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static PdfName ProductionBoxName(PdfPageBox pageBox) =>
        new(System.Text.Encoding.ASCII.GetBytes(pageBox switch
        {
            PdfPageBox.Bleed => "BleedBox",
            PdfPageBox.Trim => "TrimBox",
            PdfPageBox.Art => "ArtBox",
            _ => throw new ArgumentOutOfRangeException(nameof(pageBox))
        }));
}
