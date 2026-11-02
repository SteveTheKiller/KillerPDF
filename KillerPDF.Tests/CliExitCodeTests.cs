using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Runs the selected fresh app with command-line verbs. These verbs never open a window; the
/// tests read only the process exit code, because the console is attached to a parent console.
/// </summary>
public sealed class CliExitCodeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "killerpdf-cli-exit-" + Guid.NewGuid().ToString("N"));

    public CliExitCodeTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SearchTextOnADocumentThatCannotBeReadFailsInsteadOfReportingNoHits()
    {
        string garbage = Path.Combine(folder, "garbage.pdf");
        File.WriteAllBytes(garbage, Encoding.ASCII.GetBytes("not a pdf"));
        Assert.Equal(1, Run("--search-text", garbage, "x"));
    }

    [Fact]
    public void BatchResaveRefusesAnOutputFolderThatIsTheInputFolder()
    {
        string corpus = Path.Combine(folder, "corpus");
        Directory.CreateDirectory(corpus);
        string pdf = Path.Combine(corpus, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        byte[] before = File.ReadAllBytes(pdf);
        Assert.Equal(2, Run("--batch-resave", corpus, corpus));
        Assert.Equal(2, Run("--batch-resave", corpus, corpus + Path.DirectorySeparatorChar));
        Assert.Equal(before, File.ReadAllBytes(pdf));
    }

    [Fact]
    public void BatchResaveRefusesAnOutputFileThatIsTheInputFile()
    {
        string pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        byte[] before = File.ReadAllBytes(pdf);
        Assert.Equal(2, Run("--batch-resave", pdf, pdf));
        Assert.Equal(before, File.ReadAllBytes(pdf));
    }

    [Fact]
    public void BatchResaveToASiblingFolderStillSucceeds()
    {
        string corpus = Path.Combine(folder, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllBytes(Path.Combine(corpus, "one.pdf"), BlankPdf());
        string result = Path.Combine(folder, "result");
        Assert.Equal(0, Run("--batch-resave", corpus, result));
        Assert.True(File.Exists(Path.Combine(result, "one.pdf")));
    }

    [Theory]
    [InlineData("BatchRunner.cs", "Batch mode failed: ")]
    [InlineData("BatchRenderRunner.cs", "Render batch failed: ")]
    public void ABatchRunThatFailsOutsideItsFileLoopIsAnOperationFailureNotBadUsage(string file, string message)
    {
        // The per-file failures are caught inside the loop; this catch is the run itself failing.
        string source = ReadSource("Features", "Cli", file);
        int start = source.IndexOf(message, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Matches(@"exitCode = 1;", source[start..Math.Min(source.Length, start + 200)]);
    }

    [Theory]
    [InlineData("--color-mode", "purple")]
    [InlineData("--jpeg-quality", "500")]
    [InlineData("--compression", "zip")]
    public void FlattenWithAnUnusableOptionValueIsBadUsage(string option, string value)
    {
        string pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        Assert.Equal(2, Run("--flatten", pdf, Path.Combine(folder, "flat.pdf"), option, value));
    }

    [Theory]
    [InlineData("5000")]
    [InlineData("abc")]
    [InlineData("7,5")]
    public void ToImageWithAnUnusableDpiIsBadUsage(string dpi)
    {
        string pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        Assert.Equal(2, Run("--to-image", pdf, Path.Combine(folder, "images"), "--dpi", dpi));
    }

    [Fact]
    public void ToImageAcceptsAFractionalDpiWithAPeriod()
    {
        string pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        Assert.Equal(0, Run("--to-image", pdf, Path.Combine(folder, "images"), "--dpi", "72.5"));
    }

    [Fact]
    public void ToImageReadsTheDpiWithTheInvariantCulture()
    {
        string source = ReadSource("Features", "Cli", "CliRunner.cs");
        int start = source.IndexOf("TryReadDpi(", StringComparison.Ordinal);
        Assert.True(start >= 0, "CliRunner.cs must define TryReadDpi.");
        string body = source[start..Math.Min(source.Length, start + 900)];
        Assert.Contains("CultureInfo.InvariantCulture", body, StringComparison.Ordinal);
        Assert.DoesNotContain("double.TryParse(s, out", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoBookmarksHelpDocumentsItsNothingToDoExitCode()
    {
        string source = ReadSource("Features", "Cli", "CliRunner.cs");
        int line = source.IndexOf("killerpdf auto-bookmarks", StringComparison.Ordinal);
        Assert.True(line >= 0);
        Assert.Contains("exit 3 when no headings are found", source[line..Math.Min(source.Length, line + 400)], StringComparison.Ordinal);
        Assert.Contains("Exit codes: 0 success, 1 operation failed, 2 bad usage, 3 findings or nothing to do (where noted).",
            source, StringComparison.Ordinal);
    }

    // Opens with the empty user password, but the owner forbids editing, and printing when asked to.
    private string RestrictedPdf(string name, bool mayPrint = true, string user = "")
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .SetPasswordEncryption(new KillerPdf.Engine.Security.PdfPasswordEncryptionOptions
            {
                UserPassword = user,
                OwnerPassword = "owner-password",
                AllowDocumentModification = false,
                AllowLowQualityPrinting = mayPrint,
                AllowHighQualityPrinting = mayPrint
            })
            .AddBlankPage()
            .AddAttachment("note.txt", "inside"u8.ToArray())
            .Build());
        return path;
    }

    private string LimitedPdf(string name, bool mayPrint = true, bool mayCopy = true, string user = "")
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .SetPasswordEncryption(new KillerPdf.Engine.Security.PdfPasswordEncryptionOptions
            {
                UserPassword = user,
                OwnerPassword = "owner-password",
                AllowContentCopying = mayCopy,
                AllowLowQualityPrinting = mayPrint,
                AllowHighQualityPrinting = mayPrint
            })
            .AddBlankPage()
            .AddTextField(0, "name", 10, 100, 80, 20, "value", 10)
            .Build());
        return path;
    }

    [Fact]
    public void DecryptOfAFileWithALimitTakesTheOwnerPassword()
    {
        string pdf = LimitedPdf("decrypt-limited.pdf", mayPrint: false);
        string refused = Path.Combine(folder, "decrypt-refused.pdf"), allowed = Path.Combine(folder, "decrypt-allowed.pdf");

        Assert.Equal(1, Run("--decrypt", pdf, refused));
        Assert.False(File.Exists(refused));
        Assert.Equal(0, Run("--decrypt", pdf, allowed, "--password", "owner-password"));
        Assert.True(File.Exists(allowed));
    }

    [Fact]
    public void DecryptOfAFileThatLimitsNothingTakesItsUserPassword() =>
        Assert.Equal(0, Run("--decrypt", LimitedPdf("decrypt-open.pdf", user: "reader"),
            Path.Combine(folder, "decrypt-open-out.pdf"), "--password", "reader"));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FlattenOfAPrintOrCopyLimitedFileTakesTheOwnerPassword(bool mayPrint, bool mayCopy)
    {
        string pdf = LimitedPdf($"flatten-{mayPrint}.pdf", mayPrint, mayCopy);
        string refused = Path.Combine(folder, "flatten-refused.pdf"), allowed = Path.Combine(folder, "flatten-allowed.pdf");

        Assert.Equal(1, Run("--flatten", pdf, refused));
        Assert.False(File.Exists(refused));
        Assert.Equal(0, Run("--flatten", pdf, allowed, "--password", "owner-password"));
    }

    [Fact]
    public void TextExportFollowsTheCopyBit()
    {
        Assert.Equal(0, Run("--export", LimitedPdf("text-print.pdf", mayPrint: false), Path.Combine(folder, "text-print.txt")));
        Assert.Equal(1, Run("--export", LimitedPdf("text-copy.pdf", mayCopy: false), Path.Combine(folder, "text-copy.txt")));
    }

    [Fact]
    public void FormDataExportOfACopyLimitedFileIsRefused() =>
        Assert.Equal(1, Run("--export-form-data", LimitedPdf("form-copy.pdf", mayCopy: false), Path.Combine(folder, "form-copy.json")));

    [Fact]
    public void BatchRenderReadsWithAUserPassword() =>
        Assert.Equal(0, Run("--batch-render", LimitedPdf("render-user.pdf", user: "reader"),
            Path.Combine(folder, "render-user"), "--password", "reader"));

    [Fact]
    public void AMacroReadsAPasswordInputForEveryStep()
    {
        string macro = Path.Combine(folder, "macro.json");
        File.WriteAllText(macro, new KillerPdf.Engine.Documents.PdfMacro("Keep",
            [new KillerPdf.Engine.Documents.PdfMacroStep(KillerPdf.Engine.Documents.PdfMacroOperation.Optimize)]).ToJson());

        Assert.Equal(0, Run("--macro", LimitedPdf("macro-user.pdf", user: "reader"), macro,
            Path.Combine(folder, "macro-user-out.pdf"), "--password", "reader"));
    }

    [Theory]
    [InlineData("--resave")]
    [InlineData("--to-imge")]
    public void AnUnknownSwitchExitsWithBadUsage(string option) => Assert.Equal(2, Run(option, "in.pdf", "out.pdf"));

    [Fact]
    public void ToImageGivesARestrictedPdfsPagesOnlyToTheOwnerPassword()
    {
        string pdf = RestrictedPdf("restricted.pdf");
        string refused = Path.Combine(folder, "refused"), allowed = Path.Combine(folder, "allowed");

        Assert.Equal(1, Run("--to-image", pdf, refused));
        Assert.False(Directory.Exists(refused) && Directory.GetFiles(refused).Length > 0);
        Assert.Equal(1, Run("--to-image", pdf, refused, "--password", "wrong"));
        Assert.Equal(0, Run("--to-image", pdf, allowed, "--password", "owner-password"));
        Assert.NotEmpty(Directory.GetFiles(allowed));
    }

    [Fact]
    public void AttachmentsLeaveARestrictedPdfOnlyWithTheOwnerPassword()
    {
        string pdf = RestrictedPdf("restricted-attachments.pdf");
        string refused = Path.Combine(folder, "refused-attachments"), allowed = Path.Combine(folder, "allowed-attachments");

        Assert.Equal(1, Run("--attachments", pdf, refused));
        Assert.False(Directory.Exists(refused) && Directory.GetFiles(refused).Length > 0);
        Assert.Equal(0, Run("--attachments", pdf, allowed, "--password", "owner-password"));
        Assert.NotEmpty(Directory.GetFiles(allowed));
    }

    [Fact]
    public void FlatteningGivesARestrictedPdfOnlyToTheOwnerPassword()
    {
        string pdf = RestrictedPdf("restricted-flatten.pdf");
        string refused = Path.Combine(folder, "refused-flat.pdf"), allowed = Path.Combine(folder, "allowed-flat.pdf");

        Assert.Equal(1, Run("--flatten", pdf, refused));
        Assert.False(File.Exists(refused));
        Assert.Equal(0, Run("--flatten", pdf, allowed, "--password", "owner-password"));
        Assert.True(File.Exists(allowed));
    }

    // Only the refusals run here, and they name a printer that cannot exist, so a gate that failed
    // would end at "printer not found" (2) and never reach a real printer.
    [Fact]
    public void PrintingRefusesAPdfThatForbidsItUnlessTheOwnerPasswordIsGiven()
    {
        string pdf = RestrictedPdf("no-print.pdf", mayPrint: false);

        Assert.Equal(1, Run("--print", pdf, "--printer", "KillerPDF-no-such-printer"));
        Assert.Equal(1, Run("--print", pdf, "--printer", "KillerPDF-no-such-printer", "--password", "wrong"));
    }

    // A PDF whose owner forbids only editing may still be printed, so print is not held to the edit limit.
    [Fact]
    public void PrintingIsNotHeldToTheEditRestriction()
    {
        string pdf = RestrictedPdf("edit-only.pdf");

        Assert.Equal(2, Run("--print", pdf, "--printer", "KillerPDF-no-such-printer"));
    }

    // The user password opens this one and the owner forbids editing but not printing, so the run
    // reaches the printer lookup, the same as the owner password does.
    [Fact]
    public void TheUserPasswordPrintsAPdfThatAllowsPrintingButNotEditing()
    {
        string pdf = RestrictedPdf("user-print.pdf", user: "reader");

        Assert.Equal(2, Run("--print", pdf, "--printer", "KillerPDF-no-such-printer", "--password", "reader"));
        Assert.Equal(2, Run("--print", pdf, "--printer", "KillerPDF-no-such-printer", "--password", "owner-password"));
        Assert.Equal(1, Run("--print", pdf, "--printer", "KillerPDF-no-such-printer", "--password", "wrong"));
    }

    [Fact]
    public void AWrongPasswordTakesNoAttachmentsFromARestrictedPdf()
    {
        string pdf = RestrictedPdf("wrong-attachments.pdf");
        string refused = Path.Combine(folder, "wrong-attachments");

        Assert.Equal(1, Run("--attachments", pdf, refused, "--password", "wrong"));
        Assert.False(Directory.Exists(refused) && Directory.GetFiles(refused).Length > 0);
    }

    [Fact]
    public void AttachmentsReadWithThePasswordOfAFileThatNeedsOne()
    {
        string pdf = RestrictedPdf("user-attachments.pdf", user: "reader");
        string allowed = Path.Combine(folder, "user-attachments-owner"), refused = Path.Combine(folder, "user-attachments-user");

        Assert.Equal(0, Run("--attachments", pdf, "--password", "reader"));
        Assert.Equal(0, Run("--attachments", pdf, allowed, "--password", "owner-password"));
        Assert.NotEmpty(Directory.GetFiles(allowed));
        Assert.Equal(1, Run("--attachments", pdf, refused, "--password", "reader"));
        Assert.False(Directory.Exists(refused) && Directory.GetFiles(refused).Length > 0);
    }

    [Fact]
    public void BatchRenderSkipsARestrictedPdfAndRendersItOnlyWithTheOwnerPassword()
    {
        string pdf = RestrictedPdf("batch-restricted.pdf");
        string refused = Path.Combine(folder, "batch-refused"), allowed = Path.Combine(folder, "batch-allowed");
        string log = Path.Combine(folder, "batch.csv");

        Assert.Equal(1, Run("--batch-render", pdf, refused, "--log", log));
        Assert.Empty(Directory.GetFiles(refused, "*.png"));
        string row = Assert.Single(File.ReadAllLines(log).Skip(1));
        Assert.Contains(",SKIP,", row, StringComparison.Ordinal);
        Assert.Equal(1, Run("--batch-render", pdf, refused, "--password", "wrong"));
        Assert.Empty(Directory.GetFiles(refused, "*.png"));
        Assert.Equal(0, Run("--batch-render", pdf, allowed, "--password", "owner-password"));
        Assert.NotEmpty(Directory.GetFiles(allowed, "*.png"));
    }

    [Fact]
    public void BatchRenderWithTheOwnerPasswordReadsAFileThatNeedsAPassword()
    {
        string pdf = RestrictedPdf("batch-user.pdf", user: "reader");
        string allowed = Path.Combine(folder, "batch-user");

        Assert.Equal(0, Run("--batch-render", pdf, allowed, "--password", "owner-password"));
        Assert.NotEmpty(Directory.GetFiles(allowed, "*.png"));
    }

    [Theory]
    [InlineData("out.txt")]
    [InlineData("out.json")]
    [InlineData("out.html")]
    [InlineData("out.md")]
    [InlineData("out.docx")]
    [InlineData("out.xlsx")]
    [InlineData("out.pptx")]
    public void EveryExportFormatLeavesARestrictedPdfOnlyWithTheOwnerPassword(string fileName)
    {
        string pdf = RestrictedPdf("export-" + fileName + ".pdf");
        string output = Path.Combine(folder, fileName);

        Assert.Equal(1, Run("--export", pdf, output));
        Assert.False(File.Exists(output));
        Assert.Equal(1, Run("--export", pdf, output, "--password", "wrong"));
        Assert.False(File.Exists(output));
        Assert.Equal(0, Run("--export", pdf, output, "--password", "owner-password"));
        Assert.True(File.Exists(output));
    }

    [Theory]
    [InlineData("out-images.docx")]
    [InlineData("out-images.pptx")]
    public void PageImageExportLeavesARestrictedPdfOnlyWithTheOwnerPassword(string fileName)
    {
        string pdf = RestrictedPdf("export-" + fileName + ".pdf");
        string output = Path.Combine(folder, fileName);

        Assert.Equal(1, Run("--export", pdf, output, "--layout", "images"));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ExportReadsWithThePasswordOfAFileThatNeedsOne()
    {
        string pdf = RestrictedPdf("export-user.pdf", user: "reader");
        string output = Path.Combine(folder, "export-user.txt");

        Assert.Equal(0, Run("--export", pdf, output, "--password", "owner-password"));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void MacroStepsTakeTheOwnerPassword()
    {
        string pdf = RestrictedPdf("macro.pdf");
        string refused = Path.Combine(folder, "macro-refused.pdf"), allowed = Path.Combine(folder, "macro-allowed.pdf");

        Assert.Equal(1, Run("--macro", pdf, "starter:scanning", refused));
        Assert.False(File.Exists(refused));
        Assert.Equal(1, Run("--macro", pdf, "starter:scanning", refused, "--password", "wrong"));
        Assert.False(File.Exists(refused));
        Assert.Equal(0, Run("--macro", pdf, "starter:scanning", allowed, "--password", "owner-password"));
        Assert.True(File.Exists(allowed));
    }

    // Tail padding pushes /Encrypt out of the last 2 KB, which is what a linearized file or a large
    // cross-reference stream does, so a gate that trusted a tail scan would find nothing to protect.
    [Fact]
    public void AWrongPasswordTakesNoPagesFromARestrictedPdfWhoseEncryptionIsFarFromTheEnd()
    {
        string pdf = RestrictedPdf("tail-padded.pdf");
        File.AppendAllText(pdf, "\n%" + new string('p', 4096) + "\n");
        string refused = Path.Combine(folder, "tail-refused");

        Assert.Equal(1, Run("--to-image", pdf, refused, "--password", "wrong"));
        Assert.False(Directory.Exists(refused) && Directory.GetFiles(refused).Length > 0);
        Assert.Equal(1, Run("--to-image", pdf, refused));
        Assert.False(Directory.Exists(refused) && Directory.GetFiles(refused).Length > 0);
    }

    private string Blank(string name)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, BlankPdf());
        return path;
    }

    private static KillerPdf.Engine.Documents.PdfDocument Open(string path) =>
        KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(path));

    [Fact]
    public void SetMetadataChangesOnlyTheNamedFields()
    {
        string first = Path.Combine(folder, "meta1.pdf"), second = Path.Combine(folder, "meta2.pdf");
        Assert.Equal(0, Run("--set-metadata", Blank("m.pdf"), first, "--title", "Report", "--author", "Ann"));
        Assert.Equal(0, Run("--set-metadata", first, second, "--subject", "Q3"));

        var info = KillerPdf.Engine.Documents.PdfDocumentInformation.Read(Open(second));
        Assert.Equal(("Report", "Ann", "Q3"), (info.Title, info.Author, info.Subject));
    }

    [Fact]
    public void SetMetadataWithNothingToSetIsBadUsage()
    {
        Assert.Equal(2, Run("--set-metadata", Blank("n.pdf"), Path.Combine(folder, "n2.pdf")));
    }

    [Fact]
    public void CommentsCanBeAddedAndDeleted()
    {
        string noted = Path.Combine(folder, "noted.pdf"), cleared = Path.Combine(folder, "cleared.pdf");
        Assert.Equal(0, Run("--add-comment", Blank("c.pdf"), noted, "--page", "1", "--text", "Check this"));
        Assert.Equal("Check this", Assert.Single(
            KillerPdf.Engine.Documents.PdfCommentReader.Read(Open(noted))).Contents);

        Assert.Equal(0, Run("--delete-comments", noted, cleared));
        Assert.Empty(KillerPdf.Engine.Documents.PdfCommentReader.Read(Open(cleared)));
    }

    [Fact]
    public void AnOptionValueNamingABatchCommandStaysLiteralText()
    {
        string output = Path.Combine(folder, "literal.pdf");
        Assert.Equal(0, Run("--add-comment", Blank("l.pdf"), output, "--page", "1", "--text", "--batch-resave"));
        Assert.Equal("--batch-resave", Assert.Single(
            KillerPdf.Engine.Documents.PdfCommentReader.Read(Open(output))).Contents);
    }

    [Fact]
    public void AddCommentOutsideTheDocumentIsBadUsage()
    {
        Assert.Equal(2, Run("--add-comment", Blank("p.pdf"), Path.Combine(folder, "p2.pdf"), "--page", "9", "--text", "x"));
    }

    [Theory]
    [InlineData("--box")]
    [InlineData("--media")]
    public void CropReachesThePage(string mode)
    {
        string output = Path.Combine(folder, "crop" + mode.Trim('-') + ".pdf");
        string[] arguments = mode == "--media"
            ? ["--crop", Blank("k.pdf"), output, "--box", "0,0,100,200", "--media"]
            : ["--crop", Blank("k.pdf"), output, "--box", "0,0,100,200"];
        Assert.Equal(0, Run(arguments));
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(folder, "k.pdf")).Length, new FileInfo(output).Length);
    }

    [Theory]
    [InlineData("0,0,100")]
    [InlineData("0,0,-5,10")]
    [InlineData("a,b,c,d")]
    [InlineData("100000,100000,10,10")]
    public void CropWithAnUnusableBoxIsBadUsage(string box)
    {
        Assert.Equal(2, Run("--crop", Blank("b.pdf"), Path.Combine(folder, "b2.pdf"), "--box", box));
    }

    [Fact]
    public void SetBookmarksImportsAnExchangeFile()
    {
        string json = Path.Combine(folder, "marks.json");
        File.WriteAllText(json, KillerPdf.Engine.Documents.PdfBookmarkExchange.ToJson(
            KillerPdf.Engine.Documents.PdfDocument.Open(new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
                .AddBlankPage().AddBookmark("Start", 0).Build())));
        Assert.Equal(0, Run("--set-bookmarks", Blank("s.pdf"), Path.Combine(folder, "s2.pdf"), json));
    }

    [Fact]
    public void EveryCommandIsDocumentedOnTheHelpPage()
    {
        string runner = ReadSource("Features", "Cli", "CliRunner.cs");
        int start = runner.IndexOf("\"accessibility\", \"attachments\"", StringComparison.Ordinal);
        string list = runner[start..runner.IndexOf("], \"--\")", start, StringComparison.Ordinal)];
        string page = ReadSource("pdf-landing", "help.html");
        string[] verbs = [.. System.Text.RegularExpressions.Regex.Matches(list, "\"([a-z-]+)\"").Select(match => match.Groups[1].Value)];
        Assert.NotEmpty(verbs);
        Assert.All(verbs, verb => Assert.Contains("killerpdf " + verb, page, StringComparison.Ordinal));
    }

    [Fact]
    public void SetBookmarksRefusesAMissingFile()
    {
        Assert.Equal(2, Run("--set-bookmarks", Blank("t.pdf"), Path.Combine(folder, "t2.pdf"),
            Path.Combine(folder, "none.json")));
    }

    [Fact]
    public void PrintWithAnUnusableDuplexIsBadUsage()
    {
        string pdf = Path.Combine(folder, "print.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        Assert.Equal(2, Run("--print", pdf, "--duplex", "sideways"));
    }

    private int Run(params string[] arguments)
    {
        string exe = Environment.GetEnvironmentVariable("KILLERPDF_CLI_TEST_EXE")
            ?? Path.Combine(RepositoryRoot(), "bin", "Release", "net10.0-windows", "KillerPDF.exe");
        Assert.True(File.Exists(exe), "Build KillerPDF.csproj in Release first: " + exe);
        string expectedVersion = System.Xml.Linq.XDocument.Load(Path.Combine(RepositoryRoot(), "KillerPDF.csproj"))
            .Descendants("FileVersion").Single().Value;
        string? actualVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion;
        Assert.True(actualVersion == expectedVersion,
            $"CLI tests need version {expectedVersion}, but {exe} is {actualVersion}. Build the current app or set KILLERPDF_CLI_TEST_EXE to its fresh payload EXE.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The command did not finish.");
        }
        return process.ExitCode;
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static byte[] BlankPdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>"
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new System.Collections.Generic.List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
