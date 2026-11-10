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

    [Theory]
    [InlineData("epub")]
    [InlineData("djvu")]
    public void ABookPageDrawnWithinFivePercentOfItsBaseGetsNoTile(string format)
    {
        using var revision = Revision(format);
        var basePage = NativeFrameRendering.Render(revision, 0, 64, 64, default);
        // 1.04 times the base stays within the 5% hysteresis; 1.06 times does not.
        Assert.Null(VisibleDetail.TryRender(revision, 0, basePage.Width * 1.04, basePage.Height * 1.04,
            basePage.Width, basePage.Height, 0, 0, 0, 1, 1, null, null, null, default));
        Assert.NotNull(VisibleDetail.TryRender(revision, 0, basePage.Width * 1.06, basePage.Height * 1.06,
            basePage.Width, basePage.Height, 0, 0, 0, 1, 1, null, null, null, default));
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
