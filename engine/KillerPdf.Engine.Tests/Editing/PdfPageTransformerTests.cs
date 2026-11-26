using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

// The Transform tool's vector path: a page is turned, scaled or mirrored by one matrix around its own content,
// so its text stays text and its page object (and the structure tree that points at it) stays the same.
public sealed class PdfPageTransformerTests
{
    // A quarter turn clockwise of a 300 x 400 page: (x, y) lands on (y, 300 - x) of a 400 x 300 page.
    private static readonly PdfPageTransform QuarterTurn = new(0, -1, 1, 0, 0, 300, 400, 300);
    private static readonly PdfPageTransform Identity = new(1, 0, 0, 1, 0, 0, 300, 400);

    private static PdfContentStreamBuilder Text() => new PdfContentStreamBuilder().BeginText()
        .SetFont(PdfStandardFont.Helvetica, 16).MoveText(20, 300).ShowLatin1Text("Selectable text").EndText();

    private static byte[] Transform(byte[] source, int page, PdfPageTransform transform) =>
        PdfPageTransformer.Transform(PdfDocument.Open(source),
            new Dictionary<int, PdfPageTransform> { [page] = transform });

    [Fact]
    public void ATurnedPageKeepsItsTextSelectableAndTheTextTurnsWithIt()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400, Text()).Build();
        var before = new PdfPageContentReader(PdfDocument.Open(source)).Read(0);

        byte[] output = Transform(source, 0, QuarterTurn);

        PdfDocument document = PdfDocument.Open(output);
        var info = Assert.Single(PdfPageInformation.Read(document));
        Assert.Equal(400, info.Width, 3);
        Assert.Equal(300, info.Height, 3);
        var after = new PdfPageContentReader(document).Read(0);
        Assert.Equal(before.Text, after.Text);
        Assert.Contains("Selectable", after.Text);
        PdfContentBounds was = before.Letters[0].BoundingBox;
        PdfContentBounds now = after.Letters[0].BoundingBox;
        Assert.Equal(was.Bottom, now.Left, 1);
        Assert.Equal(300 - was.Right, now.Bottom, 1);
    }

    [Fact]
    public void TheSamePageObjectIsKeptAndOtherPagesAreLeftAlone()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400, Text()).AddBlankPage(200, 100).Build();
        PdfDocument original = PdfDocument.Open(source);

        PdfDocument document = PdfDocument.Open(Transform(source, 0, QuarterTurn));

        Assert.Equal(PdfPageTree.Read(original).Pages[0].Reference.ObjectNumber,
            PdfPageTree.Read(document).Pages[0].Reference.ObjectNumber);
        var other = PdfPageInformation.Read(document)[1];
        Assert.Equal(200, other.Width, 3);
        Assert.Equal(100, other.Height, 3);
    }

    // Markup and form fields are painted into the page the way they showed, as the raster path rendered them in,
    // so an unchanged geometry renders the same pixels.
    [Fact]
    public void MarkupAndFieldsArePaintedIntoThePageAndLookTheSame()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400, Text())
            .AddTextField(0, "Name", 20, 20, 120, 24, "Ada")
            .AddInkAnnotation(0, new PdfPoint[] { new(20, 100), new(40, 120), new(80, 100) })
            .AddHighlight(0, 20, 296, 110, 20)
            .Build();
        var options = new PdfRenderOptions(300, 400);
        PdfDocument original = PdfDocument.Open(source);
        Assert.Equal(3, PdfAnnotationsOf(original, 0).Count);
        var expected = new PdfPageRenderer(original).Render(0, options);
        var blank = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 400, Text()).Build()))
            .Render(0, options);
        Assert.True(MeanDifference(expected.Pixels.Span, blank.Pixels.Span) > 0.5);

        byte[] output = Transform(source, 0, Identity);

        PdfDocument document = PdfDocument.Open(output);
        Assert.Empty(PdfAnnotationsOf(document, 0));
        Assert.Empty(PdfFormWidgetReader.ReadPage(document, 0));
        var actual = new PdfPageRenderer(document).Render(0, options);
        Assert.True(MeanDifference(expected.Pixels.Span, actual.Pixels.Span) < 0.5,
            $"mean difference {MeanDifference(expected.Pixels.Span, actual.Pixels.Span)}");
    }

    [Fact]
    public void LinksStayLinksAndMoveWithThePage()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400, Text())
            .AddUriLink(0, 20, 300, 100, 20, "https://example.com/").Build();

        PdfDocument document = PdfDocument.Open(Transform(source, 0, QuarterTurn));

        PdfDictionary link = Assert.Single(PdfAnnotationsOf(document, 0));
        double[] rect = [.. ((PdfArray)Resolve(document, link[new PdfName("Rect"u8)])).Select(value => Number(document, value))];
        Assert.Equal([300, 180, 320, 280], rect.Select(value => Math.Round(value, 3)));
    }

    // The whole picture turns: what was top left on the screen is top right after a quarter turn clockwise.
    [Fact]
    public void ATurnedPageRendersTheOriginalTurned()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400, new PdfContentStreamBuilder()
            .SetFillGray(0).Rectangle(0, 300, 100, 100).Fill()).Build();

        PdfDocument document = PdfDocument.Open(Transform(source, 0, QuarterTurn));
        var page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(400, 300));

        Assert.True(Dark(page, 350, 50));
        Assert.False(Dark(page, 50, 50));
        Assert.False(Dark(page, 350, 250));
    }

    private static bool Dark(PdfRenderedPage page, int x, int y)
    {
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        int index = (y * page.Width + x) * 4;
        return pixels[index] < 64 && pixels[index + 1] < 64 && pixels[index + 2] < 64;
    }

    private static double MeanDifference(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        Assert.Equal(left.Length, right.Length);
        long total = 0;
        for (int index = 0; index < left.Length; index++) total += Math.Abs(left[index] - right[index]);
        return (double)total / left.Length;
    }

    private static IReadOnlyList<PdfDictionary> PdfAnnotationsOf(PdfDocument document, int pageIndex)
    {
        PdfPageTreeEntry page = PdfPageTree.Read(document).Pages[pageIndex];
        if (!page.Dictionary.TryGetValue(new PdfName("Annots"u8), out PdfObject? value)) return [];
        return [.. ((PdfArray)Resolve(document, value)).Select(item => (PdfDictionary)Resolve(document, item))];
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? Resolve(document, document.Resolve(reference)) : value;

    private static double Number(PdfDocument document, PdfObject value) => Resolve(document, value) switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new InvalidOperationException()
    };
}
