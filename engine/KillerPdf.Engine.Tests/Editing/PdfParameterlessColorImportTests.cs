using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfParameterlessColorImportTests
{
    [Theory]
    [InlineData("DeviceGray")]
    [InlineData("DeviceRGB")]
    [InlineData("DeviceCMYK")]
    public void DeviceFamilyArraysRejectExtraParameters(string family) =>
        Assert.Throws<InvalidOperationException>(() => Import(Image(family, extraParameter: true)));

    [Fact]
    public void UnknownFamilyArraysRemainRejected() =>
        Assert.Throws<InvalidOperationException>(() => Import(Image("UnknownSpace")));

    [Theory]
    [InlineData(null)]
    [InlineData("DeviceGray")]
    [InlineData("DeviceRGB")]
    [InlineData("DeviceCMYK")]
    public void SupportedPatternArraysImportAndPreservePixels(string? baseFamily)
    {
        bool colored = baseFamily is null;
        var pattern = new PdfTilingPattern(4, 4, colored
            ? new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 0, 2, 2).Fill()
            : new PdfContentStreamBuilder().Rectangle(0, 0, 2, 2).Fill(),
            paintType: colored ? PdfTilingPatternPaintType.Colored : PdfTilingPatternPaintType.Uncolored);
        var content = new PdfContentStreamBuilder();
        if (colored) content.SetFillPattern(pattern);
        else content.SetFillPattern(pattern, new PdfRgbColor(1, 0, 0));
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 20,
            content.Rectangle(0, 0, 20, 20).Fill()).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var resources = (PdfDictionary)page.Dictionary[N("Resources")];
        PdfObject space = colored ? new PdfArray([N("Pattern")])
            : new PdfArray([N("Pattern"), new PdfArray([N(baseFamily!)])]);
        resources = new PdfDictionary(resources.Append(new KeyValuePair<PdfName, PdfObject>(
            N("ColorSpace"), new PdfDictionary([new(N("Cs"), space)]))));
        string color = baseFamily switch
        {
            "DeviceGray" => "0 ", "DeviceRGB" => "1 0 0 ", "DeviceCMYK" => "0 1 1 0 ", _ => ""
        };
        var update = new PdfIncrementalUpdateBuilder(source);
        var stream = update.AddObject(new PdfStream(new PdfDictionary([]), Encoding.ASCII.GetBytes(
            $"/Cs cs {color}/P1 scn 0 0 20 20 re f")));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(page.Dictionary
            .Where(entry => entry.Key.ToString() is not "/Resources" and not "/Contents")
            .Append(new(N("Resources"), resources)).Append(new(N("Contents"), stream))));
        source = PdfDocument.Open(update.Build());
        var options = new PdfRenderOptions(20, 20);
        var expected = new PdfPageRenderer(source).Render(0, options);
        Assert.Empty(expected.Diagnostics);
        Assert.Contains(expected.Pixels.ToArray(), value => value < 250);
        var actual = new PdfPageRenderer(Import(source)).Render(0, options);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData("DeviceGray")]
    [InlineData("DeviceRGB")]
    [InlineData("DeviceCMYK")]
    public void DeviceFamilyArraysStillEnforceDecodeComponentCounts(string family)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Import(Image(family, wrongDecode: true)));
        Assert.Contains("/Decode count does not match its color components", error.Message);
    }

    [Theory]
    [InlineData("DeviceGray")]
    [InlineData("DeviceRGB")]
    [InlineData("DeviceCMYK")]
    public void ImageDeviceFamilyArraysImportAndPreservePixels(string family)
    {
        PdfDocument source = Image(family);
        var options = new PdfRenderOptions(20, 20);
        var expected = new PdfPageRenderer(source).Render(0, options);
        Assert.Empty(expected.Diagnostics);
        Assert.Contains(expected.Pixels.ToArray(), value => value < 250);
        PdfDocument imported = Import(source);
        var actual = new PdfPageRenderer(imported).Render(0, options);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        PdfDocument roundTrip = PdfDocument.Open(PdfDocumentWriter.Write(imported));
        Assert.Equal(expected.Pixels.ToArray(), new PdfPageRenderer(roundTrip).Render(0, options).Pixels.ToArray());
    }

    private static PdfDocument Import(PdfDocument source) => PdfDocument.Open(
        new PdfIncrementalPageEditor(PdfDocument.Open(new PdfDocumentBuilder().Build()))
            .AddImportedPage(source, 0).Build());

    private static PdfDocument Image(string family, bool extraParameter = false, bool wrongDecode = false)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(20, 20).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject[] colorSpace = extraParameter ? [N(family), new PdfInteger(0)] : [N(family)];
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(N("Type"), N("XObject")), new(N("Subtype"), N("Image")),
            new(N("Width"), new PdfInteger(1)), new(N("Height"), new PdfInteger(1)),
            new(N("BitsPerComponent"), new PdfInteger(8)), new(N("ColorSpace"), new PdfArray(colorSpace))
        };
        if (wrongDecode) entries.Add(new(N("Decode"), new PdfArray([
            new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1)])));
        byte[] samples = family switch
        {
            "DeviceGray" => [0], "DeviceRGB" => [255, 0, 0], "DeviceCMYK" => [0, 255, 255, 0], _ => [0]
        };
        var image = update.AddObject(new PdfStream(new PdfDictionary(entries), samples));
        var resources = new PdfDictionary([new(N("XObject"), new PdfDictionary([new(N("Im"), image)]))]);
        var content = update.AddObject(new PdfStream(new PdfDictionary([]), "20 0 0 20 0 0 cm /Im Do"u8.ToArray()));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(page.Dictionary
            .Where(entry => entry.Key.ToString() is not "/Resources" and not "/Contents")
            .Append(new(N("Resources"), resources)).Append(new(N("Contents"), content))));
        return PdfDocument.Open(update.Build());
    }

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
}
