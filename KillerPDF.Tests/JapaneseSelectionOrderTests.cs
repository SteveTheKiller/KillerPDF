using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class JapaneseSelectionOrderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RotatedLinesKeepParagraphOrderWhenTheirLengthsDiffer(bool downward, bool reverseDrawingOrder)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-rotated-paragraph-{Guid.NewGuid():N}.pdf");
        try
        {
            string first = downward ? "0 -1 1 0 120 350" : "0 1 -1 0 100 40";
            string second = downward ? "0 -1 1 0 100 350" : "0 1 -1 0 120 40";
            string shortLine = $"BT /F1 12 Tf {first} Tm (AB) Tj ET";
            string longLine = $"BT /F1 12 Tf {second} Tm (ABCDEFGH) Tj ET";
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", first,
                reverseDrawingOrder ? longLine + " " + shortLine : shortLine + " " + longLine,
                omitInitialLine: true));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal("AB\nAB CD EF", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
            Assert.Equal("AB\nAB CD EF", PageReadingText.Build(runs.Chars.Select(c => (c.Value, c.Word, c.Line)).ToArray()).Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AHorizontalFooterFollowsVerticalBodyText()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-mixed-order-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "1 0 0 1 40 340",
                "BT /F1 12 Tf 0 1 -1 0 100 150 Tm (AB) Tj ET "
                + "BT /F1 12 Tf 0 1 -1 0 120 150 Tm (DE) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 40 40 Tm (GH) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            const string expected = "AB CD EF\nAB\nCD\nEF";
            Assert.Equal(expected, TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
            Assert.Equal(expected, PageReadingText.Build(runs.Chars.Select(c => (c.Value, c.Word, c.Line)).ToArray()).Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HorizontalTextBetweenVerticalSectionsKeepsItsPlace()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-mixed-sections-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "0 1 -1 0 100 300",
                "BT /F1 12 Tf 1 0 0 1 40 220 Tm (GH) Tj ET "
                + "BT /F1 12 Tf 0 1 -1 0 100 60 Tm (DE) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal("AB CD EF\nEF\nCD", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AWordDrawnBetweenTwoRunsJoinsThemIntoOneVerticalLine()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-vertical-bridge-{Guid.NewGuid():N}.pdf");
        try
        {
            // Far ends first, the bridging word last, as an out-of-order content stream draws them.
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "0 1 -1 0 100 40",
                "BT /F1 12 Tf 0 1 -1 0 100 150 Tm (GH) Tj ET "
                + "BT /F1 12 Tf 0 1 -1 0 100 64 Tm (ABCDEFGH) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Single(runs.Lines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TwoVerticalSectionsKeepTheirPlaceOnASingleColumnPage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-two-sections-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "1 0 0 1 40 340",
                "BT /F1 12 Tf 0 1 -1 0 100 260 Tm (AB) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 40 200 Tm (DE) Tj ET "
                + "BT /F1 12 Tf 0 1 -1 0 100 120 Tm (GH) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 40 60 Tm (DE) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal("AB CD EF\nAB\nCD\nEF\nCD",
                TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AWideFooterStaysAfterAVerticalSectionBesideALaterColumn()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-column-footer-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "1 0 0 1 40 340",
                "BT /F1 12 Tf 1 0 0 1 40 320 Tm (DE) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 240 340 Tm (GH) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 240 200 Tm (AB) Tj ET "
                + "BT /F1 12 Tf 0 1 -1 0 150 240 Tm (DE) Tj ET "
                + "BT /F1 40 Tf 1 0 0 1 40 60 Tm (ABCDEFGH) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            string text = TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _);
            Assert.EndsWith("AB\nCD\nAB CD EF", text, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnIndentedFirstLineDoesNotEndAColumn()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-column-indent-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "1 0 0 1 40 340",
                "BT /F1 12 Tf 1 0 0 1 40 320 Tm (DE) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 246 340 Tm (GH) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 240 200 Tm (AB) Tj ET "
                + "BT /F1 12 Tf 0 1 -1 0 150 240 Tm (DE) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal("AB CD EF\nCD\nEF\nAB\nCD",
                TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AVerticalSectionDoesNotSplitAColumn()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-vertical-column-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "1 0 0 1 40 340",
                "BT /F1 12 Tf 1 0 0 1 40 320 Tm (DE) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 240 340 Tm (GH) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 240 200 Tm (AB) Tj ET "
                + "BT /F1 12 Tf 0 1 -1 0 150 240 Tm (DE) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            string text = TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _);
            Assert.Equal("AB CD EF\nCD\nEF\nAB\nCD", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HorizontalColumnsRetainTheirReadingOrderAndHitTargets()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-column-control-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "1 0 0 1 40 300",
                "BT /F1 12 Tf 1 0 0 1 40 280 Tm (DE) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 240 300 Tm (GH) Tj ET "
                + "BT /F1 12 Tf 1 0 0 1 240 280 Tm (AB) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal("AB CD EF\nCD\nEF\nAB", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
            var range = Assert.IsType<ValueTuple<int, int>>(TextRunService.WordRangeFromPoint(runs, 242, 303));
            Assert.Equal("EF", TextRunService.TextForRange(runs, range.Item1, range.Item2, out _));
            Assert.Equal(range.Item1, TextRunService.CaretFromPoint(runs, 242, 303));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0 1 -1 0 200 40", 196, 58)]
    [InlineData("0 -1 1 0 100 350", 104, 332)]
    [InlineData("1 0 0 1 40 75", 58, 79)]
    public void DistinctAndCrossingBaselinesStaySeparate(string secondMatrix, double x, double y)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-baselines-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdfWithContent("AB CD EF", "0 1 -1 0 100 40",
                $"BT /F1 12 Tf {secondMatrix} Tm (ABCDEFGH) Tj ET"));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal("AB CD EF\nAB CD EF", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out int words));
            Assert.Equal(6, words);
            Assert.Equal(2, runs.Lines.Count);
            var word = Assert.IsType<ValueTuple<int, int>>(TextRunService.WordRangeFromPoint(runs, x, y));
            Assert.Equal("AB", TextRunService.TextForRange(runs, word.Item1, word.Item2, out _));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0 1 -1 0 100 40", 55, 57, 72, 75)]
    [InlineData("0 -1 1 0 100 350", 315, 318, 333, 335)]
    public void VerticalSelectionBoundsCoverOnlyTheSelectedGlyphInterval(string matrix,
        double minBottom, double maxBottom, double minTop, double maxTop)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-selection-bounds-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdf("ABCDEFGH", matrix));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            var bounds = Assert.Single(TextRunService.SelectionBounds(runs, 2, 4));
            Assert.InRange(bounds.Bottom, minBottom, maxBottom);
            Assert.InRange(bounds.Top, minTop, maxTop);
            Assert.InRange(bounds.Height, 15, 20);
            Assert.Empty(TextRunService.SelectionBounds(runs, 4, 4));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0 1 -1 0 100 40", 96, 75)]
    [InlineData("0 -1 1 0 100 350", 104, 315)]
    public void VerticalWordHitTestingSelectsTheWordAtTheClickedHeight(string matrix, double x, double y)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-word-hit-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdf("AB CD EF", matrix));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            var range = Assert.IsType<ValueTuple<int, int>>(TextRunService.WordRangeFromPoint(runs, x, y));
            Assert.Equal((2, 4), range);
            Assert.Equal("CD", TextRunService.TextForRange(runs, range.Item1, range.Item2, out _));
            Assert.Null(TextRunService.WordRangeFromPoint(runs, 200, y));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0 1 -1 0 100 40", 96, 40, 1)]
    [InlineData("0 -1 1 0 100 350", 104, 350, -1)]
    public void VerticalDragResolvesDistinctCaretsAndSubstrings(string matrix, double x, double origin, int sign)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-carets-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdf("ABCDEFGH", matrix));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal(0, TextRunService.CaretFromPoint(runs, x, origin - sign * 20));
            Assert.Equal(0, TextRunService.CaretFromPoint(runs, x, origin + sign * 2));
            Assert.Equal(1, TextRunService.CaretFromPoint(runs, x, origin + sign * 9));
            int start = TextRunService.CaretFromPoint(runs, x, origin + sign * 18);
            int end = TextRunService.CaretFromPoint(runs, x, origin + sign * 35);
            Assert.Equal(2, start);
            Assert.Equal(4, end);
            Assert.Equal("CD", TextRunService.TextForRange(runs, Math.Min(start, end), Math.Max(start, end), out _));
            int reverseAnchor = TextRunService.CaretFromPoint(runs, x, origin + sign * 35);
            int reverseFocus = TextRunService.CaretFromPoint(runs, x, origin + sign * 18);
            Assert.Equal((4, 2), (reverseAnchor, reverseFocus));
            Assert.Equal("CD", TextRunService.TextForRange(runs, reverseFocus, reverseAnchor, out _));
            Assert.Equal(8, TextRunService.CaretFromPoint(runs, x, origin + sign * 100));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0 1 -1 0 100 40")]
    [InlineData("0 -1 1 0 100 350")]
    [InlineData("1 0 0 1 40 100")]
    public void SelectionKeepsWordsOnOneTransformedBaselineTogether(string matrix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-words-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdf("AB CD EF", matrix));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal("AB CD EF", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out int words));
            Assert.Equal(3, words);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0 1 -1 0 100 40")]
    [InlineData("0 -1 1 0 100 350")]
    [InlineData("1 0 0 1 40 100")]
    public void SelectionPreservesJapanesePhraseAlongTheTransformedBaseline(string matrix)
    {
        const string phrase = "「今の時間帯は予約のみです」等で並";
        string path = Path.Combine(Path.GetTempPath(), $"kp-japanese-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PhrasePdf(phrase, matrix));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal(phrase, TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        }
        finally { File.Delete(path); }
    }

    // Varying Latin glyph outlines mapped to Japanese reproduce the original PDF's uneven
    // transverse ink bounds without depending on fonts installed on the test machine.
    private static byte[] PhrasePdf(string phrase, string matrix) => PhrasePdfWithContent(phrase, matrix, "");

    private static byte[] PhrasePdfWithContent(string phrase, string matrix, string extraContent, bool omitInitialLine = false)
    {
        string codes = string.Concat(Enumerable.Range(0, phrase.Length).Select(i => ((char)('A' + i)).ToString()));
        string map = "1 begincodespacerange <00> <FF> endcodespacerange\n"
            + phrase.Length + " beginbfchar\n"
            + string.Join("\n", phrase.Select((c, i) => $"<{i + 65:X2}> <{(int)c:X4}>"))
            + "\nendbfchar";
        string content = omitInitialLine ? extraContent : $"BT /F1 12 Tf {matrix} Tm ({codes}) Tj ET {extraContent}";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Length {map.Length} >>\nstream\n{map}\nendstream"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
