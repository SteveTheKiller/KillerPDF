using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfUntaggedFormRemovalTests
{
    [Fact]
    public void RemovingAFieldWithABrokenDeclaredTagMappingStillFails()
    {
        var original = PdfDocument.Open(TaggedPageWithUntaggedField(brokenFieldTag: true));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(original).RemoveFormField("Name").Build());

        Assert.Contains("no matching ParentTree structure element", error.Message);
        Assert.Equal("Name", Assert.Single(PdfFormWidgetReader.ReadPage(original, 0)).FieldName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntaggedRemovalCanRetainOrRemoveOtherTaggedFields(bool removeTaggedField)
    {
        byte[] source = TaggedPageWithUntaggedField(includeTaggedField: true);
        var original = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(original).RemoveFormField("Name");
        if (removeTaggedField) editor.RemoveFormField("Tagged");

        var reopened = PdfDocument.Open(editor.Build());

        var fields = PdfFormWidgetReader.ReadPage(reopened, 0);
        if (removeTaggedField) Assert.Empty(fields);
        else Assert.Equal("Tagged", Assert.Single(fields).FieldName);
        var root = Dictionary(reopened, new PdfIndirectReference(7, 0));
        var parentTree = Dictionary(reopened, root[Name("ParentTree")]);
        var entries = Assert.IsType<PdfArray>(parentTree[Name("Nums")]);
        Assert.Equal(removeTaggedField ? 2 : 4, entries.Count);
        Assert.Equal(0, Assert.IsType<PdfInteger>(entries[0]).Value);
        Assert.Equal(9, Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(entries[1]))).ObjectNumber);
        Assert.Equal(PdfObjectWriter.Write(original.Resolve(new PdfIndirectReference(9, 0))),
            PdfObjectWriter.Write(reopened.Resolve(new PdfIndirectReference(9, 0))));
        Assert.Equal(0, Assert.IsType<PdfInteger>(Dictionary(reopened,
            new PdfIndirectReference(3, 0))[Name("StructParents")]).Value);
        var documentElement = Dictionary(reopened, root[Name("K")]);
        if (removeTaggedField)
            Assert.Equal(9, Assert.IsType<PdfIndirectReference>(documentElement[Name("K")]).ObjectNumber);
        else
            Assert.Equal(PdfObjectWriter.Write(original.Resolve(new PdfIndirectReference(12, 0))),
                PdfObjectWriter.Write(reopened.Resolve(new PdfIndirectReference(12, 0))));
    }

    [Fact]
    public void RemovingAnUntaggedFieldPreservesThePagesExistingTextAndStructure()
    {
        byte[] source = TaggedPageWithUntaggedField();
        var original = PdfDocument.Open(source);
        Assert.Equal("Name", Assert.Single(PdfFormWidgetReader.ReadPage(original, 0)).FieldName);
        var renderOptions = new PdfRenderOptions(200, 100);
        var originalImage = new PdfPageRenderer(original).Render(0, renderOptions);
        Assert.Empty(originalImage.Diagnostics);

        byte[] saved = new PdfIncrementalPageEditor(original).RemoveFormField("Name").Build();

        Assert.Equal(source, saved[..source.Length]);
        var reopened = PdfDocument.Open(saved);
        Assert.Empty(PdfFormWidgetReader.ReadPage(reopened, 0));
        var reader = new PdfPageContentReader(reopened);
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("Keep this text", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        foreach (int objectNumber in new[] { 7, 8, 9, 10 })
        {
            var reference = new PdfIndirectReference(objectNumber, 0);
            Assert.Equal(PdfObjectWriter.Write(original.Resolve(reference)),
                PdfObjectWriter.Write(reopened.Resolve(reference)));
        }
        var image = new PdfPageRenderer(reopened).Render(0, renderOptions);
        Assert.Empty(image.Diagnostics);
        Assert.Equal(originalImage.Pixels.ToArray(), image.Pixels.ToArray());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static PdfDictionary Dictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? document.Resolve(reference) : value);

    private static byte[] TaggedPageWithUntaggedField(bool includeTaggedField = false, bool brokenFieldTag = false)
    {
        const string content = "/P <</MCID 0>> BDC\nBT /Helv 12 Tf 10 80 Td (Keep this text) Tj ET\nEMC\n";
        string[] objects =
        [
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R {(includeTaggedField ? "11 0 R" : "")}] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >> /MarkInfo << /Marked true >> /StructTreeRoot 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R {(includeTaggedField ? "11 0 R" : "")}] /Contents 6 0 R /Resources << /Font << /Helv 5 0 R >> >> /StructParents 0 >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /P 3 0 R /Rect [10 10 110 30] {(brokenFieldTag ? "/StructParent 1" : "")} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            $"<< /Type /StructTreeRoot /K 8 0 R /ParentTree 10 0 R /ParentTreeNextKey {(includeTaggedField ? 2 : 1)} >>",
            $"<< /Type /StructElem /S /Document /P 7 0 R /K {(includeTaggedField ? "[9 0 R 12 0 R]" : "9 0 R")} >>",
            "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R /K 0 >>",
            $"<< /Nums [0 [9 0 R] {(includeTaggedField ? "1 12 0 R" : "")}] >>",
            .. (includeTaggedField ? new[]
            {
                "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Tagged) /P 3 0 R /Rect [10 40 110 60] /StructParent 1 >>",
                "<< /Type /StructElem /S /Form /P 8 0 R /Pg 3 0 R /K << /Type /OBJR /Obj 11 0 R /Pg 3 0 R >> >>"
            } : [])
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int table = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
