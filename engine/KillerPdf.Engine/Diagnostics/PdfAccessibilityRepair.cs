using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>A reviewed, unambiguous document-language accessibility correction.</summary>
public sealed record PdfAccessibilityLanguageRepair(
    string Language, PdfAccessibilityReport Before, bool WillChange);

/// <summary>A reviewed alternate-description correction for one figure structure element.</summary>
public sealed record PdfAccessibilityFigureRepair(
    int ObjectNumber, string AlternateDescription, PdfAccessibilityReport Before, bool WillChange);

/// <summary>A reviewed description correction for one AcroForm field.</summary>
public sealed record PdfAccessibilityFormFieldRepair(
    string FieldName, string Description, PdfAccessibilityReport Before, bool WillChange);

/// <summary>A reviewed description correction for one link annotation.</summary>
public sealed record PdfAccessibilityLinkRepair(
    int PageIndex, int AnnotationIndex, int? ObjectNumber, string Description,
    PdfAccessibilityReport Before, bool WillChange);

/// <summary>The saved document and accessibility reports surrounding one repair.</summary>
public sealed record PdfAccessibilityRepairResult(
    ReadOnlyMemory<byte> Document,
    PdfAccessibilityReport Before,
    PdfAccessibilityReport After);

/// <summary>Previews and applies accessibility corrections that require no semantic inference.</summary>
public static class PdfAccessibilityRepair
{
    /// <summary>Previews adding a supplied user-facing description to one link.</summary>
    public static PdfAccessibilityLinkRepair PreviewLinkDescription(
        PdfDocument document, int pageIndex, int annotationIndex, string description)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        PdfLinkInfo? link = PdfLinkReader.ReadPage(document, pageIndex)
            .SingleOrDefault(item => item.AnnotationIndex == annotationIndex);
        return new PdfAccessibilityLinkRepair(pageIndex, annotationIndex,
            link?.ObjectNumber, description, before,
            link is not null && string.IsNullOrWhiteSpace(link.Description));
    }

    /// <summary>Applies a previewed link description and verifies the saved result.</summary>
    public static PdfAccessibilityRepairResult ApplyLinkDescription(
        PdfDocument document, PdfAccessibilityLinkRepair repair)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(repair);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        PdfLinkInfo? link = PdfLinkReader.ReadPage(document, repair.PageIndex)
            .SingleOrDefault(item => item.AnnotationIndex == repair.AnnotationIndex);
        if (!repair.WillChange || link is null
            || !string.IsNullOrWhiteSpace(link.Description)
            || link.ObjectNumber != repair.ObjectNumber)
            throw new InvalidOperationException(
                "The document does not have the previewed missing link description.");
        byte[] saved = new PdfIncrementalAnnotationEditor(document)
            .SetAnnotationContentsAt(
                repair.PageIndex, repair.AnnotationIndex, repair.Description)
            .Build();
        PdfDocument reopened = PdfDocument.Open(saved);
        PdfLinkInfo? repaired = PdfLinkReader.ReadPage(reopened, repair.PageIndex)
            .SingleOrDefault(item => item.AnnotationIndex == repair.AnnotationIndex);
        if (repaired is null || !string.Equals(
                repaired.Description, repair.Description, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The saved link still has no matching user-facing description.");
        return new PdfAccessibilityRepairResult(
            saved, before, PdfAccessibilityInspector.Inspect(reopened));
    }

    /// <summary>Previews adding a supplied user-facing description to one form field.</summary>
    public static PdfAccessibilityFormFieldRepair PreviewFormFieldDescription(
        PdfDocument document, string fieldName, string description)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        PdfFormWidgetInfo[] widgets = Widgets(document, fieldName);
        bool missing = widgets.Length > 0
            && widgets.Any(widget => string.IsNullOrWhiteSpace(widget.Tooltip));
        return new PdfAccessibilityFormFieldRepair(
            fieldName, description, before, missing);
    }

    /// <summary>Applies a previewed form-field description and verifies the saved result.</summary>
    public static PdfAccessibilityRepairResult ApplyFormFieldDescription(
        PdfDocument document, PdfAccessibilityFormFieldRepair repair)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(repair);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        PdfFormWidgetInfo[] widgets = Widgets(document, repair.FieldName);
        if (!repair.WillChange || widgets.Length == 0
            || widgets.All(widget => !string.IsNullOrWhiteSpace(widget.Tooltip)))
            throw new InvalidOperationException(
                "The document does not have the previewed missing form-field description.");
        string[] mappings = [.. widgets.Select(widget => widget.MappingName)
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)];
        if (mappings.Length > 1)
            throw new InvalidOperationException(
                "The form field has conflicting export mapping names.");
        byte[] saved = new PdfIncrementalPageEditor(document)
            .SetFormFieldMetadata(repair.FieldName, new PdfFormFieldMetadata
            {
                Tooltip = repair.Description,
                MappingName = mappings.SingleOrDefault()
            })
            .Build();
        PdfDocument reopened = PdfDocument.Open(saved);
        PdfFormWidgetInfo[] repaired = Widgets(reopened, repair.FieldName);
        if (repaired.Length == 0 || repaired.Any(widget =>
                !string.Equals(widget.Tooltip, repair.Description, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "The saved form field still has no matching user-facing description.");
        return new PdfAccessibilityRepairResult(
            saved, before, PdfAccessibilityInspector.Inspect(reopened));
    }

    /// <summary>Previews adding supplied alternate text to one reported figure.</summary>
    public static PdfAccessibilityFigureRepair PreviewFigureAlternateDescription(
        PdfDocument document, int objectNumber, string alternateDescription)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        ArgumentException.ThrowIfNullOrWhiteSpace(alternateDescription);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        bool missing = before.Findings.Any(finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingFigureAlternateDescription
            && finding.ObjectNumber == objectNumber);
        return new PdfAccessibilityFigureRepair(
            objectNumber, alternateDescription, before, missing);
    }

    /// <summary>Applies a previewed figure alternate description and verifies the saved result.</summary>
    public static PdfAccessibilityRepairResult ApplyFigureAlternateDescription(
        PdfDocument document, PdfAccessibilityFigureRepair repair)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(repair);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        bool missing = before.Findings.Any(finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingFigureAlternateDescription
            && finding.ObjectNumber == repair.ObjectNumber);
        if (!repair.WillChange || !missing)
            throw new InvalidOperationException(
                "The document does not have the previewed missing figure description.");
        var reference = new PdfIndirectReference(repair.ObjectNumber, 0);
        PdfDictionary figure = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The figure object is not a dictionary.");
        var entries = figure.ToDictionary(item => item.Key, item => item.Value);
        entries[new PdfName("Alt"u8)] = TextString(repair.AlternateDescription);
        byte[] saved = new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries)).Build();
        PdfAccessibilityReport after = PdfAccessibilityInspector.Inspect(PdfDocument.Open(saved));
        if (after.Findings.Any(finding =>
                finding.Code == PdfAccessibilityFindingCode.MissingFigureAlternateDescription
                && finding.ObjectNumber == repair.ObjectNumber))
            throw new InvalidOperationException(
                "The saved figure still has no alternate description.");
        return new PdfAccessibilityRepairResult(saved, before, after);
    }

    /// <summary>Previews adding a supplied primary language when the document has none.</summary>
    public static PdfAccessibilityLanguageRepair PreviewDocumentLanguage(
        PdfDocument document, string language)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        _ = new PdfIncrementalPageEditor(document).SetDocumentLanguage(language);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        bool missing = before.Findings.Any(finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
        return new PdfAccessibilityLanguageRepair(language, before, missing);
    }

    /// <summary>Applies a previewed missing-language correction and verifies the saved result.</summary>
    public static PdfAccessibilityRepairResult ApplyDocumentLanguage(
        PdfDocument document, PdfAccessibilityLanguageRepair repair)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(repair);
        PdfAccessibilityReport before = PdfAccessibilityInspector.Inspect(document);
        bool missing = before.Findings.Any(finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
        if (!repair.WillChange || !missing)
            throw new InvalidOperationException(
                "The document does not have the previewed missing-language finding.");
        byte[] saved = new PdfIncrementalPageEditor(document)
            .SetDocumentLanguage(repair.Language)
            .Build();
        PdfAccessibilityReport after = PdfAccessibilityInspector.Inspect(
            PdfDocument.Open(saved));
        if (after.Findings.Any(finding =>
                finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage))
            throw new InvalidOperationException(
                "The saved document still has no primary language.");
        return new PdfAccessibilityRepairResult(saved, before, after);
    }

    private static PdfString TextString(string value) => new(
        [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(value)],
        PdfStringForm.Hexadecimal);

    private static PdfFormWidgetInfo[] Widgets(PdfDocument document, string fieldName) =>
        [.. PdfPageTree.Read(document).Pages.SelectMany((_, pageIndex) =>
            PdfFormWidgetReader.ReadPage(document, pageIndex))
            .Where(widget => string.Equals(
                widget.FieldName, fieldName, StringComparison.Ordinal))];
}
