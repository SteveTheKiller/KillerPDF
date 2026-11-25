using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfSingleComponentSoftMaskTests
{
    [Fact]
    public void CalGrayMaskRetainsBaseImageAndLaterContent()
    {
        PdfObject space = new PdfArray([N("CalGray"), D(("WhitePoint", A(0.95047, 1, 1.08883)))]);
        PdfRenderedPage masked = Render(space, recovery: true);
        Assert.Equal(new byte[] { 127, 127, 255, 255 }, Pixel(masked, 5));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(masked, 15));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, paintFollowing: false), 15));
        Assert.Contains("A non-DeviceGray image soft mask was interpreted as raw opacity samples during compatibility recovery.",
            masked.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(Render(null), 5));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, paintImage: false), 5));
    }

    [Theory]
    [InlineData("DeviceRGB")]
    [InlineData("MissingSpace")]
    public void UnsupportedMaskRecoveryRetainsBaseImageWithDiagnostic(string kind)
    {
        PdfRenderedPage recovered = Render(N(kind), recovery: true, explicitMask: true);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(recovered, 5));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(recovered, 15));
        Assert.Contains("An unsupported image soft mask was ignored; the base image was retained.",
            recovered.Diagnostics);
        PdfRenderedPage strict = Render(N(kind));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(strict, 5));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(strict, 15));
        Assert.Contains("The image soft mask is not implemented.", strict.Diagnostics);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, paintFollowing: false), 15));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(Render(null), 5));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, paintImage: false), 5));
    }

    [Theory]
    [InlineData("DeviceGray", false)]
    [InlineData("CalGray", false)]
    [InlineData("ICC", false)]
    [InlineData("DeviceGray", true)]
    [InlineData("CalGray", true)]
    [InlineData("ICC", true)]
    public void SingleComponentMaskPreservesOpacityDecodeAndMaskPrecedence(string kind, bool recovery)
    {
        PdfObject space = kind switch
        {
            "CalGray" => new PdfArray([N("CalGray"), D(("WhitePoint", A(0.95047, 1, 1.08883)))]),
            _ => N(kind)
        };
        PdfRenderedPage normal = Render(space, recovery, explicitMask: true);
        PdfRenderedPage inverted = Render(space, recovery, invert: true, explicitMask: true);
        bool supported = kind == "DeviceGray" || recovery;
        Assert.Equal(supported ? new byte[] { 127, 127, 255, 255 }
            : new byte[] { 255, 255, 255, 255 }, Pixel(normal, 5));
        Assert.Equal(supported ? new byte[] { 128, 128, 255, 255 }
            : new byte[] { 255, 255, 255, 255 }, Pixel(inverted, 5));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(normal, 15));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(inverted, 15));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, paintFollowing: false), 15));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, explicitMask: true), 5));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(Render(null), 5));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, paintImage: false), 5));
        if (kind == "DeviceGray") Assert.Empty(normal.Diagnostics);
        else Assert.Contains(recovery
            ? "A non-DeviceGray image soft mask was interpreted as raw opacity samples during compatibility recovery."
            : "The image soft mask is not implemented.", normal.Diagnostics);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IndexedMaskIsUnsupportedEvenWhenIndexZeroMeansWhite(bool recovery, bool rgb)
    {
        PdfObject space = new PdfArray([N("Indexed"), N(rgb ? "DeviceRGB" : "DeviceGray"),
            new PdfInteger(1), new PdfString(rgb ? new byte[] { 255, 255, 255, 0, 0, 0 }
                : new byte[] { 255, 0 }, PdfStringForm.Hexadecimal)]);
        PdfRenderedPage rendered = Render(space, recovery, explicitMask: true, maskSample: 0);
        Assert.Equal(recovery ? new byte[] { 0, 0, 255, 255 }
            : new byte[] { 255, 255, 255, 255 }, Pixel(rendered, 5));
        Assert.Contains(recovery
            ? "An unsupported image soft mask was ignored; the base image was retained."
            : "The image soft mask is not implemented.", rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(rendered, 15));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, paintFollowing: false), 15));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(Render(null), 5));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(null, paintImage: false), 5));
    }

    [Fact]
    public void OverlongMaskSamplesAreTrimmedDuringRecovery()
    {
        PdfRenderedPage rendered = Render(N("DeviceGray"), recovery: true,
            maskData: [128, 0, 0, 0]);
        Assert.Equal(new byte[] { 127, 127, 255, 255 }, Pixel(rendered, 5));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(rendered, 15));
    }

    [Fact]
    public void ShortMaskSamplesArePaddedOpaqueDuringRecovery()
    {
        PdfRenderedPage rendered = Render(N("DeviceGray"), recovery: true,
            maskData: [0], maskHeight: 2);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(rendered, 5, 2));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(rendered, 5, 7));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(rendered, 15));
    }

    [Fact]
    public void EmptyMaskDuringRecoveryRetainsBaseImageInsteadOfFailingThePage()
    {
        PdfRenderedPage rendered = Render(N("DeviceGray"), recovery: true, maskData: []);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(rendered, 5));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(rendered, 15));
        Assert.Contains("An image mask was missing sample data; the missing area was treated as opaque.",
            rendered.Diagnostics);
    }

    [Fact]
    public void InvertedShortMaskStillPadsOpaque()
    {
        PdfRenderedPage rendered = Render(N("DeviceGray"), recovery: true, invert: true,
            maskData: []);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(rendered, 5));
    }

    [Fact]
    public void BadMaskLengthStillFailsWithoutRecovery()
    {
        PdfRenderedPage rendered = Render(N("DeviceGray"), maskData: [128, 0]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(rendered, 15));
        Assert.NotEqual(new byte[] { 127, 127, 255, 255 }, Pixel(rendered, 5));
    }

    private static PdfRenderedPage Render(PdfObject? maskSpace, bool recovery = false,
        bool paintImage = true, bool invert = false, bool explicitMask = false,
        bool paintFollowing = true, byte maskSample = 128, byte[]? maskData = null,
        int maskHeight = 1)
    {
        string content = (paintImage ? "q 10 0 0 10 0 0 cm /Image Do Q " : "")
            + (paintFollowing ? "0 1 0 rg 10 0 10 10 re f" : "");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 10,
            Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        if (maskSpace is PdfName name && name.ValueAsLatin1() == "ICC")
            maskSpace = new PdfArray([N("ICCBased"), update.AddObject(new PdfStream(
                D(("N", new PdfInteger(1)), ("Alternate", N("DeviceGray"))), new byte[] { 0 }))]);
        var imageEntries = new List<(string, PdfObject)> {
            ("Subtype", N("Image")), ("Width", new PdfInteger(1)), ("Height", new PdfInteger(1)),
            ("BitsPerComponent", new PdfInteger(8)), ("ColorSpace", N("DeviceRGB")) };
        if (maskSpace is not null)
        {
            PdfDictionary mask = D(("Subtype", N("Image")), ("Width", new PdfInteger(1)),
                ("Height", new PdfInteger(maskHeight)), ("BitsPerComponent", new PdfInteger(8)),
                ("ColorSpace", maskSpace), ("Decode", invert ? A(1, 0) : A(0, 1)));
            imageEntries.Add(("SMask", update.AddObject(new PdfStream(mask,
                maskData ?? new byte[] { maskSample }))));
        }
        if (explicitMask)
            imageEntries.Add(("Mask", new PdfArray([new PdfInteger(255), new PdfInteger(255),
                new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)])));
        PdfIndirectReference image = update.AddObject(new PdfStream(D(imageEntries.ToArray()),
            new byte[] { 255, 0, 0 }));
        var changed = new PdfDictionary(page.Dictionary.Where(entry => !entry.Key.Equals(N("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(N("Resources"), D(("XObject", D(("Image", image)))))));
        byte[] bytes = update.ReplaceObject(page.Reference.ObjectNumber, changed).Build();
        return new PdfPageRenderer(recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes)
            : PdfDocument.Open(bytes)).Render(0, new PdfRenderOptions(20, 10));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y = 5) =>
        page.Pixels.Slice((y * 20 + x) * 4, 4).ToArray();
    private static PdfArray A(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary D(params (string Name, PdfObject Value)[] values) =>
        new(values.Select(value => new KeyValuePair<PdfName, PdfObject>(N(value.Name), value.Value)));
}
