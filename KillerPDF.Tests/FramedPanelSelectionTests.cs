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

    // A dense grid of rectangles that each hold one short word. The panel pass used to test every rectangle
    // against every word, which froze the first Select-tool hover on such a page.
    private static string GridPage(int cells, string paint)
    {
        var content = new StringBuilder();
        for (int row = 0; row < cells; row++)
            for (int column = 0; column < cells; column++)
            {
                int x = column * 12 + 2, y = row * 12 + 2;
                content.Append($"{x} {y} 10 10 re {paint} BT /F1 4 Tf 1 0 0 1 {x + 1} {y + 3} Tm (w) Tj ET\n");
            }
        string body = content.ToString();
        int size = cells * 12 + 4;
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {size} {size}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {body.Length} >>\nstream\n{body}\nendstream"
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
        return pdf.ToString();
    }

    [Fact]
    public void AGridTooLargeForPanelDetectionIsReadAsOnePanelWithoutPairwiseWork()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-panels-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(GridPage(150, "f")));
            TextRunService.PanelWork = 0;
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal(150, runs.Lines.Count);
            Assert.Equal(150 * 150, runs.Chars.Count);
            // 22,500 rectangles over 22,500 words is 506 million pairs when each is tested; the budget is 5 million.
            Assert.True(TextRunService.PanelWork <= 5_000_000, $"panel detection did {TextRunService.PanelWork} tests");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ABudgetSizedGridOfFramedWordsFindsEachPanelByLookingOnlyNearEachRectangle()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-panels-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(GridPage(40, "S")));
            TextRunService.PanelWork = 0;
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            // Each stroked cell is its own framed panel, so no two cells share a line: 1,600 words, 1,600 lines.
            Assert.Equal(1600, runs.Lines.Count);
            // Testing every rectangle against every word is 2.56 million pairs; a nearby lookup is a few per rectangle.
            Assert.True(TextRunService.PanelWork <= 200_000, $"panel detection did {TextRunService.PanelWork} tests");
        }
        finally { File.Delete(path); }
    }

    // Many words stacked on one spot, so every word's box covers the whole word grid, plus one framing rectangle.
    // Each word used to be added to every cell, so building the grid cost words x cells list inserts, which the
    // panel budget never saw, on the thread that handles a Select-tool hover.
    [Fact]
    public void WordsStackedOnOneSpotDoNotMakeTheWordGridDoUnboundedWork()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-panels-{Guid.NewGuid():N}.pdf");
        try
        {
            var body = new StringBuilder("0.1 w 5 5 290 290 re S\n");
            for (int word = 0; word < 6000; word++)
                body.Append(FormattableString.Invariant($"BT /F1 80 Tf 1 0 0 1 {20 + word % 40 * 0.5} {80 + word / 40 * 0.5} Tm (w ) Tj ET\n"));
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(OnePage(body.ToString(), 300)));
            TextRunService.PanelWork = 0;
            TextRunService.GridInsertions = 0;
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.True(runs.Chars.Count >= 6000);
            // 6,000 words on a 77 x 77 grid, most boxes covering most cells, is about 10 million inserts; the budget is 5 million.
            Assert.True(TextRunService.GridInsertions <= TextRunService.PanelWorkBudget,
                $"the word grid made {TextRunService.GridInsertions} inserts");
            Assert.True(TextRunService.PanelWork <= TextRunService.PanelWorkBudget,
                $"panel detection counted {TextRunService.PanelWork} tests");
        }
        finally { File.Delete(path); }
    }

    private static string OnePage(string body, int size)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {size} {size}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {body.Length} >>\nstream\n{body}\nendstream"
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
        return pdf.ToString();
    }
}
