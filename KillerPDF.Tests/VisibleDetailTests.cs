using System.IO;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A zoomed-in page shows its base raster stretched until refinement draws the visible part at device resolution.
// Books take the same detail-tile path as PDFs.
public sealed class VisibleDetailTests
{
    [Theory]
    [InlineData("epub")]
    [InlineData("djvu")]
    public void ABookPageDrawnMoreThanFivePercentLargerThanItsBaseGetsADeviceResolutionTile(string format)
    {
        using var revision = Revision(format);
        // The base page the viewer shows: fitted inside 64 pixels.
        var basePage = NativeFrameRendering.Render(revision, 0, 64, 64, default);
        // Drawn four times larger on screen; the visible part is the middle half each way.
        double deviceWidth = basePage.Width * 4.0, deviceHeight = basePage.Height * 4.0;
        var tile = VisibleDetail.TryRender(revision, 0, deviceWidth, deviceHeight, basePage.Width, basePage.Height,
            0, .25, .25, .75, .75, null, null, null, default);
        Assert.NotNull(tile);
        // The whole page at device resolution, cropped to the visible half: the tile must be exactly that.
        int maximum = (int)Math.Ceiling(Math.Max(deviceWidth, deviceHeight));
        var page = NativeFrameRendering.Render(revision, 0, maximum, maximum, default);
        byte[] whole = page.ToSrgbBgra32();
        int left = (int)Math.Floor(page.Width * .25), top = (int)Math.Floor(page.Height * .25);
        int right = (int)Math.Ceiling(page.Width * .75), bottom = (int)Math.Ceiling(page.Height * .75);
        Assert.Equal(right - left, tile.Value.Width);
        Assert.Equal(bottom - top, tile.Value.Height);
        Assert.Equal(new VisibleRenderRegion(left, top, right - left, bottom - top, left, top, page.Width, page.Height),
            tile.Value.Region);
        for (int y = 0; y < tile.Value.Height; y++)
            Assert.Equal(whole.AsSpan(((top + y) * page.Width + left) * 4, tile.Value.Width * 4).ToArray(),
                tile.Value.Pixels.AsSpan(y * tile.Value.Width * 4, tile.Value.Width * 4).ToArray());
    }

    // #526: only a base raster already at the drawn size is shown as it is. A page shown smaller than its base, the
    // usual fit-width view, gets a device-resolution tile too, because the downsampled base softens small text.
    [Theory]
    [InlineData("epub")]
    [InlineData("djvu")]
    public void OnlyABookPageDrawnAtItsBaseSizeGetsNoTile(string format)
    {
        using var revision = Revision(format);
        var basePage = NativeFrameRendering.Render(revision, 0, 64, 64, default);
        Assert.Null(VisibleDetail.TryRender(revision, 0, basePage.Width, basePage.Height,
            basePage.Width, basePage.Height, 0, 0, 0, 1, 1, null, null, null, default));
        Assert.NotNull(VisibleDetail.TryRender(revision, 0, basePage.Width * 1.04, basePage.Height * 1.04,
            basePage.Width, basePage.Height, 0, 0, 0, 1, 1, null, null, null, default));
        var smaller = VisibleDetail.TryRender(revision, 0, basePage.Width * .6, basePage.Height * .6,
            basePage.Width, basePage.Height, 0, 0, 0, 1, 1, null, null, null, default);
        Assert.NotNull(smaller);
        Assert.Equal((int)Math.Ceiling(Math.Max(basePage.Width, basePage.Height) * .6),
            Math.Max(smaller.Value.Width, smaller.Value.Height));
    }

    // #526: the tile starts on a whole device pixel and spans exactly its own pixels, so it is shown 1:1 and nothing
    // resamples it, at any monitor scale and wherever the page sits on screen.
    [Theory]
    [InlineData(1.14, 100.3, 50.7)]
    [InlineData(1.25, 0, 0)]
    [InlineData(1.5, 17.25, 3.5)]
    public void ATileLandsOnWholeDevicePixels(double scale, double originX, double originY)
    {
        double imageWidth = 612, imageHeight = 792;
        double deviceWidth = imageWidth * scale, deviceHeight = imageHeight * scale;
        int pageWidth = (int)Math.Round(deviceWidth), pageHeight = (int)Math.Round(deviceHeight);
        var region = new VisibleRenderRegion(101, 203, 333, 377, 101, 203, pageWidth, pageHeight);
        var box = VisibleDetail.Placement(new System.Windows.Point(originX, originY), deviceWidth, deviceHeight,
            imageWidth, imageHeight, region, 333, 377);
        double perX = deviceWidth / imageWidth, perY = deviceHeight / imageHeight;
        double left = originX + box.Left * perX, top = originY + box.Top * perY;
        Assert.Equal(Math.Round(left), left, 6);
        Assert.Equal(Math.Round(top), top, 6);
        Assert.Equal(333, box.Width * perX, 6);
        Assert.Equal(377, box.Height * perY, 6);
        // Within a device pixel of where the region sits on the page.
        Assert.InRange(left - (originX + deviceWidth * 101 / pageWidth), -.5, .5);
        Assert.InRange(top - (originY + deviceHeight * 203 / pageHeight), -.5, .5);
    }

    [Theory]
    [InlineData("epub")]
    [InlineData("djvu")]
    public void ATurnedAndInvertedBookTileMatchesItsTurnedAndInvertedPage(string format)
    {
        using var revision = Revision(format);
        var basePage = NativeFrameRendering.Render(revision, 0, 64, 64, default);
        // Turned 90 degrees, the page is drawn base height wide and base width tall, four times larger.
        double deviceWidth = basePage.Height * 4.0, deviceHeight = basePage.Width * 4.0;
        var tile = VisibleDetail.TryRender(revision, 0, deviceWidth, deviceHeight, basePage.Height, basePage.Width,
            90, 0, .5, .5, 1, null, null, (_, _) => [], default);
        Assert.NotNull(tile);
        int maximum = (int)Math.Ceiling(Math.Max(deviceWidth, deviceHeight));
        var page = NativeFrameRendering.Render(revision, 0, maximum, maximum, default);
        byte[] upright = page.ToSrgbBgra32();
        BitmapHelpers.InvertBgraInPlaceExcept(upright, page.Width, page.Height, []);
        var (turned, width, height) = BitmapHelpers.RotateBitmap(upright, page.Width, page.Height, 90);
        // The lower-left quarter of the turned page.
        int left = 0, top = (int)Math.Floor(height * .5);
        int right = (int)Math.Ceiling(width * .5), bottom = height;
        Assert.Equal(right - left, tile.Value.Width);
        Assert.Equal(bottom - top, tile.Value.Height);
        for (int y = 0; y < tile.Value.Height; y++)
            Assert.Equal(turned.AsSpan(((top + y) * width + left) * 4, tile.Value.Width * 4).ToArray(),
                tile.Value.Pixels.AsSpan(y * tile.Value.Width * 4, tile.Value.Width * 4).ToArray());
    }

    private static PdfCapturedReadRevision Revision(string format)
    {
        if (format == "djvu") return new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(NativeWorkingDocumentTests.DjvuPage()));
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")
            ?? throw new InvalidOperationException("The portable font fixture is missing.");
        byte[] font = new byte[checked((int)resource.Length)];
        resource.ReadExactly(font);
        return new PdfCapturedReadRevision(PdfReadSnapshot.OpenEpub(
            NativeWorkingDocumentTests.Book("<p>AAAA AAA AAAAA</p><p>AA AAAA</p>"),
            new EpubLayoutOptions(80, 48, TrueTypeFont.Load(font)) { FontSize = 12, Margin = 4 }));
    }
}
