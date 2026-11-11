using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfAnnotationRecoveryTests
{
    [Theory]
    [InlineData("/Rect [10 10 /bad 30]", "/BBox [0 0 20 20]")]
    [InlineData("/Rect [10 10 /bad]", "/BBox [0 0 20 20]")]
    [InlineData("/Rect [10 10 30 30]", "/BBox [0 0 /bad 20]")]
    [InlineData("/Rect [10 10 30 30]", "/BBox [0 0 20 20] /Matrix [1 0 0 /bad 0 0]")]
    public void RecoverySkipsMalformedAnnotationAndRendersPageAndNeighbor(string rect, string appearanceGeometry)
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(CreatePdf(rect, appearanceGeometry));

        PdfRenderedPage result = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));

        Assert.Contains("An invalid annotation was skipped.", result.Diagnostics);
        Assert.Contains(Pixels(result.Pixels.ToArray()), pixel => pixel.SequenceEqual(new byte[] { 0, 255, 0, 255 }));
        Assert.Contains(Pixels(result.Pixels.ToArray()), pixel => pixel.SequenceEqual(new byte[] { 255, 0, 0, 255 }));
    }

    [Fact]
    public void StrictRenderingRejectsMalformedAnnotationGeometry()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf("/Rect [10 10 /bad 30]", "/BBox [0 0 20 20]"));

        Assert.Throws<FormatException>(() => new PdfPageRenderer(document)
            .Render(0, new PdfRenderOptions(100, 100)));
    }

    private static byte[] CreatePdf(string malformedRect, string malformedAppearanceGeometry)
    {
        string badPaint = "1 0 0 rg 0 0 20 20 re f";
        string goodPaint = "1 0 0 rg 0 0 24 30 re f 0 0 1 rg 36 10 24 30 re f";
        string pagePaint = "0 1 0 rg 2 2 10 10 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 8 0 R /Annots [4 0 R 6 0 R] >>",
            $"<< /Type /Annot /Subtype /Square {malformedRect} /AP << /N 5 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form {malformedAppearanceGeometry} /Resources << >> /Length {Encoding.ASCII.GetByteCount(badPaint)} >>\nstream\n{badPaint}\nendstream",
            "<< /Type /Annot /Subtype /Square /Rect [20 20 80 60] /AP << /N 7 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 60 40] /Resources << >> /Length {Encoding.ASCII.GetByteCount(goodPaint)} >>\nstream\n{goodPaint}\nendstream",
            $"<< /Length {Encoding.ASCII.GetByteCount(pagePaint)} >>\nstream\n{pagePaint}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (int index in Enumerable.Range(0, objects.Length))
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // An ink annotation without an appearance is drawn from its InkList. Damaged coordinates cost only that
    // annotation in recovery, as malformed geometry does, and strict rendering still rejects them.
    [Theory]
    [InlineData("[[20 20 80]]")]
    [InlineData("[[20 20 /bad 80]]")]
    [InlineData("[5]")]
    public void RecoverySkipsAnInkAnnotationWithDamagedCoordinatesAndRendersTheNeighbor(string inkList)
    {
        byte[] pdf = CreateInkPdf(inkList);

        PdfRenderedPage result = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(pdf))
            .Render(0, new PdfRenderOptions(100, 100));

        Assert.Contains("An invalid annotation was skipped.", result.Diagnostics);
        Assert.Contains(Pixels(result.Pixels.ToArray()), pixel => pixel.SequenceEqual(new byte[] { 255, 0, 0, 255 }));
        Assert.Throws<FormatException>(() => new PdfPageRenderer(PdfDocument.Open(pdf)).Render(0, new PdfRenderOptions(100, 100)));
    }

    private static byte[] CreateInkPdf(string inkList)
    {
        string goodPaint = "0 0 1 rg 0 0 40 40 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R 5 0 R] >>",
            $"<< /Type /Annot /Subtype /Ink /Rect [10 10 90 90] /C [1 0 0] /InkList {inkList} >>",
            "<< /Type /Annot /Subtype /Square /Rect [50 50 90 90] /AP << /N 6 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Resources << >> /Length {Encoding.ASCII.GetByteCount(goodPaint)} >>\nstream\n{goodPaint}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (int index in Enumerable.Range(0, objects.Length))
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static IEnumerable<byte[]> Pixels(byte[] pixels)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
            yield return pixels.AsSpan(offset, 4).ToArray();
    }
}
