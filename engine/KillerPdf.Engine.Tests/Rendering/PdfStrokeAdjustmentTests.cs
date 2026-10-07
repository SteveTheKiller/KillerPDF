using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfStrokeAdjustmentTests
{
    public static IEnumerable<object[]> AxisWidths()
    {
        foreach (bool vertical in new[] { false, true })
        foreach (double width in new[] { 0.25, 0.75, 1.25, 1.75, 2.25, 2.75 })
        foreach (double position in new[] { 20.1, 20.6 })
            yield return [vertical, width, position];
    }

    [Theory]
    [MemberData(nameof(AxisWidths))]
    public void AdjustedAxisStrokeHasNearestWholePixelWidth(bool vertical, double width, double position)
    {
        int pixels = Math.Max(1, (int)Math.Floor(width + 0.5));
        double center = vertical ? position : 100 - position;
        center = pixels % 2 == 1 ? Math.Floor(center) + 0.5 : Math.Floor(center + 0.5);
        double edge = center - pixels / 2d;
        string path = vertical ? $"{N(position)} 10 m {N(position)} 90 l S"
            : $"10 {N(position)} m 90 {N(position)} l S";
        var actual = Render(Document($"/Adjust gs {N(width)} w {path}", new PdfBoolean(true)));
        string rectangle = vertical ? $"{N(edge)} 10 {pixels} 80 re f"
            : $"10 {N(100 - edge - pixels)} 80 {pixels} re f";
        var expected = Render(PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes(rectangle)).Build()));
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Fact]
    public void FalseStrokeAdjustmentPreservesDefaultPixels()
    {
        const string path = "0.25 w 20.1 10 m 20.1 90 l S";
        Assert.Equal(Render(Document(path, new PdfBoolean(false))).Pixels.ToArray(),
            Render(Document("/Adjust gs " + path, new PdfBoolean(false))).Pixels.ToArray());
    }

    [Fact]
    public void SaveRestoreKeepsAdjustmentScoped()
    {
        var actual = Render(Document("q /Adjust gs 0.25 w 20.1 10 m 20.1 90 l S Q 0.25 w 40.1 10 m 40.1 90 l S", new PdfBoolean(true)));
        var expected = Render(Document("20 10 1 80 re f 0.25 w 40.1 10 m 40.1 90 l S", new PdfBoolean(true)));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AdjustedCapsMatchExplicitPixelGeometry(int cap)
    {
        var actual = Render(Document($"/Adjust gs {cap} J 0.25 w 20.1 10 m 20.1 90 l S", new PdfBoolean(true)));
        var expected = Render(Document($"{cap} J 1 w 20.5 10 m 20.5 90 l S", new PdfBoolean(true)));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AdjustedRectangleJoinsMatchExplicitPixelGeometry(int join)
    {
        var actual = Render(Document($"/Adjust gs {join} j 0.25 w 20.1 20.1 60 60 re S", new PdfBoolean(true)));
        var expected = Render(Document($"{join} j 1 w 20.5 20.5 60 60 re S", new PdfBoolean(true)));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Fact]
    public void AdjustmentPreservesDashLengthsAndOriginalClip()
    {
        const string clip = "12.25 10 50.5 70 re W n ";
        var actual = Render(Document(clip + "/Adjust gs [5 3] 0 d 0.25 w 10 20.1 m 90 20.1 l S", new PdfBoolean(true)));
        string rectangles = string.Join(" ", Enumerable.Range(0, 10).Select(i => $"{10+i*8} 20 5 1 re f"));
        var expected = Render(Document(clip + rectangles, new PdfBoolean(true)));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData("2 0 0 2 0 0 cm 0.125 w 10.1 5 m 10.1 45 l S", "20 10 1 80 re f")]
    [InlineData("0 1 -1 0 90 0 cm 0.25 w 10 20.1 m 90 20.1 l S", "69 10 1 80 re f")]
    public void AdjustmentUsesTransformedDeviceCoordinates(string stroke, string fill)
    {
        Assert.Equal(Render(Document(fill, new PdfBoolean(true))).Pixels.ToArray(),
            Render(Document("/Adjust gs " + stroke, new PdfBoolean(true))).Pixels.ToArray());
    }

    [Theory]
    [InlineData("20.1 10 m 80.1 90 l S")]
    [InlineData("20.1 10 m 20.1 50 l 80.1 90 l S")]
    [InlineData("20.1 10 m 25 25 70 70 80.1 90 c S")]
    public void MixedAndCurvedPathsKeepExistingRasterization(string path)
    {
        Assert.Equal(Render(Document("0.25 w " + path, new PdfBoolean(true))).Pixels.ToArray(),
            Render(Document("/Adjust gs 0.25 w " + path, new PdfBoolean(true))).Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NonbooleanFlagPreservesStrictAndRecoveryHandling(bool useName)
    {
        PdfObject invalid = useName ? Name("true") : new PdfInteger(1);
        const string content = "/Adjust gs 0.25 w 20.1 10 m 20.1 90 l S";
        Assert.Throws<FormatException>(() => Render(Document(content, invalid)));
        var actual = Render(Document(content, invalid, recovery: true));
        Assert.Single(actual.Diagnostics);
        Assert.Equal(Render(Document(content.Replace("/Adjust gs", ""), new PdfBoolean(false))).Pixels.ToArray(),
            actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(1)] [InlineData(4)]
    public void LargeStrokeRegionsMatchWholePageAtAnyWorkerCount(int workers)
    {
        var document = Document("/Adjust gs 4 w 20.13 10 m 20.13 90 l S", new PdfBoolean(true));
        var options = new PdfRenderOptions(1024, 1024, includeAnnotations: false, includeFormFields: false)
            { CacheResult = false, MaximumParallelism = workers };
        var renderer = new PdfPageRenderer(document);
        var whole = renderer.Render(0, options);
        var serial = new PdfPageRenderer(document).Render(0, options with { MaximumParallelism = 1 });
        Assert.Equal(serial.Pixels.ToArray(), whole.Pixels.ToArray());
        var region = renderer.RenderRegion(0, options, 180, 150, 80, 600);
        for (int row = 0; row < region.Height; row++)
            Assert.Equal(whole.Pixels.Slice(((150+row)*1024+180)*4, 80*4).ToArray(),
                region.Pixels.Slice(row*80*4, 80*4).ToArray());
    }

    [Theory]
    [InlineData("20.1 20.1 0.2 0.2 re S")]
    [InlineData("20.1 20.1 0.2 10 re S")]
    public void AdjustmentCannotCollapseShortRectangleSegments(string path)
    {
        var expected = Render(Document("0.25 w " + path, new PdfBoolean(true)));
        var actual = Render(Document("/Adjust gs 0.25 w " + path, new PdfBoolean(true)));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Contains(actual.Pixels.ToArray(), value => value != 255);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IndirectAdjustmentFlagMatchesDirectState(bool indirect)
    {
        const string content = "/Adjust gs 0.25 w 20.1 10 m 20.1 90 l S";
        Assert.Equal(Render(Document("20 10 1 80 re f", new PdfBoolean(true))).Pixels.ToArray(),
            Render(Document(content, new PdfBoolean(true), indirect: indirect)).Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SubsequentStateCanPreserveOrDisableAdjustment(bool disable)
    {
        string second = disable ? "/Off gs" : "/Opacity gs";
        var actual = Render(Document($"/Adjust gs {second} 0.25 w 20.1 10 m 20.1 90 l S", new PdfBoolean(true)));
        var expected = Render(Document(disable ? "0.25 w 20.1 10 m 20.1 90 l S" : "20 10 1 80 re f", new PdfBoolean(true)));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FormInheritsAdjustmentAndCannotLeakAnOverride(bool overrideFlag)
    {
        string form = (overrideFlag ? "/Off gs " : "") + "0.25 w 20.1 10 m 20.1 90 l S";
        var actual = Render(Document("/Adjust gs /Fm Do 0.25 w 40.1 10 m 40.1 90 l S", new PdfBoolean(true), form: form));
        string first = overrideFlag ? "0.25 w 20.1 10 m 20.1 90 l S " : "20 10 1 80 re f ";
        var expected = Render(Document(first + "40 10 1 80 re f", new PdfBoolean(true)));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Fact]
    public void ColoredPatternStrokeUsesAdjustedGeometricMask()
    {
        var actual = Render(Document("/Pattern CS /P SCN /Adjust gs 0.25 w 20.1 10 m 20.1 90 l S", new PdfBoolean(true), pattern: true));
        var expected = Render(Document("/Pattern cs /P scn 20 10 1 80 re f", new PdfBoolean(true), pattern: true));
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)]
    [InlineData(true, 0)] [InlineData(true, 1)]
    public void AdjustedCmykOverprintStrokeMatchesExactFilledStripe(bool native, int mode)
    {
        const string backdrop = "1 0 0 0 k 0 0 100 100 re f ";
        var actual = Render(Document(backdrop + "/Adjust gs 0 1 0 0 K 0.25 w 20.1 10 m 20.1 90 l S", new PdfBoolean(true), native: native, overprint: mode));
        var expected = Render(Document(backdrop + "/Adjust gs 0 1 0 0 k 20 10 1 80 re f", new PdfBoolean(true), native: native, overprint: mode));
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    internal static PdfDocument Document(string content, PdfObject adjustment, bool recovery = false,
        bool indirect = false, string? form = null, bool pattern = false, bool native = false, int overprint = -1)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes(content)).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var stateEntries = new List<KeyValuePair<PdfName, PdfObject>>
            { Entry("SA", indirect ? update.AddObject(adjustment) : adjustment) };
        if (overprint >= 0)
        {
            stateEntries.Add(Entry("OP", new PdfBoolean(true)));
            stateEntries.Add(Entry("op", new PdfBoolean(true)));
            stateEntries.Add(Entry("OPM", new PdfInteger(overprint)));
        }
        var states = new PdfDictionary([Entry("Adjust", new PdfDictionary(stateEntries)),
            Entry("Off", new PdfDictionary([Entry("SA", new PdfBoolean(false))])),
            Entry("Opacity", new PdfDictionary([Entry("ca", new PdfReal(0.5))]))]);
        var entries = new List<KeyValuePair<PdfName, PdfObject>> { Entry("ExtGState", states) };
        if (form is not null)
        {
            var dictionary = new PdfDictionary([Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")),
                Entry("BBox", new PdfArray([new PdfInteger(0),new PdfInteger(0),new PdfInteger(100),new PdfInteger(100)])),
                Entry("Resources", new PdfDictionary([Entry("ExtGState", states)]))]);
            var stream = new PdfStream(dictionary, Encoding.ASCII.GetBytes(form));
            entries.Add(Entry("XObject", new PdfDictionary([Entry("Fm", update.AddObject(stream))])));
        }
        if (pattern)
        {
            var dictionary = new PdfDictionary([Entry("Type", Name("Pattern")), Entry("PatternType", new PdfInteger(1)),
                Entry("PaintType", new PdfInteger(1)), Entry("TilingType", new PdfInteger(1)),
                Entry("BBox", new PdfArray([new PdfInteger(0),new PdfInteger(0),new PdfInteger(2),new PdfInteger(2)])),
                Entry("XStep", new PdfInteger(2)), Entry("YStep", new PdfInteger(2)), Entry("Resources", new PdfDictionary([]))]);
            var stream = new PdfStream(dictionary, "1 0 0 rg 0 0 1 2 re f"u8);
            entries.Add(Entry("Pattern", new PdfDictionary([Entry("P", update.AddObject(stream))])));
        }
        var pageEntries = page.Where(pair => !pair.Key.Equals(Name("Resources")) && !pair.Key.Equals(Name("Group")))
            .Append(Entry("Resources", new PdfDictionary(entries)));
        if (native) pageEntries = pageEntries.Append(Entry("Group", new PdfDictionary([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))])));
        byte[] bytes = update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(pageEntries)).Build();
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
    }

    private static PdfRenderedPage Render(PdfDocument document) => new PdfPageRenderer(document)
        .Render(0, new PdfRenderOptions(100, 100, includeAnnotations: false, includeFormFields: false));
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
