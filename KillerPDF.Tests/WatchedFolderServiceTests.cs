using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class WatchedFolderServiceTests
{
    // These waits are for work the service starts on its own (a first scan, a macro step), so the limit is only a hang
    // guard; a cold start of that work on a starved machine takes longer than any short limit.
    private static readonly TimeSpan Hang = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData("unsupported", "UnsupportedOperation", PdfMacroOperation.Redact)]
    [InlineData("prompt", "InteractiveParameter", PdfMacroOperation.Resize)]
    [InlineData("secret", "InteractiveParameter", PdfMacroOperation.Save)]
    public void UnsupportedConfigurationIdentifiesItsStepWithoutReplacingTheSavedSetup(
        string kind, string code, PdfMacroOperation operation)
    {
        using var fixture = new FolderFixture();
        using var service = fixture.Create();
        service.Configure(fixture.Options());
        string journal = Path.Combine(fixture.Storage, "queue.json");
        byte[] saved = File.ReadAllBytes(journal);
        PdfMacroStep rejectedStep = kind switch
        {
            "prompt" => new(PdfMacroOperation.Resize, new Dictionary<string, string> { ["percent"] = "${size}" }),
            "secret" => new(PdfMacroOperation.Save, new Dictionary<string, string> { ["password"] = "not-for-storage" }),
            _ => new(PdfMacroOperation.Redact)
        };
        var rejected = new PdfMacro("Unsupported", [new(PdfMacroOperation.Save), rejectedStep]);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => service.Configure(fixture.Options(rejected)));
        Assert.Equal(code, error.Message);
        Assert.True(error.Data.Contains("KillerPDF.WatchedFolderStepIssue"),
            "UI-005: unsupported watched configuration discarded the offending step.");
        object issue = error.Data["KillerPDF.WatchedFolderStepIssue"]!;
        Assert.Equal(1, issue.GetType().GetProperty("StepIndex")!.GetValue(issue));
        Assert.Equal(operation, issue.GetType().GetProperty("Operation")!.GetValue(issue));
        Assert.Equal(saved, File.ReadAllBytes(journal));
        Assert.Equal(fixture.Options().MacroJson, service.GetSnapshot().Options!.MacroJson);
    }

    [Fact]
    public async Task FailedMacroIdentifiesItsStepAndOperationAfterRestart()
    {
        using var fixture = new FolderFixture();
        byte[] original = new PdfDocumentBuilder().AddPage(200, 300, new PdfContentStreamBuilder()
            .SetFillLabColor(new PdfLabColorSpace(), 50, 20, 10).Rectangle(10, 10, 50, 50).Fill()).Build();
        string input = Path.Combine(fixture.Input, "document.pdf");
        File.WriteAllBytes(input, original);
        var macro = new PdfMacro("Color conversion", [PdfResizeMacro.ScaleStep(100),
            PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale), new(PdfMacroOperation.Save)]);
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.Resize, PdfResizeMacro.Execute),
            new(PdfMacroOperation.ConvertColor, PdfColorConversionMacro.Execute)]);
        PdfMacroFileResult control = Assert.Single(dispatcher.RunReport(macro,
            [new ReadOnlyMemory<byte>(original)]).Results);
        Assert.False(control.Succeeded);
        Assert.Equal(1, control.FailedStepIndex);
        Assert.Equal(PdfMacroOperation.ConvertColor, control.FailedOperation);
        using (var service = fixture.Create())
        {
            service.Configure(fixture.Options(macro));
            service.Resume();
            await service.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await service.ReconcileAsync();
            AssertFailure(Assert.Single(service.GetSnapshot().Jobs));
        }
        using var restarted = fixture.Create();
        AssertFailure(Assert.Single(restarted.GetSnapshot().Jobs));
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Empty(Directory.GetFiles(fixture.Output));

        static void AssertFailure(WatchedFolderJob job)
        {
            Assert.Equal(WatchedFolderJobStatus.Failed, job.Status);
            Assert.Equal("MacroFailed", job.ErrorCode);
            using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(job));
            Assert.True(json.RootElement.TryGetProperty("FailedStepIndex", out var step),
                "UI-005: the watched-folder snapshot discarded the failed macro step.");
            Assert.Equal(1, step.GetInt32());
            Assert.Equal((int)PdfMacroOperation.ConvertColor, json.RootElement.GetProperty("FailedOperation").GetInt32());
        }
    }

    [Fact]
    public async Task StablePdfIsProcessedAsANewCopyThroughTheSavedMacro()
    {
        using var fixture = new FolderFixture();
        byte[] original = new PdfDocumentBuilder().AddBlankPage(200, 300).Build();
        string input = Path.Combine(fixture.Input, "document.pdf");
        File.WriteAllBytes(input, original);
        using var service = fixture.Create();
        service.Configure(fixture.Options(new PdfMacro("Half size",
            [PdfResizeMacro.ScaleStep(50), new(PdfMacroOperation.Save)])));
        Assert.True(service.GetSnapshot().IsPaused);
        service.Resume();

        await service.ReconcileAsync();
        Assert.Empty(Directory.GetFiles(fixture.Output));
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();

        WatchedFolderJob job = Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(WatchedFolderJobStatus.Completed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(original, File.ReadAllBytes(input));
        PdfPageInformation page = Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(job.OutputPath))));
        Assert.Equal(100, page.Width);
        Assert.Equal(150, page.Height);
    }

    [Fact]
    public async Task CompletedJournalJobCannotClaimAFailedMacroStep()
    {
        using var fixture = new FolderFixture();
        File.WriteAllBytes(Path.Combine(fixture.Input, "document.pdf"), new PdfDocumentBuilder().AddBlankPage().Build());
        using (var service = fixture.Create())
        {
            service.Configure(fixture.Options());
            service.Resume();
            await service.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await service.ReconcileAsync();
            Assert.Equal(WatchedFolderJobStatus.Completed, Assert.Single(service.GetSnapshot().Jobs).Status);
        }
        string statePath = Path.Combine(fixture.Storage, "queue.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
        state["Jobs"]![0]!["FailedStepIndex"] = 0;
        state["Jobs"]![0]!["FailedOperation"] = (int)PdfMacroOperation.Save;
        File.WriteAllText(statePath, state.ToJsonString());
        byte[] journal = File.ReadAllBytes(statePath);
        using var restarted = fixture.Create();
        Assert.Equal("QueueInvalid", restarted.GetSnapshot().ErrorCode);
        Assert.Equal(journal, File.ReadAllBytes(statePath));
    }

    [Fact]
    public async Task CompletedContentIsNotProcessedAgainAfterRestartOrRename()
    {
        using var fixture = new FolderFixture();
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        string source = Path.Combine(fixture.Input, "first.pdf");
        File.WriteAllBytes(source, original);
        Guid jobId;
        using (var service = fixture.Create())
        {
            service.Configure(fixture.Options());
            service.Resume();
            await service.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await service.ReconcileAsync();
            jobId = Assert.Single(service.GetSnapshot().Jobs).Id;
        }
        File.Move(source, Path.Combine(fixture.Input, "renamed.pdf"));
        using var restarted = fixture.Create();
        Assert.NotNull(restarted.GetSnapshot().Options);
        restarted.Resume();
        await restarted.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await restarted.ReconcileAsync();
        Assert.Equal(jobId, Assert.Single(restarted.GetSnapshot().Jobs).Id);
        Assert.Single(Directory.GetFiles(fixture.Output, "*.pdf"));
    }

    [Fact]
    public async Task ExistingOutputIsPreservedAndFailureRequiresAnExplicitRetry()
    {
        using var fixture = new FolderFixture();
        File.WriteAllBytes(Path.Combine(fixture.Input, "document.pdf"), new PdfDocumentBuilder().AddBlankPage().Build());
        string destination = Path.Combine(fixture.Output, "document.pdf");
        byte[] sentinel = [1, 3, 5, 7];
        File.WriteAllBytes(destination, sentinel);
        using var service = fixture.Create();
        service.Configure(fixture.Options() with { PauseOnError = true });
        service.Resume();
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();
        WatchedFolderJob failed = Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(WatchedFolderJobStatus.Failed, failed.Status);
        Assert.Equal("OutputExists", failed.ErrorCode);
        Assert.True(service.GetSnapshot().IsPaused);
        Assert.Equal(sentinel, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(fixture.Output, "*.tmp"));
        File.Delete(destination);
        service.Resume();
        await service.ReconcileAsync();
        Assert.False(File.Exists(destination));
        Assert.Equal(1, Assert.Single(service.GetSnapshot().Jobs).Attempts);
        service.RetryFailed();
        await service.ReconcileAsync();
        Assert.Equal(WatchedFolderJobStatus.Completed, Assert.Single(service.GetSnapshot().Jobs).Status);
        Assert.Equal(2, Assert.Single(service.GetSnapshot().Jobs).Attempts);
        service.Pause();
        File.WriteAllBytes(Path.Combine(fixture.Input, "paused.pdf"), new PdfDocumentBuilder().AddBlankPage(20, 30).Build());
        await service.ReconcileAsync();
        Assert.Single(service.GetSnapshot().Jobs);
    }

    [Theory]
    [InlineData(false, "InvalidPdf")]
    [InlineData(true, "EncryptedPdf")]
    public async Task UnsupportedPdfInputsFailVisiblyWithoutWritingOutput(bool encrypted, string expectedError)
    {
        using var fixture = new FolderFixture();
        byte[] bytes = encrypted
            ? KillerPdf.Engine.Writing.PdfDocumentEncryptionWriter.EncryptWithPasswords(
                PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
                new KillerPdf.Engine.Security.PdfPasswordEncryptionOptions
                    { UserPassword = "secret", OwnerPassword = "owner" })
            : [1, 2, 3, 4, 5];
        string source = Path.Combine(fixture.Input, "document.pdf");
        File.WriteAllBytes(source, bytes);
        using var service = fixture.Create();
        service.Configure(fixture.Options());
        service.Resume();
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();
        WatchedFolderJob failed = Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(WatchedFolderJobStatus.Failed, failed.Status);
        Assert.Equal(expectedError, failed.ErrorCode);
        Assert.Empty(Directory.GetFiles(fixture.Output));
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("same-folder", "InvalidFolders")]
    [InlineData("path-filter", "InvalidPattern")]
    [InlineData("redact", "UnsupportedOperation")]
    [InlineData("prompt", "InteractiveParameter")]
    [InlineData("password", "InteractiveParameter")]
    [InlineData("resize", "InvalidMacro")]
    public void UnsafeConfigurationIsRejectedBeforeItCanRun(string kind, string expectedError)
    {
        using var fixture = new FolderFixture();
        using var service = fixture.Create();
        WatchedFolderOptions options = fixture.Options();
        options = kind switch
        {
            "same-folder" => options with { OutputFolder = fixture.Input },
            "path-filter" => options with { FilePattern = "../*.pdf" },
            "redact" => fixture.Options(new PdfMacro("Interactive", [new(PdfMacroOperation.Redact)])),
            "prompt" => fixture.Options(new PdfMacro("Prompt", [new(PdfMacroOperation.Resize,
                new Dictionary<string, string> { ["percent"] = "${size}" })])),
            "password" => fixture.Options(new PdfMacro("Secret", [new(PdfMacroOperation.Save,
                new Dictionary<string, string> { ["password"] = "do-not-persist" })])),
            _ => fixture.Options(new PdfMacro("Invalid", [new(PdfMacroOperation.Resize)]))
        };
        Assert.Equal(expectedError, Assert.Throws<InvalidOperationException>(() => service.Configure(options)).Message);
        Assert.Null(service.GetSnapshot().Options);
        Assert.True(service.GetSnapshot().IsPaused);
    }

    [Fact]
    public async Task PauseCancelsAnActiveMacroAndConcurrentReconcilesDoNotDuplicateWork()
    {
        using var fixture = new FolderFixture();
        File.WriteAllBytes(Path.Combine(fixture.Input, "document.pdf"),
            new PdfDocumentBuilder().AddBlankPage(200, 300).Build());
        var enteredResize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueResize = new ManualResetEventSlim();
        int dispatches = 0;
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.Resize, (step, bytes, values, token) =>
            {
                Interlocked.Increment(ref dispatches);
                enteredResize.TrySetResult();
                continueResize.Wait(token);
                return PdfResizeMacro.Execute(step, bytes, values, token);
            })]);
        using var service = new WatchedFolderService(fixture.Storage, dispatcher, fixture.Clock);
        service.Configure(fixture.Options(new PdfMacro("Resize", [PdfResizeMacro.ScaleStep(100)])));
        service.Resume();
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Task running = service.ReconcileAsync();
        try
        {
            await enteredResize.Task.WaitAsync(Hang);
            Assert.False(running.IsCompleted);
            Assert.Equal(WatchedFolderJobStatus.Processing, Assert.Single(service.GetSnapshot().Jobs).Status);
            Task duplicate = service.ReconcileAsync();
            await duplicate.WaitAsync(Hang);
            Assert.Equal(1, Volatile.Read(ref dispatches));
            service.Pause();
            await running.WaitAsync(Hang);
        }
        finally { continueResize.Set(); }
        Assert.True(service.GetSnapshot().IsPaused);
        WatchedFolderJob job = Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(WatchedFolderJobStatus.Waiting, job.Status);
        Assert.Equal("Canceled", job.ErrorCode);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(1, Volatile.Read(ref dispatches));
        Assert.Empty(Directory.GetFiles(fixture.Output));
    }

    [Theory]
    [InlineData("corrupt", "QueueInvalid")]
    [InlineData("locked", "QueueUnreadable")]
    [InlineData("owned", "AlreadyOwned")]
    public async Task UnusableDurableStorageFailsClosedAndPreservesExistingState(string kind, string expectedError)
    {
        using var fixture = new FolderFixture();
        using (var seed = fixture.Create()) seed.Configure(fixture.Options());
        string state = Path.Combine(fixture.Storage, "queue.json");
        if (kind == "corrupt") File.WriteAllText(state, "{broken queue");
        byte[] before = File.ReadAllBytes(state);
        using var owner = kind == "owned" ? fixture.Create() : null;
        using var held = kind == "locked" ? File.Open(state, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        using var blocked = fixture.Create();
        Assert.Equal(expectedError, blocked.GetSnapshot().ErrorCode);
        Assert.True(blocked.GetSnapshot().IsPaused);
        Assert.Equal(expectedError, Assert.Throws<InvalidOperationException>(blocked.Resume).Message);
        await blocked.ReconcileAsync();
        held?.Dispose();
        Assert.Equal(before, File.ReadAllBytes(state));
        Assert.Empty(Directory.GetFiles(fixture.Output));
    }

    [Fact]
    public async Task PartialOrLockedCopiesWaitForAStableExclusiveSnapshotWithoutRetrying()
    {
        using var fixture = new FolderFixture();
        string source = Path.Combine(fixture.Input, "copying.pdf");
        byte[] complete = new PdfDocumentBuilder().AddBlankPage().Build();
        using var service = fixture.Create();
        service.Configure(fixture.Options());
        service.Resume();
        using (var writer = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writer.Write(complete.AsSpan(0, complete.Length / 2));
            writer.Flush();
            await service.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await service.ReconcileAsync();
            Assert.Empty(service.GetSnapshot().Jobs);
            Assert.Equal("InputBusy", service.GetSnapshot().ErrorCode);
            writer.Write(complete.AsSpan(complete.Length / 2));
            writer.Flush();
        }
        await service.ReconcileAsync();
        Assert.Empty(service.GetSnapshot().Jobs);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();
        Assert.Equal(WatchedFolderJobStatus.Completed, Assert.Single(service.GetSnapshot().Jobs).Status);
        Assert.Equal(1, Assert.Single(service.GetSnapshot().Jobs).Attempts);
        Assert.Equal(complete, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task BoundedScanningEventuallyReachesLaterFilesAndNeverScansTheNestedOutput()
    {
        using var fixture = new FolderFixture();
        byte[] same = new PdfDocumentBuilder().AddBlankPage().Build();
        for (int i = 0; i < 260; i++) File.WriteAllBytes(Path.Combine(fixture.Input, $"a-{i:D3}.pdf"), same);
        File.WriteAllBytes(Path.Combine(fixture.Input, "z-last.PDF"), new PdfDocumentBuilder().AddBlankPage(50, 60).Build());
        File.WriteAllBytes(Path.Combine(fixture.Input, "ignored.txt"), same);
        string nested = Path.Combine(fixture.Input, "processed");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "never-input.pdf"), new PdfDocumentBuilder().AddBlankPage(70, 80).Build());
        using var service = fixture.Create();
        service.Configure(fixture.Options() with { OutputFolder = nested });
        service.Resume();
        await service.ReconcileAsync();
        Assert.InRange(fixture.Clock.ReadCount, 1, 256);
        for (int i = 0; i < 150 && !File.Exists(Path.Combine(nested, "z-last.PDF")); i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await service.ReconcileAsync();
        }
        Assert.True(File.Exists(Path.Combine(nested, "z-last.PDF")));
        Assert.Equal(2, service.GetSnapshot().Jobs.Count);
        Assert.DoesNotContain(service.GetSnapshot().Jobs, job => job.InputPath.Contains("processed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("published")]
    [InlineData("missing")]
    [InlineData("mismatch")]
    public async Task InterruptedPublicationRecoversOnlyTheExactExpectedOutput(string interruption)
    {
        using var fixture = new FolderFixture();
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        File.WriteAllBytes(Path.Combine(fixture.Input, "source.pdf"), original);
        string destination;
        using (var service = fixture.Create())
        {
            service.Configure(fixture.Options());
            service.Resume();
            await service.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await service.ReconcileAsync();
            destination = Assert.Single(service.GetSnapshot().Jobs).OutputPath;
        }
        string statePath = Path.Combine(fixture.Storage, "queue.json");
        var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
        var interrupted = journal["Jobs"]![0]!;
        interrupted["Status"] = (int)WatchedFolderJobStatus.Processing;
        interrupted["PublicationPending"] = true;
        interrupted["OutputHash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(destination)));
        File.WriteAllText(statePath, journal.ToJsonString());
        if (interruption == "missing") File.Delete(destination);
        if (interruption == "mismatch") File.WriteAllBytes(destination, [2, 4, 6]);
        using var restarted = fixture.Create();
        for (int i = 0; i < 3; i++)
        {
            await restarted.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        }
        WatchedFolderJob recovered = Assert.Single(restarted.GetSnapshot().Jobs);
        Assert.Equal(interruption == "mismatch" ? WatchedFolderJobStatus.Failed : WatchedFolderJobStatus.Completed,
            recovered.Status);
        Assert.Equal(interruption == "mismatch" ? "RecoveryMismatch" : null, recovered.ErrorCode);
        Assert.Equal(interruption == "missing" ? 2 : 1, recovered.Attempts);
        Assert.Single(Directory.GetFiles(fixture.Output, "*.pdf"));
        Assert.Equal(interruption == "mismatch" ? [2, 4, 6] : original, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task QueueWriteFailurePreventsOutputPublication()
    {
        using var fixture = new FolderFixture();
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        string source = Path.Combine(fixture.Input, "source.pdf");
        File.WriteAllBytes(source, original);
        using var service = fixture.Create();
        service.Configure(fixture.Options());
        service.Resume();
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        using var held = File.Open(Path.Combine(fixture.Storage, "queue.json"), FileMode.Open,
            FileAccess.Read, FileShare.None);
        await service.ReconcileAsync();
        Assert.Equal("QueueWriteFailed", service.GetSnapshot().ErrorCode);
        Assert.True(service.GetSnapshot().IsPaused);
        Assert.Empty(Directory.GetFiles(fixture.Output));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task ExplicitChangedSetupArchivesHistoryAndUnchangedSetupKeepsDeduplication()
    {
        using var fixture = new FolderFixture();
        File.WriteAllBytes(Path.Combine(fixture.Input, "source.pdf"), new PdfDocumentBuilder().AddBlankPage().Build());
        using var service = fixture.Create();
        service.Configure(fixture.Options());
        service.Resume();
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();
        Guid first = Assert.Single(service.GetSnapshot().Jobs).Id;
        service.Pause();
        service.Configure(fixture.Options() with { PauseOnError = true });
        Assert.Equal(first, Assert.Single(service.GetSnapshot().Jobs).Id);
        var changed = fixture.Options(new PdfMacro("Renamed", [PdfRenameMacro.RenameStep("changed-{index}")]));
        service.Configure(changed);
        Assert.Empty(service.GetSnapshot().Jobs);
        Assert.True(service.GetSnapshot().IsPaused);
        Assert.Equal(2, Directory.GetFiles(fixture.Storage, "*.json", SearchOption.AllDirectories).Length);
        service.Resume();
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();
        Assert.NotEqual(first, Assert.Single(service.GetSnapshot().Jobs).Id);
        Assert.True(File.Exists(Path.Combine(fixture.Output, "changed-1.pdf")));
        Assert.True(File.Exists(Path.Combine(fixture.Output, "source.pdf")));
    }

    [Theory]
    [InlineData("input-path")]
    [InlineData("output-path")]
    [InlineData("hash")]
    [InlineData("missing-fields")]
    public async Task InvalidQueueStructureCannotRedirectOrResetWork(string corruption)
    {
        using var fixture = new FolderFixture();
        File.WriteAllBytes(Path.Combine(fixture.Input, "source.pdf"), new PdfDocumentBuilder().AddBlankPage().Build());
        using (var service = fixture.Create())
        {
            service.Configure(fixture.Options());
            service.Resume();
            await service.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            await service.ReconcileAsync();
        }
        string statePath = Path.Combine(fixture.Storage, "queue.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
        var job = state["Jobs"]![0]!;
        if (corruption == "input-path") job["InputPath"] = Path.Combine(fixture.Root, "outside.pdf");
        if (corruption == "output-path") job["OutputPath"] = Path.Combine(fixture.Input, "source.pdf");
        if (corruption == "hash") job["OutputHash"] = "invalid";
        if (corruption == "missing-fields") state.AsObject().Remove("IsPaused");
        File.WriteAllText(statePath, state.ToJsonString());
        byte[] before = File.ReadAllBytes(statePath);
        using var restarted = fixture.Create();
        Assert.Equal("QueueInvalid", restarted.GetSnapshot().ErrorCode);
        Assert.True(restarted.GetSnapshot().IsPaused);
        await restarted.ReconcileAsync();
        Assert.Equal(before, File.ReadAllBytes(statePath));
    }

    [Fact]
    public async Task OversizedInputIsVisibleWithoutReadingOrRepeatedAttempts()
    {
        using var fixture = new FolderFixture();
        string source = Path.Combine(fixture.Input, "large.pdf");
        using (var sparse = File.Create(source)) sparse.SetLength(64L * 1024 * 1024 + 1);
        using var service = fixture.Create();
        service.Configure(fixture.Options());
        service.Resume();
        for (int i = 0; i < 4; i++)
        {
            await service.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        }
        WatchedFolderJob job = Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(WatchedFolderJobStatus.Failed, job.Status);
        Assert.Equal("InputTooLarge", job.ErrorCode);
        Assert.Equal(0, job.Attempts);
        Assert.Empty(Directory.GetFiles(fixture.Output));
    }

    [Fact]
    public async Task OfflineFoldersDoNotConsumeRetriesOrLoseTheEnabledSetting()
    {
        using var fixture = new FolderFixture();
        using var service = fixture.Create();
        service.Configure(fixture.Options());
        service.Resume();
        Directory.Move(fixture.Input, fixture.Input + "-offline");
        await service.ReconcileAsync();
        Assert.Equal("InputFolderUnavailable", service.GetSnapshot().ErrorCode);
        Assert.False(service.GetSnapshot().IsPaused);
        Directory.Move(fixture.Input + "-offline", fixture.Input);
        File.WriteAllBytes(Path.Combine(fixture.Input, "source.pdf"), new PdfDocumentBuilder().AddBlankPage().Build());
        Directory.Move(fixture.Output, fixture.Output + "-offline");
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();
        Assert.Equal("OutputFolderUnavailable", service.GetSnapshot().ErrorCode);
        Assert.Empty(service.GetSnapshot().Jobs);
        Directory.Move(fixture.Output + "-offline", fixture.Output);
        for (int i = 0; i < 3; i++) { await service.ReconcileAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(3)); }
        Assert.Equal(1, Assert.Single(service.GetSnapshot().Jobs).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAndDirectoryLinksCannotAliasTheSource(bool linkDirectory)
    {
        using var fixture = new FolderFixture();
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        string source = Path.Combine(fixture.Root, "outside.pdf");
        File.WriteAllBytes(source, original);
        using var service = fixture.Create();
        if (linkDirectory)
        {
            string alias = Path.Combine(fixture.Root, "alias");
            Directory.CreateSymbolicLink(alias, fixture.Input);
            Assert.Equal("ReparsePath", Assert.Throws<InvalidOperationException>(() =>
                service.Configure(fixture.Options() with { OutputFolder = alias })).Message);
        }
        else
        {
            File.CreateSymbolicLink(Path.Combine(fixture.Input, "linked.pdf"), source);
            service.Configure(fixture.Options());
            service.Resume();
            for (int i = 0; i < 3; i++) { await service.ReconcileAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(3)); }
            Assert.Equal("ReparsePath", service.GetSnapshot().ErrorCode);
            Assert.Empty(service.GetSnapshot().Jobs);
        }
        Assert.Empty(Directory.GetFiles(fixture.Output));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("collision")]
    public async Task ShutdownAndConcurrentOutputCreationKeepOriginalsAndResumeStateSafe(string action)
    {
        using var fixture = new FolderFixture();
        var builder = new PdfDocumentBuilder();
        for (int i = 0; i < 100; i++) builder.AddBlankPage();
        byte[] original = builder.Build();
        string source = Path.Combine(fixture.Input, "source.pdf");
        string output = Path.Combine(fixture.Output, "source.pdf");
        File.WriteAllBytes(source, original);
        var enteredResize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueResize = new ManualResetEventSlim();
        var dispatcher = new PdfMacroDispatcher([
            new(PdfMacroOperation.Resize, (step, bytes, values, token) =>
            {
                enteredResize.TrySetResult();
                continueResize.Wait(token);
                return PdfResizeMacro.Execute(step, bytes, values, token);
            })]);
        using (var service = new WatchedFolderService(fixture.Storage, dispatcher, fixture.Clock))
        {
            service.Configure(fixture.Options(new PdfMacro("Resize", [PdfResizeMacro.ScaleStep(100)])));
            service.Resume();
            await service.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            using var cancellation = new CancellationTokenSource();
            Task running = service.ReconcileAsync(cancellation.Token);
            try
            {
                await enteredResize.Task.WaitAsync(Hang);
                Assert.False(running.IsCompleted);
                Assert.Equal(WatchedFolderJobStatus.Processing, Assert.Single(service.GetSnapshot().Jobs).Status);
                if (action == "dispose") service.Dispose();
                if (action == "cancel") cancellation.Cancel();
                if (action == "collision") File.WriteAllBytes(output, [3, 6, 9]);
            }
            finally { continueResize.Set(); }
            await running;
        }
        using var restarted = fixture.Create();
        Assert.False(restarted.GetSnapshot().IsPaused);
        WatchedFolderJob job = Assert.Single(restarted.GetSnapshot().Jobs);
        Assert.Equal(action == "collision" ? WatchedFolderJobStatus.Failed : WatchedFolderJobStatus.Waiting, job.Status);
        Assert.Equal(action == "collision" ? "OutputExists" : "Canceled", job.ErrorCode);
        if (action == "collision") Assert.Equal([3, 6, 9], File.ReadAllBytes(output));
        else Assert.False(File.Exists(output));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task RetryingRenamedContentRetainsItsOriginalSequenceAndChangedBytesCreateANewJob()
    {
        using var fixture = new FolderFixture();
        string first = Path.Combine(fixture.Input, "first.pdf");
        File.WriteAllBytes(first, new PdfDocumentBuilder().AddBlankPage(200, 300).Build());
        string blocked = Path.Combine(fixture.Output, "copy-1.pdf");
        File.WriteAllBytes(blocked, [1]);
        using var service = fixture.Create();
        service.Configure(fixture.Options(new PdfMacro("Numbered", [PdfRenameMacro.RenameStep("copy-{index}")])));
        service.Resume();
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();
        string second = Path.Combine(fixture.Input, "second.pdf");
        File.WriteAllBytes(second, new PdfDocumentBuilder().AddBlankPage(300, 400).Build());
        for (int i = 0; i < 4; i++) { await service.ReconcileAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(3)); }
        string renamed = Path.Combine(fixture.Input, "renamed.pdf");
        File.Move(first, renamed);
        File.Delete(blocked);
        service.RetryFailed();
        for (int i = 0; i < 4; i++) { await service.ReconcileAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(3)); }
        WatchedFolderJob retried = Assert.Single(service.GetSnapshot().Jobs, job => job.Attempts == 2);
        Assert.Equal(renamed, retried.InputPath);
        Assert.Equal(blocked, retried.OutputPath);
        Assert.Equal(WatchedFolderJobStatus.Completed, retried.Status);
        File.WriteAllBytes(renamed, new PdfDocumentBuilder().AddBlankPage(400, 500).Build());
        for (int i = 0; i < 4; i++) { await service.ReconcileAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(3)); }
        Assert.Equal(3, service.GetSnapshot().Jobs.Count);
        Assert.True(File.Exists(Path.Combine(fixture.Output, "copy-3.pdf")));
    }

    [Fact]
    public async Task FullQueuePausesWithVisibleDurableGuidanceUntilAnExplicitNewSetup()
    {
        using var fixture = new FolderFixture();
        File.WriteAllBytes(Path.Combine(fixture.Input, "source.pdf"), new PdfDocumentBuilder().AddBlankPage().Build());
        using (var seed = fixture.Create())
        {
            seed.Configure(fixture.Options()); seed.Resume(); await seed.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3)); await seed.ReconcileAsync();
        }
        string path = Path.Combine(fixture.Storage, "queue.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var template = state["Jobs"]![0]!.DeepClone();
        var jobs = new System.Text.Json.Nodes.JsonArray();
        for (int i = 0; i < 2000; i++)
        {
            var job = template.DeepClone();
            job["Id"] = Guid.NewGuid(); job["Sequence"] = i + 1;
            job["InputHash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)));
            job["InputPath"] = Path.Combine(fixture.Input, $"old-{i}.pdf");
            job["OutputPath"] = Path.Combine(fixture.Output, $"old-{i}.pdf");
            jobs.Add(job);
        }
        state["Jobs"] = jobs;
        File.WriteAllText(path, state.ToJsonString());
        using (var service = fixture.Create())
        {
            Assert.Null(service.GetSnapshot().ErrorCode);
            await service.ReconcileAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(3)); await service.ReconcileAsync();
            Assert.True(service.GetSnapshot().IsPaused);
            Assert.Equal("QueueFull", service.GetSnapshot().ErrorCode);
            Assert.Equal(2000, service.GetSnapshot().Jobs.Count);
        }
        using var restarted = fixture.Create();
        Assert.Equal("QueueFull", restarted.GetSnapshot().ErrorCode);
        restarted.Configure(fixture.Options(new PdfMacro("New task", [PdfRenameMacro.RenameStep("new-{index}")])));
        Assert.Empty(restarted.GetSnapshot().Jobs);
        Assert.Null(restarted.GetSnapshot().ErrorCode);
    }

    [Fact]
    public void AnAlternateWindowsPathCannotMakeTheInputFolderItsOwnOutput()
    {
        using var fixture = new FolderFixture();
        using var service = fixture.Create();
        Assert.Equal("InvalidFolders", Assert.Throws<InvalidOperationException>(() => service.Configure(
            fixture.Options() with { OutputFolder = @"\\?\" + fixture.Input })).Message);
    }

    [Theory]
    [InlineData("null-setting")]
    [InlineData("null-step")]
    [InlineData("unknown-field")]
    public void MalformedMacroFieldsFailWithAStableCodeBeforePersistence(string corruption)
    {
        using var fixture = new FolderFixture();
        var macro = System.Text.Json.Nodes.JsonNode.Parse(fixture.Options().MacroJson)!;
        if (corruption == "null-setting") macro["steps"]![0]!["settings"] = new System.Text.Json.Nodes.JsonObject { ["unused"] = null };
        if (corruption == "null-step") macro["steps"]![0] = null;
        if (corruption == "unknown-field") macro["parameterPrompt"] = "unresolved";
        using var service = fixture.Create();
        Assert.Equal("InvalidMacro", Assert.Throws<InvalidOperationException>(() => service.Configure(
            fixture.Options() with { MacroJson = macro.ToJsonString() })).Message);
        Assert.Null(service.GetSnapshot().Options);
        Assert.False(File.Exists(Path.Combine(fixture.Storage, "queue.json")));
    }

    [Fact]
    public async Task RemovingAnEntireObservationWindowDoesNotStarveLaterArrivals()
    {
        using var fixture = new FolderFixture();
        for (int i = 0; i < 2000; i++) File.WriteAllBytes(Path.Combine(fixture.Input, $"copy-{i:D4}.pdf"), [1]);
        using var service = fixture.Create();
        service.Configure(fixture.Options()); service.Resume();
        for (int i = 0; i < 8; i++) await service.ReconcileAsync();
        foreach (string file in Directory.GetFiles(fixture.Input)) File.Delete(file);
        File.WriteAllBytes(Path.Combine(fixture.Input, "later.pdf"), new PdfDocumentBuilder().AddBlankPage().Build());
        for (int i = 0; i < 20; i++)
        {
            await service.ReconcileAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(WatchedFolderJobStatus.Completed, Assert.Single(service.GetSnapshot().Jobs).Status);
        Assert.True(File.Exists(Path.Combine(fixture.Output, "later.pdf")));
    }

    [Fact]
    public async Task LosingJournalAccessAfterProcessingStartsPreventsPublishingTheResult()
    {
        using var fixture = new FolderFixture();
        var builder = new PdfDocumentBuilder();
        for (int i = 0; i < 100; i++) builder.AddBlankPage();
        File.WriteAllBytes(Path.Combine(fixture.Input, "source.pdf"), builder.Build());
        using var service = fixture.Create();
        service.Configure(fixture.Options(new PdfMacro("Long resize",
            Enumerable.Repeat(PdfResizeMacro.ScaleStep(100), 100))));
        service.Resume();
        await service.ReconcileAsync(); fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Task running = service.ReconcileAsync();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!service.GetSnapshot().Jobs.Any(job => job.Status == WatchedFolderJobStatus.Processing))
        {
            Assert.False(running.IsCompleted); Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(1);
        }
        using var held = File.Open(Path.Combine(fixture.Storage, "queue.json"), FileMode.Open,
            FileAccess.Read, FileShare.None);
        await running;
        Assert.Equal("QueueWriteFailed", service.GetSnapshot().ErrorCode);
        Assert.True(service.GetSnapshot().IsPaused);
        Assert.Empty(Directory.GetFiles(fixture.Output));
    }

    [Theory]
    [InlineData(220, false)]
    [InlineData(255, false)]
    [InlineData(220, true)]
    [InlineData(255, true)]
    public async Task LongValidPdfNamesCanBePublishedWithoutGrowingTheStagingComponent(int nameLength, bool extendedInputPath)
    {
        using var fixture = new FolderFixture();
        string name = new string('a', nameLength - 4) + ".pdf";
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        string source = Path.Combine(fixture.Input, name);
        File.WriteAllBytes(source, original);
        using var service = fixture.Create();
        service.Configure(fixture.Options() with
            { InputFolder = extendedInputPath ? @"\\?\" + fixture.Input : fixture.Input });
        service.Resume();
        await service.ReconcileAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();
        WatchedFolderJob job = Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(WatchedFolderJobStatus.Completed, job.Status);
        Assert.Equal(Path.Combine(fixture.Output, name), job.OutputPath);
        Assert.Equal(original, File.ReadAllBytes(job.OutputPath));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Single(Directory.GetFiles(fixture.Output));
    }

    [Fact]
    public async Task JournalCapacityPreservesTheLastReadableStateAndAllowsAnExplicitNewSetup()
    {
        using var fixture = new FolderFixture();
        File.WriteAllBytes(Path.Combine(fixture.Input, "seed.pdf"), new PdfDocumentBuilder().AddBlankPage().Build());
        using (var seed = fixture.Create())
        {
            seed.Configure(fixture.Options()); seed.Resume(); await seed.ReconcileAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(3)); await seed.ReconcileAsync();
        }
        string statePath = Path.Combine(fixture.Storage, "queue.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
        string longRoot = fixture.Root;
        for (int i = 0; i < 3; i++) longRoot = Path.Combine(longRoot, new string('\u65E5', 150));
        string longInput = @"\\?\" + Path.Combine(longRoot, "input"), longOutput = @"\\?\" + Path.Combine(longRoot, "output");
        Directory.CreateDirectory(longInput); Directory.CreateDirectory(longOutput);
        state["Options"]!["InputFolder"] = longInput;
        state["Options"]!["OutputFolder"] = longOutput;
        var template = state["Jobs"]![0]!.DeepClone();
        var jobs = new System.Text.Json.Nodes.JsonArray();
        for (int i = 0; i < 2000; i++)
        {
            string name = new string('\u672C', 200) + i.ToString("D4") + ".pdf";
            var job = template.DeepClone();
            job["Id"] = Guid.NewGuid(); job["Sequence"] = i + 1;
            job["InputHash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)));
            job["InputPath"] = Path.Combine(longInput, name);
            job["OutputPath"] = Path.Combine(longOutput, name);
            job["OriginalFileName"] = name;
            jobs.Add(job);
        }
        state["Jobs"] = jobs;
        var compactUnicode = new System.Text.Json.JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(statePath, state.ToJsonString(compactUnicode));
        byte[] readable = File.ReadAllBytes(statePath);
        Assert.InRange(readable.Length, 1, 16 * 1024 * 1024);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(state.ToJsonString()) > 16 * 1024 * 1024);
        using (var service = fixture.Create())
        {
            Assert.Null(service.GetSnapshot().ErrorCode);
            Assert.Equal(2000, service.GetSnapshot().Jobs.Count);
            Assert.Equal("QueueFull", Assert.Throws<InvalidOperationException>(service.Pause).Message);
            Assert.True(service.GetSnapshot().IsPaused);
            Assert.Equal("QueueFull", service.GetSnapshot().ErrorCode);
            Assert.Equal(readable, File.ReadAllBytes(statePath));
            service.Configure(fixture.Options());
            Assert.Empty(service.GetSnapshot().Jobs);
            Assert.Null(service.GetSnapshot().ErrorCode);
        }
        using var restarted = fixture.Create();
        Assert.Null(restarted.GetSnapshot().ErrorCode);
        Assert.True(restarted.GetSnapshot().IsPaused);
        Assert.Empty(restarted.GetSnapshot().Jobs);
        Assert.Equal(2, Directory.GetFiles(fixture.Storage, "*.json").Length);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public int ReadCount { get; private set; }
        public override DateTimeOffset GetUtcNow() { ReadCount++; return _now; }
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class FolderFixture : IDisposable
    {
        // The system temp folder, so the tests run on any machine and account.
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "killerpdf-watched-folders",
            "service-tests-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Root, "input");
        public string Output => Path.Combine(Root, "output");
        public string Storage => Path.Combine(Root, "state");
        public TestClock Clock { get; } = new();
        public FolderFixture()
        {
            Directory.CreateDirectory(Input);
            Directory.CreateDirectory(Output);
        }
        public WatchedFolderService Create() => new(Storage, clock: Clock);
        public WatchedFolderOptions Options(PdfMacro? macro = null) => new(Input, Output, "*.pdf",
            (macro ?? new PdfMacro("Copy", [new(PdfMacroOperation.Save)])).ToJson());
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
