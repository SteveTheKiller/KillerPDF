using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>Executes one host-provided macro operation.</summary>
public delegate PdfMacroOperationResult PdfMacroOperationHandler(
    PdfMacroStep step, ReadOnlyMemory<byte> source,
    IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);

/// <summary>Binds one host operation to a typed implementation.</summary>
public sealed record PdfMacroOperationRegistration(
    PdfMacroOperation Operation, PdfMacroOperationHandler Handler);

/// <summary>
/// Dispatches supported macro steps to engine APIs and explicitly registered host operations.
/// </summary>
public sealed class PdfMacroDispatcher
{
    private static readonly PdfMacroOperation[] BuiltIns =
    [
        PdfMacroOperation.Optimize,
        PdfMacroOperation.NumberPages,
        PdfMacroOperation.AuditNavigation,
        PdfMacroOperation.RemoveUnsafeLinks,
        PdfMacroOperation.AuditAttachments,
        PdfMacroOperation.RemoveAttachments,
        PdfMacroOperation.ImposeNUp,
        PdfMacroOperation.GenerateBookmarks,
        PdfMacroOperation.GenerateTableOfContents,
        PdfMacroOperation.Flatten,
        PdfMacroOperation.FlattenLayers,
        PdfMacroOperation.EditLayers,
        PdfMacroOperation.EditPortfolio,
        PdfMacroOperation.Validate,
        PdfMacroOperation.Export,
        PdfMacroOperation.RemoveUnresolvedLinks,
        PdfMacroOperation.ImposeBooklet,
        PdfMacroOperation.SetPageLabels,
        PdfMacroOperation.ImposeStepAndRepeat,
        PdfMacroOperation.ImposeCutStack,
        PdfMacroOperation.ImposeManualSequence,
        PdfMacroOperation.ImposePoster,
        PdfMacroOperation.EditAttachments
    ];

    private static readonly PdfMacroOperation[] HostProvided =
    [
        PdfMacroOperation.Ocr,
        PdfMacroOperation.ConvertColor,
        PdfMacroOperation.Resize,
        PdfMacroOperation.Redact,
        PdfMacroOperation.Watermark,
        PdfMacroOperation.DataMerge,
        PdfMacroOperation.Rename,
        PdfMacroOperation.Save
    ];

    private readonly IReadOnlyDictionary<PdfMacroOperation, PdfMacroOperationHandler> _handlers;
    private readonly IReadOnlyList<PdfMacroOperation> _supportedOperations;

    /// <summary>Creates a dispatcher with optional host-provided operation handlers.</summary>
    public PdfMacroDispatcher(IEnumerable<PdfMacroOperationRegistration>? registrations = null)
    {
        var handlers = new Dictionary<PdfMacroOperation, PdfMacroOperationHandler>();
        foreach (PdfMacroOperationRegistration registration in registrations ?? [])
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!Enum.IsDefined(registration.Operation))
                throw new ArgumentOutOfRangeException(nameof(registrations),
                    "A macro operation registration is not defined.");
            if (!HostProvided.Contains(registration.Operation))
                throw new ArgumentException(
                    $"Macro operation '{registration.Operation}' is implemented by the engine.",
                    nameof(registrations));
            ArgumentNullException.ThrowIfNull(registration.Handler);
            if (!handlers.TryAdd(registration.Operation, registration.Handler))
                throw new ArgumentException(
                    $"Macro operation '{registration.Operation}' has more than one handler.",
                    nameof(registrations));
        }

        _handlers = new System.Collections.ObjectModel.ReadOnlyDictionary<
            PdfMacroOperation, PdfMacroOperationHandler>(handlers);
        _supportedOperations = Array.AsReadOnly(BuiltIns.Concat(handlers.Keys)
            .OrderBy(operation => operation).ToArray());
    }

    /// <summary>Gets operations implemented directly by the engine.</summary>
    public static IReadOnlyList<PdfMacroOperation> BuiltInOperations { get; } =
        Array.AsReadOnly(BuiltIns);

    /// <summary>Gets operations that require an application or command-line host handler.</summary>
    public static IReadOnlyList<PdfMacroOperation> HostOperations { get; } =
        Array.AsReadOnly(HostProvided);

    /// <summary>Gets every operation executable by this dispatcher instance.</summary>
    public IReadOnlyList<PdfMacroOperation> SupportedOperations => _supportedOperations;

    /// <summary>Gets whether this dispatcher can execute an operation.</summary>
    public bool CanExecute(PdfMacroOperation operation) =>
        BuiltIns.Contains(operation) || _handlers.ContainsKey(operation);

    /// <summary>Executes one macro step through its typed operation API.</summary>
    public PdfMacroOperationResult Execute(PdfMacroStep step, ReadOnlyMemory<byte> source,
        IReadOnlyDictionary<string, string>? values = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string> suppliedValues = values
            ?? new Dictionary<string, string>();
        if (_handlers.TryGetValue(step.Operation, out PdfMacroOperationHandler? handler))
            return handler(step, source, suppliedValues, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Macro operation '{step.Operation}' returned no result.");
        if (!BuiltIns.Contains(step.Operation))
            throw new NotSupportedException(
                $"Macro operation '{step.Operation}' requires a host handler.");
        return ExecuteBuiltIn(step, source, cancellationToken);
    }

    /// <summary>Runs one macro against a batch with per-file isolation.</summary>
    public PdfMacroRunReport RunReport(PdfMacro macro,
        IEnumerable<ReadOnlyMemory<byte>> inputs,
        IReadOnlyDictionary<string, string>? initialValues = null,
        CancellationToken cancellationToken = default)
    {
        return PdfMacroRunner.RunContextualReport(
            macro, inputs, initialValues, Execute, cancellationToken);
    }

    /// <summary>Resumes a canceled batch with the same operation and value bindings.</summary>
    public PdfMacroRunReport ResumeReport(PdfMacro macro,
        IEnumerable<ReadOnlyMemory<byte>> inputs, PdfMacroRunReport previous,
        IReadOnlyDictionary<string, string>? initialValues = null,
        CancellationToken cancellationToken = default)
    {
        return PdfMacroRunner.ResumeContextualReport(
            macro, inputs, previous, initialValues, Execute, cancellationToken);
    }

    private static PdfMacroOperationResult ExecuteBuiltIn(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return step.Operation switch
        {
            PdfMacroOperation.Optimize => Optimize(step, source),
            PdfMacroOperation.NumberPages => Result(
                PdfPageFurnitureMacro.Execute(step, source, cancellationToken)),
            PdfMacroOperation.AuditNavigation or
                PdfMacroOperation.RemoveUnsafeLinks or
                PdfMacroOperation.GenerateBookmarks or
                PdfMacroOperation.GenerateTableOfContents or
                PdfMacroOperation.RemoveUnresolvedLinks or
                PdfMacroOperation.SetPageLabels => Result(
                    PdfNavigationMacro.Execute(step, source, cancellationToken)),
            PdfMacroOperation.AuditAttachments or
                PdfMacroOperation.RemoveAttachments or
                PdfMacroOperation.EditAttachments => Result(
                    PdfAttachmentMacro.Execute(step, source, cancellationToken)),
            PdfMacroOperation.ImposeNUp or
                PdfMacroOperation.ImposeBooklet or
                PdfMacroOperation.ImposeStepAndRepeat or
                PdfMacroOperation.ImposeCutStack or
                PdfMacroOperation.ImposeManualSequence or
                PdfMacroOperation.ImposePoster => Result(
                    PdfImpositionMacro.Execute(step, source, cancellationToken)),
            PdfMacroOperation.Flatten => Result(
                PdfFormFlattener.Flatten(PdfDocument.Open(source))),
            PdfMacroOperation.FlattenLayers or PdfMacroOperation.EditLayers => Result(
                PdfLayerMacro.Execute(step, source, cancellationToken)),
            PdfMacroOperation.EditPortfolio => Result(
                PdfCollectionMacro.Execute(step, source, cancellationToken)),
            PdfMacroOperation.Validate => Validate(step, source),
            PdfMacroOperation.Export => Result(
                PdfStructuredExportMacro.Execute(step, source, cancellationToken)),
            _ => throw new NotSupportedException(
                $"Macro operation '{step.Operation}' requires a host handler.")
        };
    }

    private static PdfMacroOperationResult Optimize(PdfMacroStep step,
        ReadOnlyMemory<byte> source)
    {
        if (step.Settings is { Count: > 0 })
            throw new NotSupportedException(
                "The built-in Optimize operation does not accept settings yet.");
        PdfDocument document = PdfDocument.Open(source);
        PdfOptimizationResult result = PdfOptimizer.CreatePlan(document,
            PdfOptimizationOptions.ForDocument(document)).Apply();
        return new PdfMacroOperationResult(result.Data,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["optimizationReport"] = result.ToJson()
            });
    }

    private static PdfMacroOperationResult Validate(PdfMacroStep step,
        ReadOnlyMemory<byte> source)
    {
        PdfPreflightProfile profile = step.Settings is not null
            && step.Settings.TryGetValue("profileJson", out string? json)
                ? PdfPreflightProfile.FromJson(json)
                : PdfPreflightProfile.General;
        string[] unknown = [.. (step.Settings?.Keys ?? [])
            .Where(name => !string.Equals(name, "profileJson", StringComparison.Ordinal))];
        if (unknown.Length != 0)
            throw new ArgumentException(
                $"Validate does not support setting '{unknown[0]}'.", nameof(step));
        PdfPreflightReport report = PdfPreflightRunner.Run(source, profile);
        return new PdfMacroOperationResult(source,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["validationPassed"] = report.Passed.ToString(),
                ["validationComplete"] = report.Complete.ToString(),
                ["validationReport"] = report.ToJson()
            });
    }

    private static PdfMacroOperationResult Result(ReadOnlyMemory<byte> data) => new(data);
}
