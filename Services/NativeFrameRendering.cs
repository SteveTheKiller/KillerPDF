using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Draws one EPUB or DjVu page into a frame no larger than the given size, keeping its proportions.</summary>
internal static class NativeFrameRendering
{
    internal static KillerPdf.Engine.Rendering.PdfRenderedFrame Render(
        PdfCapturedReadRevision revision, int page, int maximumWidth, int maximumHeight,
        CancellationToken cancellationToken, KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings? proof = null)
    {
        var (pixelsWide, pixelsHigh) = FittedSize(revision.GetPageGeometry(page), maximumWidth, maximumHeight);
        using var lease = revision.Rent(cancellationToken);
        // These frames are for viewing only, so they carry any requested gamut warning marks.
        return lease.RenderFrame(page, new KillerPdf.Engine.Rendering.PdfRenderOptions(
            pixelsWide, pixelsHigh, maximumPixelBytes: (proof?.Enabled == true ? 256L : 128L) << 20) { Proof = proof })
            .WithGamutWarningsMarked(cancellationToken);
    }

    /// <summary>The pixel size a page is drawn at inside the given size, with its intrinsic rotation applied.</summary>
    internal static (int Width, int Height) FittedSize(PdfPageInformation geometry, int maximumWidth, int maximumHeight)
    {
        double width = geometry.Width, height = geometry.Height;
        if (geometry.Rotation is 90 or 270) (width, height) = (height, width);
        double scale = Math.Min(maximumWidth / width, maximumHeight / height);
        return (Math.Clamp((int)Math.Round(width * scale), 1, maximumWidth),
            Math.Clamp((int)Math.Round(height * scale), 1, maximumHeight));
    }
}
