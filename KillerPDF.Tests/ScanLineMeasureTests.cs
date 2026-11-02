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

            using var saved = PdfContentDocument.Open(path);
            var page = saved.GetPage(1);
            double written = page.GetWords().Single(word => word.Text == "via").Letters[0].StartBaseLine.Y;
            double ink = page.Height - fit.WordBaselines[plan.First] / ScreenPixelsPerPoint;
            Assert.InRange(written - ink, -0.5, 0.5);
        }
        finally { File.Delete(path); }
    }

    private sealed record ScanLine(string[] Words, Rect[] Boxes, double Width, double Height);

    private static ScanLineFit Measure(ScanLine line) => Assert.IsType<ScanLineFit>(ScanLineEdit.Measure(
        Fixture, 0, line.Width, line.Height, line.Words, line.Boxes, PdfEngineIntegration.ScanTextFaces()));

    private static ScanLine FirstLine()
    {
        using var document = PdfContentDocument.Open(Fixture);
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
