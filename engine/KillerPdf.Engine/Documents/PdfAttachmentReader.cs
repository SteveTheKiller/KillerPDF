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
            result.Add(new PdfAttachmentInfo
            {
                FileName = fileName,
                Description = Text(document, specification, "Desc"),
                MimeType = mimeType,
                Relationship = relationshipKind,
                Data = data,
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
    /// <summary>Gets the source file-specification object number when indirect.</summary>
    public int? FileSpecificationObjectNumber { get; init; }
    /// <summary>Gets the source embedded-file stream object number when indirect.</summary>
    public int? EmbeddedFileObjectNumber { get; init; }
    /// <summary>Gets whether the supplied name is unsafe for filesystem extraction.</summary>
    public bool HasUnsafeFileName { get; init; }
    /// <summary>Gets whether the extension commonly identifies executable content.</summary>
    public bool IsPotentiallyExecutable { get; init; }
}
