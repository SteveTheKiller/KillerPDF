using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class VerticalReadingGeometryTests
{
    [Theory]
    [InlineData("Highlight", false)]
    [InlineData("Underline", false)]
    [InlineData("StrikeOut", false)]
    [InlineData("Squiggly", false)]
    [InlineData("Highlight", true)]
    [InlineData("Underline", true)]
    [InlineData("StrikeOut", true)]
    [InlineData("Squiggly", true)]
    public void BareMarkupRetainsItsWordAfterSaveAndReloadWithCropTranslation(string subtype, bool cropped)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-marked-reload-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = FixturePdf(annotation: $"<< /Type /Annot /Subtype /{subtype} /Rect [80 59 120 77] >>",
                cropBox: cropped ? "/CropBox [20 30 400 400]" : "");
            File.WriteAllBytes(path, PdfDocumentWriter.Write(PdfDocument.Open(source)));
            var document = PdfDocument.Open(File.ReadAllBytes(path));
            var comment = Assert.Single(PdfCommentReader.Read(document, includeMarkupWithoutText: true));
            var page = Assert.Single(PdfPageInformation.Read(document));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            Assert.Equal("Marked: CD", ReadingAnnotationText.Describe(comment, () => runs,
                (page.Left, page.Bottom), Localize));
            Assert.Equal("AB CD EF", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
            var body = PageReadingText.Build(runs.Chars.Select(c => (c.Value, c.Word, c.Line)).ToArray());
            Assert.Equal("AB CD EF", body.Text);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("", "Comment: Saved comment")]
    [InlineData("/T ( Reviewer )", "Comment by Reviewer: Saved comment")]
    public void SavedContentsOverrideMarkupGeometryWithoutLoadingPageText(string author, string expected)
    {
        var document = PdfDocument.Open(FixturePdf(annotation:
            $"<< /Type /Annot /Subtype /Highlight /Rect [80 59 120 77] /Contents ( Saved comment ) {author} >>"));
        var comment = Assert.Single(PdfCommentReader.Read(document, includeMarkupWithoutText: true));
        Assert.Equal(expected, ReadingAnnotationText.Describe(comment,
            () => throw new InvalidOperationException("Page text is unavailable"), (0, 0), Localize));
    }

    [Theory]
    [InlineData(true, double.NaN, double.NaN)]
    [InlineData(true, double.NegativeInfinity, 20)]
    [InlineData(true, 30, 20)]
    [InlineData(false, 100, 200)]
    public void LegacyVerticalAndHorizontalRunsRetainTheLineBand(bool vertical, double bottom, double top)
    {
        var runs = new PageTextRuns { PdfWidth = 100, PdfHeight = 100 };
        runs.Lines.Add(new RunLine { Start = 0, Count = 1, Left = 10, Right = 20, Bottom = 10, Top = 40,
            WritingDirection = vertical ? PdfWritingDirection.BottomToTop : PdfWritingDirection.LeftToRight });
        runs.Chars.Add(new RunChar("A", 10, 20, 0, 0, bottom: bottom, top: top));
        Assert.Equal(new[] { (0, 1) }, VisibleTextGeometry.VisibleCarets(runs, 100, 100, 0, p => p,
            new Rect(10, 82, 10, 3)));
        var comment = new PdfCommentInfo { PageIndex = 0, AnnotationIndex = 0, Subtype = "Highlight", Contents = "",
            Bounds = new PdfContentBounds(10, 20, 20, 30) };
        Assert.Equal("Marked: A", ReadingAnnotationText.Describe(comment, () => runs, (0, 0), Localize));
    }

    [Theory]
    [InlineData(false, 40, 56, "AB")]
    [InlineData(false, 59, 77, "CD")]
    [InlineData(true, 334, 350, "AB")]
    [InlineData(true, 313, 331, "CD")]
    public void SavedBareMarkupDescribesOnlyTheCoveredVerticalWord(bool downward, int bottom, int top, string expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-marked-vertical-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, FixturePdf(downward,
                $"<< /Type /Annot /Subtype /Highlight /Rect [80 {bottom} 120 {top}] >>"));
            var document = PdfDocument.Open(File.ReadAllBytes(path));
            var comment = Assert.Single(PdfCommentReader.Read(document, includeMarkupWithoutText: true));
            var service = new TextRunService();
            Assert.Equal("Marked: " + expected, ReadingAnnotationText.Describe(comment,
                () => service.GetPage(path, null, 0), (0, 0), Localize));
        }
        finally { File.Delete(path); }
    }

    private static string Localize(string key) => key switch
    {
        "Str_Read_MarkedText" => "Marked: {0}",
        "Str_Read_MarkedNoText" => "Marked without text",
        "Str_Read_Comment" => "Comment: {0}",
        "Str_Read_CommentBy" => "Comment by {0}: {1}",
        _ => key
    };

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 90)]
    [InlineData(false, 180)]
    [InlineData(false, 270)]
    [InlineData(true, 0)]
    [InlineData(true, 90)]
    [InlineData(true, 180)]
    [InlineData(true, 270)]
    public void VisibilityKeepsOnlyTheOnscreenVerticalWord(bool downward, int rotation)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-visible-vertical-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, FixturePdf(downward));
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, null, 0));
            // Helvetica at 12 points advances A and B by 8.004 each. These viewport edges
            // surround those two glyphs and stop before the next word after its space.
            double bottom = downward ? 334 : 40;
            double top = downward ? 350 : 56;
            Rect viewport = rotation switch
            {
                90 => new Rect(bottom, 80, top - bottom, 40),
                180 => new Rect(280, bottom, 40, top - bottom),
                270 => new Rect(400 - top, 280, top - bottom, 40),
                _ => new Rect(80, 400 - top, 40, top - bottom)
            };
            var visible = VisibleTextGeometry.VisibleCarets(runs, 400, 400, rotation, p => p, viewport);
            Assert.Equal(new[] { (0, 2) }, visible);
            Assert.Equal("AB", TextRunService.TextForRange(runs, visible[0].Start, visible[0].End, out _));
            Assert.Equal(new[] { (0, 6) }, VisibleTextGeometry.VisibleCarets(runs, 400, 400, rotation,
                p => p, new Rect(0, 0, 400, 400)));
            // The space occupies Y 56.008..59.344 upward or 330.656..333.992 downward.
            double gapBottom = downward ? 332 : 57;
            Rect gapViewport = rotation switch
            {
                90 => new Rect(gapBottom, 80, 1, 40),
                180 => new Rect(280, gapBottom, 40, 1),
                270 => new Rect(399 - gapBottom, 280, 1, 40),
                _ => new Rect(80, 399 - gapBottom, 40, 1)
            };
            Assert.Empty(VisibleTextGeometry.VisibleCarets(runs, 400, 400, rotation, p => p, gapViewport));
        }
        finally { File.Delete(path); }
    }

    internal static byte[] FixturePdf(bool downward = false, string annotation = "", string cropBox = "")
    {
        string content = $"BT /F1 12 Tf {(downward ? "0 -1 1 0 100 350" : "0 1 -1 0 100 40")} Tm (AB CD EF) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] {cropBox} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R {(annotation.Length == 0 ? "" : "/Annots [6 0 R]")} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            annotation.Length == 0 ? "null" : annotation
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
