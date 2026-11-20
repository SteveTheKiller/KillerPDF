using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Writing;

/// <summary>One material change proposed by a document optimization plan.</summary>
public enum PdfOptimizationChangeKind
{
    /// <summary>Apply previewed nonvisual structural repairs.</summary>
    RepairHarmlessArtifacts,
    /// <summary>Replace incremental history with one complete revision.</summary>
    ConsolidateRevisions,
    /// <summary>Remove document information and XMP metadata.</summary>
    RemoveMetadata,
    /// <summary>Remove every embedded file.</summary>
    RemoveAttachments,
    /// <summary>Remove the action performed when the document opens.</summary>
    RemoveOpenAction,
    /// <summary>Remove the document outline.</summary>
    RemoveBookmarks,
    /// <summary>Remove every interactive form field and widget.</summary>
    RemoveFormFields,
    /// <summary>Remove the XFA packet set without removing ordinary AcroForm fields.</summary>
    RemoveXfaData,
    /// <summary>Remove every annotation that contains review text.</summary>
    RemoveComments,
    /// <summary>Remove the document JavaScript name tree and reachable JavaScript actions.</summary>
    RemoveDocumentJavaScript,
    /// <summary>Remove embedded page thumbnail images.</summary>
    RemovePageThumbnails,
    /// <summary>Preserve initially visible optional content and remove hidden layer content.</summary>
    FlattenOptionalContent,
    /// <summary>Remove unreferenced and duplicate page font and XObject resources.</summary>
    PruneUnusedPageResources,
    /// <summary>Remove active objects that are unreachable from the document trailer.</summary>
    PruneUnreachableObjects,
    /// <summary>Flate-compress unfiltered embedded streams when the result is smaller.</summary>
    CompressUnfilteredStreams,
    /// <summary>Write eligible objects into compressed object streams.</summary>
    PackObjects,
    /// <summary>Compress structural streams.</summary>
    CompressStructure
}

/// <summary>Explicit lossless optimization and sanitization choices.</summary>
public sealed record PdfOptimizationOptions
{
    /// <summary>Gets whether source-null references may be repaired with reported locations.</summary>
    public bool ReplaceDanglingReferences { get; init; }
    /// <summary>Gets whether previewed nonvisual structural repairs are applied.</summary>
    public bool RepairHarmlessArtifacts { get; init; }
    /// <summary>Gets whether descriptive document information and XMP are removed.</summary>
    public bool RemoveMetadata { get; init; }
    /// <summary>Gets whether every embedded file is removed.</summary>
    public bool RemoveAttachments { get; init; }
    /// <summary>Gets whether the document open action is removed.</summary>
    public bool RemoveOpenAction { get; init; }
    /// <summary>Gets whether every bookmark is removed.</summary>
    public bool RemoveBookmarks { get; init; }
    /// <summary>Gets whether every interactive form field and widget is removed.</summary>
    public bool RemoveFormFields { get; init; }
    /// <summary>Gets whether the embedded XFA packet set is removed.</summary>
    public bool RemoveXfaData { get; init; }
    /// <summary>Gets whether annotations containing review text are removed.</summary>
    public bool RemoveComments { get; init; }
    /// <summary>Gets whether the document JavaScript name tree and reachable actions are removed.</summary>
    public bool RemoveDocumentJavaScript { get; init; }
    /// <summary>Gets whether embedded page thumbnail images are removed.</summary>
    public bool RemovePageThumbnails { get; init; }
    /// <summary>Gets whether initially visible optional content is flattened and hidden content removed.</summary>
    public bool FlattenOptionalContent { get; init; }
    /// <summary>Gets whether unreferenced and duplicate page font and XObject resources are removed.</summary>
    public bool PruneUnusedPageResources { get; init; }
    /// <summary>Gets whether active objects unreachable from the document trailer are removed.</summary>
    public bool PruneUnreachableObjects { get; init; }
    /// <summary>Gets whether smaller Flate encodings replace unfiltered embedded streams.</summary>
    public bool CompressUnfilteredStreams { get; init; }
    /// <summary>Gets whether eligible objects are packed into object streams.</summary>
    public bool PackObjects { get; init; } = true;
    /// <summary>Gets whether structural streams are compressed.</summary>
    public bool CompressStructure { get; init; } = true;
    /// <summary>Gets whether signatures may be invalidated by the required full rewrite.</summary>
    public bool AllowSignatureInvalidation { get; init; }

    /// <summary>
    /// Creates size-reduction settings a document can actually be rewritten with. Object
    /// streams and cross-reference streams need PDF 1.5, so an older document keeps its
    /// original structure instead of failing on its declared version.
    /// </summary>
    public static PdfOptimizationOptions ForDocument(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        bool modern = document.Header.Version.CompareTo(new PdfVersion(1, 5)) >= 0;
        return new PdfOptimizationOptions
        {
            PruneUnreachableObjects = true,
            PruneUnusedPageResources = true,
            CompressUnfilteredStreams = true,
            PackObjects = modern,
            CompressStructure = modern
        };
    }
}

/// <summary>A completed optimization and its measured size change.</summary>
public sealed partial record PdfOptimizationResult(ReadOnlyMemory<byte> Data, int OriginalSize,
    int OutputSize, IReadOnlyList<PdfOptimizationChangeKind> Changes)
{
    private static readonly PdfOptimizationResultJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfOptimizationResultJsonContext IndentedJson = new(JsonOptions(true));

    /// <summary>Gets the signed output-size difference in bytes.</summary>
    public int SizeDifference => OutputSize - OriginalSize;
    /// <summary>Gets the original count of active cross-reference objects.</summary>
    public int OriginalObjectCount { get; init; }
    /// <summary>Gets the output count of active cross-reference objects.</summary>
    public int OutputObjectCount { get; init; }
    /// <summary>Gets the signed active-object-count difference.</summary>
    public int ObjectCountDifference => OutputObjectCount - OriginalObjectCount;
    /// <summary>Gets sanitization changes whose absence was verified after saving.</summary>
    public IReadOnlyList<PdfOptimizationChangeKind> VerifiedRemovals { get; init; } = [];
    /// <summary>Gets the structural repairs applied before optimization.</summary>
    public IReadOnlyList<PdfSaveRepairChange> Repairs { get; init; } = [];

    /// <summary>Gets reference replacements applied before or during optimization.</summary>
    public IReadOnlyList<PdfReferenceRepair> ReferenceRepairs { get; init; } = [];

    /// <summary>Serializes measured results without embedding the output PDF bytes.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new ReportFile(OriginalSize, OutputSize, SizeDifference,
            OriginalObjectCount, OutputObjectCount, ObjectCountDifference,
            Changes, VerifiedRemovals, Repairs, ReferenceRepairs),
        indented ? IndentedJson.ReportFile : CompactJson.ReportFile);

    private sealed record ReportFile(
        int OriginalSize, int OutputSize, int SizeDifference,
        int OriginalObjectCount, int OutputObjectCount, int ObjectCountDifference,
        IReadOnlyList<PdfOptimizationChangeKind> Changes,
        IReadOnlyList<PdfOptimizationChangeKind> VerifiedRemovals,
        IReadOnlyList<PdfSaveRepairChange> Repairs,
        IReadOnlyList<PdfReferenceRepair> ReferenceRepairs);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter<PdfOptimizationChangeKind>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfSaveRepairKind>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfReferenceRepairReason>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfReferenceRepairAction>(JsonNamingPolicy.CamelCase)
        }
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfOptimizationResultJsonContext : JsonSerializerContext;

    /// <summary>Formats measured optimization results without output PDF data.</summary>
    public string ToText()
    {
        var output = new StringBuilder();
        output.Append("PDF optimization: ").Append(OriginalSize.ToString(CultureInfo.InvariantCulture))
            .Append(" to ").Append(OutputSize.ToString(CultureInfo.InvariantCulture))
            .Append(" bytes (").Append(SizeDifference.ToString("+0;-0;0", CultureInfo.InvariantCulture))
            .AppendLine(")");
        output.Append("Active objects: ").Append(OriginalObjectCount.ToString(CultureInfo.InvariantCulture))
            .Append(" to ").Append(OutputObjectCount.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(ObjectCountDifference.ToString("+0;-0;0", CultureInfo.InvariantCulture))
            .AppendLine(")");
        AppendList(output, "Changes", Changes);
        AppendList(output, "Verified removals", VerifiedRemovals);
        AppendList(output, "Repairs", Repairs.Select(repair => repair.Kind));
        AppendReferenceRepairs(output, ReferenceRepairs);
        return output.ToString().TrimEnd();
    }

    internal static void AppendReferenceRepairs(StringBuilder output, IReadOnlyList<PdfReferenceRepair> repairs)
    {
        output.Append("Reference repairs: ").AppendLine(repairs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (PdfReferenceRepair repair in repairs)
        {
            string owner = repair.OwnerObjectNumber is int number
                ? FormattableString.Invariant($"object {number} {repair.OwnerGeneration}") : "trailer";
            output.AppendLine(FormattableString.Invariant(
                $"{owner} {repair.Path}: {repair.TargetObjectNumber} {repair.TargetGeneration}, {repair.Reason}, {repair.Action}"));
        }
    }

    private static void AppendList<T>(StringBuilder output, string label, IEnumerable<T> values)
    {
        string[] items = [.. values.Select(value => value?.ToString() ?? string.Empty)];
        output.Append(label).Append(": ")
            .AppendLine(items.Length == 0 ? "none" : string.Join(", ", items));
    }
}

/// <summary>An immutable preview of a deterministic full-document optimization.</summary>
public sealed partial class PdfOptimizationPlan
{
    private static readonly PdfOptimizationPlanJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfOptimizationPlanJsonContext IndentedJson = new(JsonOptions(true));

    private readonly PdfDocument _document;
    private readonly PdfDocument _originalDocument;
    private readonly PdfReferenceRepair[] _referenceRepairs;
    private readonly PdfOptimizationOptions _options;
    private readonly string[] _attachmentNames;
    private readonly string[] _attachmentKeys;
    private readonly string[] _formFieldNames;
    private readonly int[] _resourcePages;
    private readonly int[] _thumbnailPages;
    private readonly int[] _unreachableObjectNumbers;
    private readonly int[] _compressedStreamObjectNumbers;
    private readonly string[] _optionalContentGroupNames;
    private readonly string[] _hiddenOptionalContentGroupNames;
    private readonly PdfSaveRepairChange[] _repairs;
    private readonly int _commentCount;

    internal PdfOptimizationPlan(PdfDocument document, PdfOptimizationOptions options,
        IEnumerable<PdfOptimizationChangeKind> changes, IEnumerable<string> attachmentNames,
        IEnumerable<string> formFieldNames, IEnumerable<int> resourcePages,
        IEnumerable<int> thumbnailPages, IEnumerable<string> optionalContentGroupNames,
        IEnumerable<string> hiddenOptionalContentGroupNames,
        IEnumerable<PdfSaveRepairChange> repairs, int commentCount,
        IEnumerable<int> unreachableObjectNumbers,
        IEnumerable<int> compressedStreamObjectNumbers,
        PdfDocument? originalDocument = null,
        IEnumerable<PdfReferenceRepair>? referenceRepairs = null,
        IEnumerable<string>? attachmentKeys = null)
    {
        _document = document;
        _originalDocument = originalDocument ?? document;
        _referenceRepairs = referenceRepairs?.ToArray() ?? [];
        _options = options;
        _attachmentNames = [.. attachmentNames];
        _attachmentKeys = attachmentKeys is null ? _attachmentNames : [.. attachmentKeys];
        _formFieldNames = [.. formFieldNames];
        _resourcePages = [.. resourcePages];
        _thumbnailPages = [.. thumbnailPages];
        _unreachableObjectNumbers = [.. unreachableObjectNumbers];
        _compressedStreamObjectNumbers = [.. compressedStreamObjectNumbers];
        _optionalContentGroupNames = [.. optionalContentGroupNames];
        _hiddenOptionalContentGroupNames = [.. hiddenOptionalContentGroupNames];
        _repairs = [.. repairs];
        _commentCount = commentCount;
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    /// <summary>Gets the original byte count.</summary>
    public int OriginalSize => _originalDocument.Source.Length;
    /// <summary>Gets every material change in application order.</summary>
    public IReadOnlyList<PdfOptimizationChangeKind> Changes { get; }
    /// <summary>Gets embedded-file names that the plan will remove.</summary>
    public IReadOnlyList<string> AttachmentNames => Array.AsReadOnly(_attachmentNames);
    /// <summary>Gets form-field names that the plan will remove.</summary>
    public IReadOnlyList<string> FormFieldNames => Array.AsReadOnly(_formFieldNames);
    /// <summary>Gets the number of review annotations that the plan will remove.</summary>
    public int CommentCount => _commentCount;
    /// <summary>Gets zero-based pages whose resource dictionaries will be pruned.</summary>
    public IReadOnlyList<int> ResourcePageIndexes => Array.AsReadOnly(_resourcePages);
    /// <summary>Gets zero-based pages whose embedded thumbnails will be removed.</summary>
    public IReadOnlyList<int> ThumbnailPageIndexes => Array.AsReadOnly(_thumbnailPages);
    /// <summary>Gets object numbers that the full rewrite will remove as unreachable.</summary>
    public IReadOnlyList<int> UnreachableObjectNumbers =>
        Array.AsReadOnly(_unreachableObjectNumbers);
    /// <summary>Gets object numbers whose unfiltered streams will receive smaller Flate encodings.</summary>
    public IReadOnlyList<int> CompressedStreamObjectNumbers =>
        Array.AsReadOnly(_compressedStreamObjectNumbers);
    /// <summary>Gets layer names whose optional-content wrappers will be flattened.</summary>
    public IReadOnlyList<string> OptionalContentGroupNames =>
        Array.AsReadOnly(_optionalContentGroupNames);
    /// <summary>Gets initially hidden layer names whose content will be removed.</summary>
    public IReadOnlyList<string> HiddenOptionalContentGroupNames =>
        Array.AsReadOnly(_hiddenOptionalContentGroupNames);
    /// <summary>Gets structural repairs that the plan will apply.</summary>
    public IReadOnlyList<PdfSaveRepairChange> Repairs => Array.AsReadOnly(_repairs);

    /// <summary>Gets source-null references the plan will repair.</summary>
    public IReadOnlyList<PdfReferenceRepair> ReferenceRepairs => Array.AsReadOnly(_referenceRepairs);

    /// <summary>Serializes the complete preview without changing the document.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new ReportFile(OriginalSize, Changes, AttachmentNames, FormFieldNames,
            CommentCount, ResourcePageIndexes, ThumbnailPageIndexes,
            UnreachableObjectNumbers, CompressedStreamObjectNumbers,
            OptionalContentGroupNames, HiddenOptionalContentGroupNames, Repairs, ReferenceRepairs),
        indented ? IndentedJson.ReportFile : CompactJson.ReportFile);

    private sealed record ReportFile(
        int OriginalSize,
        IReadOnlyList<PdfOptimizationChangeKind> Changes,
        IReadOnlyList<string> AttachmentNames,
        IReadOnlyList<string> FormFieldNames,
        int CommentCount,
        IReadOnlyList<int> ResourcePageIndexes,
        IReadOnlyList<int> ThumbnailPageIndexes,
        IReadOnlyList<int> UnreachableObjectNumbers,
        IReadOnlyList<int> CompressedStreamObjectNumbers,
        IReadOnlyList<string> OptionalContentGroupNames,
        IReadOnlyList<string> HiddenOptionalContentGroupNames,
        IReadOnlyList<PdfSaveRepairChange> Repairs,
        IReadOnlyList<PdfReferenceRepair> ReferenceRepairs);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter<PdfOptimizationChangeKind>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfSaveRepairKind>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfReferenceRepairReason>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfReferenceRepairAction>(JsonNamingPolicy.CamelCase)
        }
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfOptimizationPlanJsonContext : JsonSerializerContext;

    /// <summary>Formats the complete optimization preview without changing the document.</summary>
    public string ToText()
    {
        var output = new StringBuilder();
        output.Append("Optimization preview: ").Append(OriginalSize.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" source bytes");
        output.Append("Changes: ").AppendLine(Changes.Count == 0
            ? "none" : string.Join(", ", Changes));
        output.Append("Attachments removed: ").AppendLine(_attachmentNames.Length == 0
            ? "none" : string.Join(", ", _attachmentNames));
        output.Append("Form fields removed: ").AppendLine(_formFieldNames.Length == 0
            ? "none" : string.Join(", ", _formFieldNames));
        output.Append("Comments removed: ").AppendLine(_commentCount.ToString(CultureInfo.InvariantCulture));
        output.Append("Resource pages: ").AppendLine(Pages(_resourcePages));
        output.Append("Thumbnail pages: ").AppendLine(Pages(_thumbnailPages));
        output.Append("Unreachable objects removed: ")
            .AppendLine(Numbers(_unreachableObjectNumbers));
        output.Append("Streams compressed: ")
            .AppendLine(Numbers(_compressedStreamObjectNumbers));
        output.Append("Flattened layers: ").AppendLine(_optionalContentGroupNames.Length == 0
            ? "none" : string.Join(", ", _optionalContentGroupNames));
        output.Append("Hidden layers removed: ").AppendLine(_hiddenOptionalContentGroupNames.Length == 0
            ? "none" : string.Join(", ", _hiddenOptionalContentGroupNames));
        PdfOptimizationResult.AppendReferenceRepairs(output, ReferenceRepairs);
        return output.ToString().TrimEnd();
    }

    private static string Pages(IEnumerable<int> indexes)
    {
        string[] pages = [.. indexes.Select(index =>
            (index + 1).ToString(CultureInfo.InvariantCulture))];
        return pages.Length == 0 ? "none" : string.Join(", ", pages);
    }

    private static string Numbers(IEnumerable<int> numbers)
    {
        string[] values = [.. numbers.Select(number =>
            number.ToString(CultureInfo.InvariantCulture))];
        return values.Length == 0 ? "none" : string.Join(", ", values);
    }

    /// <summary>Applies the previewed plan and verifies that the result reopens with the same page count.</summary>
    public PdfOptimizationResult Apply()
    {
        int pageCount = PdfPageTree.Read(_document).Pages.Count;
        PdfDocument source = _repairs.Length == 0 ? _document
            : PdfOptimizer.Reopen(_document, PdfSaveSanitizer.ApplyPlan(_document, _repairs));
        source = ApplySelectiveSanitization(source);
        if (Changes.Contains(PdfOptimizationChangeKind.RemoveDocumentJavaScript))
            source = PdfOptimizer.RemoveJavaScriptActions(source);
        PdfDocumentWriteResult written = PdfDocumentWriter.WriteWithReport(source, new PdfDocumentWriteOptions
        {
            MetadataPolicy = _options.RemoveMetadata
                ? PdfMetadataPolicy.RemoveDocumentInformationAndXmp : PdfMetadataPolicy.Preserve,
            CrossReferenceFormat = _options.PackObjects || _options.CompressStructure
                ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table,
            UseObjectStreams = _options.PackObjects,
            CompressStructuralStreams = _options.CompressStructure,
            // A removed attachment that stays in the file as an unused object is still in the file.
            PruneUnreachableObjects = _options.PruneUnreachableObjects
                || Changes.Contains(PdfOptimizationChangeKind.RemoveAttachments),
            CompressUnfilteredStreams = _options.CompressUnfilteredStreams,
            DeduplicatePageResources = _options.PruneUnusedPageResources,
            AllowSignatureInvalidation = _options.AllowSignatureInvalidation,
            ReplaceDanglingReferences = _options.ReplaceDanglingReferences
        });
        byte[] output = written.Bytes;
        PdfDocument reopened = PdfDocument.Open(output);
        if (PdfPageTree.Read(reopened).Pages.Count != pageCount)
            throw new InvalidOperationException("The optimized document did not preserve its page count.");
        if (reopened.CrossReferences.Sections.Count != 1)
            throw new InvalidOperationException("The optimized document still contains revision history.");
        IReadOnlyList<PdfOptimizationChangeKind> verified = VerifySanitization(reopened);
        return new PdfOptimizationResult(output, OriginalSize, output.Length, Changes)
        {
            VerifiedRemovals = verified,
            Repairs = Array.AsReadOnly(_repairs),
            ReferenceRepairs = Array.AsReadOnly(_referenceRepairs.Concat(written.ReferenceRepairs).ToArray()),
            OriginalObjectCount = ActiveObjectCount(_originalDocument),
            OutputObjectCount = ActiveObjectCount(reopened)
        };
    }

    private static int ActiveObjectCount(PdfDocument document) =>
        document.CrossReferences.Values.Count(entry =>
            entry.Type is CrossReference.PdfCrossReferenceEntryType.InUse
                or CrossReference.PdfCrossReferenceEntryType.Compressed);

    private System.Collections.ObjectModel.ReadOnlyCollection<PdfOptimizationChangeKind> VerifySanitization(PdfDocument document)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        var verified = new List<PdfOptimizationChangeKind>();
        Verify(PdfOptimizationChangeKind.RemoveMetadata, () =>
            !document.Trailer.ContainsKey(new PdfName("Info"u8))
            && !tree.Catalog.ContainsKey(new PdfName("Metadata"u8)));
        Verify(PdfOptimizationChangeKind.RemoveAttachments, () =>
            PdfAttachmentReader.Read(document).Count == 0
            && tree.Pages.SelectMany((_, pageIndex) =>
                PdfAttachmentReader.ReadPageAnnotations(document, pageIndex)).Any() == false
            && PdfEmbeddedFileReach.Reachable(document).Count == 0);
        Verify(PdfOptimizationChangeKind.RemoveOpenAction, () =>
            !tree.Catalog.ContainsKey(new PdfName("OpenAction"u8)));
        Verify(PdfOptimizationChangeKind.RemoveBookmarks, () =>
            !tree.Catalog.ContainsKey(new PdfName("Outlines"u8)));
        Verify(PdfOptimizationChangeKind.RemoveFormFields, () =>
            tree.Pages.SelectMany((_, pageIndex) =>
                PdfFormWidgetReader.ReadPage(document, pageIndex)).Any() == false);
        Verify(PdfOptimizationChangeKind.RemoveXfaData, () =>
            PdfXfaReader.Read(document) is null);
        Verify(PdfOptimizationChangeKind.RemoveComments, () =>
            PdfCommentReader.Read(document).Count == 0);
        Verify(PdfOptimizationChangeKind.RemoveDocumentJavaScript, () =>
            !(tree.Catalog.TryGetValue(new PdfName("Names"u8), out PdfObject? namesValue)
                && Resolve(document, namesValue) is PdfDictionary names
                && names.ContainsKey(new PdfName("JavaScript"u8)))
            && !PdfOptimizer.HasJavaScriptActions(document));
        Verify(PdfOptimizationChangeKind.RemovePageThumbnails, () =>
            tree.Pages.All(page => !page.Dictionary.ContainsKey(new PdfName("Thumb"u8))));
        Verify(PdfOptimizationChangeKind.FlattenOptionalContent, () =>
            PdfOptionalContentReader.Read(document).Groups.Count == 0);
        // Writing merges identical objects after their names were consolidated, so a page can end
        // with two names for one object, which costs nothing and is not content that was left behind.
        Verify(PdfOptimizationChangeKind.PruneUnusedPageResources, () =>
            PdfOptimizer.UnusedResourcePages(document, includeAliases: false).Count == 0
            && PdfDocumentWriter.CountDuplicatePageResourceObjects(document) == 0);
        Verify(PdfOptimizationChangeKind.PruneUnreachableObjects, () =>
            PdfDocumentWriter.CountUnreachableObjects(document) == 0);
        Verify(PdfOptimizationChangeKind.CompressUnfilteredStreams, () =>
            PdfDocumentWriter.CountCompressibleStreams(document) == 0);
        return Array.AsReadOnly(verified.ToArray());

        // Each check reads only for a change that was made, so a damaged part of the document that a
        // Reduce never touched cannot stop it.
        void Verify(PdfOptimizationChangeKind kind, Func<bool> absent)
        {
            if (!Changes.Contains(kind)) return;
            if (!absent())
                throw new InvalidOperationException(
                    $"The optimized document still contains content reported as {kind}.");
            verified.Add(kind);
        }
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An indirect object chain contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private PdfDocument ApplySelectiveSanitization(PdfDocument document)
    {
        bool removesAttachments = Changes.Contains(PdfOptimizationChangeKind.RemoveAttachments);
        bool removesOpenAction = Changes.Contains(PdfOptimizationChangeKind.RemoveOpenAction);
        bool removesBookmarks = Changes.Contains(PdfOptimizationChangeKind.RemoveBookmarks);
        bool removesFormFields = Changes.Contains(PdfOptimizationChangeKind.RemoveFormFields);
        bool removesXfaData = Changes.Contains(PdfOptimizationChangeKind.RemoveXfaData);
        bool removesComments = Changes.Contains(PdfOptimizationChangeKind.RemoveComments);
        bool removesDocumentJavaScript = Changes.Contains(
            PdfOptimizationChangeKind.RemoveDocumentJavaScript);
        bool removesPageThumbnails = Changes.Contains(
            PdfOptimizationChangeKind.RemovePageThumbnails);
        bool flattensOptionalContent = Changes.Contains(
            PdfOptimizationChangeKind.FlattenOptionalContent);
        bool prunesUnusedResources = Changes.Contains(
            PdfOptimizationChangeKind.PruneUnusedPageResources)
            && _resourcePages.Length > 0;
        if (!removesAttachments && !removesOpenAction && !removesBookmarks
            && !removesFormFields && !removesXfaData && !removesComments && !removesDocumentJavaScript
            && !removesPageThumbnails && !flattensOptionalContent
            && !prunesUnusedResources)
            return document;
        PdfDocument formSanitized = flattensOptionalContent
            ? PdfOptimizer.Reopen(document, PdfOptionalContentEditor.FlattenPageContent(document))
            : document;
        if (_attachmentKeys.Length > 0 || removesOpenAction || removesBookmarks || removesFormFields
            || removesXfaData
            || removesDocumentJavaScript || removesPageThumbnails || prunesUnusedResources)
        {
            var editor = new PdfIncrementalPageEditor(formSanitized);
            if (prunesUnusedResources)
            {
                // A tagged document refuses replaced content without matching structure, so its pages
                // keep their operators and only their /Resources change.
                bool tagged = PdfOptimizer.IsTagged(formSanitized);
                var content = new PdfPageContentReader(formSanitized);
                var fingerprints = new PdfResourceFingerprints();
                foreach (int pageIndex in _resourcePages)
                {
                    IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions =
                        content.ReadInstructions(pageIndex);
                    if (!tagged)
                    {
                        instructions = PdfOptimizer.ConsolidateResourceAliases(
                            formSanitized, pageIndex, instructions, fingerprints);
                        editor.SetPageContent(pageIndex, instructions);
                    }
                    editor.PrunePageResources(pageIndex,
                        PdfOptimizer.NamingDefaultColorSpaces(formSanitized, pageIndex, instructions));
                }
            }
            if (removesAttachments)
                foreach (string key in _attachmentKeys) editor.RemoveAttachmentByKey(key);
            if (removesOpenAction) editor.ClearOpenAction();
            if (removesBookmarks) editor.ClearBookmarks();
            if (removesFormFields)
                foreach (string name in _formFieldNames) editor.RemoveFormField(name);
            if (removesXfaData) editor.RemoveXfa();
            if (removesDocumentJavaScript) editor.ClearDocumentJavaScript();
            if (removesPageThumbnails)
                for (int pageIndex = 0; pageIndex < PdfPageTree.Read(formSanitized).Pages.Count; pageIndex++)
                    editor.ClearPageThumbnail(pageIndex);
            formSanitized = PdfOptimizer.Reopen(formSanitized, editor.Build());
        }
        if (!removesAttachments && !removesComments) return formSanitized;
        var annotationEditor = new PdfIncrementalAnnotationEditor(formSanitized);
        IEnumerable<(int PageIndex, int AnnotationIndex)> annotationRemovals = [];
        if (removesAttachments)
            annotationRemovals = annotationRemovals.Concat(
                PdfPageTree.Read(formSanitized).Pages.SelectMany((_, pageIndex) =>
                    PdfAttachmentReader.ReadPageAnnotations(formSanitized, pageIndex))
                .Select(attachment =>
                    (attachment.PageIndex, attachment.AnnotationIndex)));
        if (removesComments)
            annotationRemovals = annotationRemovals.Concat(
                PdfCommentReader.Read(formSanitized).Select(comment =>
                    (comment.PageIndex, comment.AnnotationIndex)));
        (int PageIndex, int AnnotationIndex)[] removalTargets = [.. annotationRemovals
            .Distinct()
            .OrderByDescending(comment => comment.PageIndex)
            .ThenByDescending(comment => comment.AnnotationIndex)];
        foreach ((int pageIndex, int annotationIndex) in removalTargets)
            annotationEditor.RemoveAnnotationAt(pageIndex, annotationIndex);
        PdfDocument annotationsRemoved = removalTargets.Length == 0
            ? formSanitized : PdfOptimizer.Reopen(formSanitized, annotationEditor.Build());
        return removesAttachments ? PdfEmbeddedFileReach.Strip(annotationsRemoved) : annotationsRemoved;
    }
}

/// <summary>Previews deterministic lossless structural optimization and metadata sanitization.</summary>
public static class PdfOptimizer
{
    private static readonly PdfName InformationName = Name("Info");
    private static readonly PdfName MetadataName = Name("Metadata");
    private static readonly PdfName OpenActionName = Name("OpenAction");
    private static readonly PdfName OutlinesName = Name("Outlines");
    private static readonly PdfName NamesName = Name("Names");
    private static readonly PdfName JavaScriptName = Name("JavaScript");

    /// <summary>Creates an explainable plan without changing the document.</summary>
    public static PdfOptimizationPlan CreatePlan(PdfDocument document,
        PdfOptimizationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfOptimizationOptions();
        PdfDocument original = document;
        IReadOnlyList<PdfReferenceRepair> referenceRepairs = [];
        if (options.ReplaceDanglingReferences && PdfDocumentWriter.FindReferenceRepairs(document).Count > 0)
        {
            if (document.IsEncrypted)
                throw new NotSupportedException("Repairing references before optimization requires a decrypted working copy.");
            PdfDocumentWriteResult normalized = PdfDocumentWriter.WriteWithReport(document,
                new PdfDocumentWriteOptions
                {
                    ReplaceDanglingReferences = true,
                    AllowSignatureInvalidation = options.AllowSignatureInvalidation
                }, dropUnreadable: true);
            referenceRepairs = normalized.ReferenceRepairs;
            document = PdfDocument.Open(normalized.Data);
        }
        var changes = new List<PdfOptimizationChangeKind> { PdfOptimizationChangeKind.ConsolidateRevisions };
        PdfSaveRepairChange[] repairs = options.RepairHarmlessArtifacts
            ? [.. PdfSaveSanitizer.CreateRepairPlan(document).Changes] : [];
        if (repairs.Length > 0)
            changes.Add(PdfOptimizationChangeKind.RepairHarmlessArtifacts);
        PdfPageTree tree = PdfPageTree.Read(document);
        PdfAttachmentInfo[] attachments = options.RemoveAttachments
            ? [.. PdfAttachmentReader.Read(document)] : [];
        string[] attachmentKeys = [.. attachments.Select(attachment => attachment.AttachmentKey!)];
        // The name tree is one way to reach a file, so a file only an /AF entry or an action names
        // is found by walking the document, and the preview lists it with the others.
        IReadOnlyDictionary<int, string?> reachableFiles = options.RemoveAttachments
            ? PdfEmbeddedFileReach.Reachable(document) : new Dictionary<int, string?>();
        HashSet<int> listedStreams = options.RemoveAttachments
            ? [.. PdfEmbeddedFileReach.ListedStreams(document).Values.SelectMany(streams => streams)] : [];
        string[] attachmentNames = [.. attachments.Select(attachment => attachment.FileName)
            .Concat(reachableFiles.Where(file => !listedStreams.Contains(file.Key) && file.Value is not null)
                .Select(file => file.Value!).Distinct(StringComparer.Ordinal))];
        bool hasAttachmentAnnotations = options.RemoveAttachments
            && tree.Pages.SelectMany((_, pageIndex) =>
                PdfAttachmentReader.ReadPageAnnotations(document, pageIndex)).Any();
        string[] formFieldNames = options.RemoveFormFields
            ? [.. tree.Pages.SelectMany((_, pageIndex) => PdfFormWidgetReader.ReadPage(document, pageIndex))
                .Select(widget => widget.FieldName).Distinct(StringComparer.Ordinal)] : [];
        (int PageIndex, int AnnotationIndex)[] comments = options.RemoveComments
            ? [.. PdfCommentReader.Read(document)
                .Select(comment => (comment.PageIndex, comment.AnnotationIndex))] : [];
        if (options.RemoveMetadata && (document.Trailer.ContainsKey(InformationName)
            || tree.Catalog.ContainsKey(MetadataName)))
            changes.Add(PdfOptimizationChangeKind.RemoveMetadata);
        if (attachmentNames.Length > 0 || hasAttachmentAnnotations || reachableFiles.Count > 0)
            changes.Add(PdfOptimizationChangeKind.RemoveAttachments);
        if (options.RemoveOpenAction && tree.Catalog.ContainsKey(OpenActionName))
            changes.Add(PdfOptimizationChangeKind.RemoveOpenAction);
        if (options.RemoveBookmarks && tree.Catalog.ContainsKey(OutlinesName))
            changes.Add(PdfOptimizationChangeKind.RemoveBookmarks);
        if (formFieldNames.Length > 0)
            changes.Add(PdfOptimizationChangeKind.RemoveFormFields);
        if (options.RemoveXfaData && PdfXfaReader.Read(document) is not null)
            changes.Add(PdfOptimizationChangeKind.RemoveXfaData);
        if (comments.Length > 0)
            changes.Add(PdfOptimizationChangeKind.RemoveComments);
        bool hasJavaScriptNameTree = tree.Catalog.TryGetValue(
                NamesName, out PdfObject? namesValue)
            && Resolve(document, namesValue) is PdfDictionary names
            && names.ContainsKey(JavaScriptName);
        if (options.RemoveDocumentJavaScript
            && (hasJavaScriptNameTree || HasJavaScriptActions(document)))
            changes.Add(PdfOptimizationChangeKind.RemoveDocumentJavaScript);
        int[] thumbnailPages = options.RemovePageThumbnails
            ? [.. tree.Pages.Where(page => page.Dictionary.ContainsKey(Name("Thumb")))
                .Select(page => page.Index)] : [];
        if (thumbnailPages.Length > 0)
            changes.Add(PdfOptimizationChangeKind.RemovePageThumbnails);
        PdfOptionalContentGroupInfo[] optionalContentGroups = options.FlattenOptionalContent
            ? [.. PdfOptionalContentReader.Read(document).Groups
                .OrderBy(group => group.Name, StringComparer.Ordinal)
                .ThenBy(group => group.ObjectNumber)] : [];
        if (optionalContentGroups.Length > 0)
            changes.Add(PdfOptimizationChangeKind.FlattenOptionalContent);
        int[] resourcePages = options.PruneUnusedPageResources
            ? [.. UnusedResourcePages(document, includeAliases: !IsTagged(document))] : [];
        if (resourcePages.Length > 0 || options.PruneUnusedPageResources
            && PdfDocumentWriter.CountDuplicatePageResourceObjects(document) > 0)
            changes.Add(PdfOptimizationChangeKind.PruneUnusedPageResources);
        int[] unreachableObjectNumbers = options.PruneUnreachableObjects
            ? [.. PdfDocumentWriter.UnreachableObjectNumbers(document)] : [];
        if (unreachableObjectNumbers.Length > 0)
            changes.Add(PdfOptimizationChangeKind.PruneUnreachableObjects);
        int[] compressedStreamObjectNumbers = options.CompressUnfilteredStreams
            ? [.. PdfDocumentWriter.CompressibleStreamObjectNumbers(document)] : [];
        if (compressedStreamObjectNumbers.Length > 0)
            changes.Add(PdfOptimizationChangeKind.CompressUnfilteredStreams);
        if (options.PackObjects) changes.Add(PdfOptimizationChangeKind.PackObjects);
        if (options.CompressStructure) changes.Add(PdfOptimizationChangeKind.CompressStructure);
        return new PdfOptimizationPlan(document, options, changes, attachmentNames,
            formFieldNames, resourcePages, thumbnailPages,
            optionalContentGroups.Select(group => group.Name),
            optionalContentGroups.Where(group => !group.IsInitiallyVisible)
                .Select(group => group.Name),
            repairs, comments.Length, unreachableObjectNumbers,
            compressedStreamObjectNumbers, original, referenceRepairs, attachmentKeys);
    }

    // A revision built from a document is read the way that document was, so what recovery read from
    // the source stays readable through every step.
    internal static PdfDocument Reopen(PdfDocument source, ReadOnlyMemory<byte> revision) =>
        source.UsesCompatibilityRecovery
            ? PdfDocument.OpenWithCompatibilityRecovery(revision) : PdfDocument.Open(revision);

    internal static bool IsTagged(PdfDocument document) =>
        PdfPageTree.Read(document).Catalog.ContainsKey(Name("StructTreeRoot"));

    internal static IReadOnlyList<int> UnusedResourcePages(PdfDocument document, bool includeAliases = true,
        PdfResourceFingerprints? fingerprints = null)
    {
        fingerprints ??= new PdfResourceFingerprints();
        PdfPageTree tree = PdfPageTree.Read(document);
        var reader = new PdfPageContentReader(document);
        var result = new List<int>();
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            if (!page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? value)
                || Resolve(document, value) is not PdfDictionary resources)
                continue;
            IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions;
            // Content that cannot be read proves nothing about which names it uses.
            try { instructions = NamingDefaultColorSpaces(document, resources, reader.ReadInstructions(page.Index)); }
            catch (Exception error) when (PdfReaderDamage.Is(error)) { continue; }
            if (HasIndirectResourceConsumers(document, resources, instructions, page.Dictionary)) continue;
            HashSet<PdfName> fonts = [.. instructions
                .Where(item => item.Operator == "Tf" && item.Operands.Count > 0)
                .Select(item => item.Operands[0]).OfType<PdfName>()];
            HashSet<PdfName> xObjects = [.. instructions
                .Where(item => item.Operator == "Do" && item.Operands.Count > 0)
                .Select(item => item.Operands[0]).OfType<PdfName>()];
            HashSet<PdfName> colorSpaces = [.. instructions
                .Where(item => item.Operator is "CS" or "cs" && item.Operands.Count > 0)
                .Select(item => item.Operands[0]).OfType<PdfName>()];
            HashSet<PdfName> graphicsStates = UsedNames(instructions, "gs", 0);
            HashSet<PdfName> shadings = UsedNames(instructions, "sh", 0);
            HashSet<PdfName> patterns = [.. instructions
                .Where(item => item.Operator is "SCN" or "scn"
                    && item.Operands.Count > 0 && item.Operands[^1] is PdfName)
                .Select(item => item.Operands[^1]).OfType<PdfName>()];
            HashSet<PdfName> properties = [.. instructions
                .Where(item => item.Operator is "BDC" or "DP" && item.Operands.Count > 1)
                .Select(item => item.Operands[1]).OfType<PdfName>()];
            if (NeedsCleanup("Font", fonts) || NeedsCleanup("XObject", xObjects)
                || NeedsCleanup("ColorSpace", colorSpaces)
                || NeedsCleanup("ExtGState", graphicsStates)
                || NeedsCleanup("Shading", shadings)
                || NeedsCleanup("Pattern", patterns)
                || NeedsCleanup("Properties", properties))
                result.Add(page.Index);

            bool NeedsCleanup(string category, HashSet<PdfName> used) =>
                resources.TryGetValue(Name(category), out PdfObject? categoryValue)
                && Resolve(document, categoryValue) is PdfDictionary dictionary
                && (dictionary.Keys.Any(key => !used.Contains(key))
                    || includeAliases && DuplicateAliases(document, dictionary, fingerprints).Count > 0);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    // Device color operators paint through a default color space and no operator names it, so the
    // cleanup is handed a use of each one to keep. These instructions are never written to the page.
    internal static IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> NamingDefaultColorSpaces(
        PdfDocument document, int pageIndex,
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions) =>
        PdfPageTree.Read(document).Pages[pageIndex].InheritedValues.TryGetValue(Name("Resources"), out PdfObject? value)
            && Resolve(document, value) is PdfDictionary resources
            ? NamingDefaultColorSpaces(document, resources, instructions) : instructions;

    private static IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> NamingDefaultColorSpaces(
        PdfDocument document, PdfDictionary resources,
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions)
    {
        PdfName[] defaults = [.. DefaultColorSpaces(document, resources)];
        return defaults.Length == 0 ? instructions : Array.AsReadOnly(instructions.Concat(defaults.Select(
            space => new KillerPdf.Engine.Parsing.PdfContentInstruction("cs", 0, [space]))).ToArray());
    }

    private static IEnumerable<PdfName> DefaultColorSpaces(PdfDocument document, PdfDictionary resources) =>
        resources.TryGetValue(Name("ColorSpace"), out PdfObject? value)
        && Resolve(document, value) is PdfDictionary spaces
            ? spaces.Keys.Where(key => key.ValueAsLatin1() is "DefaultGray" or "DefaultRGB" or "DefaultCMYK") : [];

    internal static bool HasIndirectResourceConsumers(PdfDocument document,
        PdfDictionary resources,
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions,
        PdfDictionary? page = null)
    {
        // Forms, Type 3 fonts, tiling patterns, soft masks and annotation appearances can
        // inherit page resources, and inline image dictionaries can name them without a
        // separate cs operator. Preserve names the page-only scan and alias rewriter cannot
        // track, including those used in nested forms. A default color space keeps the page
        // whole too unless the caller has named it, as NamingDefaultColorSpaces does.
        if (PdfInheritedResources.ConsumesPageResources(document, resources, page)
            || DefaultColorSpaces(document, resources).Any(space => !instructions.Any(instruction =>
                instruction.Operator == "cs" && instruction.Operands.Count > 0
                && space.Equals(instruction.Operands[0]))))
            return true;
        return instructions.Any(instruction => UsesNamedInlineColorSpace(instruction)
            || instruction.Operator == "Do"
            && instruction.Operands.Count > 0
            && instruction.Operands[0] is PdfName name
            && resources.TryGetValue(Name("XObject"), out PdfObject? category)
            && Resolve(document, category) is PdfDictionary xObjects
            && xObjects.TryGetValue(name, out PdfObject? value)
            && Resolve(document, value) is PdfStream stream
            && stream.Dictionary.TryGetValue(Name("Subtype"), out PdfObject? subtype)
            && Resolve(document, subtype).Equals(Name("Form")));

        static bool UsesNamedInlineColorSpace(
            KillerPdf.Engine.Parsing.PdfContentInstruction instruction)
        {
            if (instruction.Operator != "BI" || instruction.Operands.Count == 0
                || instruction.Operands[0] is not PdfDictionary dictionary) return false;
            if (!dictionary.TryGetValue(Name("ColorSpace"), out PdfObject? value))
                dictionary.TryGetValue(Name("CS"), out value);
            return value is PdfArray || value is PdfName name && name.ValueAsLatin1() is not
                ("DeviceGray" or "G" or "DeviceRGB" or "RGB" or "DeviceCMYK" or "CMYK");
        }
    }

    internal static bool HasJavaScriptActions(PdfDocument document)
    {
        // An explicit stack keeps a long /Next chain from becoming a deep call chain.
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        var pending = new Stack<PdfObject>();
        pending.Push(document.Trailer[new PdfName("Root"u8)]);
        while (pending.Count > 0)
        {
            PdfObject value = pending.Pop();
            if (value is PdfIndirectReference reference)
            {
                if (!visited.Add((reference.ObjectNumber, reference.Generation))) continue;
                value = document.Resolve(reference);
            }
            if (IsJavaScriptAction(document, value)) return true;
            switch (value)
            {
                case PdfDictionary dictionary:
                    foreach (PdfObject item in dictionary.Values) pending.Push(item);
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array) pending.Push(item);
                    break;
                case PdfStream stream:
                    foreach (PdfObject item in stream.Dictionary.Values) pending.Push(item);
                    break;
            }
        }
        return false;
    }

    internal static PdfDocument RemoveJavaScriptActions(PdfDocument document)
    {
        var update = new PdfIncrementalUpdateBuilder(document);
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        bool changed = false;
        // Each indirect object is sanitized on its own, so they are queued instead of
        // recursed into and a long /Next chain stays off the call stack.
        var pending = new Stack<PdfIndirectReference>();
        Visit(document.Trailer[new PdfName("Root"u8)]);
        while (pending.Count > 0)
        {
            PdfIndirectReference reference = pending.Pop();
            PdfObject resolved = document.Resolve(reference);
            PdfObject sanitized = VisitDirect(resolved);
            if (!ReferenceEquals(resolved, sanitized))
            {
                update.ReplaceObject(reference.ObjectNumber, sanitized);
                changed = true;
            }
        }
        return changed ? Reopen(document, update.Build()) : document;

        PdfObject Visit(PdfObject value)
        {
            if (value is PdfIndirectReference reference)
            {
                if (visited.Add((reference.ObjectNumber, reference.Generation)))
                    pending.Push(reference);
                return reference;
            }
            return VisitDirect(value);
        }

        PdfObject VisitDirect(PdfObject value)
        {
            if (value is PdfDictionary dictionary)
            {
                var entries = new List<KeyValuePair<PdfName, PdfObject>>(dictionary.Count);
                bool localChange = false;
                foreach ((PdfName key, PdfObject item) in dictionary)
                {
                    if (IsJavaScriptAction(document, item))
                    {
                        localChange = true;
                        continue;
                    }
                    PdfObject sanitized = Visit(item);
                    entries.Add(new KeyValuePair<PdfName, PdfObject>(key, sanitized));
                    localChange |= !ReferenceEquals(item, sanitized);
                }
                return localChange ? new PdfDictionary(entries) : dictionary;
            }
            if (value is PdfArray array)
            {
                var items = new List<PdfObject>(array.Count);
                bool localChange = false;
                foreach (PdfObject item in array)
                {
                    if (IsJavaScriptAction(document, item))
                    {
                        localChange = true;
                        continue;
                    }
                    PdfObject sanitized = Visit(item);
                    items.Add(sanitized);
                    localChange |= !ReferenceEquals(item, sanitized);
                }
                return localChange ? new PdfArray(items) : array;
            }
            if (value is PdfStream stream)
            {
                PdfObject streamDictionary = VisitDirect(stream.Dictionary);
                return ReferenceEquals(streamDictionary, stream.Dictionary) ? stream
                    : new PdfStream((PdfDictionary)streamDictionary, stream.EncodedData.Span);
            }
            return value;
        }
    }

    private static bool IsJavaScriptAction(PdfDocument document, PdfObject value)
    {
        try
        {
            return Resolve(document, value) is PdfDictionary action
                && action.TryGetValue(new PdfName("S"u8), out PdfObject? type)
                && Resolve(document, type) is PdfName name
                && name.ValueAsLatin1() == "JavaScript";
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction>
        ConsolidateResourceAliases(PdfDocument document, int pageIndex,
            IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions,
            PdfResourceFingerprints fingerprints)
    {
        PdfPageTreeEntry page = PdfPageTree.Read(document).Pages[pageIndex];
        if (!page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? value)
            || Resolve(document, value) is not PdfDictionary resources)
            return instructions;
        Dictionary<PdfName, PdfName> fonts = Aliases("Font");
        Dictionary<PdfName, PdfName> xObjects = Aliases("XObject");
        Dictionary<PdfName, PdfName> colorSpaces = Aliases("ColorSpace");
        Dictionary<PdfName, PdfName> graphicsStates = Aliases("ExtGState");
        Dictionary<PdfName, PdfName> shadings = Aliases("Shading");
        Dictionary<PdfName, PdfName> patterns = Aliases("Pattern");
        Dictionary<PdfName, PdfName> properties = Aliases("Properties");
        return Array.AsReadOnly(instructions.Select(instruction =>
        {
            (Dictionary<PdfName, PdfName>? aliases, int operandIndex) = instruction.Operator switch
            {
                "Tf" => (fonts, 0),
                "Do" => (xObjects, 0),
                "CS" or "cs" => (colorSpaces, 0),
                "gs" => (graphicsStates, 0),
                "sh" => (shadings, 0),
                "SCN" or "scn" => (patterns, instruction.Operands.Count - 1),
                "BDC" or "DP" => (properties, 1),
                _ => (null, 0)
            };
            if (aliases is null || operandIndex < 0
                || instruction.Operands.Count <= operandIndex
                || instruction.Operands[operandIndex] is not PdfName name
                || !aliases.TryGetValue(name, out PdfName? canonical))
                return instruction;
            PdfObject[] operands = [.. instruction.Operands];
            operands[operandIndex] = canonical;
            return new KillerPdf.Engine.Parsing.PdfContentInstruction(
                instruction.Operator, instruction.Offset, operands,
                instruction.InlineImageData);
        }).ToArray());

        Dictionary<PdfName, PdfName> Aliases(string category)
        {
            if (!resources.TryGetValue(Name(category), out PdfObject? categoryValue)
                || Resolve(document, categoryValue) is not PdfDictionary dictionary)
                return [];
            return DuplicateAliases(document, dictionary, fingerprints);
        }
    }

    private static HashSet<PdfName> UsedNames(
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions,
        string operation, int operandIndex) => [.. instructions
            .Where(item => item.Operator == operation
                && item.Operands.Count > operandIndex)
            .Select(item => item.Operands[operandIndex]).OfType<PdfName>()];

    private static Dictionary<PdfName, PdfName> DuplicateAliases(
        PdfDocument document, PdfDictionary dictionary, PdfResourceFingerprints fingerprints)
    {
        var canonical = new Dictionary<string, PdfName>(StringComparer.Ordinal);
        var aliases = new Dictionary<PdfName, PdfName>();
        foreach ((PdfName name, PdfObject value) in dictionary)
        {
            PdfObject resource = Resolve(document, value);
            if (PdfDocumentWriter.IsOptionalContent(resource, item => Resolve(document, item))) continue;
            string identity = fingerprints.Of(resource);
            if (canonical.TryGetValue(identity, out PdfName? first))
                aliases[name] = first;
            else
                canonical[identity] = name;
        }
        return aliases;
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An indirect object chain contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }
}
