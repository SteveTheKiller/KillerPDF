namespace KillerPdf.Engine.Documents;

/// <summary>A reusable ordered list of supported PDF operations.</summary>
public sealed record PdfMacro
{
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
}

/// <summary>One configured operation in a PDF macro.</summary>
public sealed record PdfMacroStep(PdfMacroOperation Operation,
    IReadOnlyDictionary<string, string>? Settings = null);

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
    /// <summary>Flatten editable content.</summary>
    Flatten,
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

/// <summary>The isolated result for one macro input.</summary>
public sealed record PdfMacroFileResult(int InputIndex, ReadOnlyMemory<byte>? Data,
    string? Error, bool WasCanceled)
{
    /// <summary>Gets whether every step completed.</summary>
    public bool Succeeded => Data.HasValue && Error is null && !WasCanceled;
}
