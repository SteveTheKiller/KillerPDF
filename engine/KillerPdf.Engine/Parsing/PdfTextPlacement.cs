using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Parsing;

/// <summary>A source character placed in unrotated PDF user space.</summary>
/// <remarks>Baseline endpoints describe advance width, not the glyph outline or a selection rectangle.</remarks>
public sealed record PdfTextPlacement(
    string Text,
    uint CharacterCode,
    string FontResource,
    double FontSize,
    PdfPoint Origin,
    PdfPoint AdvanceEnd);
