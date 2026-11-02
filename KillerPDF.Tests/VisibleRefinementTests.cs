using System.IO;
using System.Text;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

public sealed class VisibleRefinementTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public void VisibleCropMatchesFullRasterAtEveryRotationAndInversion(int rotation, bool invert)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(100, 150, Encoding.ASCII.GetBytes(
                "0.8 0.1 0.3 rg 3.25 4.5 94 140 re f 0.1 0.8 0.2 RG 3.5 w 1 J 1 j " +
                "-5 8 m 110 140 l S 0.4 0.2 0.9 rg 4 70 m 100 160 120 -40 30 45 c f")).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 321, 321, null);
            var full = session.RenderFittedPage(0, 321, 321);
            int dw = rotation is 90 or 270 ? full.Height : full.Width;
            int dh = rotation is 90 or 270 ? full.Width : full.Height;
            byte[] display = (byte[])full.Pixels.Clone();
            if (invert) BitmapHelpers.InvertBgraInPlace(display);
            if (rotation != 0) (display, _, _) = BitmapHelpers.RotateBitmap(display, full.Width, full.Height, rotation);
            foreach (var bounds in new[] { (0.0, 0.0, .31, .46), (.23, .19, .77, .83), (.79, .74, 1.0, 1.0) })
            {
                var region = VisibleRenderRegion.Create(full.Width, full.Height, rotation,
                    bounds.Item1, bounds.Item2, bounds.Item3, bounds.Item4)!.Value;
                var crop = session.RenderFittedRegion(0, 321, 321,
                    region.Left, region.Top, region.Width, region.Height);
                byte[] pixels = crop.Pixels;
                int w = crop.Width, h = crop.Height;
                if (invert) BitmapHelpers.InvertBgraInPlace(pixels);
                if (rotation != 0) (pixels, w, h) = BitmapHelpers.RotateBitmap(pixels, w, h, rotation);
                Assert.Equal(dw, region.DisplayWidth);
                Assert.Equal(dh, region.DisplayHeight);
                for (int row = 0; row < h; row++)
                    Assert.Equal(display.AsSpan(((region.DisplayTop + row) * dw + region.DisplayLeft) * 4, w * 4).ToArray(),
                        pixels.AsSpan(row * w * 4, w * 4).ToArray());
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CroppingAcrossFractionalAnnotationEdgesKeepsDocumentCoverage()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(612, 396, ReadOnlyMemory<byte>.Empty)
                .AddHighlight(0, 100, 200, 300, 36).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 512, 512, null);
            var full = session.RenderFittedPage(0, 512, 512);
            var crop = session.RenderFittedRegion(0, 512, 512, 128, 82, 170, 110);
            for (int row = 0; row < crop.Height; row++)
                Assert.Equal(full.Pixels.AsSpan(((82 + row) * full.Width + 128) * 4, crop.Width * 4).ToArray(),
                    crop.Pixels.AsSpan(row * crop.Width * 4, crop.Width * 4).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OffPageOrInvalidViewportProducesNoWork()
    {
        Assert.Null(VisibleRenderRegion.Create(100, 200, 0, 1.1, 0, 1.2, 1));
        Assert.Null(VisibleRenderRegion.Create(100, 200, 45, 0, 0, 1, 1));
        Assert.Null(VisibleRenderRegion.Create(100, 200, 0, double.NaN, 0, 1, 1));
    }
}
