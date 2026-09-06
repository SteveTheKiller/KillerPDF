using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed portfolio collection macro steps.</summary>
public static partial class PdfCollectionMacro
{
    private static readonly PdfCollectionMacroJsonContext CollectionJson = new();

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
                ["folders"] = JsonSerializer.Serialize(
                    selected, CollectionJson.PdfCollectionFolderArray)
            });
    }

    /// <summary>Creates a step that replaces portfolio fields and sort rules.</summary>
    public static PdfMacroStep SchemaStep(
        IEnumerable<PdfCollectionFieldInfo> fields,
        IEnumerable<PdfCollectionSortInfo>? sort = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        PdfCollectionFieldInfo[] selectedFields = fields.ToArray();
        PdfCollectionSortInfo[] selectedSort = sort?.ToArray() ?? [];
        return new PdfMacroStep(PdfMacroOperation.EditPortfolio,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = "schema",
                ["fields"] = JsonSerializer.Serialize(
                    selectedFields, CollectionJson.PdfCollectionFieldInfoArray),
                ["sort"] = JsonSerializer.Serialize(
                    selectedSort, CollectionJson.PdfCollectionSortInfoArray)
            });
    }

    /// <summary>Creates a step that replaces one portfolio file's collection values.</summary>
    public static PdfMacroStep ItemValuesStep(
        string fileName, IEnumerable<PdfCollectionItemValue> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(values);
        return new PdfMacroStep(PdfMacroOperation.EditPortfolio,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = "itemValues",
                ["fileName"] = fileName,
                ["values"] = JsonSerializer.Serialize(
                    values.ToArray(), CollectionJson.PdfCollectionItemValueArray)
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
            "schema" => SetSchema(step, document),
            "itemValues" => SetItemValues(step, document),
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
            PdfCollectionFolder[] folders = JsonSerializer.Deserialize(
                json, CollectionJson.PdfCollectionFolderArray)
                ?? throw new JsonException("The portfolio folder list is empty.");
            return PdfCollectionEditor.SetFolders(document, folders);
        }
        catch (JsonException error)
        {
            throw new ArgumentException(
                "The portfolio folder list is invalid.", nameof(step), error);
        }
    }

    private static byte[] SetSchema(PdfMacroStep step, PdfDocument document)
    {
        if (step.Settings is null || step.Settings.Count != 3
            || !step.Settings.TryGetValue("fields", out string? fieldsJson)
            || !step.Settings.TryGetValue("sort", out string? sortJson))
            throw new ArgumentException(
                "The portfolio schema settings are invalid.", nameof(step));
        try
        {
            PdfCollectionFieldInfo[] fields =
                JsonSerializer.Deserialize(
                    fieldsJson, CollectionJson.PdfCollectionFieldInfoArray)
                ?? throw new JsonException("The portfolio field list is empty.");
            PdfCollectionSortInfo[] sort =
                JsonSerializer.Deserialize(
                    sortJson, CollectionJson.PdfCollectionSortInfoArray)
                ?? throw new JsonException("The portfolio sort list is empty.");
            return PdfCollectionEditor.SetSchema(document, fields, sort);
        }
        catch (JsonException error)
        {
            throw new ArgumentException(
                "The portfolio schema is invalid.", nameof(step), error);
        }
    }

    private static byte[] SetItemValues(PdfMacroStep step, PdfDocument document)
    {
        if (step.Settings is null || step.Settings.Count != 3
            || !step.Settings.TryGetValue("fileName", out string? fileName)
            || string.IsNullOrWhiteSpace(fileName)
            || !step.Settings.TryGetValue("values", out string? valuesJson))
            throw new ArgumentException(
                "The portfolio item settings are invalid.", nameof(step));
        try
        {
            PdfCollectionItemValue[] values =
                JsonSerializer.Deserialize(
                    valuesJson, CollectionJson.PdfCollectionItemValueArray)
                ?? throw new JsonException("The portfolio item value list is empty.");
            return PdfCollectionEditor.SetItemValues(document, fileName, values);
        }
        catch (JsonException error)
        {
            throw new ArgumentException(
                "The portfolio item values are invalid.", nameof(step), error);
        }
    }

    [JsonSerializable(typeof(PdfCollectionFolder[]))]
    [JsonSerializable(typeof(PdfCollectionFieldInfo[]))]
    [JsonSerializable(typeof(PdfCollectionSortInfo[]))]
    [JsonSerializable(typeof(PdfCollectionItemValue[]))]
    private sealed partial class PdfCollectionMacroJsonContext : JsonSerializerContext;
}
