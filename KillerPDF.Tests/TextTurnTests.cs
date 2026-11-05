using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using PdfRenderedPage = KillerPdf.Engine.Rendering.PdfRenderedPage;
using Colors = System.Windows.Media.Colors;
using Xunit;

namespace KillerPDF.Tests;

// In-place edits of text that reads sideways or upside down on screen: the line is found in the
// text's reading frame, the replacement box opens turned to match, the burn draws it the same way,
// and a later page turn keeps it on its cover. Checked against the engine's text extraction and
// renderer, like RedactionGeometryTests.
public sealed class TextTurnTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void ReadingFrameRoundTripsPointsAndRectangles(int turn)
    {
        var point = new Point(37, 52);
        var rect = new Rect(37, 52, 120, 33);

        Point back = TextTurn.FromReading(TextTurn.ToReading(point, turn), turn);
        Rect backRect = TextTurn.FromReading(TextTurn.ToReading(rect, turn), turn);

        Assert.Equal(point.X, back.X, 9);
        Assert.Equal(point.Y, back.Y, 9);
        Assert.Equal(rect, backRect);
    }

    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(0, 10, 90)]
    [InlineData(-10, 0, 180)]
    [InlineData(0, -10, 270)]
    [InlineData(10, 3, 0)]
    [InlineData(0, 0, 0)]
    public void BaselineDirectionSnapsToAQuarterTurn(double dx, double dy, int turn)
    {
        Assert.Equal(turn, TextTurn.FromBaseline(new Point(50, 50), new Point(50 + dx, 50 + dy)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TurnedTextBoxCornersMapBackToTheSourceGlyphs(int rotation)
    {
        // Level text on a page turned by 'rotation' reads turned by the same amount on screen.
        PdfDocument document = PdfDocument.Open(Pdf(rotation, "BT /F1 24 Tf 40 80 Td (Turned) Tj ET",
            "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>"));
        bool quarter = rotation is 90 or 270;
        double canvasWidth = quarter ? 400 : 600, canvasHeight = quarter ? 600 : 400;
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        var word = page.Words.Single(w => w.Text == "Turned");
        var first = word.Letters[0];
        Point Canvas(double x, double y) =>
            RedactionGeometry.PageToCanvas(x, y, x, y, 300, 200, canvasWidth, canvasHeight, rotation).TopLeft;

        int turn = TextTurn.FromBaseline(Canvas(first.StartBaseLine.X, first.StartBaseLine.Y),
            Canvas(first.EndBaseLine.X, first.EndBaseLine.Y));
        var b = word.BoundingBox;
        Rect glyphs = RedactionGeometry.PageToCanvas(b.Left, b.Bottom, b.Right, b.Top,
            300, 200, canvasWidth, canvasHeight, rotation);
        Rect reading = TextTurn.ToReading(glyphs, turn);

        // A box opened at the line's reading-frame top-left, as wide as the line and as tall as it,
        // sits on the canvas exactly over the glyphs it replaces.
        Point origin = TextTurn.FromReading(reading.TopLeft, turn);
        Point topLeft = origin - TextTurn.OriginOffset(turn, reading.Width, reading.Height);
        var box = new Rect(topLeft, TextTurn.Footprint(turn, reading.Width, reading.Height));

        Assert.Equal(rotation, turn);
        Assert.Equal(glyphs.X, box.X, 6);
        Assert.Equal(glyphs.Y, box.Y, 6);
        Assert.Equal(glyphs.Width, box.Width, 6);
        Assert.Equal(glyphs.Height, box.Height, 6);
        // The text reads left to right in its reading frame.
        Point start = TextTurn.ToReading(Canvas(first.StartBaseLine.X, first.StartBaseLine.Y), turn);
        Point end = TextTurn.ToReading(Canvas(first.EndBaseLine.X, first.EndBaseLine.Y), turn);
        Assert.True(end.X > start.X);
        Assert.Equal(start.Y, end.Y, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void BurnedTurnedTextLandsWhereTheCanvasShowsIt(int turn)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-turned-text-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(300, 300).Build());
            var text = new TextAnnotation
            {
                PageIndex = 0, Position = new Point(70, 60), Width = 160, Height = 34, Rotation = turn,
                Content = "MMMM", FontName = "Segoe UI", FontSize = 20
            };
            Rect footprint = new(text.Position, text.FootprintSize);
            var annotations = new Dictionary<int, List<PageAnnotation>> { [0] = [text] };
            PdfEngineBurn.Burn(path, annotations, new Dictionary<int, (int w, int h)> { [0] = (300, 300) });

            PdfDocument burned = PdfDocument.Open(File.ReadAllBytes(path));
            PdfRenderedPage page = new PdfPageRenderer(burned).Render(0, new PdfRenderOptions(300, 300));
            Rect ink = InkBounds(page);

            // Every glyph pixel sits inside the box shown on the canvas...
            Assert.False(ink.IsEmpty);
            Assert.True(ink.Left >= footprint.Left - 1 && ink.Right <= footprint.Right + 1
                && ink.Top >= footprint.Top - 1 && ink.Bottom <= footprint.Bottom + 1,
                $"ink {ink} outside box {footprint}");
            // ...starting at the box's frame origin and reading in the box's direction.
            Point origin = text.Position + TextTurn.OriginOffset(turn, text.Width, text.Height);
            Rect readingInk = TextTurn.ToReading(ink, turn);
            Point readingOrigin = TextTurn.ToReading(origin, turn);
            Assert.InRange(readingInk.Left - readingOrigin.X, 0, 8);
            Assert.InRange(readingInk.Top - readingOrigin.Y, 0, 14);
            Assert.True(readingInk.Width > readingInk.Height, "the text runs along its reading direction");

            TextAnnotation savedBox = Assert.IsType<TextAnnotation>(Assert.Single(PdfOverlayAnnotations.Read(burned)).Annotation);
            Assert.Equal(turn, savedBox.Rotation);
            Assert.Equal("MMMM", savedBox.Content);
            Assert.Equal(1, SearchService.Search(path, "MMMM").TotalHits);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(0, 270)]
    [InlineData(90, 90)]
    [InlineData(270, 180)]
    public void EditTextTurnsWithItsCoverWhenThePageTurns(int startTurn, int delta)
    {
        const double W = 800, H = 600;
        var edit = new TextAnnotation
        {
            PairId = "edit", Rotation = startTurn, Width = 180, Height = 40, Position = new Point(105, 55)
        };
        // The cover sits under the edit with a small margin on every side, as an in-place edit makes it.
        Size footprint = edit.FootprintSize;
        var cover = new CoverAnnotation
        {
            PairId = "edit", Bounds = new Rect(100, 50, footprint.Width + 10, footprint.Height + 10)
        };
        Point origin = edit.Position + TextTurn.OriginOffset(startTurn, edit.Width, edit.Height);
        var free = new TextAnnotation { Position = new Point(400, 300), Width = 120, Height = 30 };

        AnnotationRotate.Remap([cover, edit, free], delta, W, H);

        Rect turned = new(edit.Position, edit.FootprintSize);
        Assert.Equal(TextTurn.Normalize(startTurn + delta), edit.Rotation);
        Assert.True(cover.Bounds.Contains(turned), $"edit {turned} left cover {cover.Bounds}");
        // The first line still starts at the same spot on the page.
        Point mappedOrigin = edit.Position + TextTurn.OriginOffset(edit.Rotation, edit.Width, edit.Height);
        Point expected = TextTurn.Normalize(delta) switch
        {
            90 => new Point(H - origin.Y, origin.X),
            270 => new Point(origin.Y, W - origin.X),
            _ => new Point(W - origin.X, H - origin.Y)
        };
        Assert.Equal(expected.X, mappedOrigin.X, 6);
        Assert.Equal(expected.Y, mappedOrigin.Y, 6);
        // Free text keeps reading level.
        Assert.Equal(0, free.Rotation);
    }

    [Fact]
    public void EditTextTurnThenCounterTurnIsIdentity()
    {
        var cover = new CoverAnnotation { PairId = "edit", Bounds = new Rect(100, 50, 60, 200) };
        var edit = new TextAnnotation { PairId = "edit", Position = new Point(110, 60), Width = 180, Height = 40, Rotation = 90 };

        AnnotationRotate.Remap([cover, edit], 90, 800, 600);
        AnnotationRotate.Remap([cover, edit], -90, 600, 800);

        Assert.Equal(90, edit.Rotation);
        Assert.Equal(110, edit.Position.X, 6);
        Assert.Equal(60, edit.Position.Y, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void SavedEditsLeaveOnlyTheirReplacementText(int rotation)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-replaced-text-{Guid.NewGuid():N}.pdf");
        bool quarter = rotation is 90 or 270;
        int canvasWidth = quarter ? 400 : 600, canvasHeight = quarter ? 600 : 400;
        try
        {
            File.WriteAllBytes(path, Pdf(rotation,
                "BT /F1 24 Tf 40 80 Td (Turned) Tj ET BT /F1 12 Tf 40 30 Td (Footer) Tj ET",
                "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>"));

            // The first save replaces page text; the second replaces the first edit's own text, which
            // the first save drew inside an overlay form.
            Edit("Turned", "Fresh");
            string first = Letters();
            Assert.DoesNotContain("Turned", first);
            Assert.Contains("Fresh", first);
            Assert.Contains("Footer", first);

            Edit("Fresh", "Again");
            string second = Letters();
            Assert.DoesNotContain("Fresh", second);
            Assert.Contains("Again", second);
            Assert.Contains("Footer", second);
        }
        finally { File.Delete(path); }

        string Letters() => string.Concat(new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path)))
            .Read(0).Letters.Select(letter => letter.Value));

        void Edit(string word, string replacement)
        {
            var b = new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path))).Read(0).Words
                .Single(w => w.Text == word).BoundingBox;
            Rect line = RedactionGeometry.PageToCanvas(b.Left, b.Bottom, b.Right, b.Top,
                300, 200, canvasWidth, canvasHeight, rotation);
            // Level text on a turned page reads turned by the page's rotation.
            int turn = rotation;
            var cover = new CoverAnnotation
            {
                PageIndex = 0, PairId = word, Bounds = new Rect(line.X - 3, line.Y - 3, line.Width + 6, line.Height + 6)
            };
            cover.SetColor(Colors.White);
            Rect reading = TextTurn.ToReading(line, turn);
            var text = new TextAnnotation
            {
                PageIndex = 0, PairId = word, Rotation = turn, Width = reading.Width + 40,
                Height = Math.Max(reading.Height, 48),
                Content = replacement, FontName = "Segoe UI", FontSize = 20
            };
            text.Position = TextTurn.FromReading(reading.TopLeft, turn)
                - TextTurn.OriginOffset(turn, text.Width, text.Height);
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [cover, text] },
                new Dictionary<int, (int w, int h)> { [0] = (canvasWidth, canvasHeight) });
        }
    }

    private static Rect InkBounds(PdfRenderedPage page)
    {
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
            {
                int offset = (y * page.Width + x) * 4;
                if (pixels[offset] >= 128 || pixels[offset + 1] >= 128 || pixels[offset + 2] >= 128) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? Rect.Empty : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static byte[] Pdf(int rotation, string content, string resources = "")
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Rotate {rotation} {resources} /Contents 4 0 R >>",
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
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
