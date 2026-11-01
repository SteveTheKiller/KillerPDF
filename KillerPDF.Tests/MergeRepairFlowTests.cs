using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// What the merge, drop and folder-combine flows do with a file they cannot take as it is: the check
// has to see what the merge will see, only parser damage offers a repair, every file left out is
// counted, and a window that changes underneath says so (R3A-008, R3C-006 to 009, 017, 018, 022, 028 to 030).
public sealed class MergeRepairFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-merge-repair-" + Guid.NewGuid().ToString("N"));

    public MergeRepairFlowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // ---- R3C-006: Sanitize removes every link, including the ones the reader cannot read ----

    [Fact]
    public void SanitizeWithLinksOnRemovesAGoodLinkAndABadOne()
    {
        string path = Write("links.pdf", Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R 5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (https://example.com) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI <FEFFD800> >> >>",
            "<< /Type /Annot /Subtype /Text /Rect [20 20 30 30] /Contents (kept) >>"));
        Assert.Equal([0, 1], PdfLinkReader.ReadLinkAnnotationIndexes(PdfDocument.Open(File.ReadAllBytes(path)), 0));

        PdfEngineIntegration.SanitizeFile(path, new PdfEngineIntegration.SanitizeChoices(
            Comments: false, Attachments: false, FormFields: false, HiddenLayers: false,
            Scripts: false, Links: true, Bookmarks: false));

        PdfDocument changed = PdfDocument.Open(File.ReadAllBytes(path));
        Assert.Empty(PdfLinkReader.ReadLinkAnnotationIndexes(changed, 0));
        Assert.Equal("kept", Assert.Single(PdfCommentReader.Read(changed)).Contents);
    }

    // ---- R3C-007 and R3C-030: the check is the merge's own page copy ----

    [Fact]
    public void AFileWhoseDamageSitsInAnObjectOnlyThePageCopyReadsFailsTheCheck()
    {
        string path = Write("lazy.pdf", LazilyDamaged());
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))));

        Exception failure = Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.ValidateImportable(path));

        Assert.True(PdfEngineIntegration.IsParserDamage(failure), failure.GetType().Name + ": " + failure.Message);
        // The real merge fails on the same file, which is why the check has to.
        Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.MergeFiles([Write("plain.pdf", Plain()), path]));
    }

    [Theory]
    [InlineData("poppler", "poppler-41005-0.pdf")]
    [InlineData("PDFBOX", "PDFBOX-3668-0.zip-8.pdf")]
    [InlineData("poppler-gitlab", "poppler-786-0.pdf")]
    [InlineData("GHOSTSCRIPT", "GHOSTSCRIPT-695207-0.pdf")]
    public void TheFourFilesTheReviewNamedFailTheCheckAndOfferTheRepair(string folder, string name)
    {
        string path = CorpusFile("stress", "pdf-association", folder, name);
        if (!File.Exists(path)) return;

        Exception failure = Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.ValidateImportable(path));

        Assert.True(PdfEngineIntegration.IsParserDamage(failure), failure.GetType().Name + ": " + failure.Message);
    }

    [Fact]
    public void AHealthyTaggedFileAndAFileWithOnlyALeftoverStructureKeyPassTheCheck()
    {
        byte[] tagged = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Tagged", Language = "en-US" })
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginMarkedContent(PdfStructureType.Figure, 0).Rectangle(10, 10, 20, 20).Fill().EndMarkedContent())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "A square")
            .Build();
        string taggedPath = Write("tagged.pdf", tagged);
        string leftover = Write("leftover.pdf", LeftoverStructureKeys(3));
        string target = Write("target.pdf", Plain());

        PdfEngineIntegration.ValidateImportable(taggedPath);
        PdfEngineIntegration.ValidateImportable(leftover);
        PdfEngineIntegration.InsertDocuments(target,
            [new PdfEngineIntegration.ImportedDocument(leftover, [0, 0, 0])], int.MaxValue);

        Assert.Equal(4, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(target))).Count);
    }

    [Fact]
    public void AHealthyFileThatOnlyOpensAfterRecoveryStillPassesTheCheck()
    {
        byte[] damaged = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(Plain()).Replace("startxref\n", "startxref\n9", StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(damaged));
        string path = Write("recovered.pdf", damaged);

        PdfEngineIntegration.ValidateImportable(path);
    }

    [Fact]
    public void AFileWithNoPagesFailsTheCheckWithAPlainMessageThatNamesIt()
    {
        string path = Write("empty-pages.pdf", Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>"));

        var failure = Assert.Throws<PdfEngineIntegration.ImportNoPagesException>(
            () => PdfEngineIntegration.ValidateImportable(path));

        Assert.Equal("empty-pages.pdf has no pages, so there is nothing to merge.", failure.Message);
        Assert.False(PdfEngineIntegration.IsParserDamage(failure));
    }

    [Fact]
    public void OnlyParserDamageOffersARepair()
    {
        var syntax = new PdfSyntaxException("An indirect object must end with the endobj keyword", 40);

        Assert.True(PdfEngineIntegration.IsParserDamage(syntax));
        Assert.True(PdfEngineIntegration.IsParserDamage(PdfEngineIntegration.WithImportSource(@"C:\a.pdf", syntax)));
        Assert.True(PdfEngineIntegration.IsParserDamage(new FormatException("A stream is damaged.")));
        // A validator refusing a form, a name tree or a destination is not damage, and a repair does not help it.
        Assert.False(PdfEngineIntegration.IsParserDamage(new NotSupportedException("A selected widget is not reachable from the source AcroForm /Fields tree.")));
        Assert.False(PdfEngineIntegration.IsParserDamage(
            PdfEngineIntegration.WithImportSource(@"C:\a.pdf", new InvalidOperationException("A page-label range starts outside the document."))));
        Assert.False(PdfEngineIntegration.IsParserDamage(new DamagedSaveRefusedException(DamagedSaveRefusal.Encrypted)));
        Assert.False(PdfEngineIntegration.IsParserDamage(new OperationCanceledException()));
    }

    // ---- R3C-008: a folder combine says what it left out, and the leftover structure key no longer costs a file ----

    [Fact]
    public void MergeReadableFilesReturnsTheFilesItLeftOutAndKeepsEveryPageOfTheRest()
    {
        string plain = Write("plain.pdf", Plain());
        string leftover = Write("leftover.pdf", LeftoverStructureKeys(5));
        string broken = Write("broken.pdf", Encoding.ASCII.GetBytes("not a pdf at all"));
        string image = Write("fake.png", Encoding.ASCII.GetBytes("not an image either"));
        string empty = Write("empty-pages.pdf", Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>"));

        byte[] merged = PdfEngineIntegration.MergeReadableFiles(
            [plain, leftover, broken, empty, image], out IReadOnlyList<string> leftOut);

        Assert.Equal(6, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
        Assert.Equal([broken, empty, image], leftOut);
    }

    [Fact]
    public void MergeReadableFilesReturnsNothingLeftOutWhenEveryFileIsTaken()
    {
        byte[] merged = PdfEngineIntegration.MergeReadableFiles(
            [Write("a.pdf", Plain()), Write("b.pdf", Plain())], out IReadOnlyList<string> leftOut);

        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
        Assert.Empty(leftOut);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARealFileWithLeftoverStructureKeysKeepsItsPagesWhereverItSitsInTheCombine(bool first)
    {
        string path = CorpusFile("conformance", "preservation", "openpreserve-format-corpus",
            "jhove-errors", "PDF-HUL-34", "jcpe13896_am.pdf");
        if (!File.Exists(path)) return;
        string plain = Write("plain.pdf", Plain());
        int pages = new PdfIncrementalPageEditor(PdfDocument.Open(File.ReadAllBytes(path))).PageCount;
        Assert.Equal(23, pages);   // 24 with the plain page beside it

        byte[] merged = PdfEngineIntegration.MergeReadableFiles(first ? [path, plain] : [plain, path], out IReadOnlyList<string> leftOut);

        Assert.Equal(pages + 1, new PdfIncrementalPageEditor(PdfDocument.Open(merged)).PageCount);
        Assert.Empty(leftOut);
    }

    // ---- R3C-009: a damaged outline is told apart by what failed, not by a word in a path ----

    [Fact]
    public void ADanglingOutlinesEntryDoesNotStopARasterReplacementOrAMerge()
    {
        byte[] dangling = Pdf(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 99 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        string path = Write("dangling.pdf", dangling);
        string replacement = Write("replacement.pdf", Plain());

        PdfEngineIntegration.ReplaceAllPagesAndCompact(path, [replacement]);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))));
        byte[] merged = PdfEngineIntegration.MergeFiles([Write("dangling-again.pdf", dangling), replacement]);
        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
    }

    [Fact]
    public void AWordInAFilePathIsNotADamagedOutline()
    {
        const string path = @"C:\Users\steve\Bookmarks\bookmark-list.pdf";

        Assert.False(PdfEngineIntegration.IsBookmarkGraphFailure(
            PdfEngineIntegration.WithImportSource(path, new InvalidOperationException("The page tree is damaged."))));
        Assert.False(PdfEngineIntegration.IsBookmarkGraphFailure(
            new FileNotFoundException("Could not find file '" + path + "'.", path)));
        Assert.False(PdfEngineIntegration.IsBookmarkGraphFailure(
            PdfEngineIntegration.WithImportSource(path, new FileNotFoundException("Could not find file '" + path + "'.", path))));

        // The outline failures the editor reports still count, bare or wrapped with the source's path.
        var outline = new InvalidOperationException("A bookmark item is not a dictionary.");
        Assert.True(PdfEngineIntegration.IsBookmarkGraphFailure(outline));
        Assert.True(PdfEngineIntegration.IsBookmarkGraphFailure(PdfEngineIntegration.WithImportSource(path, outline)));
        Assert.True(PdfEngineIntegration.IsBookmarkGraphFailure(
            new InvalidOperationException("The catalog /Outlines value is not a dictionary.")));
    }

    [Fact]
    public void AMergeWhosePathNamesBookmarksReportsItsOwnFailureWithoutARetry()
    {
        string folder = Path.Combine(_directory, "bookmarks");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "bad.pdf");
        File.WriteAllBytes(path, LazilyDamaged());

        var failure = Assert.ThrowsAny<Exception>(() =>
            PdfEngineIntegration.MergeFiles([Write("plain.pdf", Plain()), path]));

        Assert.StartsWith("\"" + path + "\": ", failure.Message);
        Assert.False(PdfEngineIntegration.IsBookmarkGraphFailure(failure));
    }

    // ---- R3C-022: an attachment edit costs one copy of the payload per change, not a copy per step ----

    [Fact]
    public void ARenameWithNoSwapSkipsTheTemporaryNameAndCopiesThePayloadOncePerChange()
    {
        byte[] payload = new byte[400_000];
        new Random(7).NextBytes(payload);
        string path = Write("attachment.pdf", new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("data.bin", payload, "application/octet-stream", "Before").Build());
        long start = new FileInfo(path).Length;
        PdfAttachmentInfo original = PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path))).Single();

        PdfEngineIntegration.ApplyAttachmentEdits(path,
        [
            new("data.bin", "renamed.bin", "After", "text/plain", PdfAssociatedFileRelationship.Source, payload.Length,
                Key: original.NameTreeKey)
        ]);

        PdfAttachmentInfo changed = PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path))).Single();
        Assert.Equal(("renamed.bin", "After", "text/plain"), (changed.FileName, changed.Description, changed.MimeType));
        Assert.Equal(payload, changed.Data.ToArray());
        // Three changes are three copies at most. The two hops through a temporary name made it four.
        Assert.True(new FileInfo(path).Length - start < 3.5 * payload.Length,
            $"The file grew from {start} to {new FileInfo(path).Length}.");
    }

    [Fact]
    public void AnEditOfOneFieldCopiesThePayloadOnce()
    {
        byte[] payload = new byte[400_000];
        new Random(8).NextBytes(payload);
        string path = Write("attachment.pdf", new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("data.bin", payload, "application/octet-stream", "Before").Build());
        long start = new FileInfo(path).Length;
        PdfAttachmentInfo original = PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path))).Single();

        PdfEngineIntegration.ApplyAttachmentEdits(path,
        [
            new("data.bin", "data.bin", "After", "application/octet-stream", PdfAssociatedFileRelationship.Data,
                payload.Length, Key: original.NameTreeKey)
        ]);

        Assert.Equal("After", PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path))).Single().Description);
        Assert.True(new FileInfo(path).Length - start < 1.5 * payload.Length,
            $"The file grew from {start} to {new FileInfo(path).Length}.");
    }

    [Fact]
    public void TwoFilesStillSwapNamesAfterTheChange()
    {
        string path = Write("swap.pdf", new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("one.txt", "first"u8.ToArray(), "text/plain")
            .AddAttachment("two.txt", "second"u8.ToArray(), "text/plain").Build());

        PdfEngineIntegration.ApplyAttachmentEdits(path,
        [
            new("one.txt", "two.txt", null, "text/plain", PdfAssociatedFileRelationship.Data, 5),
            new("two.txt", "one.txt", null, "text/plain", PdfAssociatedFileRelationship.Data, 6)
        ]);

        PdfAttachmentInfo[] files = [.. PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path)))
            .OrderBy(file => file.FileName, StringComparer.Ordinal)];
        Assert.Equal("second"u8.ToArray(), files[0].Data.ToArray());
        Assert.Equal("first"u8.ToArray(), files[1].Data.ToArray());
    }

    // ---- the shell: pinned in source, because a window is the only seam ----

    [Fact]
    public void EveryOpenPathThatCanFailAfterTheDocumentIsAssignedAbandonsIt()
    {
        string source = ShellSource("FileOperations.cs");

        foreach (string method in new[] { "TryRepairAndOpen", "OpenCertificateProtected", "StripEncryptionAndOpen", "OpenViewingCopy" })
        {
            string body = MethodBody(source, method);
            Match finalCatch = Regex.Match(body, @"catch \(Exception[^)]*\)\s*(when \([^)]*\)\s*)?\{(?<handler>.*?)\n            \}", RegexOptions.Singleline);
            Assert.True(finalCatch.Success, method + " has no general catch.");
            Assert.Contains("AbandonFailedOpen();", finalCatch.Groups["handler"].Value);
        }
    }

    [Fact]
    public void TheWindowMergeHoldsTheWindowAcrossTheCheckAndSaysWhenItDropsTheMerge()
    {
        string operations = ShellSource("FileOperations.cs");
        string drop = ShellSource("DragDrop.cs");
        string merge = MethodBody(operations, "MergeAtIndex");
        string append = MethodBody(drop, "AppendFilesToCurrentDoc");

        foreach (string body in new[] { merge, append })
        {
            // The check and the page read run off the UI thread, behind the overlay the repair uses.
            Assert.Contains("RunBusyAsync(", body);
            Assert.DoesNotMatch(@"await Task\.Run\(\(\) => PdfEngineIntegration\.ValidateImportable", body);
            // Only parser damage is offered a repair.
            Assert.Contains("when (PdfEngineIntegration.IsParserDamage(", body);
            // Both ways out of a changed window report it.
            Assert.Matches(@"if \(_doc is null \|\| !isTargetCurrent\(\)\)\s*\{\s*SetStatus\(Loc\(""Str_Merge_TargetChanged""\)\);\s*return;\s*\}", body);
        }
        Assert.Contains("HideBusyOverlay(busy)", MethodBody(drop, "RunBusyAsync"));
    }

    [Fact]
    public void ADropCountsEveryFileItLeavesOutAndNamesAFailedRepair()
    {
        string append = MethodBody(ShellSource("DragDrop.cs"), "AppendFilesToCurrentDoc");

        Assert.Contains("Str_Drop_RepairFailed", append);
        Assert.Contains("Str_Drop_MergeCanceled", append);
        Assert.Contains("ImportNoPagesException", append);
        Assert.Contains("Str_Drop_SkippedFiles", append);
        // The empty catches that let a file vanish are gone.
        Assert.DoesNotMatch(@"catch \{ importPath = null; \}", append);
        Assert.DoesNotMatch(@"catch \{ /\* skip anything still unreadable after repair \*/ \}", append);
    }

    [Fact]
    public void AFolderCombineReportsTheFilesItLeftOut()
    {
        string zip = ShellSource("ImportAndZip.cs");
        string merged = MethodBody(zip, "OpenMerged");

        Assert.Contains("MergeReadableFiles(files, out", zip);
        Assert.Contains("Str_Drop_SkippedFiles", merged);
        Assert.Contains("found.Count - ", merged);
    }

    // ---- fixtures ----

    private static string ShellSource(string name) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", name));

    // The text of a method from its signature to the closing brace at the same indent.
    private static string MethodBody(string source, string name)
    {
        Match signature = Regex.Match(source, @"^( +)(?:private|internal|public)[^\n]*\b" + Regex.Escape(name) + @"(?:<[^>]*>)?\(", RegexOptions.Multiline);
        Assert.True(signature.Success, name + " was not found.");
        string indent = signature.Groups[1].Value;
        int end = source.IndexOf("\n" + indent + "}", signature.Index, StringComparison.Ordinal);
        Assert.True(end > 0, name + " has no closing brace.");
        return source[signature.Index..end];
    }

    // One page whose content stream claims more bytes than the file holds. The strict parser opens the
    // file and reads its page tree without looking at the stream, and only a page copy resolves it.
    private static byte[] LazilyDamaged()
    {
        return Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            "<< /Length 90000 >>\nstream\n0 0 1 rg\nendstream");
    }

    // Pages that carry /StructParents although the catalog has no /StructTreeRoot, as jcpe13896_am.pdf does.
    private static byte[] LeftoverStructureKeys(int pages)
    {
        var objects = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(index => $"{3 + index} 0 R"))}] /Count {pages} >>"
        };
        objects.AddRange(Enumerable.Range(0, pages).Select(index =>
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /StructParents {index} >>"));
        return Pdf([.. objects]);
    }

    private static byte[] Plain() => new PdfDocumentBuilder().AddBlankPage().Build();

    // Object 1 is the catalog; the objects follow in order.
    private static byte[] Pdf(params string[] objects)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xrefOffset = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private static string CorpusFile(params string[] parts) =>
        Path.Combine([@"C:\Users\steve\code\KillerPDF-Corpus\corpus", .. parts]);

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
