using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed attachment macro steps.</summary>
public static class PdfAttachmentMacro
{
    /// <summary>Creates an attachment safety-audit step.</summary>
    public static PdfMacroStep AuditStep() =>
        new(PdfMacroOperation.AuditAttachments);

    /// <summary>Creates a step that removes document-level and page-placed attachments.</summary>
    public static PdfMacroStep RemoveStep() =>
        new(PdfMacroOperation.RemoveAttachments);

    /// <summary>Executes one attachment macro step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ValidateStep(step);
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        return step.Operation switch
        {
            PdfMacroOperation.AuditAttachments => Audit(source),
            PdfMacroOperation.RemoveAttachments => Remove(source, cancellationToken),
            _ => throw new ArgumentException(
                "The macro step is not an attachment operation.", nameof(step))
        };
    }

    /// <summary>Returns attachment safety findings without changing the document.</summary>
    public static IReadOnlyList<PdfPreflightFinding> Inspect(
        PdfMacroStep step, ReadOnlyMemory<byte> source)
    {
        ValidateStep(step);
        if (step.Operation != PdfMacroOperation.AuditAttachments)
            throw new ArgumentException(
                "The macro step is not an attachment audit.", nameof(step));
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        return PdfPreflightRunner.Run(source, PdfPreflightProfile.Attachments).Findings;
    }

    private static ReadOnlyMemory<byte> Audit(ReadOnlyMemory<byte> source)
    {
        _ = PdfPreflightRunner.Run(source, PdfPreflightProfile.Attachments);
        return source.ToArray();
    }

    private static ReadOnlyMemory<byte> Remove(
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        PdfDocument document = PdfDocument.Open(source);
        cancellationToken.ThrowIfCancellationRequested();
        return PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveAttachments = true,
            PackObjects = false,
            CompressStructure = false
        }).Apply().Data;
    }

    private static void ValidateStep(PdfMacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Settings is { Count: > 0 })
            throw new ArgumentException(
                "Attachment macro steps do not accept settings.", nameof(step));
    }
}
