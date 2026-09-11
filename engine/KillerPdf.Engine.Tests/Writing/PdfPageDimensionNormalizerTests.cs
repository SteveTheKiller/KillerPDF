using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfPageDimensionNormalizerTests
{
    [Fact]
    public void FindPagesOutsideRange_ReturnsOnlyOutOfRangePages()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(100, 200)
            .AddBlankPage(20_000, 100)
            .AddBlankPage(2, 2)
            .Build());

        Assert.Equal([1, 2],
            PdfPageDimensionNormalizer.FindPagesOutsideRange(document, 3, 14_400));
    }

    [Fact]
    public void NormalizePages_ScalesGeometryContentAndAnnotationCoordinates()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage(20_000, 10_000)
            .SetPageBox(0, PdfPageBox.Crop, 100, 200, 19_000, 9_000)
            .AddHighlight(0, 100, 200, 300, 40)
            .Build();
        PdfDocument original = PdfDocument.Open(source);

        byte[] result = PdfPageDimensionNormalizer.NormalizePages(
            original, [0], 3, 14_400);

        Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
        PdfDocument reopened = PdfDocument.Open(result);
        PdfPageInformation pageInfo = Assert.Single(PdfPageInformation.Read(reopened));
        Assert.Equal(13_680, pageInfo.Width, 6);
        Assert.Equal(6_480, pageInfo.Height, 6);
        PdfDictionary page = FirstPage(reopened);
        PdfArray contents = Assert.IsType<PdfArray>(page[Name("Contents")]);
        Assert.True(contents.Count >= 2);
        PdfStream prefix = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(contents[0])));
        Assert.Contains("q 0.72 0 0 0.72 0 0 cm", Encoding.ASCII.GetString(prefix.EncodedData.Span));
        PdfArray annotations = ResolveArray(reopened, page[Name("Annots")]);
        PdfDictionary annotation = ResolveDictionary(reopened, annotations[0]);
        Assert.Equal([72d, 144d, 288d, 172.8d], Numbers(reopened, annotation[Name("Rect")]),
            new DoubleArrayComparer());
        Assert.All(Numbers(reopened, annotation[Name("QuadPoints")]),
            number => Assert.InRange(number, 0, 14_400));
    }

    [Fact]
    public void NormalizePages_PreservesBytesWhenSelectedPagesAreAlreadyValid()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(100, 200).Build();
        Assert.Equal(source, PdfPageDimensionNormalizer.NormalizePages(
            PdfDocument.Open(source), [0], 3, 14_400));
    }

    [Fact]
    public void NormalizePages_GrowScaleDoesNotPushTheOtherDimensionPastMaximum()
    {
        // A 2 x 14,000 point page needs a 1.5x grow to reach the 3 point
        // minimum, but that would put height at 21,000, past the 14,400 point
        // maximum. No uniform scale can satisfy both bounds, so the page must
        // be left untouched instead of rewritten still out of range.
        byte[] source = new PdfDocumentBuilder().AddBlankPage(2, 14_000).Build();

        byte[] result = PdfPageDimensionNormalizer.NormalizePages(
            PdfDocument.Open(source), [0], 3, 14_400);

        Assert.Equal(source, result);
    }

    [Theory]
    [InlineData(20_000, 2)]
    [InlineData(2, 20_000)]
    [InlineData(20_000, 1)]
    [InlineData(0.5, 30_000)]
    public void NormalizePages_LeavesBytesUnchangedWhenNoUniformScaleFitsRange(
        double width, double height)
    {
        // One dimension exceeds the maximum while the other falls below the
        // minimum: shrinking fixes one bound and breaks the other, and any
        // grow does the reverse. The prior behavior scaled by whichever rule
        // matched first and produced output still outside the supported range
        // (20,000 x 2 scaled to 14,400 x 1.44). Such pages must be reported by
        // FindPagesOutsideRange and then preserved byte for byte.
        byte[] source = new PdfDocumentBuilder().AddBlankPage(width, height).Build();
        PdfDocument original = PdfDocument.Open(source);
        Assert.Equal([0], PdfPageDimensionNormalizer.FindPagesOutsideRange(
            original, 3, 14_400));

        byte[] result = PdfPageDimensionNormalizer.NormalizePages(
            original, [0], 3, 14_400);

        Assert.Equal(source, result);
    }

    [Theory]
    [InlineData(0.06444000000000308, 53)]
    [InlineData(22913.474794679445, 7000)]
    public void NormalizePages_ConvergesInsideRangeDespiteBoundaryRounding(
        double width, double height)
    {
        // The boundary quotient can round back outside the range in binary
        // floating point: 0.06444000000000308 grown by the exact minimum
        // quotient produces a serialized width of 2.9999999999999996, just
        // below the minimum, and 22913.474794679445 shrunk by the exact
        // maximum quotient produces 14400.000000000002, just above it. Both
        // would be flagged by FindPagesOutsideRange after the rewrite, so the
        // factor must be nudged one ulp toward feasibility first.
        byte[] source = new PdfDocumentBuilder().AddBlankPage(width, height).Build();

        byte[] result = PdfPageDimensionNormalizer.NormalizePages(
            PdfDocument.Open(source), [0], 3, 14_400);

        Assert.NotEqual(source, result);
        PdfDocument reopened = PdfDocument.Open(result);
        Assert.Empty(PdfPageDimensionNormalizer.FindPagesOutsideRange(
            reopened, 3, 14_400));
    }

    [Fact]
    public void NormalizePages_ContentTransformFactorMatchesTheVerifiedScale()
    {
        // A 1,000,000,000,000 x 10,000,000,000 point page shrinks by 1.44E-08.
        // The legacy 0.######## factor format truncated that to 0.00000001, so
        // the emitted content stream drew at one tenth of the verified scale
        // while the page box claimed 14,400 x 100 - the content no longer
        // filled its own page. The factor written into the CTM must reproduce
        // the scale that the serialized page boxes use.
        byte[] source = new PdfDocumentBuilder()
            .AddPage(1_000_000_000_000, 10_000_000_000, "q Q\n"u8.ToArray())
            .Build();

        byte[] result = PdfPageDimensionNormalizer.NormalizePages(
            PdfDocument.Open(source), [0], 3, 14_400);

        PdfDocument reopened = PdfDocument.Open(result);
        PdfPageInformation info = Assert.Single(PdfPageInformation.Read(reopened));
        PdfDictionary page = FirstPage(reopened);
        PdfArray contents = Assert.IsType<PdfArray>(page[Name("Contents")]);
        PdfStream prefix = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(contents[0])));
        string stream = System.Text.Encoding.ASCII.GetString(prefix.EncodedData.Span);
        string factor = stream.Split(' ')[1];
        double applied = double.Parse(factor, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(info.Width, 1_000_000_000_000 * applied, 6);
        Assert.Equal(info.Height, 10_000_000_000 * applied, 6);
    }

    private static PdfDictionary FirstPage(PdfDocument document)
    {
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        PdfArray kids = ResolveArray(document, pages[Name("Kids")]);
        return ResolveDictionary(document, kids[0]);
    }

    private static double[] Numbers(PdfDocument document, PdfObject value) =>
        [.. ResolveArray(document, value).Select(item => Resolve(document, item) switch
        {
            PdfInteger integer => (double)integer.Value,
            PdfReal real => real.Value,
            _ => throw new Xunit.Sdk.XunitException("Expected a numeric PDF value.")
        })];

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(Resolve(document, value));
    private static PdfArray ResolveArray(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfArray>(Resolve(document, value));
    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private sealed class DoubleArrayComparer : IEqualityComparer<double[]>
    {
        public bool Equals(double[]? x, double[]? y) => x is not null && y is not null
            && x.Length == y.Length && x.Zip(y).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6);
        public int GetHashCode(double[] obj) => 0;
    }
}
