using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfOutputIntentPrecisionFrameTests
{
    // Synthetic affine XYZ fixture with frozen independent Decimal60 PCS oracles.
    private const string ProfileBase64 =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";

    [Fact]
    public void CatalogCmykOutputIntentInterpretsDeviceCmykWithoutDestinationSimulation()
    {
        var renderer = Renderer("0.50000762951094835 0.50000762951094835 0.50000762951094835 0.50000762951094835 k 0 0 1 1 re f");

        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1));

        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertPixel(frame, 0, 0.25656640931229085f, 0.24270025515688283f, 0.31217781759250244f);
    }

    [Theory]
    [InlineData("0.25 g")]
    [InlineData("0.25 0.25 0.25 rg")]
    public void CatalogPrinterDoesNotSimulateGrayOrRgbViewingSources(string paint)
    {
        var frame = Renderer(paint + " 0 0 1 1 re f").RenderFrame(0, new PdfRenderOptions(1, 1));

        AssertPixel(frame, 0, .05087608817155679f, .05087608817155679f, .05087608817155679f);
    }

    [Fact]
    public void ResourceDefaultCmykPrecedesAConflictingCatalogProfile()
    {
        var frame = Renderer("0.50000762951094835 0.50000762951094835 0.50000762951094835 0.50000762951094835 k 0 0 1 1 re f",
            scenario: "default").RenderFrame(0, new PdfRenderOptions(1, 1));

        AssertPixel(frame, 0, .25656640931229085f, .24270025515688283f, .31217781759250244f);
    }

    [Fact]
    public void CatalogCmykImageRetainsAdjacentSixteenBitSourceLevels()
    {
        var frame = Renderer("2 0 0 1 0 0 cm /Image Do", scenario: "image", width: 2)
            .RenderFrame(0, new PdfRenderOptions(2, 1));

        AssertPixel(frame, 0, .25656640931229085f, .24270025515688283f, .31217781759250244f);
        AssertPixel(frame, 1, .25655445469789561f, .24270398889375892f, .31217754305628471f);
        Assert.True(frame.Pixels[0] > frame.Pixels[4]);
        byte[] quantized = frame.ToSrgbBgra32();
        Assert.Equal(quantized[..4], quantized[4..]);
    }

    [Theory]
    [InlineData("page-rgb")]
    [InlineData("form-rgb")]
    public void CatalogIntentPreservesSupportedRgbGroups(string scenario)
    {
        var frame = Renderer(scenario == "form-rgb" ? "/Form Do" : "0.25 g 0 0 1 1 re f", scenario)
            .RenderFrame(0, new PdfRenderOptions(1, 1));

        AssertPixel(frame, 0, .05087608817155679f, .05087608817155679f, .05087608817155679f);
    }

    [Theory]
    [InlineData("page-cmyk")]
    [InlineData("page-knockout")]
    [InlineData("page-profiled")]
    [InlineData("page-rgb-profiled")]
    [InlineData("form-cmyk")]
    [InlineData("form-knockout")]
    [InlineData("form-profiled")]
    [InlineData("form-rgb-profiled")]
    public void CatalogIntentDoesNotEnableUnsupportedGroups(string scenario)
    {
        var renderer = Renderer(scenario.StartsWith("form", StringComparison.Ordinal) ? "/Form Do" : "0.25 g 0 0 1 1 re f", scenario);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    [Theory]
    [InlineData("overprint")]
    [InlineData("spot")]
    [InlineData("devicen")]
    [InlineData("process-devicen")]
    [InlineData("process-devicen-stroke")]
    public void CatalogIntentDoesNotEnableUnsupportedPaints(string scenario)
    {
        bool stroke = scenario.EndsWith("-stroke", StringComparison.Ordinal);
        if (stroke) scenario = scenario[..^7];
        string content = stroke ? "/Spot CS 0.5 SCN 1 w 0 0 m 1 1 l S"
            : scenario == "overprint" ? "/Overprint gs 0.25 g 0 0 1 1 re f"
                : "/Spot cs 0.5 scn 0 0 1 1 re f";

        Assert.Throws<NotSupportedException>(() => Renderer(content, scenario)
            .RenderFrame(0, new PdfRenderOptions(1, 1)));
    }

    [Fact]
    public void CatalogIntentDoesNotEnableSeparationFrames()
    {
        Assert.Throws<NotSupportedException>(() => Renderer("1 0 0 0 k 0 0 1 1 re f")
            .RenderFrame(0, new PdfRenderOptions(1, 1) { SeparationPlate = "Cyan" }));
    }

    [Fact]
    public void NonpaintingNoneSeparationRemainsOmitted()
    {
        var frame = Renderer("/Spot cs 0.5 scn 0 0 1 1 re f", "none")
            .RenderFrame(0, new PdfRenderOptions(1, 1));

        AssertPixel(frame, 0, 1, 1, 1);
    }

    private static PdfPageRenderer Renderer(string content, string? scenario = null, int width = 1)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, 1,
            Encoding.ASCII.GetBytes(content)).Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(4))]),
            Convert.FromBase64String(ProfileBase64)));
        PdfIndirectReference catalogProfile = profile;
        var resources = new List<KeyValuePair<PdfName, PdfObject>>();
        var iccSpace = new PdfArray([Name("ICCBased"), profile]);
        if (scenario == "default")
        {
            using Stream bundled = typeof(PdfPageRenderer).Assembly.GetManifestResourceStream(
                "KillerPdf.Engine.ColorProfiles.CGATS001Compat-v2-micro.icc")!;
            using var buffer = new MemoryStream();
            bundled.CopyTo(buffer);
            catalogProfile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(4))]), buffer.ToArray()));
            resources.Add(Entry("ColorSpace", new PdfDictionary([Entry("DefaultCMYK", iccSpace)])));
        }
        if (scenario == "image")
        {
            var image = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
                Entry("ColorSpace", Name("DeviceCMYK")), Entry("BitsPerComponent", new PdfInteger(16))]),
                [128, 0, 128, 0, 128, 0, 128, 0, 128, 1, 128, 0, 128, 0, 128, 0]));
            resources.Add(Entry("XObject", new PdfDictionary([Entry("Image", image)])));
        }
        if (scenario == "overprint")
            resources.Add(Entry("ExtGState", new PdfDictionary([Entry("Overprint", new PdfDictionary([
                Entry("op", new PdfBoolean(true)), Entry("OP", new PdfBoolean(true))]))])));
        if (scenario is "spot" or "devicen" or "process-devicen" or "none")
        {
            var tint = new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
                Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(1, 0, 0, 0)), Entry("N", new PdfInteger(1))]);
            PdfObject colorants = scenario is "spot" or "none" ? Name(scenario == "none" ? "None" : "Orange")
                : new PdfArray([Name(scenario == "process-devicen" ? "Cyan" : "Orange")]);
            var spot = new PdfArray([Name(scenario is "spot" or "none" ? "Separation" : "DeviceN"), colorants, Name("DeviceCMYK"), tint]);
            resources.Add(Entry("ColorSpace", new PdfDictionary([Entry("Spot", spot)])));
        }
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var pageEntries = original.Where(pair => !pair.Key.Equals(Name("Resources"))).ToList();
        if (scenario?.StartsWith("page-", StringComparison.Ordinal) == true
            || scenario?.StartsWith("form-", StringComparison.Ordinal) == true)
        {
            PdfObject groupSpace = scenario.EndsWith("profiled", StringComparison.Ordinal) ? iccSpace
                : Name(scenario.EndsWith("cmyk", StringComparison.Ordinal) ? "DeviceCMYK" : "DeviceRGB");
            if (scenario.EndsWith("rgb-profiled", StringComparison.Ordinal))
            {
                var rgbProfile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(3))]),
                    Convert.FromBase64String(PdfPrecisionIccImageTests.RgbProfileBase64)));
                groupSpace = new PdfArray([Name("ICCBased"), rgbProfile]);
            }
            var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", groupSpace),
                Entry("I", new PdfBoolean(true)), Entry("K", new PdfBoolean(scenario.EndsWith("knockout", StringComparison.Ordinal)))]);
            if (scenario.StartsWith("page-", StringComparison.Ordinal)) pageEntries.Add(Entry("Group", group));
            else
            {
                var form = update.AddObject(new PdfStream(new PdfDictionary([
                    Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 1, 1)),
                    Entry("Group", group), Entry("Resources", new PdfDictionary([]))]),
                    Encoding.ASCII.GetBytes("0.25 g 0 0 1 1 re f")));
                resources.Add(Entry("XObject", new PdfDictionary([Entry("Form", form)])));
            }
        }
        pageEntries.Add(Entry("Resources", new PdfDictionary(resources)));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(pageEntries));
        var root = (PdfIndirectReference)source.Trailer[Name("Root")];
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(root));
        update.ReplaceObject(root.ObjectNumber, new PdfDictionary(catalog.Append(Entry("OutputIntents",
            new PdfArray([new PdfDictionary([Entry("S", Name("GTS_PDFX")), Entry("DestOutputProfile", catalogProfile)])])))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build()));
    }

    private static void AssertPixel(PdfRenderedFrame frame, int pixel, float red, float green, float blue)
    {
        const float tolerance = 0.000002f;
        float[] expected = [red, green, blue];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[pixel * 4 + channel], expected[channel] - tolerance, expected[channel] + tolerance);
        Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
