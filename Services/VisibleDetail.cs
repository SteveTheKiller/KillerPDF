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
    /// The fitted page size to draw the detail at, or null when the base raster is already drawn at its own size. Any
    /// other base is resampled on screen, larger or smaller, and that softens small text (#526), so the settled view
    /// draws the visible part at device resolution.
    /// </summary>
    internal static int? DetailMaximum(double deviceWidth, double deviceHeight, int baseWidth, int baseHeight)
    {
        int maximum = Math.Clamp((int)Math.Ceiling(Math.Max(deviceWidth, deviceHeight)), 1, MaximumSize);
        return Math.Abs(maximum - Math.Max(baseWidth, baseHeight)) <= 1 ? null : maximum;
    }

    /// <summary>Where a tile goes inside its page image, and whether it is shown 1:1 at device pixels.</summary>
    internal readonly record struct TilePlacement(System.Windows.Rect Box, bool OneToOne);

    /// <summary>
    /// Where the tile goes inside its page image, in the image's own units. A tile drawn at the page's device size
    /// starts on a whole device pixel and spans exactly its own pixel count, so it is shown 1:1 and nothing resamples it
    /// (#526); the rounding moves it by under half a device pixel. A page drawn larger than <see cref="MaximumSize"/>
    /// gets a smaller tile, which is stretched over its part of the page as before.
    /// </summary>
    internal static TilePlacement Placement(System.Windows.Point imageScreenOrigin, double deviceWidth,
        double deviceHeight, double imageWidth, double imageHeight, VisibleRenderRegion region, int pixelWidth,
        int pixelHeight)
    {
        // The fitted page is within a pixel or two of the drawn size whenever it was not capped.
        if (Math.Abs(region.DisplayWidth - deviceWidth) > 2 || Math.Abs(region.DisplayHeight - deviceHeight) > 2)
            return new(new System.Windows.Rect(imageWidth * region.DisplayLeft / region.DisplayWidth,
                imageHeight * region.DisplayTop / region.DisplayHeight, imageWidth * pixelWidth / region.DisplayWidth,
                imageHeight * pixelHeight / region.DisplayHeight), false);
        double perX = deviceWidth / imageWidth, perY = deviceHeight / imageHeight;
        double left = Math.Round(imageScreenOrigin.X + deviceWidth * region.DisplayLeft / region.DisplayWidth)
            - imageScreenOrigin.X;
        double top = Math.Round(imageScreenOrigin.Y + deviceHeight * region.DisplayTop / region.DisplayHeight)
            - imageScreenOrigin.Y;
        return new(new System.Windows.Rect(left / perX, top / perY, pixelWidth / perX, pixelHeight / perY), true);
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
