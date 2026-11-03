using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>Describes native page dimensions and fallback typography in CSS pixels.</summary>
public sealed record EpubLayoutOptions(double PageWidth, double PageHeight, TrueTypeFont DefaultFont)
{
    /// <summary>Gets the fallback font size.</summary>
    public double FontSize { get; init; } = 16;
    /// <summary>Gets the page margin.</summary>
    public double Margin { get; init; } = 16;
    /// <summary>Gets the fallback text color.</summary>
    public PdfRgbColor TextColor { get; init; } = new(0, 0, 0);
}

/// <summary>Locates a UTF-16 range within structural spine text and its logical CSS-pixel bounds.</summary>
public sealed record EpubTextPlacement(int SpineIndex, int Start, int Length,
    double Left, double Top, double Width, double Height);

/// <summary>Provides native page text and logical placement geometry.</summary>
public sealed class EpubLayoutPage
{
    internal EpubLayoutPage(string text, IEnumerable<EpubTextPlacement> placements,
        IEnumerable<NativeGlyphPaint> glyphs)
    {
        Text = text;
        TextPlacements = Array.AsReadOnly(placements.ToArray());
        Glyphs = Array.AsReadOnly(glyphs.ToArray());
    }
    /// <summary>Gets the page's original structural text.</summary>
    public string Text { get; }
    /// <summary>Gets logical text placements independently of glyph ink.</summary>
    public IReadOnlyList<EpubTextPlacement> TextPlacements { get; }
    internal IReadOnlyList<NativeGlyphPaint> Glyphs { get; }
}

/// <summary>Owns native EPUB pages, typography and rendering state.</summary>
public sealed class EpubLayoutDocument
{
    private readonly EpubLayoutOptions _options;

    internal EpubLayoutDocument(EpubLayoutOptions options, IEnumerable<EpubLayoutPage> pages)
    {
        _options = options with { };
        Pages = Array.AsReadOnly(pages.ToArray());
    }
    /// <summary>Gets the immutable native pages.</summary>
    public IReadOnlyList<EpubLayoutPage> Pages { get; }
    /// <summary>Renders a native page at its natural CSS-pixel dimensions.</summary>
    public PdfRenderedFrame RenderPage(int pageIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return NativeGlyphPainter.Render((int)Math.Ceiling(_options.PageWidth), (int)Math.Ceiling(_options.PageHeight),
            Pages[pageIndex].Glyphs, _options.TextColor, cancellationToken);
    }
}
