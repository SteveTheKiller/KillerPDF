using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererFieldCacheTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CachedFieldVisibilityMatchesUncachedInBothToggleOrders(bool signature, bool shownFirst)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(120, 60)
            .AddUriPushButton(0, "open", 10, 10, 100, 40, "Open", "https://example.com").Build());
        if (signature)
        {
            var widget = Assert.Single(PdfFormWidgetReader.ReadPage(document, 0));
            var reference = new PdfIndirectReference(widget.ObjectNumber, 0);
            var dictionary = (PdfDictionary)document.Resolve(reference);
            var update = new PdfIncrementalUpdateBuilder(document);
            update.ReplaceObject(widget.ObjectNumber, new PdfDictionary(dictionary
                .Where(entry => entry.Key.ToString() is not "/FT" and not "/V")
                .Append(new(new PdfName("FT"u8), new PdfName("Sig"u8)))
                .Append(new(new PdfName("V"u8), new PdfDictionary([])))));
            document = PdfDocument.Open(update.Build());
        }
        var renderer = new PdfPageRenderer(document);
        var hidden = new PdfRenderOptions(120, 60, includeFormFields: false);
        var shown = signature ? hidden with { IncludeSignedSignatureFields = true }
            : hidden with { IncludePushButtonFields = true };
        byte[] expectedHidden = renderer.Render(0, hidden with { CacheResult = false }).Pixels.ToArray();
        byte[] expectedShown = renderer.Render(0, shown with { CacheResult = false }).Pixels.ToArray();
        Assert.NotEqual(expectedHidden, expectedShown);
        renderer.Render(0, shownFirst ? shown : hidden);
        Assert.Equal(shownFirst ? expectedHidden : expectedShown,
            renderer.Render(0, shownFirst ? hidden : shown).Pixels.ToArray());
        Assert.Equal(shownFirst ? expectedShown : expectedHidden,
            renderer.Render(0, shownFirst ? shown : hidden).Pixels.ToArray());
    }
}
