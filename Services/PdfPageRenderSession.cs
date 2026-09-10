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
    private PdfPageRenderSession(EngineDocument document,
        IReadOnlyList<EnginePageInformation> pages, int maximumWidth, int maximumHeight,
        double scale)
    {
        _engineRenderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance);
        _enginePages = pages;
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

    /// <summary>
    /// Opens a file for rendering. Files encrypted with only an owner password open with the
    /// empty user password, the same as every mainstream viewer.
    /// </summary>
    internal static EngineDocument OpenDocument(string path)
    {
        using FileStream source = File.OpenRead(path);
        EngineDocument document = EngineDocument.OpenWithCompatibilityRecovery(source);
        if (document.CanReadPageContent) return document;
        try
        {
            source.Position = 0;
            return EngineDocument.OpenWithCompatibilityRecovery(source, string.Empty);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return document;
        }
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
        bool includeFormFields = false)
    {
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        if (maximumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        return RenderOwnedPage(Renderer, pageIndex,
            CreateRenderOptions(pageIndex, false, true, includeFormFields, maximumWidth, maximumHeight), default);
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
            var renderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance);
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

internal readonly record struct PdfPageForEncoding(
    int Width, int Height, ReadOnlyMemory<byte> Pixels, IReadOnlyList<string> Diagnostics);

internal readonly record struct PdfRenderedPage(
    int Width, int Height, byte[] Pixels, PdfRenderBackend Backend, string? EngineFailure);
