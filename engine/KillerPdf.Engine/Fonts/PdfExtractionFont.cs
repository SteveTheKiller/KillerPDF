namespace KillerPdf.Engine.Fonts;

/// <summary>A horizontal extraction font with widths keyed by source character code.</summary>
/// <remarks>Widths and glyph bounds are in thousandths of text space.</remarks>
public sealed class PdfExtractionFont
{
    private readonly Dictionary<uint, double> _widths;
    private readonly double _defaultWidth;

    /// <summary>Creates an extraction font using a Unicode map and resolved character widths.</summary>
    public PdfExtractionFont(PdfToUnicodeMap unicode, IReadOnlyDictionary<uint, double> widths, double defaultWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(unicode);
        ArgumentNullException.ThrowIfNull(widths);
        if (!double.IsFinite(defaultWidth)) throw new ArgumentOutOfRangeException(nameof(defaultWidth));
        _widths = new Dictionary<uint, double>(widths);
        if (_widths.Values.Any(w => !double.IsFinite(w))) throw new ArgumentException("Font widths must be finite.", nameof(widths));
        Unicode = unicode;
        _defaultWidth = defaultWidth;
    }

    /// <summary>Gets the font's Unicode character mapping.</summary>
    public PdfToUnicodeMap Unicode { get; }

    /// <summary>Gets the PostScript font name from the PDF resource.</summary>
    public string FontName { get; internal init; } = string.Empty;

    /// <summary>Gets the ascender in thousandths of text space.</summary>
    public double Ascent { get; internal init; } = 800;

    /// <summary>Gets the descender in thousandths of text space.</summary>
    public double Descent { get; internal init; } = -200;

    /// <summary>Gets whether the font uses vertical writing.</summary>
    public bool IsVertical { get; internal init; }

    internal Func<uint, uint>? CidSelector { get; init; }
    internal Func<uint, PdfGlyphBounds?>? BoundsReader { get; init; }
    internal Func<uint, PdfGlyphOutline?>? OutlineReader { get; init; }
    internal Func<uint, string?>? UnicodeFallback { get; init; }
    internal Func<ReadOnlyMemory<byte>, IReadOnlyList<PdfDecodedCharacter>>? CharacterDecoder { get; init; }
    internal Func<uint, PdfVerticalGlyphMetrics>? VerticalMetricsReader { get; init; }

    /// <summary>Gets the vertical advance and horizontal-to-vertical origin offset in thousandths of text space.</summary>
    public PdfVerticalGlyphMetrics GetVerticalMetrics(uint code) => VerticalMetricsReader?.Invoke(code)
        ?? new PdfVerticalGlyphMetrics(-1000, GetWidth(code) / 2, 880);

    /// <summary>Decodes text, preserving source codes and substituting U+FFFD for unmapped characters.</summary>
    public IReadOnlyList<PdfDecodedCharacter> Decode(ReadOnlyMemory<byte> source) =>
        CharacterDecoder?.Invoke(source) ?? Unicode.DecodeWithFallback(source.Span, UnicodeFallback);

    internal IReadOnlyList<PdfDecodedCharacter> DecodeWithCompatibilityRecovery(
        ReadOnlyMemory<byte> source) => CharacterDecoder?.Invoke(source)
            ?? Unicode.DecodeWithCompatibilityRecovery(source.Span, UnicodeFallback);

    /// <summary>Returns the embedded outline bounds when available.</summary>
    public PdfGlyphBounds? GetGlyphBounds(uint code) => BoundsReader?.Invoke(code);

    /// <summary>Returns an embedded glyph outline when available.</summary>
    public PdfGlyphOutline? GetGlyphOutline(uint code) => OutlineReader?.Invoke(code);

    /// <summary>Returns a resolved width, or the font's configured missing width.</summary>
    public double GetWidth(uint code) => _widths.GetValueOrDefault(CidSelector?.Invoke(code) ?? code, _defaultWidth);
}

/// <summary>A glyph outline box in thousandths of text space.</summary>
public readonly record struct PdfGlyphBounds(double Left, double Bottom, double Right, double Top);

/// <summary>A glyph outline containing ordered contours in thousandths of text space.</summary>
public sealed record PdfGlyphOutline(IReadOnlyList<PdfGlyphContour> Contours);

/// <summary>A closed contour containing on-curve and quadratic or cubic control points.</summary>
public sealed record PdfGlyphContour(IReadOnlyList<PdfGlyphPoint> Points);

/// <summary>A point in a glyph contour.</summary>
public readonly record struct PdfGlyphPoint(
    double X, double Y, bool OnCurve, bool IsCubicControl = false);

/// <summary>Vertical writing advance and origin offsets in thousandths of text space.</summary>
public readonly record struct PdfVerticalGlyphMetrics(double Advance, double OriginX, double OriginY);
