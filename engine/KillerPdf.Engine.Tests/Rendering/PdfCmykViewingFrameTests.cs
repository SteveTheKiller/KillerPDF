using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCmykViewingFrameTests
{
    [Fact]
    public void UntaggedCyanFillUsesTheBundledViewingAssumptionWithoutClipping()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("1 0 0 0 k 0 0 1 1 re f")).Build();
        var renderer = new PdfPageRenderer(PdfDocument.Open(source));

        PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1, 1));

        // Frozen Decimal60 oracle from the approved profile's exact cyan CLUT corner.
        // Legacy Lab is [40699,21461,19925]; matrices were independently inverted.
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.InRange(frame.Pixels[0], -0.20839846805136676f - 0.000002f, -0.20839846805136676f + 0.000002f);
        Assert.InRange(frame.Pixels[1], 0.42131251226180745f - 0.000002f, 0.42131251226180745f + 0.000002f);
        Assert.InRange(frame.Pixels[2], 0.86494485126038379f - 0.000002f, 0.86494485126038379f + 0.000002f);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void ResourceDefaultCmykRemainsAuthoritativeForVectorFills()
    {
        // Same synthetic affine profile and independent Decimal60 sample as the
        // existing CMYK precision image fixture, now through the vector paint path.
        byte[] profile = Convert.FromBase64String(
            "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=");
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("0.50000762951094835 0.50000762951094835 0.50000762951094835 0.50000762951094835 k 0 0 1 1 re f")).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var icc = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(4))]), profile));
        var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([
            Entry("DefaultCMYK", new PdfArray([Name("ICCBased"), icc]))]))]);
        var document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(1, 1));

        Assert.InRange(frame.Pixels[0], 0.25656640931229085f - 0.000002f, 0.25656640931229085f + 0.000002f);
        Assert.InRange(frame.Pixels[1], 0.24270025515688283f - 0.000002f, 0.24270025515688283f + 0.000002f);
        Assert.InRange(frame.Pixels[2], 0.31217781759250244f - 0.000002f, 0.31217781759250244f + 0.000002f);
        Assert.Equal(1, frame.Pixels[3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
