using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads portfolio collection settings without interpreting presentation data.</summary>
public static partial class PdfCollectionReader
{
    private static readonly PdfCollectionReaderJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfCollectionReaderJsonContext IndentedJson = new(JsonOptions(true));

    /// <summary>Formats portfolio presentation metadata for review.</summary>
    public static string ToText(PdfDocument document)
    {
        PdfCollectionInfo? collection = Read(document);
        if (collection is null) return "PDF portfolio: none";
        var text = new StringBuilder();
        text.Append("PDF portfolio: ").Append(collection.View);
        if (collection.View == PdfCollectionView.Unknown && collection.RawViewName is not null)
            text.Append(" (").Append(collection.RawViewName).Append(')');
        text.AppendLine();
        text.Append("Initial document: ").AppendLine(collection.InitialDocument ?? "none");
        text.AppendLine($"Fields: {collection.Fields.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (PdfCollectionFieldInfo field in collection.Fields)
        {
            text.Append("  ").Append(field.Key).Append(": ").Append(field.DisplayName)
                .Append(", type ").Append(field.Subtype ?? "unspecified")
                .Append(", ").Append(field.IsVisible ? "visible" : "hidden")
                .Append(", ").Append(field.IsEditable ? "editable" : "read-only");
            if (field.Order.HasValue)
                text.Append(", order ").Append(field.Order.Value.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
        }
        text.AppendLine($"Sort rules: {collection.Sort.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (PdfCollectionSortInfo sort in collection.Sort)
            text.Append("  ").Append(sort.Key).Append(": ")
                .AppendLine(sort.Ascending ? "ascending" : "descending");
        text.AppendLine($"Folders: {collection.Folders.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (PdfCollectionFolderInfo folder in collection.Folders)
        {
            text.Append(' ', (folder.Depth + 1) * 2).Append(folder.Name)
                .Append(" (ID ").Append(folder.Id.ToString(CultureInfo.InvariantCulture))
                .Append(", object ").Append(folder.ObjectNumber.ToString(CultureInfo.InvariantCulture)).AppendLine(")");
            if (!string.IsNullOrWhiteSpace(folder.Description))
                text.Append(' ', (folder.Depth + 2) * 2).Append("Description: ").AppendLine(folder.Description);
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Exports portfolio metadata without embedded file payloads.</summary>
    public static string ToJson(PdfDocument document, bool indented = false)
    {
        PdfCollectionInfo? collection = Read(document);
        ReportCollection? report = collection is null ? null : new(
            collection.View.ToString(),
            collection.RawViewName,
            collection.InitialDocument,
            collection.Fields,
            collection.Sort,
            collection.Folders);
        return JsonSerializer.Serialize(new ReportFile(1, report is not null, report),
            indented ? IndentedJson.ReportFile : CompactJson.ReportFile);
    }

    private sealed record ReportFile(
        int Version, bool HasCollection, ReportCollection? Collection);

    private sealed record ReportCollection(
        string View,
        string? RawViewName,
        string? InitialDocument,
        IReadOnlyList<PdfCollectionFieldInfo> Fields,
        IReadOnlyList<PdfCollectionSortInfo> Sort,
        IReadOnlyList<PdfCollectionFolderInfo> Folders);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfCollectionReaderJsonContext : JsonSerializerContext;

    /// <summary>Reads the document catalog's optional collection dictionary.</summary>
    public static PdfCollectionInfo? Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before reading collection metadata.");
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(Name("Collection"), out PdfObject? collectionValue))
            return null;
        PdfDictionary collection = Dictionary(document, collectionValue,
            "The catalog /Collection value is not a dictionary.");
        string? rawView = OptionalName(document, collection, "View");
        PdfCollectionView view = rawView switch
        {
            null or "D" => PdfCollectionView.Details,
            "T" => PdfCollectionView.Tile,
            "H" => PdfCollectionView.Hidden,
            _ => PdfCollectionView.Unknown
        };
        return new PdfCollectionInfo
        {
            View = view,
            RawViewName = rawView,
            InitialDocument = OptionalText(document, collection, "D"),
            Fields = ReadFields(document, collection),
            Sort = ReadSort(document, collection),
            Folders = ReadFolders(document, collection)
        };
    }

    private static IReadOnlyList<PdfCollectionFolderInfo> ReadFolders(
        PdfDocument document, PdfDictionary collection)
    {
        if (!collection.TryGetValue(Name("Folders"), out PdfObject? rootValue)) return [];
        PdfIndirectReference rootReference = Reference(document, rootValue,
            "The collection /Folders value is not an indirect reference.");
        var result = new List<PdfCollectionFolderInfo>();
        var visited = new HashSet<(int, int)>();
        var identifiers = new HashSet<long>();
        var folderReferences = new Dictionary<long, (int ObjectNumber, int Generation)>();
        ReadChain(rootReference, null, 0, true);
        return Array.AsReadOnly(result.ToArray());

        void ReadChain(PdfIndirectReference reference, long? parentId, int depth, bool root)
        {
            if (depth > 64)
                throw new NotSupportedException("A collection folder hierarchy is too deeply nested.");
            while (true)
            {
                PdfIndirectReference identity = Reference(document, reference,
                    "A collection folder is not an indirect reference.");
                if (!visited.Add((identity.ObjectNumber, identity.Generation)))
                    throw new InvalidOperationException(
                        "A collection folder hierarchy contains a cycle or reused folder.");
                PdfDictionary folder = Dictionary(document, identity,
                    "A collection folder is not a dictionary.");
                long id = RequiredNonnegativeInteger(document, folder, "ID");
                if (!identifiers.Add(id))
                    throw new InvalidOperationException($"A collection folder reuses ID {id}.");
                folderReferences.Add(id, (identity.ObjectNumber, identity.Generation));
                if (root && folder.ContainsKey(Name("Parent")))
                    throw new InvalidOperationException(
                        "A root collection folder contains a parent entry.");
                if (!root)
                {
                    PdfIndirectReference parentReference = folder.TryGetValue(Name("Parent"), out PdfObject? parent)
                        ? Reference(document, parent,
                            "A collection folder has no indirect parent reference.")
                        : throw new InvalidOperationException(
                            "A collection folder has no parent reference.");
                    var expected = folderReferences[parentId!.Value];
                    if (parentReference.ObjectNumber != expected.ObjectNumber
                        || parentReference.Generation != expected.Generation)
                        throw new InvalidOperationException(
                            "A collection folder parent reference is not reciprocal.");
                }
                result.Add(new PdfCollectionFolderInfo(id, identity.ObjectNumber,
                    RequiredText(document, folder, "Name", "A collection folder has no /Name string."),
                    OptionalText(document, folder, "Desc"),
                    OptionalText(document, folder, "CreationDate"),
                    OptionalText(document, folder, "ModDate"), parentId, depth));
                if (folder.TryGetValue(Name("Child"), out PdfObject? child))
                    ReadChain(Reference(document, child,
                        "A collection folder /Child value is not an indirect reference."), id,
                        depth + 1, false);
                if (!folder.TryGetValue(Name("Next"), out PdfObject? next)) return;
                reference = Reference(document, next,
                    "A collection folder /Next value is not an indirect reference.");
            }
        }
    }

    private static IReadOnlyList<PdfCollectionFieldInfo> ReadFields(
        PdfDocument document, PdfDictionary collection)
    {
        if (!collection.TryGetValue(Name("Schema"), out PdfObject? schemaValue)) return [];
        PdfDictionary schema = Dictionary(document, schemaValue,
            "The collection /Schema value is not a dictionary.");
        var fields = new List<PdfCollectionFieldInfo>();
        foreach ((PdfName key, PdfObject value) in schema)
        {
            PdfDictionary field = Dictionary(document, value,
                $"The collection field /{key.ValueAsLatin1()} is not a dictionary.");
            fields.Add(new PdfCollectionFieldInfo
            {
                Key = key.ValueAsLatin1(),
                DisplayName = RequiredText(document, field, "N",
                    $"The collection field /{key.ValueAsLatin1()} has no /N string."),
                Subtype = OptionalName(document, field, "Subtype"),
                Order = OptionalInteger(document, field, "O"),
                IsVisible = OptionalBoolean(document, field, "V") ?? true,
                IsEditable = OptionalBoolean(document, field, "E") ?? false
            });
        }
        return Array.AsReadOnly(fields.OrderBy(field => field.Order ?? int.MaxValue)
            .ThenBy(field => field.Key, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<PdfCollectionSortInfo> ReadSort(
        PdfDocument document, PdfDictionary collection)
    {
        if (!collection.TryGetValue(Name("Sort"), out PdfObject? sortValue)) return [];
        PdfDictionary sort = Dictionary(document, sortValue,
            "The collection /Sort value is not a dictionary.");
        if (!sort.TryGetValue(Name("S"), out PdfObject? keysValue))
            throw new InvalidOperationException("The collection /Sort dictionary has no /S key.");
        string[] keys = Names(document, keysValue, "The collection /Sort /S value");
        bool[] ascending = sort.TryGetValue(Name("A"), out PdfObject? orderValue)
            ? Booleans(document, orderValue, keys.Length) : [true];
        return Array.AsReadOnly(keys.Select((key, index) => new PdfCollectionSortInfo(
            key, ascending.Length == 1 ? ascending[0] : ascending[index])).ToArray());
    }

    private static string[] Names(PdfDocument document, PdfObject value, string label)
    {
        PdfObject resolved = Resolve(document, value);
        if (resolved is PdfName name) return [name.ValueAsLatin1()];
        if (resolved is not PdfArray array || array.Count == 0)
            throw new InvalidOperationException($"{label} is not a name or nonempty name array.");
        return array.Select(item => Resolve(document, item) is PdfName itemName
            ? itemName.ValueAsLatin1()
            : throw new InvalidOperationException($"{label} contains a value that is not a name."))
            .ToArray();
    }

    private static bool[] Booleans(
        PdfDocument document, PdfObject value, int keyCount)
    {
        PdfObject resolved = Resolve(document, value);
        if (resolved is PdfBoolean boolean) return [boolean.Value];
        if (resolved is not PdfArray array || array.Count != keyCount)
            throw new InvalidOperationException(
                "The collection /Sort /A array must match the number of sort keys.");
        return array.Select(item => Resolve(document, item) is PdfBoolean itemBoolean
            ? itemBoolean.Value
            : throw new InvalidOperationException(
                "The collection /Sort /A array contains a value that is not Boolean."))
            .ToArray();
    }

    private static PdfDictionary Dictionary(
        PdfDocument document, PdfObject value, string error) =>
        Resolve(document, value) as PdfDictionary
        ?? throw new InvalidOperationException(error);

    private static string RequiredText(PdfDocument document, PdfDictionary dictionary,
        string key, string error) => OptionalText(document, dictionary, key)
        ?? throw new InvalidOperationException(error);

    private static string? OptionalText(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span,
                $"A collection /{key} value")
            : throw new InvalidOperationException(
                $"A collection /{key} value is not a string.");
    }

    private static string? OptionalName(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfName name ? name.ValueAsLatin1()
            : throw new InvalidOperationException(
                $"A collection /{key} value is not a name.");
    }

    private static int? OptionalInteger(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        if (Resolve(document, value) is not PdfInteger integer
            || integer.Value is < int.MinValue or > int.MaxValue)
            throw new InvalidOperationException(
                $"A collection /{key} value is not an integer.");
        return (int)integer.Value;
    }

    private static long RequiredNonnegativeInteger(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)
            || Resolve(document, value) is not PdfInteger integer || integer.Value < 0)
            throw new InvalidOperationException(
                $"A collection folder has no nonnegative /{key} integer.");
        return integer.Value;
    }

    private static bool? OptionalBoolean(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfBoolean boolean ? boolean.Value
            : throw new InvalidOperationException(
                $"A collection /{key} value is not Boolean.");
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A collection reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfIndirectReference Reference(
        PdfDocument document, PdfObject value, string error)
    {
        var visited = new HashSet<(int, int)>();
        PdfIndirectReference? identity = null;
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A collection reference contains a cycle.");
            identity = reference;
            value = document.Resolve(reference);
        }
        return identity ?? throw new InvalidOperationException(error);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

/// <summary>The collection presentation mode requested by a PDF portfolio.</summary>
public enum PdfCollectionView
{
    /// <summary>Show collection fields in a details view.</summary>
    Details,
    /// <summary>Show collection entries as tiles.</summary>
    Tile,
    /// <summary>Hide the collection navigator.</summary>
    Hidden,
    /// <summary>Preserve an unrecognized view name for inspection.</summary>
    Unknown
}

/// <summary>Portfolio collection metadata from the document catalog.</summary>
public sealed record PdfCollectionInfo
{
    /// <summary>Gets the requested presentation mode.</summary>
    public PdfCollectionView View { get; init; }
    /// <summary>Gets the original /View name, including unrecognized values.</summary>
    public string? RawViewName { get; init; }
    /// <summary>Gets the optional embedded file to select initially.</summary>
    public string? InitialDocument { get; init; }
    /// <summary>Gets the collection's standard and custom fields.</summary>
    public required IReadOnlyList<PdfCollectionFieldInfo> Fields { get; init; }
    /// <summary>Gets the collection's ordered sort keys.</summary>
    public required IReadOnlyList<PdfCollectionSortInfo> Sort { get; init; }
    /// <summary>Gets portfolio folders in hierarchy and sibling order.</summary>
    public required IReadOnlyList<PdfCollectionFolderInfo> Folders { get; init; }
}

/// <summary>One folder in a PDF portfolio hierarchy.</summary>
public sealed record PdfCollectionFolderInfo(
    long Id, int ObjectNumber, string Name, string? Description,
    string? CreationDate, string? ModificationDate, long? ParentId, int Depth);

/// <summary>Defines one folder when authoring a PDF portfolio hierarchy.</summary>
public sealed record PdfCollectionFolder(
    long Id, string Name, long? ParentId = null, string? Description = null,
    string? CreationDate = null, string? ModificationDate = null);

/// <summary>One standard or custom portfolio field.</summary>
public sealed record PdfCollectionFieldInfo
{
    /// <summary>Gets the field key stored in collection item dictionaries.</summary>
    public required string Key { get; init; }
    /// <summary>Gets the field's display name.</summary>
    public required string DisplayName { get; init; }
    /// <summary>Gets the PDF field subtype name.</summary>
    public string? Subtype { get; init; }
    /// <summary>Gets the optional display order.</summary>
    public int? Order { get; init; }
    /// <summary>Gets whether the field is visible.</summary>
    public bool IsVisible { get; init; }
    /// <summary>Gets whether a viewer may edit the field.</summary>
    public bool IsEditable { get; init; }
}

/// <summary>One portfolio collection sort key.</summary>
public sealed record PdfCollectionSortInfo(string Key, bool Ascending);
