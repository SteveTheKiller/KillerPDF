using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads document-level embedded files without opening or executing them.</summary>
public static class PdfAttachmentReader
{
    /// <summary>Formats attachment metadata and page placements without payload data.</summary>
    public static string ToText(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<PdfAttachmentInfo> attachments = Read(document);
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        PdfAttachmentAnnotationInfo[] annotations = [.. Enumerable.Range(0, pageCount)
            .SelectMany(pageIndex => ReadPageAnnotations(document, pageIndex))];
        var text = new StringBuilder();
        text.AppendLine($"Attachments: {attachments.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (PdfAttachmentInfo attachment in attachments)
            AppendAttachment(text, attachment, "  ");
        text.AppendLine($"Page placements: {annotations.Length.ToString(CultureInfo.InvariantCulture)}");
        foreach (PdfAttachmentAnnotationInfo annotation in annotations)
        {
            text.Append("  Page ").Append((annotation.PageIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(", annotation ").Append((annotation.AnnotationIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(": ").Append(Quoted(annotation.Attachment.FileName))
                .Append(" at ").Append(Format(annotation.Left)).Append(", ").Append(Format(annotation.Bottom))
                .Append(" to ").Append(Format(annotation.Right)).Append(", ").Append(Format(annotation.Top));
            if (!string.IsNullOrWhiteSpace(annotation.Icon))
                text.Append(", icon ").Append(Quoted(annotation.Icon));
            if (annotation.ObjectNumber.HasValue)
                text.Append(", object ").Append(annotation.ObjectNumber.Value.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
            if (!string.IsNullOrWhiteSpace(annotation.Contents))
                text.Append("    Description: ").AppendLine(Quoted(annotation.Contents));
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Exports attachment metadata and page placements without payload data.</summary>
    public static string ToJson(PdfDocument document, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<PdfAttachmentInfo> attachments = Read(document);
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        PdfAttachmentAnnotationInfo[] annotations = [.. Enumerable.Range(0, pageCount)
            .SelectMany(pageIndex => ReadPageAnnotations(document, pageIndex))];
        object Describe(PdfAttachmentInfo attachment) => new
        {
            attachment.FileName,
            attachment.Description,
            attachment.MimeType,
            attachment.Relationship,
            ByteCount = attachment.Data.Length,
            attachment.DeclaredSize,
            attachment.SizeMatches,
            attachment.CreationDate,
            attachment.ModificationDate,
            attachment.ChecksumMatches,
            attachment.CollectionValues,
            attachment.FileSpecificationObjectNumber,
            attachment.EmbeddedFileObjectNumber,
            attachment.HasUnsafeFileName,
            attachment.IsPotentiallyExecutable,
            attachment.HasExecutableContent,
            attachment.HasEncryptedContent
        };
        return JsonSerializer.Serialize(new
        {
            Version = 1,
            Attachments = attachments.Select(Describe),
            PageAnnotations = annotations.Select(annotation => new
            {
                annotation.PageIndex,
                annotation.AnnotationIndex,
                annotation.ObjectNumber,
                annotation.Left,
                annotation.Bottom,
                annotation.Right,
                annotation.Top,
                annotation.Icon,
                annotation.Contents,
                Attachment = Describe(annotation.Attachment)
            })
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        });
    }

    private static void AppendAttachment(
        StringBuilder text, PdfAttachmentInfo attachment, string indent)
    {
        text.Append(indent).Append(Quoted(attachment.FileName)).Append(": ")
            .Append(attachment.Data.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes, ")
            .Append(attachment.MimeType).Append(", relationship ").Append(attachment.Relationship);
        if (attachment.FileSpecificationObjectNumber.HasValue)
            text.Append(", file object ").Append(attachment.FileSpecificationObjectNumber.Value.ToString(CultureInfo.InvariantCulture));
        if (attachment.EmbeddedFileObjectNumber.HasValue)
            text.Append(", stream object ").Append(attachment.EmbeddedFileObjectNumber.Value.ToString(CultureInfo.InvariantCulture));
        text.AppendLine();
        if (!string.IsNullOrWhiteSpace(attachment.Description))
            text.Append(indent).Append("  Description: ").AppendLine(Quoted(attachment.Description));
        if (attachment.CreationDate.HasValue)
            text.Append(indent).Append("  Created: ").AppendLine(attachment.CreationDate.Value.ToString("O", CultureInfo.InvariantCulture));
        if (attachment.ModificationDate.HasValue)
            text.Append(indent).Append("  Modified: ").AppendLine(attachment.ModificationDate.Value.ToString("O", CultureInfo.InvariantCulture));
        if (attachment.DeclaredSize.HasValue)
            text.Append(indent).Append("  Declared size: ")
                .Append(attachment.DeclaredSize.Value.ToString(CultureInfo.InvariantCulture))
                .Append(" bytes (").Append(attachment.SizeMatches == true ? "matches" : "does not match").AppendLine(")");
        if (attachment.ChecksumMatches.HasValue)
            text.Append(indent).Append("  Checksum: ")
                .AppendLine(attachment.ChecksumMatches == true ? "matches" : "does not match");
        foreach (PdfCollectionItemValue value in attachment.CollectionValues)
        {
            string displayed = value.Text ?? value.Number?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty;
            text.Append(indent).Append("  Collection ").Append(value.Key).Append(": ")
                .AppendLine(Quoted((value.Prefix ?? string.Empty) + displayed));
        }
        var findings = new List<string>();
        if (attachment.HasUnsafeFileName) findings.Add("unsafe filename");
        if (attachment.IsPotentiallyExecutable) findings.Add("executable extension");
        if (attachment.HasExecutableContent) findings.Add("executable content");
        if (attachment.HasEncryptedContent) findings.Add("encrypted content");
        text.Append(indent).Append("  Safety: ")
            .AppendLine(findings.Count == 0 ? "no findings" : string.Join(", ", findings));
    }

    private static string Format(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static string Quoted(string value) =>
        $"\"{value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)}\"";

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
            result.Add(ReadFileSpecification(document, entry.Value, treeName));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Reads file-attachment annotations and their embedded-file metadata on one page.</summary>
    public static IReadOnlyList<PdfAttachmentAnnotationInfo> ReadPageAnnotations(
        PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageTreeEntry page = tree.Pages[pageIndex];
        if (!page.Dictionary.TryGetValue(Name("Annots"), out PdfObject? annotationsValue)) return [];
        PdfArray annotations = Resolve(document, annotationsValue) as PdfArray
            ?? throw new InvalidOperationException("A page /Annots value is not an array.");
        Dictionary<int, PdfAttachmentInfo> attachments = Read(document)
            .Where(item => item.FileSpecificationObjectNumber.HasValue)
            .ToDictionary(item => item.FileSpecificationObjectNumber!.Value);
        var result = new List<PdfAttachmentAnnotationInfo>();
        for (int index = 0; index < annotations.Count; index++)
        {
            PdfObject annotationValue = annotations[index];
            PdfDictionary? annotation = Resolve(document, annotationValue) as PdfDictionary;
            if (annotation is null
                || !annotation.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
                || Resolve(document, subtypeValue) is not PdfName subtype
                || subtype.ValueAsLatin1() != "FileAttachment") continue;
            PdfArray rectangle = Value<PdfArray>(document, annotation, "Rect",
                "A file-attachment annotation has no /Rect array.");
            if (rectangle.Count != 4)
                throw new InvalidOperationException(
                    "A file-attachment annotation /Rect array must contain four numbers.");
            PdfObject fileValue = annotation.TryGetValue(Name("FS"), out PdfObject? suppliedFile)
                ? suppliedFile : throw new InvalidOperationException(
                    "A file-attachment annotation has no /FS value.");
            PdfIndirectReference? fileReference = fileValue as PdfIndirectReference;
            PdfAttachmentInfo attachment = fileReference is not null
                && attachments.TryGetValue(fileReference.ObjectNumber, out PdfAttachmentInfo? found)
                    ? found : ReadFileSpecification(document, fileValue, null);
            double x1 = Number(document, rectangle[0]), y1 = Number(document, rectangle[1]);
            double x2 = Number(document, rectangle[2]), y2 = Number(document, rectangle[3]);
            result.Add(new PdfAttachmentAnnotationInfo
            {
                PageIndex = pageIndex,
                AnnotationIndex = index,
                ObjectNumber = (annotationValue as PdfIndirectReference)?.ObjectNumber,
                Left = Math.Min(x1, x2), Bottom = Math.Min(y1, y2),
                Right = Math.Max(x1, x2), Top = Math.Max(y1, y2),
                Icon = annotation.TryGetValue(Name("Name"), out PdfObject? iconValue)
                    && Resolve(document, iconValue) is PdfName icon ? icon.ValueAsLatin1() : null,
                Contents = Text(document, annotation, "Contents"),
                Attachment = attachment
            });
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static PdfAttachmentInfo ReadFileSpecification(
        PdfDocument document, PdfObject specificationValue, string? fallbackName)
    {
        PdfIndirectReference? specificationReference = specificationValue as PdfIndirectReference;
        PdfDictionary specification = Resolve(document, specificationValue) as PdfDictionary
            ?? throw new InvalidOperationException(
                "An embedded file does not reference a file specification.");
        string fileName = Text(document, specification, "UF")
            ?? Text(document, specification, "F") ?? fallbackName
            ?? throw new InvalidOperationException("An embedded file has no file name.");
        PdfDictionary embedded = Value<PdfDictionary>(document, specification, "EF",
            "An embedded file specification has no /EF dictionary.");
        PdfObject streamValue = embedded.TryGetValue(Name("UF"), out PdfObject? unicodeStream)
            ? unicodeStream : embedded.TryGetValue(Name("F"), out PdfObject? regularStream)
                ? regularStream : throw new InvalidOperationException(
                    "An embedded file specification has no payload stream.");
        PdfIndirectReference? streamReference = streamValue as PdfIndirectReference;
        PdfStream stream = Resolve(document, streamValue) as PdfStream
            ?? throw new InvalidOperationException("An embedded file payload is not a stream.");
        string mimeType = stream.Dictionary.TryGetValue(Name("Subtype"), out PdfObject? subtype)
            && Resolve(document, subtype) is PdfName mime ? mime.ValueAsLatin1()
            : "application/octet-stream";
        string? relationshipName = specification.TryGetValue(
            Name("AFRelationship"), out PdfObject? relationshipValue)
            && Resolve(document, relationshipValue) is PdfName relationship
                ? relationship.ValueAsLatin1() : null;
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
        byte[]? declaredChecksum = OptionalBytes(document, parameters, "CheckSum");
        return new PdfAttachmentInfo
        {
            FileName = fileName,
            Description = Text(document, specification, "Desc"),
            MimeType = mimeType,
            Relationship = relationshipKind,
            Data = data,
            DeclaredSize = declaredSize,
            SizeMatches = declaredSize.HasValue ? declaredSize.Value == data.LongLength : null,
            CreationDate = OptionalDate(document, parameters, "CreationDate"),
            ModificationDate = OptionalDate(document, parameters, "ModDate"),
            DeclaredChecksum = declaredChecksum,
            ChecksumMatches = declaredChecksum is not null
                ? CryptographicOperations.FixedTimeEquals(
                    declaredChecksum, MD5.HashData(data)) : null,
            CollectionValues = ReadCollectionValues(document, specification),
            FileSpecificationObjectNumber = specificationReference?.ObjectNumber,
            EmbeddedFileObjectNumber = streamReference?.ObjectNumber,
            HasUnsafeFileName = !IsSafeFileName(fileName),
            IsPotentiallyExecutable = IsPotentiallyExecutable(fileName),
            HasExecutableContent = HasExecutableContent(data),
            HasEncryptedContent = HasEncryptedContent(data)
        };
    }

    private static IReadOnlyList<PdfCollectionItemValue> ReadCollectionValues(
        PdfDocument document, PdfDictionary specification)
    {
        if (!specification.TryGetValue(Name("CI"), out PdfObject? itemValue)) return [];
        if (Resolve(document, itemValue) is not PdfDictionary item)
            throw new InvalidOperationException(
                "A file specification /CI value is not a dictionary.");
        var values = new List<PdfCollectionItemValue>();
        foreach ((PdfName key, PdfObject storedValue) in item)
        {
            PdfObject value = Resolve(document, storedValue);
            string? prefix = null;
            if (value is PdfDictionary subitem)
            {
                prefix = Text(document, subitem, "P");
                if (!subitem.TryGetValue(Name("D"), out PdfObject? dataValue))
                    throw new InvalidOperationException(
                        $"A collection item /{key.ValueAsLatin1()} has no /D value.");
                value = Resolve(document, dataValue);
            }
            string name = key.ValueAsLatin1();
            values.Add(value switch
            {
                PdfString text => new PdfCollectionItemValue(name,
                    PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span,
                        $"A collection item /{name} value"), null, prefix),
                PdfInteger integer => new PdfCollectionItemValue(
                    name, null, integer.Value, prefix),
                PdfReal real => new PdfCollectionItemValue(
                    name, null, real.Value, prefix),
                _ => throw new InvalidOperationException(
                    $"A collection item /{name} value is not a string or number.")
            });
        }
        return Array.AsReadOnly(values.OrderBy(value => value.Key,
            StringComparer.Ordinal).ToArray());
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

    /// <summary>Extracts an attachment inside the selected directory without overwriting by default.</summary>
    public static string Extract(
        PdfAttachmentInfo attachment, string directory, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        string path = GetSafeExtractionPath(directory, attachment.FileName);
        using var destination = new FileStream(path,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        destination.Write(attachment.Data.Span);
        return path;
    }

    /// <summary>Preflights and extracts an attachment batch without partial validation failures.</summary>
    public static IReadOnlyList<string> ExtractAll(
        IEnumerable<PdfAttachmentInfo> attachments,
        string directory, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        PdfAttachmentInfo[] values = attachments.Select(attachment => attachment
            ?? throw new ArgumentException(
                "An attachment extraction item cannot be null.", nameof(attachments)))
            .ToArray();
        string[] paths = [.. values.Select(attachment =>
            GetSafeExtractionPath(directory, attachment.FileName))];
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new ArgumentException(
                "Attachment extraction destinations must be unique.", nameof(attachments));
        if (!overwrite && paths.Any(File.Exists))
            throw new IOException(
                "An attachment extraction destination already exists.");
        for (int index = 0; index < values.Length; index++)
        {
            using var destination = new FileStream(paths[index],
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            destination.Write(values[index].Data.Span);
        }
        return Array.AsReadOnly(paths);
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

    private static bool HasExecutableContent(ReadOnlySpan<byte> data) =>
        data.StartsWith("MZ"u8)
        || data.StartsWith(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' })
        || data.StartsWith(new byte[] { 0xfe, 0xed, 0xfa, 0xce })
        || data.StartsWith(new byte[] { 0xfe, 0xed, 0xfa, 0xcf })
        || data.StartsWith(new byte[] { 0xca, 0xfe, 0xba, 0xbe });

    private static bool HasEncryptedContent(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("%PDF-"u8))
        {
            try
            {
                return !PdfDocument.Open(data.ToArray()).IsDecrypted;
            }
            catch (Exception error) when (error is FormatException
                or InvalidOperationException or NotSupportedException)
            {
                return false;
            }
        }
        return data.Length >= 8
            && data[0] == (byte)'P' && data[1] == (byte)'K'
            && data[2] == 3 && data[3] == 4
            && (data[6] & 1) != 0;
    }

    private static T Value<T>(PdfDocument document, PdfDictionary dictionary, string key, string error)
        where T : PdfObject => dictionary.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(document, value) is T typed ? typed : throw new InvalidOperationException(error);

    private static double Number(PdfDocument document, PdfObject value) =>
        Resolve(document, value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => throw new InvalidOperationException(
                "A file-attachment annotation rectangle coordinate is not numeric.")
        };

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
    /// <summary>Gets the portfolio collection values assigned to this file.</summary>
    public required IReadOnlyList<PdfCollectionItemValue> CollectionValues { get; init; }
    /// <summary>Gets the source file-specification object number when indirect.</summary>
    public int? FileSpecificationObjectNumber { get; init; }
    /// <summary>Gets the source embedded-file stream object number when indirect.</summary>
    public int? EmbeddedFileObjectNumber { get; init; }
    /// <summary>Gets whether the supplied name is unsafe for filesystem extraction.</summary>
    public bool HasUnsafeFileName { get; init; }
    /// <summary>Gets whether the extension commonly identifies executable content.</summary>
    public bool IsPotentiallyExecutable { get; init; }
    /// <summary>Gets whether the payload begins with a recognized executable-file signature.</summary>
    public bool HasExecutableContent { get; init; }
    /// <summary>Gets whether a PDF or ZIP-family payload declares encryption.</summary>
    public bool HasEncryptedContent { get; init; }
}

/// <summary>A page placement that exposes a registered embedded file.</summary>
public sealed record PdfAttachmentAnnotationInfo
{
    /// <summary>Gets the zero-based containing page.</summary>
    public required int PageIndex { get; init; }
    /// <summary>Gets the annotation's index in the page annotation array.</summary>
    public required int AnnotationIndex { get; init; }
    /// <summary>Gets the source annotation object number when indirect.</summary>
    public int? ObjectNumber { get; init; }
    /// <summary>Gets the normalized left coordinate.</summary>
    public required double Left { get; init; }
    /// <summary>Gets the normalized bottom coordinate.</summary>
    public required double Bottom { get; init; }
    /// <summary>Gets the normalized right coordinate.</summary>
    public required double Right { get; init; }
    /// <summary>Gets the normalized top coordinate.</summary>
    public required double Top { get; init; }
    /// <summary>Gets the optional standard or custom icon name.</summary>
    public string? Icon { get; init; }
    /// <summary>Gets the optional user-facing annotation description.</summary>
    public string? Contents { get; init; }
    /// <summary>Gets the referenced embedded file and its safety metadata.</summary>
    public required PdfAttachmentInfo Attachment { get; init; }
}

/// <summary>One text or numeric value assigned to a portfolio file.</summary>
public sealed record PdfCollectionItemValue(
    string Key, string? Text, double? Number, string? Prefix);
