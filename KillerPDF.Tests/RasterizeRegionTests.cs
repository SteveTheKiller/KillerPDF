using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class RasterizeRegionTests
{
    [Fact]
    public void TaggedDocumentIsRefusedDuringPlanningBeforeRasterWork()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-tagged-region-{Guid.NewGuid():N}.pdf");
        byte[] original = new PdfDocumentBuilder()
            .AddPage(200, 200, new PdfContentStreamBuilder()
                .BeginMarkedContent(PdfStructureType.Paragraph, 0).Rectangle(20, 20, 60, 60).Fill().EndMarkedContent())
            .AddStructureElement(PdfStructureType.Paragraph, 0, 0).Build();
        try
        {
            File.WriteAllBytes(path, original);
            Assert.Throws<NotSupportedException>(() => PdfEngineIntegration.PlanPartialRasterization(
                path, 0, new PdfContentBounds(10, 10, 90, 90), 72));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RasterizeRegion_ReplacesOnlyTheBoxWithAnImage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-raster-region-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddPage(200, 200, new PdfContentStreamBuilder()
                    .SetFillRgb(1, 0, 0).Rectangle(20, 20, 60, 60).Fill()
                    .SetFillRgb(0, 0, 1).Rectangle(120, 120, 60, 60).Fill())
                .Build());

            PdfEngineIntegration.RasterizeRegion(path, 0, new PdfContentBounds(10, 10, 90, 90), 72);

            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
            PdfPageContent page = new PdfPageContentReader(document).Read(0);
            PdfExtractedImage image = Assert.Single(page.Images);
            Assert.Equal(80, image.BoundingBox.Width, 1);
            Assert.Equal(80, image.BoundingBox.Height, 1);
            Assert.Contains(page.Paths, drawn => drawn.BoundingBox.Left >= 119);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
