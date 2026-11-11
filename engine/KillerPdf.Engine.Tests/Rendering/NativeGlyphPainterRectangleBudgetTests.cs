using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class NativeGlyphPainterRectangleBudgetTests
{
    private static readonly PdfRgbColor Ink = new(0, 0, 0);

    private static List<NativePagePaint> PageSized(int width, int height, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => (NativePagePaint)new NativeRectanglePaint(0, 0, width, height,
            new PdfRgbColor(.5, .5, .5)))];

    [Fact]
    public void PageSizedBackgroundsOnALargePageStayWithinTheRenderSizeBudget()
    {
        // 2048 x 2048 is 4.2 million pixels, so six page-sized fills cost 25 million pixels of work. A real page
        // can stack that many backgrounds (body, wrapper, rows); the work is a few page areas, not hostile.
        var frame = NativeGlyphPainter.Render(2048, 2048, PageSized(2048, 2048, 6), Ink, CancellationToken.None);

        Assert.Equal(2048, frame.Width);
        // Opaque fills leave the last fill: sRGB 0.5 is .21404114 in linear light (IEC 61966-2-1).
        Assert.Equal(.21404114f, frame.Pixels[0], 4);
    }

    [Fact]
    public void RectangleWorkOnASmallPageIsStillCapped()
    {
        // 100 x 100 with 2000 fills is 20 million pixels, above the 16 million floor.
        Assert.Throws<InvalidDataException>(() =>
            NativeGlyphPainter.Render(100, 100, PageSized(100, 100, 2000), Ink, CancellationToken.None));
    }

    [Fact]
    public void RectangleWorkIsCappedAtAFixedMultipleOfTheRenderSize()
    {
        // 1000 x 1000 allows 32 page areas of rectangle work; 33 page-sized fills exceed it.
        _ = NativeGlyphPainter.Render(1000, 1000, PageSized(1000, 1000, 32), Ink, CancellationToken.None);
        Assert.Throws<InvalidDataException>(() =>
            NativeGlyphPainter.Render(1000, 1000, PageSized(1000, 1000, 33), Ink, CancellationToken.None));
    }
}
