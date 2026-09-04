using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits optional-content groups while preserving their existing configuration.</summary>
public static class PdfOptionalContentEditor
{
    private static readonly PdfName NameKey = new("Name"u8);
    private static readonly PdfName UsageKey = new("Usage"u8);
    private static readonly PdfName OptionalContentPropertiesKey = new("OCProperties"u8);
    private static readonly PdfName DefaultConfigurationKey = new("D"u8);
    private static readonly PdfName BaseStateKey = new("BaseState"u8);
    private static readonly PdfName OnKey = new("ON"u8);
    private static readonly PdfName OffKey = new("OFF"u8);
    private static readonly PdfName LockedKey = new("Locked"u8);
    private static readonly PdfName OrderKey = new("Order"u8);
    private static readonly PdfName CreatorKey = new("Creator"u8);
    private static readonly PdfName GroupsKey = new("OCGs"u8);
    private static readonly PdfName TypeKey = new("Type"u8);

    /// <summary>Creates and registers a layer in the default configuration.</summary>
    public static byte[] AddGroup(PdfDocument document, string name,
        bool initiallyVisible = true, bool locked = false,
        bool? printVisible = null, bool? exportVisible = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A layer name is required.", nameof(name));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        if (info.Groups.Any(group => string.Equals(group.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException("Layer names must be unique.", nameof(name));

        PdfPageTree tree = PdfPageTree.Read(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        var groupEntries = new Dictionary<PdfName, PdfObject>
        {
            [TypeKey] = new PdfName("OCG"u8),
            [NameKey] = UnicodeString(name)
        };
        var usageEntries = new Dictionary<PdfName, PdfObject>();
        AddUsage("Print", "PrintState", printVisible);
        AddUsage("Export", "ExportState", exportVisible);
        if (usageEntries.Count != 0)
            groupEntries[UsageKey] = new PdfDictionary(usageEntries);
        PdfIndirectReference groupReference = update.AddObject(new PdfDictionary(groupEntries));

        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
        {
            var configurationEntries = new Dictionary<PdfName, PdfObject>
            {
                [BaseStateKey] = new PdfName("ON"u8),
                [OrderKey] = new PdfArray([groupReference])
            };
            if (!initiallyVisible)
                configurationEntries[OffKey] = new PdfArray([groupReference]);
            if (locked)
                configurationEntries[LockedKey] = new PdfArray([groupReference]);
            var properties = new PdfDictionary(new Dictionary<PdfName, PdfObject>
            {
                [GroupsKey] = new PdfArray([groupReference]),
                [DefaultConfigurationKey] = new PdfDictionary(configurationEntries)
            });
            var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
            catalogEntries[OptionalContentPropertiesKey] = properties;
            return update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                new PdfDictionary(catalogEntries)).Build();
        }

        (PdfDictionary existingProperties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!existingProperties.TryGetValue(DefaultConfigurationKey,
                out PdfObject? configurationValue))
            throw new InvalidOperationException(
                "The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntriesExisting = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        PdfOptionalContentConfigurationInfo defaultInfo = info.Configurations.Single(
            candidate => candidate.IsDefault);
        AddState(configurationEntriesExisting,
            initiallyVisible == (defaultInfo.BaseState != PdfOptionalContentBaseState.Off)
                ? null : initiallyVisible ? OnKey : OffKey);
        AddState(configurationEntriesExisting, locked ? LockedKey : null);
        PdfObject[] order = ResolvedArray(document,
            configurationEntriesExisting.GetValueOrDefault(OrderKey), "display order");
        configurationEntriesExisting[OrderKey] = new PdfArray([.. order, groupReference]);
        var replacementConfiguration = new PdfDictionary(configurationEntriesExisting);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);

        var propertiesEntries = existingProperties.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        PdfObject[] groups = ResolvedArray(document,
            propertiesEntries.GetValueOrDefault(GroupsKey), "group list");
        propertiesEntries[GroupsKey] = new PdfArray([.. groups, groupReference]);
        if (configurationReference is null)
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
        var replacementProperties = new PdfDictionary(propertiesEntries);
        if (propertiesReference is not null)
            update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
        else
        {
            var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
            catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
            update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                new PdfDictionary(catalogEntries));
        }
        return update.Build();

        void AddUsage(string category, string state, bool? visible)
        {
            if (!visible.HasValue) return;
            usageEntries[new PdfName(System.Text.Encoding.ASCII.GetBytes(category))] =
                new PdfDictionary(new Dictionary<PdfName, PdfObject>
                {
                    [new PdfName(System.Text.Encoding.ASCII.GetBytes(state))] =
                        new PdfName(visible.Value ? "ON"u8 : "OFF"u8)
                });
        }

        void AddState(Dictionary<PdfName, PdfObject> entries, PdfName? key)
        {
            if (key is null) return;
            PdfObject[] values = ResolvedArray(document, entries.GetValueOrDefault(key),
                "configuration state");
            entries[key] = new PdfArray([.. values, groupReference]);
        }
    }

    /// <summary>Renames one registered layer by its source object number.</summary>
    public static byte[] RenameGroup(PdfDocument document, int objectNumber, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A layer name is required.", nameof(name));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        PdfOptionalContentGroupInfo group = FindGroup(info, objectNumber);
        if (info.Groups.Any(item => item.ObjectNumber != objectNumber
                && string.Equals(item.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException("Layer names must be unique.", nameof(name));
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The optional-content group is not a dictionary.");
        var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[NameKey] = UnicodeString(name);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(entries))
            .Build();
    }

    /// <summary>Sets or clears a layer's preferred print visibility.</summary>
    public static byte[] SetPrintVisibility(
        PdfDocument document, int objectNumber, bool? visible) =>
        SetUsageVisibility(document, objectNumber, "Print", "PrintState", visible);

    /// <summary>Sets or clears a layer's preferred export visibility.</summary>
    public static byte[] SetExportVisibility(
        PdfDocument document, int objectNumber, bool? visible) =>
        SetUsageVisibility(document, objectNumber, "Export", "ExportState", visible);

    /// <summary>Sets a layer's initial visibility in the default configuration.</summary>
    public static byte[] SetInitialVisibility(
        PdfDocument document, int objectNumber, bool visible)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), objectNumber);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException("The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        PdfObject[] on = StateReferences(document, configurationEntries.GetValueOrDefault(OnKey), reference);
        PdfObject[] off = StateReferences(document, configurationEntries.GetValueOrDefault(OffKey), reference);
        if (visible)
            on = [.. on, reference];
        else
            off = [.. off, reference];
        SetArray(configurationEntries, OnKey, on);
        SetArray(configurationEntries, OffKey, off);
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    /// <summary>Sets whether the default configuration locks a layer's visibility.</summary>
    public static byte[] SetLocked(PdfDocument document, int objectNumber, bool locked)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), objectNumber);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException("The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        PdfObject[] values = StateReferences(
            document, configurationEntries.GetValueOrDefault(LockedKey), reference);
        if (locked) values = [.. values, reference];
        SetArray(configurationEntries, LockedKey, values);
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    /// <summary>Replaces the default configuration's flat layer display order.</summary>
    public static byte[] SetDisplayOrder(
        PdfDocument document, IReadOnlyList<int> objectNumbers)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(objectNumbers);
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        int[] registered = [.. info.Groups.Select(group => group.ObjectNumber).Order()];
        int[] requested = [.. objectNumbers.Order()];
        if (!registered.SequenceEqual(requested))
            throw new ArgumentException(
                "The display order must contain every registered layer exactly once.",
                nameof(objectNumbers));
        Dictionary<int, PdfIndirectReference> references = info.Groups.ToDictionary(
            group => group.ObjectNumber,
            group => new PdfIndirectReference(group.ObjectNumber, group.Generation));
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException("The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        configurationEntries[OrderKey] = new PdfArray(objectNumbers.Select(
            objectNumber => (PdfObject)references[objectNumber]));
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    /// <summary>Replaces the default configuration's nested layer display order.</summary>
    public static byte[] SetDisplayOrderTree(
        PdfDocument document, IReadOnlyList<PdfOptionalContentOrderItem> items)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(items);
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        Dictionary<int, PdfIndirectReference> references = info.Groups.ToDictionary(
            group => group.ObjectNumber,
            group => new PdfIndirectReference(group.ObjectNumber, group.Generation));
        var used = new HashSet<int>();
        PdfObject[] order = [.. items.Select(item => Build(item, 0))];
        if (!used.SetEquals(references.Keys))
            throw new ArgumentException(
                "The display order must contain every registered layer exactly once.",
                nameof(items));

        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException(
                "The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        configurationEntries[OrderKey] = new PdfArray(order);
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();

        PdfObject Build(PdfOptionalContentOrderItem item, int depth)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (depth > 64)
                throw new ArgumentException(
                    "The display order is too deeply nested.", nameof(items));
            if (item.GroupObjectNumber is int objectNumber)
            {
                if (item.Label is not null || item.Children.Count != 0
                    || !references.TryGetValue(objectNumber, out PdfIndirectReference? reference)
                    || !used.Add(objectNumber))
                    throw new ArgumentException(
                        "A layer entry must reference one unique registered layer.", nameof(items));
                return reference;
            }
            if (string.IsNullOrWhiteSpace(item.Label) || item.Children.Count == 0)
                throw new ArgumentException(
                    "A layer folder requires a name and at least one child.", nameof(items));
            return new PdfArray([UnicodeString(item.Label),
                .. item.Children.Select(child => Build(child, depth + 1))]);
        }
    }

    /// <summary>Sets or clears the default layer configuration name and creator.</summary>
    public static byte[] SetDefaultConfigurationMetadata(
        PdfDocument document, string? name, string? creator)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (name is not null && string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "A layer configuration name cannot be empty.", nameof(name));
        if (creator is not null && string.IsNullOrWhiteSpace(creator))
            throw new ArgumentException(
                "A layer configuration creator cannot be empty.", nameof(creator));
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException(
                "The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        SetOptionalText(configurationEntries, NameKey, name);
        SetOptionalText(configurationEntries, CreatorKey, creator);
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    /// <summary>Sets the default state applied before explicit layer overrides.</summary>
    public static byte[] SetDefaultBaseState(
        PdfDocument document, PdfOptionalContentBaseState baseState)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(baseState))
            throw new ArgumentOutOfRangeException(nameof(baseState));
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException(
                "The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        configurationEntries[BaseStateKey] = baseState switch
        {
            PdfOptionalContentBaseState.On => new PdfName("ON"u8),
            PdfOptionalContentBaseState.Off => new PdfName("OFF"u8),
            PdfOptionalContentBaseState.Unchanged => new PdfName("Unchanged"u8),
            _ => throw new ArgumentOutOfRangeException(nameof(baseState))
        };
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    private static byte[] SetUsageVisibility(PdfDocument document, int objectNumber,
        string categoryName, string stateName, bool? visible)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), objectNumber);
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The optional-content group is not a dictionary.");
        var groupEntries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        var categoryKey = new PdfName(System.Text.Encoding.ASCII.GetBytes(categoryName));
        var stateKey = new PdfName(System.Text.Encoding.ASCII.GetBytes(stateName));
        var usageEntries = ResolveDictionary(document, groupEntries.GetValueOrDefault(UsageKey));
        var categoryEntries = ResolveDictionary(document, usageEntries.GetValueOrDefault(categoryKey));
        if (visible.HasValue)
            categoryEntries[stateKey] = new PdfName(visible.Value ? "ON"u8 : "OFF"u8);
        else
            categoryEntries.Remove(stateKey);
        if (categoryEntries.Count == 0)
            usageEntries.Remove(categoryKey);
        else
            usageEntries[categoryKey] = new PdfDictionary(categoryEntries);
        if (usageEntries.Count == 0)
            groupEntries.Remove(UsageKey);
        else
            groupEntries[UsageKey] = new PdfDictionary(usageEntries);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(groupEntries))
            .Build();
    }

    private static PdfOptionalContentGroupInfo FindGroup(
        PdfOptionalContentInfo info, int objectNumber) =>
        info.Groups.FirstOrDefault(item => item.ObjectNumber == objectNumber)
        ?? throw new KeyNotFoundException(
            $"Optional-content group {objectNumber} is not registered.");

    private static Dictionary<PdfName, PdfObject> ResolveDictionary(
        PdfDocument document, PdfObject? value) => value is null
        ? []
        : ((value is PdfIndirectReference reference ? document.Resolve(reference) : value)
            as PdfDictionary
            ?? throw new InvalidOperationException("Optional-content usage metadata is not a dictionary."))
        .ToDictionary(entry => entry.Key, entry => entry.Value);

    private static (PdfDictionary Dictionary, PdfIndirectReference? Reference)
        ResolveDictionaryWithReference(PdfDocument document, PdfObject value, string description)
    {
        PdfIndirectReference? lastReference = null;
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException($"{description} has an invalid reference chain.");
            lastReference = reference;
            value = document.Resolve(reference);
        }
        return (value as PdfDictionary
            ?? throw new InvalidOperationException($"{description} is not a dictionary."),
            lastReference);
    }

    private static PdfObject[] StateReferences(PdfDocument document, PdfObject? value,
        PdfIndirectReference excluded)
    {
        if (value is null) return [];
        PdfObject resolved = value is PdfIndirectReference reference
            ? document.Resolve(reference) : value;
        PdfArray array = resolved as PdfArray
            ?? throw new InvalidOperationException("An optional-content state value is not an array.");
        return [.. array.Where(item => item is not PdfIndirectReference candidate
            || candidate.ObjectNumber != excluded.ObjectNumber
            || candidate.Generation != excluded.Generation)];
    }

    private static PdfObject[] ResolvedArray(
        PdfDocument document, PdfObject? value, string description)
    {
        if (value is null) return [];
        PdfObject resolved = value is PdfIndirectReference reference
            ? document.Resolve(reference) : value;
        return resolved is PdfArray array ? [.. array] : throw new InvalidOperationException(
            $"The optional-content {description} is not an array.");
    }

    private static void SetArray(Dictionary<PdfName, PdfObject> entries,
        PdfName key, PdfObject[] values)
    {
        if (values.Length == 0) entries.Remove(key);
        else entries[key] = new PdfArray(values);
    }

    private static void SetOptionalText(Dictionary<PdfName, PdfObject> entries,
        PdfName key, string? value)
    {
        if (value is null) entries.Remove(key);
        else entries[key] = UnicodeString(value);
    }

    private static PdfString UnicodeString(string value)
    {
        byte[] text = PdfUnicodeEncoding.EncodeBigEndian(value);
        byte[] bytes = new byte[text.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        text.CopyTo(bytes, 2);
        return new PdfString(bytes, PdfStringForm.Hexadecimal);
    }
}

/// <summary>One layer or named folder in an optional-content display-order tree.</summary>
public sealed record PdfOptionalContentOrderItem
{
    private PdfOptionalContentOrderItem(
        int? groupObjectNumber, string? label,
        IReadOnlyList<PdfOptionalContentOrderItem> children)
    {
        GroupObjectNumber = groupObjectNumber;
        Label = label;
        Children = children;
    }

    /// <summary>Gets the source object number for a layer entry.</summary>
    public int? GroupObjectNumber { get; }
    /// <summary>Gets the name for a folder entry.</summary>
    public string? Label { get; }
    /// <summary>Gets the folder's child entries.</summary>
    public IReadOnlyList<PdfOptionalContentOrderItem> Children { get; }

    /// <summary>Creates a layer entry.</summary>
    public static PdfOptionalContentOrderItem Layer(int objectNumber) =>
        new(objectNumber, null, []);

    /// <summary>Creates a named folder entry.</summary>
    public static PdfOptionalContentOrderItem Folder(
        string label, params PdfOptionalContentOrderItem[] children) =>
        new(null, label, Array.AsReadOnly(children.ToArray()));
}
