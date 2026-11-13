using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Paints that keep only the ink must not pay for converting it to display RGB through the
// working CMYK profile, which is an exact profile evaluation per call.
public sealed class PdfInkPaintDisplayConversionTests
{
    // A plate render keeps the page as ink and reads it back as a plate, so nothing on the page
    // is displayed as RGB. Half opacity sends every image pixel through the ink compositor.
    [Fact]
    public void ADeviceCmykImageOnAnInkSurfaceMakesNoDisplayConversions()
    {
        var samples = new byte[4 * 4 * 4];
        for (int index = 0; index < samples.Length; index++) samples[index] = (byte)(index * 37 % 256);
        byte[] source = new PdfDocumentBuilder()
            .AddPage(16, 16, new PdfContentStreamBuilder().SetOpacity(0.5)
                .DrawImage(PdfImage.FromCmyk(4, 4, samples), 0, 0, 16, 16))
            .Build();
        var renderer = new PdfPageRenderer(PdfDocument.Open(source));
        var options = new PdfRenderOptions(16, 16, includeAnnotations: false, includeFormFields: false)
        {
            SeparationPlate = "Cyan", MaximumParallelism = 1
        };

        PdfCmykDisplayTable.ConversionCountOnThisThread = 0;
        PdfRenderedPage plate = renderer.Render(0, options);

        Assert.Equal(0, PdfCmykDisplayTable.ConversionCountOnThisThread);
        Assert.NotEqual(255, plate.Pixels.Span[(8 * 16 + 8) * 4 + 2]);
    }

    // An inverted Decode sends each DeviceCMYK sample through the decoded conversion, which on
    // an ink surface keeps the ink alone.
    [Fact]
    public void ADecodedDeviceCmykImageOnAnInkSurfaceMakesNoDisplayConversions()
    {
        var hex = new StringBuilder();
        for (int index = 0; index < 4 * 4 * 4; index++) hex.Append((index * 37 % 256).ToString("X2"));
        byte[] content = Encoding.ASCII.GetBytes("q 16 0 0 16 0 0 cm BI /W 4 /H 4 /CS /DeviceCMYK /BPC 8 "
            + "/D [1 0 1 0 1 0 1 0] /F /AHx ID " + hex + "> EI Q");
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(16, 16, content).Build()));
        var options = new PdfRenderOptions(16, 16, includeAnnotations: false, includeFormFields: false)
        {
            SeparationPlate = "Cyan", MaximumParallelism = 1
        };

        PdfCmykDisplayTable.ConversionCountOnThisThread = 0;
        PdfRenderedPage plate = renderer.Render(0, options);

        Assert.Equal(0, PdfCmykDisplayTable.ConversionCountOnThisThread);
        Assert.NotEqual(255, plate.Pixels.Span[(8 * 16 + 8) * 4 + 2]);
    }

    // An ICC-based CMYK fill is displayed from its profile; the ink it carries is only packed.
    [Fact]
    public void AnIccBasedCmykFillPacksItsInkWithoutADisplayConversion()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 4, Encoding.ASCII.GetBytes("/Profiled cs 0.5 0.2 0.1 0 scn 0 0 4 4 re f")).Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        using Stream bundled = typeof(PdfPageRenderer).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.ColorProfiles.CGATS001Compat-v2-micro.icc")!;
        using var buffer = new MemoryStream();
        bundled.CopyTo(buffer);
        PdfIndirectReference profile = update.AddObject(new PdfStream(
            new PdfDictionary([Entry("N", new PdfInteger(4))]), buffer.ToArray()));
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", new PdfDictionary([
                Entry("ColorSpace", new PdfDictionary([
                    Entry("Profiled", new PdfArray([Name("ICCBased"), profile]))]))])))));
        var renderer = new PdfPageRenderer(PdfDocument.Open(update.Build()));
        var options = new PdfRenderOptions(4, 4, includeAnnotations: false, includeFormFields: false)
        {
            MaximumParallelism = 1
        };

        PdfCmykDisplayTable.ConversionCountOnThisThread = 0;
        PdfRenderedPage rendered = renderer.Render(0, options);

        Assert.Equal(0, PdfCmykDisplayTable.ConversionCountOnThisThread);
        Assert.NotEqual(255, rendered.Pixels.Span[2]);
    }

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
