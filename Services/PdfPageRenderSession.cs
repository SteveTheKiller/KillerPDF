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
    private readonly EngineRenderer _engineRenderer;
    private readonly IReadOnlyList<EnginePageInformation> _enginePages;
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
        EngineDocument document = EngineDocument.OpenWithCompatibilityRecovery(
            File.ReadAllBytes(path));
        IReadOnlyList<EnginePageInformation> pages = EnginePageInformation.Read(document);
        return new PdfPageRenderSession(document, pages, maximumWidth, maximumHeight, 0);
    }

    internal static PdfPageRenderSession OpenEngineFirst(string path, double scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        EngineDocument document = EngineDocument.OpenWithCompatibilityRecovery(
            File.ReadAllBytes(path));
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
        if (pageIndex < 0 || pageIndex >= _enginePages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        EnginePageInformation pageInformation = _enginePages[pageIndex];
        bool quarterTurn = pageInformation.Rotation is 90 or 270;
        double pageWidth = quarterTurn ? pageInformation.Height : pageInformation.Width;
        double pageHeight = quarterTurn ? pageInformation.Width : pageInformation.Height;
        double renderScale = _scale > 0
            ? _scale : Math.Min(_maximumWidth / pageWidth, _maximumHeight / pageHeight);
        int engineWidth = Math.Max(1, (int)Math.Round(pageWidth * renderScale));
        int engineHeight = Math.Max(1, (int)Math.Round(pageHeight * renderScale));
        KillerPdf.Engine.Rendering.PdfRenderedPage rendered = _engineRenderer.Render(
            pageIndex, new EngineRenderOptions(engineWidth, engineHeight, transparentBackground,
                includeAnnotations, includeFormFields), cancellationToken);
        return new PdfRenderedPage(engineWidth, engineHeight, rendered.Pixels.ToArray(),
            PdfRenderBackend.Engine, Diagnostics(rendered.Diagnostics));
    }

    internal static PdfRenderedPage? RenderExactPage(
        string path, int pageIndex, int width, int height,
        bool transparentBackground = false, bool includeFormFields = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EngineDocument document = EngineDocument.OpenWithCompatibilityRecovery(
                File.ReadAllBytes(path));
            var renderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance);
            KillerPdf.Engine.Rendering.PdfRenderedPage rendered = renderer.Render(
                pageIndex, new EngineRenderOptions(width, height, transparentBackground,
                    includeAnnotations: true, includeFormFields), cancellationToken);
            return new PdfRenderedPage(width, height, rendered.Pixels.ToArray(),
                PdfRenderBackend.Engine, Diagnostics(rendered.Diagnostics));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && exception is not OperationCanceledException)
        {
            return null;
        }
    }

    public void Dispose() { }

    private static string? Diagnostics(IReadOnlyList<string> diagnostics) =>
        diagnostics.Count == 0 ? null : string.Join(" ", diagnostics);
}

internal enum PdfRenderBackend { Engine }

internal readonly record struct PdfRenderedPage(
    int Width, int Height, byte[] Pixels, PdfRenderBackend Backend, string? EngineFailure);
