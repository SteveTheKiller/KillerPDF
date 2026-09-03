using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using EngineDocument = KillerPdf.Engine.Documents.PdfDocument;
using Xunit;

namespace KillerPDF.Tests;

// #169: content placed on a rotated page must burn where the user placed it. The burn draws in
// the VISUAL frame and maps back through a quarter-turn matrix. Two parts have regressed
// independently and each is pinned here by reading the saved content stream:
//   1. the scale basis - sx/sy must come from the visual page size, not the raw page box
//      (a regression squeezes the rect by exactly the page's aspect ratio), and
//   2. the quarter-turn matrix reaching both burn paths (annotations AND stamps) - dropping
//      it leaves the rect numbers right but the content turned 90 degrees on the page.
public sealed class PdfBurnRotationTests
{
    [Fact]
    public void EngineBurn_RasterPreparationAcceptsTaggedPagesWithoutRemovingTheirStructure()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-tagged-raster-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Tagged source", Language = "en-US" })
                .EnablePdfUa2Conformance()
                .AddPage(100, 100, new PdfContentStreamBuilder()
                    .BeginMarkedContent(PdfStructureType.Figure, 0)
                    .Rectangle(10, 10, 20, 20).Fill().EndMarkedContent())
                .AddStructureContainer(PdfStructureType.Document)
                .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Square")
                .Build();
            File.WriteAllBytes(path, source);
            var annotations = new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new HighlightAnnotation { PageIndex = 0, Bounds = new Rect(20, 30, 40, 12) }]
            };
            var dimensions = new Dictionary<int, (int w, int h)> { [0] = (100, 100) };
            Assert.Throws<NotSupportedException>(() => PdfEngineBurn.Burn(path, annotations, dimensions));
            PdfEngineBurn.Burn(path, annotations, dimensions, forRasterization: true);
            EngineDocument result = EngineDocument.Open(File.ReadAllBytes(path));
            var catalog = Assert.IsType<PdfDictionary>(result.Resolve(
                Assert.IsType<PdfIndirectReference>(result.Trailer[new PdfName("Root"u8)])));
            Assert.True(catalog.ContainsKey(new PdfName("StructTreeRoot"u8)));
            Assert.Contains("/Artifact BMC", AllDecodedStreams(path));
            Assert.Contains("/MCID 0", AllDecodedStreams(path));
            Assert.Single(annotations[0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // A5-ish landscape map on a portrait MediaBox, the shape from the #169 repro files.
    private const double BoxW = 842, BoxH = 1191;

    private static string BurnHighlightContent(int nativeRotate, Dictionary<int, int>? rotations,
        double pageW, double pageH, int renderW, int renderH, Rect bounds)
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(pageW, pageH).Build();
        if (nativeRotate != 0)
            source = new PdfIncrementalPageEditor(EngineDocument.Open(source))
                .SetRotation(0, nativeRotate).Build();

        var annots = new Dictionary<int, List<PageAnnotation>>
        {
            [0] = [new HighlightAnnotation { PageIndex = 0, Bounds = bounds, Style = HighlightStyle.Fill }],
        };
        var dims = new Dictionary<int, (int w, int h)> { [0] = (renderW, renderH) };
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-burn-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, source);
            PdfEngineBurn.Burn(path, annots, dims, null, null, rotations);
            return AllDecodedStreams(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string AllDecodedStreams(string path)
    {
        EngineDocument document = EngineDocument.Open(File.ReadAllBytes(path));
        var text = new StringBuilder();
        foreach (int objectNumber in document.CrossReferences.Keys)
            if (document.Resolve(objectNumber) is PdfStream stream)
                try { text.AppendLine(Encoding.GetEncoding("ISO-8859-1").GetString(
                    PdfStreamDecoder.Decode(stream, document.Resolve))); }
                catch { }
        return text.ToString();
    }

    // Every `x y w h re` operator in the saved file, as (w, h).
    private static List<(double w, double h)> RectSizes(string pdf)
    {
        var list = new List<(double, double)>();
        foreach (Match m in Regex.Matches(pdf,
            @"(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) re"))
        {
            list.Add((double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                      double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }
        return list;
    }

    // True when the content carries a quarter-turn cm (a and d zero, b and c unit) - the visual-to-page
    // mapping the rotated burn must emit. The unrotated base transform is axis-aligned and never matches.
    private static bool HasQuarterTurnCm(string pdf)
    {
        foreach (Match m in Regex.Matches(pdf,
            @"(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) cm"))
        {
            double a = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double b = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double c = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            double d = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(a) < 0.001 && Math.Abs(d) < 0.001 &&
                Math.Abs(Math.Abs(b) - 1) < 0.001 && Math.Abs(Math.Abs(c) - 1) < 0.001)
                return true;
        }
        return false;
    }

    [Fact]
    public void UnrotatedPage_BurnsAtRenderScale_NoTurn()
    {
        // Letter page rendered at 2x: a 300x60 canvas rect must burn as 150x30 points.
        string pdf = BurnHighlightContent(0, null, 612, 792, 1224, 1584, new Rect(100, 200, 300, 60));

        var rects = RectSizes(pdf);
        var (w, h) = Assert.Single(rects);
        Assert.Equal(150, w, 2);
        Assert.Equal(30, h, 2);
        Assert.False(HasQuarterTurnCm(pdf));
    }

    // Native /Rotate on a freshly opened file (the 1.7.1 fallback - the rotation map is empty),
    // and both quarter turns.
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void NativeRotate_UsesVisualScaleAndTurns(int rotate)
    {
        // Visual frame is 1191x842, rendered at 2x. A 300x60 canvas rect must burn as 150x30.
        // The #169 regression scaled against the raw 842x1191 box instead, which burns exactly
        // 106.07x42.45 - the aspect-ratio squeeze from terada-d's measurements.
        string pdf = BurnHighlightContent(rotate, null, BoxW, BoxH, 2382, 1684, new Rect(200, 400, 300, 60));

        var rects = RectSizes(pdf);
        var (w, h) = Assert.Single(rects);
        Assert.Equal(150, w, 2);
        Assert.Equal(30, h, 2);
        Assert.True(HasQuarterTurnCm(pdf), "rotated burn emitted no quarter-turn cm - content will land turned 90 degrees");
    }

    [Fact]
    public void InAppRotation_MapOverridesStrippedPage()
    {
        // In-app rotation: the working copy has /Rotate stripped to 0 and the angle lives in the
        // shell's rotation map. The burn must honor the map exactly as it honors a native /Rotate.
        var rotations = new Dictionary<int, int> { [0] = 90 };
        string pdf = BurnHighlightContent(0, rotations, BoxW, BoxH, 2382, 1684, new Rect(200, 400, 300, 60));

        var rects = RectSizes(pdf);
        var (w, h) = Assert.Single(rects);
        Assert.Equal(150, w, 2);
        Assert.Equal(30, h, 2);
        Assert.True(HasQuarterTurnCm(pdf));
    }

    [Fact]
    public void Rotate180_ScalesUnswappedAndTurns()
    {
        // 180 keeps the axes (no dimension swap) but still needs its half-turn mapping.
        string pdf = BurnHighlightContent(180, null, BoxW, BoxH, 1684, 2382, new Rect(200, 400, 300, 60));

        var rects = RectSizes(pdf);
        var (w, h) = Assert.Single(rects);
        Assert.Equal(150, w, 2);
        Assert.Equal(30, h, 2);
        // A half turn is (-1 0 0 -1) pre-flip; composed with the base flip it is axis-aligned,
        // so assert on the rect numbers plus the annotation surviving - not on the cm shape.
    }

    [Fact]
    public void StampBurn_RotatedPage_GetsTheTurnToo()
    {
        // Stamps share the visual-frame helpers; the original #169 gap was the stamp burn never
        // receiving the angle at all, so preview and output disagreed.
        byte[] source = new PdfIncrementalPageEditor(EngineDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(BoxW, BoxH).Build()))
            .SetRotation(0, 90).Build();

        var spec = new StampSpec { NumbersEnabled = true, Format = "{n} / {N}" };
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-stamp-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, source);
        PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>(),
            new Dictionary<int, (int w, int h)>(), spec);
        string pdf = AllDecodedStreams(path);
        File.Delete(path);
        Assert.True(HasQuarterTurnCm(pdf), "stamp burn on a rotated page emitted no quarter-turn cm");
    }

    [Fact]
    public void EngineBurn_WritesTypedMarkupResourcesAndReopens()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-typed-burn-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(612, 792).Build());
            var annotations = new Dictionary<int, List<PageAnnotation>>
            {
                [0] =
                [
                    new HighlightAnnotation { PageIndex = 0, Bounds = new Rect(20, 30, 80, 14) },
                    new InkAnnotation
                    {
                        PageIndex = 0, Points = [new Point(10, 10), new Point(40, 50)],
                        StrokeWidth = 3
                    },
                    new ImageAnnotation
                    {
                        PageIndex = 0, Position = new Point(50, 60), SourceWidth = 10,
                        SourceHeight = 10, Scale = 1,
                        ImageData = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2pGQAAAAASUVORK5CYII="
                    }
                ]
            };
            PdfEngineBurn.Burn(path, annotations,
                new Dictionary<int, (int w, int h)> { [0] = (612, 792) });

            EngineDocument reopened = EngineDocument.Open(File.ReadAllBytes(path));
            Assert.Single(KillerPdf.Engine.Documents.PdfPageInformation.Read(reopened));
            string streams = AllDecodedStreams(path);
            Assert.Contains(" gs", streams);
            Assert.Contains("1 J", streams);
            Assert.Contains("10 0 0 -10 50 70 cm", streams);
            Assert.Contains(" Do", streams);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TextBurn_WritesLetterSpacingOperator()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-letter-spacing-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(612, 792).Build());
            var text = new TextAnnotation
            {
                PageIndex = 0,
                Position = new Point(20, 30),
                Content = "A1B2C3",
                FontName = "Segoe UI",
                FontSize = 14,
                LetterSpacing = 3,
                Width = 200,
                Height = 30
            };
            PdfEngineBurn.Burn(path,
                new Dictionary<int, List<PageAnnotation>> { [0] = [text] },
                new Dictionary<int, (int w, int h)> { [0] = (612, 792) });

            Assert.Contains("3 Tc", AllDecodedStreams(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void CoverBurn_UsesNormalBlendSoItHidesOriginalText()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-cover-burn-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(612, 792).Build());
            var cover = new CoverAnnotation
            {
                PageIndex = 0,
                Bounds = new Rect(20, 30, 80, 14)
            };
            cover.SetColor(System.Windows.Media.Colors.White);

            PdfEngineBurn.Burn(path,
                new Dictionary<int, List<PageAnnotation>> { [0] = [cover] },
                new Dictionary<int, (int w, int h)> { [0] = (612, 792) });

            string saved = Encoding.GetEncoding("ISO-8859-1").GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("/BM /Multiply", saved);
            Assert.Contains("20 30 80 14 re", AllDecodedStreams(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TextBurn_UsesWpfTypefaceBaselineInsteadOfAssumedFullEm()
    {
        double ratio = PdfEngineBurn.BaselineRatio("Segoe UI", false, false);

        Assert.InRange(ratio, .5, 1.5);
        Assert.NotEqual(1, ratio, 3);
    }
}
