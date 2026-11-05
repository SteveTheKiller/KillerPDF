using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ReplacementLayerVisibilityTests
{
    [Fact]
    public void HiddenReplacementTextIsAbsentFromSavedTaggedReadingOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-hidden-reading-order-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Layer visibility", Language = "en-US" })
                .EnablePdfUa2Conformance()
                .AddPage(200, 100, new PdfContentStreamBuilder()
                    .BeginMarkedContent(PdfStructureType.Figure, 0)
                    .Rectangle(0, 0, 5, 5).Fill().EndMarkedContent())
                .AddStructureContainer(PdfStructureType.Document)
                .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Original figure")
                .Build());
            var pair = new ReplacementPair(
                new TextAnnotation { Content = "Hidden replacement", Width = 160, Height = 70, PairId = "pair" },
                new CoverAnnotation { Bounds = new Rect(0, 0, 160, 70), PairId = "pair" });
            ReplacementLayers.SetVisibility(pair, ReplacementLayer.Group, false);
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [pair.Cover, pair.Text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            var order = PdfAccessibilityReadingOrder.Read(PdfDocument.Open(File.ReadAllBytes(path)));
            Assert.DoesNotContain(order.Items, item => item.AlternateDescription?.Contains("Hidden replacement") == true
                || item.ActualText?.Contains("Hidden replacement") == true);
            Assert.Single(order.Items, item => item.AlternateDescription == "Original figure");
            Assert.Equal(0, SearchService.Search(path, "Hidden replacement").TotalHits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void HidingBothReplacementLayersKeepsTheOriginalTextWhenSaving()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-hidden-replacement-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddPage(200, 100,
                new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                    .MoveText(20, 40).ShowLatin1Text("Original").EndText()).Build();
            Assert.Contains(new PdfPageContentReader(PdfDocument.Open(source)).Read(0).Words,
                word => word.Text == "Original");
            File.WriteAllBytes(path, source);
            var cover = new CoverAnnotation
                { Bounds = new Rect(0, 0, 200, 100), PairId = "replacement", IsVisible = false };
            var text = new TextAnnotation
                { Content = "Replacement", Width = 200, Height = 100, PairId = "replacement", IsVisible = false };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [cover, text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            var words = new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path))).Read(0).Words;
            Assert.Contains(words, word => word.Text == "Original");
            Assert.DoesNotContain(words, word => word.Text == "Replacement");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void HidingTheReplacementCoverRevealsTheOriginalPageInThePrintCopy()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-hidden-cover-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(10, 10,
                new PdfContentStreamBuilder().SetFillGray(0).Rectangle(0, 0, 10, 10).Fill()).Build());
            var cover = new CoverAnnotation { Bounds = new Rect(0, 0, 10, 10), PairId = "replacement" };
            JsonObject settings = JsonSerializer.SerializeToNode(cover)!.AsObject();
            settings["IsVisible"] = false;
            cover = settings.Deserialize<CoverAnnotation>()!;
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [cover] },
                new Dictionary<int, (int w, int h)> { [0] = (10, 10) }, forRasterization: true);
            var rendered = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path)))
                .Render(0, new PdfRenderOptions(10, 10) { ForPrinting = true });
            Assert.Empty(rendered.Diagnostics);
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
