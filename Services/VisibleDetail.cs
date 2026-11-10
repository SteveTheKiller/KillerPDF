using KillerPdf.Engine.ColorManagement;
using KillerPDF.Presentation;
using PdfRenderedFrame = KillerPdf.Engine.Rendering.PdfRenderedFrame;

namespace KillerPDF.Services;

/// <summary>A rented renderer that draws part of a PDF or native book page at a fitted size.</summary>
internal interface IVisibleRegionLease : IDisposable
{
    (int Width, int Height) Dimensions(int pageIndex, int maximumWidth, int maximumHeight);
    PdfRenderedFrame RenderFrameRegion(int pageIndex, int maximumWidth, int maximumHeight, VisibleRenderRegion region,
        CancellationToken cancellationToken, PdfDocumentProofSettings? proof = null);
    PdfRenderedPage RenderRegion(int pageIndex, int maximumWidth, int maximumHeight,
        int left, int top, int width, int height, CancellationToken cancellationToken = default);
}

/// <summary>A document revision whose visible page regions can be drawn at device resolution.</summary>
internal interface IVisibleRegionSource
{
    IVisibleRegionLease RentRegion(CancellationToken cancellationToken);

    /// <summary>The bytes one region render may hold at once, for the viewport queue's memory reservation.</summary>
    long RegionReservation(int pageIndex, int maximum, bool proof, bool monitor);

    /// <summary>The bytes a region render of the page decodes beside the region itself, such as a whole DjVu scan.</summary>
    long RegionWorkingBytes(int pageIndex);
}

/// <summary>
/// The detail tile a zoomed page shows over its stretched base raster: the visible part of the page drawn at device
/// resolution, for PDFs and native books alike.
/// </summary>
internal static class VisibleDetail
{
    /// <summary>The largest page size, in device pixels, a detail tile is drawn at.</summary>
    internal const int MaximumSize = 6144;

    internal readonly record struct Tile(byte[] Pixels, int Width, int Height, VisibleRenderRegion Region,
        PdfViewingPixels Viewing);

    /// <summary>
    /// The fitted page size to draw the detail at, or null when the base raster is already within 5% of the drawn size.
    /// Same 5% hysteresis as the 1.8 re-sharpen: a base render within 5% of the drawn size shows no visible softness,
    /// and re-rendering it only delays the settled view.
    /// </summary>
    internal static int? DetailMaximum(double deviceWidth, double deviceHeight, int baseWidth, int baseHeight)
    {
        int maximum = Math.Clamp((int)Math.Ceiling(Math.Max(deviceWidth, deviceHeight)), 1, MaximumSize);
        return maximum <= Math.Max(baseWidth, baseHeight) * 1.05 ? null : maximum;
    }

    /// <summary>Draws the tile for a page drawn at the given device size, or returns null when the base suffices.</summary>
    internal static Tile? TryRender(IVisibleRegionSource source, int page, double deviceWidth, double deviceHeight,
        int baseWidth, int baseHeight, int rotation, double left, double top, double right, double bottom,
        PdfDocumentProofSettings? proof, PdfSdrDisplayConverter? monitor,
        Func<(int Width, int Height), VisibleRenderRegion, BitmapHelpers.FracRect[]>? invertKeep,
        CancellationToken cancellationToken)
        => DetailMaximum(deviceWidth, deviceHeight, baseWidth, baseHeight) is { } maximum
            ? Render(source, page, maximum, rotation, left, top, right, bottom, proof, monitor, invertKeep, cancellationToken)
            : null;

    /// <summary>
    /// Draws the visible fractions of the page fitted inside maximum x maximum, converted for the monitor like its
    /// base page, inverted with invertKeep's carve-outs when given (dark mode), and turned by the page rotation.
    /// </summary>
    internal static Tile Render(IVisibleRegionSource source, int page, int maximum, int rotation,
        double left, double top, double right, double bottom, PdfDocumentProofSettings? proof,
        PdfSdrDisplayConverter? monitor, Func<(int Width, int Height), VisibleRenderRegion, BitmapHelpers.FracRect[]>? invertKeep,
        CancellationToken cancellationToken)
    {
        using var lease = source.RentRegion(cancellationToken);
        var dimensions = lease.Dimensions(page, maximum, maximum);
        var region = VisibleRenderRegion.Create(dimensions.Width, dimensions.Height, rotation, left, top, right, bottom)
            ?? throw new OperationCanceledException();
        var viewing = PdfViewingFrameConversion.Render(
            requested => lease.RenderFrameRegion(page, maximum, maximum, region, cancellationToken, requested),
            () => lease.RenderRegion(page, maximum, maximum, region.Left, region.Top, region.Width, region.Height, cancellationToken),
            proof, monitor, cancellationToken);
        byte[] pixels = viewing.Pixels;
        int width = region.Width, height = region.Height;
        if (invertKeep is not null)
            BitmapHelpers.InvertBgraInPlaceExcept(pixels, width, height, invertKeep(dimensions, region));
        if (rotation != 0) (pixels, width, height) = BitmapHelpers.RotateBitmap(pixels, width, height, rotation);
        return new Tile(pixels, width, height, region, viewing);
    }
}
