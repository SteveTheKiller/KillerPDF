using System.Text;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

internal static class PdfNameTree
{
    private static readonly PdfName NamesName = Name("Names");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName LimitsName = Name("Limits");
    private const int MaximumDepth = 256;
    internal const int MaximumEntryCount = 1_000_000;

    // Named destinations are a lookup, so damage costs the names it touches and never the outline or the
    // links that ask for them. The walk keeps every pair it can read: lookup needs neither the key order
    // nor /Limits, a node that cannot be read loses only itself, a key that cannot be decoded names
    // nothing anything could ask for, and when two keys decode to the same text the first one wins.
    // The depth and entry limits and the cycle check still bound the walk.
    internal static void ReadDestinations(PdfDocument document, PdfObject root,
        Dictionary<string, PdfObject> destinations)
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        int entryCount = 0;
        Visit(root, 0);

        void Visit(PdfObject value, int depth)
        {
            if (depth >= MaximumDepth) return;
            PdfDictionary? node;
            PdfArray? names = null, kids = null;
            try
            {
                node = Resolve(value, trackNodes: true) as PdfDictionary;
                if (node is null) return;
                if (node.TryGetValue(NamesName, out PdfObject? namesValue))
                    names = Resolve(namesValue, trackNodes: false) as PdfArray;
                if (node.TryGetValue(KidsName, out PdfObject? kidsValue))
                    kids = Resolve(kidsValue, trackNodes: false) as PdfArray;
            }
            catch (Exception error) when (PdfReaderDamage.Is(error))
            {
                return;
            }
            if (names is not null)
                for (int index = 0; index + 1 < names.Count; index += 2)
                {
                    if (++entryCount > MaximumEntryCount) return;
                    try
                    {
                        if (Resolve(names[index], trackNodes: false) is PdfString key)
                            destinations.TryAdd(PdfUnicodeEncoding.DecodeTextString(
                                key.Bytes.Span, "A named destination key"), names[index + 1]);
                    }
                    catch (Exception error) when (PdfReaderDamage.Is(error))
                    {
                    }
                }
            if (kids is not null)
                foreach (PdfObject kid in kids) Visit(kid, depth + 1);
        }

        // A node reached a second time, which is also how a cycle shows itself, reads as nothing.
        PdfObject Resolve(PdfObject value, bool trackNodes)
        {
            for (int aliasDepth = 0; value is PdfIndirectReference reference; aliasDepth++)
            {
                if (aliasDepth >= 32)
                    throw new InvalidOperationException("A name-tree value is too deeply indirect.");
                if (trackNodes && !visited.Add((reference.ObjectNumber, reference.Generation)))
                    return PdfNull.Instance;
                value = document.Resolve(reference);
            }
            return value;
        }
    }

    // allowDuplicateKeys keeps every entry of a tree that repeats a key, in order, for a caller that
    // gives each one its own key, as merge does for iLovePDF's three "Content Credentials" files (#455).
    internal static IReadOnlyList<PdfNameTreeEntry> Read(
        PdfDocument document, PdfObject root, bool allowDuplicateKeys = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(root);
        var result = new List<PdfNameTreeEntry>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<(int ObjectNumber, int Generation)>();
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        byte[]? previousKey = null;
        Visit(root, 0);
        return result;

        void Visit(PdfObject value, int depth)
        {
            if (depth >= MaximumDepth)
                throw new InvalidOperationException("The name tree exceeds the supported nesting depth.");
            var referenceKeys = new List<(int ObjectNumber, int Generation)>();
            for (int aliasDepth = 0; value is PdfIndirectReference reference; aliasDepth++)
            {
                if (aliasDepth >= 32)
                    throw new InvalidOperationException(
                        "A name-tree node is too deeply indirect.");
                var referenceKey = (reference.ObjectNumber, reference.Generation);
                if (!active.Add(referenceKey))
                    throw new InvalidOperationException("The name tree contains a cycle.");
                referenceKeys.Add(referenceKey);
                if (!visited.Add(referenceKey))
                {
                    foreach (var key in referenceKeys) active.Remove(key);
                    throw new InvalidOperationException(
                        "The name tree references the same node more than once.");
                }
                value = document.Resolve(reference);
            }
            try
            {
                PdfDictionary node = value as PdfDictionary
                    ?? throw new InvalidOperationException("A name-tree node is not a dictionary.");
                int firstEntryIndex = result.Count;
                bool hasNames = node.TryGetValue(NamesName, out PdfObject? namesValue);
                bool hasKids = node.TryGetValue(KidsName, out PdfObject? kidsValue);
                // A root with nothing in it holds no entries; only a node inside the tree must say which it is.
                if (!hasNames && !hasKids && depth == 0 && node.Count == 0) return;
                if (hasNames == hasKids)
                    throw new InvalidOperationException(
                        "A name-tree node must contain exactly one of /Names or /Kids.");
                if (hasNames)
                {
                    PdfArray names = Resolve(namesValue!) as PdfArray
                        ?? throw new InvalidOperationException("A name-tree /Names value is not an array.");
                    if (names.Count % 2 != 0)
                        throw new InvalidOperationException("A name-tree /Names array has an unmatched key.");
                    for (int index = 0; index < names.Count; index += 2)
                    {
                        PdfString key = Resolve(names[index]) as PdfString
                            ?? throw new InvalidOperationException("A name-tree key is not a string.");
                        if (!keys.Add(Convert.ToBase64String(key.Bytes.Span)) && !allowDuplicateKeys)
                            throw new InvalidOperationException("The name tree contains a duplicate key.");
                        // A key equal to the one before it was settled by the duplicate check above.
                        if (previousKey is not null
                            && previousKey.AsSpan().SequenceCompareTo(key.Bytes.Span) > 0)
                            throw new InvalidOperationException(
                                "The name tree contains keys that are not strictly ordered.");
                        previousKey = key.Bytes.ToArray();
                        if (result.Count >= MaximumEntryCount)
                            throw new NotSupportedException("The name tree contains too many entries.");
                        result.Add(new PdfNameTreeEntry(key, names[index + 1]));
                    }
                }
                else
                {
                    PdfArray kids = Resolve(kidsValue!) as PdfArray
                        ?? throw new InvalidOperationException("A name-tree /Kids value is not an array.");
                    if (kids.Count == 0)
                        throw new InvalidOperationException("A name-tree /Kids array is empty.");
                    foreach (PdfObject kid in kids) Visit(kid, depth + 1);
                }
                bool hasLimits = node.TryGetValue(LimitsName, out PdfObject? limitsValue);
                if (depth > 0 && !hasLimits)
                    throw new InvalidOperationException(
                        "A non-root name-tree node has no /Limits value.");
                if (hasLimits)
                {
                    PdfArray limits = Resolve(limitsValue!) as PdfArray
                        ?? throw new InvalidOperationException("A name-tree /Limits value is not an array.");
                    if (limits.Count != 2 || Resolve(limits[0]) is not PdfString lower
                        || Resolve(limits[1]) is not PdfString upper)
                        throw new InvalidOperationException(
                            "A name-tree /Limits value is not a two-string array.");
                    if (result.Count == firstEntryIndex
                        || !lower.Bytes.Span.SequenceEqual(result[firstEntryIndex].Key.Bytes.Span)
                        || !upper.Bytes.Span.SequenceEqual(result[^1].Key.Bytes.Span))
                        throw new InvalidOperationException(
                            "A name-tree /Limits value does not match its descendant key range.");
                }
            }
            finally
            {
                foreach (var referenceKey in referenceKeys) active.Remove(referenceKey);
            }
        }

        PdfObject Resolve(PdfObject value)
        {
            var visitedReferences = new HashSet<(int ObjectNumber, int Generation)>();
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 32)
                    throw new InvalidOperationException(
                        "A name-tree structural value is too deeply indirect.");
                if (!visitedReferences.Add((reference.ObjectNumber, reference.Generation)))
                    throw new InvalidOperationException(
                        "A name-tree structural value contains an indirect-reference cycle.");
                value = document.Resolve(reference);
            }
            return value;
        }
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

internal sealed record PdfNameTreeEntry(PdfString Key, PdfObject Value);

// The failures a read of damaged file content raises. The tolerant readers catch exactly these, so a
// value that cannot be read costs that value, and a defect in the reader itself is not swallowed.
internal static class PdfReaderDamage
{
    internal static bool Is(Exception error) => error is InvalidOperationException or ArgumentException
        or FormatException or NotSupportedException or PdfFilterException;
}
