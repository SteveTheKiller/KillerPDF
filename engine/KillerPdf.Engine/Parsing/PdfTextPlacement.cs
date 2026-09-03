using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Parsing;

/// <summary>A source character placed in unrotated PDF user space.</summary>
/// <remarks>Baseline endpoints describe advance width, not the glyph outline or a selection rectangle.</remarks>
public sealed record PdfTextPlacement(
    string Text,
    uint CharacterCode,
    string FontResource,
    double FontSize,
    PdfPoint Origin,
    PdfPoint AdvanceEnd)
{
    /// <summary>Gets the font's face name.</summary>
    public string FontName { get; init; } = FontResource;
    /// <summary>Gets the effective size after text and graphics transforms.</summary>
    public double PointSize { get; init; } = Math.Abs(FontSize);
    /// <summary>Gets the transformed glyph bounds.</summary>
    public PdfContentBounds Bounds { get; init; }
}
