using Docnet.Core;
using Docnet.Core.Models;
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
    private readonly string _path;
    private Docnet.Core.Readers.IDocReader? _reader;
    private readonly EngineRenderer? _engineRenderer;
    private readonly IReadOnlyList<EnginePageInformation>? _enginePages;
    private readonly int _maximumWidth;
    private readonly int _maximumHeight;
    private readonly double _scale;
    private readonly bool _allowNativeFallback;
    private readonly HashSet<int> _nativeFallbackPages = [];

    private PdfPageRenderSession(string path, Docnet.Core.Readers.IDocReader reader)
    {
        _path = path;
        _reader = reader;
    }

    private PdfPageRenderSession(string path, EngineDocument document,
        IReadOnlyList<EnginePageInformation> pages, int maximumWidth, int maximumHeight,
        double scale, bool allowNativeFallback)
    {
        _path = path;
        _engineRenderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance);
        _enginePages = pages;
        _maximumWidth = maximumWidth;
        _maximumHeight = maximumHeight;
        _scale = scale;
        _allowNativeFallback = allowNativeFallback;
    }

    internal static PdfPageRenderSession Open(string path, int maximumWidth, int maximumHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        if (maximumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        return new PdfPageRenderSession(path, DocLib.Instance.GetDocReader(
            path, new PageDimensions(maximumWidth, maximumHeight)));
    }

    internal static PdfPageRenderSession Open(string path, double scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        return new PdfPageRenderSession(path, DocLib.Instance.GetDocReader(
            path, new PageDimensions(scale)));
    }

    internal static PdfPageRenderSession OpenEngineFirst(
        string path, int maximumWidth, int maximumHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        if (maximumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        try
        {
            EngineDocument document = EngineDocument.OpenWithCompatibilityRecovery(
                File.ReadAllBytes(path));
            IReadOnlyList<EnginePageInformation> pages = EnginePageInformation.Read(document);
            return new PdfPageRenderSession(path, document, pages,
                maximumWidth, maximumHeight, 0, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Open(path, maximumWidth, maximumHeight);
        }
    }

    internal static PdfPageRenderSession OpenEngineFirst(string path, double scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        try
        {
            EngineDocument document = EngineDocument.OpenWithCompatibilityRecovery(
                File.ReadAllBytes(path));
            IReadOnlyList<EnginePageInformation> pages = EnginePageInformation.Read(document);
            return new PdfPageRenderSession(path, document, pages, 0, 0, scale, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Open(path, scale);
        }
    }

    internal int PageCount => _enginePages?.Count ?? _reader!.GetPageCount();

    internal PdfRenderedPage RenderBasePage(int pageIndex)
    {
        if (_engineRenderer is not null && _enginePages is not null
            && !_nativeFallbackPages.Contains(pageIndex))
        {
            try
            {
                return RenderEnginePage(pageIndex, transparentBackground: false,
                    includeAnnotations: false, includeFormFields: false);
            }
            catch (Exception exception) when (_allowNativeFallback
                && exception is not OutOfMemoryException)
            {
                _nativeFallbackPages.Add(pageIndex);
            }
        }

        using var page = NativeReader().GetPageReader(pageIndex);
        return new PdfRenderedPage(page.GetPageWidth(), page.GetPageHeight(), page.GetImage());
    }

    internal PdfRenderedPage RenderPage(int pageIndex, bool transparentBackground = false,
        bool includeFormFields = true, bool removeTransparencyOnFallback = false)
    {
        if (_engineRenderer is not null && _enginePages is not null
            && !_nativeFallbackPages.Contains(pageIndex))
        {
            try
            {
                return RenderEnginePage(pageIndex, transparentBackground,
                    includeAnnotations: true, includeFormFields);
            }
            catch (Exception exception) when (_allowNativeFallback
                && exception is not OutOfMemoryException)
            {
                _nativeFallbackPages.Add(pageIndex);
            }
        }

        using var page = NativeReader().GetPageReader(pageIndex);
        int width = page.GetPageWidth();
        int height = page.GetPageHeight();
        byte[] pixels = PdfiumInterop.RenderPageWithAnnotations(
            _path, pageIndex, width, height, transparentBackground, includeFormFields)
            ?? (removeTransparencyOnFallback
                ? page.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover())
                : page.GetImage());
        return new PdfRenderedPage(width, height, pixels);
    }

    private PdfRenderedPage RenderEnginePage(int pageIndex, bool transparentBackground,
        bool includeAnnotations, bool includeFormFields)
    {
        if (pageIndex < 0 || pageIndex >= _enginePages!.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        EnginePageInformation pageInformation = _enginePages[pageIndex];
        bool quarterTurn = pageInformation.Rotation is 90 or 270;
        double pageWidth = quarterTurn ? pageInformation.Height : pageInformation.Width;
        double pageHeight = quarterTurn ? pageInformation.Width : pageInformation.Height;
        double renderScale = _scale > 0
            ? _scale : Math.Min(_maximumWidth / pageWidth, _maximumHeight / pageHeight);
        int engineWidth = Math.Max(1, (int)Math.Round(pageWidth * renderScale));
        int engineHeight = Math.Max(1, (int)Math.Round(pageHeight * renderScale));
        KillerPdf.Engine.Rendering.PdfRenderedPage rendered = _engineRenderer!.Render(
            pageIndex, new EngineRenderOptions(engineWidth, engineHeight, transparentBackground,
                includeAnnotations, includeFormFields));
        if (rendered.Diagnostics.Count > 0)
            throw new NotSupportedException(string.Join(" ", rendered.Diagnostics));
        return new PdfRenderedPage(engineWidth, engineHeight, rendered.Pixels.ToArray());
    }

    private Docnet.Core.Readers.IDocReader NativeReader()
    {
        if (_reader is not null) return _reader;
        _reader = _scale > 0
            ? DocLib.Instance.GetDocReader(_path, new PageDimensions(_scale))
            : DocLib.Instance.GetDocReader(
                _path, new PageDimensions(_maximumWidth, _maximumHeight));
        return _reader;
    }

    internal static byte[]? RenderExactPage(string path, int pageIndex, int width, int height,
        bool transparentBackground = false, bool includeFormFields = true)
    {
        try
        {
            EngineDocument document = EngineDocument.OpenWithCompatibilityRecovery(
                File.ReadAllBytes(path));
            var renderer = new EngineRenderer(document, InstalledPdfFontResolver.Instance);
            KillerPdf.Engine.Rendering.PdfRenderedPage rendered = renderer.Render(
                pageIndex, new EngineRenderOptions(width, height, transparentBackground,
                    includeAnnotations: true, includeFormFields));
            if (rendered.Diagnostics.Count == 0) return rendered.Pixels.ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        return PdfiumInterop.RenderPageWithAnnotations(
            path, pageIndex, width, height, transparentBackground, includeFormFields);
    }

    public void Dispose() => _reader?.Dispose();
}

internal readonly record struct PdfRenderedPage(int Width, int Height, byte[] Pixels);
