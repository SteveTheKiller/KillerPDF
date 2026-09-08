using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfInitialColorTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string space in new[] { "DeviceGray", "DeviceRGB", "DeviceCMYK", "CalGray",
            "CalRGB", "Lab", "Indexed", "Separation", "DeviceN", "All", "None", "ICCBased", "Pattern" })
            foreach (bool stroke in new[] { false, true }) yield return [space, stroke];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SelectingColorSpaceResetsPaint(string space, bool stroke)
    {
        Assert.Equal(Render(space, stroke, true), Render(space, stroke, false));
    }

    private static byte[] Render(string kind, bool stroke, bool explicitColor)
    {
        string initial = kind switch
        {
            "DeviceCMYK" => "0 0 0 1",
            "DeviceRGB" or "CalRGB" => "0 0 0",
            "Lab" => "0 10 -20",
            "ICCBased" => "0.25 0 0 0",
            "Separation" or "DeviceN" or "All" or "None" => "1",
            _ => "0"
        };
        string select = stroke ? "/Space CS" : "/Space cs";
        string color = explicitColor && kind != "Pattern" ? initial + (stroke ? " SCN" : " scn") : "";
        string paint = explicitColor && kind == "Pattern" ? "" : stroke ? "2 w 0 4 m 8 4 l S" : "0 0 8 8 re f";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8,
            Encoding.ASCII.GetBytes($"1 0 0 rg 1 0 0 RG {select} {color} {paint}")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var parameters = new PdfDictionary([Entry("WhitePoint", Numbers(0.9642, 1, 0.8249))]);
        var function = new PdfDictionary([Entry("FunctionType", new PdfInteger(2)),
            Entry("Domain", Numbers(0, 1)), Entry("C0", Numbers(1, 1, 1)),
            Entry("C1", Numbers(0, 0, 1)), Entry("N", new PdfInteger(1))]);
        PdfObject space = kind switch
        {
            "CalGray" or "CalRGB" => new PdfArray([Name(kind), parameters]),
            "Lab" => new PdfArray([Name(kind), new PdfDictionary(parameters.Append(Entry("Range", Numbers(10, 20, -30, -20))))]),
            "Indexed" => new PdfArray([Name(kind), Name("DeviceRGB"), new PdfInteger(1), new PdfString(new byte[] { 0, 255, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)]),
            "Separation" or "All" or "None" => new PdfArray([Name("Separation"), Name(kind == "Separation" ? "Spot" : kind), Name("DeviceRGB"), function]),
            "DeviceN" => new PdfArray([Name(kind), new PdfArray([Name("Spot")]), Name("DeviceRGB"), function]),
            "ICCBased" => new PdfArray([Name(kind), update.AddObject(new PdfStream(new PdfDictionary([
                Entry("N", new PdfInteger(4)), Entry("Alternate", Name("DeviceCMYK")),
                Entry("Range", Numbers(0.25, 1, 0, 1, 0, 1, 0, 1))]), Array.Empty<byte>()))]),
            _ => Name(kind)
        };
        var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("Space", space)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(8, 8));
        if (kind == "ICCBased")
            Assert.Equal("A source ICC profile could not be used; its alternate color space was used.",
                Assert.Single(result.Diagnostics));
        else Assert.Empty(result.Diagnostics);
        return result.Pixels.ToArray();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
    private static PdfArray Numbers(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
}
