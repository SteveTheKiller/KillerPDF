using System.IO;
using KillerPDF.Features;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

// The command line answers from the same function as the app, through the one seam the dispatcher
// asks. This runs in this process, with no window and no KillerPDF.exe; CliExitCodeTests runs the same
// verbs through the real exe on a lab machine.
public sealed class CliRestrictionTests : IDisposable
{
    private const string Owner = "owner-password";
    private const string Reader = "reader";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-cli-limits-" + Guid.NewGuid().ToString("N"));

    public CliRestrictionTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string InFolder(string name) => Path.Combine(_folder, name);

    private string Pdf(string user = "", bool print = true, bool copy = true, bool modify = true)
    {
        string path = InFolder(Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = user,
                OwnerPassword = Owner,
                AllowLowQualityPrinting = print,
                AllowHighQualityPrinting = print,
                AllowContentCopying = copy,
                AllowDocumentModification = modify
            })
            .AddPage(200, 200, "q 0 0 50 50 re f Q"u8.ToArray())
            .AddTextField(0, "name", 10, 100, 80, 20, "value", 10)
            .Build());
        return path;
    }

    private string? Verb(string command, string pdf, string? password = null, params string[] more)
    {
        var options = new Dictionary<string, string>();
        if (password is not null) options["--password"] = password;
        for (int index = 0; index + 1 < more.Length; index += 2) options[more[index]] = more[index + 1];
        return CliProtectionGate.VerbRefusal(command, [pdf, InFolder("out")], options);
    }

    // R3-007: decrypt is the command line's Remove Password.
    [Fact]
    public void DecryptOfAFileWithALimitTakesTheOwnerPassword()
    {
        string pdf = Pdf(print: false);

        Assert.NotNull(Verb("--decrypt", pdf));
        Assert.Null(Verb("--decrypt", pdf, Owner));
    }

    [Fact]
    public void DecryptOfAFileThatLimitsNothingTakesItsUserPassword() =>
        Assert.Null(Verb("--decrypt", Pdf(user: Reader), Reader));

    [Fact]
    public void AUserPasswordDoesNotDecryptAFileWithALimit() =>
        Assert.NotNull(Verb("--decrypt", Pdf(user: Reader, print: false), Reader));

    [Fact]
    public void AWrongPasswordDecryptsNothing() =>
        Assert.NotNull(Verb("--decrypt", Pdf(user: Reader), "wrong"));

    // R3-007: page images and new PDFs of a print-limited file wait for the owner password.
    [Theory]
    [InlineData("--to-image")]
    [InlineData("--flatten")]
    [InlineData("--extract-pages")]
    [InlineData("--split")]
    [InlineData("--booklet")]
    [InlineData("--nup")]
    [InlineData("--ocr")]
    [InlineData("--straighten")]
    [InlineData("--separations")]
    [InlineData("--data-merge")]
    public void APrintableOutputOfAPrintLimitedFileTakesTheOwnerPassword(string verb)
    {
        string pdf = Pdf(print: false);

        Assert.NotNull(Verb(verb, pdf));
        Assert.Null(Verb(verb, pdf, Owner));
    }

    [Fact]
    public void AMergeAsksOfEveryInput()
    {
        string plain = InFolder("plain.pdf");
        File.WriteAllBytes(plain, new PdfDocumentBuilder().AddBlankPage().Build());
        var options = new Dictionary<string, string>();

        Assert.NotNull(CliProtectionGate.VerbRefusal("--merge", [plain, Pdf(print: false), InFolder("merged.pdf")], options));
        Assert.Null(CliProtectionGate.VerbRefusal("--merge", [plain, plain, InFolder("merged.pdf")], options));
    }

    // The final ruling: pages of a copy-forbidden file stay in it, as the engine's page import says.
    [Theory]
    [InlineData("--to-image")]
    [InlineData("--flatten")]
    [InlineData("--extract-pages")]
    public void APrintableOutputOfACopyForbiddenFileTakesTheOwnerPassword(string verb)
    {
        string pdf = Pdf(copy: false);

        Assert.NotNull(Verb(verb, pdf));
        Assert.Null(Verb(verb, pdf, Owner));
    }

    [Fact]
    public void APrintableOutputOfAFileThatLimitsNothingIsAllowed() =>
        Assert.Null(Verb("--flatten", Pdf(user: Reader), Reader));

    // Text follows the copy bit.
    [Theory]
    [InlineData("--export", ".txt")]
    [InlineData("--export", ".json")]
    [InlineData("--export-form-data", ".xfdf")]
    [InlineData("--scan-to-text", ".pdf")]
    public void TextOfAPrintLimitedFileThatAllowsCopyingGoesAhead(string verb, string extension)
    {
        string pdf = Pdf(print: false);

        Assert.Null(CliProtectionGate.VerbRefusal(verb, [pdf, InFolder("out" + extension)], new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("--export", ".txt")]
    [InlineData("--export-form-data", ".json")]
    [InlineData("--scan-to-text", ".pdf")]
    public void TextOfACopyForbiddenFileTakesTheOwnerPassword(string verb, string extension)
    {
        string pdf = Pdf(copy: false);
        string output = InFolder("out" + extension);

        Assert.NotNull(CliProtectionGate.VerbRefusal(verb, [pdf, output], new Dictionary<string, string>()));
        Assert.Null(CliProtectionGate.VerbRefusal(verb, [pdf, output], new Dictionary<string, string> { ["--password"] = Owner }));
    }

    [Fact]
    public void AnOfficeExportAsPageImagesIsAPrintableOutput()
    {
        string pdf = Pdf(print: false);
        string docx = InFolder("slides.docx");

        Assert.Null(CliProtectionGate.VerbRefusal("--export", [pdf, docx], new Dictionary<string, string>()));
        Assert.NotNull(CliProtectionGate.VerbRefusal("--export", [pdf, docx], new Dictionary<string, string> { ["--layout"] = "images" }));
    }

    // Only writing the attachments out counts; listing them reads.
    [Fact]
    public void AttachmentsFollowTheCopyBitOnlyWhenWrittenOut()
    {
        string pdf = Pdf(copy: false);
        var none = new Dictionary<string, string>();

        Assert.Null(CliProtectionGate.VerbRefusal("--attachments", [pdf], none));
        Assert.NotNull(CliProtectionGate.VerbRefusal("--attachments", [pdf, InFolder("files")], none));
    }

    // R3-008: form data is content leaving the file.
    [Fact]
    public void FormDataOfAnEditForbiddenFileTakesTheOwnerPassword() =>
        Assert.NotNull(CliProtectionGate.VerbRefusal("--export-form-data", [Pdf(modify: false), InFolder("form.json")],
            new Dictionary<string, string>()));

    // A command that keeps the input's encryption is a save, and one that only reports writes nothing.
    [Theory]
    [InlineData("--rotate-pages")]
    [InlineData("--set-metadata")]
    [InlineData("--optimize")]
    [InlineData("--fix-orientation")]
    [InlineData("--document-info")]
    [InlineData("--print-production")]
    [InlineData("--preflight")]
    public void AVerbThatKeepsOrOnlyReadsIsNotAsked(string verb) =>
        Assert.Null(Verb(verb, Pdf(print: false, copy: false)));

    // A new protection in place of the old one takes the old one off first.
    [Fact]
    public void ProtectOfAFileWithALimitTakesTheOwnerPassword()
    {
        string pdf = Pdf(print: false);

        Assert.NotNull(Verb("--protect", pdf));
        Assert.Null(Verb("--protect", pdf, Owner));
    }

    // R3-013: the user password that opened the file is the one the render reads with.
    [Fact]
    public void BatchRenderReadsWithAUserPassword()
    {
        (string? refusal, string? readWith) = CliProtectionGate.BatchRender(Pdf(user: Reader), Reader);

        Assert.Null(refusal);
        Assert.Equal(Reader, readWith);
    }

    [Fact]
    public void BatchRenderRefusesPageImagesOfAPrintLimitedFile() =>
        Assert.NotNull(CliProtectionGate.BatchRender(Pdf(print: false), null).Refusal);

    // R3-018: one password spans a tree, so a file it does not open is read as if none was given.
    [Fact]
    public void ABatchPasswordThatDoesNotOpenAFileLeavesItToItsEmptyPassword()
    {
        (string? refusal, string? readWith) = CliProtectionGate.BatchRender(Pdf(), "another");

        Assert.Null(refusal);
        Assert.Null(readWith);
    }

    [Fact]
    public void ABatchPasswordThatOpensNothingStillSkipsTheFile() =>
        Assert.NotNull(CliProtectionGate.BatchRender(Pdf(user: Reader), "another").Refusal);

    // R3-014: a mistyped switch prints usage and exits 2.
    [Theory]
    [InlineData("--resave")]
    [InlineData("--to-imge")]
    [InlineData("--RESAVE")]
    public void AnUnknownSwitchIsBadUsage(string option) =>
        Assert.True(CliProtectionGate.IsUnknownSwitch([option, "in.pdf", "out.pdf"]));

    [Fact]
    public void ThePlainLaunchesStillReachTheWindow()
    {
        string oddlyNamed = InFolder("--looks-like-a-switch.pdf");
        File.WriteAllBytes(oddlyNamed, [1]);

        Assert.False(CliProtectionGate.IsUnknownSwitch([InFolder("document.pdf")]));
        Assert.False(CliProtectionGate.IsUnknownSwitch(["some-word"]));
        Assert.False(CliProtectionGate.IsUnknownSwitch([oddlyNamed]));
        Assert.False(CliProtectionGate.IsUnknownSwitch(["--lang-file", InFolder("--strings.xaml"), InFolder("document.pdf")]));
        Assert.False(CliProtectionGate.IsUnknownSwitch(["--crash-dialog-preview"]));
        Assert.False(CliProtectionGate.IsUnknownSwitch(["--startup-smoke"]));
        Assert.False(CliProtectionGate.IsUnknownSwitch([RenderFailureRecovery.RecoverySwitch, "1", "manifest", "session", "--a-note"]));
        Assert.False(CliProtectionGate.IsUnknownSwitch(["/silent"]));
    }

    // R3-017: a locked or damaged file is not an owner's print limit.
    [Fact]
    public void PrintSaysWhatReallyStoppedIt()
    {
        string lockedMessage = CliProtectionGate.PrintRefusal(PdfRestrictions.Read(Pdf(user: Reader), null));
        string limitMessage = CliProtectionGate.PrintRefusal(PdfRestrictions.Read(Pdf(print: false), null));

        Assert.Equal(PdfRestrictions.LockedMessage, lockedMessage);
        Assert.Contains("restricted printing", limitMessage, StringComparison.Ordinal);
    }

    // R3-019: the password opens the input for every step, and the output class still decides.
    [Fact]
    public void AMacroReadsAPasswordInputForEveryStep()
    {
        string pdf = Pdf(user: Reader);
        var macro = new PdfMacro("Keep", [new PdfMacroStep(PdfMacroOperation.Optimize)]);

        byte[] input = CliProtectionGate.MacroInput(pdf, Reader, out string? refusal);

        Assert.Null(refusal);
        Assert.False(PdfDocument.Open(input).IsEncrypted);
        PdfMacroRunReport report = new PdfMacroDispatcher().RunReport(macro, [new ReadOnlyMemory<byte>(input)]);
        Assert.True(report.Results[0].Succeeded, report.Results[0].Error);
    }

    [Fact]
    public void AMacroOfAPrintLimitedFileTakesTheOwnerPassword()
    {
        string pdf = Pdf(print: false);

        CliProtectionGate.MacroInput(pdf, null, out string? refused);
        byte[] input = CliProtectionGate.MacroInput(pdf, Owner, out string? allowed);

        Assert.NotNull(refused);
        Assert.Null(allowed);
        Assert.False(PdfDocument.Open(input).IsEncrypted);
    }

    [Fact]
    public void APlainMacroInputIsReadAsItIs()
    {
        string pdf = InFolder("plain-macro.pdf");
        byte[] bytes = new PdfDocumentBuilder().AddBlankPage().Build();
        File.WriteAllBytes(pdf, bytes);

        Assert.Equal(bytes, CliProtectionGate.MacroInput(pdf, null, out _));
    }

    // R3-024: a read failure is reported as one.
    [Fact]
    public void BatchResaveCallsALockedFileUnreadable()
    {
        string path = InFolder("locked-plain.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());

        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.StartsWith("unreadable", CliProtectionGate.BatchResaveSkip(path)!, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchResaveStillPassesOverAnEncryptedFile() =>
        Assert.StartsWith("encrypted", CliProtectionGate.BatchResaveSkip(Pdf())!, StringComparison.Ordinal);

    private static string TestData(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "KillerPDF.Tests", "TestData", name);
    }

    private static byte[] Optimized(PdfDocument document) => PdfOptimizer.CreatePlan(document,
        PdfOptimizationOptions.ForDocument(document) with
        {
            RepairHarmlessArtifacts = true,
            ReplaceDanglingReferences = true
        }).Apply().Data.ToArray();

    // A healthy encrypted file reached the optimizer as ciphertext, which read as bad zlib data.
    [Fact]
    public void OptimizeTakesAHealthyEncryptedFileAndKeepsItsProtection()
    {
        string output = InFolder("optimized.pdf");

        PdfDocument? document = CliProtectionGate.OpenForRewrite(TestData("over-noprint.pdf"), null,
            out OpenedProtection protection, out string? refusal);
        Assert.Null(refusal);
        byte[] optimized = [];
        Assert.Null(Record.Exception(() => optimized = Optimized(document!)));
        File.WriteAllBytes(output, CliProtectionGate.SealRewrite(optimized, protection));

        Assert.Equal(PdfEncryptionState.Encrypted, PdfRestrictions.ReadEncryptionState(output));
        PdfAccess reopened = PdfRestrictions.Read(output, null);
        Assert.Equal(PdfRestrictions.Read(TestData("over-noprint.pdf"), null).Print, reopened.Print);
        Assert.True(reopened.MayModify);
        Assert.NotEqual(PrintAllowance.Allowed, reopened.Print);
        Assert.Equal(PdfAccessRole.Owner, PdfRestrictions.Read(output, "39e2345a58e1").Role);
    }

    [Fact]
    public void OptimizeOpensAUserPasswordFileWithItsPassword()
    {
        string source = Pdf(user: Reader);

        Assert.NotNull(CliProtectionGate.OpenForRewrite(source, null, out _, out string? locked) is null ? locked : null);
        Assert.NotNull(CliProtectionGate.OpenForRewrite(source, Reader, out _, out string? refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void OptimizeRefusesAFileThatForbidsChanges()
    {
        Assert.Null(CliProtectionGate.OpenForRewrite(Pdf(modify: false), null, out _, out string? refusal));
        Assert.Equal(CliProtectionGate.RestrictedContentMessage, refusal);
    }

    [Fact]
    public void APlainFileRewritesAsItAlwaysDid()
    {
        string source = InFolder("plain.pdf");
        string output = InFolder("plain-out.pdf");
        File.WriteAllBytes(source, new PdfDocumentBuilder().AddBlankPage().Build());

        PdfDocument? document = CliProtectionGate.OpenForRewrite(source, null, out OpenedProtection protection, out _);
        File.WriteAllBytes(output, CliProtectionGate.SealRewrite(Optimized(document!), protection));

        Assert.Equal(PdfEncryptionState.Plain, PdfRestrictions.ReadEncryptionState(output));
    }

    // Commands with no route around encryption name it in words a user can act on.
    [Fact]
    public void AnEncryptedInputIsNamedInPlainWords()
    {
        string source = Pdf();
        string raw = Record.Exception(() => PdfEngineIntegration.RotatePages(source, [0], 90))!.Message;
        Assert.Contains("must be opened with a password", raw, StringComparison.Ordinal);

        string said = CliProtectionGate.FailureMessage(raw, [source, "1", "90", InFolder("out.pdf")]);

        Assert.DoesNotContain("must be opened", said, StringComparison.Ordinal);
        Assert.Contains("decrypt", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrelatedFailureKeepsItsWording()
    {
        string source = Pdf();

        Assert.Equal("The disk is full.", CliProtectionGate.FailureMessage("The disk is full.", [source]));
    }

    [Fact]
    public void AFailureOnAPlainFileKeepsItsWording()
    {
        string source = InFolder("plain-failed.pdf");
        File.WriteAllBytes(source, new PdfDocumentBuilder().AddBlankPage().Build());
        const string raw = "An encrypted source PDF must be opened with a password before its pages can be imported.";

        Assert.Equal(raw, CliProtectionGate.FailureMessage(raw, [source, "missing.pdf", "1"]));
    }
}
