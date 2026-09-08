using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererCachePolicyTests
{
    [Fact]
    public void RenderInto_ReusesCallerBufferWithoutChangingCachedPageOrBufferTail()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(1, 2, 6, 5).Fill()).Build());
        var renderer = new PdfPageRenderer(source);
        var options = new PdfRenderOptions(20, 20);
        PdfRenderedPage cached = renderer.Render(0, options);
        byte[] destination = Enumerable.Repeat((byte)77, 1700).ToArray();
        Assert.Empty(renderer.RenderInto(0, options, destination));
        Assert.Equal(cached.Pixels.ToArray(), destination[..1600]);
        Assert.All(destination[1600..], value => Assert.Equal(77, value));
        Array.Clear(destination);
        renderer.RenderInto(0, options, destination);
        Assert.Equal(cached.Pixels.ToArray(), destination[..1600]);
        Assert.Same(cached, renderer.Render(0, options));
        Assert.Throws<ArgumentException>(() => renderer.RenderInto(0, options, new byte[1599]));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderInto(1, options, destination));
        Assert.Throws<OperationCanceledException>(() => renderer.RenderInto(0, options, destination,
            new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Render_OneShotOutputBypassesBitmapCacheWithoutChangingPixels()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(1.25, 2.5, 6, 5).Fill()).Build());
        var renderer = new PdfPageRenderer(source);
        var cachedOptions = new PdfRenderOptions(20, 20);
        var oneShot = cachedOptions with { CacheResult = false };
        PdfRenderedPage cached = renderer.Render(0, cachedOptions);
        PdfRenderedPage first = renderer.Render(0, oneShot);
        PdfRenderedPage second = renderer.Render(0, oneShot);
        Assert.NotSame(cached, first);
        Assert.NotSame(first, second);
        Assert.Same(cached, renderer.Render(0, cachedOptions));
        Assert.Equal(cached.Pixels.ToArray(), first.Pixels.ToArray());
        Assert.Equal(first.Pixels.ToArray(), second.Pixels.ToArray());
    }
}
