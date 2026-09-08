using System.Reflection;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfReducedMaskCacheTests
{
    [Fact]
    public void ThumbnailRetainsOnlyTheReducedMaskAndReusesIt()
    {
        const int size = 1024;
        byte[] rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 3] = (byte)(i / 4 % 251);
        }
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().DrawImage(PdfImage.FromRgba(size, size, rgba), 0, 0, 100, 100)).Build());
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(100, 100);
        byte[] pixels = new byte[100 * 100 * 4];
        Assert.Empty(renderer.RenderInto(0, options, pixels));
        object cache = typeof(PdfPageRenderer).GetField("_imageCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
        long retained = (long)cache.GetType().GetField("_currentWeight", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
        Assert.True(retained <= size * size * 3 + 200 * 200, $"Decoded cache retained {retained} bytes.");
        byte[] expected = pixels.ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Empty(renderer.RenderInto(0, options, pixels));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 * 1024, $"Warm thumbnail allocated {allocated} bytes.");
        Assert.Equal(expected, pixels);

        var zoom = new PdfRenderOptions(512, 512);
        byte[] actualZoom = new byte[512 * 512 * 4];
        Assert.Empty(renderer.RenderInto(0, zoom, actualZoom));
        var fresh = new PdfPageRenderer(document).Render(0, zoom);
        Assert.Empty(fresh.Diagnostics);
        Assert.Equal(fresh.Pixels.ToArray(), actualZoom);
    }
}
