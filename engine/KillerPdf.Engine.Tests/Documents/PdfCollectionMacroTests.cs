using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfCollectionMacroTests
{
    [Fact]
    public void MacroRoundTripsAndExecutesPortfolioEdits()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("cover.pdf", "payload"u8.ToArray()).Build();
        var macro = new PdfMacro("Portfolio", [
            PdfCollectionMacro.PresentationStep(PdfCollectionView.Tile, "cover.pdf"),
            PdfCollectionMacro.FoldersStep([
                new PdfCollectionFolder(1, "Evidence"),
                new PdfCollectionFolder(2, "Photos", 1)]),
            PdfCollectionMacro.SchemaStep([
                new PdfCollectionFieldInfo
                {
                    Key = "Department", DisplayName = "Department",
                    Subtype = "S", IsVisible = true
                },
                new PdfCollectionFieldInfo
                {
                    Key = "Score", DisplayName = "Score",
                    Subtype = "N", IsVisible = true
                }
            ], [new PdfCollectionSortInfo("Score", false)]),
            PdfCollectionMacro.ItemValuesStep("cover.pdf", [
                new PdfCollectionItemValue("Department", "Legal", null, "Team: "),
                new PdfCollectionItemValue("Score", null, 4.5, null)])
        ]);
        PdfMacro restored = PdfMacro.FromJson(macro.ToJson());

        ReadOnlyMemory<byte> output = source;
        foreach (PdfMacroStep step in restored.Steps)
            output = PdfCollectionMacro.Execute(step, output);
        PdfCollectionInfo collection = Assert.IsType<PdfCollectionInfo>(
            PdfCollectionReader.Read(PdfDocument.Open(output)));

        Assert.Equal(PdfCollectionView.Tile, collection.View);
        Assert.Equal("cover.pdf", collection.InitialDocument);
        Assert.Equal(["Evidence", "Photos"],
            collection.Folders.Select(folder => folder.Name));
        Assert.Equal(1L, collection.Folders[1].ParentId);
        Assert.Equal(["Department", "Score"],
            collection.Fields.Select(field => field.Key));
        Assert.Equal(new PdfCollectionSortInfo("Score", false),
            Assert.Single(collection.Sort));
        Assert.Equal([
            new PdfCollectionItemValue("Department", "Legal", null, "Team: "),
            new PdfCollectionItemValue("Score", null, 4.5, null)
        ], Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(output))).CollectionValues);

        PdfDocument cleared = PdfDocument.Open(PdfCollectionMacro.Execute(
            PdfCollectionMacro.ClearStep(), output));
        Assert.Null(PdfCollectionReader.Read(cleared));
        Assert.Single(PdfAttachmentReader.Read(cleared));
    }
}
