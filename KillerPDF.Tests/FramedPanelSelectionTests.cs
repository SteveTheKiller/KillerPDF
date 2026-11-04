using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FramedPanelSelectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void CopyKeepsFramedParagraphsSeparateWithoutSplittingWords(int layout)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-panels-{Guid.NewGuid():N}.pdf");
        try
        {
            string content = "0.1 w 28 95 64 60 re S 48 30 18 50 re S "
                + "BT /F1 12 Tf 0 1 -1 0 40 100 Tm (FIRST) Tj "
                + "0 1 -1 0 60 100 Tm (MIDDLE) Tj "
                + (layout == 1 ? "0 1 -1 0 50 35 Tm (STRAY) Tj 0 1 -1 0 110 35 Tm (TALLANNOTATION) Tj ET"
                    : "0 1 -1 0 60 35 Tm (STRAY) Tj ET");
            if (layout == 2)
                content = "0.1 w 28 95 61 60 re S 90 130 50 20 re S "
                    + "BT /F1 12 Tf 1 0 0 1 40 140 Tm (FIRST) Tj "
                    + "1 0 0 1 40 120 Tm (MIDDLE) Tj 1 0 0 1 92 140 Tm (STRAY) Tj ET";
            if (layout == 3)
                content = "28 95 52 60 re f BT /F1 12 Tf "
                    + "1 0 0 1 40 140 Tm (FIRST) Tj 1 0 0 1 78 140 Tm (MIDDLE) Tj "
                    + "1 0 0 1 40 120 Tm (NEXT) Tj ET";
            if (layout == 4)
                content = "28 95 117 60 re S 80 95 90 60 re S BT /F1 12 Tf "
                    + "1 0 0 1 40 140 Tm (FIRST) Tj 1 0 0 1 90 140 Tm (MIDDLE) Tj "
                    + "1 0 0 1 40 120 Tm (NEXT) Tj ET";
            if (layout == 5)
                content = "28 95 90 70 re S BT /F1 24 Tf 1 0 0 1 40 140 Tm (FIRST) Tj "
                    + "/F1 12 Tf 1 0 0 1 40 100 Tm (MIDDLE) Tj "
                    + "/F1 6 Tf 1 0 0 1 0 120 Tm (STRAY) Tj ET";
            if (layout == 6) content = "2 2 196 196 re S " + content;
            string[] objects =
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
                $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
            ];
            var pdf = new StringBuilder("%PDF-1.7\n");
            var offsets = new List<int>();
            for (int index = 0; index < objects.Length; index++)
            {
                offsets.Add(pdf.Length);
                pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
            }
            int xref = pdf.Length;
            pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
            pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
            PdfPageContent page = new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path))).Read(0);
            PdfExtractedWord first = Assert.Single(page.Words, word => word.Text == "FIRST");
            PdfExtractedWord last = Assert.Single(page.Words, word => word.Text == "MIDDLE");
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            var start = first.Letters[0].StartBaseLine;
            var end = last.Letters[^1].EndBaseLine;
            int anchor = TextRunService.CaretFromPoint(runs, start.X, start.Y);
            int focus = TextRunService.CaretFromPoint(runs, end.X, end.Y);
            string copied = TextRunService.TextForRange(runs, anchor, focus, out _);
            Assert.Equal(layout is 3 or 4 ? "FIRST MIDDLE" : "FIRST\nMIDDLE", copied);
            string whole = TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _);
            Assert.Contains(layout is 3 or 4 ? "NEXT" : "STRAY", whole);
            if (layout == 1) Assert.Contains("TALLANNOTATION", whole);
        }
        finally { File.Delete(path); }
    }
}
