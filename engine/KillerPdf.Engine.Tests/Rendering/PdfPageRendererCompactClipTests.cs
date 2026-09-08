using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererCompactClipTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_FractionalRectanglesMatchFullRasterMasks(bool reverse, bool clip)
    {
        var random = new Random(7319);
        for (int i = 0; i < 200; i++)
        {
            double x = random.NextDouble() * 10 - (i < 100 ? 0.5 : 20);
            double y = random.NextDouble() * 5 - (i < 100 ? 0.5 : 20);
            double right = x + 0.02 + random.NextDouble() * (i < 100 ? 18 : 60);
            double top = y + 4 + random.NextDouble() * (i < 100 ? 16 : 60);
            byte[] expected = Render(x, y, right, top, reverse, clip, subdivide: true);
            byte[] actual = Render(x, y, right, top, reverse, clip, subdivide: false);
            Assert.Equal(expected, actual);
        }
    }

    private static byte[] Render(double x, double y, double right, double top,
        bool reverse, bool clip, bool subdivide)
    {
        var points = new List<(double X, double Y)> { (x, y), (right, y), (right, top), (x, top) };
        if (reverse) points.Reverse();
        var content = new PdfContentStreamBuilder().SetFillRgb(0.2, 0.4, 0.8);
        content.MoveTo(points[0].X, points[0].Y);
        for (int i = 0; i < points.Count; i++)
        {
            var from = points[i];
            var to = points[(i + 1) % points.Count];
            if (subdivide) content.LineTo((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            content.LineTo(to.X, to.Y);
        }
        content.ClosePath();
        if (clip)
            content.Clip().Rectangle(2.125, 1.375, 20.25, 18.125).Clip()
                .Rectangle(0, 0, 24, 20).Fill();
        else content.Fill();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(24, 20, content).Build());
        return new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(24, 20, transparentBackground: true)).Pixels.ToArray();
    }
}
