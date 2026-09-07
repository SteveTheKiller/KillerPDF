using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererFormAppearanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RequestedTextReplacesStaleSectionAndRetainsBackground(bool recovery, bool choice)
    {
        var options = new PdfRenderOptions(120, 40);
        PdfDocument blank = Create(true, "", recovery, choice);
        byte[] before = PdfDocumentWriter.Write(blank);
        var renderer = new PdfPageRenderer(blank);
        PdfRenderedPage result = renderer.Render(0, options);
        PdfRenderedPage stale = new PdfPageRenderer(Create(true,
            "BT /F1 10 Tf 3 3 Td (OLD) Tj ET", recovery, choice)).Render(0, options);
        Assert.Equal(result.Pixels.ToArray(), stale.Pixels.ToArray());
        Assert.Contains("A requested form-field text appearance was regenerated.", result.Diagnostics);
        Assert.Contains(result.Pixels.ToArray(), pixel => pixel == 0);
        Assert.NotEqual(new PdfPageRenderer(Create(false, "", recovery, choice))
            .Render(0, options).Pixels.ToArray(), result.Pixels.ToArray());
        Assert.Equal(before, PdfDocumentWriter.Write(blank));
        Assert.Equal(result.Pixels.ToArray(), renderer.Render(0, options).Pixels.ToArray());
        Assert.All(renderer.Render(0, new PdfRenderOptions(120, 40, includeFormFields: false))
            .Pixels.ToArray(), pixel => Assert.Equal(255, pixel));
        // The yellow saved background remains outside the regenerated text interior.
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, result.Pixels.Span[..4].ToArray());
    }

    [Fact]
    public void ChoiceUsesDisplayLabelAndInheritedDefaultAppearance()
    {
        var options = new PdfRenderOptions(120, 40);
        var text = new PdfPageRenderer(Create(true, "", false, false)).Render(0, options);
        var choice = new PdfPageRenderer(Create(true, "", false, true)).Render(0, options);
        Assert.Equal(text.Pixels.ToArray(), choice.Pixels.ToArray());
    }

    [Fact]
    public void UnsupportedLayoutRetainsSavedAppearanceWithDiagnostic()
    {
        var options = new PdfRenderOptions(120, 40);
        var unsupported = new PdfPageRenderer(Create(true, "", false, false, 4096)).Render(0, options);
        var saved = new PdfPageRenderer(Create(false, "", false, false, 4096)).Render(0, options);
        Assert.Equal(saved.Pixels.ToArray(), unsupported.Pixels.ToArray());
        Assert.Contains(unsupported.Diagnostics, text => text.Contains("multiline"));
        Assert.Empty(saved.Diagnostics);
    }

    [Fact]
    public void InvalidRegenerationDataRetainsAppearanceOnlyInRecovery()
    {
        var options = new PdfRenderOptions(120, 40);
        var recovered = new PdfPageRenderer(Create(true, "", true, false,
            defaultAppearance: "/F1 (invalid) Tf")).Render(0, options);
        var saved = new PdfPageRenderer(Create(false, "", false, false)).Render(0, options);
        Assert.Equal(saved.Pixels.ToArray(), recovered.Pixels.ToArray());
        Assert.Contains("Invalid form-field regeneration data retained the saved appearance.", recovered.Diagnostics);
        Assert.Throws<FormatException>(() => new PdfPageRenderer(Create(true, "", false, false,
            defaultAppearance: "/F1 (invalid) Tf")).Render(0, options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegenerationPreservesExistingOrInheritedArtworkFont(bool inheritedResources)
    {
        var options = new PdfRenderOptions(120, 40);
        var actual = new PdfPageRenderer(Create(true, "", false, false,
            artwork: true, inheritedResources: inheritedResources)).Render(0, options);
        var saved = new PdfPageRenderer(Create(false, "", false, false,
            artwork: true, inheritedResources: inheritedResources)).Render(0, options);
        Assert.Equal(saved.Pixels.Span[..(120 * 10 * 4)].ToArray(),
            actual.Pixels.Span[..(120 * 10 * 4)].ToArray());
        Assert.NotEqual(saved.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.DoesNotContain(actual.Diagnostics, text => text.Contains("not implemented"));
    }

    private static PdfDocument Create(bool requested, string previousText, bool recovery,
        bool choice, int flags = 0, string defaultAppearance = "0 g /F1 11 Tf",
        bool artwork = false, bool inheritedResources = false)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(120, 40).Build());
        PdfPageTree tree = PdfPageTree.Read(source);
        var update = new PdfIncrementalUpdateBuilder(source);
        var box = new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(120), new PdfInteger(40)]);
        var font = D(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("Encoding", N("WinAnsiEncoding")));
        var resources = D(("Font", D(("F1", font))));
        var artworkResources = artwork ? D(("Font", D(("F1", D(("Type", N("Font")),
            ("Subtype", N("Type1")), ("BaseFont", N("Courier"))))))) : resources;
        PdfDictionary appearanceDictionary = inheritedResources
            ? D(("Subtype", N("Form")), ("BBox", box))
            : D(("Subtype", N("Form")), ("BBox", box), ("Resources", artworkResources));
        string outsideText = artwork ? "0 g BT /F1 4 Tf 90 34 Td (Art) Tj ET" : "";
        var appearance = update.AddObject(new PdfStream(appearanceDictionary, Encoding.ASCII.GetBytes(
            $"1 1 0 rg 0 0 120 40 re f {outsideText} /Tx BMC {previousText} EMC")));
        var parent = update.AddObject(D(("FT", N(choice ? "Ch" : "Tx")),
            ("V", S(choice ? "export" : "Man")), ("DA", S(defaultAppearance)),
            ("Ff", new PdfInteger(choice ? 131072 : flags)),
            ("Opt", new PdfArray([new PdfArray([S("export"), S("Man")])]))));
        var widget = update.AddObject(D(("Subtype", N("Widget")), ("Parent", parent),
            ("Rect", box), ("AP", D(("N", appearance)))));
        var page = tree.Pages[0];
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(page.Dictionary
            .Where(entry => !entry.Key.Equals(N("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(N("Resources"), artworkResources))
            .Append(new KeyValuePair<PdfName, PdfObject>(N("Annots"), new PdfArray([widget])))));
        var root = (PdfIndirectReference)source.Trailer[N("Root")];
        update.ReplaceObject(root.ObjectNumber, new PdfDictionary(tree.Catalog.Append(
            new KeyValuePair<PdfName, PdfObject>(N("AcroForm"), D(("Fields", new PdfArray([parent])),
                ("NeedAppearances", new PdfBoolean(requested)), ("DR", resources))))));
        byte[] bytes = update.Build();
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
    }

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfString S(string value) => new(Encoding.ASCII.GetBytes(value), PdfStringForm.Literal);
    private static PdfDictionary D(params (string Name, PdfObject Value)[] values) =>
        new(values.Select(value => new KeyValuePair<PdfName, PdfObject>(N(value.Name), value.Value)));
}
