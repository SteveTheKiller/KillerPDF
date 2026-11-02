using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The guard in front of a damaged file's rewrite decides what a save may destroy, so it has to hold
// when the signature reader gives up, and it must not call a readable file protected.
public sealed class DamagedSaveGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-damaged-guard-" + Guid.NewGuid().ToString("N"));

    public DamagedSaveGuardTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // A field listed twice and a dangling field both make the signature reader throw. The refusal
    // has to come out as this exception type, because IsXRefException turns any other error into
    // the repair fallbacks, and those drop the signature.
    [Theory]
    [InlineData("[6 0 R 6 0 R]")]
    [InlineData("[6 0 R 99 0 R]")]
    public void ASignedFileTheReaderCannotWalkIsRefusedBeforeAnyRewrite(string fields)
    {
        byte[] damaged = DamageStartXref(SignedPdf(fields));
        string path = Write("signed.pdf", damaged);
        bool rewritten = false, rebuilt = false;

        var refusal = Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.TryNormalizeDamagedCopy(
            path, damaged, _ => { rewritten = true; return damaged; }, _ => rebuilt = true));

        Assert.Equal(DamagedSaveRefusal.Signed, refusal.Reason);
        Assert.False(rewritten);
        Assert.False(rebuilt);
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));
    }

    // With no /ByteRange in the file, these are the only signs of a signature once the reader fails.
    [Theory]
    [InlineData("/SigFlags 3")]
    [InlineData("/Note /Adobe.PPKLite")]
    [InlineData("/Note /adbe.pkcs7.detached")]
    [InlineData("/Note /ETSI.CAdES.detached")]
    public void ANamedSignatureCountsWhenTheReaderFails(string evidence)
    {
        byte[] damaged = DamageStartXref(RawPdf($"/AcroForm << {evidence} /Fields [6 0 R 6 0 R] >>", "/Annots [6 0 R]",
            "null", "<< /Type /Annot /Subtype /Widget /FT /Tx /T (a) /Rect [0 0 0 0] /P 3 0 R >>"));
        string path = Write("named.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));

        Assert.Equal(DamagedSaveRefusal.Signed, refusal.Reason);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    // No signature bytes means nothing to protect, so the reader's failure must not stop the save.
    [Theory]
    [InlineData("[6 0 R 6 0 R]")]
    [InlineData("[6 0 R 99 0 R]")]
    public void TheGuardLetsAnUnsignedFileWithABrokenFormThrough(string fields)
    {
        byte[] damaged = DamageStartXref(UnsignedFormPdf(fields));
        string path = Write("form.pdf", damaged);
        byte[] rewrittenBytes = RawPdf("");

        bool normalized = PdfEngineIntegration.TryNormalizeDamagedCopy(
            path, damaged, _ => rewrittenBytes, _ => Assert.Fail("The rewrite did not fail."));

        Assert.True(normalized);
        Assert.Equal(rewrittenBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void AnUnsignedFileWithABrokenFormSavesThroughTheRebuild()
    {
        byte[] damaged = DamageStartXref(RawPdf("/AcroForm << /Fields [5 0 R] >>", "",
            "<< /FT /Sig /T (empty) /V 3 >>"));
        string path = Write("form.pdf", damaged);

        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))));
    }

    // A field listed twice in /Fields is damage, and the viewer shows it once. The save keeps the
    // field and what it holds, listed once, and the file reopens with its form intact.
    [Fact]
    public void ADamagedUnsignedFileWithAFieldListedTwiceSavesAndKeepsTheFieldAndItsValue()
    {
        byte[] damaged = DamageStartXref(RawPdf("/AcroForm << /Fields [6 0 R 6 0 R] >>", "/Annots [6 0 R]", "null",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (a) /V (kept) /Rect [0 0 50 20] /P 3 0 R >>"));
        string path = Write("twice.pdf", damaged);

        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);

        PdfDocument saved = PdfDocument.Open(File.ReadAllBytes(path));
        Assert.Single(PdfPageInformation.Read(saved));
        Assert.Equal("kept", PdfFormCalculation.ReadValues(saved)["a"]);
        Assert.Equal("a", Assert.Single(PdfFormWidgetReader.ReadPage(saved, 0)).FieldName);
        PdfEngineIntegration.ClearInvalidatedSignatures(path);
        PdfEngineIntegration.StripLinkAppearances(path);
        string copy = Path.Combine(_directory, "copy.pdf");
        Assert.True(PdfEngineIntegration.TryCreateZeroRotationCopy(Write("twice-again.pdf", damaged), copy));
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(copy))));
    }

    // ConfirmSignatureRemoval reads a throw as not signed, so a file the reader cannot finish has to
    // answer here. One bad text value in a signature says nothing about the signature.
    [Theory]
    [InlineData("/M (D:20240101120000+01)")]
    [InlineData("/Name (\\376\\377\\330)")]
    public void HasSignedSignaturesAnswersWhenTheReaderFailsOnAHealthyFile(string signatureExtra)
    {
        string path = Write("signed.pdf", SignedPdf("[6 0 R]", signatureExtra));

        Assert.True(PdfEngineIntegration.HasSignedSignatures(path));
    }

    [Fact]
    public void HasSignedSignaturesSeesASignatureWithNoType()
    {
        string path = Write("signed.pdf", Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(SignedPdf("[6 0 R]"))
            .Replace("<< /Type /Sig /Filter", "<<             /Filter", StringComparison.Ordinal)));

        Assert.True(PdfEngineIntegration.HasSignedSignatures(path));
    }

    [Fact]
    public void HasSignedSignaturesAnswersOnARealSignedFileTheReaderRefuses()
    {
        string path = CorpusFile("stress", "pdf-association", "poppler-gitlab", "poppler-782-0.pdf");
        if (!File.Exists(path)) return;

        Assert.True(PdfEngineIntegration.HasSignedSignatures(path));
    }

    // The public-key encryption handler names adbe.pkcs7.s3, s4 or s5, which is not a signature.
    [Fact]
    public void AnEncryptionHandlerFilterIsNotASignature()
    {
        string path = Write("recipients.pdf", RawPdf(
            "/AcroForm << /Note /adbe.pkcs7.s5 /Fields [6 0 R 6 0 R] >>", "/Annots [6 0 R]",
            "null", "<< /Type /Annot /Subtype /Widget /FT /Tx /T (a) /Rect [0 0 0 0] /P 3 0 R >>"));

        Assert.False(PdfEngineIntegration.HasSignedSignatures(path));
    }

    [Fact]
    public void HasSignedSignaturesStaysFalseForABrokenFormWithNoSignatureBytes()
    {
        string path = Write("form.pdf", UnsignedFormPdf("[6 0 R 6 0 R]"));

        Assert.False(PdfEngineIntegration.HasSignedSignatures(path));
    }

    // A stale encryption dictionary, as a decryption tool leaves behind, with page text anyone can
    // read. Refusing it would call an unprotected file password protected.
    [Fact]
    public void ALeftoverEncryptionDictionaryOverReadablePagesIsNotProtection()
    {
        byte[] damaged = DamageStartXref(RawPdf("", "", LeftoverEncryptionObject));
        string path = Write("cleaned.pdf", damaged);

        Assert.False(PdfEngineIntegration.HidesEncryption(damaged));
        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))));
    }

    [Fact]
    public void RemovingEncryptionFromALeftoverDictionaryRewritesTheFile()
    {
        string source = Write("cleaned.pdf", DamageStartXref(RawPdf("", "", LeftoverEncryptionObject)));
        string destination = Path.Combine(_directory, "plain.pdf");

        PdfEngineIntegration.RemoveEncryption(source, destination, string.Empty);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(destination))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReadableInlineImageKeepsStructuralRecoveryWithoutContentRecovery(bool plottingAfterImage)
    {
        const string oldContent = "0 0 1 rg 0 0 10 10 re f";
        string content = "q BI /W 1 /H 1 /BPC 8 /CS /Custom ID %\0\u00ff EI Q"
            + (plottingAfterImage ? " 0.00-96880406 0 m 1 1 l S" : "");
        string text = Encoding.Latin1.GetString(RawPdf("",
            "/Resources << /ColorSpace << /Custom /DeviceRGB >> >>", LeftoverEncryptionObject));
        text = text.Replace(oldContent, content, StringComparison.Ordinal)
            .Replace($"/Length {oldContent.Length}", $"/Length {content.Length}", StringComparison.Ordinal)
            .Replace("/Count 1", "/Count 2", StringComparison.Ordinal);
        byte[] source = DamageStartXref(Encoding.Latin1.GetBytes(text));
        string path = Write("readable-inline-image.pdf", source);

        Assert.False(PdfEngineIntegration.HidesEncryption(source));
        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);
        Assert.Single(new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path))).Read(0).Images);
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("utf8")]
    [InlineData("latin1")]
    [InlineData("latin1-long")]
    [InlineData("binary-string")]
    [InlineData("escaped-string")]
    [InlineData("hex-string")]
    [InlineData("content-number")]
    public void AuthoredCommentsAndBinaryStringOperandsRemainReadable(string kind)
    {
        const string oldContent = "0 0 1 rg 0 0 10 10 re f";
        string content = kind switch
        {
            "ascii" => "f % ordinary authored comment\n",
            "utf8" => "f % " + Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("\u4e2d\u6587\u6ce8\u91ca\u4fdd\u7559\u53ef\u8bfb\u6587\u672c")) + "\n",
            "latin1" => "f % caf\u00e9\n",
            "latin1-long" => "f % " + new string('\u00e9', 32) + "\n",
            "hex-string" => "BT /F1 12 Tf <2504FF> Tj ET",
            "content-number" => "0.00-96880406 0 m 1 1 l S",
            "escaped-string" => @"BT /F1 12 Tf (a\)b%\(" + "\u0004\u00ff) Tj ET",
            _ => "BT /F1 12 Tf (100%\u0004\u00ff) Tj ET"
        };
        string text = Encoding.Latin1.GetString(RawPdf("", "", LeftoverEncryptionObject));
        text = text.Replace(oldContent, content, StringComparison.Ordinal)
            .Replace($"/Length {oldContent.Length}", $"/Length {content.Length}", StringComparison.Ordinal);
        byte[] source = DamageStartXref(Encoding.Latin1.GetBytes(text));
        string path = Write("readable-content.pdf", source);

        Assert.False(PdfEngineIntegration.HidesEncryption(source));
        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);
        Assert.NotEmpty(new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path))).ReadInstructions(0));
    }

    [Theory]
    [InlineData("GHOSTSCRIPT", "GHOSTSCRIPT-697944-0.pdf")]
    [InlineData("pdf.js", "pdf.js-11231-0.pdf")]
    [InlineData("pdf.js", "pdf.js-7303-0.pdf")]
    [InlineData("poppler", "poppler-22485-0.pdf")]
    public void ARealFileThatReadsAsPlainTextPassesTheGuard(string folder, string name)
    {
        string corpus = CorpusFile("stress", "pdf-association", folder, name);
        if (!File.Exists(corpus)) return;
        byte[] source = File.ReadAllBytes(corpus);
        string path = Write(name, source);

        Assert.False(PdfEngineIntegration.HidesEncryption(source));
        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);
        Assert.NotEmpty(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))));
    }

    // Unfiltered ciphertext decodes without complaint, so only what it reads as can give it away.
    [Theory]
    [InlineData("/Filter /Standard")]
    [InlineData("/F#69lter /Standard")]
    [InlineData("/Filter % long comment between the key and security handler\n /Sta#6Edard")]
    public void CiphertextPagesBehindALostTrailerStayRefused(string marker)
    {
        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(RawPdf("", "", "<< /Note (padding so the file has more than one stream) >>")),
            new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" });
        encrypted = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(encrypted)
            .Replace("/Filter /Standard", marker, StringComparison.Ordinal));
        int trailer = encrypted.AsSpan().LastIndexOf("trailer"u8);
        byte[] damaged = encrypted[..encrypted.AsSpan(0, trailer).LastIndexOf("xref"u8)];
        string path = Write("protected.pdf", damaged);

        Assert.True(PdfEngineIntegration.HidesEncryption(damaged));
        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));
        Assert.Equal(DamagedSaveRefusal.Encrypted, refusal.Reason);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void CiphertextThatRecoversToAKnownOperatorStaysRefused()
    {
        byte[] damaged = Convert.FromBase64String(
            "JVBERi0xLjcKJeLjz9MKMSAwIG9iago8PCAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0NvdW50IDEgL0tpZHMgWzMgMCBSXSAvVHlwZSAvUGFnZXMgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL0NvbnRlbnRzIDQgMCBSIC9NZWRpYUJveCBbMCAwIDEwMCAxMDBdIC9QYXJlbnQgMiAwIFIgL1R5cGUgL1BhZ2UgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA0OCA+PgpzdHJlYW0KaCn9C2ZxFSoj1jXlK3hZlxlps/oSsroLaxCy8r3k901tNF0X1/Fz4L1y3AvSzmemCmVuZHN0cmVhbQplbmRvYmoKNSAwIG9iago8PCAvTm90ZSA8QkExQzE2RjZEQjI3OThBMkYyNEMyMjcwOThGMTFERTlGRDI0MDQ1NUVENEVGQ0E0MkZFQ0EyRTFDQkM1NzlFOEY1NEFGQ0I4ODk5RDg5MDE1QjM3ODdDRjA4RkJFRTk1NTk1OTg3QjMxRTY0OTc5RkI1RDg2NjAwOEU0NUYxQkM+ID4+CmVuZG9iago2IDAgb2JqCjw8IC9DRiA8PCAvU3RkQ0YgPDwgL0F1dGhFdmVudCAvRG9jT3BlbiAvQ0ZNIC9BRVNWMyAvTGVuZ3RoIDMyID4+ID4+IC9FRkYgL1N0ZENGIC9FbmNyeXB0TWV0YWRhdGEgdHJ1ZSAvRiM2OWx0ZXIgL1N0YW5kYXJkIC9MZW5ndGggMjU2IC9PIDwwOUY2OUI1QUU4NjE5QUZDMDhBMzM4QjJEMjc2QUI4MTIyRTk3RkRBOTI5Q0M2RjI1OEYxNTE1Qjc5N0JBRDRGQUZDMUQ0RDlEN0Q1RjE3RTJGOTAyOTA0NzU1OTEyMDM+IC9PRSA8NDQ4MzgyM0RDRkU5RTgxNUQ5QzIyNUQwRDNDQjIyRTc2Q0MwQkJGMjRFMkRDQjFGREUzQkQwM0E4RkU0NjA5Qz4gL1AgLTQgL1Blcm1zIDxFOEI1QjRCMjFCQzEzOUUyMUVFMkE0MDdGNzJDOEQ5MT4gL1IgNiAvU3RtRiAvU3RkQ0YgL1N0ckYgL1N0ZENGIC9VIDw5QzczRTk4NDBFRjg5Q0E0OTQyNEYyNkUwNEY1RjVGMjk5RUU0QkE0OUY2NDY4NEI5NjZFMDA1QzhFNTU0RjMwNkE2RTEzQTA2N0Q3QUMwNDEzMUM2REJCM0NENTQxNEY+IC9VRSA8RDVGQjlDQjk3Q0MwNzNBNjRFMjE2RTY5M0I4MjlEMkVBQ0YyQzA1NEZENDQ4RTA3QTA5RDZBNTE0NzQ2NUNDMD4gL1YgNSA+PgplbmRvYmoK");
        string path = Write("known-operator-ciphertext.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));
        Assert.Equal(DamagedSaveRefusal.Encrypted, refusal.Reason);
        Assert.True(PdfEngineIntegration.HidesEncryption(damaged));
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void CiphertextBehindAnOperatorAndBinaryCommentStaysRefused()
    {
        byte[] damaged = Convert.FromBase64String(
            "JVBERi0xLjcKJeLjz9MKMSAwIG9iago8PCAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0NvdW50IDEgL0tpZHMgWzMgMCBSXSAvVHlwZSAvUGFnZXMgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL0NvbnRlbnRzIDQgMCBSIC9NZWRpYUJveCBbMCAwIDEwMCAxMDBdIC9QYXJlbnQgMiAwIFIgL1R5cGUgL1BhZ2UgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA0OCA+PgpzdHJlYW0KZiXMIW5kMAQxPE7tAPj+kwh+uf6BENqmKTjqnvVgOPzhfYejBJuCKhr6haofI8DaCmVuZHN0cmVhbQplbmRvYmoKNSAwIG9iago8PCAvTm90ZSA8MDE3QTI4QUI4MDFGMUY0MTlBRjBGNUJEMUFDNTRDOUUwNTQ2MTcwNDZBMEY4RTI4N0Y5NjNGQkQ2NDRGODVFNzJGRkI4M0ExMEUxRjA1MEI3NzczMDMwNzdFNzEyQUI3MDRDNUVENTE3OEIxM0I5QUVGNzBGODhDQjE3QkNGQ0Q+ID4+CmVuZG9iago2IDAgb2JqCjw8IC9DRiA8PCAvU3RkQ0YgPDwgL0F1dGhFdmVudCAvRG9jT3BlbiAvQ0ZNIC9BRVNWMyAvTGVuZ3RoIDMyID4+ID4+IC9FRkYgL1N0ZENGIC9FbmNyeXB0TWV0YWRhdGEgdHJ1ZSAvRiM2OWx0ZXIgL1N0YW5kYXJkIC9MZW5ndGggMjU2IC9PIDw2QzA1MDVFRkYyRTEwMjc3QkNERjEyNzUyNzIzNEVCNjg5Njk0NDBFMkE2Qjg5MDlCQUE4NUEwMDA3RTExOEY3MTlGQjQxNjhCMzgyRUMzMkFGOUVEMzc0QzAxMTJCQzE+IC9PRSA8NjdGNDkyMDE0NDEwM0U3NzdDM0UyNDE0MDlCQjk5QTZCNUUzRjZGMEU3MkY4QzE2QTIyMDVDNTAxNjlBRDE5OT4gL1AgLTQgL1Blcm1zIDwxQzA2MDg0ODc2NDI2NEE2NjdDRjI3OEZGMkNEMzhCNz4gL1IgNiAvU3RtRiAvU3RkQ0YgL1N0ckYgL1N0ZENGIC9VIDwyRTE2RjFFQkY2OTg1RkNDQjhCRjUzQjJGMjNERTEzQUY0QUFCQzM4NDg5QjI3MEE2Mzg3MzcwNjM3NjEzRDZBNzZEOTc2MjQ1MjQxRjAwNTk3QzYxN0QwQzdGNDczNjk+IC9VRSA8MDY5REM5M0QzRTFGRThCRTg1QUFFODVDQTg0QzIwRDNDNDcwMUJGMjFFNUZGRjhDQkZGOUExQkY2REQ3QkY3ND4gL1YgNSA+PgplbmRvYmoK");
        string path = Write("binary-comment-ciphertext.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));
        Assert.Equal(DamagedSaveRefusal.Encrypted, refusal.Reason);
        Assert.True(PdfEngineIntegration.HidesEncryption(damaged));
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void ARealLostTrailerFileStaysRefused()
    {
        string corpus = CorpusFile("stress", "pdf-association", "GHOSTSCRIPT", "GHOSTSCRIPT-691661-1.pdf");
        if (!File.Exists(corpus)) return;
        byte[] source = File.ReadAllBytes(corpus);
        string path = Write("lost.pdf", source);

        Assert.True(PdfEngineIntegration.HidesEncryption(source));
        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));
        Assert.Equal(DamagedSaveRefusal.Encrypted, refusal.Reason);
    }

    // Streams and strings are encrypted separately, so readable pages do not clear a file whose
    // strings are still ciphertext. A PDF date is plain text, which makes it the one to check.
    [Fact]
    public void ReadablePagesOverEncryptedStringsStayRefused()
    {
        byte[] damaged = DamageStartXref(WithInfo(RawPdf("", "", LeftoverEncryptionObject,
            "<< /CreationDate <9A33F1C2087D54E6B1> >>")));

        Assert.True(PdfEngineIntegration.HidesEncryption(damaged));
    }

    [Fact]
    public void ReadablePagesAndADateMarkALeftoverDictionary()
    {
        byte[] damaged = DamageStartXref(WithInfo(RawPdf("", "", LeftoverEncryptionObject,
            "<< /CreationDate (D:20150226160635-05'00') >>")));

        Assert.False(PdfEngineIntegration.HidesEncryption(damaged));
    }

    private static byte[] WithInfo(byte[] pdf) => Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(pdf)
        .Replace("/Root 1 0 R >>", "/Root 1 0 R /Info 6 0 R >>", StringComparison.Ordinal));

    // A page tree whose /Count is wrong survives the rewrite, and the save's own repair step reads
    // that tree strictly, so a rewrite only counts if the output can be walked.
    [Fact]
    public void ADamagedFileWithAWrongPageCountStillSaves()
    {
        byte[] damaged = DamageStartXref(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(RawPdf(""))
            .Replace("/Count 1", "/Count 7", StringComparison.Ordinal)));
        string path = Write("count.pdf", damaged);

        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))));
    }

    // The check on a rewrite is only as strict as the repair step after it: a page with no
    // /MediaBox is a problem for the reader of the page, not for this save.
    [Fact]
    public void ADamagedFileWithAPageMissingItsMediaBoxStillSaves()
    {
        byte[] damaged = DamageStartXref(Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(RawPdf(""))
            .Replace("/MediaBox [0 0 100 100]", "", StringComparison.Ordinal)));
        string path = Write("nobox.pdf", damaged);

        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);

        _ = PdfDocument.Open(File.ReadAllBytes(path));
    }

    // With nothing on any page to test, the bytes are all there is, and they say encrypted.
    [Fact]
    public void AnEncryptionDictionaryOverEmptyPagesStaysRefused()
    {
        byte[] plain = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(RawPdf("", "", LeftoverEncryptionObject))
            .Replace(" /Contents 4 0 R", "                ", StringComparison.Ordinal));
        byte[] damaged = DamageStartXref(plain);

        Assert.True(PdfEngineIntegration.HidesEncryption(damaged));
    }

    // Reader usage rights are a signature over the document, and a rewrite voids them the same way,
    // so the prompt that covers a signed file covers these too.
    [Fact]
    public void HasSignedSignaturesCountsReaderUsageRightsUntilTheyAreCleared()
    {
        string path = Write("rights.pdf", UsageRightsPdf());

        Assert.True(PdfEngineIntegration.HasSignedSignatures(path));

        PdfEngineIntegration.ClearInvalidatedSignatures(path);

        Assert.False(PdfEngineIntegration.HasSignedSignatures(path));
    }

    [Fact]
    public void HasSignedSignaturesIgnoresAnEmptyPermissionsDictionary()
    {
        string path = Write("empty.pdf", RawPdf("/Perms << >>"));

        Assert.False(PdfEngineIntegration.HasSignedSignatures(path));
    }

    [Fact]
    public void ThrowIfDamagedProtectedRefusesARealDamagedFormWithUsageRights()
    {
        string path = CorpusFile("regression", "pdfjs", "test", "pdfs", "prefilled_f1040.pdf");
        if (!File.Exists(path)) return;

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.ThrowIfDamagedProtected(path));

        Assert.Equal(DamagedSaveRefusal.UsageRights, refusal.Reason);
    }

    [Theory]
    [InlineData("signed", "Signed")]
    [InlineData("rights", "UsageRights")]
    [InlineData("encrypted", "Encrypted")]
    public void ThrowIfDamagedProtectedRefusesEveryKindOfProtectionAndChangesNothing(string kind, string expected)
    {
        byte[] damaged = kind switch
        {
            "signed" => DamageStartXref(SignedPdf("[6 0 R]")),
            "rights" => DamageStartXref(UsageRightsPdf()),
            _ => EncryptedWithItsTrailerKeywordDamaged()
        };
        string path = Write("protected.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.ThrowIfDamagedProtected(path));

        Assert.Equal(Enum.Parse<DamagedSaveRefusal>(expected), refusal.Reason);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    // Only damage earns the refusal: a healthy protected file has the prompt, and a file nothing can
    // read is reported by the caller's own open.
    [Fact]
    public void ThrowIfDamagedProtectedLeavesEverythingElseToTheCaller()
    {
        PdfEngineIntegration.ThrowIfDamagedProtected(Write("healthy-rights.pdf", UsageRightsPdf()));
        PdfEngineIntegration.ThrowIfDamagedProtected(Write("healthy-signed.pdf", SignedPdf("[6 0 R]")));
        PdfEngineIntegration.ThrowIfDamagedProtected(Write("plain.pdf", DamageStartXref(RawPdf(""))));
        PdfEngineIntegration.ThrowIfDamagedProtected(Write("noise.pdf", Encoding.ASCII.GetBytes("not a pdf")));
    }

    // The copy behind a print job or an export is deleted afterward, so it can give up what a saved
    // copy could not, and the save pipeline itself must keep refusing.
    [Theory]
    [InlineData("signed")]
    [InlineData("rights")]
    public void PrepareThrowawayCopyLetsADamagedProtectedCopyThrough(string kind)
    {
        byte[] damaged = DamageStartXref(kind == "signed" ? SignedPdf("[6 0 R]") : UsageRightsPdf());
        string path = Write("copy.pdf", damaged);
        string saved = Write("saved.pdf", damaged);

        PdfEngineIntegration.PrepareThrowawayCopy(path);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))));
        PdfEngineIntegration.ClearInvalidatedSignatures(path);
        Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.RepairHarmlessSaveArtifacts(saved));
    }

    [Fact]
    public void PrepareThrowawayCopyReadsARealDamagedFormWithUsageRights()
    {
        string corpus = CorpusFile("regression", "pdfjs", "test", "pdfs", "prefilled_f1040.pdf");
        if (!File.Exists(corpus)) return;
        string path = Write("form.pdf", File.ReadAllBytes(corpus));
        int pages = PdfPageInformation.Read(PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(corpus))).Count;

        PdfEngineIntegration.PrepareThrowawayCopy(path);

        Assert.Equal(pages, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))).Count);
        PdfEngineIntegration.ClearInvalidatedSignatures(path);
    }

    [Fact]
    public void PrepareThrowawayCopyStillRefusesAnEncryptedFile()
    {
        byte[] damaged = EncryptedWithItsTrailerKeywordDamaged();
        string path = Write("protected.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.PrepareThrowawayCopy(path));

        Assert.Equal(DamagedSaveRefusal.Encrypted, refusal.Reason);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void PrepareThrowawayCopyLeavesAHealthyFileAlone()
    {
        byte[] healthy = RawPdf("");
        string path = Write("healthy.pdf", healthy);

        PdfEngineIntegration.PrepareThrowawayCopy(path);

        Assert.Equal(healthy, File.ReadAllBytes(path));
    }

    private static byte[] UsageRightsPdf() => RawPdf("/Perms << /UR3 5 0 R >>", "",
        SignatureObject("/Reference [<< /Type /SigRef /TransformMethod /UR3 /TransformParams "
            + "<< /Type /TransformParams /V /2.2 >> >>]"));

    private static byte[] EncryptedWithItsTrailerKeywordDamaged()
    {
        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(RawPdf("")),
            new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" });
        byte[] damaged = (byte[])encrypted.Clone();
        damaged[encrypted.AsSpan().LastIndexOf("trailer"u8) + 2] = (byte)'@';
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(damaged));
        return damaged;
    }

    private const string LeftoverEncryptionObject =
        "<< /Filter /Standard /V 1 /R 2 /P -44 /O <00> /U <00> >>";

    private static string CorpusFile(params string[] parts) =>
        Path.Combine([@"C:\Users\steve\code\KillerPDF-Corpus\corpus", .. parts]);

    private static byte[] UnsignedFormPdf(string fields) => RawPdf($"/AcroForm << /Fields {fields} >>", "/Annots [6 0 R]",
        "null", "<< /Type /Annot /Subtype /Widget /FT /Tx /T (a) /Rect [0 0 0 0] /P 3 0 R >>");

    private static byte[] SignedPdf(string fields, string signatureExtra = "") => RawPdf(
        $"/AcroForm << /SigFlags 3 /Fields {fields} >>", "/Annots [6 0 R]",
        SignatureObject(signatureExtra),
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (s) /V 5 0 R /Rect [0 0 0 0] /P 3 0 R >>");

    private static string SignatureObject(string extra = "") =>
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <0000> "
        + extra + " >>";

    // A one-page file with a content stream, then the given objects numbered from 5.
    private static byte[] RawPdf(string catalogExtra, string pageExtra = "", params string[] extraObjects)
    {
        const string content = "0 0 1 rg 0 0 10 10 re f";
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R {pageExtra} >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        };
        objects.AddRange(extraObjects);
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
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

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
