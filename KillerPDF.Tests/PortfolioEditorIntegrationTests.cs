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
            Assert.Equal(["cover.pdf"], edited.AttachmentNames);

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
}
