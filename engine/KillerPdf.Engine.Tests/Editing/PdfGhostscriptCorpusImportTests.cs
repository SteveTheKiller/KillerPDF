using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfGhostscriptCorpusImportTests
{
    [Fact]
    public void ImportingAnEmptyAcroFormWithoutFieldsKeepsThePage()
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /NeedAppearances true >>"
        ]));

        byte[] output = new PdfIncrementalPageEditor(
                PdfDocument.Open(new PdfDocumentBuilder().Build()))
            .AddImportedDocument(source).Build();

        PdfDocument merged = PdfDocument.Open(output);
        Assert.Single(PdfPageInformation.Read(merged));
        PdfDictionary catalog = Dictionary(merged, merged.Trailer[Name("Root")]);
        PdfDictionary form = Dictionary(merged, catalog[Name("AcroForm")]);
        Assert.Empty(Assert.IsType<PdfArray>(form[Name("Fields")]));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("/Fields 7", false)]
    [InlineData("/Fields null", false)]
    public void ImportingMissingOrMalformedFieldsDoesNotHideWidgets(string fields, bool widget)
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] {(widget ? "/Annots [5 0 R]" : "")} >>",
            $"<< {fields} >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (editable) /Rect [10 10 30 30] >>"
        ]));

        Assert.Throws<InvalidOperationException>(() => Import(source));
    }

    [Fact]
    public void ImportingTextAnnotationWithNameEncodedStatePreservesTheState()
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /StateModel /Marked /State /Unmarked /Contents (retained) >>"
        ]));

        PdfDocument merged = PdfDocument.Open(Import(source));

        PdfDictionary annotation = FirstAnnotation(merged);
        Assert.Equal("Marked", Assert.IsType<PdfName>(annotation[Name("StateModel")]).ValueAsLatin1());
        Assert.Equal("Unmarked", Assert.IsType<PdfName>(annotation[Name("State")]).ValueAsLatin1());
    }

    [Fact]
    public void ImportingUnknownResourceEntriesPreservesTheirOriginalValues()
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSets [/PDF /Text] >> >>"
        ]));

        PdfDocument merged = PdfDocument.Open(Import(source));

        PdfDictionary page = FirstPage(merged);
        PdfDictionary resources = Dictionary(merged, page[Name("Resources")]);
        PdfArray extension = Assert.IsType<PdfArray>(resources[Name("ProcSets")]);
        Assert.Equal(["PDF", "Text"], extension.Select(item => Assert.IsType<PdfName>(item).ValueAsLatin1()));
    }

    [Fact]
    public void ImportingMarkedContentPropertiesPreservesPaginationMetadata()
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Properties << /R8 << /Type /Pagination /Subtype /Header >> >> >> >>"
        ]));

        PdfDocument merged = PdfDocument.Open(Import(source));

        PdfDictionary resources = Dictionary(merged, FirstPage(merged)[Name("Resources")]);
        PdfDictionary properties = Dictionary(merged, resources[Name("Properties")]);
        PdfDictionary header = Dictionary(merged, properties[Name("R8")]);
        Assert.Equal("Pagination", Assert.IsType<PdfName>(header[Name("Type")]).ValueAsLatin1());
        Assert.Equal("Header", Assert.IsType<PdfName>(header[Name("Subtype")]).ValueAsLatin1());
    }

    [Fact]
    public void ImportingOptionalContentPrintUsageDoesNotRequireOptionalSubtype()
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [4 0 R] /D << /Order [4 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /OCG /Name (Watermark) /Usage << /Print << /PrintState /ON >> >> >>"
        ]));

        PdfDocument merged = PdfDocument.Open(Import(source));

        PdfDictionary catalog = Dictionary(merged, merged.Trailer[Name("Root")]);
        PdfDictionary properties = Dictionary(merged, catalog[Name("OCProperties")]);
        PdfDictionary group = Dictionary(merged, Assert.IsType<PdfArray>(properties[Name("OCGs")])[0]);
        PdfDictionary usage = Dictionary(merged, group[Name("Usage")]);
        PdfDictionary print = Dictionary(merged, usage[Name("Print")]);
        Assert.Equal("ON", Assert.IsType<PdfName>(print[Name("PrintState")]).ValueAsLatin1());
        Assert.False(print.ContainsKey(Name("Subtype")));
    }

    [Fact]
    public void ImportingPatternWithIndexedBasePreservesTheColorSpace()
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ColorSpace << /CS2 [/Pattern [/Indexed /DeviceGray 0 <00>]] >> >> >>"
        ]));

        PdfDocument merged = PdfDocument.Open(Import(source));

        PdfDictionary resources = Dictionary(merged, FirstPage(merged)[Name("Resources")]);
        PdfDictionary spaces = Dictionary(merged, resources[Name("ColorSpace")]);
        PdfArray pattern = Assert.IsType<PdfArray>(spaces[Name("CS2")]);
        PdfArray palette = Assert.IsType<PdfArray>(pattern[1]);
        Assert.Equal("Indexed", Assert.IsType<PdfName>(palette[0]).ValueAsLatin1());
        Assert.Equal(new byte[] { 0 }, Assert.IsType<PdfString>(palette[3]).Bytes.ToArray());
    }

    [Fact]
    public void ImportingSavedAnnotationAppearanceWithImplicitFormSubtypePreservesPixels()
    {
        const string appearance = "1 0 0 rg 0 0 20 20 re f";
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Stamp /Rect [10 10 30 30] /AP << /N 5 0 R >> >>",
            $"<< /BBox [0 0 20 20] /Resources << >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream"
        ]));
        byte[] expected = new PdfPageRenderer(source).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray();
        Assert.Contains(Enumerable.Range(0, expected.Length / 4), index =>
            expected[index * 4] == 0 && expected[index * 4 + 1] == 0 && expected[index * 4 + 2] == 255);

        PdfDocument merged = PdfDocument.Open(Import(source));

        Assert.Equal(expected, new PdfPageRenderer(merged).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray());
    }

    [Fact]
    public void ImportingSimpleFontDescriptorPreservesLanguageNameMetadata()
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F0 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Roboto-Regular /FirstChar 0 /LastChar 0 /Widths [500] /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /Roboto-Regular /Flags 32 /FontBBox [0 0 500 500] /ItalicAngle 0 /Ascent 500 /Descent 0 /CapHeight 500 /StemV 80 /Lang /ku >>"
        ]));

        PdfDocument merged = PdfDocument.Open(Import(source));

        PdfDictionary resources = Dictionary(merged, FirstPage(merged)[Name("Resources")]);
        PdfDictionary fonts = Dictionary(merged, resources[Name("Font")]);
        PdfDictionary font = Dictionary(merged, fonts[Name("F0")]);
        PdfDictionary descriptor = Dictionary(merged, font[Name("FontDescriptor")]);
        Assert.Equal("ku", Assert.IsType<PdfName>(descriptor[Name("Lang")]).ValueAsLatin1());
    }

    [Fact]
    public void ImportingImageWithSoftAndColorKeyMasksPreservesSoftMaskPrecedence()
    {
        const string paint = "q 50 0 0 50 0 0 cm /Im1 Do Q";
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask [255 255 0 0 0 0] /SMask 5 0 R /Filter /ASCIIHexDecode /Length 7 >>\nstream\nFF0000>\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length 3 >>\nstream\n80>\nendstream",
            $"<< /Length {paint.Length} >>\nstream\n{paint}\nendstream"
        ]));
        byte[] expected = new PdfPageRenderer(source).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray();
        Assert.Contains(Enumerable.Range(0, expected.Length / 4), index =>
            expected[index * 4] is >= 126 and <= 129
            && expected[index * 4 + 1] is >= 126 and <= 129 && expected[index * 4 + 2] == 255);

        PdfDocument merged = PdfDocument.Open(Import(source));

        Assert.Equal(expected, new PdfPageRenderer(merged).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray());
    }

    [Fact]
    public void ImportingNestedAndRootFieldsPreservesInheritedFontsAndAlignment()
    {
        PdfDocument source = PdfDocument.Open(BuildPdf([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 220 100] /Annots [7 0 R 9 0 R] >>",
            "<< /Fields [5 0 R 9 0 R] /DR << /Font << /F1 6 0 R /F2 8 0 R >> >> /DA (/F1 12 Tf 0 g) /Q 0 /NeedAppearances true >>",
            "<< /T (group) /Kids [7 0 R] /DA (/F2 12 Tf 0 g) /Q 2 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /Parent 5 0 R /P 3 0 R /T (nested) /V (Mixed) /Rect [10 10 100 35] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /P 3 0 R /T (direct) /V (Mixed) /Rect [110 10 210 35] >>"
        ]));
        PdfRenderedPage before = new PdfPageRenderer(source).Render(0, new PdfRenderOptions(220, 100));
        Assert.Contains("A requested form-field text appearance was regenerated.", before.Diagnostics);

        PdfDocument merged = PdfDocument.Open(Import(source));

        PdfRenderedPage after = new PdfPageRenderer(merged).Render(0, new PdfRenderOptions(220, 100));
        Assert.Equal(before.Diagnostics, after.Diagnostics);
        Assert.True(before.Pixels.Span.SequenceEqual(after.Pixels.Span),
            "Import changed the inherited font or alignment of a nested or root form field.");
    }

    private static byte[] Import(PdfDocument source) => new PdfIncrementalPageEditor(
        PdfDocument.Open(new PdfDocumentBuilder().Build())).AddImportedDocument(source).Build();

    private static PdfDictionary FirstAnnotation(PdfDocument document)
        => Dictionary(document, Assert.IsType<PdfArray>(FirstPage(document)[Name("Annots")])[0]);

    private static PdfDictionary FirstPage(PdfDocument document)
    {
        PdfDictionary catalog = Dictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = Dictionary(document, catalog[Name("Pages")]);
        return Dictionary(document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
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
