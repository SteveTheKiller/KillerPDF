using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Documents;
using Microsoft.Win32.SafeHandles;

namespace KillerPDF.Services;

public sealed class WatchedFolderService : IDisposable
{
    private const long MaximumFileBytes = 64 * 1024 * 1024;
    private const int MaximumJobs = 2000;
    private const int MaximumJournalBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JournalJson = new()
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _idle = new(true);
    private CancellationTokenSource? _active;
    private bool _running;
    private bool _disposed;
    private bool _blocked;
    private string? _errorCode;
    private FileStream? _owner;
    private DirectoryLease? _storageLease;
    private WatchedFolderOptions? _options;
    private bool _paused = true;
    private Guid _setupId = Guid.NewGuid();
    private readonly PdfMacroDispatcher _dispatcher;
    private readonly Func<PdfMacro, WatchedFolderReadinessIssue?>? _readiness;
    private readonly TimeProvider _clock;
    private readonly string _statePath;
    private readonly List<JobState> _jobs = [];
    private readonly Dictionary<string, Observation> _observations = new(StringComparer.OrdinalIgnoreCase);
    private IEnumerator<string>? _scan;
    private int _observationCursor;

    public WatchedFolderService(string storageDirectory, PdfMacroDispatcher? dispatcher = null,
        TimeProvider? clock = null, Func<PdfMacro, WatchedFolderReadinessIssue?>? readiness = null)
    {
        _statePath = Path.Combine(Path.GetFullPath(storageDirectory), "queue.json");
        _clock = clock ?? TimeProvider.System;
        _readiness = readiness;
        _dispatcher = dispatcher ?? new PdfMacroDispatcher([
            new(PdfMacroOperation.Resize, PdfResizeMacro.Execute),
            new(PdfMacroOperation.ConvertColor, WorkingCmykProfiles.ConvertColor),
            new(PdfMacroOperation.Rename, PdfRenameMacro.Execute),
            new(PdfMacroOperation.Save, (_, source, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return new(source);
            })]);
        try
        {
            RejectReparseAncestors(storageDirectory);
            Directory.CreateDirectory(storageDirectory);
            _storageLease = DirectoryLease.Acquire(storageDirectory);
            RejectReparseAncestors(Path.Combine(storageDirectory, "owner.lock"));
            RejectReparseAncestors(_statePath);
        }
        catch (InvalidOperationException) { Block("ReparsePath"); return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Block("StorageUnavailable");
            return;
        }
        try { _owner = new FileStream(Path.Combine(storageDirectory, "owner.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { Block("AlreadyOwned"); return; }
        catch (UnauthorizedAccessException) { Block("StorageUnavailable"); return; }
        LoadState();
    }

    public void Configure(WatchedFolderOptions options)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWritable();
            if (!_paused || _running) throw new InvalidOperationException("ConfigurationBusy");
            WatchedFolderOptions validated = ValidateOptions(options);
            RequireReadiness(validated.MacroJson);
            if (_options is not null && !SameSetup(_options, validated))
            {
                ArchiveState();
                _jobs.Clear();
                _setupId = Guid.NewGuid();
            }
            _options = validated;
            _errorCode = null;
            _observations.Clear();
            _scan?.Dispose();
            _scan = null;
            SaveState();
        }
    }
    public void Pause()
    {
        lock (_gate) { _paused = true; _active?.Cancel(); if (!_blocked && !_disposed) SaveState(); }
    }
    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWritable();
            if (_options is null) throw new InvalidOperationException("NotConfigured");
            if (_running) throw new InvalidOperationException("ConfigurationBusy");
            RequireReadiness(_options.MacroJson);
            _errorCode = null;
            _paused = false;
            SaveState();
        }
    }
    public void RetryFailed()
    {
        lock (_gate)
        {
            EnsureWritable();
            if (_running) throw new InvalidOperationException("ConfigurationBusy");
            if (_options is not null) RequireReadiness(_options.MacroJson);
            foreach (JobState job in _jobs.Where(job => job.Status == WatchedFolderJobStatus.Failed))
            {
                job.Status = WatchedFolderJobStatus.Waiting;
                job.ErrorCode = null;
                job.FailedStepIndex = null;
                job.FailedOperation = null;
            }
            SaveState();
        }
    }
    public WatchedFolderReadinessIssue? InspectReadiness(PdfMacro macro) => _readiness?.Invoke(macro);

    private void RequireReadiness(string macroJson)
    {
        if (InspectReadiness(PdfMacro.FromJson(macroJson)) is not null)
            throw new InvalidOperationException("OcrModelsMissing");
    }

    public WatchedFolderSnapshot GetSnapshot()
    {
        lock (_gate) return new(_options, _paused,
            _jobs.Select(job => new WatchedFolderJob(job.Id, job.InputPath, job.OutputPath,
                job.Status, job.Attempts, job.ErrorCode)
                { FailedStepIndex = job.FailedStepIndex, FailedOperation = job.FailedOperation }).ToArray(), _errorCode);
    }
    public Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _paused || _running || _blocked || _options is null) return Task.CompletedTask;
            if (InspectReadiness(PdfMacro.FromJson(_options.MacroJson)) is not null)
            {
                _paused = true;
                _errorCode = "OcrModelsMissing";
                SaveState();
                return Task.CompletedTask;
            }
            _running = true;
            _idle.Reset();
            _active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _active.Token;
        }
        return Task.Run(() =>
        {
            try { ReconcileCore(token); }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    foreach (JobState job in _jobs.Where(job => job.Status == WatchedFolderJobStatus.Processing))
                    {
                        job.Status = WatchedFolderJobStatus.Waiting;
                        job.ErrorCode = "Canceled";
                    }
                    if (!_blocked) SaveState();
                }
            }
            catch (InvalidOperationException) when (_blocked || _errorCode == "QueueFull") { }
            catch (InvalidOperationException error) when (error.Message == "ReparsePath")
            { lock (_gate) _errorCode = "ReparsePath"; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                lock (_gate) _errorCode = "InputFolderUnavailable";
                _scan?.Dispose();
                _scan = null;
            }
            finally
            {
                lock (_gate)
                {
                    _active?.Dispose();
                    _active = null;
                    _running = false;
                    _idle.Set();
                }
            }
        });
    }

    private void ReconcileCore(CancellationToken cancellationToken)
    {
        if (_paused || _options is null) return;
        lock (_gate) _errorCode = null;
        if (!Directory.Exists(_options.InputFolder))
        { lock (_gate) _errorCode = "InputFolderUnavailable"; return; }
        if (!Directory.Exists(_options.OutputFolder))
        { lock (_gate) _errorCode = "OutputFolderUnavailable"; return; }
        using DirectoryLease inputLease = DirectoryLease.Acquire(_options.InputFolder);
        using DirectoryLease outputLease = DirectoryLease.Acquire(_options.OutputFolder);
        if (inputLease.Identity == outputLease.Identity)
        { lock (_gate) _errorCode = "InvalidFolders"; return; }
        PruneMissingObservations();
        if (RecoverPublication(cancellationToken)) return;
        int captures = 0;
        foreach (string path in ScanPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_paused) return;
            if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)
                || !FileSystemName.MatchesSimpleExpression(_options.FilePattern, Path.GetFileName(path), ignoreCase: true)
                || Directory.Exists(path)) continue;
            byte[]? bytes = ReadStableInput(path, cancellationToken);
            if (bytes is null) continue;
            captures++;
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            JobState? job;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                job = _jobs.FirstOrDefault(job => job.InputHash == hash);
                if (job is not null && job.Status != WatchedFolderJobStatus.Waiting)
                {
                    if (_observations.Count >= MaximumJobs) _observations.Remove(path);
                    if (captures >= 4) return;
                    continue;
                }
                if (job is null)
                {
                    if (!CanAddJob()) return;
                    job = new JobState { InputPath = path, InputHash = hash,
                        OriginalFileName = Path.GetFileName(path), Sequence = _jobs.Count + 1 };
                    _jobs.Add(job);
                }
                job.InputPath = path;
                job.Status = WatchedFolderJobStatus.Processing;
                job.ErrorCode = null;
                job.FailedStepIndex = null;
                job.FailedOperation = null;
                job.Attempts++;
                SaveState();
            }
            string? inputError;
            try
            {
                PdfDocument document = PdfDocument.Open(bytes);
                inputError = document.IsEncrypted ? "EncryptedPdf"
                    : PdfPageInformation.Read(document).Count == 0 ? "InvalidPdf" : null;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException
                and not AccessViolationException)
            {
                inputError = "InvalidPdf";
            }
            if (inputError is not null) { Fail(job, inputError); return; }
            PdfMacro macro = PdfMacro.FromJson(_options.MacroJson);
            PdfMacroRunReport report = _dispatcher.RunReport(macro, [new ReadOnlyMemory<byte>(bytes)],
                PdfRenameMacro.CreateContext(job.OriginalFileName, job.Sequence), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PdfMacroFileResult result = report.Results.Single();
            if (!result.Succeeded || result.Data is not ReadOnlyMemory<byte> data)
            {
                int? failedStep = result.FailedStepIndex is int index && index >= 0 && index < macro.Steps.Count
                    ? index : null;
                Fail(job, "MacroFailed", failedStep,
                    failedStep is int step ? macro.Steps[step].Operation : null);
                return;
            }
            if (data.Length > MaximumFileBytes) { Fail(job, "OutputWriteFailed"); return; }
            string outputName = result.OutputFileName ?? job.OriginalFileName;
            if (!ValidFileName(outputName)) { Fail(job, "OutputWriteFailed"); return; }
            lock (_gate)
            {
                job.OutputPath = Path.Combine(_options.OutputFolder, outputName);
                job.OutputHash = Convert.ToHexString(SHA256.HashData(data.Span));
            }
            string temporary = Path.Combine(_options.OutputFolder, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(data.Span);
                    output.Flush(flushToDisk: true);
                }
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    job.PublicationPending = true;
                    SaveState();
                    File.Move(temporary, job.OutputPath, overwrite: false);
                    job.Status = WatchedFolderJobStatus.Completed;
                    job.PublicationPending = false;
                    SaveState();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Fail(job, File.Exists(job.OutputPath) ? "OutputExists" : "OutputWriteFailed");
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return;
        }
    }

    private bool RecoverPublication(CancellationToken token)
    {
        JobState? job = _jobs.FirstOrDefault(job => job.PublicationPending
            || job.Status == WatchedFolderJobStatus.Processing);
        if (job is null) return false;
        if (!job.PublicationPending)
        {
            lock (_gate) { job.Status = WatchedFolderJobStatus.Waiting; SaveState(); }
            return false;
        }
        try
        {
            using var output = OpenInput(job.OutputPath, FileShare.Read);
            token.ThrowIfCancellationRequested();
            bool matches = output.Length <= MaximumFileBytes
                && Convert.ToHexString(SHA256.HashData(output)) == job.OutputHash;
            lock (_gate)
            {
                job.PublicationPending = false;
                if (matches)
                {
                    job.Status = WatchedFolderJobStatus.Completed;
                    job.ErrorCode = null;
                    SaveState();
                }
                else Fail(job, "RecoveryMismatch");
            }
            return true;
        }
        catch (FileNotFoundException)
        {
            lock (_gate)
            {
                job.PublicationPending = false;
                job.Status = WatchedFolderJobStatus.Waiting;
                SaveState();
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _errorCode = "OutputFolderUnavailable";
            return true;
        }
    }

    private IEnumerable<string> ScanPaths()
    {
        bool restarted = false;
        for (int count = 0; count < 256; count++)
        {
            _scan ??= Directory.EnumerateFileSystemEntries(_options!.InputFolder).GetEnumerator();
            if (!_scan.MoveNext())
            {
                _scan.Dispose();
                _scan = null;
                if (count != 0 || restarted) yield break;
                restarted = true;
                count--;
                continue;
            }
            yield return _scan.Current;
        }
    }

    private void PruneMissingObservations()
    {
        string[] paths = _observations.Keys.ToArray();
        if (paths.Length == 0) return;
        int count = Math.Min(256, paths.Length);
        for (int i = 0; i < count; i++)
        {
            string path = paths[(_observationCursor + i) % paths.Length];
            if (!File.Exists(path)) _observations.Remove(path);
        }
        _observationCursor = (_observationCursor + count) % paths.Length;
    }

    private byte[]? ReadStableInput(string path, CancellationToken token)
    {
        try
        {
            RejectReparseAncestors(path);
            var info = new FileInfo(path);
            var observation = new Observation(info.Length, info.LastWriteTimeUtc, _clock.GetUtcNow());
            if (!_observations.ContainsKey(path) && _observations.Count >= MaximumJobs) return null;
            if (!_observations.TryGetValue(path, out Observation? previous)
                || previous.Length != observation.Length || previous.Modified != observation.Modified)
            {
                _observations[path] = observation;
                return null;
            }
            if (_clock.GetUtcNow() - previous.Seen < TimeSpan.FromSeconds(2)) return null;
            using var input = OpenInput(path, FileShare.None);
            if (input.Length != observation.Length || File.GetLastWriteTimeUtc(path) != observation.Modified)
            {
                _observations.Remove(path);
                lock (_gate) _errorCode = "InputChanged";
                return null;
            }
            if (input.Length > MaximumFileBytes)
            {
                lock (_gate)
                {
                    JobState? rejected = _jobs.FirstOrDefault(job => job.InputHash.Length == 0
                        && string.Equals(job.InputPath, path, StringComparison.OrdinalIgnoreCase)
                        && job.InputLength == observation.Length && job.InputModified == observation.Modified);
                    if (rejected is null)
                    {
                        if (!CanAddJob()) return null;
                        rejected = new JobState { InputPath = path, InputLength = observation.Length,
                            InputModified = observation.Modified, OriginalFileName = Path.GetFileName(path),
                            Sequence = _jobs.Count + 1 };
                        _jobs.Add(rejected);
                    }
                    if (rejected.Status != WatchedFolderJobStatus.Failed) Fail(rejected, "InputTooLarge");
                }
                return null;
            }
            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            token.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (FileNotFoundException) { lock (_gate) _errorCode = "InputMissing"; }
        catch (InvalidOperationException error) when (error.Message == "ReparsePath")
        { lock (_gate) _errorCode = "ReparsePath"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _observations.Remove(path);
            lock (_gate) _errorCode = "InputBusy";
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active?.Cancel();
        }
        _idle.Wait();
        _scan?.Dispose();
        _owner?.Dispose();
        _storageLease?.Dispose();
    }

    private WatchedFolderOptions ValidateOptions(WatchedFolderOptions options, bool requireFolders = true)
    {
        string input;
        string output;
        try
        {
            input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.InputFolder));
            output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.OutputFolder));
            RejectReparseAncestors(input);
            RejectReparseAncestors(output);
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)
                || (requireFolders && (!Directory.Exists(input) || !Directory.Exists(output))))
                throw new InvalidOperationException("InvalidFolders");
            if (Directory.Exists(input) && Directory.Exists(output))
            {
                using DirectoryLease inputLease = DirectoryLease.Acquire(input);
                using DirectoryLease outputLease = DirectoryLease.Acquire(output);
                if (inputLease.Identity == outputLease.Identity) throw new InvalidOperationException("InvalidFolders");
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            throw new InvalidOperationException("InvalidFolders");
        }
        if (string.IsNullOrWhiteSpace(options.FilePattern) || options.FilePattern.Length > 255
            || options.FilePattern.Any(character => character < 32 || "<>:\"/\\|".Contains(character)))
            throw new InvalidOperationException("InvalidPattern");
        ValidateMacro(options.MacroJson);
        return options with { InputFolder = input, OutputFolder = output };
    }

    private void ValidateMacro(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_000_000)
            throw new InvalidOperationException("InvalidMacro");
        try
        {
            using JsonDocument content = JsonDocument.Parse(json);
            if (ContainsSecretField(content.RootElement))
            {
                if (SecretStepIssue(content.RootElement) is { } issue)
                    throw StepProblem("InteractiveParameter", issue.StepIndex, issue.Operation);
                throw new InvalidOperationException("InteractiveParameter");
            }
            ValidateMacroShape(content.RootElement);
            PdfMacro macro = PdfMacro.FromJson(json);
            if (macro.Steps.Count > 100) throw new InvalidOperationException("InvalidMacro");
            for (int index = 0; index < macro.Steps.Count; index++)
            {
                PdfMacroStep step = macro.Steps[index];
                if (step.Operation is PdfMacroOperation.Redact or PdfMacroOperation.DataMerge or PdfMacroOperation.Export
                    || !_dispatcher.CanExecute(step.Operation))
                    throw StepProblem("UnsupportedOperation", index, step.Operation);
                if (step.Settings?.Values.Any(value => value.Contains("${", StringComparison.Ordinal)) == true)
                    throw StepProblem("InteractiveParameter", index, step.Operation);
                switch (step.Operation)
                {
                    case PdfMacroOperation.Resize: _ = PdfResizeMacro.ReadPercent(step); break;
                    case PdfMacroOperation.ConvertColor: _ = PdfColorConversionMacro.ReadOptions(step); break;
                    case PdfMacroOperation.Rename: _ = PdfRenameMacro.ReadTemplate(step); break;
                    case PdfMacroOperation.Optimize:
                    case PdfMacroOperation.Save:
                        if (step.Settings is { Count: > 0 }) throw new InvalidOperationException("InvalidMacro");
                        break;
                    case PdfMacroOperation.Ocr:
                        foreach ((string name, string value) in step.Settings ?? new Dictionary<string, string>())
                        {
                            bool valid = name switch
                            {
                                "language" => value.Length is > 0 and <= 128 && value.Split('+').All(code =>
                                    code.Length is > 0 and <= 24 && code.All(c => c is >= 'a' and <= 'z' or '_')),
                                "formAware" => bool.TryParse(value, out _),
                                _ => false
                            };
                            if (!valid) throw new InvalidOperationException("InvalidMacro");
                        }
                        break;
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or JsonException or NotSupportedException
            or FormatException)
        {
            throw new InvalidOperationException("InvalidMacro");
        }
    }

    private static void ValidateMacroShape(JsonElement root)
    {
        CheckProperties(root, ["version", "name", "steps"]);
        JsonElement steps = root.EnumerateObject().First(property =>
            property.Name.Equals("steps", StringComparison.OrdinalIgnoreCase)).Value;
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > 100)
            throw new InvalidOperationException("InvalidMacro");
        foreach (JsonElement step in steps.EnumerateArray())
        {
            CheckProperties(step, ["operation", "settings"], "settings");
            foreach (JsonProperty property in step.EnumerateObject())
            {
                if (!property.Name.Equals("settings", StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind == JsonValueKind.Null) continue;
                if (property.Value.ValueKind != JsonValueKind.Object
                    || property.Value.EnumerateObject().Any(setting => setting.Value.ValueKind != JsonValueKind.String))
                    throw new InvalidOperationException("InvalidMacro");
            }
        }
    }

    private static void CheckProperties(JsonElement element, string[] allowed, string? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("InvalidMacro");
        string[] names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length
            || names.Any(name => !allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
            || allowed.Any(name => name != optional && !names.Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("InvalidMacro");
    }

    private const string StepIssueKey = "KillerPDF.WatchedFolderStepIssue";

    private static InvalidOperationException StepProblem(string code, int index, PdfMacroOperation operation)
    {
        var error = new InvalidOperationException(code);
        error.Data[StepIssueKey] = new WatchedFolderStepIssue(index, operation);
        return error;
    }

    public static WatchedFolderStepIssue? ConfigurationIssueOf(Exception error) =>
        error.Data[StepIssueKey] is WatchedFolderStepIssue issue ? issue : null;

    private static WatchedFolderStepIssue? SecretStepIssue(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!property.Name.Equals("steps", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Array) continue;
            int index = 0;
            foreach (JsonElement step in property.Value.EnumerateArray())
            {
                if (index >= 100) return null;
                if (step.ValueKind == JsonValueKind.Object && ContainsSecretField(step))
                    foreach (JsonProperty field in step.EnumerateObject())
                        if (field.Name.Equals("operation", StringComparison.OrdinalIgnoreCase)
                            && field.Value.ValueKind == JsonValueKind.String
                            && Enum.TryParse(field.Value.GetString(), true, out PdfMacroOperation operation)
                            && Enum.IsDefined(operation)) return new(index, operation);
                index++;
            }
        }
        return null;
    }

    private static bool ContainsSecretField(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Array) return item.EnumerateArray().Any(ContainsSecretField);
        if (item.ValueKind != JsonValueKind.Object) return false;
        return item.EnumerateObject().Any(property =>
            new[] { "password", "secret", "credential", "token" }.Any(word =>
                property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)) || ContainsSecretField(property.Value));
    }

    private void Fail(JobState job, string code, int? failedStepIndex = null, PdfMacroOperation? failedOperation = null)
    {
        lock (_gate)
        {
            job.Status = WatchedFolderJobStatus.Failed;
            job.PublicationPending = false;
            job.ErrorCode = code;
            job.FailedStepIndex = failedStepIndex;
            job.FailedOperation = failedOperation;
            if (_options?.PauseOnError == true) _paused = true;
            SaveState();
        }
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_blocked) throw new InvalidOperationException(_errorCode);
    }

    private void Block(string code)
    {
        _errorCode = code;
        _blocked = true;
        _paused = true;
        _active?.Cancel();
    }

    private void LoadState()
    {
        try
        {
            using var input = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumJournalBytes) { Block("QueueInvalid"); return; }
            PersistedState? state = JsonSerializer.Deserialize<PersistedState>(input, JournalJson);
            if (state is null || state.Version != 1 || state.SetupId == Guid.Empty
                || state.Jobs is null || state.Jobs.Count > MaximumJobs
                || (state.ErrorCode is not null and not "QueueFull")
                || (state.ErrorCode == "QueueFull" && !state.IsPaused)
                || (state.Options is null && state.Jobs.Count != 0)
                || state.Jobs.Select(job => job.Id).Distinct().Count() != state.Jobs.Count
                || state.Jobs.Select(job => job.Sequence).Distinct().Count() != state.Jobs.Count
                || state.Jobs.Where(job => job.InputHash.Length != 0).Select(job => job.InputHash)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Jobs.Count(job => job.InputHash.Length != 0)
                || state.Jobs.Any(job => !ValidJob(job, state.Options!)))
            { Block("QueueInvalid"); return; }
            _options = state.Options is null ? null : ValidateOptions(state.Options, requireFolders: false);
            _setupId = state.SetupId;
            _paused = state.IsPaused;
            _errorCode = state.ErrorCode;
            _jobs.AddRange(state.Jobs);
        }
        catch (FileNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Block("QueueUnreadable"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException
            or NullReferenceException) { Block("QueueInvalid"); }
    }

    private void SaveState()
    {
        if (_blocked) throw new InvalidOperationException(_errorCode);
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedState(1, _setupId, _options, _paused, _jobs, DurableError()));
            if (bytes.Length > MaximumJournalBytes)
            {
                _errorCode = "QueueFull";
                _paused = true;
                throw new InvalidOperationException("QueueFull");
            }
            AtomicFileOutput.Write(_statePath, bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Block("QueueWriteFailed");
            throw new InvalidOperationException("QueueWriteFailed");
        }
    }

    private bool CanAddJob()
    {
        if (_jobs.Count < MaximumJobs) return true;
        _errorCode = "QueueFull";
        _paused = true;
        SaveState();
        return false;
    }

    private static bool SameSetup(WatchedFolderOptions first, WatchedFolderOptions second) =>
        string.Equals(first.InputFolder, second.InputFolder, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.OutputFolder, second.OutputFolder, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.FilePattern, second.FilePattern, StringComparison.OrdinalIgnoreCase)
        && first.MacroJson == second.MacroJson;

    private void ArchiveState()
    {
        try
        {
            string archive = Path.Combine(Path.GetDirectoryName(_statePath)!, $"history-{_setupId:N}-{Guid.NewGuid():N}.json");
            using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, new PersistedState(1, _setupId, _options, _paused, _jobs, DurableError()));
            output.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Block("QueueWriteFailed"); throw new InvalidOperationException("QueueWriteFailed"); }
    }

    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private string? DurableError() => _errorCode == "QueueFull" ? _errorCode : null;
    private static bool IsDirectChild(string path, string folder) => Path.IsPathFullyQualified(path)
        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
    private static bool ValidFileName(string name) => !string.IsNullOrWhiteSpace(name)
        && name.Length <= 255 && name == Path.GetFileName(name)
        && !name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character))
        && string.Equals(Path.GetExtension(name), ".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool ValidJob(JobState job, WatchedFolderOptions options) => job.Id != Guid.Empty
        && job.Sequence is > 0 and <= MaximumJobs && job.Attempts >= 0 && Enum.IsDefined(job.Status)
        && (ValidHash(job.InputHash) || (job.InputHash.Length == 0 && job.InputLength > MaximumFileBytes
            && job.Status is WatchedFolderJobStatus.Failed or WatchedFolderJobStatus.Waiting))
        && ValidFileName(job.OriginalFileName) && IsDirectChild(job.InputPath, options.InputFolder)
        && (job.OutputPath.Length == 0 || (IsDirectChild(job.OutputPath, options.OutputFolder)
            && ValidFileName(Path.GetFileName(job.OutputPath))))
        && (job.OutputHash.Length == 0 || ValidHash(job.OutputHash))
        && (!job.PublicationPending || (job.OutputPath.Length != 0 && ValidHash(job.OutputHash)))
        && (job.ErrorCode is null or "InputTooLarge" or "InvalidPdf" or "EncryptedPdf" or "MacroFailed"
            or "OutputExists" or "OutputWriteFailed" or "Canceled" or "RecoveryMismatch")
        && ValidFailureDetail(job, options)
        && (job.Status != WatchedFolderJobStatus.Completed || (job.OutputPath.Length != 0 && ValidHash(job.OutputHash)));

    private static bool ValidFailureDetail(JobState job, WatchedFolderOptions options)
    {
        if (job.FailedStepIndex is null && job.FailedOperation is null) return true;
        if (job.Status != WatchedFolderJobStatus.Failed || job.ErrorCode != "MacroFailed"
            || job.FailedStepIndex is not int index || index < 0 || index >= 100
            || job.FailedOperation is not PdfMacroOperation operation || !Enum.IsDefined(operation)) return false;
        PdfMacro macro = PdfMacro.FromJson(options.MacroJson);
        return index < macro.Steps.Count && macro.Steps[index].Operation == operation;
    }

    private sealed record PersistedState([property: JsonRequired] int Version,
        [property: JsonRequired] Guid SetupId, [property: JsonRequired] WatchedFolderOptions? Options,
        [property: JsonRequired] bool IsPaused, [property: JsonRequired] List<JobState> Jobs,
        [property: JsonRequired] string? ErrorCode);

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("ReparsePath");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static FileStream OpenInput(string path, FileShare share)
    {
        SafeFileHandle handle = OpenHandle(path, 0x80000000, (uint)share, 0x00200000);
        try
        {
            RejectReparseHandle(handle);
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenHandle(string path, uint access, uint share, uint flags)
    {
        string nativePath = Path.GetFullPath(path);
        if (!nativePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            nativePath = nativePath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + nativePath[2..] : @"\\?\" + nativePath;
        SafeFileHandle handle = CreateFile(nativePath, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw error switch
        {
            2 => new FileNotFoundException(),
            3 => new DirectoryNotFoundException(),
            5 => new UnauthorizedAccessException(),
            _ => new IOException()
        };
    }

    private static void RejectReparseHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out HandleInformation info)) throw new IOException();
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("ReparsePath");
    }

    // Holding every ancestor without delete sharing prevents a directory swap between
    // validation and publication. The captured input handle also refuses links atomically.
    private sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        public (uint Volume, uint High, uint Low) Identity { get; private set; }
        public static DirectoryLease Acquire(string directory)
        {
            var paths = new Stack<string>();
            for (string? current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                current is not null; current = Path.GetDirectoryName(current)) paths.Push(current);
            var lease = new DirectoryLease();
            try
            {
                foreach (string path in paths)
                {
                    SafeFileHandle handle = OpenHandle(path, 0, (uint)(FileShare.Read | FileShare.Write), 0x02200000);
                    lease._handles.Add(handle);
                    RejectReparseHandle(handle);
                    if (!GetFileInformationByHandle(handle, out HandleInformation info)) throw new IOException();
                    lease.Identity = (info.Volume, info.IndexHigh, info.IndexLow);
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (SafeFileHandle handle in _handles) handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation info);

    private sealed record Observation(long Length, DateTime Modified, DateTimeOffset Seen);
    private sealed class JobState
    {
        [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
        [JsonRequired] public string InputPath { get; set; } = "";
        [JsonRequired] public string InputHash { get; set; } = "";
        [JsonRequired] public long InputLength { get; set; }
        [JsonRequired] public DateTime InputModified { get; set; }
        [JsonRequired] public string OriginalFileName { get; set; } = "";
        [JsonRequired] public int Sequence { get; set; }
        [JsonRequired] public string OutputPath { get; set; } = "";
        [JsonRequired] public string OutputHash { get; set; } = "";
        [JsonRequired] public bool PublicationPending { get; set; }
        [JsonRequired] public WatchedFolderJobStatus Status { get; set; } = WatchedFolderJobStatus.Waiting;
        [JsonRequired] public int Attempts { get; set; }
        public string? ErrorCode { get; set; }
        public int? FailedStepIndex { get; set; }
        public PdfMacroOperation? FailedOperation { get; set; }
    }
}
