using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccFallbackTests
{
    [Theory]
    [InlineData("fill")]
    [InlineData("stroke")]
    [InlineData("image")]
    [InlineData("inline")]
    [InlineData("shading")]
    [InlineData("default")]
    [InlineData("indexed")]
    [InlineData("form")]
    public void SourceFallbackReportsOnceAndPreservesAlternatePixels(string paint)
    {
        var renderer = new PdfPageRenderer(Create(paint, true));
        var options = new PdfRenderOptions(8, 8);
        byte[] expected = new PdfPageRenderer(Create(paint, false)).Render(0, options).Pixels.ToArray();
        for (int repeat = 0; repeat < 2; repeat++)
        {
            var pixels = new byte[expected.Length];
            var diagnostics = renderer.RenderInto(0, options, pixels);
            Assert.Equal(expected, pixels);
            Assert.Equal("A source ICC profile could not be used; its alternate color space was used.",
                Assert.Single(diagnostics));
        }
    }

    [Fact]
    public void UnusedInvalidProfileDoesNotReportFallback()
    {
        var rendered = new PdfPageRenderer(Create("unused", true)).Render(0, new PdfRenderOptions(8, 8));
        Assert.Empty(rendered.Diagnostics);
    }

    private static PdfDocument Create(string paint, bool invalid)
    {
        string fill = "/Space cs 0.5 scn 0 0 8 8 re f ";
        string content = paint switch
        {
            "stroke" => "/Space CS 0.5 SCN 8 w 0 4 m 8 4 l S",
            "image" => "8 0 0 8 0 0 cm /Image Do",
            "inline" => "8 0 0 8 0 0 cm BI /W 1 /H 1 /BPC 8 /CS /Space ID " + (char)128 + " EI",
            "shading" => "/Shade sh",
            "default" => "0.5 g 0 0 8 8 re f",
            "indexed" => "/Indexed cs 0 scn 0 0 8 8 re f",
            "form" => "/Form Do",
            "unused" => "0.5 g 0 0 8 8 re f",
            _ => fill + fill
        };
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8, Encoding.Latin1.GetBytes(content)).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("N", new PdfInteger(1)), Entry("Alternate", Name("DeviceGray"))]), [0]));
        PdfObject space = invalid ? new PdfArray([Name("ICCBased"), profile]) : Name("DeviceGray");
        var spaces = new PdfDictionary([
            Entry("Space", space),
            Entry("Indexed", new PdfArray([Name("Indexed"), space, new PdfInteger(0),
                new PdfString(new byte[] { 128 }, PdfStringForm.Hexadecimal)]))]);
        if (paint == "default") spaces = new PdfDictionary(spaces.Append(Entry("DefaultGray", space)));
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", space)]), [128]));
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 8, 8)),
            Entry("Resources", new PdfDictionary([Entry("ColorSpace", spaces)]))]), Encoding.ASCII.GetBytes(fill)));
        var function = new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0.5)), Entry("C1", Numbers(0.5)), Entry("N", new PdfInteger(1))]);
        var shading = new PdfDictionary([Entry("ShadingType", new PdfInteger(2)), Entry("ColorSpace", space),
            Entry("Coords", Numbers(0, 0, 8, 0)), Entry("Function", function)]);
        var resources = new PdfDictionary([Entry("ColorSpace", spaces),
            Entry("XObject", new PdfDictionary([Entry("Image", image), Entry("Form", form)])),
            Entry("Shading", new PdfDictionary([Entry("Shade", shading)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        return PdfDocument.Open(update.Build());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
    private static PdfArray Numbers(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
}
