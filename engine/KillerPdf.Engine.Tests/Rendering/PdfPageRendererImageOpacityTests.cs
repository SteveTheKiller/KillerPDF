using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererImageOpacityTests
{
    [Theory]
    [InlineData("None", false)]
    [InlineData("None", true)]
    [InlineData("Soft", false)]
    [InlineData("Soft", true)]
    [InlineData("Explicit", false)]
    [InlineData("Explicit", true)]
    [InlineData("ColorKey", false)]
    [InlineData("ColorKey", true)]
    [InlineData("SoftAndColorKey", false)]
    [InlineData("SoftAndColorKey", true)]
    [InlineData("SoftAndExplicit", false)]
    [InlineData("SoftAndExplicit", true)]
    [InlineData("SoftAndInvalidMask", false)]
    [InlineData("SoftAndInvalidMask", true)]
    public void Render_ImageMaskReplacesGraphicsMaskWithoutChangingPageState(string maskKind,
        bool transparent)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1,
            Encoding.ASCII.GetBytes("/Outer gs q 2 0 0 1 0 0 cm /Image Do Q 1 0 0 rg 2 0 1 1 re f")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var group = new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 3, 1)),
            Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceGray"))])),
            Entry("Resources", new PdfDictionary([]))]), Encoding.ASCII.GetBytes("0.25 g 0 0 3 1 re f"));
        var graphicsMask = new PdfDictionary([Entry("S", Name("Luminosity")), Entry("G", update.AddObject(group))]);
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceRGB")) };
        if (maskKind.StartsWith("Soft", StringComparison.Ordinal))
            imageEntries.Add(Entry("SMask", update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
                Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
                Entry("ColorSpace", Name("DeviceGray"))]), new byte[] { 128, 0 }))));
        if (maskKind is "Explicit" or "SoftAndExplicit")
            imageEntries.Add(Entry("Mask", update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)),
                Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(1)),
                Entry("ImageMask", new PdfBoolean(true))]),
                new byte[] { maskKind == "Explicit" ? (byte)0x40 : (byte)0xC0 }))));
        if (maskKind == "SoftAndInvalidMask") imageEntries.Add(Entry("Mask", Numbers(-1)));
        if (maskKind is "ColorKey" or "SoftAndColorKey")
            imageEntries.Add(Entry("Mask", maskKind == "ColorKey"
                ? Numbers(0, 0, 0, 0, 255, 255) : Numbers(255, 255, 0, 0, 0, 0)));
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries),
            new byte[] { 255, 0, 0, 0, 0, 255 }));
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Image", image)])),
            Entry("ExtGState", new PdfDictionary([Entry("Outer", new PdfDictionary([
                Entry("SMask", graphicsMask), Entry("ca", new PdfReal(0.5))]))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(3, 1, transparentBackground: transparent));
        Assert.Empty(rendered.Diagnostics);
        byte alpha = maskKind == "None" ? (byte)32
            : maskKind.StartsWith("Soft", StringComparison.Ordinal) ? (byte)64 : (byte)128;
        byte faded = maskKind is "Explicit" or "ColorKey" ? (byte)128 : (byte)(255 - alpha);
        Assert.Equal(transparent ? new byte[] { 0, 0, 255, alpha }
            : new byte[] { faded, faded, 255, 255 }, rendered.Pixels.Slice(0, 4).ToArray());
        Assert.Equal(transparent ? new byte[] { 0, 0, 255, 32 }
            : new byte[] { 223, 223, 255, 255 }, rendered.Pixels.Slice(8, 4).ToArray());
        if (maskKind != "None") Assert.Equal(new byte[] { 255, 255, 255, transparent ? (byte)0 : (byte)255 },
            rendered.Pixels.Slice(4, 4).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_ImageOpacityMultipliesImageSoftMask(bool transparent)
    {
        var image = PdfImage.FromRgba(1, 1, new byte[] { 255, 0, 0, 128 });
        var content = new PdfContentStreamBuilder().SetOpacity(0.5, 1)
            .DrawImage(image, 0, 0, 1, 1);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, content).Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(1, 1, transparentBackground: transparent));

        Assert.Equal(transparent ? new byte[] { 0, 0, 255, 64 }
            : new byte[] { 191, 191, 255, 255 }, page.Pixels.ToArray());
        Assert.Empty(page.Diagnostics);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 0.5)]
    [InlineData(1, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 0.5)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    [InlineData(4, 0.5)]
    [InlineData(4, 1)]
    public void Render_ImageUsesNonstrokingOpacity(int components, double opacity)
    {
        PdfImage image = components switch
        {
            1 => PdfImage.FromGray(1, 1, new byte[] { 0 }),
            3 => PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }),
            _ => PdfImage.FromCmyk(1, 1, new byte[] { 0, 0, 0, 255 })
        };
        var content = new PdfContentStreamBuilder().SetOpacity(opacity, 1)
            .DrawImage(image, 0, 0, 1, 1)
            .SetFillGray(0).Rectangle(1, 0, 1, 1).Fill();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 1));

        Assert.Equal(page.Pixels.Slice(4, 4).ToArray(), page.Pixels.Slice(0, 4).ToArray());
        Assert.Empty(page.Diagnostics);
    }
}
