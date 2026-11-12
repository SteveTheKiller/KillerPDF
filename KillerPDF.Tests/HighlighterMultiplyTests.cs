using System.IO;
using System.Text.Json;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

// A highlighter mark darkens the paper behind the text and never covers the text, at any opacity,
// in saved, printed and flattened copies (#200). A filled Shapes rectangle is the opposite: it
// covers like it does on screen (#474). The two share HighlightAnnotation, so IsShape tells them apart.
public sealed class HighlighterMultiplyTests
{
    private static byte[] BurnOverHalfBlackPage(HighlightAnnotation annotation, bool forRasterization)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-highlighter-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(10, 10,
                new PdfContentStreamBuilder().SetFillGray(0).Rectangle(0, 0, 5, 10).Fill()).Build());
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [annotation] },
                new Dictionary<int, (int w, int h)> { [0] = (10, 10) }, forRasterization: forRasterization);
            var printed = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path))).Render(0,
                new PdfRenderOptions(10, 10) { ForPrinting = true });
            Assert.Empty(printed.Diagnostics);
            return printed.Pixels.ToArray();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static byte[] Pixel(byte[] pixels, int x) => pixels.AsSpan((5 * 10 + x) * 4, 4).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullOpacityHighlightKeepsTextDarkAndTintsThePaper(bool forRasterization)
    {
        var highlight = new HighlightAnnotation
        {
            PageIndex = 0, Bounds = new Rect(0, 0, 10, 10), Style = HighlightStyle.Fill,
            ColorR = 255, ColorG = 255, ColorB = 0, ColorA = 255
        };

        byte[] pixels = BurnOverHalfBlackPage(highlight, forRasterization);

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(pixels, 2));
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, Pixel(pixels, 7));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HalfOpacityHighlightKeepsTextDarkAndTintsThePaperHalfway(bool forRasterization)
    {
        var highlight = new HighlightAnnotation
        {
            PageIndex = 0, Bounds = new Rect(0, 0, 10, 10), Style = HighlightStyle.Fill,
            ColorR = 255, ColorG = 255, ColorB = 0, ColorA = 128
        };

        byte[] pixels = BurnOverHalfBlackPage(highlight, forRasterization);

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(pixels, 2));
        byte[] paper = Pixel(pixels, 7);
        Assert.InRange(paper[0], 126, 128);
        Assert.Equal(new byte[] { 255, 255, 255 }, paper[1..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilledShapeRectangleStillCoversText(bool forRasterization)
    {
        var shape = new HighlightAnnotation
        {
            PageIndex = 0, Bounds = new Rect(0, 0, 10, 10), Style = HighlightStyle.Fill, IsShape = true,
            ColorR = 255, ColorG = 255, ColorB = 0, ColorA = 255
        };

        byte[] pixels = BurnOverHalfBlackPage(shape, forRasterization);

        Assert.Equal(new byte[] { 0, 255, 255, 255 }, Pixel(pixels, 2));
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, Pixel(pixels, 7));
    }

    [Fact]
    public void TextHighlightJsonHasNoShapeFieldSoOlderOverlaysStillMatch()
    {
        string json = JsonSerializer.Serialize(new HighlightAnnotation { Bounds = new Rect(0, 0, 1, 1) });
        Assert.DoesNotContain("IsShape", json);

        string shape = JsonSerializer.Serialize(new HighlightAnnotation { Bounds = new Rect(0, 0, 1, 1), IsShape = true });
        Assert.Contains("\"IsShape\":true", shape);
        Assert.True(JsonSerializer.Deserialize<HighlightAnnotation>(shape)!.IsShape);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEditableLayerKeepsTheShapeMarkerThroughSaveAndReopen(bool shape)
    {
        // The real overlay writer and its strict reader: unmapped members are refused and the property count must
        // match, so this proves both the new field and the old shape of a plain highlight survive.
        var model = new HighlightAnnotation { PageIndex = 0, Bounds = new Rect(1, 1, 10, 5), IsShape = shape };
        var blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(20, 20).Build());
        byte[] saved = PdfOverlayAnnotations.AppendPageBatch(blank, 0, 20, 20, 0, [model], new PdfContentStreamBuilder(), null);

        var restored = Assert.IsType<HighlightAnnotation>(Assert.Single(PdfOverlayAnnotations.Read(PdfDocument.Open(saved))).Annotation);
        Assert.Equal(shape, restored.IsShape);
    }

    [Fact]
    public void ScreenAndSavedCopyShareOneHighlighterRule()
    {
        Assert.True(PdfBurn.IsHighlighterMark(new HighlightAnnotation { Style = HighlightStyle.Fill }));
        Assert.False(PdfBurn.IsHighlighterMark(new HighlightAnnotation { Style = HighlightStyle.Fill, IsShape = true }));
        Assert.False(PdfBurn.IsHighlighterMark(new HighlightAnnotation { Style = HighlightStyle.Underline }));
        Assert.False(PdfBurn.IsHighlighterMark(new CoverAnnotation()));
        Assert.Contains("PdfBurn.IsHighlighterMark(", Source("Services", "PdfEngineBurn.cs"));
        Assert.Contains("PdfBurn.IsHighlighterMark(", Source("Controls", "Viewer", "PdfViewer.Annotations.cs"));
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void ShapesToolMarksItsFilledRectangle()
    {
        string shapes = Source("Shell", "Shapes.cs");
        Assert.Contains("IsShape = true", shapes);
    }

    [Fact]
    public void CopiesKeepTheShapeMarker()
    {
        string menu = Source("Shell", "ContextMenu.cs");
        Assert.Contains("IsShape = h.IsShape", menu);
    }

    [Fact]
    public void SelectingAShapeRectangleDoesNotChangeTheHighlighterColor()
    {
        // 1.9.4 moved the selection's bar sync into PdfViewer.DrawLook.cs.
        string annotations = Source("Controls", "Viewer", "PdfViewer.DrawLook.cs");
        int shapeBranch = annotations.IndexOf("case HighlightAnnotation { IsShape: true } shape:", StringComparison.Ordinal);
        int highlightBranch = annotations.IndexOf("case HighlightAnnotation highlight:", StringComparison.Ordinal);
        Assert.True(shapeBranch >= 0 && shapeBranch < highlightBranch, "a selected Shapes rectangle must be handled before the highlighter branch");
        string bar = Source("Shell", "TextSettingsBar.cs");
        Assert.Contains("ha.IsShape ? _drawColor", bar);
    }
}
