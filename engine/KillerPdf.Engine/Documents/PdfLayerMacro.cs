using System.Text.Json;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed PDF layer macro steps.</summary>
public static class PdfLayerMacro
{
    /// <summary>Creates a step that registers a new layer.</summary>
    public static PdfMacroStep CreateStep(string layerName,
        bool initiallyVisible = true, bool locked = false,
        bool? printVisible = null, bool? exportVisible = null) =>
        EditStep("create", layerName, JsonSerializer.Serialize(
            new CreateSettings(initiallyVisible, locked, printVisible, exportVisible)));

    /// <summary>Creates a step that duplicates one layer under a new name.</summary>
    public static PdfMacroStep DuplicateStep(string layerName, string newName) =>
        EditStep("duplicate", layerName, newName);

    /// <summary>Creates a step that renames one layer selected by its current name.</summary>
    public static PdfMacroStep RenameStep(string layerName, string newName) =>
        EditStep("rename", layerName, newName);

    /// <summary>Creates a step that changes one layer's initial visibility.</summary>
    public static PdfMacroStep VisibilityStep(string layerName, bool visible) =>
        EditStep("visibility", layerName, visible ? "true" : "false");

    /// <summary>Creates a step that changes or clears one layer's print visibility.</summary>
    public static PdfMacroStep PrintVisibilityStep(string layerName, bool? visible) =>
        EditStep("printVisibility", layerName,
            visible.HasValue ? visible.Value ? "true" : "false" : "clear");

    /// <summary>Creates a step that changes or clears one layer's export visibility.</summary>
    public static PdfMacroStep ExportVisibilityStep(string layerName, bool? visible) =>
        EditStep("exportVisibility", layerName,
            visible.HasValue ? visible.Value ? "true" : "false" : "clear");

    /// <summary>Creates a step that changes one layer's lock state.</summary>
    public static PdfMacroStep LockStep(string layerName, bool locked) =>
        EditStep("lock", layerName, locked ? "true" : "false");

    /// <summary>Creates a step that merges one layer into another layer.</summary>
    public static PdfMacroStep MergeStep(string sourceLayerName, string targetLayerName) =>
        EditStep("merge", sourceLayerName, targetLayerName);

    /// <summary>Creates a step that removes an unused layer.</summary>
    public static PdfMacroStep RemoveUnusedStep(string layerName) =>
        EditStep("removeUnused", layerName, null);

    /// <summary>Creates a step that changes or clears default layer configuration metadata.</summary>
    public static PdfMacroStep ConfigurationMetadataStep(string? name, string? creator) =>
        new(PdfMacroOperation.EditLayers,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = "configurationMetadata",
                ["value"] = JsonSerializer.Serialize(
                    new ConfigurationMetadataSettings(name, creator))
            });

    /// <summary>Creates a step that changes the default layer base state.</summary>
    public static PdfMacroStep BaseStateStep(PdfOptionalContentBaseState baseState)
    {
        if (!Enum.IsDefined(baseState))
            throw new ArgumentOutOfRangeException(nameof(baseState));
        return new PdfMacroStep(PdfMacroOperation.EditLayers,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = "baseState",
                ["value"] = baseState.ToString()
            });
    }

    /// <summary>Creates a step that replaces the flat display order using stable layer names.</summary>
    public static PdfMacroStep DisplayOrderStep(IEnumerable<string> layerNames)
    {
        ArgumentNullException.ThrowIfNull(layerNames);
        string[] names = layerNames.ToArray();
        ValidateNames(names, nameof(layerNames));
        return new PdfMacroStep(PdfMacroOperation.EditLayers,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = "displayOrder",
                ["value"] = JsonSerializer.Serialize(names)
            });
    }

    /// <summary>Creates a step that replaces the nested display order using stable layer names.</summary>
    public static PdfMacroStep DisplayOrderTreeStep(
        IEnumerable<PdfLayerOrderItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        PdfLayerOrderItem[] tree = items.ToArray();
        ValidateOrderTree(tree, nameof(items));
        return new PdfMacroStep(PdfMacroOperation.EditLayers,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = "displayOrderTree",
                ["value"] = JsonSerializer.Serialize(tree)
            });
    }

    /// <summary>Creates a layer-flattening step using default visibility.</summary>
    public static PdfMacroStep FlattenStep() => new(PdfMacroOperation.FlattenLayers);

    /// <summary>Creates a layer-flattening step with an explicit visible layer set.</summary>
    public static PdfMacroStep FlattenStep(IEnumerable<string> visibleLayerNames)
    {
        ArgumentNullException.ThrowIfNull(visibleLayerNames);
        string[] names = visibleLayerNames.ToArray();
        if (names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException(
                "Visible layer names must be nonempty and unique.", nameof(visibleLayerNames));
        return new PdfMacroStep(PdfMacroOperation.FlattenLayers,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["visibleLayers"] = JsonSerializer.Serialize(names)
            });
    }

    /// <summary>Executes one layer macro step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation is not (PdfMacroOperation.FlattenLayers
                or PdfMacroOperation.EditLayers))
            throw new ArgumentException("The macro step is not a layer operation.", nameof(step));
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        PdfDocument document = PdfDocument.Open(source);
        if (step.Operation == PdfMacroOperation.EditLayers)
            return ExecuteEdit(step, document, cancellationToken);
        IReadOnlyCollection<int>? visible = VisibleGroups(step, document);
        cancellationToken.ThrowIfCancellationRequested();
        return PdfOptionalContentEditor.FlattenPageContent(document, visible);
    }

    private static ReadOnlyMemory<byte> ExecuteEdit(PdfMacroStep step,
        PdfDocument document, CancellationToken cancellationToken)
    {
        if (step.Settings is null
            || !step.Settings.TryGetValue("action", out string? action)
            || step.Settings.Keys.Any(key => key is not ("action" or "layer" or "value")))
            throw new ArgumentException("The layer edit settings are invalid.", nameof(step));
        step.Settings.TryGetValue("value", out string? value);
        cancellationToken.ThrowIfCancellationRequested();
        if (action == "configurationMetadata" && !string.IsNullOrWhiteSpace(value))
        {
            ConfigurationMetadataSettings settings;
            try
            {
                settings = JsonSerializer.Deserialize<ConfigurationMetadataSettings>(value)
                    ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "The layer configuration metadata is invalid.", nameof(step), exception);
            }
            if (step.Settings.ContainsKey("layer"))
                throw new ArgumentException("The layer edit settings are invalid.", nameof(step));
            return PdfOptionalContentEditor.SetDefaultConfigurationMetadata(
                document, settings.Name, settings.Creator);
        }
        if (action == "baseState"
            && Enum.TryParse(value, false, out PdfOptionalContentBaseState baseState)
            && Enum.IsDefined(baseState))
        {
            if (step.Settings.ContainsKey("layer"))
                throw new ArgumentException("The layer edit settings are invalid.", nameof(step));
            return PdfOptionalContentEditor.SetDefaultBaseState(document, baseState);
        }
        if (action == "displayOrder" && !string.IsNullOrWhiteSpace(value))
        {
            if (step.Settings.ContainsKey("layer"))
                throw new ArgumentException("The layer edit settings are invalid.", nameof(step));
            string[] names;
            try
            {
                names = JsonSerializer.Deserialize<string[]>(value)
                    ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "The layer display order is invalid.", nameof(step), exception);
            }
            ValidateNames(names, nameof(step));
            int[] objectNumbers = [.. names.Select(name => GroupNumber(document, name))];
            return PdfOptionalContentEditor.SetDisplayOrder(document, objectNumbers);
        }
        if (action == "displayOrderTree" && !string.IsNullOrWhiteSpace(value))
        {
            if (step.Settings.ContainsKey("layer"))
                throw new ArgumentException("The layer edit settings are invalid.", nameof(step));
            PdfLayerOrderItem[] items;
            try
            {
                items = JsonSerializer.Deserialize<PdfLayerOrderItem[]>(value)
                    ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "The nested layer display order is invalid.", nameof(step), exception);
            }
            ValidateOrderTree(items, nameof(step));
            return PdfOptionalContentEditor.SetDisplayOrderTree(document,
                Array.AsReadOnly(items.Select(item => Convert(document, item)).ToArray()));
        }
        if (!step.Settings.TryGetValue("layer", out string? layerName))
            throw new ArgumentException("The layer edit settings are invalid.", nameof(step));
        if (action == "create" && !string.IsNullOrWhiteSpace(value))
        {
            CreateSettings settings;
            try
            {
                settings = JsonSerializer.Deserialize<CreateSettings>(value)
                    ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "The layer creation settings are invalid.", nameof(step), exception);
            }
            return PdfOptionalContentEditor.AddGroup(document, layerName,
                settings.InitiallyVisible, settings.Locked,
                settings.PrintVisible, settings.ExportVisible);
        }
        int objectNumber = GroupNumber(document, layerName);
        return action switch
        {
            "duplicate" when !string.IsNullOrWhiteSpace(value) =>
                PdfOptionalContentEditor.DuplicateGroup(document, objectNumber, value),
            "rename" when !string.IsNullOrWhiteSpace(value) =>
                PdfOptionalContentEditor.RenameGroup(document, objectNumber, value),
            "visibility" when bool.TryParse(value, out bool visible) =>
                PdfOptionalContentEditor.SetInitialVisibility(document, objectNumber, visible),
            "printVisibility" when value == "clear" =>
                PdfOptionalContentEditor.SetPrintVisibility(document, objectNumber, null),
            "printVisibility" when bool.TryParse(value, out bool printVisible) =>
                PdfOptionalContentEditor.SetPrintVisibility(document, objectNumber, printVisible),
            "exportVisibility" when value == "clear" =>
                PdfOptionalContentEditor.SetExportVisibility(document, objectNumber, null),
            "exportVisibility" when bool.TryParse(value, out bool exportVisible) =>
                PdfOptionalContentEditor.SetExportVisibility(document, objectNumber, exportVisible),
            "lock" when bool.TryParse(value, out bool locked) =>
                PdfOptionalContentEditor.SetLocked(document, objectNumber, locked),
            "merge" when !string.IsNullOrWhiteSpace(value) =>
                PdfOptionalContentEditor.MergeGroups(
                    document, objectNumber, GroupNumber(document, value)),
            "removeUnused" when value is null =>
                PdfOptionalContentEditor.RemoveUnusedGroup(document, objectNumber),
            _ => throw new ArgumentException("The layer edit action is invalid.", nameof(step))
        };
    }

    private sealed record CreateSettings(bool InitiallyVisible, bool Locked,
        bool? PrintVisible, bool? ExportVisible);
    private sealed record ConfigurationMetadataSettings(string? Name, string? Creator);

    private static PdfMacroStep EditStep(string action, string layerName, string? value)
    {
        if (string.IsNullOrWhiteSpace(layerName))
            throw new ArgumentException("A layer name is required.", nameof(layerName));
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A layer edit value cannot be empty.", nameof(value));
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = action,
            ["layer"] = layerName
        };
        if (value is not null) settings.Add("value", value);
        return new PdfMacroStep(PdfMacroOperation.EditLayers, settings);
    }

    private static int GroupNumber(PdfDocument document, string name)
    {
        PdfOptionalContentGroupInfo[] matches = [.. PdfOptionalContentReader.Read(document).Groups
            .Where(group => string.Equals(group.Name, name, StringComparison.Ordinal))];
        return matches.Length switch
        {
            1 => matches[0].ObjectNumber,
            0 => throw new ArgumentException($"Layer '{name}' was not found."),
            _ => throw new InvalidOperationException(
                $"Layer name '{name}' is ambiguous in this document.")
        };
    }

    private static void ValidateNames(IReadOnlyList<string> names, string parameterName)
    {
        if (names.Count == 0 || names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException(
                "Layer names must be nonempty and unique.", parameterName);
    }

    private static void ValidateOrderTree(
        IReadOnlyList<PdfLayerOrderItem> items, string parameterName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (items.Count == 0 || items.Any(item => !Valid(item, 0)))
            throw new ArgumentException(
                "The nested layer display order is invalid.", parameterName);

        bool Valid(PdfLayerOrderItem? item, int depth)
        {
            if (item is null || depth > 64) return false;
            PdfLayerOrderItem[] children = item.Children?.ToArray() ?? [];
            if (item.LayerName is not null)
                return !string.IsNullOrWhiteSpace(item.LayerName)
                    && item.Label is null && children.Length == 0
                    && names.Add(item.LayerName);
            return !string.IsNullOrWhiteSpace(item.Label) && children.Length > 0
                && children.All(child => Valid(child, depth + 1));
        }
    }

    private static PdfOptionalContentOrderItem Convert(
        PdfDocument document, PdfLayerOrderItem item) =>
        item.LayerName is not null
            ? PdfOptionalContentOrderItem.Layer(GroupNumber(document, item.LayerName))
            : PdfOptionalContentOrderItem.Folder(item.Label!,
                (item.Children ?? []).Select(child => Convert(document, child)).ToArray());

    private static IReadOnlyCollection<int>? VisibleGroups(
        PdfMacroStep step, PdfDocument document)
    {
        if (step.Settings is null) return null;
        if (step.Settings.Count != 1
            || !step.Settings.TryGetValue("visibleLayers", out string? json))
            throw new ArgumentException("The layer macro settings are invalid.", nameof(step));
        string[] names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(json)
                ?? throw new JsonException("The visible layer list is empty.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The visible layer list is invalid.", nameof(step), error);
        }
        if (names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException(
                "Visible layer names must be nonempty and unique.", nameof(step));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        Dictionary<string, int> groups;
        try
        {
            groups = info.Groups.ToDictionary(group => group.Name,
                group => group.ObjectNumber, StringComparer.Ordinal);
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException(
                "Layer names are not unique, so a name-based macro is ambiguous.", error);
        }
        if (names.Any(name => !groups.ContainsKey(name)))
            throw new ArgumentException(
                "The visible layer list contains a layer not found in the document.", nameof(step));
        return Array.AsReadOnly(names.Select(name => groups[name]).ToArray());
    }
}

/// <summary>One stable-name layer or named folder in a macro display-order tree.</summary>
public sealed record PdfLayerOrderItem(
    string? LayerName, string? Label, IReadOnlyList<PdfLayerOrderItem>? Children = null)
{
    /// <summary>Creates a layer entry.</summary>
    public static PdfLayerOrderItem Layer(string layerName) =>
        new(layerName, null);

    /// <summary>Creates a named folder entry.</summary>
    public static PdfLayerOrderItem Folder(
        string label, params PdfLayerOrderItem[] children) =>
        new(null, label, Array.AsReadOnly(children.ToArray()));
}
