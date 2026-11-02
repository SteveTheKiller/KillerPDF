using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The viewer leaves a signed /Sig field without an overlay (#425), so the page raster that
// PdfViewer.PreparePrimaryBitmap gets from PdfBackgroundRenderCache.Lease.Render must paint the
// signed appearance itself. Unsigned fields stay out of the raster because their overlays draw them.
public sealed class SignedSignatureViewerRasterTests
{
    private const int Width = 200, Height = 100;

    [Fact]
    public void ViewerRasterPaintsSignedSignatureAppearance()
    {
        byte[] pixels = RenderThroughViewerPath(signed: true);
        Assert.True(IsGreen(pixels, 70, 50), "The signed signature appearance is missing from the viewer raster.");
    }

    [Fact]
    public void ViewerRasterLeavesUnsignedSignatureFieldToItsOverlay()
    {
        byte[] pixels = RenderThroughViewerPath(signed: false);
        Assert.False(IsGreen(pixels, 70, 50), "An unsigned signature field was baked into the viewer raster.");
    }

    [Fact]
    public void OutputRasterPaintsSignedSignatureAppearance()
    {
        // Fixture check: the CLI and export path (form fields on) draws the same appearance.
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, Document(signed: true));
            using var session = PdfPageRenderSession.OpenEngineFirst(path, Width, Height, null);
            Assert.True(IsGreen(session.RenderFittedPage(0, Width, Height, includeFormFields: true).Pixels, 70, 50));
        }
        finally { File.Delete(path); }
    }

    private static byte[] RenderThroughViewerPath(bool signed)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        var cache = new PdfBackgroundRenderCache();
        try
        {
            File.WriteAllBytes(path, Document(signed));
            var request = cache.Capture(path, 0, null);
            using var lease = request.Rent();
            var rendered = lease.Render(0, Width, Height);
            Assert.Equal(Width, rendered.Width);
            Assert.Equal(Height, rendered.Height);
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

    private static byte[] Document(bool signed)
    {
        const string appearance = "0 0.6 0 rg 0 0 100 60 re f";
        string value = signed ? " /V 6 0 R" : "";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [20 20 120 80] /F 4 /P 3 0 R"
                + value + " /AP << /N 5 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 60] /Length " + appearance.Length + " >>\nstream\n"
                + appearance + "\nendstream",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 0 0 0] /Contents <00> >>",
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
