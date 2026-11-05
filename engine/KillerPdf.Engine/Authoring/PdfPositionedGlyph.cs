namespace KillerPdf.Engine.Authoring;

/// <summary>A font-owned glyph and its baseline origin in current user space before the CTM.</summary>
public readonly record struct PdfPositionedGlyph(ushort GlyphId, double X, double Y);
