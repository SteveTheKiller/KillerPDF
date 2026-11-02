using System.IO;
using System.Windows;
using System.Windows.Media;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The app's own OCR on a real scan. Its words' boxes follow their letters, and the scanned line
/// tilts a little, so size and height on the page come from the page's ink, never from the OCR layer.
/// </summary>
public sealed class ScanLineMeasureTests
{
    private const double ScreenPixelsPerPoint = 1.26;
    private const double FontBaseline = 0.89;

    private static string Fixture => Path.Combine(FindRepositoryRoot(), "KillerPDF.Tests", "TestData", "scan-realocr.pdf");

    [Fact]
    public void ALineMeasuresTheSameAtALowZoom()
    {
        ScanLine line = FirstLine();

        ScanLineFit fit = Measure(line);

        // The scan was set at 12 points.
        Assert.InRange(fit.FontSize / ScreenPixelsPerPoint, 11.3, 12.4);
    }

    [Fact]
    public void TheEditorSitsOnTheLineTheInkShows()
    {
        ScanLineFit fit = Measure(FirstLine());
        // What the box takes from the OCR layer: the first word's letter baseline, at the letter's size.
        double letterBaseline = (792 - 638.90) * ScreenPixelsPerPoint;
        double fromOcr = letterBaseline - FontBaseline * 8.2 * ScreenPixelsPerPoint - 1;

        double top = ScanLineEdit.EditorTop(fit, FontBaseline, fromOcr);

        double baseline = top + 1 + FontBaseline * fit.FontSize;
        Assert.InRange(baseline - fit.Baseline, -0.5, 0.5);
    }

    [Fact]
    public void AChangedWordFollowsTheTiltOfTheLine()
    {
        ScanLine line = FirstLine();
        ScanLineFit fit = Measure(line);
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(line.Words, line.Boxes, "Invoice total due today via Acme Supply", text => text.Length));
        Assert.Equal(4, plan.First);

        double shift = ScanLineEdit.TopShift(plan, fit);

        double tilt = fit.WordBaselines[4] - fit.Baseline;
        Assert.True(tilt > 0.25, $"The fourth word sits lower than the line's middle, but the tilt was {tilt}.");
        Assert.Equal(tilt - ScanLineEdit.AnnotationInsetGap, shift, 3);
    }

    // The whole way through: what the commit builds, burned into the page and read back.
    [Fact]
    public void ASavedReplacementSitsOnTheInkOfTheWordItReplaced()
    {
        ScanLine line = FirstLine();
        ScanLineFit fit = Measure(line);
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(line.Words, line.Boxes, "Invoice total due today via Acme Supply", text => text.Length));
        const string family = "Times New Roman";
        double top = ScanLineEdit.EditorTop(fit, new FontFamily(family).Baseline, 0) + ScanLineEdit.TopShift(plan, fit);
        var cover = new CoverAnnotation { PageIndex = 0, PairId = "edit", Bounds = plan.Cover };
        cover.SetColor(Colors.White);
        var text = new TextAnnotation
        {
            PageIndex = 0, PairId = "edit", Position = new Point(plan.Covered.Left, top), Content = "via",
            FontName = family, FontSize = fit.FontSize, Width = plan.Covered.Width + 20, Height = fit.FontSize * 1.4
        };
        string path = Path.Combine(Path.GetTempPath(), "KillerPDF-scan-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.Copy(Fixture, path);
        try
        {
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [cover, text] },
                new Dictionary<int, (int w, int h)> { [0] = ((int)Math.Round(line.Width), (int)Math.Round(line.Height)) });

            using var saved = PdfContentDocument.Open(path, null);
            var page = saved.GetPage(1);
            double written = page.GetWords().Single(word => word.Text == "via").Letters[0].StartBaseLine.Y;
            double ink = page.Height - fit.WordBaselines[plan.First] / ScreenPixelsPerPoint;
            Assert.InRange(written - ink, -0.5, 0.5);
        }
        finally { File.Delete(path); }
    }

    // SN01 D11: replacing "within" on the scan left the old word's bottom row of ink as a dotted line,
    // because the cover followed the recognizer's box, which stops above the ink. The cover reaches
    // every dark pixel of the word it replaces.
    // The cover's margin is a few canvas units, so the zoom decides how much of a short box it makes up.
    [Theory]
    [InlineData(1.26)]
    [InlineData(2.5)]
    [InlineData(4.0)]
    public void TheCoverHidesEveryRowOfTheReplacedWordsInk(double pixelsPerPoint)
    {
        ScanLine line = LineWith("within", pixelsPerPoint);
        int index = Array.IndexOf(line.Words, "within");
        ScanLineFit fit = Measure(line);
        string typed = string.Join(' ', line.Words.Select((word, at) => at == index ? "via" : word));
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(line.Words, line.Boxes, typed, text => text.Length * 4.0, fit.InkBoxes));
        Assert.Equal(index, plan.First);

        Rect ink = DarkPixels(line, line.Boxes[index]);

        Assert.True(plan.Cover.Top <= ink.Top && plan.Cover.Bottom >= ink.Bottom,
            $"The cover {plan.Cover} leaves ink of the word showing: {ink}.");
        Assert.True(plan.Cover.Left <= ink.Left && plan.Cover.Right >= ink.Right,
            $"The cover {plan.Cover} leaves ink of the word showing: {ink}.");
    }

    // The dark pixels of the page around one word's box, in canvas units: the box widened by half its
    // height above and below, where a clipped descender or baseline row would be.
    private static Rect DarkPixels(ScanLine line, Rect box)
    {
        using var session = PdfPageRenderSession.OpenEngineFirst(Fixture, (int)(line.Width * 2), (int)(line.Height * 2), null);
        PdfRenderedPage page = session.RenderPage(0);
        double scale = page.Width / line.Width;
        int left = (int)Math.Floor((box.Left + 1) * scale), right = (int)Math.Ceiling((box.Right - 1) * scale);
        int top = (int)Math.Floor((box.Top - box.Height / 2) * scale), bottom = (int)Math.Ceiling((box.Bottom + box.Height / 2) * scale);
        int inkLeft = int.MaxValue, inkTop = int.MaxValue, inkRight = -1, inkBottom = -1;
        for (int y = Math.Max(0, top); y < Math.Min(page.Height, bottom); y++)
            for (int x = Math.Max(0, left); x < Math.Min(page.Width, right); x++)
            {
                int at = (y * page.Width + x) * 4;
                if ((page.Pixels[at] + page.Pixels[at + 1] + page.Pixels[at + 2]) / 3 >= 128) continue;
                inkLeft = Math.Min(inkLeft, x); inkRight = Math.Max(inkRight, x + 1);
                inkTop = Math.Min(inkTop, y); inkBottom = Math.Max(inkBottom, y + 1);
            }
        Assert.True(inkRight > inkLeft, "The word has no ink on the page.");
        return new Rect(inkLeft / scale, inkTop / scale, (inkRight - inkLeft) / scale, (inkBottom - inkTop) / scale);
    }

    private static ScanLine LineWith(string text, double pixelsPerPoint)
    {
        using var document = PdfContentDocument.Open(Fixture, null);
        var page = document.GetPage(1);
        double width = page.Width * pixelsPerPoint, height = page.Height * pixelsPerPoint;
        PdfExtractedWord[] all = [.. page.GetWords()];
        PdfExtractedWord target = all.First(word => word.Text == text);
        double middle = (target.BoundingBox.Bottom + target.BoundingBox.Top) / 2;
        PdfExtractedWord[] line = [.. all.Where(word => Math.Abs((word.BoundingBox.Bottom + word.BoundingBox.Top) / 2 - middle)
                < (target.BoundingBox.Top - target.BoundingBox.Bottom) / 2)
            .OrderBy(word => word.BoundingBox.Left)];
        return new ScanLine([.. line.Select(word => word.Text)],
            [.. line.Select(word => RedactionGeometry.PageToCanvas(word.BoundingBox.Left, word.BoundingBox.Bottom,
                word.BoundingBox.Right, word.BoundingBox.Top, page.Width, page.Height, width, height, 0))],
            width, height);
    }

    private sealed record ScanLine(string[] Words, Rect[] Boxes, double Width, double Height);

    private static ScanLineFit Measure(ScanLine line) => Assert.IsType<ScanLineFit>(ScanLineEdit.Measure(
        Fixture, 0, line.Width, line.Height, line.Words, line.Boxes, PdfEngineIntegration.ScanTextFaces()));

    private static ScanLine FirstLine()
    {
        using var document = PdfContentDocument.Open(Fixture, null);
        var page = document.GetPage(1);
        double width = page.Width * ScreenPixelsPerPoint, height = page.Height * ScreenPixelsPerPoint;
        PdfExtractedWord[] line = [.. page.GetWords().Take(7)];
        return new ScanLine([.. line.Select(word => word.Text)],
            [.. line.Select(word => RedactionGeometry.PageToCanvas(word.BoundingBox.Left, word.BoundingBox.Bottom,
                word.BoundingBox.Right, word.BoundingBox.Top, page.Width, page.Height, width, height, 0))],
            width, height);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
