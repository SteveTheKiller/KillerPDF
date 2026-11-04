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
    internal EpubLayoutPage(string text, int textStart, int spineIndex, IEnumerable<EpubTextPlacement> placements,
        IEnumerable<NativeGlyphPaint> glyphs, PdfRgbColor? canvasBackground = null)
    {
        Text = text;
        TextStart = textStart;
        SpineIndex = spineIndex;
        TextPlacements = Array.AsReadOnly(placements.ToArray());
        Glyphs = Array.AsReadOnly(glyphs.ToArray());
        CanvasBackground = canvasBackground;
    }
    /// <summary>Gets the page's original structural text.</summary>
    public string Text { get; }
    /// <summary>Gets the original spine UTF-16 offset where this page's text starts.</summary>
    public int TextStart { get; }
    /// <summary>Gets this page's original declared spine index.</summary>
    public int SpineIndex { get; }
    /// <summary>Gets logical text placements independently of glyph ink.</summary>
    public IReadOnlyList<EpubTextPlacement> TextPlacements { get; }
    internal IReadOnlyList<NativeGlyphPaint> Glyphs { get; }
    internal PdfRgbColor? CanvasBackground { get; }
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
            Pages[pageIndex].Glyphs, _options.TextColor, cancellationToken, background: Pages[pageIndex].CanvasBackground);
    }

    /// <summary>Renders original glyph outlines at exact pixel dimensions without reflowing text.</summary>
    public PdfRenderedFrame RenderPage(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (options.Proof is { Enabled: true })
            throw new NotSupportedException("Printer proof rendering is unavailable for native EPUB documents.");
        if (options.SeparationPlate is not null)
            throw new NotSupportedException("Native EPUB rendering does not support color separations.");
        if ((long)options.Width * options.Height > options.PixelByteLimit / (2 * 4 * sizeof(float)))
            throw new ArgumentException("The native render exceeds the caller's combined pixel storage limit.", nameof(options));
        return NativeGlyphPainter.Render(options.Width, options.Height, Pages[pageIndex].Glyphs,
            _options.TextColor, cancellationToken, options.Width / _options.PageWidth,
            options.Height / _options.PageHeight, Pages[pageIndex].CanvasBackground);
    }
}
