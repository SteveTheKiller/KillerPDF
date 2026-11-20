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
    [Theory]
    [InlineData("replace")]
    [InlineData("rename")]
    [InlineData("description")]
    [InlineData("classification")]
    public void AttachmentEditsUpdateSharedPageIconTargets(string operation)
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().AddBlankPage()
            .AddAttachment("evidence.txt", "original"u8.ToArray(), "text/plain", "Evidence")
            .AddAttachment("other.txt", "unrelated"u8.ToArray(), "text/plain")
            .AddFileAttachmentAnnotation(0, 20, 30, 24, "evidence.txt")
            .AddFileAttachmentAnnotation(1, 20, 30, 24, "evidence.txt")
            .AddFileAttachmentAnnotation(1, 60, 30, 24, "other.txt")
            .Build();
        PdfDocument original = PdfDocument.Open(source);
        PdfDictionary catalog = ResolveDictionary(original, original.Trailer[Name("Root")]);
        PdfDictionary names = ResolveDictionary(original, catalog[Name("Names")]);
        PdfDictionary tree = ResolveDictionary(original, names[Name("EmbeddedFiles")]);
        PdfArray files = Assert.IsType<PdfArray>(tree[Name("Names")]);
        PdfIndirectReference reference = Assert.IsType<PdfIndirectReference>(files[1]);
        PdfDictionary specification = ResolveDictionary(original, reference);
        var collectionItem = new PdfDictionary([new(Name("Department"), Text("Legal"))]);
        specification = new PdfDictionary(specification.Concat([
            new KeyValuePair<PdfName, PdfObject>(Name("CI"), collectionItem),
            new KeyValuePair<PdfName, PdfObject>(Name("CustomValue"), Text("Preserved"))]));
        source = new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(reference.ObjectNumber, specification).Build();
        original = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(original);
        switch (operation)
        {
            case "replace": editor.ReplaceAttachment("EVIDENCE.TXT", "revised"u8.ToArray()); break;
            case "rename": editor.RenameAttachment("evidence.txt", "renamed.txt"); break;
            case "description": editor.SetAttachmentDescription("evidence.txt", null); break;
            case "classification": editor.SetAttachmentClassification("evidence.txt",
                "application/octet-stream", PdfAssociatedFileRelationship.Source); break;
        }
        byte[] bytes = editor.Build();
        Assert.Equal(source, bytes.AsSpan(0, source.Length).ToArray());
        PdfDocument changed = PdfDocument.Open(bytes);
        PdfAttachmentInfo registered = PdfAttachmentReader.Read(changed)
            .Single(item => item.FileName != "other.txt");
        Assert.Equal([new PdfCollectionItemValue("Department", "Legal", null, null)],
            registered.CollectionValues);
        Assert.Equal("Preserved"u8.ToArray(), Assert.IsType<PdfString>(
            ResolveDictionary(changed, reference)[Name("CustomValue")]).Bytes.ToArray());
        for (int page = 0; page < 2; page++)
        {
            PdfAttachmentInfo target = PdfAttachmentReader.ReadPageAnnotations(changed, page)[0].Attachment;
            Assert.Equal(registered.FileName, target.FileName);
            Assert.Equal(registered.Data.ToArray(), target.Data.ToArray());
            Assert.Equal(registered.Description, target.Description);
            Assert.Equal(registered.MimeType, target.MimeType);
            Assert.Equal(registered.Relationship, target.Relationship);
        }
        Assert.Equal("unrelated"u8.ToArray(),
            PdfAttachmentReader.ReadPageAnnotations(changed, 1)[1].Attachment.Data.ToArray());
        Assert.Equal("original"u8.ToArray(),
            PdfAttachmentReader.ReadPageAnnotations(original, 0)[0].Attachment.Data.ToArray());
    }

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

    [Theory]
    [InlineData("run.bat.")]
    [InlineData("run.bat ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("COM1.pdf")]
    [InlineData("LPT¹")]
    [InlineData("NUL .txt")]
    [InlineData("notes.txt:hidden")]
    [InlineData("a\\b.txt")]
    [InlineData("what?.txt")]
    public void NamesWindowsWouldRewriteOrOpenAsADeviceAreRefused(string name)
    {
        Assert.Throws<ArgumentException>(() => PdfAttachmentReader.GetSafeExtractionPath(Path.GetTempPath(), name));
    }

    [Theory]
    [InlineData("invoice.hta", true)]
    [InlineData("report.docm", true)]
    [InlineData("disk.iso", true)]
    [InlineData("shortcut.url", true)]
    [InlineData("tool.PS1", true)]
    [InlineData("contract.pdf", false)]
    [InlineData("console.log", false)]
    public void ExecutableExtensionsAreFlaggedIncludingATrailingDot(string name, bool executable)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment(name, new byte[] { 1, 2, 3 }).Build());

        Assert.Equal(executable, Assert.Single(PdfAttachmentReader.Read(document)).IsPotentiallyExecutable);
    }

    [Fact]
    public void AnExecutableNameWithATrailingDotIsFlaggedAndUnsafe()
    {
        // The builder refuses this name, so the file is written by hand.
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(run.bat.) 4 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>",
            "<< /Type /Filespec /F (run.bat.) /UF (run.bat.) /EF << /F 5 0 R >> >>",
            "<< /Type /EmbeddedFile /Length 3 >>\nstream\nabc\nendstream",
        ];
        var text = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(text.Length);
            text.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text.ToString());

        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(bytes)));

        Assert.Equal("run.bat.", attachment.FileName);
        Assert.True(attachment.IsPotentiallyExecutable);
        Assert.True(attachment.HasUnsafeFileName);
    }

    [Fact]
    public void ExtractionPathRejectsTraversalAndExecutableNamesAreFlagged()
    {
        // Traversal through the platform's own separator. Windows name rules apply everywhere,
        // so a backslash is refused on Linux too.
        Assert.Throws<ArgumentException>(() =>
            PdfAttachmentReader.GetSafeExtractionPath(Path.GetTempPath(), ".." + Path.DirectorySeparatorChar + "outside.txt"));
        Assert.Throws<ArgumentException>(() =>
            PdfAttachmentReader.GetSafeExtractionPath(Path.GetTempPath(), "../outside.txt"));
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

    // #455: files that share a name all land: the first under its name, each later one numbered before
    // its extension, past names the other files already use, and an existing file is still never replaced.
    [Fact]
    public void ExtractAllNumbersFilesThatShareAName()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            IReadOnlyList<PdfAttachmentInfo> manifests = PdfAttachmentReader.Read(
                PdfDocument.Open(RawPdfFixtures.RepeatedContentCredentials()));
            IReadOnlyList<PdfAttachmentInfo> reports = PdfAttachmentReader.Read(PdfDocument.Open(
                KeyedAttachments(false, ("k1", "report.pdf", "ONE"), ("k2", "report.pdf", "TWO"),
                    ("k3", "report (2).pdf", "THREE"))));

            IReadOnlyList<string> paths = PdfAttachmentReader.ExtractAll(manifests, directory);
            IReadOnlyList<string> reportPaths = PdfAttachmentReader.ExtractAll(reports, directory);

            Assert.Equal(["Content Credentials", "Content Credentials (2)", "Content Credentials (3)"],
                paths.Select(Path.GetFileName));
            Assert.Equal(["manifest 0", "manifest 1", "manifest 2"], paths.Select(File.ReadAllText));
            Assert.Equal(["report.pdf", "report (3).pdf", "report (2).pdf"], reportPaths.Select(Path.GetFileName));
            Assert.Equal(["ONE", "TWO", "THREE"], reportPaths.Select(File.ReadAllText));

            File.Delete(paths[0]);
            Assert.Throws<IOException>(() => PdfAttachmentReader.ExtractAll(manifests, directory));
            Assert.False(File.Exists(paths[0]));
            Assert.Equal("manifest 1", File.ReadAllText(paths[1]));
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

    public static TheoryData<string, string> KeyedScenariosAndOperations()
    {
        var data = new TheoryData<string, string>();
        foreach (string scenario in new[] { "cross", "duplicate", "case" })
            foreach (string operation in new[] { "remove", "rename", "replace", "description", "classification" })
                data.Add(scenario, operation);
        return data;
    }

    [Theory]
    [MemberData(nameof(KeyedScenariosAndOperations))]
    public void AttachmentEditsByKeyTouchExactlyThatFile(string scenario, string operation)
    {
        (byte[] source, string target, string other) = KeyedScenario(scenario);
        PdfAttachmentInfo[] before = [.. PdfAttachmentReader.Read(PdfDocument.Open(source))];
        PdfAttachmentInfo untouched = Assert.Single(before, item => item.NameTreeKey == other);
        PdfAttachmentInfo targeted = Assert.Single(before, item => item.NameTreeKey == target);
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(source));
        switch (operation)
        {
            case "remove": editor.RemoveAttachmentByKey(target); break;
            case "rename": editor.RenameAttachmentByKey(target, "renamed.txt"); break;
            case "replace": editor.ReplaceAttachmentByKey(target, "changed"u8.ToArray()); break;
            case "description": editor.SetAttachmentDescriptionByKey(target, "New note"); break;
            case "classification":
                editor.SetAttachmentClassificationByKey(target, "text/plain", PdfAssociatedFileRelationship.Source);
                break;
        }

        PdfAttachmentInfo[] after = [.. PdfAttachmentReader.Read(PdfDocument.Open(editor.Build()))];

        PdfAttachmentInfo kept = Assert.Single(after, item => item.NameTreeKey == other);
        Assert.Equal(untouched.FileName, kept.FileName);
        Assert.Equal(untouched.Data.ToArray(), kept.Data.ToArray());
        if (operation == "remove")
        {
            Assert.Single(after);
            return;
        }
        Assert.Equal(2, after.Length);
        string changedKey = operation == "rename" ? "renamed.txt" : target;
        PdfAttachmentInfo changed = Assert.Single(after, item => item.NameTreeKey == changedKey);
        Assert.Equal(operation == "rename" ? "renamed.txt" : targeted.FileName, changed.FileName);
        Assert.Equal(operation == "replace" ? "changed"u8.ToArray() : targeted.Data.ToArray(), changed.Data.ToArray());
        if (operation == "description") Assert.Equal("New note", changed.Description);
        if (operation == "classification")
        {
            Assert.Equal("text/plain", changed.MimeType);
            Assert.Equal(PdfAssociatedFileRelationship.Source, changed.Relationship);
        }
    }

    public static TheoryData<bool, string, int> RepeatedKeyOperations()
    {
        var data = new TheoryData<bool, string, int>();
        foreach (bool direct in new[] { false, true })
            foreach (string operation in new[] { "remove", "rename", "replace", "description", "classification" })
                foreach (int target in new[] { 0, 1, 2 })
                    data.Add(direct, operation, target);
        return data;
    }

    // #455: three files share the key "Content Credentials". An edit by attachment key touches exactly the
    // chosen one, keeps the others and their order, and leaves a document that still opens.
    [Theory]
    [MemberData(nameof(RepeatedKeyOperations))]
    public void AttachmentEditsByAttachmentKeyTouchOneOfTheFilesThatShareAKey(
        bool directSpecifications, string operation, int target)
    {
        byte[] source = RawPdfFixtures.RepeatedContentCredentials(directSpecifications);
        PdfAttachmentInfo[] before = [.. PdfAttachmentReader.Read(PdfDocument.Open(source))];
        string key = before[target].AttachmentKey!;
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(source));
        switch (operation)
        {
            case "remove": editor.RemoveAttachmentByKey(key); break;
            case "rename": editor.RenameAttachmentByKey(key, "renamed.txt"); break;
            case "replace": editor.ReplaceAttachmentByKey(key, "changed"u8.ToArray()); break;
            case "description": editor.SetAttachmentDescriptionByKey(key, "New note"); break;
            default:
                editor.SetAttachmentClassificationByKey(key, "text/plain", PdfAssociatedFileRelationship.Source);
                break;
        }

        PdfAttachmentInfo[] after = [.. PdfAttachmentReader.Read(PdfDocument.Open(editor.Build()))];

        string[] payloads = ["manifest 0", "manifest 1", "manifest 2"];
        string[] others = [.. payloads.Where((_, index) => index != target)];
        string[] expected = operation switch
        {
            "remove" => others,
            "rename" => [.. others, payloads[target]],
            "replace" => [.. payloads.Select((payload, index) => index == target ? "changed" : payload)],
            _ => payloads,
        };
        Assert.Equal(expected, after.Select(item => System.Text.Encoding.ASCII.GetString(item.Data.Span)));
        Assert.Equal(after.Length, after.Select(item => item.AttachmentKey).Distinct().Count());
        PdfAttachmentInfo[] changed = [.. after.Where((item, index) => operation switch
        {
            "rename" => item.FileName == "renamed.txt",
            "replace" => item.Data.Span.SequenceEqual("changed"u8),
            "description" => item.Description == "New note",
            "classification" => item.Relationship == PdfAssociatedFileRelationship.Source,
            _ => false,
        })];
        Assert.Equal(operation == "remove" ? 0 : 1, changed.Length);
        Assert.All(after.Except(changed), item =>
        {
            Assert.Equal("Content Credentials", item.FileName);
            Assert.Null(item.Description);
            Assert.Equal(before[0].Relationship, item.Relationship);
        });
    }

    // A key by itself names the first file that holds it, as it does for keys that only decode alike. A
    // listed name that several files share is refused.
    [Fact]
    public void ARepeatedKeyNamesTheFirstOfItsFilesAndARepeatedNameIsRefused()
    {
        byte[] source = RawPdfFixtures.RepeatedContentCredentials();
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(source));
        string key = PdfAttachmentReader.Read(PdfDocument.Open(source))[1].AttachmentKey!;

        Assert.Contains("Several attachments are named Content Credentials.",
            Assert.Throws<ArgumentException>(() => editor.RemoveAttachment("Content Credentials")).Message);
        editor.RemoveAttachmentByKey(key);
        Assert.Throws<ArgumentException>(() => editor.RemoveAttachmentByKey(key));
        editor.RemoveAttachmentByKey("Content Credentials");

        Assert.Equal(["manifest 2"], PdfAttachmentReader.Read(PdfDocument.Open(editor.Build()))
            .Select(item => System.Text.Encoding.ASCII.GetString(item.Data.Span)));
    }

    // #455: comments and attachment annotations still work on a document whose attachment keys repeat.
    // Only an annotation for the repeated name itself is refused, since it cannot say which file it shows.
    [Fact]
    public void AnnotationsWorkOnADocumentWhoseAttachmentKeysRepeat()
    {
        byte[] source = new PdfIncrementalPageEditor(PdfDocument.Open(RawPdfFixtures.RepeatedContentCredentials()))
            .AddAttachment("notes.txt", "notes"u8.ToArray(), "text/plain").Build();

        byte[] output = new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddTextNote(0, 20, 30, "A comment")
            .AddFileAttachmentAnnotation(0, 60, 30, 24, "notes.txt")
            .Build();

        PdfDocument document = PdfDocument.Open(output);
        Assert.Equal(["Content Credentials", "Content Credentials", "Content Credentials", "notes.txt"],
            PdfAttachmentReader.Read(document).Select(item => item.FileName));
        PdfAttachmentAnnotationInfo shown = Assert.Single(PdfAttachmentReader.ReadPageAnnotations(document, 0));
        Assert.Equal("notes.txt", shown.Attachment?.FileName);
        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddFileAttachmentAnnotation(0, 60, 30, 24, "Content Credentials"));
    }

    // A tree that repeats a key can name one file specification twice. Its page annotation is still read.
    [Fact]
    public void ReadPageAnnotationsReadsAFileThatARepeatedKeyNamesTwice()
    {
        PdfDocument document = PdfDocument.Open(RawPdfFixtures.RawPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(Content Credentials) 5 0 R (Content Credentials) 5 0 R] >> >> >>",
            "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            RawPdfFixtures.StreamObject("/Type /EmbeddedFile", "manifest 0"),
            "<< /Type /Filespec /F (Content Credentials) /EF << /F 4 0 R >> >>",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [20 20 40 40] /FS 5 0 R >>"));

        Assert.Equal(2, PdfAttachmentReader.Read(document).Count);
        PdfAttachmentAnnotationInfo shown = Assert.Single(PdfAttachmentReader.ReadPageAnnotations(document, 0));
        Assert.Equal("manifest 0", System.Text.Encoding.ASCII.GetString(shown.Attachment.Data.Span));
    }

    [Fact]
    public void RemoveAttachmentByNameUsesTheListedNameBeforeAnotherFilesKey()
    {
        (byte[] source, _, _) = KeyedScenario("cross");

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RemoveAttachment("Y").Build();

        // Key X lists itself as Y, and key Y lists itself as Z. "Y" names the file at key X.
        PdfAttachmentInfo kept = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(output)));
        Assert.Equal("Y", kept.NameTreeKey);
        Assert.Equal("Z", kept.FileName);
        Assert.Equal("PAYLOAD2"u8.ToArray(), kept.Data.ToArray());
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("rename")]
    [InlineData("replace")]
    [InlineData("description")]
    [InlineData("classification")]
    public void AttachmentEditsByNameRefuseAListedNameSeveralFilesShare(string operation)
    {
        (byte[] source, _, _) = KeyedScenario("duplicate");
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(source));

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
        {
            switch (operation)
            {
                case "remove": editor.RemoveAttachment("dup.txt"); break;
                case "rename": editor.RenameAttachment("dup.txt", "renamed.txt"); break;
                case "replace": editor.ReplaceAttachment("dup.txt", "changed"u8.ToArray()); break;
                case "description": editor.SetAttachmentDescription("dup.txt", "Note"); break;
                default:
                    editor.SetAttachmentClassification("dup.txt", "text/plain", PdfAssociatedFileRelationship.Data);
                    break;
            }
        });
        Assert.Contains("Several attachments are named dup.txt.", error.Message);
    }

    [Fact]
    public void RemoveAttachmentByKeyRefusesAKeyThatIsOnlyAListedName()
    {
        (byte[] source, _, _) = KeyedScenario("duplicate");
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(source));

        Assert.Throws<ArgumentException>(() => editor.RemoveAttachmentByKey("dup.txt"));
        Assert.Throws<ArgumentException>(() => editor.RemoveAttachmentByKey("K2"));
        editor.RemoveAttachmentByKey("k2");
        Assert.Throws<ArgumentException>(() => editor.RemoveAttachmentByKey("k2"));
    }

    [Theory]
    [InlineData("cross")]
    [InlineData("duplicate")]
    [InlineData("case")]
    public void RemovingAnAttachmentByKeyKeepsTheOtherFilesAssociation(string scenario)
    {
        (byte[] source, string target, string other) = KeyedScenario(scenario, associatedFiles: true);

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RemoveAttachmentByKey(target).Build();

        PdfDocument document = PdfDocument.Open(output);
        PdfAttachmentInfo kept = Assert.Single(PdfAttachmentReader.Read(document));
        Assert.Equal(other, kept.NameTreeKey);
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfArray associated = Assert.IsType<PdfArray>(catalog[Name("AF")]);
        PdfIndirectReference remaining = Assert.IsType<PdfIndirectReference>(Assert.Single(associated));
        Assert.Equal(kept.FileSpecificationObjectNumber, remaining.ObjectNumber);
    }

    [Theory]
    [InlineData("cross")]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("repeated")]
    [InlineData("repeated-direct")]
    public void SanitizeStripsEveryAttachmentWhateverTheirKeysAndListedNames(string scenario)
    {
        // #455: "repeated" is three files under the one key "Content Credentials".
        byte[] source = scenario.StartsWith("repeated", StringComparison.Ordinal)
            ? RawPdfFixtures.RepeatedContentCredentials(scenario == "repeated-direct")
            : KeyedScenario(scenario).Source;

        PdfOptimizationResult result = PdfOptimizer.CreatePlan(PdfDocument.Open(source),
            new PdfOptimizationOptions
            {
                RemoveAttachments = true,
                PackObjects = false,
                CompressStructure = false
            }).Apply();

        Assert.Empty(PdfAttachmentReader.Read(PdfDocument.Open(result.Data)));
    }

    [Fact]
    public void ANewAttachmentWhoseNameMatchesARetainedKeyApartFromCaseIsStillRefused()
    {
        (byte[] source, _, _) = KeyedScenario("case");
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .AddAttachment("A.TXT", "new"u8.ToArray());

        Assert.Throws<ArgumentException>(() => editor.Build());
    }

    /// <summary>
    /// Builds attachments whose name-tree key, listed file name and payload are all chosen by the caller,
    /// as files from PDFBox, LaTeX and hand-edited tools can be. Keys are written in byte order.
    /// </summary>
    internal static byte[] KeyedAttachments(
        bool associatedFiles, params (string Key, string Listed, string Payload)[] items)
    {
        var objects = new List<string>
        {
            "",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"
        };
        var names = new System.Text.StringBuilder();
        var specifications = new System.Text.StringBuilder();
        foreach ((string key, string listed, string payload) in items.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            int specification = objects.Count + 1;
            objects.Add($"<< /Type /Filespec /F ({listed}) /UF ({listed}) /EF << /F {specification + 1} 0 R >> >>");
            objects.Add($"<< /Type /EmbeddedFile /Length {payload.Length} >>\nstream\n{payload}\nendstream");
            names.Append($"({key}) {specification} 0 R ");
            specifications.Append($"{specification} 0 R ");
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [" + names + "] >> >>"
            + (associatedFiles ? " /AF [" + specifications + "]" : "") + " >>";
        var text = new System.Text.StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(text.ToString());
    }

    /// <summary>The three shapes where a listed name is not an identity: crossed, repeated and case-only keys.</summary>
    internal static (byte[] Source, string TargetKey, string OtherKey) KeyedScenario(
        string scenario, bool associatedFiles = false) => scenario switch
        {
            // Key X lists Y and key Y lists Z, so each file's key is another file's listed name.
            "cross" => (KeyedAttachments(associatedFiles, ("X", "Y", "PAYLOAD1"), ("Y", "Z", "PAYLOAD2")), "Y", "X"),
            "duplicate" => (KeyedAttachments(associatedFiles, ("k1", "dup.txt", "PAYLOAD1"), ("k2", "dup.txt", "PAYLOAD2")), "k2", "k1"),
            "case" => (KeyedAttachments(associatedFiles, ("A.txt", "A.txt", "PAYLOAD1"), ("a.txt", "a.txt", "PAYLOAD2")), "a.txt", "A.txt"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

    // #455: iLovePDF stores three C2PA manifests under one key; each is listed and has its own key.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadListsEveryFileWhenNameTreeKeysRepeat(bool directSpecifications)
    {
        PdfDocument document = PdfDocument.Open(
            RawPdfFixtures.RepeatedContentCredentials(directSpecifications));

        IReadOnlyList<PdfAttachmentInfo> attachments = PdfAttachmentReader.Read(document);

        Assert.Equal(["manifest 0", "manifest 1", "manifest 2"], attachments
            .Select(item => System.Text.Encoding.ASCII.GetString(item.Data.Span)));
        Assert.All(attachments, item => Assert.Equal("Content Credentials", item.NameTreeKey));
        Assert.Equal(3, attachments.Select(item => item.AttachmentKey).Distinct(StringComparer.Ordinal).Count());
        string text = PdfAttachmentReader.ToText(document);
        string json = PdfAttachmentReader.ToJson(document);
        Assert.Equal(3, JsonDocument.Parse(json).RootElement.GetProperty("attachments").GetArrayLength());
        Assert.DoesNotContain('\0', text);
        Assert.DoesNotContain("\\u0000", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadKeepsTheNameTreeKeyAsTheAttachmentKeyWhenKeysAreUnique()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("first.txt", "first"u8.ToArray())
            .AddAttachment("second.txt", "second"u8.ToArray()).Build());

        Assert.All(PdfAttachmentReader.Read(document),
            item => Assert.Equal(item.NameTreeKey, item.AttachmentKey));
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference
            ? document.Resolve(reference) : value);

    private static PdfName Name(string value) =>
        new(System.Text.Encoding.ASCII.GetBytes(value));

    private static PdfString Text(string value) =>
        new(System.Text.Encoding.UTF8.GetBytes(value), PdfStringForm.Literal);
}
