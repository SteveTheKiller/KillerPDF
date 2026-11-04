using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfPopupImportCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportingPopupWithoutOptionalParentPreservesItsContentsAndForwardLink(bool linkedFromMarkup)
    {
        PdfDocument source = PdfDocument.Open(PopupDocument(linkedFromMarkup));

        byte[] output = new PdfIncrementalPageEditor(
                PdfDocument.Open(new PdfDocumentBuilder().Build()))
            .AddImportedDocument(source).Build();

        PdfDocument merged = PdfDocument.Open(output);
        PdfDictionary catalog = Dictionary(merged, merged.Trailer[Name("Root")]);
        PdfDictionary pages = Dictionary(merged, catalog[Name("Pages")]);
        PdfDictionary page = Dictionary(merged, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfArray annotations = Assert.IsType<PdfArray>(page[Name("Annots")]);
        Assert.Equal(linkedFromMarkup ? 2 : 1, annotations.Count);
        PdfDictionary popup = Dictionary(merged, annotations[0]);
        Assert.Equal("Popup", Assert.IsType<PdfName>(popup[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal("popup text", Encoding.ASCII.GetString(Assert.IsType<PdfString>(popup[Name("Contents")]).Bytes.Span));
        Assert.False(popup.ContainsKey(Name("Parent")));
        if (linkedFromMarkup)
        {
            PdfDictionary markup = Dictionary(merged, annotations[1]);
            var popupReference = Assert.IsType<PdfIndirectReference>(annotations[0]);
            var forwardLink = Assert.IsType<PdfIndirectReference>(markup[Name("Popup")]);
            Assert.Equal(popupReference.ObjectNumber, forwardLink.ObjectNumber);
            Assert.Equal(popupReference.Generation, forwardLink.Generation);
            Assert.Equal("markup text", Encoding.ASCII.GetString(Assert.IsType<PdfString>(markup[Name("Contents")]).Bytes.Span));
        }
    }

    [Theory]
    [InlineData("Highlight")]
    [InlineData("Underline")]
    [InlineData("Squiggly")]
    [InlineData("StrikeOut")]
    public void ImportingTextMarkupWithSavedAppearanceDoesNotRequireRegenerationGeometry(string subtype)
    {
        const string appearance = "1 0 0 rg 0 0 20 20 re f";
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /{subtype} /Rect [10 10 30 30] /AP << /N 5 0 R >> /Contents (kept markup) >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream"
        ]));
        byte[] expectedPixels = new PdfPageRenderer(source).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray();
        Assert.Contains(Enumerable.Range(0, expectedPixels.Length / 4), index =>
            expectedPixels[index * 4] == 0 && expectedPixels[index * 4 + 1] == 0 && expectedPixels[index * 4 + 2] == 255);

        byte[] output = new PdfIncrementalPageEditor(
                PdfDocument.Open(new PdfDocumentBuilder().Build()))
            .AddImportedDocument(source).Build();

        PdfDocument merged = PdfDocument.Open(output);
        Assert.Equal(expectedPixels, new PdfPageRenderer(merged).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray());
        PdfDictionary catalog = Dictionary(merged, merged.Trailer[Name("Root")]);
        PdfDictionary pages = Dictionary(merged, catalog[Name("Pages")]);
        PdfDictionary page = Dictionary(merged, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary annotation = Dictionary(merged, Assert.Single(Assert.IsType<PdfArray>(page[Name("Annots")])));
        Assert.False(annotation.ContainsKey(Name("QuadPoints")));
        PdfDictionary appearances = Dictionary(merged, annotation[Name("AP")]);
        Assert.IsType<PdfStream>(merged.Resolve(Assert.IsType<PdfIndirectReference>(appearances[Name("N")])));
        Assert.Equal("kept markup", Encoding.ASCII.GetString(Assert.IsType<PdfString>(annotation[Name("Contents")]).Bytes.Span));
    }

    [Theory]
    [InlineData("Highlight")]
    [InlineData("Underline")]
    [InlineData("Squiggly")]
    [InlineData("StrikeOut")]
    public void ImportingTextMarkupWithoutGeometryOrSavedAppearanceIsRejected(string subtype)
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /{subtype} /Rect [10 10 30 30] >>"
        ]));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(PdfDocument.Open(new PdfDocumentBuilder().Build()))
                .AddImportedDocument(source).Build());

        Assert.Contains("has no /QuadPoints array", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportingTwoPointPolygonPreservesItsSavedAppearance()
    {
        const string appearance = "1 0 0 RG 1 w 0 0 m 20 20 l h S";
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Polygon /Rect [10 10 30 30] /Vertices [0 0 20 20] /AP << /N 5 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream"
        ]));
        byte[] expectedPixels = new PdfPageRenderer(source).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray();
        Assert.Contains(Enumerable.Range(0, expectedPixels.Length / 4), index =>
            expectedPixels[index * 4] == 0 && expectedPixels[index * 4 + 1] == 0 && expectedPixels[index * 4 + 2] == 255);

        byte[] output = new PdfIncrementalPageEditor(
                PdfDocument.Open(new PdfDocumentBuilder().Build()))
            .AddImportedDocument(source).Build();

        Assert.Equal(expectedPixels, new PdfPageRenderer(PdfDocument.Open(output))
            .Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, "0 0 20 20")]
    [InlineData(true, "0 0 20")]
    [InlineData(true, "0 0 /Bad 20")]
    [InlineData(true, "0 0")]
    public void ImportingPolygonStillRejectsIncompleteOrMalformedGeometry(bool withAppearance, string vertices)
    {
        const string appearance = "1 0 0 RG 1 w 0 0 m 20 20 l h S";
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Polygon /Rect [10 10 30 30] /Vertices [{vertices}] {(withAppearance ? "/AP << /N 5 0 R >>" : "")} >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream"
        ]));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(PdfDocument.Open(new PdfDocumentBuilder().Build()))
                .AddImportedDocument(source).Build());

        Assert.Contains("/Vertices", error.Message, StringComparison.Ordinal);
    }

    private static byte[] PopupDocument(bool linkedFromMarkup)
    {
        var objects = new List<string> {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [{(linkedFromMarkup ? "4 0 R 5 0 R" : "4 0 R")}] >>",
            "<< /Type /Annot /Subtype /Popup /Rect [10 10 30 30] /Contents (popup text) >>"
        };
        if (linkedFromMarkup)
            objects.Add("<< /Type /Annot /Subtype /Circle /Rect [10 10 30 30] /Popup 4 0 R /Contents (markup text) >>");
        return BuildPdf(objects);
    }

    private static byte[] BuildPdf(IReadOnlyList<string> objects)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string body in objects)
        {
            offsets.Add(text.Length);
            text.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static PdfDictionary Dictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? document.Resolve(reference) : value);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
