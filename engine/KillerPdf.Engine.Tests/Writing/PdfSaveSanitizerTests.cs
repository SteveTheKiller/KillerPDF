using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfSaveSanitizerTests
{
    [Fact]
    public void RepairHarmlessArtifacts_RemovesEmptyOutlinesAndInvalidCropBox()
    {
        byte[] source = WithArtifacts(invalidCrop: true, emptyOutlines: true);

        PdfSaveRepairPlan plan = PdfSaveSanitizer.CreateRepairPlan(PdfDocument.Open(source));
        Assert.True(plan.HasChanges);
        Assert.Equal(source.Length, plan.OriginalSize);
        Assert.Collection(plan.Changes,
            change =>
            {
                Assert.Equal(PdfSaveRepairKind.RemoveDanglingOutlines, change.Kind);
                Assert.Null(change.PageIndex);
            },
            change =>
            {
                Assert.Equal(PdfSaveRepairKind.RemoveInvalidCropBox, change.Kind);
                Assert.Equal(0, change.PageIndex);
            });

        byte[] result = plan.Apply();
        PdfDocument reopened = PdfDocument.Open(result);
        PdfDictionary catalog = Dictionary(reopened, reopened.Trailer[Name("Root")]);
        Assert.False(catalog.ContainsKey(Name("Outlines")));
        PdfDictionary pages = Dictionary(reopened, catalog[Name("Pages")]);
        PdfArray kids = Assert.IsType<PdfArray>(pages[Name("Kids")]);
        PdfDictionary page = Dictionary(reopened, kids[0]);
        Assert.False(page.ContainsKey(Name("CropBox")));
    }

    [Fact]
    public void RepairHarmlessArtifacts_PreservesValidStateByteForByte()
    {
        byte[] source = WithArtifacts(invalidCrop: false, emptyOutlines: false);
        PdfSaveRepairPlan plan = PdfSaveSanitizer.CreateRepairPlan(PdfDocument.Open(source));
        Assert.False(plan.HasChanges);
        Assert.Empty(plan.Changes);
        Assert.Equal(source, plan.Apply());
    }

    private static PdfDictionary Dictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference
            ? document.Resolve(reference) : value);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] WithArtifacts(bool invalidCrop, bool emptyOutlines)
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 200).Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[Name("Root")]);
        PdfDictionary catalog = Dictionary(document, catalogReference);
        PdfDictionary pages = Dictionary(document, catalog[Name("Pages")]);
        PdfArray kids = Assert.IsType<PdfArray>(pages[Name("Kids")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(kids[0]);
        PdfDictionary page = Dictionary(document, pageReference);
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfArray crop = invalidCrop
            ? new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)])
            : new PdfArray([new PdfInteger(10), new PdfInteger(10), new PdfInteger(90), new PdfInteger(190)]);
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("CropBox"), crop))));
        if (emptyOutlines)
        {
            PdfIndirectReference outlines = update.AddObject(new PdfDictionary([
                new(Name("Type"), Name("Outlines")), new(Name("Count"), new PdfInteger(0))]));
            update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Outlines"), outlines))));
        }
        return update.Build();
    }
}
