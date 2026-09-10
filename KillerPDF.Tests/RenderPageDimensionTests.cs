using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

public sealed class RenderPageDimensionTests
{
    [Theory]
    [InlineData(0, 85, 128)]
    [InlineData(90, 128, 85)]
    [InlineData(180, 85, 128)]
    [InlineData(270, 128, 85)]
    public void FittedCropPreservesRotationAndLegacyDimensions(int rotation, int expectedWidth, int expectedHeight)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage()
                .SetPageBox(0, PdfPageBox.Crop, 10, 20, 200.8, 301.6)
                .SetPageRotation(0, rotation).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 128, 128);
            var page = session.RenderBasePage(0);
            Assert.Equal(expectedWidth, page.Width);
            Assert.Equal(expectedHeight, page.Height);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(200.8, 301.6, 100, 150)]
    [InlineData(301.6, 200.8, 150, 100)]
    [InlineData(200.000001, 301.999999, 100, 150)]
    [InlineData(1, 1, 1, 1)]
    public void ScaledPagePreservesLegacyPixelDimensions(double width, double height,
        int expectedWidth, int expectedHeight)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddPage(width, height, new PdfContentStreamBuilder()).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 0.5);
            var page = session.RenderBasePage(0);
            Assert.Equal(expectedWidth, page.Width);
            Assert.Equal(expectedHeight, page.Height);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(200.8, 301.6, 85, 128)]
    [InlineData(301.6, 200.8, 128, 85)]
    public void FittedPagePreservesLegacyPixelDimensions(double width, double height,
        int expectedWidth, int expectedHeight)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddPage(width, height, new PdfContentStreamBuilder()).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 128, 128);
            var page = session.RenderBasePage(0);
            Assert.Equal(expectedWidth, page.Width);
            Assert.Equal(expectedHeight, page.Height);
        }
        finally { File.Delete(path); }
    }
}
