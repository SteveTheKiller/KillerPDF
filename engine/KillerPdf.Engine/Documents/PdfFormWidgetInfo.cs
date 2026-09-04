using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Documents;

/// <summary>Identifies the effective AcroForm field type of a widget.</summary>
public enum PdfFormFieldKind
{
    /// <summary>The field type is absent or not recognized.</summary>
    Unknown,
    /// <summary>A text field.</summary>
    Text,
    /// <summary>A button field, including checkboxes, radio buttons, and push buttons.</summary>
    Button,
    /// <summary>A choice field, including combo boxes and list boxes.</summary>
    Choice,
    /// <summary>A signature field.</summary>
    Signature
}

/// <summary>Describes one export and display pair in a choice field.</summary>
public sealed record PdfFormChoiceInfo
{
    /// <summary>Gets the value written into the field.</summary>
    public required string ExportValue { get; init; }
    /// <summary>Gets the value shown to the user.</summary>
    public required string DisplayValue { get; init; }
}

/// <summary>Describes one page widget with its effective inherited field state.</summary>
public sealed record PdfFormWidgetInfo
{
    /// <summary>Gets the zero-based page containing the widget.</summary>
    public required int PageIndex { get; init; }
    /// <summary>Gets the widget's index in the page annotation array.</summary>
    public required int AnnotationIndex { get; init; }
    /// <summary>Gets the indirect-object number, or zero for a direct widget.</summary>
    public required int ObjectNumber { get; init; }
    /// <summary>Gets the indirect-object generation, or zero for a direct widget.</summary>
    public required int Generation { get; init; }
    /// <summary>Gets the fully qualified field name.</summary>
    public required string FieldName { get; init; }
    /// <summary>Gets the effective user-facing field description.</summary>
    public string Tooltip { get; init; } = string.Empty;
    /// <summary>Gets the effective export mapping name.</summary>
    public string MappingName { get; init; } = string.Empty;
    /// <summary>Gets the effective field type.</summary>
    public required PdfFormFieldKind FieldKind { get; init; }
    /// <summary>Gets the effective field flags.</summary>
    public required long Flags { get; init; }
    /// <summary>Gets the effective current value.</summary>
    public required string Value { get; init; }
    /// <summary>Gets every effective current value for a multi-select choice field.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];
    /// <summary>Gets the effective default appearance string.</summary>
    public required string DefaultAppearance { get; init; }
    /// <summary>Gets the widget background color, or null when none is defined.</summary>
    public PdfRgbColor? BackgroundColor { get; init; }
    /// <summary>Gets the widget border color, or null when none is defined.</summary>
    public PdfRgbColor? BorderColor { get; init; }
    /// <summary>Gets the effective maximum text length, or zero when unspecified.</summary>
    public required int MaximumLength { get; init; }
    /// <summary>Gets the widget's button on-state name, including the leading slash.</summary>
    public required string OnValue { get; init; }
    /// <summary>Gets whether the widget defines an activation action.</summary>
    public required bool HasAction { get; init; }
    /// <summary>Gets whether the widget defines an appearance state.</summary>
    public required bool HasAppearanceState { get; init; }
    /// <summary>Gets the effective choice options.</summary>
    public required IReadOnlyList<PdfFormChoiceInfo> Options { get; init; }
    /// <summary>Gets the normalized widget rectangle's left coordinate.</summary>
    public required double Left { get; init; }
    /// <summary>Gets the normalized widget rectangle's bottom coordinate.</summary>
    public required double Bottom { get; init; }
    /// <summary>Gets the normalized widget rectangle's right coordinate.</summary>
    public required double Right { get; init; }
    /// <summary>Gets the normalized widget rectangle's top coordinate.</summary>
    public required double Top { get; init; }
    /// <summary>Gets the effective rendered page-box left coordinate.</summary>
    public required double PageBoxLeft { get; init; }
    /// <summary>Gets the effective rendered page-box bottom coordinate.</summary>
    public required double PageBoxBottom { get; init; }
    /// <summary>Gets the effective rendered page-box width.</summary>
    public required double PageBoxWidth { get; init; }
    /// <summary>Gets the effective rendered page-box height.</summary>
    public required double PageBoxHeight { get; init; }
    /// <summary>Gets the normalized clockwise page rotation.</summary>
    public required int PageRotation { get; init; }
}
