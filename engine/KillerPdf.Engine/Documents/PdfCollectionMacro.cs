using System.Text.Json;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed portfolio collection macro steps.</summary>
public static class PdfCollectionMacro
{
    /// <summary>Creates a step that sets portfolio presentation metadata.</summary>
    public static PdfMacroStep PresentationStep(
        PdfCollectionView view, string? initialDocument = null)
    {
        if (view is PdfCollectionView.Unknown || !Enum.IsDefined(view))
            throw new ArgumentOutOfRangeException(nameof(view));
        if (initialDocument is not null && string.IsNullOrWhiteSpace(initialDocument))
            throw new ArgumentException(
                "An initial portfolio document cannot be empty.", nameof(initialDocument));
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = "presentation",
            ["view"] = view.ToString()
        };
        if (initialDocument is not null) settings["initialDocument"] = initialDocument;
        return new PdfMacroStep(PdfMacroOperation.EditPortfolio, settings);
    }

    /// <summary>Creates a step that replaces the portfolio folder hierarchy.</summary>
    public static PdfMacroStep FoldersStep(IEnumerable<PdfCollectionFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        PdfCollectionFolder[] selected = folders.ToArray();
        return new PdfMacroStep(PdfMacroOperation.EditPortfolio,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = "folders",
                ["folders"] = JsonSerializer.Serialize(selected)
            });
    }

    /// <summary>Creates a step that removes portfolio metadata without removing attachments.</summary>
    public static PdfMacroStep ClearStep() => new(PdfMacroOperation.EditPortfolio,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["action"] = "clear" });

    /// <summary>Executes one portfolio collection macro step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation != PdfMacroOperation.EditPortfolio
            || step.Settings is null
            || !step.Settings.TryGetValue("action", out string? action))
            throw new ArgumentException(
                "The macro step is not a portfolio edit operation.", nameof(step));
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        PdfDocument document = PdfDocument.Open(source);
        return action switch
        {
            "presentation" => SetPresentation(step, document),
            "folders" => SetFolders(step, document),
            "clear" when step.Settings.Count == 1 => PdfCollectionEditor.Clear(document),
            _ => throw new ArgumentException(
                "The portfolio edit settings are invalid.", nameof(step))
        };
    }

    private static byte[] SetPresentation(PdfMacroStep step, PdfDocument document)
    {
        if (step.Settings is null
            || !step.Settings.TryGetValue("view", out string? viewText)
            || !Enum.TryParse(viewText, ignoreCase: false, out PdfCollectionView view)
            || view is PdfCollectionView.Unknown || !Enum.IsDefined(view)
            || step.Settings.Keys.Any(key => key is not
                ("action" or "view" or "initialDocument")))
            throw new ArgumentException(
                "The portfolio presentation settings are invalid.", nameof(step));
        step.Settings.TryGetValue("initialDocument", out string? initialDocument);
        return PdfCollectionEditor.SetPresentation(document, view, initialDocument);
    }

    private static byte[] SetFolders(PdfMacroStep step, PdfDocument document)
    {
        if (step.Settings is null || step.Settings.Count != 2
            || !step.Settings.TryGetValue("folders", out string? json))
            throw new ArgumentException(
                "The portfolio folder settings are invalid.", nameof(step));
        try
        {
            PdfCollectionFolder[] folders = JsonSerializer.Deserialize<PdfCollectionFolder[]>(json)
                ?? throw new JsonException("The portfolio folder list is empty.");
            return PdfCollectionEditor.SetFolders(document, folders);
        }
        catch (JsonException error)
        {
            throw new ArgumentException(
                "The portfolio folder list is invalid.", nameof(step), error);
        }
    }
}
