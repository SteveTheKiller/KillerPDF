using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfDefaultColorSpaceTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (int components in new[] { 1, 3, 4 })
            foreach (string paint in new[] { "fill", "stroke", "selected", "initial", "image", "inline", "indexed", "shading", "form" })
                yield return [components, paint];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DefaultSpaceMatchesExplicitSpace(int components, string paint)
    {
        Assert.Equal(Render(components, paint, false), Render(components, paint, true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void DefaultSpaceAlsoDefinesGroupBlending(int components)
    {
        Assert.Equal(Render(components, "group", false), Render(components, "group", true));
    }

    [Fact]
    public void InitialPageGrayUsesDefaultSpace()
    {
        Assert.Equal(Render(1, "implicit", false), Render(1, "implicit", true));
    }

    [Fact]
    public void DefaultIccRangeClampsDeviceSamplesWithoutRescaling()
    {
        Assert.Equal(Render(1, "range", false), Render(1, "range", true));
    }

    [Theory]
    [InlineData("DeviceRGB")]
    [InlineData("DeviceGray")]
    [InlineData("Unknown")]
    [InlineData("Pattern")]
    [InlineData("Lab")]
    [InlineData("Indexed")]
    [InlineData("Malformed")]
    [InlineData("Cycle")]
    public void IdentityAndUnsupportedDefaultsPreserveDeviceColors(string kind)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("0.2 0.4 0.6 rg 0 0 1 1 re f")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        PdfObject replacement = kind switch
        {
            "Malformed" => new PdfArray([Name("CalRGB"), new PdfDictionary([])]),
            "Cycle" => Name("DefaultRGB"),
            "Lab" => new PdfArray([Name("Lab"), new PdfDictionary([Entry("WhitePoint", Numbers(0.95047, 1, 1.08883))])]),
            "Indexed" => new PdfArray([Name("Indexed"), Name("DeviceRGB"), new PdfInteger(0),
                new PdfString(new byte[] { 255, 0, 0 }, PdfStringForm.Hexadecimal)]),
            _ => Name(kind)
        };
        var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("DefaultRGB", replacement)]))]);
        var update = new PdfIncrementalUpdateBuilder(source).ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources))));
        var options = new PdfRenderOptions(1, 1);
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, options);
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new PdfPageRenderer(source).Render(0, options).Pixels.ToArray(), rendered.Pixels.ToArray());
    }

    private static byte[] Render(int components, string paint, bool remapped)
    {
        string device = components switch { 1 => "DeviceGray", 3 => "DeviceRGB", _ => "DeviceCMYK" };
        string defaultName = components switch { 1 => "DefaultGray", 3 => "DefaultRGB", _ => "DefaultCMYK" };
        string selected = remapped ? device : "Replacement";
        string operands = string.Join(" ", Enumerable.Repeat("0.5", components));
        string shorthand = components switch { 1 => "g", 3 => "rg", _ => "k" };
        string content = paint switch
        {
            "implicit" => remapped ? "0 0 8 8 re f" : "/Replacement cs 0 scn 0 0 8 8 re f",
            "initial" => remapped ? $"/{selected} cs 0 0 8 8 re f"
                : $"/Replacement cs {(components == 4 ? "0 0 0 1" : string.Join(" ", Enumerable.Repeat("0", components)))} scn 0 0 8 8 re f",
            "range" => $"8 0 0 8 0 0 cm BI /W 1 /H 1 /BPC 8 /CS /{selected} "
                + (remapped ? "" : "/D [0 1] ") + "ID @ EI",
            "group" => $"/Replacement cs {string.Join(" ", Enumerable.Repeat("0.2", components))} scn 0 0 8 8 re f "
                + $"/Half gs {string.Join(" ", Enumerable.Repeat("0.8", components))} scn 0 0 8 8 re f",
            "fill" or "form" => remapped ? $"{operands} {shorthand} 0 0 8 8 re f"
                : $"/Replacement cs {operands} scn 0 0 8 8 re f",
            "stroke" => remapped ? $"{operands} {shorthand.ToUpperInvariant()} 4 w 0 4 m 8 4 l S"
                : $"/Replacement CS {operands} SCN 4 w 0 4 m 8 4 l S",
            "selected" => $"/{selected} cs {operands} scn 0 0 8 8 re f",
            "image" or "indexed" => "8 0 0 8 0 0 cm /Image Do",
            "inline" => $"8 0 0 8 0 0 cm BI /W 1 /H 1 /BPC 8 /CS /{selected} ID "
                + new string((char)128, components) + " EI",
            _ => "/Gradient sh"
        };
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8,
            Encoding.Latin1.GetBytes(paint == "form" ? "/Form Do" : content)).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var parameters = new PdfDictionary([Entry("WhitePoint", Numbers(0.95047, 1, 1.08883)),
            Entry("Gamma", components == 1 ? new PdfInteger(3) : Numbers(3, 3, 3))]);
        PdfObject replacement = components < 4
            ? new PdfArray([Name(components == 1 ? "CalGray" : "CalRGB"), parameters])
            : new PdfArray([Name("DeviceN"), new PdfArray([Name("SpotA"), Name("SpotB"), Name("SpotC"), Name("SpotD")]),
                Name("DeviceRGB"), update.AddObject(new PdfStream(new PdfDictionary([
                    Entry("FunctionType", new PdfInteger(4)), Entry("Domain", Numbers(0, 1, 0, 1, 0, 1, 0, 1)),
                    Entry("Range", Numbers(0, 1, 0, 1, 0, 1))]), Encoding.ASCII.GetBytes("{ 4 1 roll pop pop pop dup dup }")))]);
        if (paint == "implicit") replacement = new PdfArray([Name("Separation"), Name("Spot"), Name("DeviceRGB"),
            new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
                Entry("C0", Numbers(1, 0, 0)), Entry("C1", Numbers(0, 0, 1)), Entry("N", new PdfInteger(1))])]);
        if (paint == "range") replacement = new PdfArray([Name("ICCBased"), update.AddObject(new PdfStream(
            new PdfDictionary([Entry("N", new PdfInteger(1)), Entry("Alternate", replacement),
                Entry("Range", Numbers(0.25, 0.75))]), Array.Empty<byte>()))]);
        var spaces = new PdfDictionary([Entry("Replacement", replacement), Entry(defaultName, replacement)]);
        PdfObject imageSpace = Name(selected);
        byte[] samples = Enumerable.Repeat((byte)128, components).ToArray();
        if (paint == "indexed")
        {
            imageSpace = new PdfArray([Name("Indexed"), imageSpace, new PdfInteger(0),
                new PdfString(samples, PdfStringForm.Hexadecimal)]);
            samples = [0];
        }
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", imageSpace)]), samples));
        var function = new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(new double[components])), Entry("C1", Numbers(Enumerable.Repeat(1d, components).ToArray())),
            Entry("N", new PdfInteger(1))]);
        var shading = new PdfDictionary([Entry("ShadingType", new PdfInteger(2)), Entry("ColorSpace", Name(selected)),
            Entry("Coords", Numbers(0, 0, 8, 0)), Entry("Function", function)]);
        var resources = new PdfDictionary([Entry("ColorSpace", spaces),
            Entry("XObject", new PdfDictionary([Entry("Image", image)])),
            Entry("ExtGState", new PdfDictionary([Entry("Half", new PdfDictionary([Entry("ca", new PdfReal(0.5))]))])),
            Entry("Shading", new PdfDictionary([Entry("Gradient", shading)]))]);
        if (paint == "form")
        {
            var form = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 8, 8)),
                Entry("Resources", resources)]), Encoding.ASCII.GetBytes(content)));
            resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Form", form)]))]);
        }
        IEnumerable<KeyValuePair<PdfName, PdfObject>> pageEntries = page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources));
        if (paint == "group") pageEntries = pageEntries.Append(Entry("Group", new PdfDictionary([
            Entry("S", Name("Transparency")), Entry("CS", Name(selected))])));
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(pageEntries));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(8, 8));
        if (paint == "range")
            Assert.Equal("A source ICC profile could not be used; its alternate color space was used.",
                Assert.Single(rendered.Diagnostics));
        else Assert.Empty(rendered.Diagnostics);
        return rendered.Pixels.ToArray();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
    private static PdfArray Numbers(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
}
