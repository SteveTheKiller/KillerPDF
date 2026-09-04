using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Documents;

/// <summary>A reusable ordered list of supported PDF operations.</summary>
public sealed record PdfMacro
{
    /// <summary>Creates one editable built-in workflow without executable code.</summary>
    public static PdfMacro CreateStarter(PdfMacroStarterKind kind) => kind switch
    {
        PdfMacroStarterKind.Archival => new("Archival", [
            new(PdfMacroOperation.Ocr),
            new(PdfMacroOperation.Validate),
            new(PdfMacroOperation.Save)]),
        PdfMacroStarterKind.Sharing => new("Sharing", [
            new(PdfMacroOperation.Optimize),
            new(PdfMacroOperation.Flatten),
            new(PdfMacroOperation.Validate),
            new(PdfMacroOperation.Save)]),
        PdfMacroStarterKind.Scanning => new("Scanning", [
            new(PdfMacroOperation.Ocr),
            new(PdfMacroOperation.Optimize),
            new(PdfMacroOperation.Save)]),
        PdfMacroStarterKind.Privacy => new("Privacy", [
            new(PdfMacroOperation.Redact),
            new(PdfMacroOperation.Flatten),
            new(PdfMacroOperation.Validate),
            new(PdfMacroOperation.Save)]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Creates a named macro.</summary>
    public PdfMacro(string name, IEnumerable<PdfMacroStep> steps)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A macro name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(steps);
        PdfMacroStep[] values = steps.ToArray();
        if (values.Length == 0) throw new ArgumentException("A macro requires at least one step.", nameof(steps));
        if (values.Any(step => !Enum.IsDefined(step.Operation)))
            throw new ArgumentException("A macro contains an unsupported operation.", nameof(steps));
        Name = name;
        Steps = Array.AsReadOnly(values);
    }

    /// <summary>Gets the macro name.</summary>
    public string Name { get; }
    /// <summary>Gets the ordered operation steps.</summary>
    public IReadOnlyList<PdfMacroStep> Steps { get; }

    /// <summary>Creates a data-free preview of steps, file names, and overwrite decisions.</summary>
    public PdfMacroPreview Preview(IEnumerable<PdfMacroPreviewFile> files,
        PdfMacroOverwriteBehavior overwriteBehavior = PdfMacroOverwriteBehavior.Error)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!Enum.IsDefined(overwriteBehavior))
            throw new ArgumentOutOfRangeException(nameof(overwriteBehavior));
        PdfMacroPreviewFile[] planned = files.ToArray();
        if (planned.Any(file => string.IsNullOrWhiteSpace(file.InputName)
            || string.IsNullOrWhiteSpace(file.OutputName)))
            throw new ArgumentException("Macro preview file names cannot be empty.", nameof(files));
        if (planned.Select(file => file.OutputName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != planned.Length)
            throw new ArgumentException("Macro preview output names must be unique.", nameof(files));
        PdfMacroPreviewFile[] selected = overwriteBehavior == PdfMacroOverwriteBehavior.Skip
            ? [.. planned.Where(file => !file.OutputExists)] : planned;
        bool canRun = overwriteBehavior != PdfMacroOverwriteBehavior.Error
            || planned.All(file => !file.OutputExists);
        return new PdfMacroPreview(Name,
            Array.AsReadOnly(Steps.Select(Copy).ToArray()),
            Array.AsReadOnly(planned), Array.AsReadOnly(selected),
            overwriteBehavior, canRun);
    }

    /// <summary>Returns an independently named copy of this macro.</summary>
    public PdfMacro Duplicate(string name) => new(name, Steps.Select(Copy));

    /// <summary>Returns a copy with one step moved to a new position.</summary>
    public PdfMacro MoveStep(int fromIndex, int toIndex)
    {
        if ((uint)fromIndex >= (uint)Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if ((uint)toIndex >= (uint)Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(toIndex));
        PdfMacroStep[] reordered = Steps.Select(Copy).ToArray();
        PdfMacroStep moved = reordered[fromIndex];
        if (fromIndex < toIndex)
            Array.Copy(reordered, fromIndex + 1, reordered, fromIndex, toIndex - fromIndex);
        else if (fromIndex > toIndex)
            Array.Copy(reordered, toIndex, reordered, toIndex + 1, fromIndex - toIndex);
        reordered[toIndex] = moved;
        return new PdfMacro(Name, reordered);
    }

    /// <summary>Returns a copy with a step inserted at the requested position.</summary>
    public PdfMacro InsertStep(int index, PdfMacroStep step)
    {
        if ((uint)index > (uint)Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        ArgumentNullException.ThrowIfNull(step);
        var changed = Steps.Select(Copy).ToList();
        changed.Insert(index, Copy(step));
        return new PdfMacro(Name, changed);
    }

    /// <summary>Returns a copy with one step replaced.</summary>
    public PdfMacro ReplaceStep(int index, PdfMacroStep step)
    {
        if ((uint)index >= (uint)Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        ArgumentNullException.ThrowIfNull(step);
        PdfMacroStep[] changed = Steps.Select(Copy).ToArray();
        changed[index] = Copy(step);
        return new PdfMacro(Name, changed);
    }

    /// <summary>Returns a copy with one step removed.</summary>
    public PdfMacro RemoveStep(int index)
    {
        if ((uint)index >= (uint)Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (Steps.Count == 1)
            throw new InvalidOperationException("A macro requires at least one step.");
        return new PdfMacro(Name, Steps.Where((_, itemIndex) => itemIndex != index)
            .Select(Copy));
    }

    /// <summary>Serializes the macro without executable code or external actions.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new PdfMacroFile(1, Name, Steps.Select(step => new PdfMacroStepFile(
            step.Operation, step.Settings is null ? null
                : new Dictionary<string, string>(step.Settings, StringComparer.Ordinal))).ToArray()),
        JsonOptions(indented));

    /// <summary>Reads and validates a serialized macro.</summary>
    public static PdfMacro FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PdfMacroFile file = JsonSerializer.Deserialize<PdfMacroFile>(json, JsonOptions(false))
            ?? throw new JsonException("The macro file is empty.");
        if (file.Version != 1)
            throw new NotSupportedException(
                $"Macro file version {file.Version} is not supported.");
        return new PdfMacro(file.Name, (file.Steps
            ?? throw new JsonException("The macro file has no steps."))
            .Select(step => new PdfMacroStep(step.Operation,
                step.Settings is null ? null
                    : new Dictionary<string, string>(step.Settings, StringComparer.Ordinal))));
    }

    private static PdfMacroStep Copy(PdfMacroStep step) => new(step.Operation,
        step.Settings is null ? null
            : new Dictionary<string, string>(step.Settings, StringComparer.Ordinal));

    private static JsonSerializerOptions JsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PdfMacroFile(int Version, string Name, PdfMacroStepFile[]? Steps);
    private sealed record PdfMacroStepFile(
        PdfMacroOperation Operation, Dictionary<string, string>? Settings);
}

/// <summary>A built-in editable macro workflow.</summary>
public enum PdfMacroStarterKind
{
    /// <summary>Recognize, validate, and save archival documents.</summary>
    Archival,
    /// <summary>Optimize and flatten a validated sharing copy.</summary>
    Sharing,
    /// <summary>Recognize and optimize scanned documents.</summary>
    Scanning,
    /// <summary>Redact and flatten a validated privacy copy.</summary>
    Privacy
}

/// <summary>One configured operation in a PDF macro.</summary>
public sealed record PdfMacroStep(PdfMacroOperation Operation,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>One input and planned output shown before a macro run.</summary>
public sealed record PdfMacroPreviewFile(
    string InputName, string OutputName, bool OutputExists = false);

/// <summary>A data-free macro execution preview.</summary>
public sealed record PdfMacroPreview(string Name, IReadOnlyList<PdfMacroStep> Steps,
    IReadOnlyList<PdfMacroPreviewFile> Files,
    IReadOnlyList<PdfMacroPreviewFile> FilesToProcess,
    PdfMacroOverwriteBehavior OverwriteBehavior, bool CanRun);

/// <summary>How a macro handles output files that already exist.</summary>
public enum PdfMacroOverwriteBehavior
{
    /// <summary>Block the run until collisions are resolved.</summary>
    Error,
    /// <summary>Skip inputs whose output already exists.</summary>
    Skip,
    /// <summary>Allow replacement of existing outputs.</summary>
    Replace
}

/// <summary>The fixed set of operations accepted by the macro model.</summary>
public enum PdfMacroOperation
{
    /// <summary>Recognize page text.</summary>
    Ocr,
    /// <summary>Optimize document storage.</summary>
    Optimize,
    /// <summary>Convert document colors.</summary>
    ConvertColor,
    /// <summary>Resize pages.</summary>
    Resize,
    /// <summary>Apply redactions.</summary>
    Redact,
    /// <summary>Add a watermark.</summary>
    Watermark,
    /// <summary>Add page numbers.</summary>
    NumberPages,
    /// <summary>Generate documents from a reusable data mapping.</summary>
    DataMerge,
    /// <summary>Inspect bookmark, link, and action navigation.</summary>
    AuditNavigation,
    /// <summary>Inspect embedded-file safety and integrity.</summary>
    AuditAttachments,
    /// <summary>Remove document-level and page-placed attachments.</summary>
    RemoveAttachments,
    /// <summary>Arrange source pages on output sheets using an N-up preset.</summary>
    ImposeNUp,
    /// <summary>Generate reviewed bookmarks from detected headings.</summary>
    GenerateBookmarks,
    /// <summary>Generate clickable table-of-contents pages.</summary>
    GenerateTableOfContents,
    /// <summary>Flatten editable content.</summary>
    Flatten,
    /// <summary>Flatten PDF layers to an explicitly selected visible result.</summary>
    FlattenLayers,
    /// <summary>Validate the document.</summary>
    Validate,
    /// <summary>Export document content.</summary>
    Export,
    /// <summary>Rename the output.</summary>
    Rename,
    /// <summary>Save the result.</summary>
    Save
}

/// <summary>Runs typed PDF macros with per-file isolation and cancellation.</summary>
public static class PdfMacroRunner
{
    /// <summary>Runs a macro and returns aggregate and per-file outcomes.</summary>
    public static PdfMacroRunReport RunReport(
        PdfMacro macro, IEnumerable<ReadOnlyMemory<byte>> inputs,
        Func<PdfMacroStep, ReadOnlyMemory<byte>, CancellationToken, ReadOnlyMemory<byte>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ReadOnlyMemory<byte>[] supplied = inputs.ToArray();
        return new PdfMacroRunReport(supplied.Length,
            Run(macro, supplied, operation, cancellationToken));
    }

    /// <summary>
    /// Resumes an interrupted report, preserving completed outcomes and retrying a canceled file.
    /// </summary>
    public static PdfMacroRunReport ResumeReport(
        PdfMacro macro, IEnumerable<ReadOnlyMemory<byte>> inputs,
        PdfMacroRunReport previous,
        Func<PdfMacroStep, ReadOnlyMemory<byte>, CancellationToken, ReadOnlyMemory<byte>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(previous);
        ReadOnlyMemory<byte>[] supplied = inputs.ToArray();
        if (previous.TotalInputCount != supplied.Length)
            throw new ArgumentException(
                "The previous macro report does not match the supplied input count.", nameof(previous));
        for (int index = 0; index < previous.Results.Count; index++)
            if (previous.Results[index].InputIndex != index
                || (previous.Results[index].WasCanceled && index != previous.Results.Count - 1))
                throw new ArgumentException(
                    "The previous macro report is not a contiguous resumable prefix.", nameof(previous));

        int startIndex = previous.Results.Count;
        var combined = previous.Results.ToList();
        if (combined.LastOrDefault()?.WasCanceled == true)
        {
            startIndex--;
            combined.RemoveAt(combined.Count - 1);
        }
        IReadOnlyList<PdfMacroFileResult> resumed = Run(
            macro, supplied.Skip(startIndex), operation, cancellationToken);
        combined.AddRange(resumed.Select(result => result with
        {
            InputIndex = result.InputIndex + startIndex
        }));
        return new PdfMacroRunReport(supplied.Length, combined);
    }

    /// <summary>Runs a macro against every input while preserving each source buffer.</summary>
    public static IReadOnlyList<PdfMacroFileResult> Run(
        PdfMacro macro, IEnumerable<ReadOnlyMemory<byte>> inputs,
        Func<PdfMacroStep, ReadOnlyMemory<byte>, CancellationToken, ReadOnlyMemory<byte>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(operation);
        var results = new List<PdfMacroFileResult>();
        int index = 0;
        foreach (ReadOnlyMemory<byte> source in inputs)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                ReadOnlyMemory<byte> current = source.ToArray();
                foreach (PdfMacroStep step in macro.Steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current = operation(step, current, cancellationToken);
                }
                results.Add(new PdfMacroFileResult(index, current.ToArray(), null, false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new PdfMacroFileResult(index, null, null, true));
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                results.Add(new PdfMacroFileResult(index, null, exception.Message, false));
            }
            index++;
        }
        return Array.AsReadOnly(results.ToArray());
    }
}

/// <summary>Aggregate and per-file outcomes from one macro batch.</summary>
public sealed record PdfMacroRunReport
{
    /// <summary>Creates a report for a bounded input batch.</summary>
    public PdfMacroRunReport(int totalInputCount, IEnumerable<PdfMacroFileResult> results)
    {
        if (totalInputCount < 0) throw new ArgumentOutOfRangeException(nameof(totalInputCount));
        ArgumentNullException.ThrowIfNull(results);
        PdfMacroFileResult[] values = results.ToArray();
        if (values.Length > totalInputCount)
            throw new ArgumentException("Macro results exceed the input count.", nameof(results));
        TotalInputCount = totalInputCount;
        Results = Array.AsReadOnly(values);
    }

    /// <summary>Gets the number of supplied inputs.</summary>
    public int TotalInputCount { get; }
    /// <summary>Gets each attempted input result.</summary>
    public IReadOnlyList<PdfMacroFileResult> Results { get; }
    /// <summary>Gets the number of successful inputs.</summary>
    public int SucceededCount => Results.Count(result => result.Succeeded);
    /// <summary>Gets the number of failed inputs.</summary>
    public int FailedCount => Results.Count(result => result.Error is not null);
    /// <summary>Gets the number of canceled inputs.</summary>
    public int CanceledCount => Results.Count(result => result.WasCanceled);
    /// <summary>Gets the number of inputs not started after cancellation.</summary>
    public int UnprocessedCount => TotalInputCount - Results.Count;

    /// <summary>Exports stable batch outcomes without embedding document data.</summary>
    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        return JsonSerializer.Serialize(new
        {
            Version = 1,
            TotalInputCount,
            SucceededCount,
            FailedCount,
            CanceledCount,
            UnprocessedCount,
            Results = Results.Select(result => new
            {
                result.InputIndex,
                result.Succeeded,
                result.Error,
                result.WasCanceled
            })
        }, options);
    }
}

/// <summary>The isolated result for one macro input.</summary>
public sealed record PdfMacroFileResult(int InputIndex, ReadOnlyMemory<byte>? Data,
    string? Error, bool WasCanceled)
{
    /// <summary>Gets whether every step completed.</summary>
    public bool Succeeded => Data.HasValue && Error is null && !WasCanceled;
}
