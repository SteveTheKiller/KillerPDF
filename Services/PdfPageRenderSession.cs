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
    private readonly DocnetRenderFallback _nativeFallback;
    private readonly EngineRenderer? _engineRenderer;
    private readonly IReadOnlyList<EnginePageInformation>? _enginePages;
    private readonly int _maximumWidth;
    private readonly int _maximumHeight;
    private readonly double _scale;
    private readonly bool _allowNativeFallback;
    private readonly HashSet<EngineRenderProfile> _nativeFallbackProfiles = [];

    private PdfPageRenderSession(DocnetRenderFallback nativeFallback)
    {
        _nativeFallback = nativeFallback;
    }

    private PdfPageRenderSession(string path, EngineDocument document,
        IReadOnlyList<EnginePageInformation> pages, int maximumWidth, int maximumHeight,
        double scale, bool allowNativeFallback)
    {
        _nativeFallback = scale > 0
            ? new DocnetRenderFallback(path, scale)
            : new DocnetRenderFallback(path, maximumWidth, maximumHeight);
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
        return new PdfPageRenderSession(
            new DocnetRenderFallback(path, maximumWidth, maximumHeight));
    }

    internal static PdfPageRenderSession Open(string path, double scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        return new PdfPageRenderSession(new DocnetRenderFallback(path, scale));
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

    internal int PageCount => _enginePages?.Count ?? _nativeFallback.PageCount;

    internal PdfRenderedPage RenderBasePage(int pageIndex)
    {
        var profile = new EngineRenderProfile(pageIndex,
            IncludeAnnotations: false, IncludeFormFields: false);
        if (_engineRenderer is not null && _enginePages is not null
            && !_nativeFallbackProfiles.Contains(profile))
        {
            try
            {
                return RenderEnginePage(pageIndex, transparentBackground: false,
                    includeAnnotations: false, includeFormFields: false);
            }
            catch (Exception exception) when (_allowNativeFallback
                && exception is not OutOfMemoryException)
            {
                _nativeFallbackProfiles.Add(profile);
            }
        }

        return _nativeFallback.RenderBasePage(pageIndex);
    }

    internal PdfRenderedPage RenderPage(int pageIndex, bool transparentBackground = false,
        bool includeFormFields = true, bool removeTransparencyOnFallback = false)
    {
        var profile = new EngineRenderProfile(pageIndex,
            IncludeAnnotations: true, IncludeFormFields: includeFormFields);
        if (_engineRenderer is not null && _enginePages is not null
            && !_nativeFallbackProfiles.Contains(profile))
        {
            try
            {
                return RenderEnginePage(pageIndex, transparentBackground,
                    includeAnnotations: true, includeFormFields);
            }
            catch (Exception exception) when (_allowNativeFallback
                && exception is not OutOfMemoryException)
            {
                _nativeFallbackProfiles.Add(profile);
            }
        }

        return _nativeFallback.RenderPage(pageIndex, transparentBackground,
            includeFormFields, removeTransparencyOnFallback);
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

        return DocnetRenderFallback.RenderExactPage(
            path, pageIndex, width, height, transparentBackground, includeFormFields);
    }

    public void Dispose() => _nativeFallback.Dispose();

    private readonly record struct EngineRenderProfile(
        int PageIndex, bool IncludeAnnotations, bool IncludeFormFields);
}

internal readonly record struct PdfRenderedPage(int Width, int Height, byte[] Pixels);
