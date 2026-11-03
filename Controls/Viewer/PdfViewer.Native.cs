using KillerPDF.Services;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private NativeWorkingDocument? _nativeDoc;

    internal NativeWorkingDocument? NativeDocumentRef { get => _nativeDoc; set => _nativeDoc = value; }
    private bool HasReadDocument => _doc is not null || _nativeDoc is not null;
    internal bool HasReadDocumentExt => HasReadDocument;
    private int ReadPageCount => _nativeDoc?.PageCount ?? _doc?.PageCount ?? 0;

    internal static KillerPdf.Engine.Rendering.PdfRenderedFrame RenderNativeFrame(
        PdfCapturedReadRevision revision, int page, int maximumWidth, int maximumHeight,
        CancellationToken cancellationToken)
    {
        var geometry = revision.GetPageGeometry(page);
        double width = geometry.Width, height = geometry.Height;
        if (geometry.Rotation is 90 or 270) (width, height) = (height, width);
        double scale = Math.Min(maximumWidth / width, maximumHeight / height);
        int pixelsWide = Math.Clamp((int)Math.Round(width * scale), 1, maximumWidth);
        int pixelsHigh = Math.Clamp((int)Math.Round(height * scale), 1, maximumHeight);
        using var lease = revision.Rent(cancellationToken);
        return lease.RenderFrame(page, new KillerPdf.Engine.Rendering.PdfRenderOptions(
            pixelsWide, pixelsHigh, maximumPixelBytes: 128L << 20));
    }

    private PdfPageInformation ReadPageInformation(int page)
        => _nativeDoc is not null ? _nativeDoc.CaptureReadRevision().GetPageGeometry(page)
            : EnsureEngineDocumentSession().Pages[page];

    private (double Width, double Height) ReadVisualPageSize(int page)
    {
        if (_nativeDoc is null) return EnsureEngineDocumentSession().VisualPageSize(page, _pageRotations);
        PdfPageInformation geometry = ReadPageInformation(page);
        int extra = _pageRotations.TryGetValue(page, out int rotation) ? rotation : 0;
        int total = ((geometry.Rotation + extra) % 360 + 360) % 360;
        return total is 90 or 270
            ? (geometry.Height, geometry.Width) : (geometry.Width, geometry.Height);
    }

    private (double Width, double Height) ReadPhysicalPageSize(int page)
    {
        if (_nativeDoc is null) return EnsureEngineDocumentSession().PhysicalPageSize(page, _pageRotations);
        var (width, height) = ReadVisualPageSize(page);
        double unit = ReadPageInformation(page).UserUnit;
        return (width * unit, height * unit);
    }
}
