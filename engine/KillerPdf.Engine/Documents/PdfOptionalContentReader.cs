using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads document-level PDF layer definitions and configurations.</summary>
public static class PdfOptionalContentReader
{
    /// <summary>Reads the optional-content groups and configurations declared by a document.</summary>
    public static PdfOptionalContentInfo Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException("Authenticate the document before reading layers.");
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!TryValue(document, catalog, "OCProperties", out PdfObject? propertiesValue))
            return new PdfOptionalContentInfo();
        PdfDictionary properties = propertiesValue as PdfDictionary
            ?? throw new InvalidOperationException("The catalog /OCProperties value is not a dictionary.");
        PdfArray groupsArray = RequiredArray(document, properties, "OCGs");
        var groupReferences = new List<PdfIndirectReference>(groupsArray.Count);
        var groupNames = new List<string>(groupsArray.Count);
        var printVisibility = new List<bool?>(groupsArray.Count);
        var exportVisibility = new List<bool?>(groupsArray.Count);
        var seen = new HashSet<(int, int)>();
        foreach (PdfObject value in groupsArray)
        {
            PdfIndirectReference reference = value as PdfIndirectReference
                ?? throw new InvalidOperationException("An /OCProperties /OCGs entry is not an indirect reference.");
            if (!seen.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("The /OCProperties /OCGs array contains a duplicate reference.");
            PdfDictionary group = Resolve(document, reference) as PdfDictionary
                ?? throw new InvalidOperationException("An optional-content group is not a dictionary.");
            string type = RequiredName(document, group, "Type");
            if (type != "OCG") throw new InvalidOperationException("An optional-content group does not declare /Type /OCG.");
            groupReferences.Add(reference);
            groupNames.Add(RequiredText(document, group, "Name"));
            printVisibility.Add(UsageState(document, group, "Print", "PrintState"));
            exportVisibility.Add(UsageState(document, group, "Export", "ExportState"));
        }

        var configurations = new List<PdfOptionalContentConfigurationInfo>();
        if (TryValue(document, properties, "D", out PdfObject? defaultValue))
            configurations.Add(ReadConfiguration(document, defaultValue!, true, groupReferences));
        if (TryValue(document, properties, "Configs", out PdfObject? configsValue))
        {
            if (configsValue is not PdfArray configs)
                throw new InvalidOperationException("The /OCProperties /Configs value is not an array.");
            configurations.AddRange(configs.Select(value =>
                ReadConfiguration(document, value, false, groupReferences)));
        }
        PdfOptionalContentConfigurationInfo? selected = configurations.FirstOrDefault(configuration => configuration.IsDefault);
        var groups = groupReferences.Select((reference, index) => new PdfOptionalContentGroupInfo
        {
            ObjectNumber = reference.ObjectNumber,
            Generation = reference.Generation,
            Name = groupNames[index],
            IsInitiallyVisible = selected?.VisibleGroupObjectNumbers.Contains(reference.ObjectNumber) ?? true,
            IsLocked = selected?.LockedGroupObjectNumbers.Contains(reference.ObjectNumber) ?? false,
            IsVisibleWhenPrinting = printVisibility[index],
            IsVisibleWhenExporting = exportVisibility[index]
        }).ToArray();
        return new PdfOptionalContentInfo
        {
            Groups = Array.AsReadOnly(groups),
            Configurations = Array.AsReadOnly(configurations.ToArray())
        };
    }

    private static PdfOptionalContentConfigurationInfo ReadConfiguration(PdfDocument document,
        PdfObject value, bool isDefault, IReadOnlyList<PdfIndirectReference> groups)
    {
        PdfDictionary dictionary = Resolve(document, value) as PdfDictionary
            ?? throw new InvalidOperationException("An optional-content configuration is not a dictionary.");
        string baseState = OptionalName(document, dictionary, "BaseState") ?? "ON";
        if (baseState is not ("ON" or "OFF" or "Unchanged"))
            throw new InvalidOperationException("An optional-content configuration has an invalid /BaseState.");
        var visible = baseState == "OFF" ? new HashSet<int>()
            : groups.Select(group => group.ObjectNumber).ToHashSet();
        ApplyState("ON", true);
        ApplyState("OFF", false);
        HashSet<int> locked = References("Locked");
        IReadOnlyList<int> displayOrder = ReadDisplayOrder();
        return new PdfOptionalContentConfigurationInfo
        {
            Name = OptionalText(document, dictionary, "Name"),
            Creator = OptionalText(document, dictionary, "Creator"),
            IsDefault = isDefault,
            BaseState = baseState switch
            {
                "OFF" => PdfOptionalContentBaseState.Off,
                "Unchanged" => PdfOptionalContentBaseState.Unchanged,
                _ => PdfOptionalContentBaseState.On
            },
            VisibleGroupObjectNumbers = visible,
            LockedGroupObjectNumbers = locked,
            DisplayOrderGroupObjectNumbers = displayOrder
        };

        void ApplyState(string key, bool state)
        {
            foreach (int objectNumber in References(key))
                if (state) visible.Add(objectNumber); else visible.Remove(objectNumber);
        }

        HashSet<int> References(string key)
        {
            if (!TryValue(document, dictionary, key, out PdfObject? arrayValue)) return [];
            if (arrayValue is not PdfArray array)
                throw new InvalidOperationException($"An optional-content configuration /{key} value is not an array.");
            var result = new HashSet<int>();
            foreach (PdfObject item in array)
            {
                PdfIndirectReference reference = item as PdfIndirectReference
                    ?? throw new InvalidOperationException($"An optional-content configuration /{key} entry is not an indirect reference.");
                if (!groups.Any(group => group.ObjectNumber == reference.ObjectNumber
                    && group.Generation == reference.Generation))
                    throw new InvalidOperationException($"An optional-content configuration /{key} entry is not a registered group.");
                result.Add(reference.ObjectNumber);
            }
            return result;
        }

        IReadOnlyList<int> ReadDisplayOrder()
        {
            if (!TryValue(document, dictionary, "Order", out PdfObject? orderValue))
                return Array.AsReadOnly(groups.Select(group => group.ObjectNumber).ToArray());
            if (orderValue is not PdfArray order)
                throw new InvalidOperationException(
                    "An optional-content configuration /Order value is not an array.");
            var result = new List<int>();
            AddItems(order, 0);
            return Array.AsReadOnly(result.ToArray());

            void AddItems(PdfArray items, int depth)
            {
                if (depth > 64)
                    throw new InvalidOperationException(
                        "An optional-content configuration /Order value is too deeply nested.");
                foreach (PdfObject item in items)
                {
                    PdfObject resolved = Resolve(document, item);
                    if (resolved is PdfString) continue;
                    if (resolved is PdfArray children)
                    {
                        AddItems(children, depth + 1);
                        continue;
                    }
                    if (item is not PdfIndirectReference reference
                        || resolved is not PdfDictionary
                        || !groups.Any(group => group.ObjectNumber == reference.ObjectNumber
                            && group.Generation == reference.Generation))
                        throw new InvalidOperationException(
                            "An optional-content configuration /Order entry is not a registered group or subgroup.");
                    if (!result.Contains(reference.ObjectNumber))
                        result.Add(reference.ObjectNumber);
                }
            }
        }
    }

    private static PdfArray RequiredArray(PdfDocument document, PdfDictionary dictionary, string key) =>
        TryValue(document, dictionary, key, out PdfObject? value) && value is PdfArray array
            ? array : throw new InvalidOperationException($"The /OCProperties /{key} value is not an array.");
    private static bool? UsageState(
        PdfDocument document, PdfDictionary group,
        string category, string stateKey)
    {
        if (!TryValue(document, group, "Usage", out PdfObject? usageValue)) return null;
        if (usageValue is not PdfDictionary usage)
            throw new InvalidOperationException(
                "An optional-content group /Usage value is not a dictionary.");
        if (!TryValue(document, usage, category, out PdfObject? categoryValue)) return null;
        if (categoryValue is not PdfDictionary categoryDictionary)
            throw new InvalidOperationException(
                $"An optional-content group /Usage /{category} value is not a dictionary.");
        string? state = OptionalName(document, categoryDictionary, stateKey);
        return state switch
        {
            null => null,
            "ON" => true,
            "OFF" => false,
            _ => throw new InvalidOperationException(
                $"An optional-content group /{stateKey} value is invalid.")
        };
    }
    private static string RequiredName(PdfDocument document, PdfDictionary dictionary, string key) =>
        OptionalName(document, dictionary, key)
        ?? throw new InvalidOperationException($"An optional-content group has no /{key} name.");
    private static string RequiredText(PdfDocument document, PdfDictionary dictionary, string key) =>
        OptionalText(document, dictionary, key)
        ?? throw new InvalidOperationException($"An optional-content group has no /{key} string.");
    private static string? OptionalName(PdfDocument document, PdfDictionary dictionary, string key) =>
        TryValue(document, dictionary, key, out PdfObject? value) && value is PdfName name
            ? name.ValueAsLatin1() : null;
    private static string? OptionalText(PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!TryValue(document, dictionary, key, out PdfObject? value)) return null;
        return value is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, $"An optional-content /{key} value")
            : throw new InvalidOperationException($"An optional-content /{key} value is not a string.");
    }
    private static bool TryValue(PdfDocument document, PdfDictionary dictionary, string key, out PdfObject? value)
    {
        if (!dictionary.TryGetValue(Name(key), out value)) return false;
        value = Resolve(document, value);
        return true;
    }
    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An optional-content reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

/// <summary>The layer definitions and configurations declared by a PDF.</summary>
public sealed partial record PdfOptionalContentInfo
{
    private static readonly PdfOptionalContentInfoJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfOptionalContentInfoJsonContext IndentedJson = new(JsonOptions(true));

    /// <summary>Gets the registered optional-content groups.</summary>
    public IReadOnlyList<PdfOptionalContentGroupInfo> Groups { get; init; } = [];
    /// <summary>Gets the default and alternate optional-content configurations.</summary>
    public IReadOnlyList<PdfOptionalContentConfigurationInfo> Configurations { get; init; } = [];

    /// <summary>Formats layer identities and configuration state for review.</summary>
    public string ToText()
    {
        var output = new StringBuilder();
        output.Append("Layers: ").AppendLine(Groups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (PdfOptionalContentGroupInfo group in Groups)
        {
            output.Append("  ").Append(group.Name).Append(" (object ")
                .Append(group.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ').Append(group.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("): ").Append(group.IsInitiallyVisible ? "visible" : "hidden")
                .Append(", ").Append(group.IsLocked ? "locked" : "unlocked")
                .Append(", print ").Append(State(group.IsVisibleWhenPrinting))
                .Append(", export ").AppendLine(State(group.IsVisibleWhenExporting));
        }
        output.Append("Configurations: ").AppendLine(Configurations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (PdfOptionalContentConfigurationInfo configuration in Configurations)
        {
            output.Append("  ").Append(configuration.IsDefault ? "Default" : "Alternate")
                .Append(": ").Append(configuration.Name ?? "unnamed")
                .Append(", base state ").Append(configuration.BaseState);
            if (!string.IsNullOrWhiteSpace(configuration.Creator))
                output.Append(", creator ").Append(configuration.Creator);
            output.AppendLine();
            output.Append("    Visible objects: ").AppendLine(Objects(configuration.VisibleGroupObjectNumbers));
            output.Append("    Locked objects: ").AppendLine(Objects(configuration.LockedGroupObjectNumbers));
            output.Append("    Display order: ").AppendLine(Objects(configuration.DisplayOrderGroupObjectNumbers));
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>Exports layer identities and effective configuration state as stable JSON.</summary>
    public string ToJson(bool indented = false)
    {
        return JsonSerializer.Serialize(new ReportFile(
            1,
            [.. Groups],
            [.. Configurations.Select(configuration => new ReportConfiguration(
                configuration.Name,
                configuration.Creator,
                configuration.IsDefault,
                configuration.BaseState,
                [.. configuration.VisibleGroupObjectNumbers.Order()],
                [.. configuration.LockedGroupObjectNumbers.Order()],
                configuration.DisplayOrderGroupObjectNumbers))]),
            indented ? IndentedJson.ReportFile : CompactJson.ReportFile);
    }

    private sealed record ReportFile(
        int Version,
        PdfOptionalContentGroupInfo[] Groups,
        ReportConfiguration[] Configurations);

    private sealed record ReportConfiguration(
        string? Name,
        string? Creator,
        bool IsDefault,
        PdfOptionalContentBaseState BaseState,
        int[] VisibleGroupObjectNumbers,
        int[] LockedGroupObjectNumbers,
        IReadOnlyList<int> DisplayOrderGroupObjectNumbers);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented,
        Converters =
        {
            new JsonStringEnumConverter<PdfOptionalContentBaseState>(JsonNamingPolicy.CamelCase)
        }
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfOptionalContentInfoJsonContext : JsonSerializerContext;

    private static string State(bool? value) => value switch
    {
        true => "visible",
        false => "hidden",
        null => "unspecified"
    };

    private static string Objects(IEnumerable<int> values)
    {
        string result = string.Join(", ", values.Order().Select(value =>
            value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return result.Length == 0 ? "none" : result;
    }
}

/// <summary>One registered PDF layer.</summary>
public sealed record PdfOptionalContentGroupInfo
{
    /// <summary>Gets the source object number.</summary>
    public int ObjectNumber { get; init; }
    /// <summary>Gets the source object generation.</summary>
    public int Generation { get; init; }
    /// <summary>Gets the layer display name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets whether the default configuration initially displays the layer.</summary>
    public bool IsInitiallyVisible { get; init; }
    /// <summary>Gets whether the default configuration locks the layer state.</summary>
    public bool IsLocked { get; init; }
    /// <summary>Gets the preferred print visibility, or null when unspecified.</summary>
    public bool? IsVisibleWhenPrinting { get; init; }
    /// <summary>Gets the preferred export visibility, or null when unspecified.</summary>
    public bool? IsVisibleWhenExporting { get; init; }
}

/// <summary>One optional-content configuration.</summary>
public sealed record PdfOptionalContentConfigurationInfo
{
    /// <summary>Gets the optional configuration name.</summary>
    public string? Name { get; init; }
    /// <summary>Gets the optional creator name.</summary>
    public string? Creator { get; init; }
    /// <summary>Gets whether this is the document's default configuration.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Gets the configuration base state.</summary>
    public PdfOptionalContentBaseState BaseState { get; init; }
    /// <summary>Gets visible group object numbers after applying explicit overrides.</summary>
    public IReadOnlySet<int> VisibleGroupObjectNumbers { get; init; } = new HashSet<int>();
    /// <summary>Gets locked group object numbers.</summary>
    public IReadOnlySet<int> LockedGroupObjectNumbers { get; init; } = new HashSet<int>();
    /// <summary>Gets group object numbers in the configuration's display order.</summary>
    public IReadOnlyList<int> DisplayOrderGroupObjectNumbers { get; init; } = [];
}

/// <summary>The initial state applied before explicit group overrides.</summary>
public enum PdfOptionalContentBaseState
{
    /// <summary>All groups begin enabled.</summary>
    On,
    /// <summary>All groups begin disabled.</summary>
    Off,
    /// <summary>The viewer retains its current group states.</summary>
    Unchanged
}
