using System.Globalization;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;

namespace KillerPdf.Engine.Writing;

/// <summary>
/// Appends a deterministic incremental revision while preserving every byte of the source PDF.
/// Existing objects may be superseded and new indirect objects may be reserved before their
/// values are known, allowing callers to construct mutually referring object graphs.
/// </summary>
public sealed class PdfIncrementalUpdateBuilder
{
    private const int MaximumObjectsPerObjectStream = 100;
    private static readonly PdfName SizeName = new("Size"u8);
    private static readonly PdfName PrevName = new("Prev"u8);
    private static readonly PdfName XRefStmName = new("XRefStm"u8);
    private static readonly PdfName RootName = new("Root"u8);
    private static readonly PdfName InfoName = new("Info"u8);
    private static readonly PdfName EncryptName = new("Encrypt"u8);
    private static readonly PdfName IdName = new("ID"u8);
    private static readonly PdfName VersionName = new("Version"u8);
    private static readonly PdfName TypeName = new("Type"u8);
    private static readonly PdfName XRefName = new("XRef"u8);
    private static readonly PdfName WName = new("W"u8);
    private static readonly PdfName IndexName = new("Index"u8);
    private static readonly PdfName LengthName = new("Length"u8);
    private static readonly PdfName FilterName = new("Filter"u8);
    private static readonly PdfName FlateDecodeName = new("FlateDecode"u8);
    private static readonly PdfName DocChecksumName = new("DocChecksum"u8);
    private static readonly PdfName ObjStmName = new("ObjStm"u8);
    private static readonly PdfName NName = new("N"u8);
    private static readonly PdfName FirstName = new("First"u8);
    private static readonly HashSet<PdfName> XrefStreamOnlyNames =
    [
        new("Type"u8), new("W"u8), new("Index"u8), new("Length"u8),
        new("Filter"u8), new("DecodeParms"u8), new("DL"u8)
    ];

    private readonly PdfDocument _document;
    private readonly SortedDictionary<int, PendingObject> _objects = [];
    private readonly SortedDictionary<int, FreedObject> _freed = [];
    private readonly HashSet<int> _reserved = [];
    private readonly HashSet<int> _directObjectNumbers = [];
    private int _nextObjectNumber;
    private bool _documentInformationSpecified;
    private PdfObject? _documentInformation;
    private bool _documentInformationRemovedByFree;
    private IReadOnlyCollection<string> _documentInformationKeptKeys = [];

    /// <summary>
    /// Creates an incremental update builder for an opened document. A document opened with its user
    /// password must permit document modification, as its author set it, unless the owner password opened it.
    /// </summary>
    public PdfIncrementalUpdateBuilder(PdfDocument document) : this(document, permissionsCheckedByCaller: false)
    {
    }

    /// <summary>
    /// For the editors that check their own, finer permission before writing: page assembly, annotations
    /// and signatures. Every other caller uses the public constructor, which requires modification.
    /// </summary>
    internal PdfIncrementalUpdateBuilder(PdfDocument document, bool permissionsCheckedByCaller)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        if (document.IsEncrypted && !document.IsDecrypted)
            throw new InvalidOperationException(
                "An encrypted PDF must be opened with a password before it can be updated.");
        if (!permissionsCheckedByCaller
            && document.PasswordAuthenticationRole == PdfPasswordAuthenticationRole.User
            && (document.DeclaredPermissions is not PdfDocumentPermissions permissions
                || !permissions.AllowDocumentModification))
            throw new InvalidOperationException(
                "The PDF user password does not permit document modification.");
        document.PreparePageStructureForWriting();
        _nextObjectNumber = InitialSize(document);
        // Repairs belong to the document model and must survive an incremental save,
        // including virtual catalog/page-tree objects that do not exist in the source bytes.
        foreach (var pair in document.RecoveredPageObjects)
        {
            PdfCrossReferenceEntry entry = document.CrossReferences[pair.Key];
            int generation = entry.Type == PdfCrossReferenceEntryType.InUse ? entry.Field2 : 0;
            _objects[pair.Key] = new PendingObject(pair.Key, generation, pair.Value);
        }
    }

    /// <summary>The registered changes at one moment, for an edit that may have to be undone.</summary>
    internal sealed class Checkpoint(PdfIncrementalUpdateBuilder builder)
    {
        private readonly KeyValuePair<int, PendingObject>[] _objects = [.. builder._objects];
        private readonly KeyValuePair<int, FreedObject>[] _freed = [.. builder._freed];
        private readonly int[] _reserved = [.. builder._reserved];
        private readonly int[] _directObjectNumbers = [.. builder._directObjectNumbers];
        private readonly int _nextObjectNumber = builder._nextObjectNumber;

        internal void Restore()
        {
            builder._objects.Clear();
            foreach ((int number, PendingObject value) in _objects) builder._objects[number] = value;
            builder._freed.Clear();
            foreach ((int number, FreedObject value) in _freed) builder._freed[number] = value;
            builder._reserved.Clear();
            builder._reserved.UnionWith(_reserved);
            builder._directObjectNumbers.Clear();
            builder._directObjectNumbers.UnionWith(_directObjectNumbers);
            builder._nextObjectNumber = _nextObjectNumber;
        }
    }

    internal Checkpoint Mark() => new(this);

    /// <summary>Reserves a generation-zero object number for a value supplied later.</summary>
    public PdfIndirectReference ReserveObject()
    {
        if (_nextObjectNumber == int.MaxValue)
            throw new NotSupportedException("The PDF object-number range is exhausted.");
        int objectNumber = _nextObjectNumber++;
        _reserved.Add(objectNumber);
        return new PdfIndirectReference(objectNumber, 0);
    }

    /// <summary>Adds a new indirect object and returns its reference.</summary>
    public PdfIndirectReference AddObject(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        PdfIndirectReference reference = ReserveObject();
        SetObject(reference, value);
        return reference;
    }

    /// <summary>Assigns the value of an object previously returned by <see cref="ReserveObject"/>.</summary>
    public PdfIncrementalUpdateBuilder SetObject(PdfIndirectReference reference, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(value);
        if (reference.Generation != 0 || !_reserved.Contains(reference.ObjectNumber))
            throw new ArgumentException("The reference was not reserved by this update builder.", nameof(reference));
        if (!_objects.TryAdd(reference.ObjectNumber, new PendingObject(reference.ObjectNumber, 0, value)))
            throw new InvalidOperationException($"Object {reference.ObjectNumber} already has a value.");
        return this;
    }

    /// <summary>Supersedes the current value of an existing in-use or compressed object.</summary>
    public PdfIncrementalUpdateBuilder ReplaceObject(int objectNumber, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectNumber);
        if (!_document.CrossReferences.TryGetValue(objectNumber, out PdfCrossReferenceEntry entry)
            || entry.Type is not (PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed))
            throw NotInUse(objectNumber);
        int generation = entry.Type == PdfCrossReferenceEntryType.InUse ? entry.Field2 : 0;
        if (_documentInformationRemovedByFree
            && IsInheritedActiveTrailerChainReference(InfoName, objectNumber))
        {
            _documentInformationSpecified = false;
            _documentInformationRemovedByFree = false;
        }
        _freed.Remove(objectNumber);
        _objects[objectNumber] = new PendingObject(objectNumber, generation, value);
        return this;
    }

    /// <summary>Marks an existing indirect object free in the appended revision.</summary>
    public PdfIncrementalUpdateBuilder FreeObject(int objectNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectNumber);
        if (!_document.CrossReferences.TryGetValue(objectNumber, out PdfCrossReferenceEntry entry)
            || entry.Type is not (PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed))
            throw NotInUse(objectNumber);
        if (IsInheritedActiveTrailerChainReference(InfoName, objectNumber)
            && !_documentInformationSpecified)
        {
            _documentInformationSpecified = true;
            _documentInformation = null;
            _documentInformationRemovedByFree = true;
        }
        int generation = entry.Type == PdfCrossReferenceEntryType.InUse ? entry.Field2 : 0;
        generation = Math.Min(65_535, checked(generation + 1));
        _objects.Remove(objectNumber);
        _freed[objectNumber] = new FreedObject(objectNumber, generation);
        return this;
    }

    /// <summary>Replaces the inherited trailer /Info value, or removes it when null.</summary>
    public PdfIncrementalUpdateBuilder SetDocumentInformation(PdfObject? value)
    {
        _documentInformationSpecified = true;
        _documentInformation = value;
        _documentInformationRemovedByFree = false;
        _documentInformationKeptKeys = [];
        return this;
    }

    // keptKeys are entries copied as they were found. They could not be read, so they skip the checks
    // on strings and dates that a value being written must pass.
    internal PdfIncrementalUpdateBuilder SetDocumentInformation(
        PdfObject value, IReadOnlyCollection<string> keptKeys)
    {
        SetDocumentInformation(value);
        _documentInformationKeptKeys = keptKeys;
        return this;
    }

    internal PdfIncrementalUpdateBuilder KeepObjectDirect(PdfIndirectReference reference)
    {
        _directObjectNumbers.Add(reference.ObjectNumber);
        return this;
    }

    /// <summary>Serializes the registered changes as one incremental revision.</summary>
    public byte[] Build(PdfIncrementalUpdateWriteOptions? options = null)
    {
        options ??= new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = RequiresCrossReferenceStream
                ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table
        };
        if (!Enum.IsDefined(options.CrossReferenceFormat))
            throw new ArgumentOutOfRangeException(nameof(options),
                "The cross-reference format is not defined.");
        if (options.CompressCrossReferenceStream
            && options.CrossReferenceFormat != PdfCrossReferenceFormat.Stream)
            throw new InvalidOperationException(
                "Cross-reference-stream compression requires the stream format.");
        if ((options.UseObjectStreams || options.CompressObjectStreams)
            && options.CrossReferenceFormat != PdfCrossReferenceFormat.Stream)
            throw new InvalidOperationException(
                "Object streams require the cross-reference-stream format.");
        if (options.CompressObjectStreams && !options.UseObjectStreams)
            throw new InvalidOperationException(
                "Object-stream compression requires object streams to be enabled.");
        PdfVersion effectiveVersion = EffectiveVersion();
        if (options.CrossReferenceFormat == PdfCrossReferenceFormat.Stream
            && effectiveVersion.CompareTo(new PdfVersion(1, 5)) < 0)
            throw new InvalidOperationException(
                "Cross-reference streams require PDF 1.5 or later.");
        int[] unassigned = [.. _reserved.Where(number => !_objects.ContainsKey(number)).Order()];
        if (unassigned.Length > 0)
            throw new InvalidOperationException(
                $"Reserved object {unassigned[0]} has not been assigned a value.");
        if (_objects.Count == 0 && _freed.Count == 0)
            throw new InvalidOperationException("An incremental update must contain at least one object.");
        ValidateStandardTrailerState();
        HashSet<int> encryptionBootstrapObjectNumbers =
            CurrentEncryptionBootstrapObjectNumbers();
        if (_freed.Keys.Any(number => IsInheritedTrailerReference(RootName, number)
                || encryptionBootstrapObjectNumbers.Contains(number)))
            throw new InvalidOperationException(
                "The document catalog or encryption dictionary cannot be freed.");
        ValidateApplicationTrailerGraphs();
        if (RequiresCrossReferenceStream && options.CrossReferenceFormat == PdfCrossReferenceFormat.Table)
            throw new NotSupportedException(
                "A recovered PDF with compressed or unknown cross-reference entries requires cross-reference stream output.");

        using var output = new MemoryStream();
        output.Write(_document.Source.Span);
        ReadOnlySpan<byte> source = _document.Source.Span;
        if (source.Length > 0 && source[^1] is not ((byte)'\r') and not ((byte)'\n'))
            output.WriteByte((byte)'\n');

        List<PendingObject> packed = options.UseObjectStreams
            ? [.. _objects.Values.Where(item => item.Generation == 0 && item.Value is not PdfStream
                && !encryptionBootstrapObjectNumbers.Contains(item.ObjectNumber)
                && !_directObjectNumbers.Contains(item.ObjectNumber))]
            : [];
        var packedNumbers = packed.Select(item => item.ObjectNumber).ToHashSet();
        List<PendingObject[]> chunks =
        [
            .. packed.Chunk(MaximumObjectsPerObjectStream).Select(chunk => chunk.ToArray())
        ];
        long crossReferenceEntryCount = options.CrossReferenceFormat
            == PdfCrossReferenceFormat.Stream
                ? (long)_objects.Count + chunks.Count + _freed.Count + 2
                : (long)_objects.Count + _freed.Count + 1;
        if (NeedsCompleteCrossReference)
            crossReferenceEntryCount += InheritedEntries().Count(entry => entry.ObjectNumber != 0);
        if (crossReferenceEntryCount > PdfCrossReferenceReader.MaximumEntriesPerSection)
            throw new NotSupportedException(
                "The incremental cross-reference section would contain too many entries.");
        if (_nextObjectNumber > int.MaxValue - chunks.Count - 1)
            throw new NotSupportedException(
                "The PDF object-number range has no room for incremental structural streams.");
        int firstObjectStreamNumber = _nextObjectNumber;
        int xrefObjectNumber = checked(firstObjectStreamNumber + chunks.Count);

        var written = new List<WrittenObject>(_objects.Count + chunks.Count);
        foreach (PendingObject pending in _objects.Values.Where(
                     item => !packedNumbers.Contains(item.ObjectNumber)))
        {
            if (output.Position > 9_999_999_999L)
                throw new NotSupportedException("Classic cross-reference offsets cannot exceed ten digits.");
            int offset = checked((int)output.Position);
            written.Add(new WrittenObject(pending.ObjectNumber, pending.Generation, offset));
            PdfObject value = encryptionBootstrapObjectNumbers.Contains(pending.ObjectNumber)
                ? pending.Value
                : _document.EncryptObject(
                    pending.ObjectNumber, pending.Value,
                    reference => ResolveCurrentValue(
                        reference, "An encrypted stream metadata value"));
            PdfObjectWriter.Write(output,
                new PdfIndirectObject(pending.ObjectNumber, pending.Generation, value, offset));
        }
        var compressed = new List<CompressedObject>(packed.Count);
        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            PendingObject[] chunk = chunks[chunkIndex];
            int objectNumber = checked(firstObjectStreamNumber + chunkIndex);
            int offset = checked((int)output.Position);
            written.Add(new WrittenObject(objectNumber, 0, offset));
            for (int index = 0; index < chunk.Length; index++)
                compressed.Add(new CompressedObject(
                    chunk[index].ObjectNumber, objectNumber, index));
            PdfStream stream = BuildObjectStream(chunk, options.CompressObjectStreams);
            PdfObjectWriter.Write(output, new PdfIndirectObject(objectNumber, 0,
                _document.EncryptObject(objectNumber, stream,
                    reference => ResolveCurrentValue(
                        reference, "An encrypted object-stream metadata value")), offset));
        }

        byte[] revisionIdentifier = CreateRevisionIdentifier(output);

        int xrefOffset = checked((int)output.Position);
        if (options.CrossReferenceFormat == PdfCrossReferenceFormat.Stream)
        {
            WriteCrossReferenceStream(output, written, compressed, xrefObjectNumber, xrefOffset,
                revisionIdentifier, options.CompressCrossReferenceStream);
        }
        else
        {
            output.Write("xref\n"u8);
            if (NeedsCompleteCrossReference)
            {
                foreach (PdfCrossReferenceEntry item in CompleteEntries(written, []))
                {
                    WriteAscii(output, $"{item.ObjectNumber} 1\n");
                    WriteAscii(output, $"{item.Field1:0000000000} {item.Field2:00000} "
                        + (item.Type == PdfCrossReferenceEntryType.Free ? "f \n" : "n \n"));
                }
            }
            else
            {
                WriteClassicFreeEntries(output);
                foreach (WrittenObject item in written)
                {
                    WriteAscii(output, $"{item.ObjectNumber} 1\n");
                    WriteAscii(output, $"{item.Offset:0000000000} {item.Generation:00000} n \n");
                }
            }

            output.Write("trailer\n"u8);
            PdfObjectWriter.Write(output, BuildTrailer(
                revisionIdentifier, _nextObjectNumber));
            output.WriteByte((byte)'\n');
        }
        output.Write("startxref\n"u8);
        WriteAscii(output, xrefOffset.ToString(CultureInfo.InvariantCulture));
        output.Write("\n%%EOF\n"u8);
        return output.ToArray();
    }

    private void WriteCrossReferenceStream(
        Stream output, IReadOnlyList<WrittenObject> written,
        IReadOnlyList<CompressedObject> compressed, int objectNumber, int xrefOffset,
        byte[] revisionIdentifier, bool compress)
    {
        int size = checked(objectNumber + 1);
        PdfCrossReferenceEntry[]? complete = NeedsCompleteCrossReference
            ? CompleteEntries(written, compressed).Append(new PdfCrossReferenceEntry(
                objectNumber, PdfCrossReferenceEntryType.InUse, xrefOffset, 0)).OrderBy(entry => entry.ObjectNumber).ToArray()
            : null;
        int[] numbers = complete is not null ? [.. complete.Select(entry => entry.ObjectNumber)]
            : [.. written.Select(item => item.ObjectNumber)
            .Concat(compressed.Select(item => item.ObjectNumber))
            .Concat(_freed.Keys)
            .Append(0)
            .Append(objectNumber).Order()];
        var byNumber = written.ToDictionary(item => item.ObjectNumber);
        var compressedByNumber = compressed.ToDictionary(item => item.ObjectNumber);
        var rows = new byte[checked(numbers.Length * 9)];
        Dictionary<int, int> nextFree = BuildNextFreeChain();
        for (int index = 0; index < numbers.Length; index++)
        {
            int number = numbers[index];
            if (complete is not null)
            {
                PdfCrossReferenceEntry entry = complete[index];
                byte type = entry.Type switch
                {
                    PdfCrossReferenceEntryType.Free => 0,
                    PdfCrossReferenceEntryType.InUse => 1,
                    PdfCrossReferenceEntryType.Compressed => 2,
                    _ => 3
                };
                WriteXrefRow(rows, index, type, checked((int)entry.Field1), entry.Field2);
            }
            else if (number == 0)
                WriteXrefRow(rows, index, 0, nextFree[0], 65_535);
            else if (_freed.TryGetValue(number, out FreedObject? freed))
                WriteXrefRow(rows, index, 0, nextFree[number], freed.Generation);
            else if (number == objectNumber)
                WriteXrefRow(rows, index, 1, xrefOffset, 0);
            else if (compressedByNumber.TryGetValue(number, out CompressedObject? item))
                WriteXrefRow(rows, index, 2, item.ObjectStreamNumber, item.Index);
            else
            {
                WrittenObject direct = byNumber[number];
                WriteXrefRow(rows, index, 1, direct.Offset, direct.Generation);
            }
        }
        PdfArray indexRanges = BuildIndexRanges(numbers);
        byte[] encoded = compress ? Compress(rows) : rows;
        var entries = BuildTrailer(
            revisionIdentifier, size).ToList();
        entries.Add(new(TypeName, XRefName));
        entries.Add(new(WName, new PdfArray([
            new PdfInteger(1), new PdfInteger(4), new PdfInteger(4)])));
        entries.Add(new(IndexName, indexRanges));
        if (compress) entries.Add(new(FilterName, FlateDecodeName));
        entries.Add(new(LengthName, new PdfInteger(encoded.Length)));
        PdfObjectWriter.Write(output, new PdfIndirectObject(
            objectNumber, 0, new PdfStream(new PdfDictionary(entries), encoded), xrefOffset));
    }

    internal bool RequiresCrossReferenceStream => NeedsCompleteCrossReference
        && InheritedEntries().Any(entry => entry.Type is
            PdfCrossReferenceEntryType.Compressed or PdfCrossReferenceEntryType.Null);

    private bool NeedsCompleteCrossReference
    {
        get
        {
            // Scans use the header as a synthetic startxref; nearby-section recovery can
            // retain a stale declaration. Neither is a valid predecessor for a new revision.
            IReadOnlyList<PdfCrossReferenceSection> sections = _document.CrossReferences.Sections;
            if (_document.CrossReferences.StartXref.Offset == _document.Header.Offset
                || _document.CrossReferences.StartXref.Offset != sections[0].Offset)
                return true;
            for (int index = 0; index < sections.Count; index++)
                if (sections[index].PreviousOffset != (index + 1 < sections.Count
                    ? sections[index + 1].Offset : (long?)null))
                    return true;
            return false;
        }
    }

    private IEnumerable<PdfCrossReferenceEntry> InheritedEntries() =>
        _document.CrossReferences.Values.Where(entry => !_objects.ContainsKey(entry.ObjectNumber)
            && !_freed.ContainsKey(entry.ObjectNumber));

    private PdfCrossReferenceEntry[] CompleteEntries(
        IReadOnlyList<WrittenObject> written, IReadOnlyList<CompressedObject> compressed)
    {
        var entries = InheritedEntries().ToDictionary(entry => entry.ObjectNumber);
        foreach (WrittenObject item in written)
            entries[item.ObjectNumber] = new(item.ObjectNumber,
                PdfCrossReferenceEntryType.InUse, item.Offset, item.Generation);
        foreach (CompressedObject item in compressed)
            entries[item.ObjectNumber] = new(item.ObjectNumber,
                PdfCrossReferenceEntryType.Compressed, item.ObjectStreamNumber, item.Index);
        Dictionary<int, int> nextFree = BuildNextFreeChain();
        foreach (FreedObject item in _freed.Values)
            entries[item.ObjectNumber] = new(item.ObjectNumber,
                PdfCrossReferenceEntryType.Free, nextFree[item.ObjectNumber], item.Generation);
        if (_freed.Count > 0 || !entries.ContainsKey(0))
            entries[0] = new(0, PdfCrossReferenceEntryType.Free, nextFree[0], 65_535);
        return [.. entries.Values.OrderBy(entry => entry.ObjectNumber)];
    }

    private void WriteClassicFreeEntries(Stream output)
    {
        Dictionary<int, int> nextFree = BuildNextFreeChain();
        WriteAscii(output, "0 1\n");
        WriteAscii(output, $"{nextFree[0]:0000000000} 65535 f \n");
        foreach (FreedObject item in _freed.Values)
        {
            WriteAscii(output, $"{item.ObjectNumber} 1\n");
            WriteAscii(output,
                $"{nextFree[item.ObjectNumber]:0000000000} {item.Generation:00000} f \n");
        }
    }

    private Dictionary<int, int> BuildNextFreeChain()
    {
        int previousHead = _document.CrossReferences.TryGetValue(0, out PdfCrossReferenceEntry zero)
            && zero.Type == PdfCrossReferenceEntryType.Free ? checked((int)zero.Field1) : 0;
        int[] numbers = [.. _freed.Keys];
        var result = new Dictionary<int, int> { [0] = numbers.FirstOrDefault() };
        for (int index = 0; index < numbers.Length; index++)
            result[numbers[index]] = index + 1 < numbers.Length
                ? numbers[index + 1] : previousHead;
        return result;
    }

    private byte[] CreateRevisionIdentifier(MemoryStream output)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        Span<byte> action = stackalloc byte[9];
        foreach (FreedObject item in _freed.Values)
        {
            action[0] = (byte)'F';
            WriteInt32BigEndian(action[1..5], item.ObjectNumber);
            WriteInt32BigEndian(action[5..9], item.Generation);
            hash.AppendData(action);
        }
        return hash.GetHashAndReset()[..16];
    }

    private static void WriteInt32BigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static PdfArray BuildIndexRanges(int[] numbers)
    {
        var ranges = new List<PdfObject>();
        for (int start = 0; start < numbers.Length;)
        {
            int end = start + 1;
            while (end < numbers.Length && numbers[end] == numbers[end - 1] + 1) end++;
            ranges.Add(new PdfInteger(numbers[start]));
            ranges.Add(new PdfInteger(end - start));
            start = end;
        }
        return new PdfArray(ranges);
    }

    private static PdfStream BuildObjectStream(
        IReadOnlyList<PendingObject> objects, bool compress)
    {
        using var body = new MemoryStream();
        var offsets = new int[objects.Count];
        for (int index = 0; index < objects.Count; index++)
        {
            offsets[index] = checked((int)body.Position);
            PdfObjectWriter.Write(body, objects[index].Value);
            body.WriteByte((byte)'\n');
        }
        using var header = new MemoryStream();
        for (int index = 0; index < objects.Count; index++)
            WriteAscii(header, $"{objects[index].ObjectNumber} {offsets[index]} ");
        int first = checked((int)header.Length);
        header.Write(body.ToArray());
        byte[] data = header.ToArray();
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(TypeName, ObjStmName),
            new(NName, new PdfInteger(objects.Count)),
            new(FirstName, new PdfInteger(first))
        };
        if (compress)
        {
            data = Compress(data);
            entries.Add(new(FilterName, FlateDecodeName));
        }
        entries.Add(new(LengthName, new PdfInteger(data.Length)));
        return new PdfStream(new PdfDictionary(entries), data);
    }

    private static void WriteXrefRow(
        byte[] rows, int index, byte type, int field1, int field2)
    {
        int position = checked(index * 9);
        rows[position] = type;
        rows[position + 1] = (byte)(field1 >> 24);
        rows[position + 2] = (byte)(field1 >> 16);
        rows[position + 3] = (byte)(field1 >> 8);
        rows[position + 4] = (byte)field1;
        rows[position + 5] = (byte)(field2 >> 24);
        rows[position + 6] = (byte)(field2 >> 16);
        rows[position + 7] = (byte)(field2 >> 8);
        rows[position + 8] = (byte)field2;
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    private PdfDictionary BuildTrailer(byte[] revisionIdentifier, int size)
    {
        var entries = new List<KeyValuePair<PdfName, PdfObject>>();
        foreach ((PdfName name, PdfObject value) in _document.CrossReferences.MergedTrailer)
        {
            if (name.Equals(SizeName) || name.Equals(PrevName) || name.Equals(XRefStmName)
                || name.Equals(IdName) || name.Equals(DocChecksumName)
                || _documentInformationSpecified && name.Equals(InfoName))
                continue;
            if (XrefStreamOnlyNames.Contains(name))
                continue;
            entries.Add(new KeyValuePair<PdfName, PdfObject>(name, value));
        }
        AddInheritedIfMissing(entries, RootName);
        if (_documentInformationSpecified)
        {
            if (_documentInformation is not null)
                entries.Add(new KeyValuePair<PdfName, PdfObject>(
                    InfoName, _documentInformation));
        }
        else
            AddInheritedIfMissing(entries, InfoName);
        AddUpdatedIdentifier(entries, revisionIdentifier);
        entries.Add(new KeyValuePair<PdfName, PdfObject>(SizeName, new PdfInteger(size)));
        if (!NeedsCompleteCrossReference)
            entries.Add(new KeyValuePair<PdfName, PdfObject>(
                PrevName, new PdfInteger(_document.CrossReferences.StartXref.Offset)));
        return new PdfDictionary(entries);
    }

    private void AddUpdatedIdentifier(
        List<KeyValuePair<PdfName, PdfObject>> entries, byte[] revisionIdentifier)
    {
        if (!_document.CrossReferences.TryGetTrailerValue(IdName, out PdfObject value)
            || value is not PdfArray identifiers
            || identifiers.Count == 0
            || identifiers[0] is not PdfString permanentIdentifier)
            return;
        entries.Add(new KeyValuePair<PdfName, PdfObject>(IdName, new PdfArray([
            permanentIdentifier,
            new PdfString(revisionIdentifier, PdfStringForm.Hexadecimal)])));
    }

    private void AddInheritedIfMissing(
        List<KeyValuePair<PdfName, PdfObject>> entries, PdfName name)
    {
        if (entries.Any(entry => entry.Key.Equals(name)))
            return;
        if (_document.CrossReferences.TryGetTrailerValue(name, out PdfObject value))
            entries.Add(new KeyValuePair<PdfName, PdfObject>(name, value));
    }

    private bool IsInheritedTrailerReference(PdfName name, int objectNumber) =>
        _document.CrossReferences.TryGetTrailerValue(name, out PdfObject value)
        && value is PdfIndirectReference reference
        && reference.ObjectNumber == objectNumber;

    private bool IsInheritedActiveTrailerChainReference(PdfName name, int objectNumber)
    {
        if (!_document.CrossReferences.TryGetTrailerValue(name, out PdfObject value))
            return false;
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add(
                    (reference.ObjectNumber, reference.Generation)))
                return false;
            if (reference.ObjectNumber == objectNumber
                && _document.CrossReferences.TryGetValue(
                    objectNumber, out PdfCrossReferenceEntry entry)
                && entry.Type is PdfCrossReferenceEntryType.InUse
                    or PdfCrossReferenceEntryType.Compressed
                && reference.Generation == (entry.Type == PdfCrossReferenceEntryType.InUse
                    ? entry.Field2 : 0))
                return true;
            value = _objects.TryGetValue(reference.ObjectNumber,
                    out PendingObject? pending)
                && pending.Generation == reference.Generation
                    ? pending.Value
                    : _document.Resolve(reference);
        }
        return false;
    }

    private PdfVersion EffectiveVersion()
    {
        PdfVersion version = _document.Header.Version;
        if (!_document.CrossReferences.TryGetTrailerValue(RootName, out PdfObject rootValue)
            || rootValue is not PdfIndirectReference rootReference)
            return version;
        PdfObject root = ResolveCurrentValue(
            rootReference, "The catalog root value");
        if (root is not PdfDictionary catalog
            || !catalog.TryGetValue(VersionName, out PdfObject catalogVersionValue))
            return version;
        PdfObject resolvedCatalogVersion = ResolveCurrentValue(
            catalogVersionValue, "The catalog /Version value");
        if (resolvedCatalogVersion is not PdfName catalogVersion)
            throw new InvalidOperationException("The catalog /Version value is not a name.");
        string text = catalogVersion.ValueAsLatin1();
        if (text.Length != 3 || text[1] != '.'
            || text[0] is < '0' or > '9' || text[2] is < '0' or > '9')
            throw new InvalidOperationException("The catalog /Version value is not a PDF version.");
        int major = text[0] - '0';
        int minor = text[2] - '0';
        if (!PdfVersion.IsDefined(major, minor))
            throw new InvalidOperationException(
                $"The catalog /Version PDF {major}.{minor} is not defined.");
        PdfVersion declared = new(major, minor);
        return declared.CompareTo(version) > 0 ? declared : version;
    }

    private void ValidateApplicationTrailerGraphs()
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        foreach ((PdfName name, PdfObject value) in _document.CrossReferences.MergedTrailer)
        {
            if (name.Equals(SizeName) || name.Equals(PrevName) || name.Equals(XRefStmName)
                || name.Equals(IdName) || name.Equals(DocChecksumName)
                || name.Equals(RootName) || name.Equals(InfoName) || name.Equals(EncryptName)
                || XrefStreamOnlyNames.Contains(name))
                continue;
            Validate(value, $"Trailer /{name.ValueAsLatin1()} value", 0);
        }

        void Validate(PdfObject value, string description, int depth)
        {
            if (depth > 64)
                throw new NotSupportedException(
                    "An application trailer graph is too deeply nested.");
            if (value is PdfIndirectReference reference)
            {
                if (!IsLive(reference))
                    throw new InvalidOperationException(
                        $"{description} contains a stale indirect reference.");
                if (!visited.Add((reference.ObjectNumber, reference.Generation))) return;
                PdfObject resolved = _objects.TryGetValue(reference.ObjectNumber,
                        out PendingObject? pending)
                    && pending.Generation == reference.Generation
                        ? pending.Value : _document.Resolve(reference);
                Validate(resolved, description, depth + 1);
                return;
            }
            if (value is PdfArray array)
            {
                foreach (PdfObject item in array)
                    Validate(item, description, depth + 1);
                return;
            }
            PdfDictionary? dictionary = value switch
            {
                PdfDictionary directDictionary => directDictionary,
                PdfStream stream => stream.Dictionary,
                _ => null
            };
            if (dictionary is null) return;
            foreach (var entry in dictionary)
                Validate(entry.Value, description, depth + 1);
        }

        bool IsLive(PdfIndirectReference reference)
        {
            if (_freed.ContainsKey(reference.ObjectNumber)) return false;
            if (_objects.TryGetValue(reference.ObjectNumber, out PendingObject? pending))
                return pending.Generation == reference.Generation;
            if (!_document.CrossReferences.TryGetValue(
                    reference.ObjectNumber, out PdfCrossReferenceEntry entry))
                return false;
            return entry.Type switch
            {
                PdfCrossReferenceEntryType.InUse => entry.Field2 == reference.Generation,
                PdfCrossReferenceEntryType.Compressed => reference.Generation == 0,
                _ => false
            };
        }
    }

    private void ValidateStandardTrailerState()
    {
        if (!_document.CrossReferences.TryGetTrailerValue(RootName, out PdfObject root)
            || root is not PdfIndirectReference rootReference
            || !IsLive(rootReference)
            || ResolveCurrentValue(rootReference,
                "The trailer /Root value") is not PdfDictionary catalog
            || !catalog.TryGetValue(TypeName, out PdfObject catalogType)
            || ResolveCurrentValue(catalogType,
                "The catalog /Type value") is not PdfName catalogTypeName
            || catalogTypeName.ValueAsLatin1() != "Catalog")
            throw new InvalidOperationException(
                "An incremental update requires trailer /Root to reference a live catalog dictionary.");

        PdfObject? information = _documentInformationSpecified
            ? _documentInformation
            : _document.CrossReferences.TryGetTrailerValue(InfoName, out PdfObject inheritedInfo)
                ? inheritedInfo : null;
        if (information is not null
            && (information is not PdfIndirectReference informationReference
                || !IsLive(informationReference)
                || ResolveCurrentValue(informationReference,
                    "The trailer /Info value") is not PdfDictionary))
            throw new InvalidOperationException(
                "An incremental update requires trailer /Info to reference a live dictionary.");
        if (information is PdfIndirectReference liveInformation
            && ResolveCurrentValue(liveInformation,
                "The trailer /Info value") is PdfDictionary informationDictionary)
        {
            if (_documentInformationSpecified)
                ValidateDocumentInformation(informationDictionary, value =>
                    ResolveCurrentValue(value,
                        "A document-information value"), _documentInformationKeptKeys);
            ValidateInformationGraph(informationDictionary, 0,
                []);
        }

        bool hasIdentifiers = _document.CrossReferences.TryGetTrailerValue(
            IdName, out PdfObject identifiers);
        if (hasIdentifiers
            && (identifiers is not PdfArray identifierArray
                || identifierArray.Count != 2
                || identifierArray.Any(item => item is not PdfString)))
            throw new InvalidOperationException(
                "An incremental update requires trailer /ID to be an array of two strings.");
        if (_document.IsEncrypted && !hasIdentifiers)
            throw new InvalidOperationException(
                "An encrypted incremental update requires trailer /ID document identifiers.");

        if (_document.CrossReferences.TryGetTrailerValue(
                EncryptName, out PdfObject encryption)
            && (encryption is not PdfIndirectReference encryptionReference
                || !IsLive(encryptionReference)
                || ResolveCurrentValue(encryptionReference,
                    "The trailer /Encrypt value") is not PdfDictionary))
            throw new InvalidOperationException(
                "An incremental update requires trailer /Encrypt to reference a live dictionary.");

        bool IsLive(PdfIndirectReference reference)
        {
            if (_freed.ContainsKey(reference.ObjectNumber)) return false;
            if (_objects.TryGetValue(reference.ObjectNumber, out PendingObject? pending))
                return pending.Generation == reference.Generation;
            if (!_document.CrossReferences.TryGetValue(
                    reference.ObjectNumber, out PdfCrossReferenceEntry entry))
                return false;
            return entry.Type switch
            {
                PdfCrossReferenceEntryType.InUse => entry.Field2 == reference.Generation,
                PdfCrossReferenceEntryType.Compressed => reference.Generation == 0,
                _ => false
            };
        }

        void ValidateInformationGraph(PdfObject value, int depth,
            HashSet<(int ObjectNumber, int Generation)> visited)
        {
            if (depth > 64)
                throw new NotSupportedException(
                    "The document-information graph is too deeply nested.");
            if (value is PdfIndirectReference reference)
            {
                if (!IsLive(reference))
                    throw new InvalidOperationException(
                        "Trailer /Info value contains a stale indirect reference.");
                if (!visited.Add((reference.ObjectNumber, reference.Generation))) return;
                ValidateInformationGraph(ResolveCurrentValue(reference,
                    "Trailer /Info value"), depth + 1, visited);
                return;
            }
            if (value is PdfArray array)
            {
                foreach (PdfObject item in array)
                    ValidateInformationGraph(item, depth + 1, visited);
                return;
            }
            PdfDictionary? dictionary = value switch
            {
                PdfDictionary directDictionary => directDictionary,
                PdfStream stream => stream.Dictionary,
                _ => null
            };
            if (dictionary is null) return;
            foreach (var entry in dictionary)
                ValidateInformationGraph(entry.Value, depth + 1, visited);
        }
    }

    private PdfObject ResolveCurrentValue(PdfObject value, string description)
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32)
                throw new InvalidOperationException(
                    $"{description} is too deeply indirect.");
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException(
                    $"{description} contains an indirect-reference cycle.");
            if (_freed.ContainsKey(reference.ObjectNumber))
                return PdfNull.Instance;
            value = _objects.TryGetValue(reference.ObjectNumber, out PendingObject? pending)
                    && pending.Generation == reference.Generation
                ? pending.Value
                : _document.Resolve(reference);
        }
        return value;
    }

    private HashSet<int> CurrentEncryptionBootstrapObjectNumbers()
    {
        var result = new HashSet<int>();
        if (!_document.CrossReferences.TryGetTrailerValue(
                EncryptName, out PdfObject value))
            return result;
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32)
                throw new InvalidOperationException(
                    "The trailer /Encrypt value is too deeply indirect.");
            var identity = (reference.ObjectNumber, reference.Generation);
            if (!visited.Add(identity))
                throw new InvalidOperationException(
                    "The trailer /Encrypt value contains an indirect-reference cycle.");
            result.Add(reference.ObjectNumber);
            if (_freed.ContainsKey(reference.ObjectNumber))
                throw new InvalidOperationException(
                    "The trailer /Encrypt value contains a freed indirect reference.");
            value = _objects.TryGetValue(reference.ObjectNumber,
                    out PendingObject? pending)
                && pending.Generation == reference.Generation
                    ? pending.Value
                    : _document.Resolve(reference);
        }
        if (value is not PdfDictionary)
            throw new InvalidOperationException(
                "The trailer /Encrypt value does not resolve to a dictionary.");
        return result;
    }

    private static void ValidateDocumentInformation(
        PdfDictionary information, Func<PdfObject, PdfObject> resolve,
        IReadOnlyCollection<string> keptKeys)
    {
        foreach (string key in new[]
            { "Title", "Author", "Subject", "Keywords", "Creator", "Producer",
              "CreationDate", "ModDate" })
            if (!keptKeys.Contains(key)
                && information.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)),
                    out PdfObject value)
                && resolve(value) is not PdfString)
                throw new InvalidOperationException(
                    $"Trailer /Info /{key} value is not a string.");
        foreach (string key in new[] { "CreationDate", "ModDate" })
        {
            PdfName name = new(Encoding.ASCII.GetBytes(key));
            if (!keptKeys.Contains(key) && information.TryGetValue(name, out PdfObject value)
                && resolve(value) is PdfString date && !PdfDateStringValidator.IsValid(date))
                throw new InvalidOperationException(
                    $"Trailer /Info /{key} value is not a valid PDF date string.");
        }
        PdfName trappedName = new("Trapped"u8);
        if (keptKeys.Contains("Trapped") || !information.TryGetValue(trappedName, out PdfObject trapped)) return;
        string state = (resolve(trapped) as PdfName)?.ValueAsLatin1()
            ?? throw new InvalidOperationException(
                "Trailer /Info /Trapped value is not a name.");
        if (state is not ("True" or "False" or "Unknown"))
            throw new InvalidOperationException(
                $"Trailer /Info /Trapped value /{state} is not defined.");
    }

    // A document opened by scanning for objects can resolve one that its cross-reference data does not
    // list. That is a damaged file, not a caller mistake, so it gets a refusal the user can act on.
    private Exception NotInUse(int objectNumber) => _document.UsesCompatibilityRecovery
        ? new InvalidOperationException(
            "This document had to be repaired when it was opened, and its cross-reference data does not "
            + "cover every object, so it cannot be edited in place. Resave the document to repair it, "
            + "then edit the saved copy.")
        : new ArgumentException($"Object {objectNumber} is not currently in use.", nameof(objectNumber));

    private static int InitialSize(PdfDocument document)
    {
        long declaredSize = 0;
        if (document.CrossReferences.TryGetTrailerValue(SizeName, out PdfObject sizeValue)
            && sizeValue is PdfInteger integer)
            declaredSize = integer.Value;
        int highestEntry = document.CrossReferences.Keys.DefaultIfEmpty(0).Max();
        long result = Math.Max(declaredSize, (long)highestEntry + 1);
        if (result is <= 0 or > int.MaxValue)
            throw new NotSupportedException("The PDF trailer /Size is outside the supported object-number range.");
        return (int)result;
    }

    private static void WriteAscii(Stream output, string value)
    {
        foreach (char character in value)
            output.WriteByte(checked((byte)character));
    }

    private sealed record PendingObject(int ObjectNumber, int Generation, PdfObject Value);
    private sealed record WrittenObject(int ObjectNumber, int Generation, int Offset);
    private sealed record CompressedObject(int ObjectNumber, int ObjectStreamNumber, int Index);
    private sealed record FreedObject(int ObjectNumber, int Generation);
}
