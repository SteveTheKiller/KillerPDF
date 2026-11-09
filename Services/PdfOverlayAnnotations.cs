using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPDF.Services;

public sealed record OverlayWriteRecord(PageAnnotation Annotation, int RenderWidth, int RenderHeight,
    int Rotation, int AnnotationIndex, int ObjectNumber);
public sealed record OverlayRecord(PageAnnotation Annotation, int RenderWidth, int RenderHeight,
    int Rotation, int AnnotationIndex, int ObjectNumber);
internal sealed record OverlayPageBatch(int PageIndex, int RenderWidth, int RenderHeight, int Rotation,
    IReadOnlyList<PageAnnotation> Annotations, PdfContentStreamBuilder Content, string? Description);
public sealed record OverlayExtraction(byte[] Bytes, IReadOnlyList<OverlayRecord> Records);

public static class PdfOverlayAnnotations
{
    private const int MaximumPayload = 8 * 1024 * 1024;
    private static readonly PdfName MetadataName = Name("KillerPDFOverlay");
    private static readonly JsonSerializerOptions Json = new()
    {
        IgnoreReadOnlyProperties = true, MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static byte[] AppendPageBatch(PdfDocument document, int pageIndex,
        int renderWidth, int renderHeight, int rotation, IReadOnlyList<PageAnnotation> annotations,
        PdfContentStreamBuilder content, string? description) =>
        AppendPageBatches(document, [new(pageIndex, renderWidth, renderHeight, rotation, annotations, content, description)]);

    /// <summary>Appends every page's batch in one pass; one batch per page.</summary>
    internal static byte[] AppendPageBatches(PdfDocument document, IReadOnlyList<OverlayPageBatch> batches)
    {
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        return PdfEditablePageOverlayEditor.AppendMany(document, batches.Select(batch => BatchUnit(pages, batch)).ToArray());
    }

    private static PdfEditablePageOverlayUnit BatchUnit(IReadOnlyList<PdfPageInformation> pages, OverlayPageBatch batch)
    {
        (int pageIndex, int renderWidth, int renderHeight, int rotation, IReadOnlyList<PageAnnotation> annotations,
            PdfContentStreamBuilder content, string? description) = batch;
        ValidateCanvas(renderWidth, renderHeight, rotation);
        if (annotations.Count is 0 or > 10000) throw new ArgumentException("Invalid overlay batch size.");
        PdfPageInformation frame = pages[pageIndex];
        var identities = new HashSet<Guid>();
        var items = annotations.Select(annotation =>
        {
            ValidateModel(annotation);
            if (annotation.PageIndex != pageIndex || !identities.Add(annotation.HistoryId))
                throw new ArgumentException("Invalid overlay batch identity.");
            string kind = Kind(annotation);
            return new BatchItem(kind, annotation.HistoryId,
                JsonSerializer.SerializeToElement(annotation, ModelType(kind), Json));
        }).ToArray();
        var envelope = new BatchEnvelope(annotations.Any(IsSourcePdfImage) ? 2 : 1, renderWidth, renderHeight, rotation,
            frame.Left, frame.Bottom, frame.Width, frame.Height, frame.UserUnit, items);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        if (payload.Length > MaximumPayload) throw new ArgumentException("Overlay batch exceeds the payload limit.");
        using (JsonDocument check = JsonDocument.Parse(payload, new() { MaxDepth = 32 })) ValidateJson(check.RootElement);
        return new PdfEditablePageOverlayUnit(pageIndex, frame.Width, frame.Height, content, payload, description, rotation);
    }

    private static IReadOnlyList<PageBatch> ReadPageBatches(PdfDocument document)
    {
        var result = new List<PageBatch>();
        var overlays = PdfEditablePageOverlayEditor.Read(document);
        // Page geometry is read only for a document that has a page-content layer to check against it.
        if (overlays.Count == 0) return result;
        var pages = PdfPageInformation.Read(document);
        foreach (var overlay in overlays)
        {
            try
            {
                if (overlay.EditingData.Length > MaximumPayload) continue;
                using (JsonDocument check = JsonDocument.Parse(overlay.EditingData,
                    new() { MaxDepth = 32 })) ValidateJson(check.RootElement);
                BatchEnvelope envelope = JsonSerializer.Deserialize<BatchEnvelope>(overlay.EditingData.Span, Json)
                    ?? throw new InvalidOperationException("Missing overlay batch.");
                if (envelope.Version is not (1 or 2) || envelope.Items is not { Length: > 0 and <= 10000 }) continue;
                ValidateCanvas(envelope.RenderWidth, envelope.RenderHeight, envelope.Rotation);
                PdfPageInformation frame = pages[overlay.PageIndex];
                if (envelope.Rotation != frame.Rotation || envelope.Left != frame.Left
                    || envelope.Bottom != frame.Bottom || envelope.Width != frame.Width
                    || envelope.Height != frame.Height || envelope.UserUnit != frame.UserUnit) continue;
                var identities = new HashSet<Guid>();
                var records = new List<OverlayRecord>();
                foreach (BatchItem item in envelope.Items)
                {
                    if (item is null || item.HistoryId == Guid.Empty || !identities.Add(item.HistoryId))
                        throw new ArgumentException("Invalid overlay batch identity.");
                    PageAnnotation model = (PageAnnotation?)item.Model.Deserialize(ModelType(item.Kind), Json)
                        ?? throw new InvalidOperationException("Missing overlay model.");
                    ValidateShape(item.Model, JsonSerializer.SerializeToElement(model, ModelType(item.Kind), Json));
                    if (model.PageIndex != overlay.PageIndex) throw new ArgumentException("Invalid overlay page.");
                    model.HistoryId = item.HistoryId;
                    ValidateModel(model);
                    records.Add(new(model, envelope.RenderWidth, envelope.RenderHeight, envelope.Rotation,
                        -1, overlay.FormObjectNumber));
                }
                if (envelope.Version == 2 && !records.Any(record => IsSourcePdfImage(record.Annotation))) continue;
                result.Add(new(overlay, records.AsReadOnly()));
            }
            catch (Exception error) when (Unsupported(error)) { }
        }
        return result.AsReadOnly();
    }

    /// <summary>Attaches a bounded editing representation to explicitly identified new annotations.
    /// The supplied rotation is the intended final page rotation; extraction waits until it matches.</summary>
    public static byte[] Attach(PdfDocument document, IReadOnlyList<OverlayWriteRecord> records)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count is 0 or > 10000) throw new ArgumentException("Supply between 1 and 10000 new annotations.", nameof(records));
        var comments = PdfCommentReader.Read(document, true);
        var pages = PdfPageInformation.Read(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        var seen = new HashSet<int>();
        foreach (OverlayWriteRecord record in records)
        {
            if (record.Annotation is null || !seen.Add(record.ObjectNumber))
                throw new ArgumentException("Annotation identities must be unique.", nameof(records));
            PdfCommentInfo comment = comments.SingleOrDefault(c => c.PageIndex == record.Annotation.PageIndex
                && c.AnnotationIndex == record.AnnotationIndex && c.ObjectNumber == record.ObjectNumber)
                ?? throw new ArgumentException("The annotation identity does not match the document.", nameof(records));
            PdfDictionary annotation = document.Resolve(record.ObjectNumber) as PdfDictionary
                ?? throw new ArgumentException("The annotation must be indirect.", nameof(records));
            if (annotation.ContainsKey(MetadataName)) throw new ArgumentException("The annotation already carries overlay metadata.", nameof(records));
            string kind = Kind(record.Annotation);
            ValidateModel(record.Annotation);
            ValidateCanvas(record.RenderWidth, record.RenderHeight, record.Rotation);
            PdfPageInformation frame = pages[comment.PageIndex];
            ValidateNative(document, annotation, comment, record.Annotation);
            var envelope = new Envelope(kind, record.Annotation.HistoryId, record.RenderWidth,
                record.RenderHeight, record.Rotation, frame.Left, frame.Bottom, frame.Width,
                frame.Height, frame.UserUnit, Fingerprint(document, annotation),
                JsonSerializer.SerializeToElement(record.Annotation, ModelType(kind), Json));
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
            if (payload.Length > MaximumPayload) throw new ArgumentException("Overlay metadata exceeds the payload limit.", nameof(records));
            using (JsonDocument check = JsonDocument.Parse(payload, new() { MaxDepth = 32 })) ValidateJson(check.RootElement);
            var metadata = new PdfDictionary(new Dictionary<PdfName, PdfObject>
            {
                [Name("V")] = new PdfInteger(IsSourcePdfImage(record.Annotation) ? 2 : 1),
                [Name("Data")] = new PdfString(payload, PdfStringForm.Hexadecimal)
            });
            update.ReplaceObject(record.ObjectNumber, new PdfDictionary(annotation.Append(new(MetadataName, metadata))));
        }
        return update.Build();
    }

    /// <summary>Reads validated logical overlays without changing the native annotation stack.</summary>
    public static IReadOnlyList<OverlayRecord> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<OverlayRecord>();
        try
        {
            var pages = PdfPageInformation.Read(document);
            foreach (PdfCommentInfo comment in PdfCommentReader.Read(document, true))
                if (TryRead(document, comment, pages[comment.PageIndex]) is { } record) result.Add(record);
            foreach (PageBatch batch in ReadPageBatches(document)) result.AddRange(batch.Records);
        }
        catch (Exception error) when (Unsupported(error)) { return []; }
        return result.AsReadOnly();
    }

    /// <summary>As <see cref="TakeEditableTail(byte[])"/>, reading <paramref name="document"/>, a strict parse of
    /// <paramref name="sourceBytes"/> the caller already holds.</summary>
    public static OverlayExtraction TakeEditableTail(byte[] sourceBytes, PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        ArgumentNullException.ThrowIfNull(document);
        return TakeEditableTail(sourceBytes, () => document);
    }

    /// <summary>Moves only an intact owned tail into the editable layer, preserving all source bytes.</summary>
    public static OverlayExtraction TakeEditableTail(byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        return TakeEditableTail(sourceBytes, () => PdfDocument.Open(sourceBytes));
    }

    private static OverlayExtraction TakeEditableTail(byte[] sourceBytes, Func<PdfDocument> open)
    {
        try
        {
            PdfDocument document = open();
            // Extraction is restricted to unencrypted working copies.
            if (document.IsEncrypted) return new(sourceBytes, []);
            // Page geometry and annotation counts are read only when a page has a comment carrying overlay metadata:
            // a long PDF without KillerPDF overlays read every page's geometry for nothing.
            IReadOnlyList<PdfPageInformation>? pages = null;
            var comments = PdfCommentReader.Read(document, true).ToLookup(comment => comment.PageIndex);
            var extracted = new List<OverlayRecord>();
            var editor = new PdfIncrementalAnnotationEditor(document);
            // A tail starts at a page's topmost annotation and holds only annotations that carry overlay metadata,
            // so a page with no such comment has none. Every page's count comes from one page-tree read: counted
            // a page at a time, a long PDF read the whole tree once for every page at open.
            IReadOnlyList<int>? counts = null;
            foreach (var pageComments in comments.Where(group => group.Any(comment => CarriesOverlayMetadata(document, comment)))
                .OrderBy(group => group.Key))
            {
                int page = pageComments.Key;
                var stack = pageComments.ToDictionary(c => c.AnnotationIndex);
                pages ??= PdfPageInformation.Read(document);
                counts ??= PdfCommentReader.ReadPageAnnotationCounts(document);
                int count = counts[page];
                var tail = new List<OverlayRecord>();
                bool invalid = false;
                for (int index = count - 1; index >= 0; index--)
                {
                    if (!stack.TryGetValue(index, out PdfCommentInfo? comment)) break;
                    if (comment.ObjectNumber is not int number || document.Resolve(number) is not PdfDictionary annotation
                        || !annotation.ContainsKey(MetadataName)) break;
                    if (TryRead(document, comment, pages[page]) is not { } record) { invalid = true; break; }
                    tail.Add(record);
                }
                if (invalid) continue;
                foreach (OverlayRecord record in tail) editor.RemoveAnnotationAt(page, record.AnnotationIndex);
                extracted.AddRange(tail.AsEnumerable().Reverse());
            }
            byte[] bytes = extracted.Count == 0 ? sourceBytes : editor.Build();
            // Nothing taken leaves the bytes as they were, so their parse is the one already read.
            PdfDocument remaining = extracted.Count == 0 ? document : PdfDocument.Open(bytes);
            // A page-content layer cannot be promoted above a remaining native annotation.
            var batchCandidates = ReadPageBatches(remaining);
            IReadOnlyList<int> remainingCounts = batchCandidates.Count == 0 ? [] : PdfCommentReader.ReadPageAnnotationCounts(remaining);
            var batches = batchCandidates.Where(batch => remainingCounts[batch.Overlay.PageIndex] == 0).ToArray();
            if (batches.Length > 0)
            {
                bytes = PdfEditablePageOverlayEditor.Remove(remaining, batches.Select(batch => batch.Overlay).ToArray());
                extracted.InsertRange(0, batches.SelectMany(batch => batch.Records));
            }
            return extracted.Count == 0 ? new(sourceBytes, []) : new(bytes, extracted.AsReadOnly());
        }
        catch (Exception error) when (Unsupported(error)) { return new(sourceBytes, []); }
    }

    private static bool CarriesOverlayMetadata(PdfDocument document, PdfCommentInfo comment) =>
        comment.ObjectNumber is int number && document.Resolve(number) is PdfDictionary annotation
        && annotation.ContainsKey(MetadataName);

    private static OverlayRecord? TryRead(PdfDocument document, PdfCommentInfo comment, PdfPageInformation frame)
    {
        try
        {
            if (comment.ObjectNumber is not int number || document.Resolve(number) is not PdfDictionary annotation
                || !annotation.TryGetValue(MetadataName, out PdfObject? metadataValue)
                || metadataValue is not PdfDictionary metadata || metadata.Count != 2
                || !metadata.TryGetValue(Name("V"), out PdfObject? version) || version is not PdfInteger { Value: 1 or 2 } schema
                || !metadata.TryGetValue(Name("Data"), out PdfObject? value) || value is not PdfString data
                || data.Bytes.Length > MaximumPayload) return null;
            using (JsonDocument check = JsonDocument.Parse(data.Bytes, new() { MaxDepth = 32 })) ValidateJson(check.RootElement);
            Envelope envelope = JsonSerializer.Deserialize<Envelope>(data.Bytes.Span, Json)
                ?? throw new InvalidOperationException("Missing overlay payload.");
            ValidateCanvas(envelope.RenderWidth, envelope.RenderHeight, envelope.Rotation);
            if (envelope.HistoryId == Guid.Empty || envelope.Rotation != frame.Rotation
                || envelope.Left != frame.Left || envelope.Bottom != frame.Bottom
                || envelope.Width != frame.Width || envelope.Height != frame.Height
                || envelope.UserUnit != frame.UserUnit
                || envelope.Fingerprint != Fingerprint(document, annotation)) return null;
            PageAnnotation model = (PageAnnotation?)envelope.Model.Deserialize(ModelType(envelope.Kind), Json)
                ?? throw new InvalidOperationException("Missing overlay model.");
            ValidateShape(envelope.Model, JsonSerializer.SerializeToElement(model, ModelType(envelope.Kind), Json));
            if (model.PageIndex != comment.PageIndex) return null;
            model.HistoryId = envelope.HistoryId;
            ValidateModel(model);
            if (schema.Value == 2 && !IsSourcePdfImage(model)) return null;
            ValidateNative(document, annotation, comment, model);
            return new(model, envelope.RenderWidth, envelope.RenderHeight, envelope.Rotation,
                comment.AnnotationIndex, number);
        }
        catch (Exception error) when (Unsupported(error)) { return null; }
    }

    private static bool Unsupported(Exception error) => error is InvalidOperationException or ArgumentException
        or JsonException or FormatException or NotSupportedException or OverflowException or IOException;

    private static void ValidateNative(PdfDocument document, PdfDictionary annotation, PdfCommentInfo comment, PageAnnotation model)
    {
        if (comment.Subtype != (model is TextAnnotation ? "FreeText" : "Stamp")
            || model is TextAnnotation text && comment.Contents != (ReplacementLayers.IsVisible(text) ? text.Content : string.Empty)
            || !annotation.TryGetValue(Name("AP"), out PdfObject? appearances)
            || Resolve(document, appearances) is not PdfDictionary ap
            || !ap.TryGetValue(Name("N"), out PdfObject? normal)
            || Resolve(document, normal) is not PdfStream)
            throw new InvalidOperationException("The native annotation does not match the editing representation.");
    }

    private static string Kind(PageAnnotation value) => value.GetType() == typeof(TextAnnotation) ? "Text"
        : value.GetType() == typeof(CoverAnnotation) ? "Cover"
        : value.GetType() == typeof(HighlightAnnotation) ? "Highlight"
        : value.GetType() == typeof(InkAnnotation) ? "Ink"
        : value.GetType() == typeof(SignatureAnnotation) ? "Signature"
        : value.GetType() == typeof(ImageAnnotation) ? "Image"
        : throw new NotSupportedException("Unsupported overlay kind.");

    private static Type ModelType(string kind) => kind switch
    {
        "Text" => typeof(TextAnnotation), "Cover" => typeof(CoverAnnotation),
        "Highlight" => typeof(HighlightAnnotation), "Ink" => typeof(InkAnnotation),
        "Signature" => typeof(SignatureAnnotation), "Image" => typeof(ImageAnnotation),
        _ => throw new NotSupportedException("Unsupported overlay kind.")
    };

    private static void ValidateCanvas(int width, int height, int rotation)
    {
        if (width is < 1 or > 100000 || height is < 1 or > 100000 || rotation is not (0 or 90 or 180 or 270))
            throw new ArgumentException("Invalid overlay canvas.");
    }

    private static void ValidateModel(PageAnnotation model)
    {
        _ = Kind(model);
        if (model.PageIndex < 0 || model.HistoryId == Guid.Empty || model.PairId is null || model.GroupId is null
            || model.PairId.Length > 1024 || model.GroupId.Length > 1024) throw new ArgumentException("Invalid overlay identity.");
        switch (model)
        {
            case TextAnnotation text:
                Point(text.Position); Positive(text.Width); Positive(text.Height); Positive(text.FontSize);
                Number(text.LetterSpacing); Nonnegative(text.BlurRadius);
                if (text.Content is null || text.Content.Length > 1_000_000 || string.IsNullOrWhiteSpace(text.FontName)
                    || text.FontName.Length > 1024 || text.Rotation is not (0 or 90 or 180 or 270)) throw new ArgumentException("Invalid text overlay.");
                break;
            case HighlightAnnotation highlight:
                if (highlight.Bounds.IsEmpty) throw new ArgumentException("Invalid highlight bounds.");
                Number(highlight.Bounds.X); Number(highlight.Bounds.Y); Positive(highlight.Bounds.Width); Positive(highlight.Bounds.Height);
                if (!Enum.IsDefined(highlight.Style) || highlight is CoverAnnotation && (highlight.ColorA != 255 || highlight.Style != HighlightStyle.Fill))
                    throw new ArgumentException("Invalid highlight style.");
                if (highlight.Erases is { } erases)
                {
                    if (erases.Count > 10000) throw new ArgumentException("Too many erase strokes.");
                    foreach (HighlightErase erase in erases) { if (erase is null) throw new ArgumentException("Missing erase stroke."); Positive(erase.Radius); Points(erase.Points); }
                }
                break;
            case InkAnnotation ink: Positive(ink.StrokeWidth); Points(ink.Points); break;
            case PlacedAnnotation placed:
                Point(placed.Position); Positive(placed.Scale); Positive(placed.SourceWidth); Positive(placed.SourceHeight);
                if (placed is ImageAnnotation image) Image(image.ImageData);
                if (placed is SignatureAnnotation signature)
                {
                    Positive(signature.StrokeWidth);
                    if (signature.FieldObjectNumber is <= 0 || signature.Strokes is null || signature.Strokes.Count > 10000) throw new ArgumentException("Invalid signature.");
                    foreach (var stroke in signature.Strokes) Points(stroke);
                    if (signature.ImageData is not null) Image(signature.ImageData);
                    if (signature.Vectors is { } vectors)
                    {
                        if (vectors.Count > 10000) throw new ArgumentException("Too many signature vectors.");
                        foreach (SignatureVector vector in vectors)
                        {
                            if (vector is null || vector.Data is null || vector.Data.Length > 1_000_000 || vector.LineCap is < 0 or > 2 || vector.LineJoin is < 0 or > 2) throw new ArgumentException("Invalid signature vector.");
                            Nonnegative(vector.StrokeWidth);
                        }
                    }
                }
                break;
        }
    }

    private static void Point(Point point) { Number(point.X); Number(point.Y); }
    private static void Points(IReadOnlyList<Point>? points)
    {
        if (points is null || points.Count > 100000) throw new ArgumentException("Invalid overlay points.");
        foreach (Point point in points) Point(point);
    }
    private static void Number(double value) { if (!double.IsFinite(value) || Math.Abs(value) > 10_000_000) throw new ArgumentException("Invalid overlay number."); }
    private static void Nonnegative(double value) { Number(value); if (value < 0) throw new ArgumentException("Negative overlay size."); }
    private static void Positive(double value) { Number(value); if (value <= 0) throw new ArgumentException("Nonpositive overlay size."); }
    private static void Image(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 7 * 1024 * 1024) throw new ArgumentException("Invalid overlay image.");
        _ = Convert.FromBase64String(value);
    }

    private static bool IsSourcePdfImage(PageAnnotation model) => model is ImageAnnotation image
        && Convert.FromBase64String(image.ImageData).AsSpan().StartsWith("%PDF-"u8);

    private static void ValidateJson(JsonElement root)
    {
        int nodes = 0;
        Visit(root);
        void Visit(JsonElement element)
        {
            if (++nodes > 300000) throw new ArgumentException("Overlay metadata contains too many values.");
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (property.Name.Length > 128 || !names.Add(property.Name)) throw new ArgumentException("Invalid overlay property.");
                        Visit(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    if (element.GetArrayLength() > 100000) throw new ArgumentException("Overlay collection exceeds the limit.");
                    foreach (JsonElement item in element.EnumerateArray()) Visit(item);
                    break;
                case JsonValueKind.Number:
                    if (!element.TryGetDouble(out double value) || !double.IsFinite(value)) throw new ArgumentException("Invalid overlay number.");
                    break;
                case JsonValueKind.String:
                    if (element.GetString()!.Length > 7 * 1024 * 1024) throw new ArgumentException("Overlay string exceeds the limit.");
                    break;
            }
        }
    }

    private static void ValidateShape(JsonElement source, JsonElement normalized)
    {
        if (source.ValueKind != normalized.ValueKind) throw new ArgumentException("Invalid overlay value shape.");
        if (source.ValueKind == JsonValueKind.Object)
        {
            JsonProperty[] properties = source.EnumerateObject().ToArray();
            if (properties.Length != normalized.EnumerateObject().Count()) throw new ArgumentException("Missing overlay properties.");
            foreach (JsonProperty property in normalized.EnumerateObject())
            {
                if (!source.TryGetProperty(property.Name, out JsonElement value)) throw new ArgumentException("Missing overlay property.");
                ValidateShape(value, property.Value);
            }
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            if (source.GetArrayLength() != normalized.GetArrayLength()) throw new ArgumentException("Invalid overlay collection shape.");
            for (int index = 0; index < source.GetArrayLength(); index++) ValidateShape(source[index], normalized[index]);
        }
        else if (source.ValueKind == JsonValueKind.Number && source.GetDouble() != normalized.GetDouble()
            || source.ValueKind == JsonValueKind.String && source.GetString() != normalized.GetString())
            throw new ArgumentException("Conflicting overlay property values.");
    }

    private static string Fingerprint(PdfDocument document, PdfDictionary annotation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var references = new Dictionary<(int, int), int>();
        int nodes = 0, bytes = 0;
        Write(annotation, 0, true);
        return Convert.ToHexString(hash.GetHashAndReset());
        void Add(ReadOnlySpan<byte> data)
        {
            bytes = checked(bytes + data.Length);
            if (bytes > 32 * 1024 * 1024) throw new NotSupportedException("Overlay appearance graph exceeds the byte limit.");
            hash.AppendData(data);
        }
        void Token(string value)
        {
            byte[] data = Encoding.UTF8.GetBytes(value);
            Add(Encoding.ASCII.GetBytes(data.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            Add(data);
        }
        void Write(PdfObject value, int depth, bool outer = false)
        {
            if (++nodes > 10000 || depth > 32) throw new NotSupportedException("Overlay appearance graph exceeds the structural limit.");
            if (value is PdfIndirectReference reference)
            {
                var key = (reference.ObjectNumber, reference.Generation);
                if (references.TryGetValue(key, out int seen)) { Token("ref" + seen.ToString(CultureInfo.InvariantCulture)); return; }
                references.Add(key, references.Count);
                PdfObject resolved = document.Resolve(reference);
                if (resolved is PdfNull) throw new NotSupportedException("Missing appearance reference.");
                Token("object"); Write(resolved, depth + 1); return;
            }
            switch (value)
            {
                case PdfDictionary dictionary:
                    Token("dictionary");
                    foreach (var pair in dictionary.OrderBy(p => p.Key.ValueAsLatin1(), StringComparer.Ordinal))
                    {
                        if (outer && (pair.Key.Equals(MetadataName) || pair.Key.Equals(Name("P")))) continue;
                        Token(pair.Key.ValueAsLatin1()); Write(pair.Value, depth + 1);
                    }
                    Token("end"); break;
                case PdfStream stream:
                    Token("stream"); Write(stream.Dictionary, depth + 1); Token(stream.EncodedData.Length.ToString(CultureInfo.InvariantCulture)); Add(stream.EncodedData.Span); break;
                case PdfArray array:
                    Token("array"); foreach (PdfObject item in array) Write(item, depth + 1); Token("end"); break;
                case PdfName name: Token("name"); Token(name.ValueAsLatin1()); break;
                case PdfString text: Token("string"); Token(text.Bytes.Length.ToString(CultureInfo.InvariantCulture)); Add(text.Bytes.Span); break;
                case PdfInteger integer: Token("integer" + integer.Value.ToString(CultureInfo.InvariantCulture)); break;
                case PdfReal real: Token("real" + real.Value.ToString("R", CultureInfo.InvariantCulture)); break;
                case PdfBoolean boolean: Token(boolean.Value ? "true" : "false"); break;
                case PdfNull: Token("null"); break;
                default: throw new NotSupportedException("Unsupported appearance value.");
            }
        }
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var seen = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (seen.Count >= 32 || !seen.Add((reference.ObjectNumber, reference.Generation))) throw new InvalidOperationException("Invalid overlay reference chain.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private sealed record Envelope(string Kind, Guid HistoryId, int RenderWidth, int RenderHeight,
        int Rotation, double Left, double Bottom, double Width, double Height, double UserUnit,
        string Fingerprint, JsonElement Model);
    private sealed record BatchItem(string Kind, Guid HistoryId, JsonElement Model);
    private sealed record BatchEnvelope(int Version, int RenderWidth, int RenderHeight, int Rotation,
        double Left, double Bottom, double Width, double Height, double UserUnit, BatchItem[] Items);
    private sealed record PageBatch(PdfEditablePageOverlay Overlay, IReadOnlyList<OverlayRecord> Records);
}
