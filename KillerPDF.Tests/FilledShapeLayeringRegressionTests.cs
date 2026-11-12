using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FilledShapeLayeringRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalTextStaysCoveredBelowAnOpaqueShapeAndNewTextInFlattenAndPrint(bool flatten)
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-shape-layers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string original = Path.Combine(folder, "original.pdf");
            string copy = Path.Combine(folder, "copy.pdf");
            string output = Path.Combine(folder, "flattened.pdf");
            byte[] source = new PdfDocumentBuilder().AddPage(200, 100,
                new PdfContentStreamBuilder().SetFillRgb(1, 0, 0)
                    .BeginText().SetFont(PdfStandardFont.Helvetica, 18)
                    .MoveText(20, 25).ShowLatin1Text("ORIGINAL").EndText()).Build();
            File.WriteAllBytes(original, source);
            Assert.True(CountInk(Render(original), red: true) > 100);
            var annotations = new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new HighlightAnnotation
                {
                    PageIndex = 0, Bounds = new Rect(10, 10, 180, 80), Style = HighlightStyle.Fill, IsShape = true,
                    ColorR = 255, ColorG = 255, ColorB = 255, ColorA = 255
                }, new TextAnnotation
                {
                    PageIndex = 0, Position = new Point(20, 20), Width = 160, Height = 30,
                    Content = "REPLACEMENT", FontSize = 16, ColorR = 0, ColorG = 0, ColorB = 255
                }]
            };
            using (var document = PdfWorkingDocument.Open(original, null)) PrintCopy.Write(document, copy);
            PrintCopy.Ready(copy, path => PdfEngineBurn.Burn(path, annotations,
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) }, forRasterization: true),
                new PdfEngineIntegration.FormEdits(new Dictionary<string, string>(), new Dictionary<string, string>(),
                    new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
                    new Dictionary<string, string>(), new Dictionary<string, double>()),
                new Dictionary<int, int>());
            if (flatten)
                PdfRasterize.FlattenToPdf(copy, 1, [(200, 100)], output,
                    new PdfRasterize.FlattenOptions(Dpi: 72), (_, _) => { }, CancellationToken.None);
            byte[] pixels = Render(flatten ? output : copy);
            Assert.Equal(0, CountInk(pixels, red: true));
            Assert.True(CountInk(pixels, red: false) > 100, "Replacement text must remain visible above the shape.");
            Assert.Equal(source, File.ReadAllBytes(original));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static byte[] Render(string path) => new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path)))
        .Render(0, new PdfRenderOptions(200, 100) { ForPrinting = true }).Pixels.ToArray();

    private static int CountInk(byte[] pixels, bool red)
    {
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + (red ? 2 : 0)] > 180
                && pixels[offset + (red ? 0 : 2)] < 80 && pixels[offset + 1] < 80) count++;
        return count;
    }
}
