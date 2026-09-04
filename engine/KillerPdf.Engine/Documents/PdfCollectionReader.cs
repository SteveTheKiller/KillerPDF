using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads portfolio collection settings without interpreting presentation data.</summary>
public static class PdfCollectionReader
{
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
            Sort = ReadSort(document, collection)
        };
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
}

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
