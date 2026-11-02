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
    public void AddAttachmentPreservesExistingFilesAndRejectsDuplicateNames()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-add-attachment-{System.Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage()
                .AddAttachment("existing.txt", "existing"u8.ToArray()).Build());
            PdfEngineIntegration.AddAttachment(path, "new.txt", "new payload"u8.ToArray());
            byte[] saved = File.ReadAllBytes(path);
            var files = PdfAttachmentReader.Read(PdfDocument.Open(saved));
            Assert.Equal(2, files.Count);
            Assert.Equal("new payload"u8.ToArray(), files.Single(file => file.FileName == "new.txt").Data.ToArray());
            Assert.Throws<System.ArgumentException>(() => PdfEngineIntegration.AddAttachment(path, "NEW.TXT", [1]));
            Assert.Equal(saved, File.ReadAllBytes(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

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

    // Every rewrite of a file copies its payload into the document again, and each Build appends one more
    // update, so the number of end-of-file markers counts the copies (R3C-022).
    [Fact]
    public void ApplyAttachmentEdits_RewritesARowOnceWhateverNumberOfFieldsItChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-attachment-once-{System.Guid.NewGuid():N}.pdf");
        try
        {
            byte[] payload = new byte[64 * 1024];
            new System.Random(7).NextBytes(payload);
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage()
                .AddAttachment("first.txt", payload, "text/plain", "First")
                .AddAttachment("other.txt", "other"u8.ToArray(), "text/plain", "Other")
                .Build());
            int before = EndOfFileMarkers(path);
            var items = PdfEngineIntegration.ReadAttachmentEditorItems(path, null);

            PdfEngineIntegration.ApplyAttachmentEdits(path,
            [
                items[0] with
                {
                    FileName = "renamed.txt", Description = "Renamed", MimeType = "application/pdf",
                    Relationship = PdfAssociatedFileRelationship.Source
                },
                items[1]
            ]);

            Assert.Equal(before + 1, EndOfFileMarkers(path));
            PdfAttachmentInfo[] saved = [.. PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path)))];
            PdfAttachmentInfo edited = saved.Single(file => file.FileName == "renamed.txt");
            Assert.Equal("renamed.txt", edited.NameTreeKey);
            Assert.Equal("Renamed", edited.Description);
            Assert.Equal("application/pdf", edited.MimeType);
            Assert.Equal(PdfAssociatedFileRelationship.Source, edited.Relationship);
            Assert.Equal(payload, edited.Data.ToArray());
            Assert.Equal("Other", saved.Single(file => file.FileName == "other.txt").Description);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ApplyAttachmentEdits_RewritesEachEditedRowOnceAndAnUntouchedRowNotAtAll()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-attachment-rows-{System.Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage()
                .AddAttachment("a.txt", "a"u8.ToArray(), "text/plain", "A")
                .AddAttachment("b.txt", "b"u8.ToArray(), "text/plain", "B")
                .AddAttachment("c.txt", "c"u8.ToArray(), "text/plain", "C")
                .Build());
            int before = EndOfFileMarkers(path);
            var items = PdfEngineIntegration.ReadAttachmentEditorItems(path, null);

            PdfEngineIntegration.ApplyAttachmentEdits(path,
            [
                items[0] with { FileName = "a2.txt", Description = "A2" },
                items[1] with { Description = "B2", MimeType = "application/pdf" },
                items[2]
            ]);

            Assert.Equal(before + 2, EndOfFileMarkers(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Two files that swap names must each move to a temporary name first; after that a row still takes
    // one rewrite for its final name and every other field.
    [Fact]
    public void ApplyAttachmentEdits_RewritesSwappedRowsOnceMoreForTheirFinalNameAndFields()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-attachment-swap-{System.Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage()
                .AddAttachment("first.txt", "first"u8.ToArray(), "text/plain", "First")
                .AddAttachment("second.txt", "second"u8.ToArray(), "text/plain", "Second")
                .Build());
            int before = EndOfFileMarkers(path);
            var items = PdfEngineIntegration.ReadAttachmentEditorItems(path, null);

            PdfEngineIntegration.ApplyAttachmentEdits(path,
            [
                items[0] with { FileName = "second.txt", Description = "Was first" },
                items[1] with { FileName = "first.txt", Description = "Was second" }
            ]);

            Assert.Equal(before + 4, EndOfFileMarkers(path));
            PdfAttachmentInfo[] saved = [.. PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path)))];
            PdfAttachmentInfo first = saved.Single(file => file.FileName == "first.txt");
            PdfAttachmentInfo second = saved.Single(file => file.FileName == "second.txt");
            Assert.Equal("second", System.Text.Encoding.ASCII.GetString(first.Data.Span));
            Assert.Equal("Was second", first.Description);
            Assert.Equal("first", System.Text.Encoding.ASCII.GetString(second.Data.Span));
            Assert.Equal("Was first", second.Description);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static int EndOfFileMarkers(string path) =>
        System.Text.RegularExpressions.Regex.Count(
            System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)), "%%EOF");
}
