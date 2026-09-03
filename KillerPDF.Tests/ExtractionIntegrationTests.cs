using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ExtractionIntegrationTests
{
    [Fact]
    public void SearchAndFlowingSelectionUseTheSameExtractedGeometry()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, PdfTextSizeTests.BuildPdf());
            var search = SearchService.Search(path, "Conventional");
            Assert.Equal(1, search.TotalHits);
            var hit = Assert.Single(search.PageRects[0]);
            var runs = Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, 0));
            Assert.Equal(2, runs.Lines.Count);
            var first = runs.Lines[0];
            Assert.Equal(hit.Left, first.Left, 5);
            Assert.Equal(hit.Right, first.Right, 5);
            Assert.Equal(hit.Top, first.Top, 5);
            Assert.Equal(hit.Bottom, first.Bottom, 5);
            Assert.True(TextRunService.IsOverText(runs, (hit.Left + hit.Right) / 2, (hit.Bottom + hit.Top) / 2));
            Assert.Equal("Conventional\nScaled", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out int count));
            Assert.Equal(2, count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CachedExtractionKeepsRawAndVisualFontSizesDistinct()
    {
        using var document = PdfContentDocument.Open(PdfTextSizeTests.BuildPdf());
        var page = document.GetPage(1);
        Assert.Same(page, document.GetPage(1));
        var scaled = page.Words.Single(w => w.Text == "Scaled").Letters[0];
        Assert.Equal(1, scaled.FontSize);
        Assert.Equal(12, scaled.PointSize);
        Assert.True(scaled.BoundingBox.Height > 5);
        Assert.Equal("Helvetica", scaled.FontName);
    }
}
