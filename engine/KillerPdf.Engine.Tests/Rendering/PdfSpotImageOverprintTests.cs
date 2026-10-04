using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfSpotImageOverprintTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ReducedSeparationImage_KeepsNamedSpotUnderWhiteCmykOverprint(int sourceScale)
    {
        PdfRenderedPage reference = Render(sourceScale: 1, clippedSpotImage: false);
        PdfRenderedPage reduced = Render(sourceScale, clippedSpotImage: false);

        byte[] green = Pixel(reference, 0);
        Assert.NotEqual([255, 255, 255, 255], green);
        for (int x = 0; x < 4; x++)
        {
            Assert.Equal(green, Pixel(reference, x));
            Assert.Equal(green, Pixel(reduced, x));
        }
        Assert.Empty(reference.Diagnostics);
        Assert.Empty(reduced.Diagnostics);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ReducedSeparationImage_FractionalClipRetainsSameNamedSpot(int sourceScale)
    {
        PdfRenderedPage reference = Render(sourceScale: 1, clippedSpotImage: true);
        PdfRenderedPage reduced = Render(sourceScale, clippedSpotImage: true);

        byte[] green = Pixel(reference, 0);
        Assert.NotEqual([255, 255, 255, 255], green);
        for (int x = 0; x < 4; x++)
        {
            Assert.Equal(green, Pixel(reference, x));
            Assert.Equal(green, Pixel(reduced, x));
        }
        Assert.Empty(reference.Diagnostics);
        Assert.Empty(reduced.Diagnostics);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ReducedSeparationImage_AveragesAlternatingTintsAndRetainsSpot(int sourceScale)
    {
        PdfRenderedPage halfTint = Render(sourceScale: 1, clippedSpotImage: false,
            uniformTint: 128);
        PdfRenderedPage alternating = Render(sourceScale, clippedSpotImage: false,
            alternatingTints: true);

        byte[] expected = Pixel(halfTint, 0);
        Assert.NotEqual([255, 255, 255, 255], expected);
        Assert.NotEqual(Pixel(Render(sourceScale: 1, clippedSpotImage: false), 0), expected);
        for (int x = 0; x < 4; x++)
        {
            byte[] actual = Pixel(alternating, x);
            for (int channel = 0; channel < 4; channel++)
                Assert.InRange(Math.Abs(actual[channel] - expected[channel]), 0, 1);
        }
        Assert.Empty(halfTint.Diagnostics);
        Assert.Empty(alternating.Diagnostics);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ReducedSeparationImage_ZeroTintIgnoresNonwhiteAlternateStart(int sourceScale)
    {
        PdfRenderedPage whiteStart = Render(sourceScale, clippedSpotImage: false,
            alternatingTints: true);
        PdfRenderedPage nonwhiteStart = Render(sourceScale, clippedSpotImage: false,
            alternatingTints: true, nonwhiteAlternateStart: true);

        for (int x = 1; x <= 2; x++)
        {
            byte[] expected = Pixel(whiteStart, x);
            byte[] actual = Pixel(nonwhiteStart, x);
            Assert.NotEqual([255, 255, 255, 255], expected);
            for (int channel = 0; channel < 4; channel++)
                Assert.InRange(Math.Abs(actual[channel] - expected[channel]), 0, 1);
        }
        Assert.Empty(whiteStart.Diagnostics);
        Assert.Empty(nonwhiteStart.Diagnostics);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ReducedZeroTintSeparationImage_KnocksOutEarlierSpot(int sourceScale)
    {
        PdfRenderedPage rendered = Render(sourceScale, clippedSpotImage: false,
            uniformTint: 0, nonwhiteAlternateStart: true, spotBackdrop: true);

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 2));
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ClippedSpotImage_WithoutOverprintClearsOtherSpotAndProcessInk(int sourceScale)
    {
        PdfRenderedPage beforeWhite = RenderDifferentSpotBackdrop(sourceScale, followingWhite: false);
        PdfRenderedPage afterWhite = RenderDifferentSpotBackdrop(sourceScale, followingWhite: true);

        byte[] untouched = PdfDeviceCmykTests.RenderInk(255, 255);
        byte[] yellow = PdfDeviceCmykTests.RenderInk(0, 0, 255);
        Assert.Equal(untouched, Pixel(beforeWhite, 0));
        Assert.Equal(untouched, Pixel(beforeWhite, 3));
        Assert.Equal(untouched, Pixel(afterWhite, 0));
        Assert.Equal(untouched, Pixel(afterWhite, 3));
        Assert.Equal(yellow, Pixel(beforeWhite, 2));
        Assert.Equal(yellow, Pixel(afterWhite, 2));
        AssertEither(Pixel(beforeWhite, 1),
            PdfDeviceCmykTests.RenderInk(127, 127, 128),
            PdfDeviceCmykTests.RenderInk(128, 128, 127));
        AssertEither(Pixel(afterWhite, 1),
            PdfDeviceCmykTests.RenderInk(127, 0, 128),
            PdfDeviceCmykTests.RenderInk(128, 0, 127));
        Assert.Empty(beforeWhite.Diagnostics);
        Assert.Empty(afterWhite.Diagnostics);
    }

    private static PdfRenderedPage RenderDifferentSpotBackdrop(int sourceScale, bool followingWhite)
    {
        string content = "0 1 0 0 k 0 0 4 1 re f "
            + "q /O gs /CyanSpot cs 1 scn 0 0 4 1 re f Q "
            + "q /N gs 1.5 0 m 3 0 l 3 1 l 1.5 1 l h W n "
            + "4 0 0 1 0 0 cm /YellowImage Do Q "
            + (followingWhite ? "q /O gs 2 0 0 1 1 0 cm /White Do Q" : "");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary originalPage = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference yellowImage = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(4 * sourceScale)),
            Entry("Height", new PdfInteger(sourceScale)),
            Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", Name("YellowSpot"))
        ]), Enumerable.Repeat((byte)255, 4 * sourceScale * sourceScale).ToArray()));
        PdfIndirectReference whiteImage = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK"))
        ]), new byte[8]));
        static PdfDictionary Tint(int c, int m, int y) => new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(c, m, y, 0)),
            Entry("N", new PdfInteger(1))
        ]);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([
                Entry("CyanSpot", new PdfArray([
                    Name("Separation"), Name("FirstSpot"), Name("DeviceCMYK"), Tint(1, 0, 0)
                ])),
                Entry("YellowSpot", new PdfArray([
                    Name("Separation"), Name("SecondSpot"), Name("DeviceCMYK"), Tint(0, 0, 1)
                ]))
            ])),
            Entry("ExtGState", new PdfDictionary([
                Entry("N", new PdfDictionary([
                    Entry("OP", new PdfBoolean(false)), Entry("op", new PdfBoolean(false))
                ])),
                Entry("O", new PdfDictionary([
                    Entry("OP", new PdfBoolean(true)), Entry("op", new PdfBoolean(true)),
                    Entry("OPM", new PdfInteger(0))
                ]))
            ])),
            Entry("XObject", new PdfDictionary([
                Entry("YellowImage", yellowImage), Entry("White", whiteImage)
            ]))
        ]);
        PdfDictionary group = new([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))
        ]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(originalPage
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));
    }

    private static void AssertEither(byte[] actual, byte[] first, byte[] second) =>
        Assert.True(actual.SequenceEqual(first) || actual.SequenceEqual(second),
            $"Unexpected pixel {Convert.ToHexString(actual)}; expected "
            + $"{Convert.ToHexString(first)} or {Convert.ToHexString(second)}.");

    private static PdfRenderedPage Render(int sourceScale, bool clippedSpotImage,
        byte uniformTint = 255, bool alternatingTints = false,
        bool nonwhiteAlternateStart = false, bool spotBackdrop = false)
    {
        string background = clippedSpotImage || spotBackdrop
            ? "/Green cs 1 scn 0 0 4 1 re f " : "";
        string clip = clippedSpotImage
            ? "1.5 0 m 3 0 l 3 1 l 1.5 1 l h W n " : "";
        string content = background + "q /N gs " + clip
            + "4 0 0 1 0 0 cm /Spot Do Q "
            + "q /O gs 2 0 0 1 1 0 cm /White Do Q";
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary originalPage = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        int spotWidth = 4 * sourceScale;
        int spotHeight = sourceScale;
        byte[] spotSamples = alternatingTints
            ? Enumerable.Range(0, spotWidth * spotHeight)
                .Select(index => (byte)(index % 2 == 0 ? 0 : 255)).ToArray()
            : Enumerable.Repeat(uniformTint, spotWidth * spotHeight).ToArray();
        PdfIndirectReference spotImage = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(spotWidth)),
            Entry("Height", new PdfInteger(spotHeight)),
            Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", Name("Green"))
        ]), spotSamples));
        PdfIndirectReference whiteImage = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK"))
        ]), new byte[8]));
        PdfDictionary tint = new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, nonwhiteAlternateStart ? 1 : 0, 0, 0)),
            Entry("C1", new PdfArray([new PdfReal(0.5), new PdfInteger(0),
                new PdfInteger(1), new PdfInteger(0)])), Entry("N", new PdfInteger(1))
        ]);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([Entry("Green", new PdfArray([
                Name("Separation"), Name("GWGGreen"), Name("DeviceCMYK"), tint
            ]))])),
            Entry("ExtGState", new PdfDictionary([
                Entry("N", new PdfDictionary([
                    Entry("OP", new PdfBoolean(false)), Entry("op", new PdfBoolean(false))
                ])),
                Entry("O", new PdfDictionary([
                    Entry("OP", new PdfBoolean(true)), Entry("op", new PdfBoolean(true)),
                    Entry("OPM", new PdfInteger(0))
                ]))
            ])),
            Entry("XObject", new PdfDictionary([
                Entry("Spot", spotImage), Entry("White", whiteImage)
            ]))
        ]);
        PdfDictionary group = new([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))
        ]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(originalPage
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x) =>
        page.Pixels.Slice(x * 4, 4).ToArray();

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) =>
        new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
