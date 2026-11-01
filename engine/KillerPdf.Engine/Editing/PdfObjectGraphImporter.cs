using KillerPdf.Engine.Documents;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Copies a source document's reachable object graph into an incremental revision.</summary>
internal sealed class PdfObjectGraphImporter
{
    private static readonly PdfName LengthName = new("Length"u8);
    private const int MaximumImportedObjects = 1_000_000;

    private readonly PdfDocument _source;
    private readonly PdfIncrementalUpdateBuilder _update;
    private readonly HashSet<SourceReference> _sourcePages;
    private readonly Dictionary<SourceReference, PdfIndirectReference> _references = [];
    private readonly Dictionary<SourceReference, SourceReference> _sourcesByDestination = [];
    private Func<PdfIndirectReference?, PdfDictionary, PdfDictionary>? _dictionaryTransform;
    private Dictionary<SourceReference, PdfDictionary>? _sourceObjectOverrides;
    private readonly HashSet<SourceReference> _populated = [];
    // Objects reserved but not yet filled. A long /Next chain is one deep path through the graph, not
    // deep nesting, so references are queued here and filled by a loop instead of by recursion.
    private readonly HashSet<SourceReference> _populating = [];
    private readonly Queue<PendingObject> _pending = new();
    private bool _draining;
    private int _importedObjectCount;

    internal PdfObjectGraphImporter(
        PdfDocument source,
        PdfIncrementalUpdateBuilder update,
        IEnumerable<PdfIndirectReference> sourcePages)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _sourcePages = [.. sourcePages.Select(reference =>
            new SourceReference(reference.ObjectNumber, reference.Generation))];
        if (source.IsEncrypted && !source.IsDecrypted)
            throw new InvalidOperationException(
                "An encrypted source PDF must be opened with a password before its pages can be imported.");
    }

    internal void SeedPage(PdfIndirectReference source, PdfIndirectReference destination)
        => SeedReference(source, destination);

    internal void SeedReference(PdfIndirectReference source, PdfIndirectReference destination)
    {
        var key = new SourceReference(source.ObjectNumber, source.Generation);
        if (!_references.TryAdd(key, destination))
            throw new InvalidOperationException($"Source object {source.ObjectNumber} was mapped more than once.");
        _sourcesByDestination.Add(
            new SourceReference(destination.ObjectNumber, destination.Generation), key);
        _populated.Add(key);
    }

    internal void AddDictionaryTransform(
        Func<PdfIndirectReference?, PdfDictionary, PdfDictionary> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Func<PdfIndirectReference?, PdfDictionary, PdfDictionary>? previous = _dictionaryTransform;
        _dictionaryTransform = previous is null
            ? transform
            : (reference, dictionary) => transform(reference, previous(reference, dictionary));
    }

    internal void AddSourceObjectOverrides(
        IReadOnlyDictionary<(int ObjectNumber, int Generation), PdfDictionary> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        _sourceObjectOverrides ??= [];
        foreach (var entry in overrides)
        {
            if (!_source.CrossReferences.TryGetValue(
                    entry.Key.ObjectNumber, out PdfCrossReferenceEntry crossReference)
                || crossReference.Type is not (PdfCrossReferenceEntryType.InUse
                    or PdfCrossReferenceEntryType.Compressed))
                throw new InvalidOperationException(
                    $"Source object {entry.Key.ObjectNumber} {entry.Key.Generation} cannot be overridden because it is not active.");
            int generation = crossReference.Type == PdfCrossReferenceEntryType.InUse
                ? crossReference.Field2 : 0;
            if (entry.Key.Generation != generation)
                throw new InvalidOperationException(
                    $"Source object {entry.Key.ObjectNumber} {entry.Key.Generation} cannot be overridden because that generation is not active.");
            var key = new SourceReference(entry.Key.ObjectNumber, entry.Key.Generation);
            if (!_sourceObjectOverrides.TryAdd(key, entry.Value))
                throw new InvalidOperationException(
                    $"Source object {entry.Key.ObjectNumber} {entry.Key.Generation} was overridden more than once.");
        }
    }

    internal PdfIndirectReference ReserveReference(PdfIndirectReference sourceReference)
    {
        var key = new SourceReference(sourceReference.ObjectNumber, sourceReference.Generation);
        if (_references.TryGetValue(key, out PdfIndirectReference? mapped)) return mapped;
        if (_sourcePages.Contains(key))
            throw new InvalidOperationException("Page references must be seeded before graph import.");
        PdfIndirectReference destination = _update.ReserveObject();
        _references.Add(key, destination);
        RegisterDestination(destination, key);
        return destination;
    }

    // A freshly reserved number that is already mapped means the document being edited holds objects its
    // cross-reference data does not list, which only happens in a file that was repaired on opening.
    private void RegisterDestination(PdfIndirectReference destination, SourceReference source)
    {
        if (!_sourcesByDestination.TryAdd(
                new SourceReference(destination.ObjectNumber, destination.Generation), source))
            throw new InvalidOperationException(
                "This document had to be repaired when it was opened, and its object numbering is not "
                + "consistent, so it cannot be edited in place. Resave the document to repair it, "
                + "then edit the saved copy.");
    }

    internal PdfObject Import(PdfObject value)
    {
        // A nested call from inside the loop only queues; the outer loop fills what it queued.
        if (_draining) return Import(value, 0, null);
        _draining = true;
        try
        {
            PdfObject imported = Import(value, 0, null);
            while (_pending.Count > 0)
            {
                PendingObject item = _pending.Dequeue();
                PopulateReference(item.Key, item.Source, item.Destination);
            }
            return imported;
        }
        catch
        {
            // Unfilled objects stay mapped and are filled if a later call reaches them again.
            _pending.Clear();
            _populating.Clear();
            throw;
        }
        finally
        {
            _draining = false;
        }
    }

    internal PdfDictionary ApplyDictionaryTransform(PdfDictionary dictionary) =>
        _dictionaryTransform?.Invoke(null, dictionary) ?? dictionary;

    internal PdfObject ResolveImportedSourceValue(PdfObject value)
    {
        if (value is not PdfIndirectReference reference) return value;
        return _sourcesByDestination.TryGetValue(
            new SourceReference(reference.ObjectNumber, reference.Generation),
            out SourceReference source)
                ? ResolveSourceValue(new PdfIndirectReference(
                    source.ObjectNumber, source.Generation))
                : value;
    }

    internal PdfObject ResolveSourceValue(PdfObject value)
    {
        var visited = new HashSet<SourceReference>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32)
                throw new InvalidOperationException(
                    "An imported scalar is too deeply indirect.");
            var identity = new SourceReference(reference.ObjectNumber, reference.Generation);
            if (!visited.Add(identity))
                throw new InvalidOperationException(
                    "An imported scalar contains an indirect-reference cycle.");
            value = ResolveSource(reference);
        }
        return value;
    }

    private PdfObject Import(PdfObject value, int depth, PdfIndirectReference? context)
    {
        if (depth >= PdfObjectWriter.MaximumNestingDepth)
            throw new InvalidOperationException("The imported PDF object nesting limit was exceeded.");
        return value switch
        {
            PdfIndirectReference reference => ImportReference(reference),
            PdfArray array => new PdfArray(array.Select(item => Import(item, depth + 1, null))),
            PdfDictionary dictionary => ImportDictionary(dictionary, depth + 1, context),
            PdfStream stream => new PdfStream(ImportStreamDictionary(stream.Dictionary, depth + 1),
                stream.EncodedData.Span),
            PdfNull or PdfBoolean or PdfInteger or PdfReal or PdfName or PdfString => value,
            _ => throw new NotSupportedException(
                $"PDF object type {value.GetType().FullName} cannot be imported.")
        };
    }

    private PdfObject ImportReference(PdfIndirectReference sourceReference)
    {
        var key = new SourceReference(sourceReference.ObjectNumber, sourceReference.Generation);
        if (_references.TryGetValue(key, out PdfIndirectReference? mapped))
        {
            if (!_populated.Contains(key) && !_populating.Contains(key))
                QueueReference(key, sourceReference, mapped);
            return mapped;
        }
        if (_sourcePages.Contains(key))
            throw new NotSupportedException(
                $"The imported page references source page {sourceReference.ObjectNumber}, which was not selected for import.");
        if (ResolveSource(sourceReference) is PdfNull) return PdfNull.Instance;
        if (_importedObjectCount >= MaximumImportedObjects)
            throw new NotSupportedException("The imported page graph contains too many indirect objects.");
        _importedObjectCount++;
        PdfIndirectReference destinationReference = _update.ReserveObject();
        _references.Add(key, destinationReference);
        RegisterDestination(destinationReference, key);
        QueueReference(key, sourceReference, destinationReference);
        return destinationReference;
    }

    private void QueueReference(
        SourceReference key, PdfIndirectReference sourceReference,
        PdfIndirectReference destinationReference)
    {
        _populating.Add(key);
        _pending.Enqueue(new PendingObject(key, sourceReference, destinationReference));
    }

    private void PopulateReference(
        SourceReference key, PdfIndirectReference sourceReference,
        PdfIndirectReference destinationReference)
    {
        try
        {
            PdfObject sourceValue = ResolveSource(sourceReference);
            if (sourceValue is PdfNull)
                throw new InvalidOperationException("A reserved imported reference resolves to null.");
            _update.SetObject(destinationReference, Import(sourceValue, 0, sourceReference));
            _populated.Add(key);
        }
        finally
        {
            _populating.Remove(key);
        }
    }

    private PdfDictionary ImportDictionary(
        PdfDictionary dictionary, int depth, PdfIndirectReference? context)
    {
        var imported = new PdfDictionary(dictionary.Select(entry =>
            new KeyValuePair<PdfName, PdfObject>(entry.Key, Import(entry.Value, depth, null))));
        return _dictionaryTransform?.Invoke(context, imported) ?? imported;
    }

    private PdfDictionary ImportStreamDictionary(PdfDictionary dictionary, int depth) =>
        new(dictionary.Where(entry => !entry.Key.Equals(LengthName)).Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Key, Import(entry.Value, depth, null))));

    private PdfObject ResolveSource(PdfIndirectReference reference) =>
        _sourceObjectOverrides is not null
        && _sourceObjectOverrides.TryGetValue(
            new SourceReference(reference.ObjectNumber, reference.Generation),
            out PdfDictionary? replacement)
            ? replacement
            : _source.Resolve(reference);

    private readonly record struct SourceReference(int ObjectNumber, int Generation);

    private readonly record struct PendingObject(
        SourceReference Key, PdfIndirectReference Source, PdfIndirectReference Destination);
}
