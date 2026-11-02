using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The viewer builds no overlay for a push button, so its appearance has to come from the page raster,
// as it did in 1.8.91. Fillable fields stay out of the raster because their overlays draw them.
public sealed class PushButtonViewerRasterTests
{
    private const int Width = 200, Height = 100;

    [Fact]
    public void ViewerRasterPaintsPushButtonAppearance()
    {
        byte[] pixels = RenderThroughViewerPath("/FT /Btn /Ff 65536 /T (Submit)");
        Assert.True(IsGreen(pixels, 70, 50), "The push button appearance is missing from the viewer raster.");
    }

    [Fact]
    public void ViewerRasterLeavesTextFieldToItsOverlay()
    {
        byte[] pixels = RenderThroughViewerPath("/FT /Tx /T (Name)");
        Assert.False(IsGreen(pixels, 70, 50), "A text field was baked into the viewer raster.");
    }

    private static byte[] RenderThroughViewerPath(string field)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        var cache = new PdfBackgroundRenderCache();
        try
        {
            File.WriteAllBytes(path, Document(field));
            var request = cache.Capture(path, 0, null);
            using var lease = request.Rent();
            var rendered = lease.Render(0, Width, Height);
            return rendered.Pixels;
        }
        finally { cache.Clear(); File.Delete(path); }
    }

    // BGRA, top-down rows. The appearance fills rgb(0, 153, 0).
    private static bool IsGreen(byte[] pixels, int x, int y)
    {
        int i = (y * Width + x) * 4;
        return pixels[i] < 40 && pixels[i + 1] > 120 && pixels[i + 2] < 40;
    }

    private static byte[] Document(string field)
    {
        const string appearance = "0 0.6 0 rg 0 0 100 60 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget " + field + " /Rect [20 20 120 80] /F 4 /P 3 0 R /AP << /N 5 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 60] /Length " + appearance.Length + " >>\nstream\n"
                + appearance + "\nendstream",
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append(offset.ToString("D10")).Append(" 00000 n \n");
        text.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
