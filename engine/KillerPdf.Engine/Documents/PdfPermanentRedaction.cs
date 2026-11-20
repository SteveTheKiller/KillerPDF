using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Documents;

/// <summary>A page image whose sensitive regions were already painted into the pixels.</summary>
public sealed record PdfSanitizedRasterPage
{
    /// <summary>Creates a validated sanitized page.</summary>
    public PdfSanitizedRasterPage(double width, double height, PdfImage image)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    /// <summary>Gets the output page width in PDF points.</summary>
    public double Width { get; }
    /// <summary>Gets the output page height in PDF points.</summary>
    public double Height { get; }
    /// <summary>Gets the complete sanitized page image.</summary>
    public PdfImage Image { get; }
}

/// <summary>A stable verification failure for a rebuilt redacted document.</summary>
public sealed record PdfRedactionVerificationFinding(
    string Code, string Message, int? PageIndex = null, int? ObjectNumber = null);

/// <summary>The result of verifying a clean-raster redaction output.</summary>
public sealed partial record PdfRedactionVerificationReport(IReadOnlyList<PdfRedactionVerificationFinding> Findings)
{
    private static readonly PdfRedactionVerificationJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfRedactionVerificationJsonContext IndentedJson = new(JsonOptions(true));

    /// <summary>Gets whether the output passed every redaction safety check.</summary>
    public bool Succeeded => Findings.Count == 0;

    private IReadOnlyList<PdfReferenceRepair> referenceRepairs = [];
    /// <summary>Gets source-null references repaired while producing the verified output.</summary>
    public IReadOnlyList<PdfReferenceRepair> ReferenceRepairs
    {
        get => referenceRepairs;
        init => referenceRepairs = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>Exports the verification result as stable machine-readable JSON.</summary>
    public string ToJson(bool indented = false)
        => JsonSerializer.Serialize(new ReportFile(1, Succeeded, Findings,
            PdfRedactionRepairDetail.From(ReferenceRepairs)),
            indented ? IndentedJson.ReportFile : CompactJson.ReportFile);

    private sealed record ReportFile(
        int Version, bool Succeeded, IReadOnlyList<PdfRedactionVerificationFinding> Findings,
        IReadOnlyList<PdfRedactionRepairDetail> ReferenceRepairs);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented,
        Converters =
        {
            new JsonStringEnumConverter<PdfReferenceRepairReason>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfReferenceRepairAction>(JsonNamingPolicy.CamelCase)
        }
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfRedactionVerificationJsonContext : JsonSerializerContext;

    /// <summary>Exports a readable verification result with page and object locations.</summary>
    public string ToText()
    {
        var output = new StringBuilder()
            .Append("Redaction verification: ")
            .AppendLine(Succeeded ? "Passed" : "Failed");
        if (Findings.Count == 0) output.AppendLine("No findings.");
        foreach (PdfRedactionVerificationFinding finding in Findings)
        {
            output.Append(finding.Code);
            if (finding.PageIndex is int pageIndex)
                output.Append(" | Page ").Append(pageIndex + 1);
            if (finding.ObjectNumber is int objectNumber)
                output.Append(" | Object ").Append(objectNumber);
            output.Append(": ").AppendLine(finding.Message);
        }
        foreach (PdfReferenceRepair repair in ReferenceRepairs)
            output.Append("Reference repair | Owner ").Append(repair.OwnerObjectNumber?.ToString() ?? "Trailer")
                .Append(" | Generation ").Append(repair.OwnerGeneration?.ToString() ?? "None")
                .Append(" | Target ").Append(repair.TargetObjectNumber).Append(' ')
                .Append(repair.TargetGeneration).Append(" | ").Append(repair.Reason)
                .Append(" | ").AppendLine(repair.Action.ToString());
        return output.ToString();
    }
}

// Container paths can contain sensitive dictionary keys, so reports export identities only.
internal sealed record PdfRedactionRepairDetail(int? OwnerObjectNumber, int? OwnerGeneration,
    int TargetObjectNumber, int TargetGeneration, PdfReferenceRepairReason Reason,
    PdfReferenceRepairAction Action)
{
    internal static IReadOnlyList<PdfRedactionRepairDetail> From(IReadOnlyList<PdfReferenceRepair> repairs)
        => Array.AsReadOnly(repairs.Select(repair => new PdfRedactionRepairDetail(
            repair.OwnerObjectNumber, repair.OwnerGeneration, repair.TargetObjectNumber,
            repair.TargetGeneration, repair.Reason, repair.Action)).ToArray());
}

/// <summary>One named set of fully sanitized raster pages for isolated batch rebuilding.</summary>
public sealed record PdfRedactionBatchInput(
    string Name,
    IReadOnlyList<PdfSanitizedRasterPage> Pages,
    IReadOnlyList<string>? ProhibitedText = null);

/// <summary>The isolated rebuild and verification result for one batch input.</summary>
public sealed record PdfRedactionBatchResult(
    int Index,
    string Name,
    bool Succeeded,
    ReadOnlyMemory<byte> Document,
    PdfRedactionVerificationReport? Verification,
    string? Error);

/// <summary>The verified result of permanently removing reviewed comment annotations.</summary>
public sealed partial record PdfCommentRedactionResult(
    ReadOnlyMemory<byte> Document, IReadOnlyList<string> RemovedIds, int RemainingComments)
{
    private static readonly PdfCommentRedactionJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfCommentRedactionJsonContext IndentedJson = new(JsonOptions(true));

    private IReadOnlyList<PdfReferenceRepair> referenceRepairs = [];
    /// <summary>Gets source-null references repaired while removing the selected comments.</summary>
    public IReadOnlyList<PdfReferenceRepair> ReferenceRepairs
    {
        get => referenceRepairs;
        init => referenceRepairs = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>Exports a data-safe result without document bytes or comment text.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new ReportFile(1, RemovedIds.Count, RemovedIds, RemainingComments,
            PdfRedactionRepairDetail.From(ReferenceRepairs)),
        indented ? IndentedJson.ReportFile : CompactJson.ReportFile);

    private sealed record ReportFile(
        int Version, int RemovedCount, IReadOnlyList<string> RemovedIds,
        int RemainingComments, IReadOnlyList<PdfRedactionRepairDetail> ReferenceRepairs);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented,
        Converters =
        {
            new JsonStringEnumConverter<PdfReferenceRepairReason>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfReferenceRepairAction>(JsonNamingPolicy.CamelCase)
        }
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfCommentRedactionJsonContext : JsonSerializerContext;
}

/// <summary>The verified result of permanently removing reviewed attachments.</summary>
public sealed partial record PdfAttachmentRedactionResult(
    ReadOnlyMemory<byte> Document, IReadOnlyList<string> RemovedIds,
    IReadOnlyList<string> RemainingAttachmentNames)
{
    private static readonly PdfAttachmentRedactionJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfAttachmentRedactionJsonContext IndentedJson = new(JsonOptions(true));

    private IReadOnlyList<PdfReferenceRepair> referenceRepairs = [];
    /// <summary>Gets source-null references repaired while removing the selected attachments.</summary>
    public IReadOnlyList<PdfReferenceRepair> ReferenceRepairs
    {
        get => referenceRepairs;
        init => referenceRepairs = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>Exports a data-safe result without document bytes or attachment payloads.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new ReportFile(1, RemovedIds.Count, RemovedIds, RemainingAttachmentNames,
            PdfRedactionRepairDetail.From(ReferenceRepairs)),
        indented ? IndentedJson.ReportFile : CompactJson.ReportFile);

    private sealed record ReportFile(
        int Version, int RemovedCount, IReadOnlyList<string> RemovedIds,
        IReadOnlyList<string> RemainingAttachmentNames,
        IReadOnlyList<PdfRedactionRepairDetail> ReferenceRepairs);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented,
        Converters =
        {
            new JsonStringEnumConverter<PdfReferenceRepairReason>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfReferenceRepairAction>(JsonNamingPolicy.CamelCase)
        }
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfAttachmentRedactionJsonContext : JsonSerializerContext;
}

/// <summary>Creates and verifies PDFs that contain only sanitized page images.</summary>
public static class PdfPermanentRedaction
{
    private static readonly PdfName InformationName = Name("Info");
    private static readonly PdfName MetadataName = Name("Metadata");
    private static readonly PdfName NamesName = Name("Names");
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName OutlinesName = Name("Outlines");
    private static readonly PdfName AnnotationsName = Name("Annots");
    private static readonly (PdfName Key, string Code, string Message)[] CatalogDataStores =
    [
        (Name("StructTreeRoot"), "StructureTree", "The output contains a structure tree."),
        (Name("AF"), "AssociatedFiles", "The output contains associated files."),
        (Name("Collection"), "Collection", "The output contains portfolio data."),
        (Name("OpenAction"), "OpenAction", "The output contains an open action."),
        (Name("AA"), "CatalogActions", "The output contains catalog actions."),
        (Name("OCProperties"), "OptionalContent", "The output contains optional-content data."),
        (Name("PieceInfo"), "CatalogPieceInfo", "The output contains private application data.")
    ];
    private static readonly (PdfName Key, string Code, string Message)[] PageDataStores =
    [
        (Name("Metadata"), "PageMetadata", "The output page contains metadata."),
        (Name("PieceInfo"), "PagePieceInfo", "The output page contains private application data."),
        (Name("AA"), "PageActions", "The output page contains actions."),
        (Name("Thumb"), "PageThumbnail", "The output page contains a thumbnail."),
        (Name("AF"), "PageAssociatedFiles", "The output page contains associated files.")
    ];

    /// <summary>Permanently removes selected reviewed attachments and verifies the result.</summary>
    public static PdfAttachmentRedactionResult ApplyReviewedAttachments(
        PdfDocument document, PdfRedactionReview review,
        bool allowSignatureInvalidation = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(review);
        PdfRedactionMatch[] selected = [.. review.Included];
        if (selected.Any(match => match.TargetKind != PdfRedactionTargetKind.Attachment))
            throw new ArgumentException(
                "Reviewed attachment removal accepts only attachment targets.", nameof(review));
        PdfAttachmentInfo[] current = [.. PdfAttachmentReader.Read(document)];
        var targets = new List<(PdfRedactionMatch Match, PdfAttachmentInfo Attachment)>();
        foreach (PdfRedactionMatch match in selected)
        {
            if (!TryAttachmentIndex(match.Id, out int index)
                || index >= current.Length
                || !string.Equals(current[index].FileName, match.Text, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The document no longer contains reviewed attachment '{match.Id}'.");
            targets.Add((match, current[index]));
        }
        // A review is made from the name tree, so with every listed file chosen, a file that only an
        // /AF entry or an action names goes too. A stream that a kept file still uses is never removed.
        Dictionary<string, HashSet<int>> listed = PdfEmbeddedFileReach.ListedStreams(document);
        HashSet<string> chosen = [.. targets.Select(item => item.Attachment.AttachmentKey
            ?? throw new InvalidOperationException("A reviewed attachment has no name-tree key."))];
        HashSet<int> removedStreams = [.. listed.Where(file => chosen.Contains(file.Key))
            .SelectMany(file => file.Value)];
        if (targets.Count == current.Length)
            removedStreams.UnionWith(PdfEmbeddedFileReach.Reachable(document).Keys);
        removedStreams.ExceptWith(listed.Where(file => !chosen.Contains(file.Key))
            .SelectMany(file => file.Value));
        if (targets.Count == 0 && removedStreams.Count == 0)
            return new PdfAttachmentRedactionResult(document.Source, [],
                Array.AsReadOnly(current.Select(item => item.FileName).ToArray()));

        (PdfDocument editingSource, IReadOnlyList<PdfReferenceRepair> preflightRepairs) =
            PrepareRemoval(document, allowSignatureInvalidation);
        if (chosen.Count > 0)
        {
            var editor = new PdfIncrementalPageEditor(editingSource);
            foreach (string key in chosen) editor.RemoveAttachmentByKey(key);
            editingSource = PdfDocument.Open(editor.Build());
        }
        PdfDocumentWriteResult written = PdfDocumentWriter.WriteWithReport(
            PdfEmbeddedFileReach.Strip(editingSource, removedStreams),
            new PdfDocumentWriteOptions
            {
                PruneUnreachableObjects = true,
                ReplaceDanglingReferences = true,
                AllowSignatureInvalidation = allowSignatureInvalidation
            });
        byte[] output = written.Bytes;
        PdfDocument reopened = PdfDocument.Open(output);
        // Files are identified by their place in the tree, since several can list the same file name and
        // several can share a name-tree key (#455). Removal keeps the order of the files it keeps.
        PdfAttachmentInfo[] reopenedAttachments = [.. PdfAttachmentReader.Read(reopened)];
        string[] remaining = [.. reopenedAttachments.Select(item => item.FileName)];
        // Each kept file is checked by its key and its payload, since keys alone do not tell apart files
        // that share one.
        PdfAttachmentInfo[] expected = [.. current.Where(item => !chosen.Contains(item.AttachmentKey!))];
        if (reopenedAttachments.Length != expected.Length
            || reopenedAttachments.Where((item, index) =>
                !string.Equals(item.NameTreeKey, expected[index].NameTreeKey, StringComparison.Ordinal)
                || !item.Data.Span.SequenceEqual(expected[index].Data.Span)).Any()
            || reopened.CrossReferences.Sections.Count != 1)
            throw new InvalidOperationException(
                "The saved document did not preserve the reviewed attachment-removal result.");
        HashSet<int> keptStreams = [.. PdfEmbeddedFileReach.ListedStreams(reopened).Values
            .SelectMany(streams => streams)];
        if (PdfEmbeddedFileReach.Reachable(reopened).Keys.Any(stream => !keptStreams.Contains(stream)))
            throw new InvalidOperationException(
                "The saved document still holds an embedded file the review did not keep.");
        return new PdfAttachmentRedactionResult(output,
            Array.AsReadOnly(selected.Select(match => match.Id).ToArray()),
            Array.AsReadOnly(remaining))
        {
            ReferenceRepairs = Array.AsReadOnly(preflightRepairs.Concat(written.ReferenceRepairs).ToArray())
        };

        static bool TryAttachmentIndex(string id, out int index)
        {
            index = -1;
            return id.StartsWith("attachment:", StringComparison.Ordinal)
                && int.TryParse(id.AsSpan("attachment:".Length), out index)
                && index >= 0;
        }
    }

    /// <summary>Permanently removes selected reviewed comments and verifies the rewritten result.</summary>
    public static PdfCommentRedactionResult ApplyReviewedComments(
        PdfDocument document, PdfRedactionReview review,
        bool allowSignatureInvalidation = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(review);
        PdfRedactionMatch[] selected = [.. review.Included];
        if (selected.Any(match => match.TargetKind != PdfRedactionTargetKind.Comment))
            throw new ArgumentException(
                "Reviewed comment removal accepts only comment targets.", nameof(review));
        if (selected.Any(match => !string.IsNullOrEmpty(match.OverlayText)))
            throw new NotSupportedException(
                "Comment redaction overlay text requires a rasterized page workflow.");
        if (selected.Length == 0)
            return new PdfCommentRedactionResult(document.Source,
                [], PdfCommentReader.Read(document).Count);

        PdfCommentInfo[] current = [.. PdfCommentReader.Read(document)];
        var targets = new List<(PdfRedactionMatch Match, PdfCommentInfo Comment)>();
        foreach (PdfRedactionMatch match in selected)
        {
            PdfCommentInfo? comment = current.SingleOrDefault(item => string.Equals(
                match.Id, $"comment:{item.PageIndex}:{item.AnnotationIndex}",
                StringComparison.Ordinal));
            if (comment is null || comment.Bounds != match.Bounds
                || !string.Equals(comment.Contents, match.Text, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The document no longer contains reviewed comment '{match.Id}'.");
            targets.Add((match, comment));
        }

        (PdfDocument editingSource, IReadOnlyList<PdfReferenceRepair> preflightRepairs) =
            PrepareRemoval(document, allowSignatureInvalidation);
        var editor = new PdfIncrementalAnnotationEditor(editingSource);
        foreach (var (Match, Comment) in targets.OrderByDescending(item => item.Comment.PageIndex)
                     .ThenByDescending(item => item.Comment.AnnotationIndex))
            editor.RemoveAnnotationAt(
                Comment.PageIndex, Comment.AnnotationIndex);
        PdfDocument edited = PdfDocument.Open(editor.Build());
        PdfDocumentWriteResult written = PdfDocumentWriter.WriteWithReport(edited, new PdfDocumentWriteOptions
        {
            PruneUnreachableObjects = true,
            ReplaceDanglingReferences = true,
            AllowSignatureInvalidation = allowSignatureInvalidation
        });
        byte[] output = written.Bytes;
        PdfDocument reopened = PdfDocument.Open(output);
        PdfCommentInfo[] remaining = [.. PdfCommentReader.Read(reopened)];
        string[] expected = [.. current.Except(targets.Select(item => item.Comment))
            .Select(item => item.Contents).Order(StringComparer.Ordinal)];
        string[] actual = [.. remaining.Select(item => item.Contents)
            .Order(StringComparer.Ordinal)];
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal)
            || reopened.CrossReferences.Sections.Count != 1)
            throw new InvalidOperationException(
                "The saved document did not preserve the reviewed comment-removal result.");
        return new PdfCommentRedactionResult(output,
            Array.AsReadOnly(selected.Select(match => match.Id).ToArray()), remaining.Length)
        {
            ReferenceRepairs = Array.AsReadOnly(preflightRepairs.Concat(written.ReferenceRepairs).ToArray())
        };
    }

    private static (PdfDocument Document, IReadOnlyList<PdfReferenceRepair> Repairs) PrepareRemoval(
        PdfDocument document, bool allowSignatureInvalidation)
    {
        if (PdfDocumentWriter.FindReferenceRepairs(document).Count == 0) return (document, []);
        if (document.IsEncrypted)
            throw new NotSupportedException(
                "Repairing an encrypted document before reviewed removal requires reopening it with credentials. Encryption was not removed.");
        PdfDocumentWriteResult normalized = PdfDocumentWriter.WriteWithReport(document,
            new PdfDocumentWriteOptions
            {
                ReplaceDanglingReferences = true,
                AllowSignatureInvalidation = allowSignatureInvalidation
            });
        return (PdfDocument.Open(normalized.Data), normalized.ReferenceRepairs);
    }

    /// <summary>Builds a new PDF without copying any object or revision from the source document.</summary>
    public static byte[] RebuildFromSanitizedPages(IEnumerable<PdfSanitizedRasterPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        PdfSanitizedRasterPage[] values = [.. pages];
        if (values.Length == 0) throw new ArgumentException("At least one sanitized page is required.", nameof(pages));
        var builder = new PdfDocumentBuilder();
        foreach (PdfSanitizedRasterPage page in values)
            builder.AddPage(page.Width, page.Height,
                new PdfContentStreamBuilder().DrawImage(page.Image, 0, 0, page.Width, page.Height));
        return builder.Build();
    }

    /// <summary>Rebuilds and verifies ordered inputs while isolating document-specific failures.</summary>
    public static IReadOnlyList<PdfRedactionBatchResult> RebuildBatch(
        IEnumerable<PdfRedactionBatchInput> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var results = new List<PdfRedactionBatchResult>();
        foreach ((PdfRedactionBatchInput input, int index) in inputs.Select((item, index) =>
                     (item ?? throw new ArgumentException("A redaction batch input is null.",
                         nameof(inputs)), index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(input.Name))
                    throw new ArgumentException("A redaction batch input name is required.");
                byte[] document = RebuildFromSanitizedPages(input.Pages);
                PdfRedactionVerificationReport verification = VerifySanitizedOutput(
                    document, input.Pages.Count, input.ProhibitedText, cancellationToken);
                results.Add(new PdfRedactionBatchResult(index, input.Name,
                    verification.Succeeded, document, verification,
                    verification.Succeeded ? null : "The rebuilt document failed verification."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is ArgumentException or FormatException
                or InvalidOperationException or NotSupportedException or OverflowException)
            {
                results.Add(new PdfRedactionBatchResult(
                    index, input.Name, false, ReadOnlyMemory<byte>.Empty, null, error.Message));
            }
        }
        return Array.AsReadOnly(results.ToArray());
    }

    /// <summary>Verifies that a rebuilt output contains only one raster image per page and no recoverable document data.</summary>
    public static PdfRedactionVerificationReport VerifySanitizedOutput(ReadOnlyMemory<byte> source,
        int expectedPageCount, IEnumerable<string>? prohibitedText = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPageCount);
        string[] prohibited = [.. (prohibitedText ?? []).Where(value => !string.IsNullOrEmpty(value))];
        PdfDocument document = PdfDocument.Open(source);
        var findings = new List<PdfRedactionVerificationFinding>();
        if (document.CrossReferences.Sections.Count != 1)
            findings.Add(new("IncrementalRevisions", "The output contains incremental revision history."));
        if (document.Trailer.ContainsKey(InformationName))
            findings.Add(new("DocumentInformation", "The output contains a document-information dictionary."));
        if (document.Trailer.ContainsKey(Name("Encrypt")))
            findings.Add(new("Encryption", "The output remains encrypted."));
        PdfPageTree tree = PdfPageTree.Read(document);
        CheckCatalog(MetadataName, "XmpMetadata", "The output contains XMP metadata.");
        CheckCatalog(NamesName, "NameTrees", "The output contains document name trees.");
        CheckCatalog(AcroFormName, "AcroForm", "The output contains form data.");
        CheckCatalog(OutlinesName, "Outlines", "The output contains bookmarks.");
        foreach (var (Key, Code, Message) in CatalogDataStores)
            CheckCatalog(Key, Code, Message);
        if (tree.Pages.Count != expectedPageCount)
            findings.Add(new("PageCount", $"Expected {expectedPageCount} pages but found {tree.Pages.Count}."));
        var reader = new PdfPageContentReader(document);
        for (int pageIndex = 0; pageIndex < tree.Pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tree.Pages[pageIndex].Dictionary.ContainsKey(AnnotationsName))
                findings.Add(new("Annotations", "The output page contains annotations.", pageIndex));
            foreach (var (Key, Code, Message) in PageDataStores)
                if (tree.Pages[pageIndex].Dictionary.ContainsKey(Key))
                    findings.Add(new(Code, Message, pageIndex));
            PdfPageContent content = reader.Read(pageIndex, cancellationToken);
            if (content.Images.Count != 1 || content.Letters.Count != 0 || content.Paths.Count != 0)
                findings.Add(new("NonRasterContent", "The output page is not a single image-only page.", pageIndex));
            foreach (string value in prohibited)
                if (content.Text.Contains(value, StringComparison.OrdinalIgnoreCase))
                    findings.Add(new("ProhibitedText", $"The output still exposes prohibited text '{value}'.", pageIndex));
        }
        InspectActiveObjects();
        return new PdfRedactionVerificationReport(Array.AsReadOnly(findings.ToArray()));

        void CheckCatalog(PdfName key, string code, string message)
        {
            if (tree.Catalog.ContainsKey(key)) findings.Add(new(code, message));
        }

        void InspectActiveObjects()
        {
            if (prohibited.Length == 0) return;
            var objects = new HashSet<int>();
            var reported = new HashSet<(int ObjectNumber, string Text)>();
            foreach (PdfCrossReferenceEntry entry in document.CrossReferences.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Type is not (PdfCrossReferenceEntryType.InUse
                    or PdfCrossReferenceEntryType.Compressed)
                    || !objects.Add(entry.ObjectNumber)) continue;
                try { Inspect(document.Resolve(entry.ObjectNumber), entry.ObjectNumber, 0); }
                catch (Exception error) when (error is ArgumentException or FormatException
                    or InvalidOperationException or NotSupportedException or OverflowException)
                {
                    findings.Add(new("ObjectInspectionFailure",
                        $"Object {entry.ObjectNumber} could not be inspected: {error.Message}",
                        ObjectNumber: entry.ObjectNumber));
                }
            }

            void Inspect(PdfObject value, int objectNumber, int depth)
            {
                if (depth >= 256)
                    throw new InvalidOperationException(
                        "A redaction-verification object graph is too deep.");
                switch (value)
                {
                    case PdfString text:
                        Check(PdfUnicodeEncoding.DecodeTextString(
                            text.Bytes.Span, $"Object {objectNumber} text"));
                        break;
                    case PdfArray array:
                        foreach (PdfObject item in array)
                            if (item is not PdfIndirectReference)
                                Inspect(item, objectNumber, depth + 1);
                        break;
                    case PdfStream stream:
                        Inspect(stream.Dictionary, objectNumber, depth + 1);
                        if (!IsImage(stream.Dictionary))
                        {
                            byte[] decoded = PdfStreamDecoder.Decode(
                                stream, document.Resolve, 64 * 1024 * 1024);
                            Check(DecodeText(decoded));
                            CheckEncodedText(decoded);
                        }
                        break;
                    case PdfDictionary dictionary:
                        foreach (PdfObject item in dictionary.Values)
                            if (item is not PdfIndirectReference)
                                Inspect(item, objectNumber, depth + 1);
                        break;
                }

                void Check(string candidate)
                {
                    foreach (string blocked in prohibited)
                        if (candidate.Contains(blocked, StringComparison.OrdinalIgnoreCase)
                            && reported.Add((objectNumber, blocked)))
                            findings.Add(new("ProhibitedObjectText",
                                $"Object {objectNumber} still exposes prohibited text '{blocked}'.",
                                ObjectNumber: objectNumber));
                }

                void CheckEncodedText(ReadOnlySpan<byte> candidate)
                {
                    foreach (string blocked in prohibited)
                    {
                        byte[] bigEndian = Encoding.BigEndianUnicode.GetBytes(blocked);
                        byte[] littleEndian = Encoding.Unicode.GetBytes(blocked);
                        string hex = Convert.ToHexString(
                            [0xFE, 0xFF, .. bigEndian]);
                        bool found = candidate.IndexOf(bigEndian) >= 0
                            || candidate.IndexOf(littleEndian) >= 0
                            || DecodeText(candidate).Contains(hex,
                                StringComparison.OrdinalIgnoreCase);
                        if (found && reported.Add((objectNumber, blocked)))
                            findings.Add(new("ProhibitedObjectText",
                                $"Object {objectNumber} still exposes prohibited text '{blocked}'.",
                                ObjectNumber: objectNumber));
                    }
                }
            }
        }
    }

    private static bool IsImage(PdfDictionary dictionary) =>
        dictionary.TryGetValue(Name("Subtype"), out PdfObject? subtype)
        && subtype is PdfName name && name.ValueAsLatin1() == "Image";

    private static string DecodeText(ReadOnlySpan<byte> value)
    {
        if (value.Length >= 2 && value[0] == 0xFE && value[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(value[2..]);
        if (value.Length >= 2 && value[0] == 0xFF && value[1] == 0xFE)
            return Encoding.Unicode.GetString(value[2..]);
        try { return new UTF8Encoding(false, true).GetString(value); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(value); }
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
}

/// <summary>
/// Finds and removes embedded files wherever a document reaches them. The name tree is only one way:
/// /AF arrays on the catalog, pages, annotations, forms and structure elements, FileAttachment
/// annotations, and the file specifications in actions and media clips all hold them too. A stream is
/// known by its object number, so a file reached several ways counts once.
/// </summary>
internal static class PdfEmbeddedFileReach
{
    private static readonly PdfName EncryptName = Name("Encrypt");
    private static readonly PdfName AssociatedFilesName = Name("AF");
    private static readonly PdfName EmbeddedFilesName = Name("EF");
    private static readonly PdfName RelatedFilesName = Name("RF");
    private static readonly PdfName TypeName = Name("Type");
    private static readonly PdfName FormPacketsName = Name("XFA");

    /// <summary>
    /// Lists every embedded-file stream reachable from the trailer, with the name its file
    /// specification gives it, or null for a stream no specification owns.
    /// </summary>
    internal static Dictionary<int, string?> Reachable(PdfDocument document)
    {
        var found = new Dictionary<int, string?>();
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        var pending = new Stack<PdfObject>(StartingPoints(document));
        // An explicit stack, because a long chain of pages or actions must not become a deep call chain.
        while (pending.Count > 0)
        {
            PdfObject value = pending.Pop();
            int? objectNumber = null;
            if (value is PdfIndirectReference reference)
            {
                if (!visited.Add((reference.ObjectNumber, reference.Generation))) continue;
                objectNumber = reference.ObjectNumber;
                try { value = document.Resolve(reference); }
                catch (Exception error) when (PdfReaderDamage.Is(error)) { continue; }
            }
            switch (value)
            {
                case PdfStream stream:
                    if (objectNumber is int number && IsEmbeddedFile(document, stream))
                        found.TryAdd(number, null);
                    foreach (PdfObject item in stream.Dictionary.Values) pending.Push(item);
                    break;
                case PdfDictionary dictionary:
                    foreach (int file in Payloads(document, dictionary))
                        found[file] = FileName(document, dictionary) ?? found.GetValueOrDefault(file);
                    // XFA writers type their packet streams as embedded files, but a packet is form data.
                    foreach ((PdfName key, PdfObject item) in dictionary)
                        if (!key.Equals(FormPacketsName)) pending.Push(item);
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array) pending.Push(item);
                    break;
                case PdfIndirectReference next:
                    pending.Push(next);
                    break;
            }
        }
        return found;
    }

    /// <summary>
    /// Gets the payload streams of each file in the embedded-files name tree, by attachment key, which
    /// stays unique when name-tree keys repeat (#455).
    /// </summary>
    internal static Dictionary<string, HashSet<int>> ListedStreams(PdfDocument document)
    {
        var listed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(Name("Names"), out PdfObject? namesValue)
            || ReadOrNull(document, namesValue) is not PdfDictionary names
            || !names.TryGetValue(Name("EmbeddedFiles"), out PdfObject? filesValue)) return listed;
        foreach (PdfAttachmentTreeEntry file in PdfAttachmentReader.ReadTree(document, filesValue))
        {
            PdfNameTreeEntry entry = file.Entry;
            if (!listed.TryGetValue(file.Identity, out HashSet<int>? streams)) listed[file.Identity] = streams = [];
            if (ReadOrNull(document, entry.Value) is PdfDictionary specification)
                streams.UnionWith(Payloads(document, specification));
        }
        return listed;
    }

    /// <summary>
    /// Removes the FileAttachment annotations and /AF entries that name the given streams, or every
    /// embedded file when none are given, and takes the payload out of any other file specification
    /// that still holds one, such as the target of an action. A stream no specification owns stays,
    /// so that verification reports it.
    /// </summary>
    internal static PdfDocument Strip(PdfDocument document, IReadOnlySet<int>? streams = null)
    {
        document = RemoveFileAnnotations(document, streams);
        var update = new PdfIncrementalUpdateBuilder(document);
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        var pending = new Stack<PdfIndirectReference>();
        foreach (PdfIndirectReference start in StartingPoints(document).OfType<PdfIndirectReference>())
            Visit(start);
        bool changed = false;
        while (pending.Count > 0)
        {
            PdfIndirectReference reference = pending.Pop();
            PdfObject resolved = ReadOrNull(document, reference);
            PdfObject sanitized = Sanitize(resolved);
            if (ReferenceEquals(resolved, sanitized)) continue;
            update.ReplaceObject(reference.ObjectNumber, sanitized);
            changed = true;
        }
        return changed ? PdfOptimizer.Reopen(document, update.Build()) : document;

        void Visit(PdfObject value)
        {
            if (value is PdfIndirectReference reference
                && visited.Add((reference.ObjectNumber, reference.Generation)))
                pending.Push(reference);
        }

        PdfObject Sanitize(PdfObject value)
        {
            Visit(value);
            switch (value)
            {
                case PdfStream stream:
                    var dictionary = (PdfDictionary)Sanitize(stream.Dictionary);
                    return ReferenceEquals(dictionary, stream.Dictionary) ? stream
                        : new PdfStream(dictionary, stream.EncodedData.Span);
                case PdfArray array:
                    PdfObject[] items = [.. array.Select(Sanitize)];
                    return items.Where((item, index) => !ReferenceEquals(item, array[index])).Any()
                        ? new PdfArray(items) : array;
                case PdfDictionary source:
                    bool removesFile = Removes(document, source, streams);
                    var entries = new List<KeyValuePair<PdfName, PdfObject>>(source.Count);
                    bool localChange = false;
                    foreach ((PdfName key, PdfObject item) in source)
                    {
                        PdfObject? kept = item;
                        if (removesFile && (key.Equals(EmbeddedFilesName) || key.Equals(RelatedFilesName)))
                            kept = null;
                        else if (key.Equals(AssociatedFilesName)) kept = KeptAssociation(document, item, streams);
                        if (kept is null) { localChange = true; continue; }
                        PdfObject sanitized = Sanitize(kept);
                        entries.Add(new KeyValuePair<PdfName, PdfObject>(key, sanitized));
                        localChange |= !ReferenceEquals(item, sanitized);
                    }
                    return localChange ? new PdfDictionary(entries) : source;
                default:
                    return value;
            }
        }
    }

    // An /AF value is an array of file specifications, or in older files a single one.
    private static PdfObject? KeptAssociation(PdfDocument document, PdfObject value, IReadOnlySet<int>? streams)
    {
        PdfObject resolved = ReadOrNull(document, value);
        if (resolved is PdfDictionary single) return Removes(document, single, streams) ? null : value;
        if (resolved is not PdfArray associated) return value;
        PdfObject[] kept = [.. associated.Where(item => ReadOrNull(document, item) is not PdfDictionary file
            || !Removes(document, file, streams))];
        if (kept.Length == associated.Count) return value;
        return kept.Length == 0 ? null : new PdfArray(kept);
    }

    private static PdfDocument RemoveFileAnnotations(PdfDocument document, IReadOnlySet<int>? streams)
    {
        PdfIncrementalAnnotationEditor? editor = null;
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
        {
            if (!page.Dictionary.TryGetValue(Name("Annots"), out PdfObject? annotationsValue)
                || ReadOrNull(document, annotationsValue) is not PdfArray annotations) continue;
            for (int index = annotations.Count - 1; index >= 0; index--)
                if (ReadOrNull(document, annotations[index]) is PdfDictionary annotation
                    && annotation.TryGetValue(Name("Subtype"), out PdfObject? subtype)
                    && ReadOrNull(document, subtype) is PdfName name && name.ValueAsLatin1() == "FileAttachment"
                    && annotation.TryGetValue(Name("FS"), out PdfObject? fileValue)
                    && ReadOrNull(document, fileValue) is PdfDictionary file
                    && Removes(document, file, streams))
                    (editor ??= new PdfIncrementalAnnotationEditor(document)).RemoveAnnotationAt(page.Index, index);
        }
        return editor is null ? document : PdfOptimizer.Reopen(document, editor.Build());
    }

    private static bool Removes(PdfDocument document, PdfDictionary specification, IReadOnlySet<int>? streams)
    {
        int[] payloads = [.. Payloads(document, specification)];
        return payloads.Length > 0 && (streams is null || payloads.Any(streams.Contains));
    }

    // The streams a file specification holds in /EF, under the keys for each platform's file name.
    private static IEnumerable<int> Payloads(PdfDocument document, PdfDictionary specification)
    {
        if (!specification.TryGetValue(EmbeddedFilesName, out PdfObject? value)
            || ReadOrNull(document, value) is not PdfDictionary files) yield break;
        foreach (PdfObject file in files.Values)
        {
            PdfObject current = file;
            for (int depth = 0; current is PdfIndirectReference reference && depth < 32; depth++)
            {
                PdfObject next = ReadOrNull(document, reference);
                if (next is PdfStream) yield return reference.ObjectNumber;
                current = next;
            }
        }
    }

    private static string? FileName(PdfDocument document, PdfDictionary specification)
    {
        foreach (string key in new[] { "UF", "F" })
            if (specification.TryGetValue(Name(key), out PdfObject? value)
                && ReadOrNull(document, value) is PdfString text)
                try { return PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "An embedded-file name"); }
                catch (Exception error) when (PdfReaderDamage.Is(error)) { return null; }
        return null;
    }

    private static bool IsEmbeddedFile(PdfDocument document, PdfStream stream) =>
        stream.Dictionary.TryGetValue(TypeName, out PdfObject? type)
        && ReadOrNull(document, type) is PdfName name && name.ValueAsLatin1() == "EmbeddedFile";

    private static IEnumerable<PdfObject> StartingPoints(PdfDocument document) =>
        document.Trailer.Where(entry => !entry.Key.Equals(EncryptName)).Select(entry => entry.Value);

    // An object that cannot be read holds nothing that can be listed or removed.
    private static PdfObject ReadOrNull(PdfDocument document, PdfObject value)
    {
        try
        {
            for (int depth = 0; value is PdfIndirectReference reference && depth < 32; depth++)
                value = document.Resolve(reference);
            return value;
        }
        catch (Exception error) when (PdfReaderDamage.Is(error)) { return PdfNull.Instance; }
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
