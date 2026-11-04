using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionWidgetFilteringTests
{
    [Fact]
    public void ExcludedTextWidgetPreservesPrecisePageColorsWithViewerOptions()
    {
        var document = PdfDocument.Open(DocumentWithTextWidget());
        var frame = new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(2, 1, includeAnnotations: true, includeFormFields: false)
            {
                IncludeSignedSignatureFields = true,
                IncludePushButtonFields = true
            });

        Assert.InRange(frame.Pixels[0], 0.2141336f, 0.2141339f);
        Assert.InRange(frame.Pixels[4], 0.2142261f, 0.2142265f);
        Assert.True(frame.Pixels[4] - frame.Pixels[0] > 0.00008f);
        Assert.Equal(1f, frame.Pixels[3]);
        Assert.Equal(1f, frame.Pixels[7]);
    }

    [Fact]
    public void IncludedTextWidgetStillReportsUnsupportedPrecision()
    {
        var document = PdfDocument.Open(DocumentWithTextWidget());
        var renderer = new PdfPageRenderer(document);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(2, 1, includeAnnotations: true, includeFormFields: true)));
    }

    private static byte[] DocumentWithTextWidget()
    {
        const string content = "0.5001 g 0 0 300 300 re f\n0.5002 g 300 0 300 300 re f\n";
        const string appearance = "q Q\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /Rect [180 90 300 150] /T (overlayField) /V (FIELD)"
                + " /DA (/Helv 12 Tf 0 g) /F 4 /MK << /BG [0 1 0] >> /BS << /W 0 >> /P 3 0 R /AP << /N 6 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 120 60] /Resources << >> /Length {Encoding.ASCII.GetByteCount(appearance)} >>\nstream\n{appearance}endstream",
            "<< /Fields [5 0 R] /DR << /Font << /Helv 8 0 R >> >> /DA (/Helv 12 Tf 0 g) /NeedAppearances false >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            text.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
