using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfSynthesizedAnnotationCompatibilityTests
{
    [Fact]
    public void InkWithoutAppearancePaintsItsPath()
    {
        var renderer = new PdfPageRenderer(Document(
            "/Subtype /Ink /Rect [10 10 90 90] /C [1 0 0] /BS << /W 4 >> /InkList [[20 20 80 20 80 80]]"));

        PdfRenderedPage page = renderer.Render(0, new PdfRenderOptions(100, 100));

        Assert.Empty(page.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(page, 50, 80));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 50, 50));
    }

    [Fact]
    public void TextNoteWithoutAppearanceHasAVisibleMarker()
    {
        var renderer = new PdfPageRenderer(Document(
            "/Subtype /Text /Rect [20 20 40 40] /Contents (A review note)"));

        PdfRenderedPage page = renderer.Render(0, new PdfRenderOptions(100, 100));

        Assert.Empty(page.Diagnostics);
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, Pixel(page, 30, 65));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 50, 50));
    }

    [Theory]
    [InlineData(false, "/Subtype /Highlight /C [1 1 0] /QuadPoints [20 40 80 40 20 20 80 20]", 50, 70, 0, 255, 255)]
    [InlineData(true, "/Subtype /Highlight /C [1 1 0] /QuadPoints [20 40 80 40 20 20 80 20]", 50, 70, 0, 255, 255)]
    [InlineData(false, "/Subtype /Ink /C [1 0 0] /BS << /W 4 >> /InkList [[20 20 80 20]]", 50, 80, 0, 0, 255)]
    [InlineData(true, "/Subtype /Ink /C [1 0 0] /BS << /W 4 >> /InkList [[20 20 80 20]]", 50, 80, 0, 0, 255)]
    public void GeometryOutsideAnIncorrectRectangleKeepsItsPageCoordinates(
        bool recovery, string entries, int x, int y, byte blue, byte green, byte red)
    {
        var renderer = new PdfPageRenderer(Document(entries + " /Rect [85 85 95 95]", recovery));

        PdfRenderedPage page = renderer.Render(0, new PdfRenderOptions(100, 100));

        Assert.Empty(page.Diagnostics);
        Assert.Equal(new byte[] { blue, green, red, 255 }, Pixel(page, x, y));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 90, 10));
    }

    [Theory]
    [InlineData("/Subtype /Text /C [1 0 0]")]
    [InlineData("/Subtype /Ink /C [1 0 0] /InkList [[50 20 80 20]]")]
    [InlineData("/Subtype /Highlight /C [1 0 0] /QuadPoints [50 40 80 40 50 20 80 20]")]
    public void SavedAppearanceTakesPrecedenceOverFallbackGeometry(string entries)
    {
        var renderer = new PdfPageRenderer(Document(entries + " /Rect [20 20 40 40]",
            appearance: "0 1 0 rg 0 0 1 1 re f"));

        PdfRenderedPage page = renderer.Render(0, new PdfRenderOptions(100, 100));

        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, 30, 70));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 60, 80));
    }

    [Theory]
    [InlineData("/Subtype /Text /Rect [20 20 40 40]", 30, 65)]
    [InlineData("/Subtype /Ink /Rect [10 10 90 90] /BS << /W 4 >> /InkList [[20 20 80 20]]", 50, 80)]
    [InlineData("/Subtype /Highlight /Rect [20 20 80 40] /C [1 1 0] /QuadPoints [20 40 80 40 20 20 80 20]", 50, 70)]
    public void SynthesizedAppearancesRespectVisibilityAndAnnotationOptions(string entries, int x, int y)
    {
        foreach (var (flags, printing, enabled, visible) in new[] {
            (4, false, true, true), (4, true, true, true), (4, false, false, false),
            (2, false, true, false), (6, true, true, false), (32, false, true, false), (0, true, true, false) })
        {
            var renderer = new PdfPageRenderer(Document(entries, flags: flags));
            var page = renderer.Render(0, new PdfRenderOptions(100, 100, includeAnnotations: enabled) { ForPrinting = printing });
            Assert.Equal(visible, !Pixel(page, x, y).SequenceEqual(new byte[] { 255, 255, 255, 255 }));
        }
    }

    [Fact]
    public void InkPathsRemainSeparateAndUseOpacityAndDefaultBlack()
    {
        var renderer = new PdfPageRenderer(Document(
            "/Subtype /Ink /Rect [10 10 90 90] /CA 0.5 /BS << /W 4 >> /InkList [[20 20 80 20] [20 80 80 80]]"));

        var page = renderer.Render(0, new PdfRenderOptions(100, 100));

        foreach (byte channel in Pixel(page, 50, 80).Take(3)) Assert.InRange(channel, (byte)126, (byte)129);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 50, 50));
    }

    [Fact]
    public void MalformedInkIsRejectedStrictlyAndDiagnosedDuringRecovery()
    {
        const string entries = "/Subtype /Ink /Rect [10 10 90 90] /InkList [[20 20 80]]";
        Assert.Throws<FormatException>(() => new PdfPageRenderer(Document(entries))
            .Render(0, new PdfRenderOptions(100, 100)));

        var recovered = new PdfPageRenderer(Document(entries, recovery: true))
            .Render(0, new PdfRenderOptions(100, 100));

        Assert.Contains("An invalid annotation was skipped.", recovered.Diagnostics);
        Assert.All(recovered.Pixels.ToArray(), value => Assert.Equal(255, value));
    }

    [Fact]
    public void SmallNoteRectangleStillShowsReadableMarkerAtItsUpperLeft()
    {
        var renderer = new PdfPageRenderer(Document(
            "/Subtype /Text /Rect [20 37 23 40] /Contents (A review note)"));

        var page = renderer.Render(0, new PdfRenderOptions(100, 100));

        Assert.Equal(new byte[] { 0, 255, 255, 255 }, Pixel(page, 30, 70));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 19, 59));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Span.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static PdfDocument Document(string annotation, bool recovery = false, int flags = 4, string? appearance = null)
    {
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            $"<< /Type /Annot /F {flags} {annotation} {(appearance is null ? "" : "/AP << /N 5 0 R >>")} >>"
        ];
        if (appearance is not null)
            objects = [.. objects, $"<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length {appearance.Length} >>\nstream\n{appearance}\nendstream"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(pdf.ToString());
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
    }
}
