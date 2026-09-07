using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererFontRecoveryTests
{
    [Theory]
    [InlineData("Helvetica", false)]
    [InlineData("Helvetica", true)]
    [InlineData("Helvetica-BoldOblique", false)]
    [InlineData("Times-Roman", true)]
    [InlineData("Courier", true)]
    [InlineData("Symbol", true)]
    [InlineData("ZapfDingbats", true)]
    public void MissingStandardFontMatchesDeclaredFontOnlyInRecovery(string name, bool widget)
    {
        var options = new PdfRenderOptions(240, 160);
        PdfRenderedPage expected = new PdfPageRenderer(Create(name, name, widget, false))
            .Render(0, options);
        Assert.Contains(expected.Pixels.ToArray(), value => value != 255);
        var renderer = new PdfPageRenderer(Create(name, null, widget, true));
        PdfRenderedPage actual = renderer.Render(0, options);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Contains($"Missing standard font resource /{name} was recovered.", actual.Diagnostics);
        Assert.Equal(actual.Pixels.ToArray(), renderer.Render(0, options).Pixels.ToArray());

        PdfRenderedPage strict = new PdfPageRenderer(Create(name, null, widget, false))
            .Render(0, options);
        Assert.All(strict.Pixels.ToArray(), value => Assert.Equal(255, value));
        Assert.Contains("Text rendering is not implemented.", strict.Diagnostics);
        if (widget)
            Assert.All(renderer.Render(0, new PdfRenderOptions(240, 160,
                includeFormFields: false)).Pixels.ToArray(), value => Assert.Equal(255, value));
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("HelveticaBold")]
    public void RecoveryDoesNotInventAnUnknownFont(string name)
    {
        PdfRenderedPage page = new PdfPageRenderer(Create(name, null, true, true))
            .Render(0, new PdfRenderOptions(240, 160));
        Assert.All(page.Pixels.ToArray(), value => Assert.Equal(255, value));
        Assert.Contains("Text rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void RecoveryPreservesDeclaredFontWithAStandardResourceName()
    {
        var options = new PdfRenderOptions(240, 160);
        PdfRenderedPage expected = new PdfPageRenderer(Create("Helvetica", "Courier", true, false))
            .Render(0, options);
        PdfRenderedPage actual = new PdfPageRenderer(Create("Helvetica", "Courier", true, true))
            .Render(0, options);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Empty(actual.Diagnostics);
    }

    private static PdfDocument Create(string resourceName, string? declaredFont, bool widget, bool recovery)
    {
        byte[] content = Encoding.ASCII.GetBytes(
            $"BT /{resourceName} 18 Tf 10 60 Td (Several) Tj 0 -24 Td (Other Jobs) Tj ET");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(120, 80, widget ? [] : content).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var resources = declaredFont is null ? D() : D(("Font", D((resourceName,
            D(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N(declaredFont)))))));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary changed;
        if (widget)
        {
            var box = new PdfArray([new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(120), new PdfInteger(80)]);
            PdfIndirectReference appearance = update.AddObject(new PdfStream(
                D(("Subtype", N("Form")), ("BBox", box), ("Resources", resources)), content));
            PdfIndirectReference annotation = update.AddObject(D(("Subtype", N("Widget")),
                ("FT", N("Tx")), ("Rect", box), ("AP", D(("N", appearance)))));
            changed = new PdfDictionary(page.Dictionary.Append(
                new KeyValuePair<PdfName, PdfObject>(N("Annots"), new PdfArray([annotation]))));
        }
        else
            changed = new PdfDictionary(page.Dictionary.Where(entry => !entry.Key.Equals(N("Resources")))
                .Append(new KeyValuePair<PdfName, PdfObject>(N("Resources"), resources)));
        byte[] bytes = update.ReplaceObject(page.Reference.ObjectNumber, changed).Build();
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
    }

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary D(params (string Name, PdfObject Value)[] values) =>
        new(values.Select(value => new KeyValuePair<PdfName, PdfObject>(N(value.Name), value.Value)));
}
