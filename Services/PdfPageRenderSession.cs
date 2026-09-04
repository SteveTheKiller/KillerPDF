using Docnet.Core;
using Docnet.Core.Models;

namespace KillerPDF.Services;

/// <summary>
/// Owns one document render session so application features do not depend directly on the
/// current rendering backend. The engine renderer can replace this backend behind the same
/// boundary as its page coverage expands.
/// </summary>
internal sealed class PdfPageRenderSession : IDisposable
{
    private readonly string _path;
    private readonly Docnet.Core.Readers.IDocReader _reader;

    private PdfPageRenderSession(string path, Docnet.Core.Readers.IDocReader reader)
    {
        _path = path;
        _reader = reader;
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

    internal int PageCount => _reader.GetPageCount();

    internal PdfRenderedPage RenderBasePage(int pageIndex)
    {
        using var page = _reader.GetPageReader(pageIndex);
        return new PdfRenderedPage(page.GetPageWidth(), page.GetPageHeight(), page.GetImage());
    }

    internal PdfRenderedPage RenderPage(int pageIndex, bool transparentBackground = false,
        bool includeFormFields = true, bool removeTransparencyOnFallback = false)
    {
        using var page = _reader.GetPageReader(pageIndex);
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

    public void Dispose() => _reader.Dispose();
}

internal readonly record struct PdfRenderedPage(int Width, int Height, byte[] Pixels);
