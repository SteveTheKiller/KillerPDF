namespace KillerPdf.Engine.Fonts;

/// <summary>A horizontal extraction font with widths keyed by source character code.</summary>
/// <remarks>Widths are in thousandths of text space. Font-resource and CID-width resolution are caller responsibilities.</remarks>
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

    /// <summary>Returns a resolved width, or the font's configured missing width.</summary>
    public double GetWidth(uint code) => _widths.GetValueOrDefault(code, _defaultWidth);
}
