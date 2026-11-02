using System.IO;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PlainFormAppearancePublicationTests
{
    [Fact]
    public void GeneratedPlainFieldMatchesExplicitAppearanceAndPublishes()
    {
        string generatedPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        string expectedPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(generatedPath, Create(false));
            File.WriteAllBytes(expectedPath, Create(true));
            using var generated = PdfPageRenderSession.OpenEngineFirst(generatedPath, 200, 100, null);
            using var expected = PdfPageRenderSession.OpenEngineFirst(expectedPath, 200, 100, null);
            byte[] pixels = expected.RenderPage(0).Pixels;
            Assert.Contains(pixels, component => component < 128);
            PdfRenderedPage preview = generated.RenderFittedPage(0, 200, 100, includeFormFields: true);
            Assert.Equal(pixels, preview.Pixels);
            Assert.Contains(preview.RenderDiagnostics, item => item.Kind == PdfRenderDiagnosticKind.Recovery
                && item.Message == "A supported plain-text form-field appearance was regenerated.");
            Assert.Equal(pixels, generated.RenderPageForEncoding(0).Pixels.ToArray());
            Assert.Equal(pixels, generated.RenderPage(0).Pixels);
        }
        finally { File.Delete(generatedPath); File.Delete(expectedPath); }
    }

    [Theory]
    [InlineData("rich-flag")]
    [InlineData("rich-value")]
    [InlineData("multiline")]
    [InlineData("readonly")]
    [InlineData("password")]
    [InlineData("comb")]
    [InlineData("missing-font")]
    [InlineData("missing-glyph")]
    [InlineData("auto-size")]
    [InlineData("styled")]
    [InlineData("color")]
    [InlineData("long-value")]
    [InlineData("aligned")]
    [InlineData("empty")]
    [InlineData("saved")]
    [InlineData("short")]
    public void UnvalidatedFormRegenerationRemainsBlocked(string variant)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, Create(variant == "saved", variant));
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 200, 100, null);
            PdfRenderedPage preview = session.RenderFittedPage(0, 200, 100, includeFormFields: true);
            Assert.Contains(preview.RenderDiagnostics, item => item.BlocksPublication);
            if (variant == "missing-glyph")
                Assert.Contains(preview.RenderDiagnostics, item => item.Message == "A text glyph outline is not implemented.");
            Assert.Throws<InvalidOperationException>(() => session.RenderPageForEncoding(0));
            Assert.Throws<InvalidOperationException>(() => session.RenderPage(0));
        }
        finally { File.Delete(path); }
    }

    private static byte[] Create(bool explicitAppearance, string variant = "plain")
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
        var catalogReference = (PdfIndirectReference)source.Trailer[N("Root")];
        var catalog = (PdfDictionary)source.Resolve(catalogReference);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[N("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[N("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(pageReference);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary fontDictionary = D(("Type", N("Font")), ("Subtype", N("Type1")),
            ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding")),
            ("FontDescriptor", D(("Ascent", new PdfInteger(800)), ("Descent", new PdfInteger(-200)))));
        if (variant == "missing-glyph")
            fontDictionary = D(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
                ("Encoding", D(("BaseEncoding", N("WinAnsiEncoding")),
                    ("Differences", new PdfArray([new PdfInteger(0), N("u1F600")])))),
                ("FirstChar", new PdfInteger(0)), ("Widths", A(600)),
                ("ToUnicode", update.AddObject(new PdfStream(D(),
                    "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <00> <D83DDE00> endbfchar"u8.ToArray()))));
        PdfIndirectReference font = update.AddObject(fontDictionary);
        PdfDictionary resources = D(("Font", D(("Helv", font))));
        var entries = new Dictionary<PdfName, PdfObject>
        {
            [N("Type")] = N("Annot"), [N("Subtype")] = N("Widget"), [N("FT")] = N("Tx"),
            [N("F")] = new PdfInteger(4), [N("T")] = S("Text"), [N("V")] = S("HELLO"),
            [N("DA")] = S("/Helv 12 Tf 0 g"), [N("Rect")] = A(10, 10, 160, 32),
            [N("MK")] = D()
        };
        switch (variant)
        {
            case "rich-flag": entries[N("Ff")] = new PdfInteger(1 << 25); break;
            case "rich-value": entries[N("RV")] = S("<body><p>HELLO</p></body>"); break;
            case "multiline": entries[N("Ff")] = new PdfInteger(1 << 12); entries[N("V")] = S("HELLO\nWORLD"); break;
            case "readonly": entries[N("Ff")] = new PdfInteger(1); break;
            case "password": entries[N("Ff")] = new PdfInteger(1 << 13); break;
            case "comb": entries[N("Ff")] = new PdfInteger(1 << 24); entries[N("MaxLen")] = new PdfInteger(8); break;
            case "missing-font": entries[N("DA")] = S("/Missing 12 Tf 0 g"); break;
            case "missing-glyph": entries[N("V")] = new PdfString(Convert.FromHexString("FEFFD83DDE00"), PdfStringForm.Hexadecimal); break;
            case "auto-size": entries[N("DA")] = S("/Helv 0 Tf 0 g"); break;
            case "styled": entries[N("MK")] = D(("BG", A(1))); break;
            case "color": entries[N("DA")] = S("/Helv 12 Tf 1 0 0 rg"); break;
            case "long-value": entries[N("V")] = S(new string('W', 100)); break;
            case "aligned": entries[N("Q")] = new PdfInteger(1); break;
            case "empty": entries[N("V")] = S(""); break;
            case "short": entries[N("Rect")] = A(10, 10, 160, 14); break;
        }
        if (explicitAppearance)
        {
            PdfIndirectReference appearance = update.AddObject(new PdfStream(
                D(("Subtype", N("Form")), ("BBox", A(0, 0, 150, 22)), ("Resources", resources)),
                "q 1 1 148 20 re W n BT /Helv 12 Tf 0 g 1 7.4 Td (HELLO) Tj ET Q"u8.ToArray()));
            entries[N("AP")] = D(("N", appearance));
        }
        PdfIndirectReference widget = update.AddObject(new PdfDictionary(entries));
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page.Append(
            new KeyValuePair<PdfName, PdfObject>(N("Annots"), new PdfArray([widget])))));
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog.Append(
            new KeyValuePair<PdfName, PdfObject>(N("AcroForm"), D(
                ("Fields", new PdfArray([widget])), ("NeedAppearances", new PdfBoolean(!explicitAppearance || variant == "saved")),
                ("DR", resources), ("DA", S("/Helv 12 Tf 0 g")))))));
        return update.Build();
    }

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfString S(string value) => new(Encoding.ASCII.GetBytes(value), PdfStringForm.Literal);
    private static PdfArray A(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static PdfDictionary D(params (string Key, PdfObject Value)[] values) =>
        new(values.Select(value => new KeyValuePair<PdfName, PdfObject>(N(value.Key), value.Value)));
}
