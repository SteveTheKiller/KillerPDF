using System.Collections;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.CrossReference;

/// <summary>One classic, stream, or hybrid component of a PDF cross-reference revision.</summary>
public sealed class PdfCrossReferenceSection : IReadOnlyDictionary<int, PdfCrossReferenceEntry>
{
    private static readonly PdfName PrevName = new("Prev"u8);
    private static readonly PdfName XRefStmName = new("XRefStm"u8);

    private readonly Dictionary<int, PdfCrossReferenceEntry> _entries;

    internal PdfCrossReferenceSection(
        long offset,
        IEnumerable<PdfCrossReferenceEntry> entries,
        PdfDictionary trailer,
        bool isStream,
        int? streamObjectNumber = null,
        bool compatibilityRecovery = false)
    {
        Offset = offset;
        Trailer = trailer;
        IsStream = isStream;
        StreamObjectNumber = streamObjectNumber;
        _entries = entries.ToDictionary(entry => entry.ObjectNumber);
        PreviousOffset = OptionalOffset(trailer, PrevName, compatibilityRecovery);
        HybridStreamOffset = OptionalOffset(trailer, XRefStmName, compatibilityRecovery);
    }

    /// <summary>Gets the byte offset at which the section begins.</summary>
    public long Offset { get; }
    /// <summary>Gets the section trailer or cross-reference stream dictionary.</summary>
    public PdfDictionary Trailer { get; }
    /// <summary>Gets whether the section is represented by a cross-reference stream.</summary>
    public bool IsStream { get; }
    /// <summary>Gets the cross-reference stream object number when applicable.</summary>
    public int? StreamObjectNumber { get; }
    /// <summary>Gets the preceding revision offset declared by Prev.</summary>
    public long? PreviousOffset { get; }
    /// <summary>Gets the hybrid companion stream offset declared by XRefStm.</summary>
    public long? HybridStreamOffset { get; }

    /// <inheritdoc/>
    public int Count => _entries.Count;
    /// <inheritdoc/>
    public IEnumerable<int> Keys => _entries.Keys;
    /// <inheritdoc/>
    public IEnumerable<PdfCrossReferenceEntry> Values => _entries.Values;
    /// <inheritdoc/>
    public PdfCrossReferenceEntry this[int key] => _entries[key];

    /// <inheritdoc/>
    public bool ContainsKey(int key) => _entries.ContainsKey(key);
    /// <inheritdoc/>
    public bool TryGetValue(int key, out PdfCrossReferenceEntry value) => _entries.TryGetValue(key, out value);
    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<int, PdfCrossReferenceEntry>> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static long? OptionalOffset(
        PdfDictionary trailer,
        PdfName name,
        bool compatibilityRecovery)
    {
        if (!trailer.TryGetValue(name, out PdfObject value))
            return null;
        if (compatibilityRecovery
            && value is PdfIndirectReference { Generation: 0 } reference)
            return reference.ObjectNumber;
        if (value is not PdfInteger integer || integer.Value < 0)
            throw new ArgumentException($"Trailer {name} must be a non-negative integer.", nameof(trailer));
        return integer.Value;
    }
}
