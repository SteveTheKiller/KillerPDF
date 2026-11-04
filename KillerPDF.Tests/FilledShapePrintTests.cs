using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FilledShapePrintTests
{
    [Theory]
    [InlineData(255, 255, 255)]
    [InlineData(255, 0, 0)]
    public void OpaqueFilledRectangleCoversDarkContentInThePrintCopy(byte red, byte green, byte blue)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-shape-print-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(10, 10,
                new PdfContentStreamBuilder().SetFillGray(0).Rectangle(0, 0, 5, 10).Fill()).Build());
            var rectangle = new HighlightAnnotation
            {
                PageIndex = 0, Bounds = new Rect(0, 0, 10, 10), Style = HighlightStyle.Fill,
                ColorR = red, ColorG = green, ColorB = blue, ColorA = 255
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [rectangle] },
                new Dictionary<int, (int w, int h)> { [0] = (10, 10) }, forRasterization: true);

            var printed = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path))).Render(0,
                new PdfRenderOptions(10, 10) { ForPrinting = true });

            Assert.Empty(printed.Diagnostics);
            foreach (int x in new[] { 2, 7 })
                Assert.Equal(new byte[] { blue, green, red, 255 },
                    printed.Pixels.Slice((5 * 10 + x) * 4, 4).ToArray());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TranslucentFillKeepsNormalAlphaInSavedAndRasterCopies(bool forRasterization)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-shape-alpha-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(10, 10,
                new PdfContentStreamBuilder().SetFillGray(0).Rectangle(0, 0, 5, 10).Fill()
                    .SetBlendMode(PdfBlendMode.Multiply)).Build());
            var rectangle = new HighlightAnnotation
            {
                PageIndex = 0, Bounds = new Rect(0, 0, 10, 10), Style = HighlightStyle.Fill,
                ColorR = 255, ColorG = 255, ColorB = 0, ColorA = 128
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [rectangle] },
                new Dictionary<int, (int w, int h)> { [0] = (10, 10) }, forRasterization: forRasterization);

            var printed = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path))).Render(0,
                new PdfRenderOptions(10, 10) { ForPrinting = true });

            Assert.Equal(new byte[] { 0, 128, 128, 255 }, printed.Pixels.Slice((5 * 10 + 2) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 127, 255, 255, 255 }, printed.Pixels.Slice((5 * 10 + 7) * 4, 4).ToArray());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
