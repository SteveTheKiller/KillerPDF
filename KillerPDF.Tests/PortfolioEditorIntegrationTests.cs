using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PortfolioEditorIntegrationTests
{
    [Fact]
    public void ApplyAndClearPortfolioPresentation_PreservesAttachments()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-portfolio-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage()
                .AddAttachment("cover.pdf", "payload"u8.ToArray())
                .Build());

            PdfEngineIntegration.ApplyPortfolioPresentation(
                path, PdfCollectionView.Tile, "cover.pdf");
            PdfEngineIntegration.PortfolioEditorState edited =
                PdfEngineIntegration.ReadPortfolioEditorState(path);
            Assert.Equal(PdfCollectionView.Tile, edited.Collection?.View);
            Assert.Equal("cover.pdf", edited.Collection?.InitialDocument);
            Assert.Equal(["cover.pdf"],
                edited.Attachments.Select(attachment => attachment.FileName));

            PdfEngineIntegration.ClearPortfolioMetadata(path);
            PdfDocument cleared = PdfDocument.Open(File.ReadAllBytes(path));
            Assert.Null(PdfCollectionReader.Read(cleared));
            Assert.Equal("cover.pdf", Assert.Single(PdfAttachmentReader.Read(cleared)).FileName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyPortfolioStructure_WritesSchemaFoldersSortAndAttachmentValues()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"killerpdf-portfolio-structure-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage()
                .AddAttachment("cover.pdf", "payload"u8.ToArray())
                .Build());
            PdfCollectionFieldInfo[] fields =
            [
                new()
                {
                    Key = "project", DisplayName = "Project", Subtype = "S",
                    Order = 1, IsVisible = true, IsEditable = true
                },
                new()
                {
                    Key = "score", DisplayName = "Score", Subtype = "N",
                    Order = 2, IsVisible = true
                }
            ];

            PdfEngineIntegration.ApplyPortfolioStructure(
                path,
                fields,
                [new PdfCollectionSortInfo("score", false)],
                [new PdfCollectionFolder(1, "Reports")],
                new Dictionary<string, IReadOnlyList<PdfCollectionItemValue>>
                {
                    ["cover.pdf"] =
                    [
                        new("project", "Apollo", null, null),
                        new("score", null, 98.5, null)
                    ]
                });

            PdfEngineIntegration.PortfolioEditorState state =
                PdfEngineIntegration.ReadPortfolioEditorState(path);
            Assert.Equal(["project", "score"],
                state.Collection!.Fields.Select(field => field.Key));
            Assert.Equal("score", Assert.Single(state.Collection.Sort).Key);
            Assert.False(Assert.Single(state.Collection.Sort).Ascending);
            Assert.Equal("Reports", Assert.Single(state.Collection.Folders).Name);
            Assert.Equal(2, Assert.Single(state.Attachments).Values.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
