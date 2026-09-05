using System.Globalization;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>
/// A parsed PDF file whose indirect objects are loaded lazily through its merged cross-reference
/// table. Compressed objects are decoded and cached by object stream.
/// </summary>
public sealed class PdfDocument
{
    /// <summary>Maximum number of compressed members accepted in one object stream.</summary>
    public const int MaximumObjectsPerObjectStream = 1_000_000;

    private static readonly PdfName TypeName = new("Type"u8);
    private static readonly PdfName ObjectStreamTypeName = new("ObjStm"u8);
    private static readonly PdfName ObjectCountName = new("N"u8);
    private static readonly PdfName FirstObjectOffsetName = new("First"u8);

    private readonly ReadOnlyMemory<byte> _source;
    private readonly bool _compatibilityRecovery;
    private readonly Dictionary<int, PdfObject> _objects = [];
    private readonly Dictionary<int, ObjectStreamContents> _objectStreams = [];
    private readonly HashSet<int> _resolving = [];
    private PdfStandardSecurityHandler? _security;
    private int? _encryptionObjectNumber;
    private HashSet<int> _encryptionBootstrapObjectNumbers = [];

    private PdfDocument(ReadOnlyMemory<byte> source, PdfCrossReferenceTable crossReferences,
        bool compatibilityRecovery = false)
    {
        _source = source;
        CrossReferences = crossReferences;
        _compatibilityRecovery = compatibilityRecovery;
    }

    /// <summary>Gets the merged cross-reference revision table.</summary>
    public PdfCrossReferenceTable CrossReferences { get; }
    /// <summary>Gets the parsed PDF header.</summary>
    public PdfHeader Header => CrossReferences.Header;
    /// <summary>Gets the newest revision trailer dictionary.</summary>
    public PdfDictionary Trailer => CrossReferences.LatestTrailer;
    internal ReadOnlyMemory<byte> Source => _source;
    internal bool UsesCompatibilityRecovery => _compatibilityRecovery;
    /// <summary>Gets whether the document declares an encryption security handler.</summary>
    public bool IsEncrypted => CrossReferences.TryGetTrailerValue(new PdfName("Encrypt"u8), out _);
    /// <summary>Gets whether encrypted objects are available in decrypted form.</summary>
    public bool IsDecrypted => !IsEncrypted || _security is not null;
    /// <summary>
    /// The password role that authenticated this document, or <see cref="PdfPasswordAuthenticationRole.None"/>
    /// when the document is unencrypted or has not been authenticated.
    /// </summary>
    public PdfPasswordAuthenticationRole PasswordAuthenticationRole =>
        _security?.AuthenticationRole ?? PdfPasswordAuthenticationRole.None;
    /// <summary>
    /// The authenticated Standard Security permission flags, or <see langword="null"/> when the
    /// document is unencrypted or has not been authenticated.
    /// </summary>
    public PdfDocumentPermissions? DeclaredPermissions => _security?.Permissions;
    internal int? EncryptionObjectNumber => _encryptionObjectNumber;
    internal IReadOnlySet<int> EncryptionBootstrapObjectNumbers =>
        _encryptionBootstrapObjectNumbers;
    internal PdfObject EncryptObject(
        int objectNumber, PdfObject value,
        Func<PdfIndirectReference, PdfObject>? resolve = null) =>
        _security is not null && objectNumber != _encryptionObjectNumber
            ? _security.Encrypt(value, objectNumber,
                CrossReferences.TryGetValue(objectNumber, out PdfCrossReferenceEntry entry)
                    && entry.Type == PdfCrossReferenceEntryType.InUse ? entry.Field2 : 0,
                resolve ?? Resolve)
            : value;

    /// <summary>Opens and validates an unencrypted PDF from memory.</summary>
    public static PdfDocument Open(ReadOnlyMemory<byte> source)
    {
        // Own the bytes so lazy resolution cannot observe caller mutations after validation.
        byte[] ownedSource = source.ToArray();
        return new PdfDocument(ownedSource, PdfCrossReferenceTable.Read(ownedSource));
    }

    /// <summary>
    /// Opens a PDF using bounded compatibility recoveries for malformed files accepted by
    /// mainstream viewers. Strict parsing remains the default through <see cref="Open(ReadOnlyMemory{byte})"/>.
    /// </summary>
    public static PdfDocument OpenWithCompatibilityRecovery(ReadOnlyMemory<byte> source)
    {
        byte[] ownedSource = source.ToArray();
        return new PdfDocument(ownedSource,
            PdfCrossReferenceTable.Read(ownedSource, compatibilityRecovery: true),
            compatibilityRecovery: true);
    }

    /// <summary>Opens and authenticates a password-encrypted PDF.</summary>
    public static PdfDocument Open(ReadOnlyMemory<byte> source, string password)
    {
        PdfDocument document = Open(source);
        if (!document.CrossReferences.TryGetTrailerValue(
                new PdfName("Encrypt"u8), out PdfObject? encryptionValue))
            return document;
        PdfIndirectReference encryptionReference = encryptionValue as PdfIndirectReference
            ?? throw new InvalidOperationException("The trailer /Encrypt value is not indirect.");
        (PdfDictionary encryption, int encryptionObjectNumber) =
            document.ResolveEncryptionDictionary(encryptionReference);
        document._encryptionObjectNumber = encryptionObjectNumber;
        ReadOnlyMemory<byte> permanentIdentifier = ReadOnlyMemory<byte>.Empty;
        if (document.CrossReferences.TryGetTrailerValue(new PdfName("ID"u8), out PdfObject? idValue)
            && idValue is PdfArray { Count: > 0 } identifiers
            && identifiers[0] is PdfString identifier)
            permanentIdentifier = identifier.Bytes;
        document._security = PdfStandardSecurityHandler.Create(
            encryption, password, permanentIdentifier);
        document._objects.Clear();
        document._objectStreams.Clear();
        return document;
    }

    /// <summary>Opens and authenticates a certificate-recipient encrypted PDF.</summary>
    public static PdfDocument Open(ReadOnlyMemory<byte> source, X509Certificate2 recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        PdfDocument document = Open(source);
        if (!document.CrossReferences.TryGetTrailerValue(
                new PdfName("Encrypt"u8), out PdfObject? encryptionValue))
            return document;
        PdfIndirectReference encryptionReference = encryptionValue as PdfIndirectReference
            ?? throw new InvalidOperationException("The trailer /Encrypt value is not indirect.");
        (PdfDictionary encryption, int encryptionObjectNumber) =
            document.ResolveEncryptionDictionary(encryptionReference);
        document._encryptionObjectNumber = encryptionObjectNumber;
        document._security = PdfStandardSecurityHandler.CreateCertificate(encryption, recipient);
        document._objects.Clear();
        document._objectStreams.Clear();
        return document;
    }

    private (PdfDictionary Dictionary, int ObjectNumber) ResolveEncryptionDictionary(
        PdfIndirectReference reference)
    {
        PdfObject value = reference;
        int dictionaryObjectNumber = reference.ObjectNumber;
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        for (int depth = 0; value is PdfIndirectReference current; depth++)
        {
            if (depth >= 32)
                throw new InvalidOperationException(
                    "The trailer /Encrypt indirect chain is too deep.");
            if (!visited.Add((current.ObjectNumber, current.Generation)))
                throw new InvalidOperationException(
                    "The trailer /Encrypt indirect chain contains a cycle.");
            dictionaryObjectNumber = current.ObjectNumber;
            value = Resolve(current);
        }
        _encryptionBootstrapObjectNumbers = [.. visited.Select(identity => identity.ObjectNumber)];
        return value is PdfDictionary dictionary
            ? (dictionary, dictionaryObjectNumber)
            : throw new InvalidOperationException(
                "The trailer /Encrypt value is not a dictionary.");
    }

    /// <summary>
    /// Resolves the current cross-reference entry for an object number. Free, absent, and
    /// future xref entry types resolve to the PDF null object.
    /// </summary>
    public PdfObject Resolve(int objectNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectNumber);
        if (!CrossReferences.TryGetValue(objectNumber, out PdfCrossReferenceEntry entry))
            return PdfNull.Instance;
        return ResolveEntry(entry);
    }

    /// <summary>
    /// Resolves an indirect reference. A stale generation number resolves to null as required
    /// for a reference that no longer identifies the current in-use object.
    /// </summary>
    public PdfObject Resolve(PdfIndirectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!CrossReferences.TryGetValue(reference.ObjectNumber, out PdfCrossReferenceEntry entry))
            return ResolveMissingReference(reference);
        if (_compatibilityRecovery
            && entry.Type is PdfCrossReferenceEntryType.Free or PdfCrossReferenceEntryType.Null)
            return ResolveMissingReference(reference);

        int currentGeneration = entry.Type == PdfCrossReferenceEntryType.InUse ? entry.Field2 : 0;
        if (entry.Type is PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed
            && reference.Generation != currentGeneration)
            return PdfNull.Instance;
        return ResolveEntry(entry);
    }

    private PdfObject ResolveMissingReference(PdfIndirectReference reference)
    {
        if (!_compatibilityRecovery)
            return PdfNull.Instance;

        byte[] header = Encoding.ASCII.GetBytes(
            $"{reference.ObjectNumber} {reference.Generation} obj");
        ReadOnlySpan<byte> source = _source.Span;
        var candidates = new List<int>();
        int searchStart = 0;
        while (searchStart + header.Length <= source.Length)
        {
            int relative = source[searchStart..].IndexOf(header);
            if (relative < 0)
                break;
            int candidate = searchStart + relative;
            if ((candidate == 0 || IsObjectBoundary(source[candidate - 1]))
                && (candidate + header.Length == source.Length
                    || IsObjectBoundary(source[candidate + header.Length])))
                candidates.Add(candidate);
            searchStart = candidate + 1;
        }

        for (int index = candidates.Count - 1; index >= 0; index--)
        {
            var recovered = new PdfCrossReferenceEntry(
                reference.ObjectNumber, PdfCrossReferenceEntryType.InUse,
                candidates[index], reference.Generation);
            try
            {
                return ResolveEntry(recovered);
            }
            catch (PdfSyntaxException)
            {
            }
        }
        return PdfNull.Instance;
    }

    private PdfObject ResolveEntry(PdfCrossReferenceEntry entry)
    {
        if (entry.Type is PdfCrossReferenceEntryType.Free or PdfCrossReferenceEntryType.Null)
            return PdfNull.Instance;
        if (_objects.TryGetValue(entry.ObjectNumber, out PdfObject? cached))
            return cached;
        if (!_resolving.Add(entry.ObjectNumber))
            throw Error($"Resolving object {entry.ObjectNumber} forms a cycle", EntryOffset(entry));

        try
        {
            PdfObject value = entry.Type switch
            {
                PdfCrossReferenceEntryType.InUse => ReadIndirectObject(entry),
                PdfCrossReferenceEntryType.Compressed => ReadCompressedObject(entry),
                _ => PdfNull.Instance
            };
            _objects.Add(entry.ObjectNumber, value);
            return value;
        }
        finally
        {
            _resolving.Remove(entry.ObjectNumber);
        }
    }

    private PdfObject ReadIndirectObject(PdfCrossReferenceEntry entry)
    {
        int offset = checked((int)entry.Field1);
        PdfIndirectObject indirect;
        try
        {
            indirect = ParseIndirectObject(offset);
        }
        catch (PdfSyntaxException) when (_compatibilityRecovery
            && FindNearbyIndirectObjectOffset(entry, offset) is int recoveredOffset)
        {
            indirect = ParseIndirectObject(recoveredOffset);
        }
        if (_compatibilityRecovery
            && (indirect.ObjectNumber != entry.ObjectNumber || indirect.Generation != entry.Field2)
            && FindNearbyIndirectObjectOffset(entry, offset) is int matchingOffset)
            indirect = ParseIndirectObject(matchingOffset);
        if (indirect.ObjectNumber != entry.ObjectNumber || indirect.Generation != entry.Field2)
        {
            throw Error(
                $"Cross-reference entry {entry.ObjectNumber} {entry.Field2} points to " +
                $"object {indirect.ObjectNumber} {indirect.Generation}",
                checked((int)entry.Field1));
        }
        return _security is not null && entry.ObjectNumber != _encryptionObjectNumber
            ? _security.Decrypt(
                indirect.Value, entry.ObjectNumber, entry.Field2, Resolve)
            : indirect.Value;

        PdfIndirectObject ParseIndirectObject(int objectOffset) =>
            new PdfObjectParser(_source, objectOffset,
                ResolveStreamLength, _compatibilityRecovery).ParseIndirectObject();
    }

    private int? FindNearbyIndirectObjectOffset(PdfCrossReferenceEntry entry, int offset)
    {
        const int maximumDistance = 1_048_576;
        byte[] header = Encoding.ASCII.GetBytes(
            $"{entry.ObjectNumber} {entry.Field2} obj");
        int maximum = Math.Min(maximumDistance, _source.Length);
        for (int distance = 1; distance <= maximum; distance++)
        {
            foreach (int candidate in new[] { offset + distance, offset - distance })
            {
                if (candidate < 0 || candidate + header.Length > _source.Length
                    || candidate > 0 && !IsObjectBoundary(_source.Span[candidate - 1])
                    || candidate + header.Length < _source.Length
                        && !IsObjectBoundary(_source.Span[candidate + header.Length])
                    || !_source.Span.Slice(candidate, header.Length).SequenceEqual(header))
                    continue;
                return candidate;
            }
        }
        return null;

    }

    private static bool IsObjectBoundary(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32
            or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private long ResolveStreamLength(PdfIndirectReference reference)
    {
        PdfObject value = reference;
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        for (int depth = 0; value is PdfIndirectReference current; depth++)
        {
            if (depth >= 32)
                throw Error(
                    $"Stream Length reference {reference.ObjectNumber} {reference.Generation} is too deeply indirect",
                    0);
            if (!visited.Add((current.ObjectNumber, current.Generation)))
                throw Error(
                    $"Stream Length reference {reference.ObjectNumber} {reference.Generation} contains an indirect-reference cycle",
                    0);
            value = Resolve(current);
        }
        if (value is not PdfInteger integer)
            throw Error($"Stream Length reference {reference.ObjectNumber} {reference.Generation} is not an integer", 0);
        return integer.Value;
    }

    private PdfObject ReadCompressedObject(PdfCrossReferenceEntry entry)
    {
        int streamNumber = checked((int)entry.Field1);
        ObjectStreamContents contents = ReadObjectStream(streamNumber);
        if (entry.Field2 < 0 || entry.Field2 >= contents.OrderedObjects.Count)
            throw Error($"Compressed object {entry.ObjectNumber} has an invalid object-stream index", streamNumber);

        ObjectStreamItem item = contents.OrderedObjects[entry.Field2];
        if (item.ObjectNumber != entry.ObjectNumber)
        {
            throw Error(
                $"Object-stream index {entry.Field2} names object {item.ObjectNumber}, not {entry.ObjectNumber}",
                streamNumber);
        }
        return item.Value;
    }

    private ObjectStreamContents ReadObjectStream(int streamNumber)
    {
        if (_objectStreams.TryGetValue(streamNumber, out ObjectStreamContents? cached))
            return cached;
        if (!CrossReferences.TryGetValue(streamNumber, out PdfCrossReferenceEntry entry)
            || entry.Type != PdfCrossReferenceEntryType.InUse)
            throw Error($"Object stream {streamNumber} is not an uncompressed in-use object", streamNumber);
        if (entry.Field2 != 0)
            throw Error($"Object stream {streamNumber} must have generation 0", EntryOffset(entry));

        PdfObject resolved = ResolveEntry(entry);
        if (resolved is not PdfStream stream)
            throw Error($"Object stream {streamNumber} does not contain a stream", EntryOffset(entry));
        RequireObjectStreamDictionary(stream.Dictionary, EntryOffset(entry));

        int objectCount = RequiredNonNegativeInt(stream.Dictionary, ObjectCountName, EntryOffset(entry));
        if (objectCount > MaximumObjectsPerObjectStream)
            throw Error(
                $"An object stream cannot contain more than {MaximumObjectsPerObjectStream:N0} objects",
                EntryOffset(entry));
        int firstObjectOffset = RequiredNonNegativeInt(stream.Dictionary, FirstObjectOffsetName, EntryOffset(entry));
        byte[] decoded = PdfStreamDecoder.Decode(stream, Resolve);
        if (firstObjectOffset > decoded.Length)
            throw Error("Object stream /First points beyond the decoded stream", EntryOffset(entry));

        List<ObjectHeader> headers = ReadObjectHeaders(decoded, objectCount, firstObjectOffset, EntryOffset(entry));
        HashSet<(int ObjectNumber, int Index)> registeredHeaders =
            CrossReferences.RegisteredHeadersForCurrentObjectStream(streamNumber);
        var items = new List<ObjectStreamItem>(objectCount);
        var objectNumbers = new HashSet<int>();
        for (int index = 0; index < headers.Count; index++)
        {
            ObjectHeader header = headers[index];
            if (!objectNumbers.Add(header.ObjectNumber))
                throw Error($"Object stream contains object {header.ObjectNumber} more than once", EntryOffset(entry));
            if (header.ObjectNumber == streamNumber)
                throw Error(
                    $"Object stream {streamNumber} cannot contain itself",
                    EntryOffset(entry));
            if (!CrossReferences.TryGetValue(header.ObjectNumber,
                    out PdfCrossReferenceEntry compressedEntry))
                throw Error(
                    $"Object stream header entry {index} for object {header.ObjectNumber} " +
                    "has no cross-reference entry",
                    EntryOffset(entry));
            bool isCurrent = compressedEntry.Type == PdfCrossReferenceEntryType.Compressed
                && compressedEntry.Field1 == streamNumber;
            if (isCurrent && compressedEntry.Field2 != index)
                throw Error(
                    $"Object stream header entry {index} for object {header.ObjectNumber} " +
                    "does not match its compressed cross-reference entry",
                    EntryOffset(entry));
            bool wasRegistered = isCurrent
                || registeredHeaders.Contains((header.ObjectNumber, index));
            if (!wasRegistered)
                throw Error(
                    $"Object stream header entry {index} for object {header.ObjectNumber} " +
                    "does not match any compressed cross-reference entry",
                    EntryOffset(entry));

            int start = checked(firstObjectOffset + header.RelativeOffset);
            int end = index + 1 < headers.Count
                ? checked(firstObjectOffset + headers[index + 1].RelativeOffset)
                : decoded.Length;
            if (start >= end || end > decoded.Length)
                throw Error("Object stream offsets do not define non-empty objects in ascending order", EntryOffset(entry));

            PdfObject value = isCurrent
                ? new PdfObjectParser(decoded.AsMemory(start, end - start),
                    allowDuplicateDictionaryKeys: _compatibilityRecovery).ParseSingleObject()
                : PdfNull.Instance;
            items.Add(new ObjectStreamItem(header.ObjectNumber, value));
        }

        var contents = new ObjectStreamContents(items);
        _objectStreams.Add(streamNumber, contents);
        return contents;
    }

    private static List<ObjectHeader> ReadObjectHeaders(
        byte[] decoded,
        int objectCount,
        int firstObjectOffset,
        int sourceOffset)
    {
        var tokenizer = new PdfTokenizer(decoded.AsMemory(0, firstObjectOffset));
        var headers = new List<ObjectHeader>(objectCount);
        int previousOffset = -1;
        for (int index = 0; index < objectCount; index++)
        {
            int objectNumber = RequiredHeaderInteger(tokenizer.Read(), "object number", sourceOffset);
            int relativeOffset = RequiredHeaderInteger(tokenizer.Read(), "object offset", sourceOffset);
            if (objectNumber == 0)
                throw Error("Object stream object numbers must be greater than zero", sourceOffset);
            if (relativeOffset <= previousOffset || relativeOffset >= decoded.Length - firstObjectOffset)
                throw Error("Object stream offsets must be ascending and point inside the object data", sourceOffset);
            previousOffset = relativeOffset;
            headers.Add(new ObjectHeader(objectNumber, relativeOffset));
        }

        PdfToken trailing = tokenizer.Read();
        if (trailing.Kind != PdfTokenKind.EndOfInput)
            throw Error("Object stream header contains more entries than /N declares", sourceOffset);
        return headers;
    }

    private void RequireObjectStreamDictionary(PdfDictionary dictionary, int offset)
    {
        if (!dictionary.TryGetValue(TypeName, out PdfObject type)
            || ResolveObjectStreamScalar(type, "Object stream /Type") is not PdfName name
            || !name.Equals(ObjectStreamTypeName))
            throw Error("A compressed object must be stored in a /Type /ObjStm stream", offset);
    }

    private int RequiredNonNegativeInt(PdfDictionary dictionary, PdfName name, int offset)
    {
        if (!dictionary.TryGetValue(name, out PdfObject value)
            || ResolveObjectStreamScalar(value,
                $"Object stream {name}") is not PdfInteger integer
            || integer.Value is < 0 or > int.MaxValue)
            throw Error($"Object stream {name} must be a non-negative 32-bit integer", offset);
        return (int)integer.Value;
    }

    private PdfObject ResolveObjectStreamScalar(PdfObject value, string description)
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32)
                throw Error($"{description} is too deeply indirect", 0);
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw Error($"{description} contains an indirect-reference cycle", 0);
            value = Resolve(reference);
        }
        return value;
    }

    private static int RequiredHeaderInteger(PdfToken token, string description, int sourceOffset)
    {
        if (token.Kind != PdfTokenKind.Integer
            || !int.TryParse(token.Value.Span, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value < 0)
            throw Error($"Object stream header {description} must be a non-negative integer", sourceOffset);
        return value;
    }

    private static int EntryOffset(PdfCrossReferenceEntry entry) =>
        entry.Type == PdfCrossReferenceEntryType.InUse ? checked((int)entry.Field1) : 0;

    private static PdfSyntaxException Error(string message, int offset) => new(message, Math.Max(offset, 0));

    private readonly record struct ObjectHeader(int ObjectNumber, int RelativeOffset);
    private sealed record ObjectStreamItem(int ObjectNumber, PdfObject Value);
    private sealed record ObjectStreamContents(IReadOnlyList<ObjectStreamItem> OrderedObjects);
}
