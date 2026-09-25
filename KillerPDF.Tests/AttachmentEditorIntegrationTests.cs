using System.IO;
using System.Linq;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class AttachmentEditorIntegrationTests
{
    [Fact]
    public void ApplyAttachmentEdits_SwapsNamesUpdatesMetadataAndRemovesSelectedFile()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"killerpdf-attachments-{System.Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage()
                .AddAttachment("first.txt", "first payload"u8.ToArray(), "text/plain", "First")
                .AddAttachment("second.txt", "second payload"u8.ToArray(), "text/plain", "Second")
                .AddAttachment("remove.bin", "remove payload"u8.ToArray())
                .AddFileAttachmentAnnotation(0, 20, 30, 24, "first.txt")
                .Build());

            PdfEngineIntegration.ApplyAttachmentEdits(path,
            [
                new("first.txt", "second.txt", "Renamed first", "application/pdf",
                    PdfAssociatedFileRelationship.Source, 13),
                new("second.txt", "first.txt", "Second", "text/plain",
                    PdfAssociatedFileRelationship.Data, 14),
                new("remove.bin", "remove.bin", null, "application/octet-stream",
                    PdfAssociatedFileRelationship.Data, 14, Remove: true)
            ]);

            PdfDocument changed = PdfDocument.Open(File.ReadAllBytes(path));
            PdfAttachmentInfo[] attachments = [.. PdfAttachmentReader.Read(changed)
                .OrderBy(item => item.FileName, System.StringComparer.Ordinal)];
            Assert.Equal(["first.txt", "second.txt"],
                attachments.Select(item => item.FileName));
            Assert.Equal("second payload"u8.ToArray(), attachments[0].Data.ToArray());
            Assert.Equal("first payload"u8.ToArray(), attachments[1].Data.ToArray());
            Assert.Equal("Renamed first", attachments[1].Description);
            Assert.Equal("application/pdf", attachments[1].MimeType);
            Assert.Equal(PdfAssociatedFileRelationship.Source, attachments[1].Relationship);
            Assert.Equal("second.txt", Assert.Single(
                PdfAttachmentReader.ReadPageAnnotations(changed, 0)).Attachment.FileName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
