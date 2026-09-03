using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfAttachmentReaderTests
{
    [Fact]
    public void ReadReturnsAttachmentMetadataPayloadAndSourceObjects()
    {
        byte[] payload = "attachment payload"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("evidence.txt", payload, "text/plain", "Case evidence",
                PdfAssociatedFileRelationship.Data)
            .Build());

        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(document));

        Assert.Equal("evidence.txt", attachment.FileName);
        Assert.Equal("Case evidence", attachment.Description);
        Assert.Equal("text/plain", attachment.MimeType);
        Assert.Equal(PdfAssociatedFileRelationship.Data, attachment.Relationship);
        Assert.Equal(payload, attachment.Data.ToArray());
        Assert.NotNull(attachment.FileSpecificationObjectNumber);
        Assert.NotNull(attachment.EmbeddedFileObjectNumber);
        Assert.False(attachment.HasUnsafeFileName);
        Assert.False(attachment.IsPotentiallyExecutable);
    }

    [Fact]
    public void ExtractionPathRejectsTraversalAndExecutableNamesAreFlagged()
    {
        Assert.Throws<ArgumentException>(() =>
            PdfAttachmentReader.GetSafeExtractionPath(Path.GetTempPath(), "..\\outside.txt"));
        string path = PdfAttachmentReader.GetSafeExtractionPath(Path.GetTempPath(), "inside.txt");
        Assert.Equal(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "inside.txt"), path);

        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("setup.exe", new byte[] { 1, 2, 3 }).Build());
        Assert.True(Assert.Single(PdfAttachmentReader.Read(document)).IsPotentiallyExecutable);
    }
}
