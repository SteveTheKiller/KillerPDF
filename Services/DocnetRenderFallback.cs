using Docnet.Core;
using Docnet.Core.Models;

namespace KillerPDF.Services;

internal sealed class DocnetRenderFallback : IDisposable
{
    private readonly string _path;
    private readonly int _maximumWidth;
    private readonly int _maximumHeight;
    private readonly double _scale;
    private Docnet.Core.Readers.IDocReader? _reader;

    internal DocnetRenderFallback(string path, int maximumWidth, int maximumHeight)
    {
        _path = path;
        _maximumWidth = maximumWidth;
        _maximumHeight = maximumHeight;
    }

    internal DocnetRenderFallback(string path, double scale)
    {
        _path = path;
        _scale = scale;
    }

    internal int PageCount => Reader().GetPageCount();

    internal PdfRenderedPage RenderBasePage(int pageIndex)
    {
        using var page = Reader().GetPageReader(pageIndex);
        return new PdfRenderedPage(page.GetPageWidth(), page.GetPageHeight(), page.GetImage(),
            PdfRenderBackend.NativeFallback, null);
    }

    internal PdfRenderedPage RenderPage(int pageIndex, bool transparentBackground,
        bool includeFormFields, bool removeTransparency)
    {
        using var page = Reader().GetPageReader(pageIndex);
        int width = page.GetPageWidth();
        int height = page.GetPageHeight();
        byte[] pixels = PdfiumInterop.RenderPageWithAnnotations(
            _path, pageIndex, width, height, transparentBackground, includeFormFields)
            ?? (removeTransparency
                ? page.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover())
                : page.GetImage());
        return new PdfRenderedPage(width, height, pixels,
            PdfRenderBackend.NativeFallback, null);
    }

    internal static byte[]? RenderExactPage(string path, int pageIndex,
        int width, int height, bool transparentBackground, bool includeFormFields) =>
        PdfiumInterop.RenderPageWithAnnotations(
            path, pageIndex, width, height, transparentBackground, includeFormFields);

    private Docnet.Core.Readers.IDocReader Reader()
    {
        if (_reader is not null) return _reader;
        _reader = _scale > 0
            ? DocLib.Instance.GetDocReader(_path, new PageDimensions(_scale))
            : DocLib.Instance.GetDocReader(
                _path, new PageDimensions(_maximumWidth, _maximumHeight));
        return _reader;
    }

    public void Dispose() => _reader?.Dispose();
}
