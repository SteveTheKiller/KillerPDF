using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>Names a CSS font family, distinguishing a generic from a quoted family name.</summary>
public sealed record EpubFontFamily(string Name, bool IsGeneric = false);

/// <summary>Describes native page dimensions and fallback typography in CSS pixels.</summary>
public sealed record EpubLayoutOptions(double PageWidth, double PageHeight, TrueTypeFont DefaultFont)
{
    /// <summary>Gets the fallback font size.</summary>
    public double FontSize { get; init; } = 16;
    /// <summary>Gets the page margin.</summary>
    public double Margin { get; init; } = 16;
    /// <summary>Gets the fallback text color.</summary>
    public PdfRgbColor TextColor { get; init; } = new(0, 0, 0);
    /// <summary>Gets an optional host font-family resolver for weight 400. Missing faces fall back to DefaultFont.</summary>
    public Func<EpubFontFamily, TrueTypeFont?>? FontFamilyResolver { get; init; }
    /// <summary>Gets an optional weight-aware resolver, used instead of the legacy family resolver when supplied.</summary>
    public Func<EpubFontFaceRequest, EpubFontFaceResult?>? FontFaceResolver { get; init; }
}

/// <summary>Locates a UTF-16 range within display-filtered spine text and its logical CSS-pixel bounds.</summary>
public sealed record EpubTextPlacement(int SpineIndex, int Start, int Length,
    double Left, double Top, double Width, double Height)
{
    /// <summary>Gets the resolved CSS text color, or null for the fallback color.</summary>
    public PdfRgbColor? Color { get; init; }
    /// <summary>Gets the package-relative local hyperlink target, without fetching or executing it.</summary>
    public string? LinkTarget { get; init; }
}

/// <summary>Locates a native EPUB navigation destination in logical CSS pixels.</summary>
public sealed record EpubNavigationDestination(int PageIndex, int SpineIndex, int TextOffset,
    double Left, double Top);

/// <summary>Provides native page text and logical placement geometry.</summary>
public sealed class EpubLayoutPage
{
    internal EpubLayoutPage(string text, int textStart, int spineIndex, IEnumerable<EpubTextPlacement> placements,
        IEnumerable<NativePagePaint> glyphs, PdfRgbColor? canvasBackground = null,
        IEnumerable<EpubGlyphCluster>? clusters = null, IEnumerable<(int Start, int Length)>? hiddenTextRanges = null,
        IEnumerable<EpubTextPlacement>? navigationPlacements = null)
    {
        Text = text;
        TextStart = textStart;
        SpineIndex = spineIndex;
        TextPlacements = Array.AsReadOnly(placements.ToArray());
        Glyphs = Array.AsReadOnly(glyphs.ToArray());
        CanvasBackground = canvasBackground;
        Clusters = (clusters ?? []).ToDictionary(cluster => cluster.Start);
        HiddenTextRanges = Array.AsReadOnly((hiddenTextRanges ?? []).ToArray());
        NavigationPlacements = navigationPlacements is null ? TextPlacements : Array.AsReadOnly(navigationPlacements.ToArray());
    }
    /// <summary>Gets the page's normalized text with display-none subtrees omitted.</summary>
    public string Text { get; }
    /// <summary>Gets the UTF-16 offset in display-filtered spine text where this page starts.</summary>
    public int TextStart { get; }
    /// <summary>Gets this page's original declared spine index.</summary>
    public int SpineIndex { get; }
    /// <summary>Gets logical text placements independently of glyph ink.</summary>
    public IReadOnlyList<EpubTextPlacement> TextPlacements { get; }
    internal IReadOnlyList<NativePagePaint> Glyphs { get; }
    internal IReadOnlyDictionary<int, EpubGlyphCluster> Clusters { get; }
    internal IReadOnlyList<(int Start, int Length)> HiddenTextRanges { get; }
    internal IReadOnlyList<EpubTextPlacement> NavigationPlacements { get; }
    internal PdfRgbColor? CanvasBackground { get; }

    internal IEnumerable<EpubTextPlacement> LinkRuns(CancellationToken cancellationToken)
    {
        EpubTextPlacement? run = null;
        foreach (var placement in TextPlacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (placement.LinkTarget is null || placement.Width <= 0 || placement.Height <= 0)
            {
                if (run is not null) yield return run;
                run = null;
                continue;
            }
            if (run is not null && run.LinkTarget == placement.LinkTarget && run.Top == placement.Top
                && run.Height == placement.Height && run.Start + run.Length == placement.Start
                && placement.Left >= run.Left)
                run = run with { Length = run.Length + placement.Length,
                    Width = Math.Max(run.Width, placement.Left + placement.Width - run.Left) };
            else
            {
                if (run is not null) yield return run;
                run = placement;
            }
        }
        if (run is not null) yield return run;
    }
}

/// <summary>Owns native EPUB pages, typography and rendering state.</summary>
public sealed class EpubLayoutDocument
{
    private readonly EpubLayoutOptions _options;
    private readonly string[] _spineHrefs;
    private readonly (int SpineIndex, string Id, int Offset)[] _anchors;
    private readonly Dictionary<(int SpineIndex, string Id, int Offset), EpubNavigationDestination> _cellAnchors;

    internal EpubLayoutDocument(EpubLayoutOptions options, IEnumerable<EpubLayoutPage> pages,
        IEnumerable<string> spineHrefs, IEnumerable<(int SpineIndex, string Id, int Offset)> anchors,
        Dictionary<(int SpineIndex, string Id, int Offset), EpubNavigationDestination> cellAnchors)
    {
        _options = options with { };
        Pages = Array.AsReadOnly(pages.ToArray());
        _spineHrefs = spineHrefs.ToArray();
        _anchors = anchors.ToArray();
        _cellAnchors = new(cellAnchors);
    }
    /// <summary>Gets the immutable native pages.</summary>
    public IReadOnlyList<EpubLayoutPage> Pages { get; }

    /// <summary>Resolves an exact, case-sensitive package-relative chapter target returned by EPUB navigation.</summary>
    /// <remarks>Targets are limited to 4,096 characters. Fragment IDs are case-sensitive.</remarks>
    public EpubNavigationDestination ResolveNavigationTarget(string target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        if (target.Length > 4096)
            throw new ArgumentException("The EPUB navigation target exceeds its 4,096 character limit.", nameof(target));
        int fragmentStart = target.IndexOf('#');
        string fragment = fragmentStart < 0 ? "" : Uri.UnescapeDataString(target[(fragmentStart + 1)..]);
        if (fragmentStart >= 0) target = target[..fragmentStart];
        int spineIndex = Array.IndexOf(_spineHrefs, target);
        if (spineIndex >= 0 && Array.IndexOf(_spineHrefs, target, spineIndex + 1) >= 0)
            throw new InvalidDataException("The EPUB navigation target occurs more than once in the native spine.");
        if (fragment.Length > 0)
        {
            int? anchorOffset = null;
            foreach (var anchor in _anchors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (anchor.SpineIndex != spineIndex || anchor.Id != fragment) continue;
                if (anchorOffset is not null)
                    throw new InvalidDataException("The EPUB navigation anchor occurs more than once in the native chapter.");
                anchorOffset = anchor.Offset;
            }
            if (anchorOffset is { } offset)
            {
                if (_cellAnchors.TryGetValue((spineIndex, fragment, offset), out var cellDestination))
                    return cellDestination;
                EpubNavigationDestination? cursor = null;
                for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Pages[pageIndex].SpineIndex != spineIndex) continue;
                    cursor ??= new(pageIndex, spineIndex, offset, _options.Margin, _options.Margin);
                    foreach (var placement in Pages[pageIndex].NavigationPlacements)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (placement.Start >= offset)
                            return new(pageIndex, spineIndex, offset, placement.Left, placement.Top);
                        cursor = new(pageIndex, spineIndex, offset, placement.Left + placement.Width, placement.Top);
                    }
                }
                if (cursor is not null) return cursor;
            }
            throw new ArgumentException("The EPUB navigation anchor is not in the native chapter.", nameof(target));
        }
        for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
            if (Pages[pageIndex].SpineIndex == spineIndex)
                return new EpubNavigationDestination(pageIndex, spineIndex, 0, _options.Margin, _options.Margin);
        throw new ArgumentException("The EPUB navigation target is not in the native spine.", nameof(target));
    }

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
        if (options.SeparationPlate is not null)
            throw new NotSupportedException("Native EPUB rendering does not support color separations.");
        if ((long)options.Width * options.Height > options.PixelByteLimit / (2 * 4 * sizeof(float)))
            throw new ArgumentException("The native render exceeds the caller's combined pixel storage limit.", nameof(options));
        PdfNativeFrameProof.Preflight(options);
        var frame = NativeGlyphPainter.Render(options.Width, options.Height, Pages[pageIndex].Glyphs,
            _options.TextColor, cancellationToken, options.Width / _options.PageWidth,
            options.Height / _options.PageHeight, Pages[pageIndex].CanvasBackground);
        return PdfNativeFrameProof.Apply(frame, options, cancellationToken);
    }
}
