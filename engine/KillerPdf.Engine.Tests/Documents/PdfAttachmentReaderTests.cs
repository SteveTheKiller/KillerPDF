using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using System.Text.Json;
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
        Assert.False(attachment.HasEncryptedContent);
    }

    [Fact]
    public void JsonReportIncludesMetadataWithoutPayloadData()
    {
        byte[] payload = "private attachment payload"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("evidence.txt", payload, "text/plain", "Case evidence")
            .Build());

        string report = PdfAttachmentReader.ToJson(document);
        using JsonDocument json = JsonDocument.Parse(report);

        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        JsonElement attachment = json.RootElement.GetProperty("attachments")[0];
        Assert.Equal("evidence.txt", attachment.GetProperty("fileName").GetString());
        Assert.Equal(payload.Length, attachment.GetProperty("byteCount").GetInt32());
        Assert.DoesNotContain("private attachment payload", report, StringComparison.Ordinal);
        Assert.False(attachment.TryGetProperty("data", out _));
    }

    [Fact]
    public void TextReportIncludesReviewDetailsWithoutPayloadData()
    {
        byte[] payload = "private attachment payload"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("evidence.txt", payload, "text/plain", "Case evidence")
            .AddFileAttachmentAnnotation(0, 20, 30, 24, "evidence.txt",
                "Open the evidence", PdfFileAttachmentIcon.PushPin)
            .Build());

        string report = PdfAttachmentReader.ToText(document);

        Assert.Contains("Attachments: 1", report, StringComparison.Ordinal);
        Assert.Contains("\"evidence.txt\": 26 bytes, text/plain", report, StringComparison.Ordinal);
        Assert.Contains("Description: \"Case evidence\"", report, StringComparison.Ordinal);
        Assert.Contains("Safety: no findings", report, StringComparison.Ordinal);
        Assert.Contains("Page 1, annotation 1", report, StringComparison.Ordinal);
        Assert.Contains("icon \"PushPin\"", report, StringComparison.Ordinal);
        Assert.Contains("Description: \"Open the evidence\"", report, StringComparison.Ordinal);
        Assert.DoesNotContain("private attachment payload", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadDetectsEncryptedPdfAndZipFamilyPayloads()
    {
        byte[] protectedPdf = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "reader",
                OwnerPassword = "owner"
            })
            .AddBlankPage()
            .Build();
        byte[] protectedZipHeader =
            [(byte)'P', (byte)'K', 3, 4, 20, 0, 1, 0];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("protected.pdf", protectedPdf, "application/pdf")
            .AddAttachment("protected.docx", protectedZipHeader,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
            .Build());

        Assert.All(PdfAttachmentReader.Read(document), attachment =>
            Assert.True(attachment.HasEncryptedContent));
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

    [Fact]
    public void ExtractWritesPayloadAndRequiresExplicitOverwrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
                .AddAttachment("evidence.txt", "first"u8.ToArray()).Build());
            PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(document));

            string path = PdfAttachmentReader.Extract(attachment, directory);

            Assert.Equal(Path.Combine(directory, "evidence.txt"), path);
            Assert.Equal("first"u8.ToArray(), File.ReadAllBytes(path));
            Assert.Throws<IOException>(() =>
                PdfAttachmentReader.Extract(attachment, directory));

            PdfDocument replacement = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
                .AddAttachment("evidence.txt", "second"u8.ToArray()).Build());
            PdfAttachmentReader.Extract(
                Assert.Single(PdfAttachmentReader.Read(replacement)), directory, overwrite: true);
            Assert.Equal("second"u8.ToArray(), File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExtractAllPreflightsEveryDestinationBeforeWriting()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
                .AddAttachment("first.txt", "first"u8.ToArray())
                .AddAttachment("second.txt", "second"u8.ToArray()).Build());
            IReadOnlyList<PdfAttachmentInfo> attachments =
                PdfAttachmentReader.Read(document);
            string existing = Path.Combine(directory, "second.txt");
            File.WriteAllText(existing, "preserved");

            Assert.Throws<IOException>(() =>
                PdfAttachmentReader.ExtractAll(attachments, directory));
            Assert.False(File.Exists(Path.Combine(directory, "first.txt")));
            Assert.Equal("preserved", File.ReadAllText(existing));

            IReadOnlyList<string> paths = PdfAttachmentReader.ExtractAll(
                attachments, directory, overwrite: true);
            Assert.Equal([Path.Combine(directory, "first.txt"), existing], paths);
            Assert.Equal("first"u8.ToArray(), File.ReadAllBytes(paths[0]));
            Assert.Equal("second"u8.ToArray(), File.ReadAllBytes(paths[1]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadPageAnnotationsReturnsPlacementIconDescriptionAndAttachment()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("evidence.txt", "evidence"u8.ToArray(), "text/plain")
            .AddFileAttachmentAnnotation(0, 20, 30, 24, "evidence.txt",
                "Open the evidence", PdfFileAttachmentIcon.PushPin)
            .Build());

        PdfAttachmentAnnotationInfo annotation = Assert.Single(
            PdfAttachmentReader.ReadPageAnnotations(document, 0));

        Assert.Equal((0, 0), (annotation.PageIndex, annotation.AnnotationIndex));
        Assert.Equal((20, 30, 44, 54),
            (annotation.Left, annotation.Bottom, annotation.Right, annotation.Top));
        Assert.Equal("PushPin", annotation.Icon);
        Assert.Equal("Open the evidence", annotation.Contents);
        Assert.Equal("evidence.txt", annotation.Attachment.FileName);
        Assert.Equal("evidence"u8.ToArray(), annotation.Attachment.Data.ToArray());
        Assert.NotNull(annotation.ObjectNumber);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfAttachmentReader.ReadPageAnnotations(document, 1));
    }

    [Fact]
    public void RenameAttachmentPreservesPayloadAndMetadata()
    {
        byte[] payload = "evidence"u8.ToArray();
        var modified = new DateTimeOffset(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("old.txt", payload, "text/plain", "Evidence",
                PdfAssociatedFileRelationship.Data, modified).Build());

        PdfDocument renamed = PdfDocument.Open(new PdfIncrementalPageEditor(original)
            .RenameAttachment("old.txt", "new.txt").Build());
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(renamed));

        Assert.Equal("new.txt", attachment.FileName);
        Assert.Equal(payload, attachment.Data.ToArray());
        Assert.Equal("text/plain", attachment.MimeType);
        Assert.Equal("Evidence", attachment.Description);
        Assert.Equal(modified, attachment.ModificationDate);
        Assert.Equal("old.txt", Assert.Single(PdfAttachmentReader.Read(original)).FileName);
    }

    [Fact]
    public void ReplaceAttachmentUpdatesPayloadAndPreservesMetadata()
    {
        byte[] originalPayload = "old evidence"u8.ToArray();
        byte[] replacementPayload = "new evidence"u8.ToArray();
        var created = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        var modified = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("evidence.txt", originalPayload, "text/plain", "Evidence",
                PdfAssociatedFileRelationship.Source, created, created).Build());

        PdfDocument replaced = PdfDocument.Open(new PdfIncrementalPageEditor(original)
            .ReplaceAttachment("EVIDENCE.TXT", replacementPayload, modified).Build());
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(replaced));

        Assert.Equal("evidence.txt", attachment.FileName);
        Assert.Equal(replacementPayload, attachment.Data.ToArray());
        Assert.Equal("text/plain", attachment.MimeType);
        Assert.Equal("Evidence", attachment.Description);
        Assert.Equal(PdfAssociatedFileRelationship.Source, attachment.Relationship);
        Assert.Equal(created, attachment.CreationDate);
        Assert.Equal(modified, attachment.ModificationDate);
        Assert.Equal(originalPayload,
            Assert.Single(PdfAttachmentReader.Read(original)).Data.ToArray());
    }

    [Fact]
    public void SetAttachmentDescriptionPreservesPayloadAndMetadata()
    {
        byte[] payload = "evidence"u8.ToArray();
        var created = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("evidence.txt", payload, "text/plain", "Old description",
                PdfAssociatedFileRelationship.Source, created, created).Build());

        PdfDocument changed = PdfDocument.Open(new PdfIncrementalPageEditor(original)
            .SetAttachmentDescription("EVIDENCE.TXT", "Reviewed evidence").Build());
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(changed));

        Assert.Equal("Reviewed evidence", attachment.Description);
        Assert.Equal(payload, attachment.Data.ToArray());
        Assert.Equal(PdfAssociatedFileRelationship.Source, attachment.Relationship);
        Assert.Equal(created, attachment.CreationDate);
        Assert.Equal(created, attachment.ModificationDate);
    }

    [Fact]
    public void SetAttachmentClassificationPreservesPayloadDescriptionAndDates()
    {
        byte[] payload = "evidence"u8.ToArray();
        var created = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("evidence.bin", payload, "application/octet-stream", "Evidence",
                PdfAssociatedFileRelationship.Unspecified, created, created).Build());

        PdfDocument changed = PdfDocument.Open(new PdfIncrementalPageEditor(original)
            .SetAttachmentClassification("EVIDENCE.BIN", "application/pdf",
                PdfAssociatedFileRelationship.Source).Build());
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(changed));

        Assert.Equal("application/pdf", attachment.MimeType);
        Assert.Equal(PdfAssociatedFileRelationship.Source, attachment.Relationship);
        Assert.Equal(payload, attachment.Data.ToArray());
        Assert.Equal("Evidence", attachment.Description);
        Assert.Equal(created, attachment.CreationDate);
        Assert.Equal(created, attachment.ModificationDate);
    }

    [Fact]
    public void ReadReturnsOrderedPortfolioValuesAndSubitemPrefixes()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("evidence.txt", "evidence"u8.ToArray()).Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary names = ResolveDictionary(source, catalog[Name("Names")]);
        PdfDictionary tree = ResolveDictionary(source, names[Name("EmbeddedFiles")]);
        PdfArray entries = Assert.IsType<PdfArray>(tree[Name("Names")]);
        PdfIndirectReference fileReference = Assert.IsType<PdfIndirectReference>(entries[1]);
        PdfDictionary file = ResolveDictionary(source, fileReference);
        var collectionItem = new PdfDictionary([
            new(Name("Score"), new PdfReal(4.5)),
            new(Name("Department"), new PdfDictionary([
                new(Name("D"), Text("Legal")),
                new(Name("P"), Text("Team: "))
            ]))
        ]);
        var updatedFile = new PdfDictionary(file.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("CI"), collectionItem)));
        PdfDocument document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(fileReference.ObjectNumber, updatedFile).Build());

        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(document));

        Assert.Equal([
            new PdfCollectionItemValue("Department", "Legal", null, "Team: "),
            new PdfCollectionItemValue("Score", null, 4.5, null)
        ], attachment.CollectionValues);
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference
            ? document.Resolve(reference) : value);

    private static PdfName Name(string value) =>
        new(System.Text.Encoding.ASCII.GetBytes(value));

    private static PdfString Text(string value) =>
        new(System.Text.Encoding.UTF8.GetBytes(value), PdfStringForm.Literal);
}
