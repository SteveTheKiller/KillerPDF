using System.IO;
using EngineDocument = KillerPdf.Engine.Documents.PdfDocument;
using EngineDocumentFormat = KillerPdf.Engine.Documents.PdfDocumentFormat;
using EnginePageInformation = KillerPdf.Engine.Documents.PdfPageInformation;
using EngineReadSnapshot = KillerPdf.Engine.Documents.PdfReadSnapshot;
using EngineRenderOptions = KillerPdf.Engine.Rendering.PdfRenderOptions;
using EngineRenderer = KillerPdf.Engine.Rendering.PdfPageRenderer;

namespace KillerPDF.Services;

/// <summary>
/// Owns one engine render session and its output buffers so application features share the
/// engine boundary without depending on rasterizer implementation details.
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
    private readonly bool _forPrinting;
    // One shared cache per parsed document. Attached via a ConditionalWeakTable so the cache
    // is reclaimed automatically when the document itself becomes unreachable. Passing it to
    // every PdfPageRenderer built for the same document means viewer, sidebar-thumbnail,
    // print-preview, and image-export renderers all reuse parsed page and form instructions,
    // decoded images, glyph masks, flattened glyph outlines, and parsed fonts.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EngineDocument,
        EngineRenderer.SharedCache> _sharedRenderCaches = [];

    private static EngineRenderer.SharedCache SharedCacheFor(EngineDocument document) =>
        _sharedRenderCaches.GetValue(document, static _ => new EngineRenderer.SharedCache());

    // What reading a document's pages costs, and how many times it was paid. Read once per parsed document
    // and shared, so a session that is let go of and opened again does not read the page tree again.
    private sealed class PageRead
    {
        internal IReadOnlyList<EnginePageInformation>? Sized;
        internal int Reads;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EngineDocument, PageRead> _pageReads = [];

    private static IReadOnlyList<EnginePageInformation> SizeAll(EngineDocument document,
        IReadOnlyList<EnginePageInformation> pages)
    {
        var boxes = KillerPdf.Engine.Documents.PdfPageBoxInformation.Read(document);
        return [.. pages.Select((page, index) => PdfLegacyPageGeometry.Size(page, boxes[index]))];
    }

    /// <summary>The document's pages with their legacy sizes, read from the page tree.</summary>
    internal static IReadOnlyList<EnginePageInformation> SizedPages(EngineDocument document)
    {
        var read = _pageReads.GetValue(document, static _ => new PageRead());
        lock (read)
        {
            if (read.Sized is null)
            {
                read.Reads++;
                read.Sized = SizeAll(document, EnginePageInformation.Read(document));
            }
            return read.Sized;
        }
    }

    /// <summary>How many times the document's page tree was read for a render session.</summary>
    internal static int PageInformationReads(EngineDocument document) =>
        _pageReads.TryGetValue(document, out var read) ? read.Reads : 0;

    private PdfPageRenderSession(EngineDocument document,
        IReadOnlyList<EnginePageInformation> sizedPages, int maximumWidth, int maximumHeight,
        double scale, bool forPrinting = false)
    {
        _engineRenderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance,
            SharedCacheFor(document), WicJpegDecoder.Instance);
        _enginePages = sizedPages;
        _maximumWidth = maximumWidth;
        _maximumHeight = maximumHeight;
        _scale = scale;
        _forPrinting = forPrinting;
        IsEncrypted = document.IsEncrypted;
    }

    /// <summary>True when the document carries encryption, so its permissions decide what leaves it.</summary>
    internal bool IsEncrypted { get; }

    internal static PdfPageRenderSession OpenCapturedDocument(EngineDocument document,
        IReadOnlyList<EnginePageInformation> pages) => new(document, SizeAll(document, pages), 1, 1, 0);

    internal KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFrame(int pageIndex,
        EngineRenderOptions options, CancellationToken cancellationToken) =>
        Renderer.RenderFrame(pageIndex, options, cancellationToken);

    /// <summary>Opens a tab's file for rendering under the tab's protection, or a file that is no
    /// tab's with null.</summary>
    internal static PdfPageRenderSession OpenEngineFirst(
        string path, int maximumWidth, int maximumHeight, OpenedProtection? protection, bool forPrinting = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);
        EngineDocument document = OpenDocument(path, protection);
        if (!document.CanReadPageContent)
            throw new InvalidOperationException("Authenticate the document before rendering page content.");
        return new PdfPageRenderSession(document, SizedPages(document), maximumWidth, maximumHeight, 0, forPrinting);
    }

    internal static PdfPageRenderSession OpenEngineFirstWithPassword(
        string path, int maximumWidth, int maximumHeight, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);
        ArgumentNullException.ThrowIfNull(password);
        using FileStream source = File.OpenRead(path);
        EngineDocument document = EngineDocument.OpenWithCompatibilityRecovery(source, password);
        return new PdfPageRenderSession(document, SizeAll(document, EnginePageInformation.Read(document)),
            maximumWidth, maximumHeight, 0);
    }

    // In-process cache of parsed PdfDocument instances keyed by absolute path plus its
    // last-write timestamp plus its size. The engine's PdfDocument is immutable after Open,
    // so sharing one across the primary render session, background render session, sidebar
    // thumbnails, print preview, and image export is safe: each PdfPageRenderer instance
    // keeps its own font, image, and instruction caches on top of the shared parse tree.
    // Weak references let the GC reclaim documents no viewer holds. The key includes size
    // and mtime so any file rewrite invalidates automatically.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedDocument> _documentCache =
        new(StringComparer.OrdinalIgnoreCase);

    // A parse that only a tab's key could decrypt is kept with that tab's protection and served to no
    // caller without it. The table holds the protection weakly, so the parses go when the tab does,
    // and ForgetKey drops them the moment the tab closes.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OpenedProtection,
        System.Collections.Concurrent.ConcurrentDictionary<string, CachedDocument>> _keyedCache = [];

    private sealed record CachedDocument(long Ticks, long Size, WeakReference<EngineDocument> Ref,
        WeakReference<PdfWorkingDocument>? Working)
    {
        /// <summary>When the file was read for this parse, for the racy-timestamp check.</summary>
        internal long ReadAt { get; init; } = DateTime.UtcNow.Ticks;
    }

    // Windows stamps a write with a clock that moves in steps of up to about 16 ms, and a lazily
    // written file can stamp later still. A parse read within this window of the file's own stamp
    // could have missed a same-size rewrite that kept the stamp, so it is not trusted.
    private static readonly long RacyWindow = TimeSpan.FromSeconds(2).Ticks;

    private static System.Collections.Concurrent.ConcurrentDictionary<string, CachedDocument> CacheFor(OpenedProtection? key) =>
        key is null ? _documentCache
            : _keyedCache.GetValue(key, static _ => new(StringComparer.OrdinalIgnoreCase));

    internal static void RegisterDocument(string normalizedPath, long ticks, long size,
        PdfWorkingDocument working, EngineDocument document, OpenedProtection? key)
    {
        CacheFor(key)[normalizedPath] = new CachedDocument(ticks, size, new WeakReference<EngineDocument>(document),
            new WeakReference<PdfWorkingDocument>(working));
    }

    /// <summary>Drops every parse a closing tab's key decrypted.</summary>
    internal static void ForgetKey(OpenedProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _keyedCache.Remove(protection);
    }

    private static bool TryCached(System.Collections.Concurrent.ConcurrentDictionary<string, CachedDocument> cache,
        string normalized, long ticks, long size, out EngineDocument document)
    {
        document = null!;
        if (!cache.TryGetValue(normalized, out CachedDocument? entry) || entry.Ticks != ticks || entry.Size != size
            || entry.Working is null && entry.ReadAt - ticks < RacyWindow
            || !entry.Ref.TryGetTarget(out EngineDocument? cached)) return false;
        if (entry.Working is not null && entry.Working.TryGetTarget(out var working))
            working.ReleaseParsedDocument(cached);
        document = cached;
        return true;
    }

    // A same-size rewrite inside one clock tick keeps the cache key, so a caller that knows the
    // file changed drops the parsed document instead of trusting the timestamp.
    // A working document's registered parse is known current and stays shared.
    internal static void ForgetDocument(string path)
    {
        string normalized = Path.GetFullPath(path);
        if (_documentCache.TryGetValue(normalized, out var entry) && entry.Working is null)
            _documentCache.TryRemove(normalized, out _);
    }

    /// <summary>
    /// Opens a file for rendering. Files encrypted with only an owner password open with the
    /// empty user password, the same as every mainstream viewer. Repeated opens of the same
    /// file, while any earlier session on that file is still reachable, share the parsed
    /// PdfDocument so a subsequent thumbnail, preview, or export does not re-parse the file.
    /// </summary>
    internal static EngineDocument OpenDocument(string path, OpenedProtection? protection)
    {
        string normalized;
        long ticks, size;
        try
        {
            var info = new FileInfo(path);
            normalized = info.FullName;
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
            // A parse nobody could read is passed over by a caller that holds a key for it.
            if (TryCached(_documentCache, normalized, ticks, size, out EngineDocument cached)
                && (protection is null || cached.CanReadPageContent))
                return cached;
            if (protection is not null && _keyedCache.TryGetValue(protection, out var keyed)
                && TryCached(keyed, normalized, ticks, size, out cached))
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
        OpenedProtection? usedKey = null;
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
                    // The tab whose file this is opens it under its own key. Otherwise keep the
                    // strict-open document; the caller inspects CanReadPageContent.
                    if (protection is not null)
                    {
                        source.Position = 0;
                        using var bytes = new MemoryStream();
                        source.CopyTo(bytes);
                        try
                        {
                            if (protection.OpenHeld(bytes.ToArray()) is EngineDocument held)
                            {
                                document = held;
                                usedKey = protection;
                            }
                        }
                        catch (Exception held) when (held is not OutOfMemoryException)
                        {
                            // A file put in the tab's place is not under its key.
                        }
                    }
                }
            }
        }
        if (normalized is not null)
            CacheFor(usedKey)[normalized] = new CachedDocument(ticks, size, new WeakReference<EngineDocument>(document), null);
        return document;
    }

    internal static PdfPageRenderSession OpenEngineFirst(string path, double scale, OpenedProtection? protection,
        bool forPrinting = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        EngineDocument document = OpenDocument(path, protection);
        return new PdfPageRenderSession(document, SizedPages(document), 0, 0, scale, forPrinting);
    }

    internal int PageCount => _enginePages.Count;

    internal PdfRenderedPage RenderBasePage(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfRenderedPage rendered = RenderEnginePage(pageIndex, transparentBackground: false,
            includeAnnotations: false, includeFormFields: false, cancellationToken);
        rendered.RequireComplete(pageIndex);
        return rendered;
    }

    /// <summary>The base page at an exact pixel size, so an image drawn at that size lands pixel for pixel.</summary>
    internal PdfRenderedPage RenderBasePage(int pageIndex, int width, int height,
        CancellationToken cancellationToken = default)
    {
        PdfRenderedPage rendered = RenderOwnedPage(Renderer, pageIndex,
            WithWorkingCmyk(new EngineRenderOptions(width, height, false, false, false)
            {
                MaximumParallelism = RenderParallelism(width, height),
                Jpeg2000PaintParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
            }), cancellationToken);
        rendered.RequireComplete(pageIndex);
        return rendered;
    }

    // Printing and image export pass requireComplete: false and name the incomplete pages
    // afterwards through IncompletePageLog; every other caller refuses them.
    internal PdfRenderedPage RenderPage(int pageIndex, bool transparentBackground = false,
        bool includeFormFields = true, bool requireComplete = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfRenderedPage rendered = RenderEnginePage(pageIndex, transparentBackground,
            includeAnnotations: true, includeFormFields, cancellationToken);
        if (requireComplete) rendered.RequireComplete(pageIndex);
        return rendered;
    }

    /// <summary>A page for display only, at the session's fitted size with annotations and form
    /// fields. Like the viewer it keeps the pixels and their diagnostics, so a page that cannot be
    /// published still gets its thumbnail instead of a label-only tile.</summary>
    internal PdfRenderedPage RenderPreview(int pageIndex, CancellationToken cancellationToken = default,
        KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (proof?.Enabled == true)
        {
            var frame = RenderPreviewFrame(pageIndex, cancellationToken, proof);
            return new PdfRenderedPage(frame.Width, frame.Height, frame.ToSrgbBgra32(cancellationToken),
                PdfRenderBackend.Engine, null);
        }
        return RenderEnginePage(pageIndex, transparentBackground: false,
            includeAnnotations: true, includeFormFields: true, cancellationToken);
    }

    /// <summary>The same display-only page as <see cref="RenderPreview"/> as a float frame, for monitor conversion.</summary>
    internal KillerPdf.Engine.Rendering.PdfRenderedFrame RenderPreviewFrame(int pageIndex,
        CancellationToken cancellationToken = default, KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = CreateRenderOptions(pageIndex, transparentBackground: false,
            includeAnnotations: true, includeFormFields: true) with { Proof = proof };
        return Renderer.RenderFrame(pageIndex, options, cancellationToken)
            .WithGamutWarningsMarked(cancellationToken);
    }

    private PdfRenderedPage RenderEnginePage(int pageIndex, bool transparentBackground,
        bool includeAnnotations, bool includeFormFields,
        CancellationToken cancellationToken)
    {
        return RenderOwnedPage(Renderer, pageIndex,
            CreateRenderOptions(pageIndex, transparentBackground, includeAnnotations, includeFormFields),
            cancellationToken);
    }

    /// <summary>One ink plate of a page as gray coverage (#264), black for full ink. Not cached.</summary>
    internal PdfRenderedPage RenderPlate(int pageIndex, string plate,
        CancellationToken cancellationToken = default)
    {
        EngineRenderOptions options = CreateRenderOptions(pageIndex, false, true, true)
            with { SeparationPlate = plate, CacheResult = false };
        PdfRenderedPage rendered = RenderOwnedPage(Renderer, pageIndex, options, cancellationToken);
        rendered.RequireComplete(pageIndex);
        return rendered;
    }

    // Pixels remain valid until the next encoding render or session disposal. Only validation
    // runs that asked for incomplete pages pass requireComplete: false.
    internal PdfPageForEncoding RenderPageForEncoding(int pageIndex, bool requireComplete = true,
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
        if (requireComplete)
            PdfRenderCompleteness.RequireComplete(pageIndex, PdfRenderCompleteness.Classify(diagnostics));
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
            PdfRenderBackend.Engine, Diagnostics(diagnostics))
        { RenderDiagnostics = PdfRenderCompleteness.Classify(diagnostics) };
    }

    internal PdfRenderedPage RenderFittedPage(int pageIndex, int maximumWidth, int maximumHeight,
        bool includeFormFields = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);
        return RenderOwnedPage(Renderer, pageIndex,
            CreateRenderOptions(pageIndex, false, true, includeFormFields, maximumWidth, maximumHeight), cancellationToken);
    }

    internal KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFittedFrame(int pageIndex,
        int maximumWidth, int maximumHeight, CancellationToken cancellationToken,
        KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);
        // Fitted frames are for viewing only, so they carry any requested gamut warning marks.
        return Renderer.RenderFrame(pageIndex, WithWorkingCmyk(
            CreateRenderOptions(pageIndex, false, true, false, maximumWidth, maximumHeight) with { Proof = proof }), cancellationToken)
            .WithGamutWarningsMarked(cancellationToken);
    }

    /// <summary>Gets or sets where renders read the chosen working CMYK profile; null means built in.</summary>
    internal static Func<KillerPdf.Engine.ColorManagement.PdfColorProfile?> WorkingCmykSource
    {
        get => WorkingCmykProfiles.Source;
        set => WorkingCmykProfiles.Source = value;
    }

    /// <summary>Gets the working CMYK choice generation, which changes whenever the choice changes.</summary>
    internal static long WorkingCmykGeneration => WorkingCmykProfiles.Generation;

    /// <summary>Marks the working CMYK choice changed, so the next render reads it again.</summary>
    internal static void InvalidateWorkingCmyk() => WorkingCmykProfiles.Invalidate();

    private static EngineRenderOptions WithWorkingCmyk(EngineRenderOptions options) => WorkingCmykProfiles.Apply(options);

    internal (int Width, int Height) FittedDimensions(int pageIndex, int maximumWidth, int maximumHeight)
    {
        var options = CreateRenderOptions(pageIndex, false, true, false, maximumWidth, maximumHeight);
        return (options.Width, options.Height);
    }

    internal KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFittedFrameRegion(int pageIndex,
        int maximumWidth, int maximumHeight, VisibleRenderRegion region, CancellationToken cancellationToken,
        KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
        => Renderer.RenderFrameRegion(pageIndex, WithWorkingCmyk(
            CreateRenderOptions(pageIndex, false, true, false, maximumWidth, maximumHeight) with { Proof = proof }),
            region.Left, region.Top, region.Width, region.Height, cancellationToken)
            .WithGamutWarningsMarked(cancellationToken);

    internal PdfRenderedPage RenderFittedRegion(int pageIndex, int maximumWidth, int maximumHeight,
        int left, int top, int width, int height, CancellationToken cancellationToken = default)
    {
        var options = CreateRenderOptions(pageIndex, false, true, false, maximumWidth, maximumHeight);
        if (left < 0 || top < 0 || width <= 0 || height <= 0
            || (long)left + width > options.Width || (long)top + height > options.Height)
            throw new ArgumentOutOfRangeException(nameof(width));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        var diagnostics = Renderer.RenderRegionInto(pageIndex, options, left, top, width, height,
            pixels, cancellationToken);
        return new PdfRenderedPage(width, height, pixels,
            PdfRenderBackend.Engine, Diagnostics(diagnostics))
        { RenderDiagnostics = PdfRenderCompleteness.Classify(diagnostics) };
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
            ? _scale * pageInformation.UserUnit : Math.Min(_maximumWidth / pageWidth, _maximumHeight / pageHeight);
        // Preserve the previous viewer's truncation of scaled page dimensions.
        int engineWidth = Math.Max(1, checked((int)(pageWidth * renderScale)));
        int engineHeight = Math.Max(1, checked((int)(pageHeight * renderScale)));
        // A print job prints a page too large for the render limit at a lower resolution
        // instead of failing the whole job (#481).
        if (_forPrinting)
            (engineWidth, engineHeight) = PrintPagePlacement.WithinRenderLimit(engineWidth, engineHeight);
        // Every surface (viewer, thumbnails, print, export and the CLI) shows untagged CMYK
        // through the chosen working profile.
        return WithWorkingCmyk(new EngineRenderOptions(engineWidth, engineHeight, transparentBackground,
            includeAnnotations, includeFormFields)
        {
            ForPrinting = _forPrinting,
            // The viewer draws other fields as live controls but never a signed signature (#425) or
            // a push button, so those appearances have to be part of the page raster.
            IncludeSignedSignatureFields = includeAnnotations,
            IncludePushButtonFields = includeAnnotations,
            // The viewer draws marks on its overlay; print previews and jobs use engine appearances.
            IncludeRedactionMarks = _forPrinting,
            MaximumParallelism = RenderParallelism(engineWidth, engineHeight),
            Jpeg2000PaintParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
        });
    }

    // Large pages split their big row-independent paints across a few threads; output pixels
    // are identical at any thread count. Small pages stay on the calling thread except for
    // large reduced JPEG 2000 paints. The cap leaves cores free for concurrent viewer pages.
    private const long ParallelRenderPixels = 1_048_576;

    internal static int RenderParallelism(int width, int height) =>
        (long)width * height < ParallelRenderPixels ? 1
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private static EngineRenderOptions CreateExactOptions(int width, int height,
        bool transparentBackground, bool includeFormFields) =>
        new(width, height, transparentBackground, includeAnnotations: true, includeFormFields)
        {
            IncludeSignedSignatureFields = true,
            IncludePushButtonFields = true,
            IncludeRedactionMarks = false,
            MaximumParallelism = RenderParallelism(width, height),
            Jpeg2000PaintParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
        };

    internal static PdfRenderedPage? RenderExactPage(
        string path, OpenedProtection? protection, int pageIndex, int width, int height,
        bool transparentBackground = false, bool includeFormFields = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EngineDocument document = OpenDocument(path, protection);
            var renderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance,
                SharedCacheFor(document), WicJpegDecoder.Instance);
            PdfRenderedPage rendered = RenderOwnedPage(renderer, pageIndex,
                CreateExactOptions(width, height, transparentBackground, includeFormFields), cancellationToken);
            rendered.RequireComplete(pageIndex);
            return rendered;
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
    private OpenedProtection? _protection;
    private long _revision;

    internal PdfRenderedPage Render(string path, OpenedProtection? protection, long revision, int pageIndex, int maximumSize)
    {
        if (_session is null || _path != path || _revision != revision || !ReferenceEquals(_protection, protection))
        {
            Clear();
            _session = PdfPageRenderSession.OpenEngineFirst(path, maximumSize, maximumSize, protection);
            _path = path;
            _protection = protection;
            _revision = revision;
        }
        return _session.RenderFittedPage(pageIndex, maximumSize, maximumSize);
    }

    internal void Clear()
    {
        _session?.Dispose();
        _session = null;
        if (_path is not null) PdfPageRenderSession.ForgetDocument(_path);
        _path = null;
        _protection = null;
    }
}

// A task exclusively owns its lease. Only one idle session is retained per pane.
internal sealed class PdfBackgroundRenderCache
{
    private readonly Lock _sync = new();
    private Request? _current;
    private PdfPageRenderSession? _idle;

    internal Request Capture(string path, long revision, OpenedProtection? protection)
    {
        lock (_sync)
        {
            if (_current is not null && _current.Path == path && _current.Revision == revision
                && ReferenceEquals(_current.Protection, protection))
                return _current;
            _idle?.Dispose();
            _idle = null;
            return _current = new Request(this, path, revision, protection);
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

    internal sealed class Request(PdfBackgroundRenderCache owner, string path, long revision, OpenedProtection? protection)
        : IVisibleRegionSource
    {
        internal string Path { get; } = path;
        internal long Revision { get; } = revision;
        internal OpenedProtection? Protection { get; } = protection;

        IVisibleRegionLease IVisibleRegionSource.RentRegion(CancellationToken cancellationToken) => Rent(cancellationToken);

        // A byte region render reserves its eight-bit and float buffers, a float one its conversion as well.
        long IVisibleRegionSource.RegionReservation(int pageIndex, int maximum, bool proof, bool monitor)
            => Math.Min((proof ? 64L : monitor ? 40L : 8L) * maximum * maximum, 512L << 20);

        // A PDF region is drawn directly; nothing larger than the region is decoded first.
        long IVisibleRegionSource.RegionWorkingBytes(int pageIndex) => 0;

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
            session ??= PdfPageRenderSession.OpenEngineFirst(Path, 1, 1, Protection);
            return new Lease(owner, this, session);
        }
    }

    internal sealed class Lease(PdfBackgroundRenderCache owner, Request request,
        PdfPageRenderSession session) : IVisibleRegionLease
    {
        private PdfPageRenderSession? _session = session;

        (int Width, int Height) IVisibleRegionLease.Dimensions(int pageIndex, int maximumWidth, int maximumHeight)
            => Dimensions(pageIndex, maximumWidth, maximumHeight);

        KillerPdf.Engine.Rendering.PdfRenderedFrame IVisibleRegionLease.RenderFrameRegion(int pageIndex, int maximumWidth,
            int maximumHeight, VisibleRenderRegion region, CancellationToken cancellationToken,
            KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof)
            => RenderFrameRegion(pageIndex, maximumWidth, maximumHeight, region, cancellationToken, proof);

        PdfRenderedPage IVisibleRegionLease.RenderRegion(int pageIndex, int maximumWidth, int maximumHeight,
            int left, int top, int width, int height, CancellationToken cancellationToken)
            => RenderRegion(pageIndex, maximumWidth, maximumHeight, left, top, width, height, cancellationToken);

        internal PdfRenderedPage Render(int pageIndex, int maximumWidth, int maximumHeight,
            CancellationToken cancellationToken = default) =>
            (_session ?? throw new ObjectDisposedException(nameof(Lease))).RenderFittedPage(
                pageIndex, maximumWidth, maximumHeight, cancellationToken: cancellationToken);

        internal KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFrame(int pageIndex,
            int maximumWidth, int maximumHeight, CancellationToken cancellationToken,
            KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null) =>
            (_session ?? throw new ObjectDisposedException(nameof(Lease))).RenderFittedFrame(
                pageIndex, maximumWidth, maximumHeight, cancellationToken, proof);

        internal KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFrameRegion(int pageIndex,
            int maximumWidth, int maximumHeight, VisibleRenderRegion region, CancellationToken cancellationToken,
            KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null) =>
            (_session ?? throw new ObjectDisposedException(nameof(Lease))).RenderFittedFrameRegion(
                pageIndex, maximumWidth, maximumHeight, region, cancellationToken, proof);

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

        internal (int Width, int Height) Dimensions(int pageIndex, int maximumWidth, int maximumHeight) =>
            (_session ?? throw new ObjectDisposedException(nameof(Lease))).FittedDimensions(pageIndex, maximumWidth, maximumHeight);

        internal PdfRenderedPage RenderRegion(int pageIndex, int maximumWidth, int maximumHeight,
            int left, int top, int width, int height, CancellationToken cancellationToken = default) =>
            (_session ?? throw new ObjectDisposedException(nameof(Lease))).RenderFittedRegion(
                pageIndex, maximumWidth, maximumHeight, left, top, width, height, cancellationToken);
    }
}

internal readonly record struct PdfPageForEncoding(
    int Width, int Height, ReadOnlyMemory<byte> Pixels, IReadOnlyList<string> Diagnostics);

internal sealed class PdfCapturedReadRevision : IDisposable, IVisibleRegionSource
{
    IVisibleRegionLease IVisibleRegionSource.RentRegion(CancellationToken cancellationToken) => Rent(cancellationToken);

    // A book region renders through float frames, like its base pages, and a DjVu page decodes whole first.
    long IVisibleRegionSource.RegionReservation(int pageIndex, int maximum, bool proof, bool monitor)
        => Math.Min((proof ? 64L : 40L) * maximum * maximum + ((IVisibleRegionSource)this).RegionWorkingBytes(pageIndex),
            512L << 20);

    long IVisibleRegionSource.RegionWorkingBytes(int pageIndex)
    {
        if (_nativePages is not { Format: EngineDocumentFormat.Djvu } snapshot
            || pageIndex < 0 || pageIndex >= snapshot.PageCount) return 0;
        var geometry = snapshot.GetPageGeometry(pageIndex);
        return (long)Math.Ceiling(geometry.Width) * (long)Math.Ceiling(geometry.Height) * 4 * sizeof(float);
    }

    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly IReadOnlyList<EnginePageInformation> _pages;
    private EngineDocument? _document;
    private EngineReadSnapshot? _native;
    private IRenderSession? _idle;
    private int _active;
    private bool _closed;
    private bool _cancellationIssued;
    private bool _cancellationDisposed;

    internal PdfCapturedReadRevision(EngineDocument document, string contentIdentity)
    {
        if (!document.CanReadPageContent)
            throw new InvalidOperationException("Authenticate the document before capturing page content.");
        _document = document;
        _pages = EnginePageInformation.Read(document);
        ContentIdentity = contentIdentity;
        Format = EngineDocumentFormat.Pdf;
    }

    internal Guid Id { get; } = Guid.NewGuid();
    internal EngineDocumentFormat Format { get; }

    /// <param name="textNotices">Told the notices of each DjVu page text a lease reads.</param>
    internal PdfCapturedReadRevision(EngineReadSnapshot snapshot,
        Action<IReadOnlyList<KillerPdf.Engine.Formats.Djvu.DjvuDiagnostic>>? textNotices = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _native = snapshot;
        _nativePages = snapshot;
        _textNotices = textNotices;
        _pages = [];
        GrowNativePages();
        ContentIdentity = snapshot.SourceSha256;
        Format = snapshot.Format;
    }

    private readonly Action<IReadOnlyList<KillerPdf.Engine.Formats.Djvu.DjvuDiagnostic>>? _textNotices;

    private void ReportTextNotices(KillerPdf.Engine.Documents.PdfReadTextPage page)
    {
        if (page.Diagnostics.Count > 0) _textNotices?.Invoke(page.Diagnostics);
    }

    // A native book still being paginated adds pages at its end; the revision grows with it, so everything keyed to
    // this revision stays current. The pages it already has never change.
    private readonly EngineReadSnapshot? _nativePages;
    private readonly List<EnginePageInformation> _grownPages = [];

    private void GrowNativePages()
    {
        if (_nativePages is not { } snapshot) return;
        lock (_sync)
            for (int index = _grownPages.Count, count = snapshot.PageCount; index < count; index++)
            {
                var geometry = snapshot.GetPageGeometry(index);
                _grownPages.Add(new EnginePageInformation
                {
                    Left = 0, Bottom = 0, Width = geometry.WidthPoints, Height = geometry.HeightPoints,
                    UserUnit = 1, Rotation = geometry.IntrinsicRotation
                });
            }
    }

    internal string ContentIdentity { get; }
    internal int PageCount
    {
        get
        {
            if (_nativePages is null) return _pages.Count;
            GrowNativePages();
            lock (_sync) return _grownPages.Count;
        }
    }

    /// <summary>True while the native book this revision reads is still adding pages.</summary>
    internal bool IsPaginating => _nativePages?.IsPaginating ?? false;

    /// <summary>How many of the book's spine items are paginated so far, and how many it has.</summary>
    internal (int Paginated, int Total) PaginationProgress => _nativePages?.PaginationProgress ?? (0, 0);

    /// <summary>
    /// Blocks until every page is known, for work that needs the whole book. A book whose pagination failed or was
    /// stopped by closing it keeps the pages it has, and those are what the caller then reads.
    /// </summary>
    internal void WaitForPagination(CancellationToken cancellationToken)
    {
        if (_nativePages is not { IsPaginating: true } snapshot) return;
        try { snapshot.Pagination.Wait(cancellationToken); }
        catch (AggregateException) { }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal bool IsClosed
    {
        get
        {
            lock (_sync) return _closed;
        }
    }

    internal EnginePageInformation GetPageGeometry(int pageIndex)
    {
        if (_nativePages is not null)
        {
            if (pageIndex < 0 || pageIndex >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            lock (_sync) return _grownPages[pageIndex];
        }
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return _pages[pageIndex];
    }

    internal Lease Rent(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EngineDocument? document;
        EngineReadSnapshot? native;
        IRenderSession? session;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            cancellationToken.ThrowIfCancellationRequested();
            document = _document;
            native = _native;
            _active++;
            session = _idle;
            _idle = null;
        }
        CancellationTokenSource? linked = null;
        try
        {
            // The reservation keeps the owner's token source alive during session creation.
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
            linked.Token.ThrowIfCancellationRequested();
            session ??= native is not null ? new OwnedRenderSession(native)
                : new OwnedRenderSession(PdfPageRenderSession.OpenCapturedDocument(document!, _pages));
            linked.Token.ThrowIfCancellationRequested();
            return new Lease(this, session, linked, native);
        }
        catch
        {
            linked?.Dispose();
            Return(session);
            throw;
        }
    }

    private void Return(IRenderSession? session)
    {
        bool disposeCancellation;
        lock (_sync)
        {
            _active--;
            if (!_closed && _idle is null)
            {
                _idle = session;
                session = null;
            }
            disposeCancellation = ReleaseCancellation();
        }
        session?.Dispose();
        if (disposeCancellation) _closing.Dispose();
    }

    // Called under _sync only. Cancel completes before the last reservation may dispose it.
    private bool ReleaseCancellation()
    {
        if (!_closed || !_cancellationIssued || _active != 0 || _cancellationDisposed) return false;
        _cancellationDisposed = true;
        return true;
    }

    public void Dispose()
    {
        IRenderSession? idle;
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            _document = null;
            _native = null;
            idle = _idle;
            _idle = null;
        }
        try
        {
            _closing.Cancel();
        }
        finally
        {
            idle?.Dispose();
            bool disposeCancellation;
            lock (_sync)
            {
                _cancellationIssued = true;
                disposeCancellation = ReleaseCancellation();
            }
            if (disposeCancellation) _closing.Dispose();
        }
    }

    // A task owns its lease exclusively, including rendering and disposal.
    internal interface IRenderSession : IDisposable
    {
        KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFrame(int pageIndex, EngineRenderOptions options,
            CancellationToken cancellationToken);
    }

    private sealed class OwnedRenderSession : IRenderSession
    {
        private PdfPageRenderSession? _pdf;
        private EngineReadSnapshot? _native;

        internal OwnedRenderSession(PdfPageRenderSession pdf) => _pdf = pdf;
        internal OwnedRenderSession(EngineReadSnapshot native) => _native = native;

        public KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFrame(int pageIndex, EngineRenderOptions options,
            CancellationToken cancellationToken) => _pdf is not null
                ? _pdf.RenderFrame(pageIndex, options, cancellationToken)
                : (_native ?? throw new ObjectDisposedException(nameof(OwnedRenderSession)))
                    .RenderPage(pageIndex, options, cancellationToken);

        public void Dispose()
        {
            _pdf?.Dispose();
            _pdf = null;
            _native = null;
        }
    }

    internal sealed class Lease(PdfCapturedReadRevision owner, IRenderSession session,
        CancellationTokenSource cancellation, EngineReadSnapshot? nativeSnapshot = null) : IVisibleRegionLease
    {
        private IRenderSession? _session = session;
        private EngineReadSnapshot? _nativeSnapshot = nativeSnapshot;

        // A book page is fitted as its base pages are, so a tile lines up with the page under it.
        public (int Width, int Height) Dimensions(int pageIndex, int maximumWidth, int maximumHeight)
            => NativeFrameRendering.FittedSize(owner.GetPageGeometry(pageIndex), maximumWidth, maximumHeight);

        public KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFrameRegion(int pageIndex, int maximumWidth,
            int maximumHeight, VisibleRenderRegion region, CancellationToken cancellationToken,
            KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
        {
            _ = _session ?? throw new ObjectDisposedException(nameof(Lease));
            EngineReadSnapshot snapshot = _nativeSnapshot
                ?? throw new NotSupportedException("Region renders are available only for captured native documents.");
            cancellationToken.ThrowIfCancellationRequested();
            cancellation.Token.ThrowIfCancellationRequested();
            var (width, height) = Dimensions(pageIndex, maximumWidth, maximumHeight);
            // The whole page is only the region's coordinate frame; the engine stores and limits the region alone.
            var options = new EngineRenderOptions(width, height,
                maximumPixelBytes: EngineRenderOptions.MaximumPixelBytes) { Proof = proof };
            var frame = snapshot.RenderPageRegion(pageIndex, options, region.Left, region.Top, region.Width, region.Height,
                cancellation.Token).WithGamutWarningsMarked(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return frame;
        }

        public PdfRenderedPage RenderRegion(int pageIndex, int maximumWidth, int maximumHeight,
            int left, int top, int width, int height, CancellationToken cancellationToken = default)
        {
            var (fullWidth, fullHeight) = Dimensions(pageIndex, maximumWidth, maximumHeight);
            var frame = RenderFrameRegion(pageIndex, maximumWidth, maximumHeight,
                new VisibleRenderRegion(left, top, width, height, left, top, fullWidth, fullHeight), cancellationToken);
            return new PdfRenderedPage(frame.Width, frame.Height, frame.ToSrgbBgra32(cancellation.Token),
                PdfRenderBackend.Engine, null);
        }

        internal KillerPdf.Engine.Documents.PdfReadTextPage ReadNativeTextPage(int pageIndex)
        {
            _ = _session ?? throw new ObjectDisposedException(nameof(Lease));
            cancellation.Token.ThrowIfCancellationRequested();
            EngineReadSnapshot snapshot = _nativeSnapshot
                ?? throw new NotSupportedException("Native text is available only for captured native documents.");
            var page = snapshot.ReadTextPage(pageIndex, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            owner.ReportTextNotices(page);
            return page;
        }

        // The page's text alone, which a native EPUB reads from its page index without laying the chapter out again.
        // A DjVu page's text is its text page's, read whole so its notices are reported too.
        internal string ReadNativeText(int pageIndex)
        {
            _ = _session ?? throw new ObjectDisposedException(nameof(Lease));
            cancellation.Token.ThrowIfCancellationRequested();
            EngineReadSnapshot snapshot = _nativeSnapshot
                ?? throw new NotSupportedException("Native text is available only for captured native documents.");
            if (snapshot.Format == EngineDocumentFormat.Djvu) return ReadNativeTextPage(pageIndex).Text;
            string text = snapshot.ReadPageText(pageIndex, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return text;
        }

        internal KillerPdf.Engine.Rendering.PdfRenderedFrame RenderFrame(int pageIndex, EngineRenderOptions options)
        {
            IRenderSession active = _session ?? throw new ObjectDisposedException(nameof(Lease));
            cancellation.Token.ThrowIfCancellationRequested();
            var frame = active.RenderFrame(pageIndex, options, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return frame;
        }

        public void Dispose()
        {
            IRenderSession? released = Interlocked.Exchange(ref _session, null);
            if (released is null) return;
            _nativeSnapshot = null;
            cancellation.Dispose();
            owner.Return(released);
        }
    }
}

internal readonly record struct PdfRenderedPage(
    int Width, int Height, byte[] Pixels, PdfRenderBackend Backend, string? EngineFailure)
{
    internal IReadOnlyList<PdfRenderDiagnostic> RenderDiagnostics { get; init; } = [];
    internal string? DisplayFallbackOperation { get; init; }
    internal void RequireComplete(int pageIndex) => PdfRenderCompleteness.RequireComplete(pageIndex, RenderDiagnostics);
}
