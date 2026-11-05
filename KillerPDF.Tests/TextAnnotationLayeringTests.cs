using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TextAnnotationLayeringTests
{
    [Fact]
    public void SavedTextHasOneLogicalDescriptionInTaggedReadingOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-text-reading-order-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Tagged source", Language = "en-US" })
                .EnablePdfUa2Conformance()
                .AddPage(200, 100, new PdfContentStreamBuilder()
                    .BeginMarkedContent(PdfStructureType.Figure, 0)
                    .Rectangle(0, 0, 5, 5).Fill().EndMarkedContent())
                .AddStructureContainer(PdfStructureType.Document)
                .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Original figure")
                .Build());
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation { Content = "One logical box", Position = new Point(20, 10), Width = 160, Height = 70 }]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            var order = PdfAccessibilityReadingOrder.Read(PdfDocument.Open(File.ReadAllBytes(path)));
            var description = Assert.Single(order.Items,
                item => item.AlternateDescription == "One logical box" || item.ActualText == "One logical box");
            Assert.Equal("Annot", description.Role);
            Assert.Single(order.Items, item => item.AlternateDescription == "Original figure");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(0, 0, 50, 30)]
    [InlineData(0, 90, 30, 50)]
    [InlineData(0, 180, 50, 30)]
    [InlineData(0, 270, 30, 50)]
    [InlineData(90, 0, 50, 30)]
    [InlineData(90, 90, 30, 50)]
    [InlineData(90, 180, 50, 30)]
    [InlineData(90, 270, 30, 50)]
    [InlineData(180, 0, 50, 30)]
    [InlineData(180, 90, 30, 50)]
    [InlineData(180, 180, 50, 30)]
    [InlineData(180, 270, 30, 50)]
    [InlineData(270, 0, 50, 30)]
    [InlineData(270, 90, 30, 50)]
    [InlineData(270, 180, 50, 30)]
    [InlineData(270, 270, 30, 50)]
    public void SavedTextBoxKeepsItsVisibleFootprintAcrossPageAndTextTurns(
        int pageTurn, int textTurn, int footprintWidth, int footprintHeight)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-text-turn-{Guid.NewGuid():N}.pdf");
        try
        {
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            File.WriteAllBytes(path, new PdfIncrementalPageEditor(source).SetRotation(0, pageTurn).Build());
            int canvasWidth = pageTurn is 90 or 270 ? 100 : 200;
            int canvasHeight = pageTurn is 90 or 270 ? 200 : 100;
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation { Position = new Point(20, 10), Width = 50, Height = 30,
                    Rotation = textTurn, BgR = 0, BgG = 255, BgB = 0, BgA = 255 }]
            }, new Dictionary<int, (int w, int h)> { [0] = (canvasWidth, canvasHeight) });
            var saved = PdfDocument.Open(File.ReadAllBytes(path));
            var rendered = new PdfPageRenderer(saved).Render(0, new PdfRenderOptions(canvasWidth, canvasHeight));
            Assert.Empty(rendered.Diagnostics);
            Pixel(25, 15, 0, 255, 0);
            Pixel(20 + footprintWidth - 5, 10 + footprintHeight - 5, 0, 255, 0);
            Pixel(5, 5, 255, 255, 255);
            Pixel(20 + footprintWidth + 5, 10 + footprintHeight + 5, 255, 255, 255);
            Assert.Single(PdfOverlayAnnotations.TakeEditableTail(File.ReadAllBytes(path)).Records);

            void Pixel(int x, int y, byte red, byte green, byte blue)
            {
                int offset = (y * canvasWidth + x) * 4;
                Assert.Equal(blue, rendered.Pixels.Span[offset]);
                Assert.Equal(green, rendered.Pixels.Span[offset + 1]);
                Assert.Equal(red, rendered.Pixels.Span[offset + 2]);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SavedTextSearchAcceptsRepeatedOriginalAnnotationReferences()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-text-shared-comment-{Guid.NewGuid():N}.pdf");
        try
        {
            var blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            File.WriteAllBytes(path, new PdfIncrementalAnnotationEditor(blank)
                .AddRectangle(0, 0, 0, 200, 100, fillColor: new PdfRgbColor(1, 1, 1)).Build());
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation { Content = "Findable", Position = new Point(20, 10), Width = 160, Height = 70 }]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            var saved = PdfDocument.Open(File.ReadAllBytes(path));
            var catalog = Assert.IsType<PdfDictionary>(saved.Resolve(Assert.IsType<PdfIndirectReference>(saved.Trailer[new PdfName("Root"u8)])));
            var pages = Assert.IsType<PdfDictionary>(saved.Resolve(Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Pages"u8)])));
            var pageReference = Assert.IsType<PdfIndirectReference>(Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)])[0]);
            var page = Assert.IsType<PdfDictionary>(saved.Resolve(pageReference));
            PdfName annots = new("Annots"u8);
            var entries = Assert.IsType<PdfArray>(page[annots]);
            Assert.Equal(2, entries.Count);
            var repeated = new PdfDictionary(page.Where(item => !item.Key.Equals(annots))
                .Append(new(annots, new PdfArray([entries[0], entries[0], entries[1]]))));
            File.WriteAllBytes(path, new PdfIncrementalUpdateBuilder(saved)
                .ReplaceObject(pageReference.ObjectNumber, repeated).Build());
            Assert.Equal(1, SearchService.Search(path, "Findable").TotalHits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SavingKeepsAnOpaqueCoverAboveEarlierAddedText()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-text-stack-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation { Content = "Covered text", Position = new Point(20, 10),
                    Width = 160, Height = 70, FontSize = 18 },
                    new CoverAnnotation { Bounds = new Rect(0, 0, 200, 100) }]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            var page = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path)))
                .Render(0, new PdfRenderOptions(200, 100));
            Assert.Empty(page.Diagnostics);
            for (int offset = 0; offset < page.Pixels.Length; offset += 4)
                Assert.Equal((byte)255, page.Pixels.Span[offset]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SavedTextCanBeFoundThroughDocumentSearch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-text-search-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation { Content = "Findable paragraph", Position = new Point(20, 10),
                    Width = 160, Height = 70, FontSize = 18 }]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            SearchResult result = SearchService.Search(path, "Findable");
            Assert.Equal(1, result.TotalHits);
            Assert.Equal(0, Assert.Single(result.Hits).page);
            Assert.Empty(result.UnreadablePages);
            Assert.Equal(0, SearchService.Search(path, "Absent").TotalHits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SavedTextReturnsAsOneEditableMultilineBoxWithoutRemovingOriginalComments()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-text-editable-{Guid.NewGuid():N}.pdf");
        try
        {
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            File.WriteAllBytes(path, new PdfIncrementalAnnotationEditor(source).AddRectangle(
                0, 0, 0, 200, 100, fillColor: new PdfRgbColor(1, 1, 1), contents: "Original comment").Build());
            var text = new TextAnnotation
            {
                PageIndex = 0, Position = new Point(20, 10), Width = 160, Height = 70,
                Content = "First line\nSecond line", FontSize = 18, Bold = true
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });

            OverlayExtraction reopened = PdfOverlayAnnotations.TakeEditableTail(File.ReadAllBytes(path));
            OverlayRecord box = Assert.Single(reopened.Records);
            TextAnnotation editable = Assert.IsType<TextAnnotation>(box.Annotation);
            Assert.Equal("First line\nSecond line", editable.Content);
            Assert.Equal(new Point(20, 10), editable.Position);
            Assert.Equal(160, editable.Width);
            Assert.Equal(70, editable.Height);
            Assert.Equal(18, editable.FontSize);
            Assert.True(editable.Bold);
            Assert.Equal(text.HistoryId, editable.HistoryId);
            var original = Assert.Single(PdfCommentReader.Read(PdfDocument.Open(reopened.Bytes), true));
            Assert.Equal("Original comment", original.Contents);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AddedTextRemainsVisibleAboveAnExistingOpaqueAnnotationAfterSaving()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-text-layer-{Guid.NewGuid():N}.pdf");
        try
        {
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            byte[] annotated = new PdfIncrementalAnnotationEditor(source).AddRectangle(
                0, 0, 0, 200, 100, strokeColor: new PdfRgbColor(1, 1, 1),
                fillColor: new PdfRgbColor(1, 1, 1)).Build();
            File.WriteAllBytes(path, annotated);
            var text = new TextAnnotation
            {
                PageIndex = 0, Position = new Point(20, 20), Width = 160, Height = 60,
                Content = "First line\nSecond line", FontSize = 18
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });

            var renderer = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path)));
            var visible = renderer.Render(0, new PdfRenderOptions(200, 100));
            Assert.Empty(visible.Diagnostics);
            int darkPixels = 0;
            for (int offset = 0; offset < visible.Pixels.Length; offset += 4)
                if (visible.Pixels.Span[offset] < 128) darkPixels++;
            Assert.True(darkPixels > 100, $"Only {darkPixels} text pixels remain visible after saving.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
