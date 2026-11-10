using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// Real DjVu scans are 300 to 600 dpi: a letter page is 2550 x 3300 or 5100 x 6600 pixels. The viewer draws thumbnails
// and fitted pages inside 128 MiB, so the decoded page has to be held as the bits, gray or color bytes it was coded as.
public sealed class PdfDjvuScanResolutionTests
{
    private const long ViewerBudget = 128L << 20;

    // Each page is black on its stored left half and white on its right half.
    // Orientation byte 6 turns the page 90 degrees counterclockwise, so its left half ends up at the bottom.
    [Theory]
    [InlineData(2550, 3300, 300, (byte)1, 396, 512)]
    [InlineData(5100, 6600, 600, (byte)1, 396, 512)]
    [InlineData(4960, 7016, 600, (byte)6, 512, 362)]
    public void AScanPageDrawsAsAThumbnailInsideTheViewerBudget(int width, int height, int dpi, byte orientation,
        int thumbWidth, int thumbHeight)
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(HalfBlackPage(width, height, dpi, orientation));
        var options = new PdfRenderOptions(thumbWidth, thumbHeight, maximumPixelBytes: ViewerBudget);
        snapshot.RenderPage(0, new PdfRenderOptions(8, 8, maximumPixelBytes: ViewerBudget));
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var thumb = snapshot.RenderPage(0, options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(thumbWidth, thumb.Width);
        Assert.Equal(thumbHeight, thumb.Height);
        var pixels = thumb.Pixels;
        // Area averaging keeps the page's mean: exactly half of it is ink.
        double sum = 0;
        for (int i = 0; i < pixels.Length; i += 4) sum += pixels[i];
        Assert.InRange(sum / (pixels.Length / 4), .5 - 1e-4, .5 + 1e-4);
        float topLeft = pixels[0], bottomLeft = pixels[(thumbHeight - 1) * thumbWidth * 4];
        float topRight = pixels[(thumbWidth - 1) * 4];
        if (orientation == 1)
        {
            Assert.Equal(0f, topLeft);
            Assert.Equal(1f, topRight);
        }
        else
        {
            Assert.Equal(1f, topLeft);
            Assert.Equal(0f, bottomLeft);
        }
        // The thumbnail's float frame and its owned copy are under 2 MiB, the page's bits at most 4.4 MB.
        Assert.True(allocated < 24L << 20, $"A {thumbWidth} x {thumbHeight} thumbnail of a {width} x {height} page allocated {allocated} bytes.");
    }

    // A fitted page is at most 2048 pixels on its long side. Area averaging a 600 dpi page down to it reads about
    // (5100 + 1583) x (6600 + 2048) = 57.8 million source taps, inside the 64 million tap work limit.
    [Theory]
    [InlineData(5100, 6600, 1583, 2048)]
    [InlineData(4960, 7016, 1448, 2048)]
    public void A600DpiScanPageDrawsFittedAt2048Pixels(int width, int height, int fitWidth, int fitHeight)
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(HalfBlackPage(width, height, 600, 1));
        var fit = snapshot.RenderPage(0, new PdfRenderOptions(fitWidth, fitHeight, maximumPixelBytes: ViewerBudget));
        Assert.Equal(fitWidth, fit.Width);
        Assert.Equal(fitHeight, fit.Height);
        var pixels = fit.Pixels;
        double sum = 0;
        for (int i = 0; i < pixels.Length; i += 4) sum += pixels[i];
        Assert.InRange(sum / (pixels.Length / 4), .5 - 1e-4, .5 + 1e-4);
        Assert.Equal(0f, pixels[0]);
        Assert.Equal(1f, pixels[(fitWidth - 1) * 4]);
    }

    // At 800% a letter page is 8.5 x 96 x 8 = 6528 by 8448 pixels; the viewer draws a 2560 x 1400 tile of it.
    [Theory]
    [InlineData(2550, 3300, 300)]
    [InlineData(5100, 6600, 600)]
    public void AScanPageDrawsAHighZoomRegionAcrossTheInkEdge(int width, int height, int dpi)
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(HalfBlackPage(width, height, dpi, 1));
        var zoomed = new PdfRenderOptions(6528, 8448);
        snapshot.RenderPageRegion(0, zoomed, 0, 0, 8, 8);
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var region = snapshot.RenderPageRegion(0, zoomed, 1984, 3524, 2560, 1400);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2560, region.Width);
        Assert.Equal(1400, region.Height);
        // Bilinear centers are (i + .5) x width / 6528 - .5; the last black stored column is width / 2 - 1. Page
        // columns up to 3262 read only black, from 3265 only white, so region columns 1278 and 1281 bracket the edge.
        var pixels = region.Pixels;
        foreach (int row in new[] { 0, 1399 })
        {
            int start = row * 2560 * 4;
            Assert.Equal(0f, pixels[start]);
            Assert.Equal(0f, pixels[start + 1278 * 4]);
            Assert.InRange(pixels[start + 1279 * 4], 1e-6f, 1 - 1e-6f);
            Assert.InRange(pixels[start + 1280 * 4], 1e-6f, 1 - 1e-6f);
            Assert.Equal(1f, pixels[start + 1281 * 4]);
            Assert.Equal(1f, pixels[start + 2559 * 4]);
        }
        // The tile's float frame and its owned copy are 109.4 MiB; the page's bits add at most 4.2 MB.
        Assert.True(allocated < 128L << 20, $"A 2560 x 1400 tile of a {width} x {height} page allocated {allocated} bytes.");
    }

    // A 300 dpi letter grayscale IW44 page with a complete initial header and no coded slices: DjVu v3 sections 10.3,
    // 10.4 and 10.6.6 give it zero coefficients, so every sample is gray 127, which the INFO gamma of 2.2 makes
    // (127 / 255)^2.2 in linear light.
    [Fact]
    public void AGrayscaleScanPageDrawsAsAThumbnailAFittedPageAndAHighZoomRegion()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(FlatGrayPage(2550, 3300, 300));
        float expected = (float)Math.Pow(127 / 255d, 2.2);
        snapshot.RenderPage(0, new PdfRenderOptions(8, 8, maximumPixelBytes: ViewerBudget));
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var thumb = snapshot.RenderPage(0, new PdfRenderOptions(396, 512, maximumPixelBytes: ViewerBudget));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var fit = snapshot.RenderPage(0, new PdfRenderOptions(1583, 2048, maximumPixelBytes: ViewerBudget));
        var region = snapshot.RenderPageRegion(0, new PdfRenderOptions(6528, 8448), 1984, 3524, 2560, 1400);
        foreach (var (frame, width, height) in new[] { (thumb, 396, 512), (fit, 1583, 2048), (region, 2560, 1400) })
        {
            Assert.Equal(width, frame.Width);
            Assert.Equal(height, frame.Height);
            var pixels = frame.Pixels;
            for (int i = 0; i < pixels.Length; i += 4)
                if (Math.Abs(pixels[i] - expected) > 1e-6f || pixels[i + 1] != pixels[i] || pixels[i + 2] != pixels[i])
                    Assert.Fail($"Pixel {i / 4} of the {width} x {height} frame is {pixels[i]}, not {expected}.");
        }
        // The IW44 decoder's coefficients, flags, reconstruction and gray samples are 5 bytes per coefficient and
        // 5 per pixel (80.8 MiB); the gray page is kept as those bytes, never as a 16-byte float frame.
        Assert.True(allocated < 96L << 20, $"A 396 x 512 thumbnail of a 2550 x 3300 gray page allocated {allocated} bytes.");
    }

    private static byte[] FlatGrayPage(int width, int height, int dpi)
    {
        byte[] info = [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 26, 0,
            (byte)dpi, (byte)(dpi >> 8), 22, 1];
        byte[] background = [0, 0, 0x81, 2, (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 0xff];
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8, .. Chunk("INFO", info), .. Chunk("BG44", background)])];
    }

    private static readonly ConcurrentDictionary<(int, int, int, byte), byte[]> Pages = new();

    internal static byte[] HalfBlackPage(int width, int height, int dpi, byte orientation) =>
        Pages.GetOrAdd((width, height, dpi, orientation), _ =>
        {
            var gray = new byte[width * height];
            for (int y = 0; y < height; y++) gray.AsSpan(y * width + width / 2, width - width / 2).Fill(255);
            byte[] g4 = PdfCcittGroup4Encoder.Encode(width, height, gray);
            byte[] info = [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 26, 0,
                (byte)dpi, (byte)(dpi >> 8), 22, orientation];
            byte[] smmr = [.. "MMR"u8, 0, (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, .. g4];
            return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8, .. Chunk("INFO", info), .. Chunk("Smmr", smmr)])];
        });

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }
}
