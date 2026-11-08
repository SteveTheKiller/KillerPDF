using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Formats.Epub;

internal readonly record struct EpubClusterGlyph(ushort GlyphId, double Advance, double OffsetX, double OffsetY);

// Source ownership stays independent of the selected font glyphs.
internal sealed class EpubGlyphCluster(int start, int length, TrueTypeFont font,
    IEnumerable<EpubClusterGlyph> glyphs, bool substituted = false)
{
    internal int Start { get; } = start;
    internal int Length { get; } = length;
    internal TrueTypeFont Font { get; } = font;
    internal bool Substituted { get; } = substituted;
    internal IReadOnlyList<EpubClusterGlyph> Glyphs { get; } = Array.AsReadOnly(glyphs.ToArray());
    internal double Advance => Glyphs.Sum(glyph => glyph.Advance);
    /// <summary>A soft hyphen, zero-width or fixed-width space: it advances but draws no ink and no PDF text.</summary>
    internal bool Blank { get; init; }
    /// <summary>No available font maps the character; the font's missing-glyph box is drawn and PDF text omits it.</summary>
    internal bool Missing { get; init; }
}
