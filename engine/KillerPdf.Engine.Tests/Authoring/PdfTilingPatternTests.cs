using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfTilingPatternTests
{
    [Fact]
    public void Build_ReusesOneColoredPatternAcrossPages()
    {
        var pattern = new PdfTilingPattern(8, 8, new PdfContentStreamBuilder()
            .SetFillRgb(0.1, 0.6, 0.9).Rectangle(0, 0, 4, 4).Fill());
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, Fill(pattern, 10, 20, 60, 40))
            .AddPage(100, 100, Fill(pattern, 0, 0, 100, 100))
            .Build());
        PdfDictionary[] pages = Pages(document);
        PdfIndirectReference first = PatternReference(pages[0]);
        PdfIndirectReference second = PatternReference(pages[1]);
        PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(first));

        Assert.Equal(first.ObjectNumber, second.ObjectNumber);
        Assert.Equal(1, Assert.IsType<PdfInteger>(stream.Dictionary[Name("PatternType")]).Value);
        Assert.Equal(1, Assert.IsType<PdfInteger>(stream.Dictionary[Name("PaintType")]).Value);
        Assert.Equal(8, Assert.IsType<PdfInteger>(stream.Dictionary[Name("XStep")]).Value);
        Assert.Equal("0.1 0.6 0.9 rg\n0 0 4 4 re\nf\n",
            Encoding.ASCII.GetString(stream.EncodedData.Span));
    }

    [Fact]
    public void Build_WritesPatternResourcesAndCustomSpacing()
    {
        var mark = new PdfFormXObject(3, 3, new PdfContentStreamBuilder()
            .SetFillGray(0).Rectangle(0, 0, 3, 3).Fill());
        var pattern = new PdfTilingPattern(6, 6,
            new PdfContentStreamBuilder().DrawForm(mark, 1, 1),
            horizontalStep: 9, verticalStep: -12,
            tilingType: PdfTilingPatternType.NoDistortion,
            matrix: new PdfPatternMatrix(0, 1, -1, 0, 40, 0));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(40, 40, Fill(pattern, 0, 0, 40, 40)).Build());
        PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(
            PatternReference(Pages(document)[0])));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(stream.Dictionary[Name("Resources")]);

        Assert.True(resources.ContainsKey(Name("XObject")));
        Assert.Equal(2, Assert.IsType<PdfInteger>(stream.Dictionary[Name("TilingType")]).Value);
        Assert.Equal(9, Assert.IsType<PdfInteger>(stream.Dictionary[Name("XStep")]).Value);
        Assert.Equal(-12, Assert.IsType<PdfInteger>(stream.Dictionary[Name("YStep")]).Value);
        PdfArray matrix = Assert.IsType<PdfArray>(stream.Dictionary[Name("Matrix")]);
        Assert.Equal([0L, 1L, -1L, 0L, 40L, 0L],
            matrix.Select(value => Assert.IsType<PdfInteger>(value).Value));
    }

    [Fact]
    public void Build_WritesUncoloredStencilWithDeviceRgbBaseColor()
    {
        var stencil = new PdfTilingPattern(12, 12, new PdfContentStreamBuilder()
            .Rectangle(2, 2, 8, 8).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(40, 40, new PdfContentStreamBuilder()
                .SetFillPattern(stencil, new PdfRgbColor(0.2, 0.7, 0.4))
                .Rectangle(0, 0, 40, 40).Fill())
            .Build());
        PdfDictionary page = Pages(document)[0];
        PdfStream pageContent = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(page[Name("Contents")])));
        PdfStream pattern = Assert.IsType<PdfStream>(document.Resolve(PatternReference(page)));

        Assert.Equal(2, Assert.IsType<PdfInteger>(pattern.Dictionary[Name("PaintType")]).Value);
        Assert.Equal("[/Pattern /DeviceRGB] cs\n0.2 0.7 0.4 /P1 scn\n0 0 40 40 re\nf\n",
            Encoding.ASCII.GetString(pageContent.EncodedData.Span));
    }

    [Fact]
    public void UncoloredPatternRefusesNestedFormsThatSelectColors()
    {
        var colored = new PdfFormXObject(8, 8,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 0, 8, 8).Fill());
        var outer = new PdfFormXObject(8, 8, new PdfContentStreamBuilder().DrawForm(colored, 0, 0));

        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(8, 8,
            new PdfContentStreamBuilder().DrawForm(colored, 0, 0),
            paintType: PdfTilingPatternPaintType.Uncolored));
        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(8, 8,
            new PdfContentStreamBuilder().DrawForm(outer, 0, 0),
            paintType: PdfTilingPatternPaintType.Uncolored));
    }

    [Fact]
    public void UncoloredPatternRefusesImagesDrawnDirectlyOrInsideNestedForms()
    {
        PdfImage image = PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 });
        var holder = new PdfFormXObject(8, 8, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 8, 8));
        var outer = new PdfFormXObject(8, 8, new PdfContentStreamBuilder().DrawForm(holder, 0, 0));

        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(8, 8,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 8, 8),
            paintType: PdfTilingPatternPaintType.Uncolored));
        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(8, 8,
            new PdfContentStreamBuilder().DrawForm(holder, 0, 0),
            paintType: PdfTilingPatternPaintType.Uncolored));
        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(8, 8,
            new PdfContentStreamBuilder().DrawForm(outer, 0, 0),
            paintType: PdfTilingPatternPaintType.Uncolored));
    }

    [Fact]
    public void UncoloredPatternStillAcceptsNestedFormsThatOnlyPaintShapes()
    {
        var shape = new PdfFormXObject(8, 8, new PdfContentStreamBuilder().Rectangle(1, 1, 6, 6).Fill());

        var stencil = new PdfTilingPattern(8, 8, new PdfContentStreamBuilder().DrawForm(shape, 0, 0),
            paintType: PdfTilingPatternPaintType.Uncolored);

        Assert.Equal(PdfTilingPatternPaintType.Uncolored, stencil.PaintType);
    }

    [Fact]
    public void Build_WritesUncoloredStencilWithDeviceCmykBaseColor()
    {
        var stencil = new PdfTilingPattern(6, 6,
            new PdfContentStreamBuilder().Rectangle(1, 1, 4, 4).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);

        Assert.Equal("[/Pattern /DeviceCMYK] cs\n0.1 0.2 0.3 0.4 /P1 scn\n",
            Encoding.ASCII.GetString(new PdfContentStreamBuilder()
                .SetFillPattern(stencil, new PdfCmykColor(0.1, 0.2, 0.3, 0.4)).Build()));
    }

    [Fact]
    public void Build_UsesColoredPatternForStroke()
    {
        var pattern = new PdfTilingPattern(5, 5, new PdfContentStreamBuilder()
            .SetFillRgb(0.8, 0.2, 0.1).Rectangle(0, 0, 3, 3).Fill());

        Assert.Equal("/Pattern CS\n/P1 SCN\n0 0 20 20 re\nS\n",
            Encoding.ASCII.GetString(new PdfContentStreamBuilder()
                .SetStrokePattern(pattern).Rectangle(0, 0, 20, 20).Stroke().Build()));
    }

    [Fact]
    public void Build_UsesDeviceAndCalibratedBaseColorsForPatternStrokes()
    {
        var stencil = new PdfTilingPattern(6, 6,
            new PdfContentStreamBuilder().Rectangle(1, 1, 4, 4).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        var calibrated = new PdfCalRgbColorSpace();
        var content = new PdfContentStreamBuilder()
            .SetStrokePattern(stencil, new PdfRgbColor(0.1, 0.3, 0.7))
            .SetStrokePattern(stencil, new PdfCmykColor(0.1, 0.2, 0.3, 0.4))
            .SetStrokePattern(stencil, calibrated, 0.2, 0.4, 0.6);

        Assert.Equal(
            "[/Pattern /DeviceRGB] CS\n0.1 0.3 0.7 /P1 SCN\n" +
            "[/Pattern /DeviceCMYK] CS\n0.1 0.2 0.3 0.4 /P1 SCN\n" +
            "[/Pattern /CS1] CS\n0.2 0.4 0.6 /P1 SCN\n",
            Encoding.ASCII.GetString(content.Build()));
    }

    [Fact]
    public void Build_UsesSpotLabAndIndexedPatternBaseColors()
    {
        var stencil = new PdfTilingPattern(6, 6,
            new PdfContentStreamBuilder().Rectangle(1, 1, 4, 4).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        var spot = new PdfSpotColor("Killer Orange", new PdfCmykColor(0, 0.65, 1, 0));
        var lab = new PdfLabColorSpace();
        var indexed = new PdfIndexedColorSpace(
            PdfIndexedBaseColorSpace.Rgb, new byte[] { 255, 0, 0, 0, 0, 255 });
        var content = new PdfContentStreamBuilder()
            .SetFillPattern(stencil, spot, 0.75)
            .SetStrokePattern(stencil, lab, 60, 25, -30)
            .SetFillPattern(stencil, indexed, 1);

        Assert.Equal(
            "[/Pattern /CS1] cs\n0.75 /P1 scn\n" +
            "[/Pattern /CS2] CS\n60 25 -30 /P1 SCN\n" +
            "[/Pattern /CS3] cs\n1 /P1 scn\n",
            Encoding.ASCII.GetString(content.Build()));
    }

    [Fact]
    public void Build_UsesDeviceGrayPatternBaseColorForFillAndStroke()
    {
        var stencil = new PdfTilingPattern(6, 6,
            new PdfContentStreamBuilder().Rectangle(1, 1, 4, 4).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        var content = new PdfContentStreamBuilder()
            .SetFillPattern(stencil, 0.2)
            .SetStrokePattern(stencil, 0.8);

        Assert.Equal(
            "[/Pattern /DeviceGray] cs\n0.2 /P1 scn\n" +
            "[/Pattern /DeviceGray] CS\n0.8 /P1 SCN\n",
            Encoding.ASCII.GetString(content.Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfContentStreamBuilder().SetFillPattern(stencil, -0.1));
    }

    [Fact]
    public void Pattern_RejectsInvalidGeometryAndTaggedContent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfTilingPattern(0, 10, new PdfContentStreamBuilder()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfTilingPattern(10, 10, new PdfContentStreamBuilder(), horizontalStep: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfTilingPattern(10, 10, new PdfContentStreamBuilder(),
                tilingType: (PdfTilingPatternType)4));
        Assert.Throws<ArgumentException>(() =>
            new PdfPatternMatrix(1, 2, 2, 4, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfTilingPattern(10, 10, new PdfContentStreamBuilder(),
                matrix: default(PdfPatternMatrix)));
        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(10, 10,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored));
        var tagged = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0).Rectangle(0, 0, 1, 1).Fill()
            .EndMarkedContent();
        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(10, 10, tagged));

        var colored = new PdfTilingPattern(2, 2, new PdfContentStreamBuilder());
        var uncolored = new PdfTilingPattern(2, 2, new PdfContentStreamBuilder(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        Assert.Throws<ArgumentException>(() =>
            new PdfContentStreamBuilder().SetFillPattern(uncolored));
        Assert.Throws<ArgumentException>(() =>
            new PdfContentStreamBuilder().SetFillPattern(colored, new PdfRgbColor(0, 0, 0)));
    }

    private static PdfContentStreamBuilder Fill(
        PdfTilingPattern pattern, double x, double y, double width, double height) =>
        new PdfContentStreamBuilder().SetFillPattern(pattern).Rectangle(x, y, width, height).Fill();

    private static PdfIndirectReference PatternReference(PdfDictionary page)
    {
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary patterns = Assert.IsType<PdfDictionary>(resources[Name("Pattern")]);
        return Assert.IsType<PdfIndirectReference>(patterns[Name("P1")]);
    }

    private static PdfDictionary[] Pages(PdfDocument document)
    {
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        return [.. Assert.IsType<PdfArray>(pages[Name("Kids")]).Select(value => ResolveDictionary(document, value))];
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
