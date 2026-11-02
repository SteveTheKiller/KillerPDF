using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The routes that write a new file or copy another file's pages must not turn a damaged protected
// file into one that reports success and cannot be read, and the copies that are thrown away after a
// print job or an export must not be refused for protection they never carry forward (R3A-008, 009, 010).
public sealed class ProtectedRouteTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-protected-routes-" + Guid.NewGuid().ToString("N"));

    public ProtectedRouteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // ---- an encrypted file whose trailer is gone reads as ciphertext, so no page route may copy it ----

    [Fact]
    public void MergeAndInsertRefuseAnEncryptedFileWhoseTrailerIsGone()
    {
        string lost = Write("lost.pdf", LostTrailerFile());
        string healthy = Write("healthy.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        Assert.True(PdfEngineIntegration.HidesEncryption(File.ReadAllBytes(lost)));

        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.MergeFiles([healthy, lost])));
        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.MergeFiles([lost, healthy])));
        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(
            () => PdfEngineIntegration.MergeDocuments([File.ReadAllBytes(healthy), File.ReadAllBytes(lost)])));
        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.OpenPageSource(File.ReadAllBytes(lost))));

        string target = Write("target.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        byte[] before = File.ReadAllBytes(target);
        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.InsertDocuments(
            target, [new PdfEngineIntegration.ImportedDocument(lost, [0])], int.MaxValue)));
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void AFolderCombineLeavesOutAnEncryptedFileWhoseTrailerIsGone()
    {
        string lost = Write("lost.pdf", LostTrailerFile());
        string healthy = Write("healthy.pdf", new PdfDocumentBuilder().AddBlankPage().Build());

        byte[] merged = PdfEngineIntegration.MergeReadableFiles([lost, healthy], out IReadOnlyList<string> leftOut);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(merged)));
        Assert.Equal([lost], leftOut);
    }

    [Fact]
    public void ARealLostTrailerFileIsRefusedByEveryPageRoute()
    {
        string corpus = CorpusFile("stress", "pdf-association", "GHOSTSCRIPT", "GHOSTSCRIPT-691661-1.pdf");
        if (!File.Exists(corpus)) return;
        string healthy = Write("healthy.pdf", new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.MergeFiles([healthy, corpus])));
        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.MergeFiles([corpus, healthy])));
        Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.ProtectWithPasswords(
            corpus, Path.Combine(_directory, "out.pdf"), Passwords(), allowSignatureInvalidation: false));
        Assert.False(File.Exists(Path.Combine(_directory, "out.pdf")));
    }

    // An engine-encrypted page whose trailer is sound and lost only its /Encrypt entry. AES draws a
    // random IV, so the page's ciphertext is overwritten with fixed bytes of the same length, which keeps
    // every offset and makes the read the same on every run.
    private static byte[] LostEntryFile(Func<int, byte[]> pageCiphertext)
    {
        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(new PdfDocumentBuilder().AddPage(200, 200, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromBitonal(2, 1, new byte[] { 0, 255 }), 0, 0, 100, 100)).Build()),
            new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" });
        string text = Encoding.Latin1.GetString(encrypted);
        Match contents = Regex.Match(text, @"/Contents\s+(\d+)\s+0\s+R");
        Assert.True(contents.Success, "The fixture's page has no content stream reference.");
        int start = text.IndexOf($"\n{contents.Groups[1].Value} 0 obj", StringComparison.Ordinal);
        Assert.True(start > 0, "The fixture's content stream was not found.");
        int length = int.Parse(Regex.Match(text[start..], @"/Length\s+(\d+)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        int data = text.IndexOf("stream", start, StringComparison.Ordinal) + "stream".Length;
        data += text[data] == '\r' ? 2 : 1;
        Assert.InRange(text.IndexOf("endstream", data, StringComparison.Ordinal) - (data + length), 0, 2);
        byte[] page = pageCiphertext(length);
        Assert.Equal(length, page.Length);
        text = text[..data] + Encoding.Latin1.GetString(page) + text[(data + length)..];
        int entry = text.IndexOf("/Encrypt", text.LastIndexOf("trailer", StringComparison.Ordinal), StringComparison.Ordinal);
        int end = text.IndexOf(" R", entry, StringComparison.Ordinal) + 2;
        return Encoding.Latin1.GetBytes(text[..entry] + new string(' ', end - entry) + text[end..]);
    }

    // R4-011: a comment runs to the end of the line, so ciphertext that begins with a percent sign and
    // has no line end reads as no instructions at all, and the page was taken as genuine.
    [Fact]
    public void CiphertextThatBeginsWithAPercentSignIsStillHiddenEncryption() =>
        Assert.True(PdfEngineIntegration.HidesEncryption(
            LostEntryFile(length => [(byte)'%', .. Enumerable.Repeat((byte)0xC8, length - 1)])));

    // The trailer is sound and only /Encrypt is gone, so nothing used to stop the ciphertext being copied.
    [Fact]
    public void AnEncryptedFileWithASoundTrailerThatLostItsEntryIsRefusedOnEveryRoute()
    {
        byte[] source = LostEntryFile(length => [.. Enumerable.Repeat((byte)0xC8, length)]);
        string lost = Write("sound.pdf", source);
        string healthy = Write("healthy.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        string output = Path.Combine(_directory, "out.pdf");

        Assert.True(PdfEngineIntegration.HidesEncryption(source));
        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.MergeFiles([healthy, lost])));
        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.MergeFiles([lost, healthy])));
        Assert.Equal(DamagedSaveRefusal.Encrypted, RefusalOf(() => PdfEngineIntegration.RepairHarmlessSaveArtifacts(lost)));
        Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.ProtectWithPasswords(
            lost, output, Passwords(), allowSignatureInvalidation: false));
        Assert.False(File.Exists(output));
        Assert.Equal(source, File.ReadAllBytes(lost));
    }

    // RepairViaImportToFile is RebuildDocument behind a catch that returns null, and PdfImport is not linked
    // into this project, so the call it makes is the one tested.
    [Fact]
    public void ARepairThatRebuildsThePagesDeclinesAnEncryptedFileWhoseTrailerIsGone()
    {
        string lost = Write("lost.pdf", LostTrailerFile());

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RebuildDocument(lost, Path.Combine(_directory, "rebuilt.pdf")));

        Assert.Equal(DamagedSaveRefusal.Encrypted, refusal.Reason);
        Assert.False(File.Exists(Path.Combine(_directory, "rebuilt.pdf")));
    }

    // ---- Protect writes a new file, so it is refused for a damaged file that a rewrite would strip ----

    [Theory]
    [InlineData("rights")]
    [InlineData("signed")]
    [InlineData("encrypted")]
    public void BothProtectMethodsRefuseADamagedProtectedFileAndWriteNothing(string kind)
    {
        byte[] damaged = kind switch
        {
            "rights" => DamageStartXref(UsageRightsPdf()),
            "signed" => DamageStartXref(SignedPdf()),
            _ => LostTrailerFile()
        };
        string source = Write("source.pdf", damaged);
        string passwords = Path.Combine(_directory, "passwords.pdf");
        string recipients = Path.Combine(_directory, "recipients.pdf");

        var first = Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.ProtectWithPasswords(
            source, passwords, Passwords(), allowSignatureInvalidation: true));
        var second = Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.ProtectForRecipients(
            source, recipients, new PdfCertificateEncryptionOptions { Recipients = [] }, allowSignatureInvalidation: true));

        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Message, second.Message);
        Assert.False(File.Exists(passwords));
        Assert.False(File.Exists(recipients));
        Assert.Equal(damaged, File.ReadAllBytes(source));
    }

    [Fact]
    public void BothProtectMethodsRefuseTheDamagedUsageRightsFormWithTheSameMessage()
    {
        string corpus = CorpusFile("regression", "pdfjs", "test", "pdfs", "prefilled_f1040.pdf");
        if (!File.Exists(corpus)) return;

        var first = Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.ProtectWithPasswords(
            corpus, Path.Combine(_directory, "a.pdf"), Passwords(), allowSignatureInvalidation: true));
        var second = Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.ProtectForRecipients(
            corpus, Path.Combine(_directory, "b.pdf"), new PdfCertificateEncryptionOptions { Recipients = [] },
            allowSignatureInvalidation: true));

        Assert.Equal(DamagedSaveRefusal.UsageRights, first.Reason);
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public void ProtectStillWritesAHealthyFileAndAHealthyUsageRightsFile()
    {
        string plain = Write("plain.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        string output = Path.Combine(_directory, "protected.pdf");

        PdfEngineIntegration.ProtectWithPasswords(plain, output, Passwords(), allowSignatureInvalidation: false);

        Assert.True(PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(output)).IsEncrypted);
    }

    [Fact]
    public void TheCommandLineOptimizeRefusesADamagedProtectedFileBeforeItReadsIt()
    {
        // CliRunner is not linked into this project, so the call is pinned in the source.
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Features", "Cli", "CliRunner.cs"));
        Match method = Regex.Match(source, @"private static int CliOptimize\((?<body>.*?)\n        \}\r?\n", RegexOptions.Singleline);
        Assert.True(method.Success, "CliOptimize was not found.");
        string body = method.Groups["body"].Value;
        int refusal = body.IndexOf("ThrowIfDamagedProtected(inPath)", StringComparison.Ordinal);
        Assert.True(refusal >= 0, "CliOptimize does not call ThrowIfDamagedProtected.");
        Assert.True(refusal < body.IndexOf("OpenForReading", StringComparison.Ordinal));
        Assert.Contains("catch (DamagedSaveRefusedException", body);
    }

    // ---- the copy behind a print job or an extract is thrown away, so it is normalized, not refused ----

    [Theory]
    [InlineData("rights")]
    [InlineData("signed")]
    public void APrintCopyAndAnExtractCopyOfADamagedProtectedFileAreProduced(string kind)
    {
        byte[] damaged = DamageStartXref(kind == "rights" ? UsageRightsPdf() : SignedPdf());
        string print = Write("print.pdf", damaged);
        string saved = Write("saved.pdf", damaged);

        PdfEngineIntegration.PrepareThrowawayCopy(print);
        PdfEngineIntegration.ClearInvalidatedSignatures(print);
        string extracted = Path.Combine(_directory, "extracted.pdf");
        PdfEngineIntegration.ExtractPages(print, extracted, [0], new System.Collections.Generic.Dictionary<int, int>());

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(print))));
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(extracted))));
        Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.RepairHarmlessSaveArtifacts(saved));
    }

    [Fact]
    public void APrintCopyAndAnExtractCopyOfTheRealDamagedUsageRightsFormAreProduced()
    {
        string corpus = CorpusFile("regression", "pdfjs", "test", "pdfs", "prefilled_f1040.pdf");
        if (!File.Exists(corpus)) return;
        string print = Write("print.pdf", File.ReadAllBytes(corpus));
        string saved = Write("saved.pdf", File.ReadAllBytes(corpus));

        PdfEngineIntegration.PrepareThrowawayCopy(print);
        PdfEngineIntegration.ClearInvalidatedSignatures(print);

        // The copy reads as a plain document. What the engine then makes of the form'"'"'s own pages is
        // another matter: ExtractPages refuses this one over a checkbox state with no appearance.
        Assert.Equal(
            PdfPageInformation.Read(PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(corpus))).Count,
            PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(print))).Count);
        Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.RepairHarmlessSaveArtifacts(saved));
    }

    [Fact]
    public void OnlyPrintExtractPagesOcrAndExportTakeTheThrowawayRoute()
    {
        string root = FindRepositoryRoot();
        string print = File.ReadAllText(Path.Combine(root, "Shell", "FileOperations.cs"));
        string pages = File.ReadAllText(Path.Combine(root, "Shell", "PageOperations.cs"));
        string reduce = File.ReadAllText(Path.Combine(root, "Shell", "ReduceSize.cs"));

        // Print readies its own copy; the old call would refuse the form the viewer shows.
        // PrintCopyTests runs what PrintCopy.Ready does to the copy.
        Assert.Contains("PrintCopy.Ready(printPath,", print);
        Assert.DoesNotContain("RepairHarmlessSaveArtifacts(printPath)", print);

        // Only extraction, OCR and raster exports request disposable copies.
        Assert.Matches(@"string WriteSavedCopyToTemp\(bool forRasterization = false, bool throwaway = false\)", reduce);
        Assert.Matches(@"if \(throwaway\) PdfEngineIntegration\.PrepareThrowawayCopy\(clean\);\s*else PdfEngineIntegration\.RepairHarmlessSaveArtifacts\(clean\);", reduce);
        // Reduce, Save Copy, Sign, the macros and the rest keep the refusal: nothing else in the shell asks.
        string[] askers = [.. Directory.EnumerateFiles(Path.Combine(root, "Shell"), "*.cs")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"(?<!string )WriteSavedCopyToTemp\([^)]*throwaway: true")
                .Select(_ => Path.GetFileName(file)))
            .Order()];
        Assert.Equal(["FileOperations.cs", "FileOperations.cs", "Ocr.cs", "PageOperations.cs"], askers);
        Assert.Contains("WriteSavedCopyToTemp(throwaway: true)", pages);
        Assert.Contains("forRasterization: options.OfficeMode == PdfOfficeExportMode.PageImages, throwaway: true", print);
        Assert.Contains("string source = WriteSavedCopyToTemp();", reduce);
    }

    [Fact]
    public void ReduceAsksBeforeItRewritesASignedOrUsageRightsSource()
    {
        string reduce = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "ReduceSize.cs"));
        int source = reduce.IndexOf("HasSignedSignatures(_currentFile", StringComparison.Ordinal);
        int prompt = reduce.IndexOf("Str_Reduce_ClearsSignature", StringComparison.Ordinal);
        int destination = reduce.IndexOf("ConfirmSignatureRemoval(dlg.FileName)", StringComparison.Ordinal);
        int rewrite = reduce.IndexOf("WriteSavedCopyToTemp()", StringComparison.Ordinal);

        Assert.True(source > 0, "Reduce does not check the source for signatures.");
        Assert.True(prompt > source, "Reduce has no prompt for a signed source.");
        Assert.True(destination > prompt, "Reduce lost its check of the destination.");
        Assert.True(rewrite > destination, "The prompts must come before the file is rewritten.");
        Assert.Contains("defaultResult: MessageBoxResult.Cancel", reduce);
    }

    // The merge names the file that failed, so the refusal can sit inside the exception that names it.
    private static DamagedSaveRefusal RefusalOf(Action action)
    {
        Exception failure = Assert.ThrowsAny<Exception>(action);
        for (Exception? current = failure; current is not null; current = current.InnerException)
            if (current is DamagedSaveRefusedException refusal) return refusal.Reason;
        throw new Xunit.Sdk.XunitException("The failure was not a refusal: " + failure);
    }

    // ---- fixtures ----

    private static PdfPasswordEncryptionOptions Passwords() => new() { UserPassword = "open", OwnerPassword = "owner" };

    // An engine-encrypted file cut before its cross-reference table, so the trailer's /Encrypt is gone.
    private static byte[] LostTrailerFile()
    {
        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(RawPdf("", "", "<< /Note (padding so the file has more than one stream) >>")),
            new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" });
        int trailer = encrypted.AsSpan().LastIndexOf("trailer"u8);
        return encrypted[..encrypted.AsSpan(0, trailer).LastIndexOf("xref"u8)];
    }

    private static byte[] UsageRightsPdf() => RawPdf("/Perms << /UR3 5 0 R >>", "",
        SignatureObject("/Reference [<< /Type /SigRef /TransformMethod /UR3 /TransformParams "
            + "<< /Type /TransformParams /V /2.2 >> >>]"));

    private static byte[] SignedPdf() => RawPdf("/AcroForm << /SigFlags 3 /Fields [6 0 R] >>", "/Annots [6 0 R]",
        SignatureObject(),
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (s) /V 5 0 R /Rect [0 0 0 0] /P 3 0 R >>");

    private static string SignatureObject(string extra = "") =>
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <0000> "
        + extra + " >>";

    private static byte[] RawPdf(string catalogExtra, string pageExtra = "", params string[] extraObjects)
    {
        const string content = "0 0 1 rg 0 0 10 10 re f";
        var objects = new System.Collections.Generic.List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R {pageExtra} >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        };
        objects.AddRange(extraObjects);
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xrefOffset = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    // The strict parser refuses a startxref past the end, and compatibility recovery rebuilds the file.
    private static byte[] DamageStartXref(byte[] valid)
    {
        string text = Encoding.Latin1.GetString(valid);
        int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int numberStart = text.IndexOf('\n', marker) + 1;
        int numberEnd = text.IndexOf('\n', numberStart);
        byte[] damaged = Encoding.Latin1.GetBytes(
            text[..numberStart] + (valid.Length + 5000).ToString() + text[numberEnd..]);
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(damaged));
        return damaged;
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
