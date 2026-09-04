using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits portfolio presentation settings while preserving collection schema and sort data.</summary>
public static class PdfCollectionEditor
{
    private static readonly PdfName CollectionName = Name("Collection");
    private static readonly HashSet<string> FieldSubtypes =
        ["S", "D", "N", "F", "Desc", "ModDate", "CreationDate", "Size"];

    /// <summary>Sets the portfolio view and optional initially selected embedded document.</summary>
    public static byte[] SetPresentation(
        PdfDocument document, PdfCollectionView view, string? initialDocument = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (view is PdfCollectionView.Unknown || !Enum.IsDefined(view))
            throw new ArgumentOutOfRangeException(nameof(view));
        if (initialDocument is not null && string.IsNullOrWhiteSpace(initialDocument))
            throw new ArgumentException(
                "An initial portfolio document cannot be empty.", nameof(initialDocument));
        PdfPageTree tree = PdfPageTree.Read(document);
        PdfObject? current = tree.Catalog.GetValueOrDefault(CollectionName);
        PdfIndirectReference? reference = current as PdfIndirectReference;
        PdfDictionary collection = current is null ? new PdfDictionary([])
            : Resolve(document, current) as PdfDictionary
                ?? throw new InvalidOperationException(
                    "The catalog /Collection value is not a dictionary.");
        var entries = collection.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[Name("Type")] = Name("Collection");
        entries[Name("View")] = Name(view switch
        {
            PdfCollectionView.Details => "D",
            PdfCollectionView.Tile => "T",
            PdfCollectionView.Hidden => "H",
            _ => throw new ArgumentOutOfRangeException(nameof(view))
        });
        if (initialDocument is null) entries.Remove(Name("D"));
        else entries[Name("D")] = Text(initialDocument);
        var replacement = new PdfDictionary(entries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (reference is not null)
            update.ReplaceObject(reference.ObjectNumber, replacement);
        else
        {
            var catalog = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
            catalog[CollectionName] = replacement;
            update.ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalog));
        }
        return update.Build();
    }

    /// <summary>Removes portfolio presentation metadata without removing embedded files.</summary>
    public static byte[] Clear(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.ContainsKey(CollectionName)) return document.Source.ToArray();
        var catalog = tree.Catalog
            .Where(entry => !entry.Key.Equals(CollectionName));
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalog))
            .Build();
    }

    /// <summary>Replaces the portfolio field schema and ordered sort rules.</summary>
    public static byte[] SetSchema(PdfDocument document,
        IEnumerable<PdfCollectionFieldInfo> fields,
        IEnumerable<PdfCollectionSortInfo>? sort = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fields);
        PdfCollectionFieldInfo[] selectedFields = fields.ToArray();
        if (selectedFields.Length == 0)
            throw new ArgumentException(
                "A portfolio schema requires at least one field.", nameof(fields));
        if (selectedFields.Any(field => field is null
            || string.IsNullOrWhiteSpace(field.Key)
            || string.IsNullOrWhiteSpace(field.DisplayName)
            || field.Subtype is null || !FieldSubtypes.Contains(field.Subtype)))
            throw new ArgumentException(
                "Every portfolio field needs a key, display name, and supported subtype.",
                nameof(fields));
        if (selectedFields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count()
            != selectedFields.Length)
            throw new ArgumentException("Portfolio field keys must be unique.", nameof(fields));
        PdfCollectionSortInfo[] selectedSort = sort?.ToArray() ?? [];
        HashSet<string> keys = selectedFields.Select(field => field.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (selectedSort.Any(rule => rule is null || !keys.Contains(rule.Key))
            || selectedSort.Select(rule => rule.Key).Distinct(StringComparer.Ordinal).Count()
                != selectedSort.Length)
            throw new ArgumentException(
                "Portfolio sort rules must reference unique schema fields.", nameof(sort));

        var schemaEntries = new List<KeyValuePair<PdfName, PdfObject>>();
        foreach (PdfCollectionFieldInfo field in selectedFields)
        {
            var fieldEntries = new List<KeyValuePair<PdfName, PdfObject>>
            {
                new(Name("N"), Text(field.DisplayName)),
                new(Name("Subtype"), Name(field.Subtype!))
            };
            if (field.Order.HasValue)
                fieldEntries.Add(new(Name("O"), new PdfInteger(field.Order.Value)));
            if (!field.IsVisible) fieldEntries.Add(new(Name("V"), new PdfBoolean(false)));
            if (field.IsEditable) fieldEntries.Add(new(Name("E"), new PdfBoolean(true)));
            schemaEntries.Add(new(Name(field.Key), new PdfDictionary(fieldEntries)));
        }

        return UpdateCollection(document, entries =>
        {
            entries[Name("Type")] = Name("Collection");
            entries[Name("Schema")] = new PdfDictionary(schemaEntries);
            if (selectedSort.Length == 0) entries.Remove(Name("Sort"));
            else
            {
                PdfObject sortKeys = selectedSort.Length == 1
                    ? Name(selectedSort[0].Key)
                    : new PdfArray(selectedSort.Select(rule => (PdfObject)Name(rule.Key)));
                PdfObject directions = selectedSort.Length == 1
                    ? new PdfBoolean(selectedSort[0].Ascending)
                    : new PdfArray(selectedSort.Select(rule =>
                        (PdfObject)new PdfBoolean(rule.Ascending)));
                entries[Name("Sort")] = new PdfDictionary([
                    new(Name("S"), sortKeys), new(Name("A"), directions)]);
            }
        });
    }

    /// <summary>Replaces one attachment's values for the declared portfolio schema.</summary>
    public static byte[] SetItemValues(PdfDocument document, string fileName,
        IEnumerable<PdfCollectionItemValue> values)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(values);
        PdfCollectionInfo collection = PdfCollectionReader.Read(document)
            ?? throw new InvalidOperationException("The document has no portfolio collection.");
        HashSet<string> schemaKeys = collection.Fields.Select(field => field.Key)
            .ToHashSet(StringComparer.Ordinal);
        PdfCollectionItemValue[] selected = values.ToArray();
        if (selected.Any(value => value is null || string.IsNullOrWhiteSpace(value.Key)
            || !schemaKeys.Contains(value.Key)
            || (value.Text is null) == (value.Number is null)
            || (value.Number.HasValue && !double.IsFinite(value.Number.Value))
            || (value.Prefix is not null && string.IsNullOrWhiteSpace(value.Prefix))))
            throw new ArgumentException(
                "Portfolio values must reference schema fields and contain one text or finite numeric value.",
                nameof(values));
        if (selected.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count()
            != selected.Length)
            throw new ArgumentException("Portfolio value keys must be unique.", nameof(values));
        PdfAttachmentInfo attachment = PdfAttachmentReader.Read(document).SingleOrDefault(item =>
            string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"The document has no attachment named '{fileName}'.", nameof(fileName));
        int objectNumber = attachment.FileSpecificationObjectNumber
            ?? throw new NotSupportedException(
                "Portfolio values require an indirect attachment file specification.");
        PdfDictionary specification = document.Resolve(objectNumber) as PdfDictionary
            ?? throw new InvalidOperationException("The attachment file specification is not a dictionary.");
        var entries = specification.ToDictionary(entry => entry.Key, entry => entry.Value);
        if (selected.Length == 0) entries.Remove(Name("CI"));
        else entries[Name("CI")] = new PdfDictionary(selected.OrderBy(value => value.Key,
            StringComparer.Ordinal).Select(value => new KeyValuePair<PdfName, PdfObject>(
                Name(value.Key), ItemValue(value))));
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(entries)).Build();

        static PdfObject ItemValue(PdfCollectionItemValue value)
        {
            PdfObject data = value.Text is not null ? Text(value.Text)
                : new PdfReal(value.Number!.Value);
            return value.Prefix is null ? data : new PdfDictionary([
                new(Name("D"), data), new(Name("P"), Text(value.Prefix))]);
        }
    }

    /// <summary>Replaces the portfolio folder hierarchy in the supplied display order.</summary>
    public static byte[] SetFolders(PdfDocument document,
        IEnumerable<PdfCollectionFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(folders);
        PdfCollectionFolder[] selected = folders.ToArray();
        if (selected.Any(folder => folder is null || folder.Id < 0
            || string.IsNullOrWhiteSpace(folder.Name)
            || (folder.Description is not null && string.IsNullOrWhiteSpace(folder.Description))
            || (folder.CreationDate is not null && string.IsNullOrWhiteSpace(folder.CreationDate))
            || (folder.ModificationDate is not null && string.IsNullOrWhiteSpace(folder.ModificationDate))))
            throw new ArgumentException(
                "Portfolio folders require a nonnegative ID, a name, and nonempty optional metadata.",
                nameof(folders));
        if (selected.Select(folder => folder.Id).Distinct().Count() != selected.Length)
            throw new ArgumentException("Portfolio folder IDs must be unique.", nameof(folders));

        var byId = new Dictionary<long, PdfCollectionFolder>();
        var depths = new Dictionary<long, int>();
        foreach (PdfCollectionFolder folder in selected)
        {
            int depth = 0;
            if (folder.ParentId.HasValue)
            {
                if (!byId.ContainsKey(folder.ParentId.Value))
                    throw new ArgumentException(
                        "A portfolio folder parent must appear before its children.", nameof(folders));
                depth = checked(depths[folder.ParentId.Value] + 1);
                if (depth > 64)
                    throw new ArgumentException(
                        "A portfolio folder hierarchy cannot exceed 64 levels.", nameof(folders));
            }
            byId.Add(folder.Id, folder);
            depths.Add(folder.Id, depth);
        }

        PdfPageTree tree = PdfPageTree.Read(document);
        PdfObject? current = tree.Catalog.GetValueOrDefault(CollectionName);
        PdfIndirectReference? collectionReference = current as PdfIndirectReference;
        PdfDictionary collection = current is null ? new PdfDictionary([])
            : Resolve(document, current) as PdfDictionary
                ?? throw new InvalidOperationException(
                    "The catalog /Collection value is not a dictionary.");
        var entries = collection.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[Name("Type")] = Name("Collection");

        var builder = new PdfIncrementalUpdateBuilder(document);
        if (selected.Length == 0) entries.Remove(Name("Folders"));
        else
        {
            Dictionary<long, PdfIndirectReference> references = selected.ToDictionary(
                folder => folder.Id, _ => builder.ReserveObject());
            Dictionary<long, PdfCollectionFolder[]> siblings = selected
                .GroupBy(folder => folder.ParentId ?? -1)
                .ToDictionary(group => group.Key, group => group.ToArray());
            foreach (PdfCollectionFolder folder in selected)
            {
                var folderEntries = new List<KeyValuePair<PdfName, PdfObject>>
                {
                    new(Name("Type"), Name("Folder")),
                    new(Name("ID"), new PdfInteger(folder.Id)),
                    new(Name("Name"), Text(folder.Name))
                };
                if (folder.Description is not null)
                    folderEntries.Add(new(Name("Desc"), Text(folder.Description)));
                if (folder.CreationDate is not null)
                    folderEntries.Add(new(Name("CreationDate"), Text(folder.CreationDate)));
                if (folder.ModificationDate is not null)
                    folderEntries.Add(new(Name("ModDate"), Text(folder.ModificationDate)));
                if (folder.ParentId.HasValue)
                    folderEntries.Add(new(Name("Parent"), references[folder.ParentId.Value]));
                PdfCollectionFolder[] peers = siblings[folder.ParentId ?? -1];
                int index = Array.IndexOf(peers, folder);
                if (index + 1 < peers.Length)
                    folderEntries.Add(new(Name("Next"), references[peers[index + 1].Id]));
                if (siblings.TryGetValue(folder.Id, out PdfCollectionFolder[]? children))
                    folderEntries.Add(new(Name("Child"), references[children[0].Id]));
                builder.SetObject(references[folder.Id], new PdfDictionary(folderEntries));
            }
            entries[Name("Folders")] = references[siblings[-1][0].Id];
        }

        PdfDictionary replacement = new(entries);
        if (collectionReference is not null)
            builder.ReplaceObject(collectionReference.ObjectNumber, replacement);
        else
        {
            var catalog = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
            catalog[CollectionName] = replacement;
            builder.ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalog));
        }
        return builder.Build();
    }

    private static byte[] UpdateCollection(PdfDocument document,
        Action<Dictionary<PdfName, PdfObject>> updateEntries)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        PdfObject? current = tree.Catalog.GetValueOrDefault(CollectionName);
        PdfIndirectReference? reference = current as PdfIndirectReference;
        PdfDictionary collection = current is null ? new PdfDictionary([])
            : Resolve(document, current) as PdfDictionary
                ?? throw new InvalidOperationException(
                    "The catalog /Collection value is not a dictionary.");
        var entries = collection.ToDictionary(entry => entry.Key, entry => entry.Value);
        updateEntries(entries);
        var replacement = new PdfDictionary(entries);
        var builder = new PdfIncrementalUpdateBuilder(document);
        if (reference is not null)
            builder.ReplaceObject(reference.ObjectNumber, replacement);
        else
        {
            var catalog = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
            catalog[CollectionName] = replacement;
            builder.ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalog));
        }
        return builder.Build();
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

    private static PdfString Text(string value) => new(
        [0xFE, 0xFF, .. PdfUnicodeEncoding.EncodeBigEndian(value)],
        PdfStringForm.Hexadecimal);
    private static PdfName Name(string value) => new(Encoding.UTF8.GetBytes(value));
}
