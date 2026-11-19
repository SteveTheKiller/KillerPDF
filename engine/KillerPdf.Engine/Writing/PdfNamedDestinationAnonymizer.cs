using System.Text;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Writing;

/// <summary>
/// Renames every named destination to a plain numbered name and points each link, bookmark and
/// go-to action at its new name, so navigation keeps working while the names stop carrying text.
/// </summary>
/// <remarks>
/// Authors name destinations after headings and clauses, so a redacted phrase can survive as a
/// destination name long after the page that showed it was cleaned.
/// </remarks>
public static class PdfNamedDestinationAnonymizer
{
    private const int MaximumTreeDepth = 64;
    private static readonly PdfName RootName = new("Root"u8);
    private static readonly PdfName NamesName = new("Names"u8);
    private static readonly PdfName DestsName = new("Dests"u8);
    private static readonly PdfName KidsName = new("Kids"u8);
    private static readonly PdfName DestName = new("Dest"u8);
    private static readonly PdfName DName = new("D"u8);
    private static readonly PdfName SName = new("S"u8);
    private static readonly PdfName GoToName = new("GoTo"u8);

    /// <summary>Returns the document with its destinations renamed, or the same document when it names none.</summary>
    public static PdfDocument Apply(PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfDictionary catalog = Dictionary(document, document.Trailer[RootName])
            ?? throw new FormatException("The document has no catalog.");
        // One new name per old one, shared by the name tree's strings and the legacy dictionary's names.
        var byString = new Dictionary<string, string>(StringComparer.Ordinal);
        var visitedNodes = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = new List<(string NewName, PdfObject Value)>();
        string Next() => "D" + (byString.Count + byName.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        PdfDictionary? names = catalog.TryGetValue(NamesName, out PdfObject? namesValue) ? Dictionary(document, namesValue) : null;
        if (names is not null && names.TryGetValue(DestsName, out PdfObject? tree))
            CollectTree(Dictionary(document, tree), 0);
        PdfDictionary? legacy = catalog.TryGetValue(DestsName, out PdfObject? legacyValue) ? Dictionary(document, legacyValue) : null;
        var legacyEntries = new List<KeyValuePair<PdfName, PdfObject>>();
        if (legacy is not null)
            foreach ((PdfName key, PdfObject value) in legacy)
            {
                // A legacy name that spells the same text as a name-tree key is the same destination
                // to a reader, so it takes the same new name.
                string text = key.ValueAsLatin1();
                string renamed = byString.TryGetValue(Convert.ToBase64String(Encoding.Latin1.GetBytes(text)), out string? shared)
                    ? shared : Next();
                byName[text] = renamed;
                legacyEntries.Add(new(new PdfName(Encoding.ASCII.GetBytes(renamed)), value));
            }
        if (byString.Count == 0 && byName.Count == 0) return document;

        var update = new PdfIncrementalUpdateBuilder(document);
        int catalogNumber = document.Trailer[RootName] is PdfIndirectReference root ? root.ObjectNumber : -1;
        foreach (PdfCrossReferenceEntry entry in document.CrossReferences.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Type is not (PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed)) continue;
            if (entry.ObjectNumber == catalogNumber) continue;
            PdfObject value;
            try { value = document.Resolve(entry.ObjectNumber); }
            catch (Exception error) when (error is FormatException or InvalidOperationException) { continue; }
            PdfObject rewritten = Rewrite(value, 0);
            if (!ReferenceEquals(rewritten, value)) update.ReplaceObject(entry.ObjectNumber, rewritten);
        }

        // The catalog gets a flat tree in the new names' order and a renamed legacy dictionary.
        var catalogEntries = new List<KeyValuePair<PdfName, PdfObject>>();
        foreach ((PdfName key, PdfObject value) in catalog)
        {
            if (key.Equals(NamesName) && names is not null)
            {
                var nameEntries = names.Where(item => !item.Key.Equals(DestsName)).ToList();
                if (entries.Count > 0)
                {
                    var flat = new List<PdfObject>();
                    foreach ((string renamed, PdfObject target) in entries.OrderBy(item => item.NewName, StringComparer.Ordinal))
                    {
                        flat.Add(new PdfString(Encoding.ASCII.GetBytes(renamed), PdfStringForm.Literal));
                        flat.Add(Rewrite(target, 0));
                    }
                    nameEntries.Add(new(DestsName, update.AddObject(new PdfDictionary([new(NamesName, new PdfArray(flat))]))));
                }
                catalogEntries.Add(new(key, new PdfDictionary(nameEntries)));
            }
            else if (key.Equals(DestsName) && legacy is not null)
                catalogEntries.Add(new(key, new PdfDictionary(legacyEntries.Select(item =>
                    new KeyValuePair<PdfName, PdfObject>(item.Key, Rewrite(item.Value, 0))))));
            else catalogEntries.Add(new(key, Rewrite(value, 0)));
        }
        if (catalogNumber < 0) throw new FormatException("The catalog is not an indirect object.");
        update.ReplaceObject(catalogNumber, new PdfDictionary(catalogEntries));
        // An incremental update keeps the earlier revision, old names included, and the replaced
        // tree nodes stay in use, so the result is rewritten whole with unreachable objects dropped.
        PdfDocument updated = PdfOptimizer.Reopen(document, update.Build());
        PdfDocumentWriteResult written = PdfDocumentWriter.WriteWithReport(updated, new PdfDocumentWriteOptions
        {
            PruneUnreachableObjects = true,
            AllowSignatureInvalidation = true
        });
        return PdfOptimizer.Reopen(updated, written.Data.ToArray());

        void CollectTree(PdfDictionary? node, int depth)
        {
            // A node reached twice, through repeated or cyclic /Kids, is walked once.
            if (node is null || depth > MaximumTreeDepth || !visitedNodes.Add(node)) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (node.TryGetValue(NamesName, out PdfObject? pairs) && Resolve(document, pairs) is PdfArray array)
                for (int index = 0; index + 1 < array.Count; index += 2)
                    if (Resolve(document, array[index]) is PdfString key)
                    {
                        string id = Convert.ToBase64String(key.Bytes.Span);
                        if (byString.ContainsKey(id)) continue;
                        string renamed = Next();
                        byString[id] = renamed;
                        entries.Add((renamed, array[index + 1]));
                    }
            if (node.TryGetValue(KidsName, out PdfObject? kids) && Resolve(document, kids) is PdfArray children)
                foreach (PdfObject child in children) CollectTree(Dictionary(document, child), depth + 1);
        }

        // Only direct values are rebuilt; an indirect one is rewritten as its own object.
        PdfObject Rewrite(PdfObject value, int depth)
        {
            if (depth > 256) return value;
            switch (value)
            {
                case PdfDictionary dictionary:
                {
                    bool goTo = dictionary.TryGetValue(SName, out PdfObject? kind)
                        && ResolveQuietly(kind) is PdfName action && action.Equals(GoToName);
                    var rebuilt = new List<KeyValuePair<PdfName, PdfObject>>(dictionary.Count);
                    bool changed = false;
                    foreach ((PdfName key, PdfObject item) in dictionary)
                    {
                        PdfObject next = key.Equals(DestName) || goTo && key.Equals(DName) ? Target(item) : Rewrite(item, depth + 1);
                        changed |= !ReferenceEquals(next, item);
                        rebuilt.Add(new(key, next));
                    }
                    return changed ? new PdfDictionary(rebuilt) : dictionary;
                }
                case PdfArray array:
                {
                    PdfObject[] items = [.. array.Select(item => Rewrite(item, depth + 1))];
                    return items.Where((item, index) => !ReferenceEquals(item, array[index])).Any() ? new PdfArray(items) : array;
                }
                case PdfStream stream:
                {
                    PdfObject dictionary = Rewrite(stream.Dictionary, depth + 1);
                    return ReferenceEquals(dictionary, stream.Dictionary) ? stream
                        : new PdfStream((PdfDictionary)dictionary, stream.EncodedData.Span);
                }
                default:
                    return value;
            }
        }

        PdfObject ResolveQuietly(PdfObject value)
        {
            try { return Resolve(document, value); }
            catch (Exception error) when (error is FormatException or InvalidOperationException) { return value; }
        }

        PdfObject Target(PdfObject value) => value switch
        {
            PdfString text when byString.TryGetValue(Convert.ToBase64String(text.Bytes.Span), out string? renamed)
                => new PdfString(Encoding.ASCII.GetBytes(renamed), PdfStringForm.Literal),
            PdfName name when byName.TryGetValue(name.ValueAsLatin1(), out string? renamed)
                => new PdfName(Encoding.ASCII.GetBytes(renamed)),
            // A destination given as an indirect string becomes the direct new name, which leaves the
            // object that spelled the old name unreachable so the rewrite drops it.
            PdfIndirectReference reference when ResolveQuietly(reference) is PdfString or PdfName
                => Target(ResolveQuietly(reference)) is var renamed && renamed is PdfString or PdfName
                    && !ReferenceEquals(renamed, ResolveQuietly(reference)) ? renamed : value,
            _ => value
        };
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference && depth < 32; depth++)
            value = document.Resolve(reference);
        return value;
    }

    private static PdfDictionary? Dictionary(PdfDocument document, PdfObject value) =>
        Resolve(document, value) as PdfDictionary;
}
