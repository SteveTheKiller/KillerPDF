using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads document-level embedded files without opening or executing them.</summary>
public static class PdfAttachmentReader
{
    /// <summary>Reads attachment metadata and immutable payloads from the embedded-files name tree.</summary>
    public static IReadOnlyList<PdfAttachmentInfo> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException("Authenticate the document before reading attachments.");
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(Name("Names"), out PdfObject? namesValue)
            || Resolve(document, namesValue) is not PdfDictionary names
            || !names.TryGetValue(Name("EmbeddedFiles"), out PdfObject? filesValue)) return [];
        var result = new List<PdfAttachmentInfo>();
        foreach (PdfNameTreeEntry entry in PdfNameTree.Read(document, filesValue))
        {
            string treeName = PdfUnicodeEncoding.DecodeTextString(entry.Key.Bytes.Span,
                "An embedded-file name");
            PdfIndirectReference? specificationReference = entry.Value as PdfIndirectReference;
            PdfDictionary specification = Resolve(document, entry.Value) as PdfDictionary
                ?? throw new InvalidOperationException("An embedded-file name does not reference a file specification.");
            string fileName = Text(document, specification, "UF")
                ?? Text(document, specification, "F") ?? treeName;
            PdfDictionary embedded = Value<PdfDictionary>(document, specification, "EF",
                "An embedded file specification has no /EF dictionary.");
            PdfObject streamValue = embedded.TryGetValue(Name("UF"), out PdfObject? unicodeStream)
                ? unicodeStream : embedded.TryGetValue(Name("F"), out PdfObject? regularStream)
                    ? regularStream : throw new InvalidOperationException("An embedded file specification has no payload stream.");
            PdfIndirectReference? streamReference = streamValue as PdfIndirectReference;
            PdfStream stream = Resolve(document, streamValue) as PdfStream
                ?? throw new InvalidOperationException("An embedded file payload is not a stream.");
            string mimeType = stream.Dictionary.TryGetValue(Name("Subtype"), out PdfObject? subtype)
                && Resolve(document, subtype) is PdfName mime ? mime.ValueAsLatin1()
                : "application/octet-stream";
            string? relationshipName = specification.TryGetValue(Name("AFRelationship"), out PdfObject? relationshipValue)
                && Resolve(document, relationshipValue) is PdfName relationship ? relationship.ValueAsLatin1() : null;
            PdfAssociatedFileRelationship relationshipKind = Enum.TryParse(relationshipName,
                ignoreCase: false, out PdfAssociatedFileRelationship parsedRelationship)
                ? parsedRelationship : PdfAssociatedFileRelationship.Unspecified;
            byte[] data = PdfStreamDecoder.Decode(stream, document.Resolve,
                PdfContentStreamReader.MaximumSourceBytes);
            PdfDictionary? parameters = stream.Dictionary.TryGetValue(
                Name("Params"), out PdfObject? parametersValue)
                ? Resolve(document, parametersValue) as PdfDictionary
                    ?? throw new InvalidOperationException(
                        "An embedded file /Params value is not a dictionary.")
                : null;
            long? declaredSize = OptionalInteger(document, parameters, "Size");
            byte[]? declaredChecksum = OptionalBytes(
                document, parameters, "CheckSum");
            result.Add(new PdfAttachmentInfo
            {
                FileName = fileName,
                Description = Text(document, specification, "Desc"),
                MimeType = mimeType,
                Relationship = relationshipKind,
                Data = data,
                DeclaredSize = declaredSize,
                SizeMatches = declaredSize.HasValue
                    ? declaredSize.Value == data.LongLength : null,
                CreationDate = OptionalDate(
                    document, parameters, "CreationDate"),
                ModificationDate = OptionalDate(
                    document, parameters, "ModDate"),
                DeclaredChecksum = declaredChecksum,
                ChecksumMatches = declaredChecksum is not null
                    ? CryptographicOperations.FixedTimeEquals(
                        declaredChecksum, MD5.HashData(data)) : null,
                FileSpecificationObjectNumber = specificationReference?.ObjectNumber,
                EmbeddedFileObjectNumber = streamReference?.ObjectNumber,
                HasUnsafeFileName = !IsSafeFileName(fileName),
                IsPotentiallyExecutable = IsPotentiallyExecutable(fileName)
            });
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Returns a destination path confined to the selected directory.</summary>
    public static string GetSafeExtractionPath(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!IsSafeFileName(fileName))
            throw new ArgumentException("The attachment name is unsafe for extraction.", nameof(fileName));
        string root = Path.GetFullPath(directory);
        string candidate = Path.GetFullPath(Path.Combine(root, fileName));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The attachment path escapes the selected directory.");
        return candidate;
    }

    private static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && !Path.IsPathRooted(fileName)
        && fileName == Path.GetFileName(fileName)
        && fileName is not "." and not ".."
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsPotentiallyExecutable(string fileName) =>
        new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".msi", ".scr", ".js", ".vbs", ".lnk" }
            .Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    private static T Value<T>(PdfDocument document, PdfDictionary dictionary, string key, string error)
        where T : PdfObject => dictionary.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(document, value) is T typed ? typed : throw new InvalidOperationException(error);

    private static string? Text(PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, $"A file specification /{key} value")
            : throw new InvalidOperationException($"A file specification /{key} value is not a string.");
    }

    private static long? OptionalInteger(
        PdfDocument document, PdfDictionary? dictionary, string key)
    {
        if (dictionary is null
            || !dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfInteger integer && integer.Value >= 0
            ? integer.Value
            : throw new InvalidOperationException(
                $"An embedded file /{key} value is not a nonnegative integer.");
    }

    private static byte[]? OptionalBytes(
        PdfDocument document, PdfDictionary? dictionary, string key)
    {
        if (dictionary is null
            || !dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfString text ? text.Bytes.ToArray()
            : throw new InvalidOperationException(
                $"An embedded file /{key} value is not a string.");
    }

    private static DateTimeOffset? OptionalDate(
        PdfDocument document, PdfDictionary? dictionary, string key)
    {
        if (dictionary is null
            || !dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        if (Resolve(document, value) is not PdfString text)
            throw new InvalidOperationException(
                $"An embedded file /{key} value is not a string.");
        string date = PdfUnicodeEncoding.DecodeTextString(
            text.Bytes.Span, $"An embedded file /{key} value");
        try
        {
            if (!date.StartsWith("D:", StringComparison.Ordinal) || date.Length < 6)
                throw new FormatException();
            string digits = new([.. date.Skip(2).TakeWhile(char.IsAsciiDigit)]);
            if (digits.Length is not (4 or 6 or 8 or 10 or 12 or 14))
                throw new FormatException();
            int Part(int offset, int length, int fallback) =>
                digits.Length >= offset + length
                    ? int.Parse(digits.AsSpan(offset, length),
                        CultureInfo.InvariantCulture) : fallback;
            int year = Part(0, 4, 1), month = Part(4, 2, 1);
            int day = Part(6, 2, 1), hour = Part(8, 2, 0);
            int minute = Part(10, 2, 0), second = Part(12, 2, 0);
            string suffix = date[(2 + digits.Length)..];
            TimeSpan zone = TimeSpan.Zero;
            if (suffix.Length > 0 && suffix != "Z")
            {
                char sign = suffix[0];
                if (sign is not ('+' or '-')) throw new FormatException();
                string compact = suffix[1..].Replace(
                    "'", string.Empty, StringComparison.Ordinal);
                if (compact.Length != 4
                    || !int.TryParse(compact[..2], out int zoneHour)
                    || !int.TryParse(compact[2..], out int zoneMinute))
                    throw new FormatException();
                zone = new TimeSpan(zoneHour, zoneMinute, 0)
                    * (sign == '-' ? -1 : 1);
            }
            return new DateTimeOffset(
                year, month, day, hour, minute, second, zone);
        }
        catch (Exception error) when (
            error is FormatException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException(
                $"An embedded file /{key} value is not a valid PDF date.", error);
        }
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An attachment reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

/// <summary>Metadata and payload for one embedded file.</summary>
public sealed record PdfAttachmentInfo
{
    /// <summary>Gets the embedded file name.</summary>
    public required string FileName { get; init; }
    /// <summary>Gets the optional description.</summary>
    public string? Description { get; init; }
    /// <summary>Gets the declared MIME type.</summary>
    public required string MimeType { get; init; }
    /// <summary>Gets the associated-file relationship.</summary>
    public PdfAssociatedFileRelationship Relationship { get; init; }
    /// <summary>Gets an immutable copy of the decoded payload.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }
    /// <summary>Gets the payload size declared by the embedded-file parameters.</summary>
    public long? DeclaredSize { get; init; }
    /// <summary>Gets whether the declared size matches the decoded payload.</summary>
    public bool? SizeMatches { get; init; }
    /// <summary>Gets the optional embedded-file creation date.</summary>
    public DateTimeOffset? CreationDate { get; init; }
    /// <summary>Gets the optional embedded-file modification date.</summary>
    public DateTimeOffset? ModificationDate { get; init; }
    /// <summary>Gets the optional checksum declared by the embedded-file parameters.</summary>
    public ReadOnlyMemory<byte>? DeclaredChecksum { get; init; }
    /// <summary>Gets whether the declared checksum matches the decoded payload.</summary>
    public bool? ChecksumMatches { get; init; }
    /// <summary>Gets the source file-specification object number when indirect.</summary>
    public int? FileSpecificationObjectNumber { get; init; }
    /// <summary>Gets the source embedded-file stream object number when indirect.</summary>
    public int? EmbeddedFileObjectNumber { get; init; }
    /// <summary>Gets whether the supplied name is unsafe for filesystem extraction.</summary>
    public bool HasUnsafeFileName { get; init; }
    /// <summary>Gets whether the extension commonly identifies executable content.</summary>
    public bool IsPotentiallyExecutable { get; init; }
}
