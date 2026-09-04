using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Editing;
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

    /// <summary>Creates a step that renames one document attachment.</summary>
    public static PdfMacroStep RenameStep(string fileName, string newFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFileName);
        return EditStep("rename", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fileName"] = fileName,
            ["newFileName"] = newFileName
        });
    }

    /// <summary>Creates a step that changes or clears one attachment description.</summary>
    public static PdfMacroStep DescriptionStep(string fileName, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException(
                "An attachment description cannot be whitespace.", nameof(description));
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fileName"] = fileName
        };
        if (description is not null) settings["description"] = description;
        return EditStep("description", settings);
    }

    /// <summary>Creates a step that changes attachment MIME type and relationship.</summary>
    public static PdfMacroStep ClassificationStep(
        string fileName, string mimeType, PdfAssociatedFileRelationship relationship)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        if (!Enum.IsDefined(relationship))
            throw new ArgumentOutOfRangeException(nameof(relationship));
        return EditStep("classification",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileName"] = fileName,
                ["mimeType"] = mimeType,
                ["relationship"] = relationship.ToString()
            });
    }

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
            PdfMacroOperation.EditAttachments => Edit(step, source, cancellationToken),
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

    private static ReadOnlyMemory<byte> Edit(
        PdfMacroStep step, ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        PdfDocument document = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(document);
        string action = step.Settings!["action"];
        string fileName = step.Settings["fileName"];
        switch (action)
        {
            case "rename" when step.Settings.Count == 3
                && step.Settings.TryGetValue("newFileName", out string? newFileName):
                editor.RenameAttachment(fileName, newFileName);
                break;
            case "description" when step.Settings.Count is 2 or 3
                && step.Settings.Keys.All(key =>
                    key is "action" or "fileName" or "description"):
                step.Settings.TryGetValue("description", out string? description);
                editor.SetAttachmentDescription(fileName, description);
                break;
            case "classification" when step.Settings.Count == 4
                && step.Settings.TryGetValue("mimeType", out string? mimeType)
                && step.Settings.TryGetValue("relationship", out string? relationshipText)
                && Enum.TryParse(relationshipText, ignoreCase: false,
                    out PdfAssociatedFileRelationship relationship)
                && Enum.IsDefined(relationship):
                editor.SetAttachmentClassification(fileName, mimeType, relationship);
                break;
            default:
                throw new ArgumentException(
                    "The attachment edit settings are invalid.", nameof(step));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return editor.Build();
    }

    private static PdfMacroStep EditStep(
        string action, Dictionary<string, string> settings)
    {
        settings["action"] = action;
        return new PdfMacroStep(PdfMacroOperation.EditAttachments, settings);
    }

    private static void ValidateStep(PdfMacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation == PdfMacroOperation.EditAttachments)
        {
            if (step.Settings is null
                || !step.Settings.TryGetValue("action", out string? action)
                || !step.Settings.TryGetValue("fileName", out string? fileName)
                || string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException(
                    "The attachment edit settings are invalid.", nameof(step));
            return;
        }
        if (step.Settings is { Count: > 0 })
            throw new ArgumentException(
                "Attachment macro steps do not accept settings.", nameof(step));
    }
}
