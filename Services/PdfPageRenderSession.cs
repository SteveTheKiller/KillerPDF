using System.IO;
using EngineDocument = KillerPdf.Engine.Documents.PdfDocument;
using EnginePageInformation = KillerPdf.Engine.Documents.PdfPageInformation;
using EngineRenderOptions = KillerPdf.Engine.Rendering.PdfRenderOptions;
using EngineRenderer = KillerPdf.Engine.Rendering.PdfPageRenderer;

namespace KillerPDF.Services;

/// <summary>
/// Owns one document render session so application features do not depend directly on the
/// current rendering backend. The engine renderer can replace this backend behind the same
/// boundary as its page coverage expands.
/// </summary>
internal sealed class PdfPageRenderSession : IDisposable
{
    private static readonly PdfEncodingBufferPool EncodingBuffers = new();
    private byte[]? _encodingBuffer;
    private EngineRenderer? _engineRenderer;
    private IReadOnlyList<EnginePageInformation> _enginePages;
    private EngineRenderer Renderer => _engineRenderer
        ?? throw new ObjectDisposedException(nameof(PdfPageRenderSession));
    private readonly int _maximumWidth;
    private readonly int _maximumHeight;
    private readonly double _scale;
    // One shared cache per parsed document. Attached via a ConditionalWeakTable so the cache
    // is reclaimed automatically when the document itself becomes unreachable. Passing it to
    // every PdfPageRenderer built for the same document means viewer, sidebar-thumbnail,
    // print-preview, and image-export renderers all reuse parsed page and form instructions,
    // decoded images, glyph masks, flattened glyph outlines, and parsed fonts.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EngineDocument,
        EngineRenderer.SharedCache> _sharedRenderCaches = new();

    private static EngineRenderer.SharedCache SharedCacheFor(EngineDocument document) =>
        _sharedRenderCaches.GetValue(document, static _ => new EngineRenderer.SharedCache());

    private PdfPageRenderSession(EngineDocument document,
        IReadOnlyList<EnginePageInformation> pages, int maximumWidth, int maximumHeight,
        double scale)
    {
        _engineRenderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance,
            SharedCacheFor(document));
        var boxes = KillerPdf.Engine.Documents.PdfPageBoxInformation.Read(document);
        _enginePages = pages.Select((page, index) => PdfLegacyPageGeometry.Size(page, boxes[index])).ToArray();
        _maximumWidth = maximumWidth;
        _maximumHeight = maximumHeight;
        _scale = scale;
    }

    internal static PdfPageRenderSession OpenEngineFirst(
        string path, int maximumWidth, int maximumHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        if (maximumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        EngineDocument document = OpenDocument(path);
        IReadOnlyList<EnginePageInformation> pages = EnginePageInformation.Read(document);
        return new PdfPageRenderSession(document, pages, maximumWidth, maximumHeight, 0);
    }

    // In-process cache of parsed PdfDocument instances keyed by absolute path plus its
    // last-write timestamp plus its size. The engine's PdfDocument is immutable after Open,
    // so sharing one across the primary render session, background render session, sidebar
    // thumbnails, print preview, and image export is safe: each PdfPageRenderer instance
    // keeps its own font, image, and instruction caches on top of the shared parse tree.
    // Weak references let the GC reclaim documents no viewer holds. The key includes size
    // and mtime so any file rewrite invalidates automatically.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        (long Ticks, long Size, WeakReference<EngineDocument> Ref)> _documentCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a file for rendering. Files encrypted with only an owner password open with the
    /// empty user password, the same as every mainstream viewer. Repeated opens of the same
    /// file, while any earlier session on that file is still reachable, share the parsed
    /// PdfDocument so a subsequent thumbnail, preview, or export does not re-parse the file.
    /// </summary>
    internal static EngineDocument OpenDocument(string path)
    {
        string normalized;
        long ticks, size;
        try
        {
            var info = new FileInfo(path);
            normalized = info.FullName;
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
            if (_documentCache.TryGetValue(normalized, out var entry)
                && entry.Ticks == ticks && entry.Size == size
                && entry.Ref.TryGetTarget(out EngineDocument? cached)
                && cached is not null)
                return cached;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The file cannot be stat'd yet the engine may still parse from the stream below;
            // fall through to the direct-open path without touching the cache.
            normalized = null!;
            ticks = 0;
            size = 0;
        }
        EngineDocument document;
        using (FileStream source = File.OpenRead(path))
        {
            document = EngineDocument.OpenWithCompatibilityRecovery(source);
            if (!document.CanReadPageContent)
            {
                try
                {
                    source.Position = 0;
                    document = EngineDocument.OpenWithCompatibilityRecovery(source, string.Empty);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Keep the strict-open document; the caller inspects CanReadPageContent.
                }
            }
        }
        if (normalized is not null)
            _documentCache[normalized] = (ticks, size, new WeakReference<EngineDocument>(document));
        return document;
    }

    internal static PdfPageRenderSession OpenEngineFirst(string path, double scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        EngineDocument document = OpenDocument(path);
        IReadOnlyList<EnginePageInformation> pages = EnginePageInformation.Read(document);
        return new PdfPageRenderSession(document, pages, 0, 0, scale);
    }

    internal int PageCount => _enginePages.Count;

    internal PdfRenderedPage RenderBasePage(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RenderEnginePage(pageIndex, transparentBackground: false,
            includeAnnotations: false, includeFormFields: false, cancellationToken);
    }

    internal PdfRenderedPage RenderPage(int pageIndex, bool transparentBackground = false,
        bool includeFormFields = true, bool removeTransparencyOnFallback = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RenderEnginePage(pageIndex, transparentBackground,
            includeAnnotations: true, includeFormFields, cancellationToken);
    }

    private PdfRenderedPage RenderEnginePage(int pageIndex, bool transparentBackground,
        bool includeAnnotations, bool includeFormFields,
        CancellationToken cancellationToken)
    {
        return RenderOwnedPage(Renderer, pageIndex,
            CreateRenderOptions(pageIndex, transparentBackground, includeAnnotations, includeFormFields),
            cancellationToken);
    }

    // Pixels remain valid until the next encoding render or session disposal.
    internal PdfPageForEncoding RenderPageForEncoding(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        EngineRenderOptions options = CreateRenderOptions(pageIndex, false, true, true);
        int length = checked(options.Width * options.Height * 4);
        if (_encodingBuffer is null || _encodingBuffer.Length < length)
        {
            if (_encodingBuffer is not null) EncodingBuffers.Return(_encodingBuffer);
            _encodingBuffer = null;
            _encodingBuffer = EncodingBuffers.Rent(length);
        }
        var diagnostics = Renderer.RenderInto(pageIndex, options, _encodingBuffer, cancellationToken);
        return new PdfPageForEncoding(options.Width, options.Height,
            _encodingBuffer.AsMemory(0, length), diagnostics);
    }

    private static PdfRenderedPage RenderOwnedPage(EngineRenderer renderer, int pageIndex,
        EngineRenderOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(options.Width * options.Height * 4));
        var diagnostics = renderer.RenderInto(pageIndex, options, pixels, cancellationToken);
        return new PdfRenderedPage(options.Width, options.Height, pixels,
            PdfRenderBackend.Engine, Diagnostics(diagnostics));
    }

    internal PdfRenderedPage RenderFittedPage(int pageIndex, int maximumWidth, int maximumHeight,
        bool includeFormFields = false, CancellationToken cancellationToken = default)
    {
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        if (maximumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        return RenderOwnedPage(Renderer, pageIndex,
            CreateRenderOptions(pageIndex, false, true, includeFormFields, maximumWidth, maximumHeight), cancellationToken);
    }

    private EngineRenderOptions CreateRenderOptions(int pageIndex, bool transparentBackground,
        bool includeAnnotations, bool includeFormFields, int maximumWidth = 0, int maximumHeight = 0)
    {
        ObjectDisposedException.ThrowIf(_engineRenderer is null, this);
        if (pageIndex < 0 || pageIndex >= _enginePages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        EnginePageInformation pageInformation = _enginePages[pageIndex];
        bool quarterTurn = pageInformation.Rotation is 90 or 270;
        // The previous viewer exposed single-precision page geometry before scaling.
        double pageWidth = (float)(quarterTurn ? pageInformation.Height : pageInformation.Width);
        double pageHeight = (float)(quarterTurn ? pageInformation.Width : pageInformation.Height);
        double renderScale = maximumWidth > 0
            ? Math.Min(maximumWidth / pageWidth, maximumHeight / pageHeight) : _scale > 0
            ? _scale : Math.Min(_maximumWidth / pageWidth, _maximumHeight / pageHeight);
        // Preserve the previous viewer's truncation of scaled page dimensions.
        int engineWidth = Math.Max(1, (int)(pageWidth * renderScale));
        int engineHeight = Math.Max(1, (int)(pageHeight * renderScale));
        return new EngineRenderOptions(engineWidth, engineHeight, transparentBackground,
            includeAnnotations, includeFormFields)
        { MaximumParallelism = RenderParallelism(engineWidth, engineHeight) };
    }

    // Large pages split their big row-independent paints across a few threads; output pixels
    // are identical at any thread count. Small pages stay on the calling thread, and the cap
    // leaves cores free for pages the viewer renders concurrently.
    private const long ParallelRenderPixels = 1_048_576;

    internal static int RenderParallelism(int width, int height) =>
        (long)width * height < ParallelRenderPixels ? 1
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private static EngineRenderOptions CreateExactOptions(int width, int height,
        bool transparentBackground, bool includeFormFields) =>
        new(width, height, transparentBackground, includeAnnotations: true, includeFormFields)
        { MaximumParallelism = RenderParallelism(width, height) };

    internal static PdfRenderedPage? RenderExactPage(
        string path, int pageIndex, int width, int height,
        bool transparentBackground = false, bool includeFormFields = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EngineDocument document = OpenDocument(path);
            var renderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance,
                SharedCacheFor(document));
            return RenderOwnedPage(renderer, pageIndex,
                CreateExactOptions(width, height, transparentBackground, includeFormFields), cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && exception is not OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _engineRenderer = null;
        _enginePages = [];
        if (_encodingBuffer is null) return;
        EncodingBuffers.Return(_encodingBuffer);
        _encodingBuffer = null;
    }

    private static string? Diagnostics(IReadOnlyList<string> diagnostics) =>
        diagnostics.Count == 0 ? null : string.Join(" ", diagnostics);
}

internal static class PdfLegacyPageGeometry
{
    internal static float Coordinate(double number)
    {
        if (!decimal.TryParse(number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out decimal value)) return (float)number;
        string digits = Math.Abs(value).ToString("0.############################",
            System.Globalization.CultureInfo.InvariantCulture);
        ReadOnlySpan<float> scales = [0.1f, 0.01f, 0.001f, 0.0001f, 0.00001f,
            0.000001f, 0.0000001f, 0.00000001f, 0.000000001f, 0.0000000001f, 0.00000000001f];
        float result = 0;
        int index = 0;
        while (index < digits.Length && digits[index] != '.')
            result = result * 10 + (digits[index++] - '0');
        for (int place = 0; ++index < digits.Length && place < scales.Length; place++)
            result += scales[place] * (digits[index] - '0');
        return number < 0 ? -result : result;
    }

    internal static EnginePageInformation Size(EnginePageInformation page,
        KillerPdf.Engine.Documents.PdfPageBoxInformation boxes)
    {
        var crop = boxes.CropBox;
        var media = boxes.MediaBox;
        double left = Math.Max(crop.Left, media.Left), bottom = Math.Max(crop.Bottom, media.Bottom);
        double right = Math.Min(crop.Right, media.Right), top = Math.Min(crop.Top, media.Top);
        if (right <= left || top <= bottom)
            (left, bottom, right, top) = (crop.Left, crop.Bottom, crop.Right, crop.Top);
        // Preserve the page reader's recovery when the two box APIs select different fallbacks.
        if (left != page.Left || bottom != page.Bottom || right - left != page.Width || top - bottom != page.Height)
            return page;
        float width = Coordinate(right) - Coordinate(left);
        float height = Coordinate(top) - Coordinate(bottom);
        return float.IsFinite(width) && float.IsFinite(height) && width > 0 && height > 0
            ? page with { Width = width, Height = height } : page;
    }
}

internal enum PdfRenderBackend { Engine }

// Owned by one viewer's UI thread. Background renderers keep independent sessions.
internal sealed class PdfPrimaryRenderSession
{
    private PdfPageRenderSession? _session;
    private string? _path;
    private long _revision;

    internal PdfRenderedPage Render(string path, long revision, int pageIndex, int maximumSize)
    {
        if (_session is null || _path != path || _revision != revision)
        {
            Clear();
            _session = PdfPageRenderSession.OpenEngineFirst(path, maximumSize, maximumSize);
            _path = path;
            _revision = revision;
        }
        return _session.RenderFittedPage(pageIndex, maximumSize, maximumSize);
    }

    internal void Clear()
    {
        _session?.Dispose();
        _session = null;
        _path = null;
    }
}

// A task exclusively owns its lease. Only one idle session is retained per pane.
internal sealed class PdfBackgroundRenderCache
{
    private readonly object _sync = new();
    private Request? _current;
    private PdfPageRenderSession? _idle;

    internal Request Capture(string path, long revision)
    {
        lock (_sync)
        {
            if (_current is not null && _current.Path == path && _current.Revision == revision)
                return _current;
            _idle?.Dispose();
            _idle = null;
            return _current = new Request(this, path, revision);
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            _current = null;
            _idle?.Dispose();
            _idle = null;
        }
    }

    internal sealed class Request(PdfBackgroundRenderCache owner, string path, long revision)
    {
        internal string Path { get; } = path;
        internal long Revision { get; } = revision;

        internal Lease Rent(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageRenderSession? session = null;
            lock (owner._sync)
            {
                if (ReferenceEquals(owner._current, this))
                {
                    session = owner._idle;
                    owner._idle = null;
                }
            }
            // Opening and rendering never hold the cache lock or wait on the UI thread.
            session ??= PdfPageRenderSession.OpenEngineFirst(Path, 1, 1);
            return new Lease(owner, this, session);
        }
    }

    internal sealed class Lease(PdfBackgroundRenderCache owner, Request request,
        PdfPageRenderSession session) : IDisposable
    {
        private PdfPageRenderSession? _session = session;

        internal PdfRenderedPage Render(int pageIndex, int maximumWidth, int maximumHeight,
            CancellationToken cancellationToken = default) =>
            (_session ?? throw new ObjectDisposedException(nameof(Lease))).RenderFittedPage(
                pageIndex, maximumWidth, maximumHeight, cancellationToken: cancellationToken);

        public void Dispose()
        {
            PdfPageRenderSession? released = Interlocked.Exchange(ref _session, null);
            if (released is null) return;
            lock (owner._sync)
            {
                if (ReferenceEquals(owner._current, request) && owner._idle is null)
                {
                    owner._idle = released;
                    return;
                }
            }
            released.Dispose();
        }
    }
}

internal readonly record struct PdfPageForEncoding(
    int Width, int Height, ReadOnlyMemory<byte> Pixels, IReadOnlyList<string> Diagnostics);

internal readonly record struct PdfRenderedPage(
    int Width, int Height, byte[] Pixels, PdfRenderBackend Backend, string? EngineFailure);
