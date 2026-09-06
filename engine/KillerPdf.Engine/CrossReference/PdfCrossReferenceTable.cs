using System.Collections;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.CrossReference;

/// <summary>
/// The merged cross-reference view of every incremental revision, with the newest definition of
/// an object taking precedence over older definitions.
/// </summary>
public sealed class PdfCrossReferenceTable : IReadOnlyDictionary<int, PdfCrossReferenceEntry>
{
    /// <summary>Maximum number of incremental revisions accepted during bounded traversal.</summary>
    public const int MaximumRevisionCount = 1_024;
    private static readonly PdfName SizeName = new("Size"u8);
    private static readonly PdfName IdName = new("ID"u8);
    private static readonly PdfName EncryptName = new("Encrypt"u8);
    private static readonly PdfName LinearizedName = new("Linearized"u8);
    private static readonly PdfName LinearizedLengthName = new("L"u8);
    private static readonly PdfName LinearizedHintsName = new("H"u8);
    private static readonly PdfName LinearizedFirstPageName = new("O"u8);
    private static readonly PdfName LinearizedEndName = new("E"u8);
    private static readonly PdfName LinearizedPageCountName = new("N"u8);
    private static readonly PdfName LinearizedXrefName = new("T"u8);

    private readonly Dictionary<int, PdfCrossReferenceEntry> _entries;
    private readonly List<Revision> _revisions;

    private PdfCrossReferenceTable(
        PdfHeader header,
        PdfStartXref startXref,
        List<Revision> revisions,
        Dictionary<int, PdfCrossReferenceEntry> entries)
    {
        Header = header;
        StartXref = startXref;
        _revisions = revisions;
        _entries = entries;
    }

    /// <summary>Gets the parsed PDF header and its source offset.</summary>
    public PdfHeader Header { get; }
    /// <summary>Gets the final startxref declaration.</summary>
    public PdfStartXref StartXref { get; }
    /// <summary>Gets primary cross-reference sections from newest revision to oldest.</summary>
    public IReadOnlyList<PdfCrossReferenceSection> Sections =>
        [.. _revisions.Select(revision => revision.Primary)];
    internal IEnumerable<PdfCrossReferenceSection> AllSections =>
        _revisions.SelectMany(revision => revision.Hybrid is null
            ? [revision.Primary] : new[] { revision.Primary, revision.Hybrid });

    internal HashSet<(int ObjectNumber, int Index)> RegisteredHeadersForCurrentObjectStream(
        int streamNumber)
    {
        if (!_entries.TryGetValue(streamNumber, out PdfCrossReferenceEntry current)
            || current.Type != PdfCrossReferenceEntryType.InUse)
            return [];
        var result = new HashSet<(int ObjectNumber, int Index)>();
        bool currentVersionActive = false;
        for (int index = _revisions.Count - 1; index >= 0; index--)
        {
            Revision revision = _revisions[index];
            PdfCrossReferenceEntry? streamEntry = null;
            if (revision.Primary.TryGetValue(streamNumber, out PdfCrossReferenceEntry primary))
                streamEntry = primary;
            if (revision.Hybrid is not null
                && revision.Hybrid.TryGetValue(streamNumber, out PdfCrossReferenceEntry hybrid))
                streamEntry = hybrid;
            if (streamEntry.HasValue)
                currentVersionActive = streamEntry.Value.Type == PdfCrossReferenceEntryType.InUse
                    && streamEntry.Value.Field1 == current.Field1
                    && streamEntry.Value.Field2 == current.Field2;
            if (!currentVersionActive)
                continue;
            AddRegistrations(revision.Primary);
            if (revision.Hybrid is not null)
                AddRegistrations(revision.Hybrid);
        }
        return result;

        void AddRegistrations(PdfCrossReferenceSection section)
        {
            foreach (PdfCrossReferenceEntry candidate in section.Values)
                if (candidate.Type == PdfCrossReferenceEntryType.Compressed
                    && candidate.Field1 == streamNumber)
                    result.Add((candidate.ObjectNumber, candidate.Field2));
        }
    }

    /// <summary>Gets the trailer belonging to the newest primary section.</summary>
    public PdfDictionary LatestTrailer => _revisions[0].Primary.Trailer;
    /// <summary>
    /// Returns the effective trailer dictionary across the revision chain, choosing the newest
    /// occurrence of each key while retaining extension-defined entries from older revisions.
    /// </summary>
    public PdfDictionary MergedTrailer
    {
        get
        {
            var entries = new Dictionary<PdfName, PdfObject>();
            foreach (Revision revision in _revisions)
            {
                foreach (var entry in revision.Primary.Trailer)
                    entries.TryAdd(entry.Key, entry.Value);
                if (revision.Hybrid is not null)
                    foreach (var entry in revision.Hybrid.Trailer)
                        entries.TryAdd(entry.Key, entry.Value);
            }
            return new PdfDictionary(entries);
        }
    }

    /// <inheritdoc/>
    public int Count => _entries.Count;
    /// <inheritdoc/>
    public IEnumerable<int> Keys => _entries.Keys;
    /// <inheritdoc/>
    public IEnumerable<PdfCrossReferenceEntry> Values => _entries.Values;
    /// <inheritdoc/>
    public PdfCrossReferenceEntry this[int key] => _entries[key];

    /// <summary>Reads, validates, and merges the complete cross-reference revision chain.</summary>
    public static PdfCrossReferenceTable Read(
        ReadOnlyMemory<byte> source, bool compatibilityRecovery = false)
    {
        PdfHeader header = PdfHeader.Parse(source.Span);
        PdfStartXref startXref;
        try
        {
            startXref = PdfStartXref.Find(
                source.Span, allowPastEndOffset: compatibilityRecovery);
        }
        catch (PdfSyntaxException) when (compatibilityRecovery)
        {
            startXref = RecoverFinalClassicTable(source.Span);
        }
        LinearizationInfo? linearization = ReadLinearizationInfo(source, header);
        var revisions = new List<Revision>();
        var visitedOffsets = new HashSet<long>();
        long? currentOffset = startXref.Offset;

        while (currentOffset.HasValue)
        {
            if (revisions.Count >= MaximumRevisionCount)
                throw new PdfSyntaxException("The PDF contains too many incremental revisions", (int)currentOffset.Value);
            if (!visitedOffsets.Add(currentOffset.Value))
                throw new PdfSyntaxException("The cross-reference revision chain contains a cycle", (int)currentOffset.Value);

            PdfCrossReferenceSection primary = PdfCrossReferenceReader.ReadSection(
                source, currentOffset.Value, compatibilityRecovery);
            long? previousOffset = primary.PreviousOffset;
            if (compatibilityRecovery
                && (previousOffset == 0 && currentOffset.Value != 0
                    || previousOffset >= source.Length))
                previousOffset = null;
            if (previousOffset > currentOffset.Value
                && !compatibilityRecovery
                && !IsLinearizedForwardPrevious(primary, linearization))
                throw new PdfSyntaxException(
                    "Trailer /Prev must point to an earlier cross-reference section",
                    ClampOffset(previousOffset.Value));
            PdfCrossReferenceSection? hybrid = null;
            if (primary.HybridStreamOffset.HasValue)
            {
                long hybridOffset = primary.HybridStreamOffset.Value;
                if (!visitedOffsets.Add(hybridOffset))
                {
                    if (!compatibilityRecovery)
                        throw new PdfSyntaxException(
                            "The hybrid cross-reference chain reuses an offset",
                            (int)hybridOffset);
                    revisions.Add(new Revision(primary, null));
                    currentOffset = previousOffset;
                    continue;
                }
                if (hybridOffset > currentOffset.Value
                    && !compatibilityRecovery
                    && !IsLinearizedForwardHybrid(primary, linearization))
                    throw new PdfSyntaxException(
                        "Trailer /XRefStm must point to an earlier cross-reference stream",
                        ClampOffset(hybridOffset));
                hybrid = PdfCrossReferenceReader.ReadSection(
                    source, hybridOffset, compatibilityRecovery);
                if (!hybrid.IsStream)
                    throw new PdfSyntaxException("Trailer /XRefStm must point to a cross-reference stream", (int)hybridOffset);
                if (hybrid.PreviousOffset.HasValue)
                    throw new PdfSyntaxException(
                        "A hybrid cross-reference stream cannot contain /Prev",
                        (int)hybridOffset);
            }

            revisions.Add(new Revision(primary, hybrid));
            currentOffset = previousOffset;
        }

        if (!compatibilityRecovery)
        {
            ValidateRevisionSizes(revisions, startXref.Offset);
            ValidateRevisionGenerations(revisions, startXref.Offset, linearization);
            ValidatePermanentIdentifiers(revisions, startXref.Offset);
            ValidateEncryptionIntroduction(revisions, startXref.Offset);
        }

        var entries = new Dictionary<int, PdfCrossReferenceEntry>();
        foreach (Revision revision in revisions)
        {
            // In a hybrid revision, stream entries supply compressed-object information absent
            // from the classic table and take precedence if a producer emitted both.
            if (revision.Hybrid is not null)
                AddNewest(entries, revision.Hybrid.Values);
            AddNewest(entries, revision.Primary.Values);
        }
        if (compatibilityRecovery && !entries.Values.Any(entry =>
                entry.Type is PdfCrossReferenceEntryType.InUse
                    or PdfCrossReferenceEntryType.Compressed))
            RebuildEntries(source, header.Offset, startXref.MarkerOffset, entries);
        if (!entries.TryGetValue(0, out PdfCrossReferenceEntry objectZero)
            || objectZero.Type != PdfCrossReferenceEntryType.Free)
            entries[0] = new PdfCrossReferenceEntry(
                0, PdfCrossReferenceEntryType.Free, 0, 65_535);
        ValidateFreeList(entries, startXref.Offset);

        return new PdfCrossReferenceTable(header, startXref, revisions, entries);
    }

    private static PdfStartXref RecoverFinalClassicTable(ReadOnlySpan<byte> source)
    {
        ReadOnlySpan<byte> marker = "xref"u8;
        int searchEnd = source.Length;
        while (searchEnd >= marker.Length)
        {
            int candidate = source[..searchEnd].LastIndexOf(marker);
            if (candidate < 0) break;
            int after = candidate + marker.Length;
            bool delimited = (candidate == 0 || IsRecoveryBoundary(source[candidate - 1]))
                && (after == source.Length || IsRecoveryBoundary(source[after]));
            int lineStart = candidate;
            while (lineStart > 0
                && source[lineStart - 1] is not ((byte)'\r') and not ((byte)'\n'))
                lineStart--;
            bool commented = source[lineStart..candidate].Contains((byte)'%');
            if (delimited && !commented)
                return new PdfStartXref(candidate, candidate);
            searchEnd = candidate;
        }
        throw new PdfSyntaxException(
            "The PDF does not contain recoverable final cross-reference data", source.Length);
    }

    private static bool IsRecoveryBoundary(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32
            or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
            or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}'
            or (byte)'/' or (byte)'%';

    private static void RebuildEntries(ReadOnlyMemory<byte> source, int start, int end,
        Dictionary<int, PdfCrossReferenceEntry> entries)
    {
        ReadOnlySpan<byte> bytes = source.Span;
        int position = Math.Clamp(start, 0, bytes.Length);
        int limit = Math.Clamp(end, position, bytes.Length);
        while (position < limit)
        {
            int candidate = position;
            while (candidate < limit && bytes[candidate] is (byte)' ' or (byte)'\t')
                candidate++;
            if (candidate < limit && bytes[candidate] is >= (byte)'1' and <= (byte)'9')
            {
                PdfIndirectObject? indirect = null;
                int objectEnd = 0;
                try
                {
                    var parser = new PdfObjectParser(source, candidate,
                        allowDuplicateDictionaryKeys: true);
                    indirect = parser.ParseIndirectObject(out objectEnd);
                }
                catch (Exception error) when (error is PdfSyntaxException
                    or FormatException or NotSupportedException or OverflowException)
                {
                }
                if (indirect is not null && indirect.Offset == candidate
                    && indirect.ObjectNumber > 0 && objectEnd > candidate
                    && objectEnd <= limit)
                {
                    if (!entries.ContainsKey(indirect.ObjectNumber)
                        && entries.Count >= PdfCrossReferenceReader.MaximumEntriesPerSection)
                        throw new PdfSyntaxException(
                            "The rebuilt cross-reference entry limit was exceeded", candidate);
                    entries[indirect.ObjectNumber] = new PdfCrossReferenceEntry(
                        indirect.ObjectNumber, PdfCrossReferenceEntryType.InUse,
                        candidate, indirect.Generation);
                    position = objectEnd;
                    continue;
                }
            }
            int lineEnd = bytes[position..limit].IndexOfAny((byte)'\r', (byte)'\n');
            if (lineEnd < 0) break;
            position += lineEnd + 1;
            if (position < limit && bytes[position - 1] == (byte)'\r'
                && bytes[position] == (byte)'\n')
                position++;
        }
    }

    private static LinearizationInfo? ReadLinearizationInfo(
        ReadOnlyMemory<byte> source, PdfHeader header)
    {
        if (header.Offset != 0)
            return null;
        int lineEnd = source.Span[header.Offset..].IndexOfAny((byte)'\r', (byte)'\n');
        if (lineEnd < 0)
            return null;
        int objectStart = header.Offset + lineEnd + 1;
        try
        {
            PdfIndirectObject first = new PdfObjectParser(source, objectStart)
                .ParseIndirectDictionaryObject(out int dictionaryEnd);
            if (first.Offset >= PdfHeader.SearchLimit
                || dictionaryEnd > PdfHeader.SearchLimit
                || first.Generation != 0
                || first.Value is not PdfDictionary dictionary
                || !dictionary.TryGetValue(LinearizedName, out PdfObject marker)
                || marker is not (PdfInteger { Value: 1 } or PdfReal { Value: 1 })
                || !TryInteger(dictionary, LinearizedLengthName, out long length)
                || !TryInteger(dictionary, LinearizedFirstPageName, out long firstPage)
                || !TryInteger(dictionary, LinearizedEndName, out long end)
                || !TryInteger(dictionary, LinearizedPageCountName, out long pageCount)
                || !TryInteger(dictionary, LinearizedXrefName, out long mainXref)
                || !dictionary.TryGetValue(LinearizedHintsName, out PdfObject hints)
                || hints is not PdfArray hintArray
                || hintArray.Count is not (2 or 4)
                || hintArray.Any(item => item is not PdfInteger { Value: >= 0 })
                || length <= 0 || length > source.Length
                || firstPage <= 0 || pageCount <= 0
                || end <= first.Offset || end >= mainXref || end > length
                || mainXref <= 0 || mainXref >= length
                || !ValidHintRanges(hintArray, dictionaryEnd, end, mainXref))
                return null;
            PdfStartXref originalStart = PdfStartXref.Find(
                source.Span[..checked((int)length)]);
            long primaryHint = ((PdfInteger)hintArray[0]).Value;
            return new LinearizationInfo(end, length, mainXref,
                originalStart.Offset, primaryHint,
                first.ObjectNumber, first.Offset, firstPage);
        }
        catch (Exception error) when (error is PdfSyntaxException
            or FormatException or NotSupportedException or OverflowException)
        {
            return null;
        }

        static bool TryInteger(
            PdfDictionary dictionary, PdfName name, out long value)
        {
            if (dictionary.TryGetValue(name, out PdfObject item)
                && item is PdfInteger integer)
            {
                value = integer.Value;
                return true;
            }
            value = 0;
            return false;
        }

        static bool ValidHintRanges(
            PdfArray hints, long dictionaryEnd, long firstPageEnd, long mainXref)
        {
            for (int index = 0; index < hints.Count; index += 2)
            {
                long offset = ((PdfInteger)hints[index]).Value;
                long count = ((PdfInteger)hints[index + 1]).Value;
                long lowerBound = index == 0 ? dictionaryEnd : firstPageEnd;
                long upperBound = index == 0 ? firstPageEnd : mainXref;
                if (offset < lowerBound || offset > upperBound
                    || count > upperBound - offset)
                    return false;
            }
            return true;
        }
    }

    private static bool IsLinearizedForwardPrevious(
        PdfCrossReferenceSection section, LinearizationInfo? linearization) =>
        linearization is { } info
        && section.Offset == info.OriginalStartXref
        && section.Offset < info.EndOffset
        && section.PreviousOffset is { } previous
        && section.Offset < info.PrimaryHintOffset
        && previous > section.Offset
        && previous < info.OriginalLength
        && Math.Abs(info.MainXrefHint - previous) <= 64
        && section.Trailer[SizeName] is PdfInteger { Value: > 0 } size
        && info.FirstPageObject < size.Value
        && info.ParameterObject < size.Value
        && section.TryGetValue(info.ParameterObject, out PdfCrossReferenceEntry parameter)
        && parameter.Type == PdfCrossReferenceEntryType.InUse
        && parameter.Field1 == info.ParameterOffset
        && parameter.Field2 == 0;

    private static bool IsLinearizedForwardHybrid(
        PdfCrossReferenceSection section, LinearizationInfo? linearization) =>
        IsLinearizedForwardPrevious(section, linearization)
        && linearization is { } info
        && section.HybridStreamOffset is { } hybrid
        && hybrid > section.Offset
        && hybrid < info.EndOffset;

    private static bool IsLinearizedFirstPageSection(
        PdfCrossReferenceSection section, LinearizationInfo? linearization) =>
        IsLinearizedForwardPrevious(section, linearization)
        || IsLinearizedForwardHybrid(section, linearization);

    private static void ValidateRevisionSizes(
        IReadOnlyList<Revision> revisions, long offset)
    {
        long previousSize = 0;
        for (int index = revisions.Count - 1; index >= 0; index--)
        {
            Revision revision = revisions[index];
            long size = ((PdfInteger)revision.Primary.Trailer[SizeName]).Value;
            if (size < previousSize)
                throw new PdfSyntaxException(
                    "Trailer /Size cannot decrease across incremental revisions",
                    ClampOffset(offset));
            if (revision.Hybrid is not null)
            {
                long hybridSize = ((PdfInteger)
                    revision.Hybrid.Trailer[SizeName]).Value;
                if (hybridSize > size)
                    throw new PdfSyntaxException(
                        "A hybrid cross-reference stream /Size cannot exceed its trailer /Size",
                        ClampOffset(revision.Hybrid.Offset));
            }
            previousSize = size;
        }
    }

    private static void ValidateRevisionGenerations(
        IReadOnlyList<Revision> revisions, long offset,
        LinearizationInfo? linearization)
    {
        var states = new Dictionary<int, (bool IsFree, int Generation)>();
        for (int index = revisions.Count - 1; index >= 0; index--)
        {
            Revision revision = revisions[index];
            var entries = revision.Primary.ToDictionary(entry => entry.Key, entry => entry.Value);
            if (revision.Hybrid is not null)
                foreach ((int objectNumber, PdfCrossReferenceEntry entry) in revision.Hybrid)
                    entries[objectNumber] = entry;

            foreach (PdfCrossReferenceEntry entry in entries.Values)
            {
                int? generation = entry.Type switch
                {
                    PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Free => entry.Field2,
                    PdfCrossReferenceEntryType.Compressed => 0,
                    _ => null
                };
                if (!generation.HasValue)
                {
                    states.Remove(entry.ObjectNumber);
                    continue;
                }
                bool isFree = entry.Type == PdfCrossReferenceEntryType.Free;
                // A hybrid companion stream restates the revision's own objects for
                // readers that understand compressed entries; the classic tables
                // deliberately retire those same object numbers at generation 65535
                // so legacy readers skip them (ISO 32000-1 7.5.8.4). That is a
                // compatibility convention, not an incremental-update generation
                // sequence, so hybrid entries do not participate in it.
                if (states.TryGetValue(entry.ObjectNumber, out var previous)
                    && revision.Hybrid?.ContainsKey(entry.ObjectNumber) != true
                    && !IsLinearizedFirstPageSection(revision.Primary, linearization))
                {
                    int requiredGeneration = !previous.IsFree && isFree
                        ? Math.Min(previous.Generation + 1, 65_535)
                        : previous.Generation;
                    if (generation.Value != requiredGeneration)
                    {
                        string reason = generation.Value < previous.Generation
                            ? "generation cannot decrease"
                            : "has an invalid generation transition";
                        throw new PdfSyntaxException(
                            $"Cross-reference object {entry.ObjectNumber} {reason} across incremental revisions",
                            ClampOffset(offset));
                    }
                }
                states[entry.ObjectNumber] = (isFree, generation.Value);
            }
        }
    }

    private static void ValidateStructuralStreamEntries(
        IReadOnlyList<Revision> revisions, long offset)
    {
        foreach (Revision revision in revisions)
        {
            if (revision.Primary.IsStream
                && !HasSelfEntry(revision.Primary, revision.Primary))
                throw new PdfSyntaxException(
                    "A cross-reference stream must contain an in-use entry for itself",
                    ClampOffset(offset));
            if (revision.Hybrid is not null
                && !HasSelfEntry(revision.Primary, revision.Hybrid)
                && !HasSelfEntry(revision.Hybrid, revision.Hybrid))
                throw new PdfSyntaxException(
                    "A hybrid cross-reference stream must have an in-use entry in its revision",
                    ClampOffset(revision.Hybrid.Offset));
        }

        static bool HasSelfEntry(
            PdfCrossReferenceSection entries, PdfCrossReferenceSection stream)
        {
            return stream.StreamObjectNumber.HasValue
                && entries.TryGetValue(stream.StreamObjectNumber.Value,
                    out PdfCrossReferenceEntry entry)
                && entry.Type == PdfCrossReferenceEntryType.InUse
                && entry.Field1 == stream.Offset
                && entry.Field2 == 0;
        }
    }

    private static void ValidatePermanentIdentifiers(
        IReadOnlyList<Revision> revisions, long _)
    {
        for (int index = revisions.Count - 1; index >= 0; index--)
        {
            Revision revision = revisions[index];
            PdfObject? value = revision.Primary.Trailer.TryGetValue(IdName, out PdfObject primary)
                ? primary
                : revision.Hybrid is not null
                    && revision.Hybrid.Trailer.TryGetValue(IdName, out PdfObject hybrid)
                        ? hybrid : null;
            if (value is null)
                continue;
            if (value is not PdfArray { Count: 2 } identifiers
                || identifiers[0] is not PdfString
                || identifiers[1] is not PdfString)
                throw new PdfSyntaxException(
                    "Trailer /ID must be an array of two strings",
                    ClampOffset(revision.Primary.Offset));
        }
    }

    private static void ValidateEncryptionIntroduction(
        IReadOnlyList<Revision> revisions, long offset)
    {
        bool oldestRevision = true;
        bool encryptionWasInitiallyPresent = false;
        for (int index = revisions.Count - 1; index >= 0; index--)
        {
            Revision revision = revisions[index];
            bool present = revision.Primary.Trailer.TryGetValue(
                EncryptName, out _);
            if (!present && revision.Hybrid is not null)
                present = revision.Hybrid.Trailer.TryGetValue(EncryptName, out _);
            if (oldestRevision)
            {
                encryptionWasInitiallyPresent = present;
                oldestRevision = false;
            }
            else if (present && !encryptionWasInitiallyPresent)
                throw new PdfSyntaxException(
                    "Trailer /Encrypt cannot be introduced by an incremental revision",
                    ClampOffset(offset));
        }
    }

    private static void ValidateFreeList(
        Dictionary<int, PdfCrossReferenceEntry> entries, long offset)
    {
        if (!entries.TryGetValue(0, out PdfCrossReferenceEntry zero)
            || zero.Type != PdfCrossReferenceEntryType.Free
            || zero.Field2 != 65_535)
            throw new PdfSyntaxException(
                "The merged cross-reference table must define object 0 as free with generation 65,535",
                ClampOffset(offset));
    }

    /// <summary>Looks up the newest occurrence of a trailer key across the revision chain.</summary>
    public bool TryGetTrailerValue(PdfName name, out PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (Revision revision in _revisions)
        {
            if (revision.Primary.Trailer.TryGetValue(name, out value!))
                return true;
            if (revision.Hybrid is not null && revision.Hybrid.Trailer.TryGetValue(name, out value!))
                return true;
        }

        value = null!;
        return false;
    }

    /// <inheritdoc/>
    public bool ContainsKey(int key) => _entries.ContainsKey(key);
    /// <inheritdoc/>
    public bool TryGetValue(int key, out PdfCrossReferenceEntry value) => _entries.TryGetValue(key, out value);
    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<int, PdfCrossReferenceEntry>> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static void AddNewest(
        Dictionary<int, PdfCrossReferenceEntry> destination,
        IEnumerable<PdfCrossReferenceEntry> source)
    {
        foreach (PdfCrossReferenceEntry entry in source)
            destination.TryAdd(entry.ObjectNumber, entry);
    }

    private sealed record Revision(
        PdfCrossReferenceSection Primary,
        PdfCrossReferenceSection? Hybrid);
    private readonly record struct LinearizationInfo(
        long EndOffset, long OriginalLength, long MainXrefHint,
        long OriginalStartXref, long PrimaryHintOffset,
        int ParameterObject, long ParameterOffset, long FirstPageObject);

    private static int ClampOffset(long offset) => offset switch
    {
        < 0 => 0,
        > int.MaxValue => int.MaxValue,
        _ => (int)offset
    };
}
