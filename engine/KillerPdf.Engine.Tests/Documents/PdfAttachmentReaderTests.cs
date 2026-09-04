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
        var modificationDate = new DateTimeOffset(
            2026, 8, 22, 20, 0, 0, TimeSpan.FromHours(-7));
        var creationDate = new DateTimeOffset(
            2026, 8, 21, 9, 30, 0, TimeSpan.FromHours(-7));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("evidence.txt", payload, "text/plain", "Case evidence",
                PdfAssociatedFileRelationship.Data, modificationDate, creationDate)
            .Build());

        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(document));

        Assert.Equal("evidence.txt", attachment.FileName);
        Assert.Equal("Case evidence", attachment.Description);
        Assert.Equal("text/plain", attachment.MimeType);
        Assert.Equal(PdfAssociatedFileRelationship.Data, attachment.Relationship);
        Assert.Equal(payload, attachment.Data.ToArray());
        Assert.Equal(payload.LongLength, attachment.DeclaredSize);
        Assert.True(attachment.SizeMatches);
        Assert.Equal(creationDate, attachment.CreationDate);
        Assert.Equal(modificationDate, attachment.ModificationDate);
        Assert.Equal(16, attachment.DeclaredChecksum?.Length);
        Assert.True(attachment.ChecksumMatches);
        Assert.NotNull(attachment.FileSpecificationObjectNumber);
        Assert.NotNull(attachment.EmbeddedFileObjectNumber);
        Assert.False(attachment.HasUnsafeFileName);
        Assert.False(attachment.IsPotentiallyExecutable);
        Assert.False(attachment.HasExecutableContent);
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

        PdfDocument disguised = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("notes.txt", new byte[] { (byte)'M', (byte)'Z', 0, 0 }).Build());
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(disguised));
        Assert.False(attachment.IsPotentiallyExecutable);
        Assert.True(attachment.HasExecutableContent);
    }
}
