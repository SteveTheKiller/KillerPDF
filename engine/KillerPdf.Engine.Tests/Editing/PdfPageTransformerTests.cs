using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
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

    // A field repeated on another page keeps working there; only the turned page's widget is painted and taken out.
    [Fact]
    public void AFieldWithAWidgetOnAnUntouchedPageKeepsThatWidget()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400, Text()).AddPage(300, 400, Text())
            .AddRadioGroup("Choice", [new PdfRadioButtonOption(0, 20, 20, 20, 20, "A"),
                new PdfRadioButtonOption(1, 20, 20, 20, 20, "B")], "B")
            .Build();

        PdfDocument document = PdfDocument.Open(Transform(source, 0, QuarterTurn));

        Assert.Empty(PdfFormWidgetReader.ReadPage(document, 0));
        PdfFormWidgetInfo kept = Assert.Single(PdfFormWidgetReader.ReadPage(document, 1));
        Assert.Equal("Choice", kept.FieldName);
        Assert.Equal(PdfFormWidgetReader.ReadPage(PdfDocument.Open(source), 1)[0].Value, kept.Value);
        PdfDictionary parent = (PdfDictionary)Resolve(document,
            ((PdfDictionary)Resolve(document, new PdfIndirectReference(kept.ObjectNumber, kept.Generation)))[new PdfName("Parent"u8)]);
        Assert.Single((PdfArray)Resolve(document, parent[new PdfName("Kids"u8)]));
    }

    // Annotations without /AP show the way the page render drew them: legacy markup gets the appearance the
    // renderer builds, and a text field gets its value regenerated.
    [Fact]
    public void AnnotationsWithoutAnAppearanceArePaintedAsTheyShowed()
    {
        byte[] built = new PdfDocumentBuilder().AddPage(300, 400, Text())
            .AddTextField(0, "Name", 20, 20, 120, 24, "Ada").Build();
        PdfDocument start = PdfDocument.Open(built);
        PdfFormWidgetInfo field = Assert.Single(PdfFormWidgetReader.ReadPage(start, 0));
        PdfDictionary widget = (PdfDictionary)Resolve(start, new PdfIndirectReference(field.ObjectNumber, field.Generation));
        PdfPageTreeEntry page = PdfPageTree.Read(start).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(start);
        update.ReplaceObject(field.ObjectNumber, new PdfDictionary(widget.Where(item => !item.Key.Equals(new PdfName("AP"u8)))));
        // A generator that leaves appearances to the viewer sets /NeedAppearances, so the page render builds one.
        PdfPageTree startTree = PdfPageTree.Read(start);
        PdfObject formValue = startTree.Catalog[new PdfName("AcroForm"u8)];
        var form = new PdfDictionary(((PdfDictionary)Resolve(start, formValue))
            .Where(item => !item.Key.Equals(new PdfName("NeedAppearances"u8)))
            .Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("NeedAppearances"u8), new PdfBoolean(true))));
        if (formValue is PdfIndirectReference formReference) update.ReplaceObject(formReference.ObjectNumber, form);
        else update.ReplaceObject(startTree.CatalogReference.ObjectNumber, new PdfDictionary(startTree.Catalog
            .Where(item => !item.Key.Equals(new PdfName("AcroForm"u8)))
            .Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("AcroForm"u8), form))));
        PdfIndirectReference square = update.AddObject(new PdfDictionary([
            new(new PdfName("Type"u8), new PdfName("Annot"u8)),
            new(new PdfName("Subtype"u8), new PdfName("Square"u8)),
            new(new PdfName("Rect"u8), new PdfArray([new PdfInteger(150), new PdfInteger(50), new PdfInteger(250), new PdfInteger(150)])),
            new(new PdfName("C"u8), new PdfArray([new PdfInteger(1), new PdfInteger(0), new PdfInteger(0)])),
            new(new PdfName("IC"u8), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(1)]))]));
        var annotations = ((PdfArray)Resolve(start, page.Dictionary[new PdfName("Annots"u8)])).Append(square);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(page.Dictionary
            .Where(item => !item.Key.Equals(new PdfName("Annots"u8)))
            .Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Annots"u8), new PdfArray([.. annotations])))));
        byte[] source = update.Build();
        var options = new PdfRenderOptions(300, 400);
        var expected = new PdfPageRenderer(PdfDocument.Open(source)).Render(0, options);
        var blank = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 400, Text()).Build()))
            .Render(0, options);
        // The field's box (y 356 to 380 on screen) and the square's both show before the transform.
        Assert.True(RegionDifference(expected, blank, 20, 356, 140, 380) > 5);
        Assert.True(RegionDifference(expected, blank, 150, 250, 250, 350) > 5);

        PdfDocument document = PdfDocument.Open(Transform(source, 0, Identity));

        Assert.Empty(PdfAnnotationsOf(document, 0));
        var actual = new PdfPageRenderer(document).Render(0, options);
        Assert.True(RegionDifference(expected, actual, 20, 356, 140, 380) < 1,
            $"field difference {RegionDifference(expected, actual, 20, 356, 140, 380)}");
        Assert.True(RegionDifference(expected, actual, 150, 250, 250, 350) < 1,
            $"square difference {RegionDifference(expected, actual, 150, 250, 250, 350)}");
    }

    private static double RegionDifference(PdfRenderedPage left, PdfRenderedPage right, int x0, int y0, int x1, int y1)
    {
        ReadOnlySpan<byte> a = left.Pixels.Span, b = right.Pixels.Span;
        long total = 0, count = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                for (int channel = 0; channel < 3; channel++, count++)
                {
                    int index = (y * left.Width + x) * 4 + channel;
                    total += Math.Abs(a[index] - b[index]);
                }
        return (double)total / count;
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

    // Real producers write more Q than q. Those extra restores must not pop the matrix the transform inserted, so
    // what follows them still turns with the page.
    [Fact]
    public void ContentWithExtraRestoresStillTurnsWithThePage()
    {
        // The builder refuses an unbalanced restore, so the content is written as plain bytes.
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400,
            "Q Q 0 g 0 300 100 100 re f"u8.ToArray()).Build();

        PdfDocument document = PdfDocument.Open(Transform(source, 0, QuarterTurn));
        var page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(400, 300));

        Assert.True(Dark(page, 350, 50));
        Assert.False(Dark(page, 50, 50));
        Assert.False(Dark(page, 350, 250));
    }

    // Destinations that point into a page keep working after the page is turned, because the transform moves
    // their coordinates the way it moves the page. Destinations into a page that is not transformed stay as they are.
    private static byte[] DestinationSource() => new PdfDocumentBuilder()
        .AddPage(300, 400, Text()).AddBlankPage(200, 100)
        .AddBookmark("xyz", 0, 0, new PdfBookmarkOptions { Destination = PdfDestination.At(50, 350, 2) })
        .AddBookmark("fith", 0, 0, new PdfBookmarkOptions { Destination = PdfDestination.FitWidth(350) })
        .AddBookmark("fitr", 0, 0, new PdfBookmarkOptions { Destination = PdfDestination.FitRectangle(10, 20, 110, 220) })
        .AddBookmark("fit", 0, 0, new PdfBookmarkOptions { Destination = PdfDestination.FitPage() })
        .AddBookmark("other", 1, 0, new PdfBookmarkOptions { Destination = PdfDestination.At(5, 6) })
        .AddNamedDestination("turned", 0, PdfDestination.At(50, 350))
        .AddNamedDestination("kept", 1, PdfDestination.At(5, 6))
        .AddNamedDestinationBookmark("named turned", "turned")
        .AddNamedDestinationBookmark("named kept", "kept")
        .AddPageLink(1, 10, 10, 50, 20, 0, destination: PdfDestination.At(50, 350))
        .AddPageLink(1, 70, 10, 50, 20, 1, destination: PdfDestination.At(5, 6))
        .Build();

    private static PdfDestination BookmarkDestination(PdfDocument document, string title) =>
        PdfBookmarkReader.Read(document).Single(item => item.Title == title).Destination!;

    private static void AssertDestination(PdfDestination destination, PdfDestinationKind kind, params double?[] values)
    {
        Assert.Equal(kind, destination.Kind);
        Assert.Equal(values.Length, destination.Values.Count);
        for (int index = 0; index < values.Length; index++)
            if (values[index].HasValue) Assert.Equal(values[index]!.Value, destination.Values[index]!.Value, 3);
            else Assert.Null(destination.Values[index]);
    }

    private static string[] LinkDestinations(PdfDocument document, int pageIndex) =>
        [.. PdfAnnotationsOf(document, pageIndex).Select(link =>
        {
            PdfObject value = link.TryGetValue(new PdfName("Dest"u8), out PdfObject? direct) ? direct
                : ((PdfDictionary)Resolve(document, link[new PdfName("A"u8)]))[new PdfName("D"u8)];
            var array = (PdfArray)Resolve(document, value);
            return string.Join(' ', array.Skip(1).Select(item => Resolve(document, item) switch
            {
                PdfName name => name.ValueAsLatin1(),
                PdfNull => "null",
                var number => Math.Round(Number(document, number), 3).ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
        })];

    [Fact]
    public void OutlineDestinationsIntoATurnedPageMoveWithIt()
    {
        PdfDocument document = PdfDocument.Open(Transform(DestinationSource(), 0, QuarterTurn));

        AssertDestination(BookmarkDestination(document, "xyz"), PdfDestinationKind.Xyz, 350, 250, 2);
        AssertDestination(BookmarkDestination(document, "fit"), PdfDestinationKind.Fit);
    }

    [Fact]
    public void AFitWidthDestinationBecomesAFitHeightDestinationWhenThePageTurns()
    {
        PdfDocument document = PdfDocument.Open(Transform(DestinationSource(), 0, QuarterTurn));

        AssertDestination(BookmarkDestination(document, "fith"), PdfDestinationKind.FitV, 350);
    }

    [Fact]
    public void AFitRectangleDestinationIsNormalizedAfterTheTurn()
    {
        PdfDocument document = PdfDocument.Open(Transform(DestinationSource(), 0, QuarterTurn));

        AssertDestination(BookmarkDestination(document, "fitr"), PdfDestinationKind.FitR, 20, 190, 220, 290);
    }

    [Fact]
    public void NamedDestinationsAreMovedWhereTheyAreStored()
    {
        PdfDocument document = PdfDocument.Open(Transform(DestinationSource(), 0, QuarterTurn));

        AssertDestination(BookmarkDestination(document, "named turned"), PdfDestinationKind.Xyz, 350, 250, null);
        AssertDestination(BookmarkDestination(document, "named kept"), PdfDestinationKind.Xyz, 5, 6, null);
    }

    [Fact]
    public void ALinkOnAnotherPageFollowsTheTurnedPageItTargets()
    {
        PdfDocument document = PdfDocument.Open(Transform(DestinationSource(), 0, QuarterTurn));

        Assert.Equal(["XYZ 350 250 null", "XYZ 5 6 null"], LinkDestinations(document, 1));
    }

    [Fact]
    public void DestinationsIntoAPageThatIsNotTransformedDoNotChange()
    {
        PdfDocument document = PdfDocument.Open(Transform(DestinationSource(), 0, QuarterTurn));

        AssertDestination(BookmarkDestination(document, "other"), PdfDestinationKind.Xyz, 5, 6, null);
    }

    [Fact]
    public void ALinkOnTheTurnedPageItselfKeepsPointingAtTheRightSpot()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400, Text())
            .AddPageLink(0, 10, 10, 50, 20, 0, destination: PdfDestination.At(50, 350)).Build();

        PdfDocument document = PdfDocument.Open(Transform(source, 0, QuarterTurn));

        Assert.Equal(["XYZ 350 250 null"], LinkDestinations(document, 0));
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
