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
    /// <summary>Gets an optional host font-family resolver for upright weight 400. Missing upright faces fall back to DefaultFont.</summary>
    public Func<EpubFontFamily, TrueTypeFont?>? FontFamilyResolver { get; init; }
    /// <summary>Gets an optional resolver for real font weights and styles, used instead of the legacy family resolver when supplied.</summary>
    public Func<EpubFontFaceRequest, EpubFontFaceResult?>? FontFaceResolver { get; init; }
    /// <summary>
    /// Gets whether a chapter whose content is refused is replaced by a diagnosis page instead of
    /// refusing the whole book. Package, safety and cancellation failures still end layout.
    /// </summary>
    public bool IsolateChapterFailures { get; init; }
    /// <summary>Gets the caller's localized heading for an isolated chapter's diagnosis page.</summary>
    public string UnavailableChapterLabel { get; init; } = "This chapter could not be displayed.";
    /// <summary>
    /// Gets an optional callback that layout invokes on its own thread after each spine item, laid out or
    /// isolated, with the number of completed spine items and the spine's length.
    /// </summary>
    public Action<int, int>? ChapterProgress { get; init; }

    /// <summary>Reports whether native layout accepts this page geometry and fallback font size.</summary>
    public static bool FitsPageLimits(double pageWidth, double pageHeight, double margin, double fontSize)
        => PageLimitError(pageWidth, pageHeight, margin, fontSize) is null;

    // The one copy of the page rule: CreateLayout throws what this returns, and FitsPageLimits reports it.
    internal static Exception? PageLimitError(double pageWidth, double pageHeight, double margin, double fontSize)
    {
        if (!double.IsFinite(pageWidth) || pageWidth is <= 0 or > 32768)
            return new ArgumentOutOfRangeException("options", "Native page width must be in (0,32768].");
        if (!double.IsFinite(pageHeight) || pageHeight is <= 0 or > 32768)
            return new ArgumentOutOfRangeException("options", "Native page height must be in (0,32768].");
        if (!double.IsFinite(margin) || margin < 0)
            return new ArgumentOutOfRangeException("options", "Native page margin must be finite and nonnegative.");
        double contentWidth = pageWidth - 2 * margin;
        double contentHeight = pageHeight - 2 * margin;
        if (contentWidth <= 0 || contentHeight <= 0 || !double.IsFinite(fontSize)
            || fontSize <= 0 || fontSize > contentHeight)
            return new ArgumentOutOfRangeException("options", "Native typography must fit a positive content box.");
        if ((long)Math.Ceiling(pageWidth) * (long)Math.Ceiling(pageHeight)
            > 128L * 1024 * 1024 / (2 * 4 * sizeof(float)))
            return new InvalidDataException("The native page exceeds its combined 128 MiB pixel storage limit.");
        return null;
    }
}

/// <summary>Records a chapter replaced by a diagnosis page during isolated layout.</summary>
/// <param name="SpineIndex">The refused chapter's spine index.</param>
/// <param name="Href">The refused chapter's package-relative path.</param>
/// <param name="Message">The refusal's diagnosis.</param>
public sealed record EpubChapterDiagnostic(int SpineIndex, string Href, string Message)
{
    /// <summary>Gets the stable reason category, for localizing the English message.</summary>
    public EpubOmissionReason Reason { get; init; }
    /// <summary>Gets the package-relative path of the one omitted item (an image or a link target), or null when there is none.</summary>
    public string? ItemHref { get; init; }
}

/// <summary>Why native EPUB layout left out a chapter or an item; values keep their meaning across versions.</summary>
public enum EpubOmissionReason
{
    /// <summary>No narrower category applies; the message describes it.</summary>
    Other,
    /// <summary>The chapter or image is encrypted (DRM).</summary>
    Encrypted,
    /// <summary>The chapter uses scripting, which the reader does not run.</summary>
    Scripting,
    /// <summary>The text needs shaping or bidirectional processing the reader does not implement yet.</summary>
    UnsupportedScript,
    /// <summary>The chapter uses a layout construct the reader does not implement yet.</summary>
    UnsupportedLayout,
    /// <summary>No available font has a glyph for a character, so a missing-glyph box is shown.</summary>
    MissingGlyph,
    /// <summary>A referenced file is not in the book.</summary>
    MissingResource,
    /// <summary>An image uses a format or encoding the reader cannot decode.</summary>
    UnsupportedImageFormat,
    /// <summary>An image uses a color profile other than sRGB.</summary>
    ImageColorProfile,
    /// <summary>An image is larger than the reader can decode.</summary>
    ImageTooLarge,
    /// <summary>An image file is damaged.</summary>
    ImageUnreadable,
    /// <summary>The chapter exceeds a safety limit.</summary>
    LimitExceeded,
    /// <summary>The chapter's markup is malformed.</summary>
    InvalidContent
}

/// <summary>Counts the CSS declarations and rules a laid-out chapter ignored as invalid or not implemented.</summary>
/// <param name="SpineIndex">The chapter's spine index.</param>
/// <param name="Href">The chapter's package-relative path.</param>
/// <param name="Count">The number of ignored declarations and dropped rules, always above zero.</param>
public sealed record EpubIgnoredDeclarations(int SpineIndex, string Href, int Count);

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

/// <summary>An ordered-list number as drawn: its text, font, left edge, baseline and size in CSS pixels.</summary>
internal sealed record EpubListMarker(string Text, TrueTypeFont Font, double Left, double Baseline, double Size, PdfRgbColor? Color);

/// <summary>Provides native page text and logical placement geometry.</summary>
public sealed class EpubLayoutPage
{
    internal EpubLayoutPage(string text, int textStart, int spineIndex, IEnumerable<EpubTextPlacement> placements,
        IEnumerable<NativePagePaint> glyphs, PdfRgbColor? canvasBackground = null,
        IEnumerable<EpubGlyphCluster>? clusters = null, IEnumerable<(int Start, int Length)>? hiddenTextRanges = null,
        IEnumerable<EpubTextPlacement>? navigationPlacements = null, IEnumerable<EpubListMarker>? markers = null,
        EpubImageStore? images = null)
    {
        Markers = Array.AsReadOnly((markers ?? []).ToArray());
        _text = text;
        TextStart = textStart;
        SpineIndex = spineIndex;
        _storage = new EpubPageStorage(placements, navigationPlacements, glyphs, clusters, images);
        CanvasBackground = canvasBackground;
        HiddenTextRanges = Array.AsReadOnly((hiddenTextRanges ?? []).ToArray());
    }

    // An index entry: the page's text, its chapter and its position there; the layout itself is rebuilt on demand.
    private EpubLayoutPage(EpubLayoutPage resident, EpubChapterPages chapter, int ordinal)
    {
        Markers = resident.Markers;
        TextStart = resident.TextStart;
        SpineIndex = resident.SpineIndex;
        CanvasBackground = resident.CanvasBackground;
        HiddenTextRanges = resident.HiddenTextRanges;
        string text = resident.Text;
        byte[] encoded = System.Text.Encoding.UTF8.GetBytes(text);
        // Book text is valid Unicode, so UTF-8 keeps it exactly in about half the space; anything else stays a string.
        if (System.Text.Encoding.UTF8.GetString(encoded) == text) _utf8Text = encoded;
        else _text = text;
        _chapter = chapter;
        _ordinal = ordinal;
    }

    private readonly EpubPageStorage? _storage;
    private readonly EpubChapterPages? _chapter;
    private readonly int _ordinal;
    private readonly string? _text;
    private readonly byte[]? _utf8Text;
    // The decoded text is kept only while a caller still uses it, so repeated reads do not decode again.
    private WeakReference<string>? _decoded;

    /// <summary>Turns a laid-out page into its compact index entry.</summary>
    internal EpubLayoutPage Indexed(EpubChapterPages chapter, int ordinal) => new(this, chapter, ordinal);

    private EpubPageStorage Storage => _storage ?? _chapter!.Resident(this, _ordinal)._storage!;

    /// <summary>Gets the page's normalized text with display-none subtrees omitted.</summary>
    public string Text
    {
        get
        {
            if (_text is not null) return _text;
            if (_decoded is not null && _decoded.TryGetTarget(out var cached)) return cached;
            string text = System.Text.Encoding.UTF8.GetString(_utf8Text!);
            _decoded = new WeakReference<string>(text);
            return text;
        }
    }
    /// <summary>Gets the UTF-16 offset in display-filtered spine text where this page starts.</summary>
    public int TextStart { get; }
    /// <summary>Gets this page's original declared spine index.</summary>
    public int SpineIndex { get; }
    /// <summary>Gets logical text placements independently of glyph ink.</summary>
    public IReadOnlyList<EpubTextPlacement> TextPlacements => Storage.TextPlacements;
    /// <summary>Rebuilds this page's paints on each access; deferred images are decoded through the book's store.</summary>
    internal IReadOnlyList<NativePagePaint> Glyphs => Storage.Paints();
    /// <summary>The number of paints without rebuilding them.</summary>
    internal int PaintCount => Storage.PaintCount;
    /// <summary>The paints as laid out, with images still deferred, for moving a cell page into its row.</summary>
    internal IReadOnlyList<NativePagePaint> StoredPaints => Storage.Paints(decodeImages: false);
    internal IReadOnlyDictionary<int, EpubGlyphCluster> Clusters => Storage.Clusters;
    internal IReadOnlyList<(int Start, int Length)> HiddenTextRanges { get; }
    internal IReadOnlyList<EpubTextPlacement> NavigationPlacements => Storage.NavigationPlacements;
    /// <summary>Ordered-list numbers drawn on this page; they are not part of the page text.</summary>
    internal IReadOnlyList<EpubListMarker> Markers { get; }
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
    private readonly Dictionary<(int SpineIndex, int Offset), EpubNavigationDestination> _anchorDestinations;

    internal EpubLayoutDocument(EpubLayoutOptions options, IEnumerable<EpubLayoutPage> pages,
        IEnumerable<string> spineHrefs, IEnumerable<(int SpineIndex, string Id, int Offset)> anchors,
        Dictionary<(int SpineIndex, string Id, int Offset), EpubNavigationDestination> cellAnchors,
        Dictionary<(int SpineIndex, int Offset), EpubNavigationDestination> anchorDestinations)
    {
        _options = options with { };
        Pages = Array.AsReadOnly(pages.ToArray());
        _spineHrefs = spineHrefs.ToArray();
        _anchors = anchors.ToArray();
        _cellAnchors = new(cellAnchors);
        _anchorDestinations = new(anchorDestinations);
    }

    /// <summary>
    /// Finds where each anchor of a just laid-out chapter lands, while its placements are still in memory: the first
    /// navigation placement at or after the anchor's offset, or the end of the chapter's last placement.
    /// </summary>
    internal static void RecordAnchorDestinations(IReadOnlyList<EpubLayoutPage> pages, int firstPage, int spineIndex,
        IEnumerable<int> offsets, double margin, Dictionary<(int SpineIndex, int Offset), EpubNavigationDestination> destinations,
        CancellationToken cancellationToken)
    {
        if (firstPage >= pages.Count) return;
        // A running maximum of placement starts makes "first placement whose start reaches the offset" a binary search.
        var reach = new List<int>();
        var at = new List<(int Page, EpubTextPlacement Placement)>();
        for (int pageIndex = firstPage; pageIndex < pages.Count; pageIndex++)
            foreach (var placement in pages[pageIndex].NavigationPlacements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reach.Add(reach.Count == 0 ? placement.Start : Math.Max(reach[^1], placement.Start));
                at.Add((pageIndex, placement));
            }
        foreach (int offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (destinations.ContainsKey((spineIndex, offset))) continue;
            int low = 0, high = reach.Count;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (reach[middle] >= offset) high = middle; else low = middle + 1;
            }
            destinations[(spineIndex, offset)] = low < at.Count
                ? new(at[low].Page, spineIndex, offset, at[low].Placement.Left, at[low].Placement.Top)
                : at.Count > 0
                    ? new(at[^1].Page, spineIndex, offset, at[^1].Placement.Left + at[^1].Placement.Width, at[^1].Placement.Top)
                    : new(firstPage, spineIndex, offset, margin, margin);
        }
    }
    /// <summary>Gets the immutable native pages.</summary>
    public IReadOnlyList<EpubLayoutPage> Pages { get; }

    /// <summary>The cache that lays chapters out again on demand.</summary>
    internal EpubChapterPages? ChapterPages { get; init; }

    /// <summary>Gets the chapters replaced by diagnosis pages, in reading order (linear items first, then non-linear ones).</summary>
    public IReadOnlyList<EpubChapterDiagnostic> ChapterDiagnostics { get; init; } = [];

    /// <summary>Gets single items, such as images, that were omitted while their chapter still laid out.</summary>
    public IReadOnlyList<EpubChapterDiagnostic> ContentDiagnostics { get; init; } = [];

    /// <summary>Gets, for each laid-out chapter that ignored any, how many CSS declarations and rules it ignored.</summary>
    public IReadOnlyList<EpubIgnoredDeclarations> IgnoredDeclarations { get; init; } = [];

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
        // An isolated chapter has no laid-out anchors; every target in it lands on its diagnosis page.
        if (spineIndex >= 0 && ChapterDiagnostics.Any(item => item.SpineIndex == spineIndex))
        {
            for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
                if (Pages[pageIndex].SpineIndex == spineIndex)
                    return new(pageIndex, spineIndex, 0, _options.Margin, _options.Margin);
        }
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
                // Recorded at layout, so following a link never lays its chapter out again.
                if (_anchorDestinations.TryGetValue((spineIndex, offset), out var destination))
                    return destination;
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
