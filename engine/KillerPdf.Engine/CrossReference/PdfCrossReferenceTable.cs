using System.Collections;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
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
    private static readonly PdfName RootName = new("Root"u8);
    private static readonly PdfName TypeName = new("Type"u8);
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
    private readonly Dictionary<int, HashSet<(int ObjectNumber, int Index)>> _recoveredHeaders = [];

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
        if (_recoveredHeaders.TryGetValue(streamNumber, out var recovered))
            return new HashSet<(int ObjectNumber, int Index)>(recovered);
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
        if (!compatibilityRecovery) return ReadChain(source, compatibilityRecovery: false);
        try
        {
            return ReadChain(source, compatibilityRecovery: true);
        }
        catch (Exception exception) when (exception is PdfSyntaxException or FormatException
            or NotSupportedException or OverflowException or PdfFilterException)
        {
            // The cross-reference data is unusable. Mainstream viewers rebuild it by scanning
            // the file for indirect objects and a trailer; do the same before giving up.
            return ReconstructFromScan(source, exception);
        }
    }

    private static PdfCrossReferenceTable ReconstructFromScan(
        ReadOnlyMemory<byte> source, Exception original)
    {
        PdfHeader header = PdfHeader.ParseWithCompatibilityRecovery(source.Span);
        var entries = new Dictionary<int, PdfCrossReferenceEntry>();
        RebuildEntries(source, header.Offset, source.Length, entries);
        if (entries.Count == 0) throw new PdfSyntaxException(
            "The PDF contains no recoverable indirect objects", header.Offset);
        var recoveredHeaders = RecoverObjectStreamEntries(source, entries);

        PdfDictionary? trailer = null;
        try
        {
            trailer = RecoverFinalSection(source).Trailer;
        }
        catch (PdfSyntaxException)
        {
        }
        if (trailer is null || !trailer.ContainsKey(RootName))
        {
            var found = new Dictionary<PdfName, PdfObject>();
            foreach (var entry in trailer ?? new PdfDictionary(new Dictionary<PdfName, PdfObject>()))
                found[entry.Key] = entry.Value;
            foreach (PdfCrossReferenceEntry entry in entries.Values
                .Where(entry => entry.Type == PdfCrossReferenceEntryType.InUse)
                .OrderBy(entry => entry.Field1))
            {
                try
                {
                    PdfIndirectObject indirect = new PdfObjectParser(source, checked((int)entry.Field1),
                        allowDuplicateDictionaryKeys: true)
                    { ShareOwnedStreamData = true }.ParseIndirectObject();
                    PdfDictionary? dictionary = indirect.Value switch
                    {
                        PdfDictionary direct => direct,
                        PdfStream stream => stream.Dictionary,
                        _ => null
                    };
                    if (dictionary is null) continue;
                    if (dictionary.TryGetValue(TypeName, out PdfObject? type) && type is PdfName typeName)
                    {
                        if (typeName.ValueAsLatin1() == "XRef")
                        {
                            foreach (var pair in dictionary)
                                if (pair.Key.ValueAsLatin1() is "Root" or "Info" or "ID" or "Encrypt")
                                    found[pair.Key] = pair.Value;
                        }
                        else if (typeName.ValueAsLatin1() == "Catalog" && !found.ContainsKey(RootName))
                            found[RootName] = new PdfIndirectReference(indirect.ObjectNumber, indirect.Generation);
                    }
                }
                catch (Exception error) when (error is PdfSyntaxException or FormatException
                    or NotSupportedException or OverflowException)
                {
                }
            }
            if (!found.ContainsKey(RootName))
                throw new PdfSyntaxException(
                    "The PDF cross-reference data could not be rebuilt: " + original.Message, 0);
            found[SizeName] = new PdfInteger(entries.Keys.Max() + 1);
            trailer = new PdfDictionary(found);
        }
        entries[0] = new PdfCrossReferenceEntry(0, PdfCrossReferenceEntryType.Free, 0, 65_535);
        var section = new PdfCrossReferenceSection(header.Offset, entries.Values, trailer,
            isStream: false, compatibilityRecovery: true);
        var startXref = new PdfStartXref(header.Offset, header.Offset);
        var table = new PdfCrossReferenceTable(header, startXref,
            [new Revision(section, null)], entries);
        foreach (var pair in recoveredHeaders)
            table._recoveredHeaders.Add(pair.Key, pair.Value);
        return table;
    }

    private static Dictionary<int, HashSet<(int ObjectNumber, int Index)>> RecoverObjectStreamEntries(
        ReadOnlyMemory<byte> source, Dictionary<int, PdfCrossReferenceEntry> entries)
    {
        var registrations = new Dictionary<int, HashSet<(int ObjectNumber, int Index)>>();
        int remainingHeaders = PdfCrossReferenceReader.MaximumEntriesPerSection;
        // Physical offsets retain the newest definition even when its predecessor was compressed.
        var offsets = entries.ToDictionary(pair => pair.Key, pair => pair.Value.Field1);
        foreach (PdfCrossReferenceEntry entry in entries.Values.OrderBy(item => item.Field1).ToArray())
        {
            try
            {
                if (entry.Field2 != 0) continue;
                int offset = checked((int)entry.Field1);
                PdfIndirectObject indirect = new PdfObjectParser(source, offset,
                    allowDuplicateDictionaryKeys: true)
                { ShareOwnedStreamData = true }.ParseIndirectObject();
                if (indirect.Value is not PdfStream stream
                    || !stream.Dictionary.TryGetValue(TypeName, out PdfObject type)
                    || type is not PdfName name || name.ValueAsLatin1() != "ObjStm"
                    || !stream.Dictionary.TryGetValue(new PdfName("N"u8), out PdfObject countValue)
                    || countValue is not PdfInteger { Value: >= 0 and <= PdfDocument.MaximumObjectsPerObjectStream } count
                    || count.Value > remainingHeaders
                    || !stream.Dictionary.TryGetValue(new PdfName("First"u8), out PdfObject firstValue)
                    || firstValue is not PdfInteger { Value: >= 0 and <= int.MaxValue } first)
                    continue;

                byte[] decoded = PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream);
                if (first.Value > decoded.Length) continue;
                var headers = PdfDocument.ReadObjectHeaders(decoded, (int)count.Value, (int)first.Value, offset);
                var numbers = new HashSet<int>();
                foreach (var member in headers)
                    if (member.ObjectNumber == entry.ObjectNumber || !numbers.Add(member.ObjectNumber))
                        throw new PdfSyntaxException("The recovered object stream repeats an object number", offset);
                if (numbers.Count(number => !entries.ContainsKey(number))
                    > PdfCrossReferenceReader.MaximumEntriesPerSection - entries.Count)
                    throw new PdfSyntaxException("The rebuilt cross-reference entry limit was exceeded", offset);

                var registered = new HashSet<(int ObjectNumber, int Index)>();
                for (int index = 0; index < headers.Count; index++)
                {
                    int number = headers[index].ObjectNumber;
                    registered.Add((number, index));
                    if (offsets.TryGetValue(number, out long newerOffset) && newerOffset > entry.Field1)
                        continue;
                    entries[number] = new PdfCrossReferenceEntry(number,
                        PdfCrossReferenceEntryType.Compressed, entry.ObjectNumber, index);
                    offsets[number] = entry.Field1;
                }
                registrations.Add(entry.ObjectNumber, registered);
                remainingHeaders -= headers.Count;
            }
            catch (Exception error) when (error is PdfSyntaxException or PdfFilterException
                or FormatException or NotSupportedException or OverflowException)
            {
                // Unreadable members stay unresolved; other recoverable streams remain usable.
            }
        }
        return registrations;
    }

    private static PdfCrossReferenceTable ReadChain(
        ReadOnlyMemory<byte> source, bool compatibilityRecovery)
    {
        PdfHeader header = compatibilityRecovery
            ? PdfHeader.ParseWithCompatibilityRecovery(source.Span)
            : PdfHeader.Parse(source.Span);
        PdfStartXref startXref;
        PdfCrossReferenceSection? recoveredFinalSection = null;
        try
        {
            startXref = PdfStartXref.Find(
                source.Span, allowPastEndOffset: compatibilityRecovery);
            if (compatibilityRecovery && startXref.Offset == 0
                && !source.Span.StartsWith("xref"u8))
            {
                recoveredFinalSection = RecoverFinalSection(source);
                startXref = new PdfStartXref(
                    recoveredFinalSection.Offset,
                    ClampOffset(recoveredFinalSection.Offset));
            }
        }
        catch (PdfSyntaxException) when (compatibilityRecovery)
        {
            recoveredFinalSection = RecoverFinalSection(source);
            startXref = new PdfStartXref(
                recoveredFinalSection.Offset,
                ClampOffset(recoveredFinalSection.Offset));
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

            PdfCrossReferenceSection primary = recoveredFinalSection
                ?? PdfCrossReferenceReader.ReadSection(
                    source, currentOffset.Value, compatibilityRecovery);
            recoveredFinalSection = null;
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
        Dictionary<int, HashSet<(int ObjectNumber, int Index)>>? recoveredHeaders = null;
        if (compatibilityRecovery && !entries.Values.Any(entry =>
                entry.Type is PdfCrossReferenceEntryType.InUse
                    or PdfCrossReferenceEntryType.Compressed))
        {
            RebuildEntries(source, header.Offset, startXref.MarkerOffset, entries);
            recoveredHeaders = RecoverObjectStreamEntries(source, entries);
        }
        if (!entries.TryGetValue(0, out PdfCrossReferenceEntry objectZero)
            || objectZero.Type != PdfCrossReferenceEntryType.Free)
            entries[0] = new PdfCrossReferenceEntry(
                0, PdfCrossReferenceEntryType.Free, 0, 65_535);
        ValidateFreeList(entries, startXref.Offset);

        if (compatibilityRecovery && !revisions.Any(revision =>
                revision.Primary.Trailer.ContainsKey(RootName)
                || revision.Hybrid?.Trailer.ContainsKey(RootName) == true))
        {
            try
            {
                return ReconstructFromScan(source,
                    new PdfSyntaxException("The recovered cross-reference chain has no catalog root", 0));
            }
            catch (PdfSyntaxException)
            {
                // A partial cross-reference table remains useful even without a recoverable catalog.
            }
        }

        var table = new PdfCrossReferenceTable(header, startXref, revisions, entries);
        if (recoveredHeaders is not null)
            foreach (var pair in recoveredHeaders)
                table._recoveredHeaders.Add(pair.Key, pair.Value);
        return table;
    }

    private static PdfCrossReferenceSection RecoverFinalSection(
        ReadOnlyMemory<byte> source)
    {
        ReadOnlySpan<byte> bytes = source.Span;
        int searchEnd = bytes.Length;
        while (TryFindFinalKeyword(bytes, "xref"u8, ref searchEnd, out int candidate))
        {
            try
            {
                return PdfCrossReferenceReader.ReadSection(
                    source, candidate, compatibilityRecovery: true);
            }
            catch (PdfSyntaxException)
            {
            }
        }

        searchEnd = bytes.Length;
        if (TryFindFinalKeyword(bytes, "trailer"u8, ref searchEnd, out int trailerOffset))
        {
            var tokenizer = new PdfTokenizer(source, trailerOffset);
            PdfToken token = tokenizer.Read();
            if (token.Kind == PdfTokenKind.Keyword)
            {
                var parser = new PdfObjectParser(source, tokenizer.Position,
                    allowDuplicateDictionaryKeys: true);
                if (parser.ParseObject() is PdfDictionary trailer)
                    return new PdfCrossReferenceSection(trailerOffset, [], trailer,
                        isStream: false, compatibilityRecovery: true);
            }
        }
        throw new PdfSyntaxException(
            "The PDF does not contain recoverable final cross-reference data", bytes.Length);
    }

    private static bool TryFindFinalKeyword(ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> marker, ref int searchEnd, out int offset)
    {
        while (searchEnd >= marker.Length)
        {
            int candidate = source[..searchEnd].LastIndexOf(marker);
            if (candidate < 0) break;
            searchEnd = candidate;
            int after = candidate + marker.Length;
            bool delimited = (candidate == 0 || IsRecoveryBoundary(source[candidate - 1]))
                && (after == source.Length || IsRecoveryBoundary(source[after]));
            int lineStart = candidate;
            while (lineStart > 0
                && source[lineStart - 1] is not ((byte)'\r') and not ((byte)'\n'))
                lineStart--;
            if (delimited && !source[lineStart..candidate].Contains((byte)'%'))
            {
                offset = candidate;
                return true;
            }
        }
        offset = -1;
        return false;
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
                        allowDuplicateDictionaryKeys: true) { ShareOwnedStreamData = true };
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
