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
    private readonly Docnet.Core.Readers.IDocReader? _reader;
    private readonly EngineRenderer? _engineRenderer;
    private readonly IReadOnlyList<EnginePageInformation>? _enginePages;
    private readonly int _maximumWidth;
    private readonly int _maximumHeight;

    private PdfPageRenderSession(string path, Docnet.Core.Readers.IDocReader reader)
    {
        _path = path;
        _reader = reader;
    }

    private PdfPageRenderSession(string path, EngineDocument document,
        IReadOnlyList<EnginePageInformation> pages, int maximumWidth, int maximumHeight)
    {
        _path = path;
        _engineRenderer = new EngineRenderer(document);
        _enginePages = pages;
        _maximumWidth = maximumWidth;
        _maximumHeight = maximumHeight;
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

    internal static PdfPageRenderSession OpenEngine(
        string path, int maximumWidth, int maximumHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        if (maximumHeight <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHeight));
        EngineDocument document = EngineDocument.Open(File.ReadAllBytes(path));
        IReadOnlyList<EnginePageInformation> pages = EnginePageInformation.Read(document);
        return new PdfPageRenderSession(path, document, pages, maximumWidth, maximumHeight);
    }

    internal int PageCount => _enginePages?.Count ?? _reader!.GetPageCount();

    internal PdfRenderedPage RenderBasePage(int pageIndex)
    {
        using var page = _reader!.GetPageReader(pageIndex);
        return new PdfRenderedPage(page.GetPageWidth(), page.GetPageHeight(), page.GetImage());
    }

    internal PdfRenderedPage RenderPage(int pageIndex, bool transparentBackground = false,
        bool includeFormFields = true, bool removeTransparencyOnFallback = false)
    {
        if (_engineRenderer is not null && _enginePages is not null)
        {
            if (pageIndex < 0 || pageIndex >= _enginePages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            EnginePageInformation pageInformation = _enginePages[pageIndex];
            bool quarterTurn = pageInformation.Rotation is 90 or 270;
            double pageWidth = quarterTurn ? pageInformation.Height : pageInformation.Width;
            double pageHeight = quarterTurn ? pageInformation.Width : pageInformation.Height;
            double scale = Math.Min(_maximumWidth / pageWidth, _maximumHeight / pageHeight);
            int engineWidth = Math.Max(1, (int)Math.Round(pageWidth * scale));
            int engineHeight = Math.Max(1, (int)Math.Round(pageHeight * scale));
            KillerPdf.Engine.Rendering.PdfRenderedPage rendered = _engineRenderer.Render(
                pageIndex, new EngineRenderOptions(engineWidth, engineHeight, transparentBackground,
                    includeAnnotations: true, includeFormFields));
            if (rendered.Diagnostics.Count > 0)
                throw new NotSupportedException(string.Join(" ", rendered.Diagnostics));
            return new PdfRenderedPage(engineWidth, engineHeight, rendered.Pixels.ToArray());
        }

        using var page = _reader!.GetPageReader(pageIndex);
        int width = page.GetPageWidth();
        int height = page.GetPageHeight();
        byte[] pixels = PdfiumInterop.RenderPageWithAnnotations(
            _path, pageIndex, width, height, transparentBackground, includeFormFields)
            ?? (removeTransparencyOnFallback
                ? page.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover())
                : page.GetImage());
        return new PdfRenderedPage(width, height, pixels);
    }

    internal static byte[]? RenderExactPage(string path, int pageIndex, int width, int height,
        bool transparentBackground = false, bool includeFormFields = true) =>
        PdfiumInterop.RenderPageWithAnnotations(
            path, pageIndex, width, height, transparentBackground, includeFormFields);

    public void Dispose() => _reader?.Dispose();
}

internal readonly record struct PdfRenderedPage(int Width, int Height, byte[] Pixels);
