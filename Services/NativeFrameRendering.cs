using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Draws one EPUB or DjVu page into a frame no larger than the given size, keeping its proportions.</summary>
internal static class NativeFrameRendering
{
    internal static KillerPdf.Engine.Rendering.PdfRenderedFrame Render(
        PdfCapturedReadRevision revision, int page, int maximumWidth, int maximumHeight,
        CancellationToken cancellationToken, KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
    {
        var geometry = revision.GetPageGeometry(page);
        double width = geometry.Width, height = geometry.Height;
        if (geometry.Rotation is 90 or 270) (width, height) = (height, width);
        double scale = Math.Min(maximumWidth / width, maximumHeight / height);
        int pixelsWide = Math.Clamp((int)Math.Round(width * scale), 1, maximumWidth);
        int pixelsHigh = Math.Clamp((int)Math.Round(height * scale), 1, maximumHeight);
        using var lease = revision.Rent(cancellationToken);
        return lease.RenderFrame(page, new KillerPdf.Engine.Rendering.PdfRenderOptions(
            pixelsWide, pixelsHigh, maximumPixelBytes: (proof?.Enabled == true ? 256L : 128L) << 20) { Proof = proof });
    }
}
