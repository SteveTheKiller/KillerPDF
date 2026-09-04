using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;

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
}

/// <summary>A completed optimization and its measured size change.</summary>
public sealed record PdfOptimizationResult(ReadOnlyMemory<byte> Data, int OriginalSize,
    int OutputSize, IReadOnlyList<PdfOptimizationChangeKind> Changes)
{
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

    /// <summary>Serializes measured results without embedding the output PDF bytes.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        OriginalSize,
        OutputSize,
        SizeDifference,
        OriginalObjectCount,
        OutputObjectCount,
        ObjectCountDifference,
        Changes,
        VerifiedRemovals,
        Repairs
    }, new JsonSerializerOptions
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    });
}

/// <summary>An immutable preview of a deterministic full-document optimization.</summary>
public sealed class PdfOptimizationPlan
{
    private readonly PdfDocument _document;
    private readonly PdfOptimizationOptions _options;
    private readonly string[] _attachmentNames;
    private readonly string[] _formFieldNames;
    private readonly int[] _resourcePages;
    private readonly int[] _thumbnailPages;
    private readonly string[] _optionalContentGroupNames;
    private readonly string[] _hiddenOptionalContentGroupNames;
    private readonly PdfSaveRepairChange[] _repairs;
    private readonly int _commentCount;

    internal PdfOptimizationPlan(PdfDocument document, PdfOptimizationOptions options,
        IEnumerable<PdfOptimizationChangeKind> changes, IEnumerable<string> attachmentNames,
        IEnumerable<string> formFieldNames, IEnumerable<int> resourcePages,
        IEnumerable<int> thumbnailPages, IEnumerable<string> optionalContentGroupNames,
        IEnumerable<string> hiddenOptionalContentGroupNames,
        IEnumerable<PdfSaveRepairChange> repairs, int commentCount)
    {
        _document = document;
        _options = options;
        _attachmentNames = attachmentNames.ToArray();
        _formFieldNames = formFieldNames.ToArray();
        _resourcePages = resourcePages.ToArray();
        _thumbnailPages = thumbnailPages.ToArray();
        _optionalContentGroupNames = optionalContentGroupNames.ToArray();
        _hiddenOptionalContentGroupNames = hiddenOptionalContentGroupNames.ToArray();
        _repairs = repairs.ToArray();
        _commentCount = commentCount;
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    /// <summary>Gets the original byte count.</summary>
    public int OriginalSize => _document.Source.Length;
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
    /// <summary>Gets layer names whose optional-content wrappers will be flattened.</summary>
    public IReadOnlyList<string> OptionalContentGroupNames =>
        Array.AsReadOnly(_optionalContentGroupNames);
    /// <summary>Gets initially hidden layer names whose content will be removed.</summary>
    public IReadOnlyList<string> HiddenOptionalContentGroupNames =>
        Array.AsReadOnly(_hiddenOptionalContentGroupNames);
    /// <summary>Gets structural repairs that the plan will apply.</summary>
    public IReadOnlyList<PdfSaveRepairChange> Repairs => Array.AsReadOnly(_repairs);

    /// <summary>Serializes the complete preview without changing the document.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        OriginalSize,
        Changes,
        AttachmentNames,
        FormFieldNames,
        CommentCount,
        ResourcePageIndexes,
        ThumbnailPageIndexes,
        OptionalContentGroupNames,
        HiddenOptionalContentGroupNames,
        Repairs
    }, new JsonSerializerOptions
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    });

    /// <summary>Applies the previewed plan and verifies that the result reopens with the same page count.</summary>
    public PdfOptimizationResult Apply()
    {
        int pageCount = PdfPageTree.Read(_document).Pages.Count;
        PdfDocument source = _repairs.Length == 0 ? _document
            : PdfDocument.Open(PdfSaveSanitizer.ApplyPlan(_document, _repairs));
        source = ApplySelectiveSanitization(source);
        if (Changes.Contains(PdfOptimizationChangeKind.RemoveDocumentJavaScript))
            source = PdfOptimizer.RemoveJavaScriptActions(source);
        byte[] output = PdfDocumentWriter.Write(source, new PdfDocumentWriteOptions
        {
            MetadataPolicy = _options.RemoveMetadata
                ? PdfMetadataPolicy.RemoveDocumentInformationAndXmp : PdfMetadataPolicy.Preserve,
            CrossReferenceFormat = _options.PackObjects || _options.CompressStructure
                ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table,
            UseObjectStreams = _options.PackObjects,
            CompressStructuralStreams = _options.CompressStructure,
            PruneUnreachableObjects = _options.PruneUnreachableObjects,
            CompressUnfilteredStreams = _options.CompressUnfilteredStreams,
            AllowSignatureInvalidation = _options.AllowSignatureInvalidation
        });
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
            OriginalObjectCount = ActiveObjectCount(_document),
            OutputObjectCount = ActiveObjectCount(reopened)
        };
    }

    private static int ActiveObjectCount(PdfDocument document) =>
        document.CrossReferences.Values.Count(entry =>
            entry.Type is CrossReference.PdfCrossReferenceEntryType.InUse
                or CrossReference.PdfCrossReferenceEntryType.Compressed);

    private IReadOnlyList<PdfOptimizationChangeKind> VerifySanitization(PdfDocument document)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        var verified = new List<PdfOptimizationChangeKind>();
        Verify(PdfOptimizationChangeKind.RemoveMetadata,
            !document.Trailer.ContainsKey(new PdfName("Info"u8))
            && !tree.Catalog.ContainsKey(new PdfName("Metadata"u8)));
        Verify(PdfOptimizationChangeKind.RemoveAttachments,
            PdfAttachmentReader.Read(document).Count == 0
            && tree.Pages.SelectMany((_, pageIndex) =>
                PdfAttachmentReader.ReadPageAnnotations(document, pageIndex)).Any() == false);
        Verify(PdfOptimizationChangeKind.RemoveOpenAction,
            !tree.Catalog.ContainsKey(new PdfName("OpenAction"u8)));
        Verify(PdfOptimizationChangeKind.RemoveBookmarks,
            !tree.Catalog.ContainsKey(new PdfName("Outlines"u8)));
        Verify(PdfOptimizationChangeKind.RemoveFormFields,
            tree.Pages.SelectMany((_, pageIndex) =>
                PdfFormWidgetReader.ReadPage(document, pageIndex)).Any() == false);
        Verify(PdfOptimizationChangeKind.RemoveXfaData,
            PdfXfaReader.Read(document) is null);
        Verify(PdfOptimizationChangeKind.RemoveComments,
            PdfCommentReader.Read(document).Count == 0);
        bool hasJavaScript = tree.Catalog.TryGetValue(new PdfName("Names"u8),
            out PdfObject? namesValue)
            && Resolve(document, namesValue) is PdfDictionary names
            && names.ContainsKey(new PdfName("JavaScript"u8));
        Verify(PdfOptimizationChangeKind.RemoveDocumentJavaScript,
            !hasJavaScript && !PdfOptimizer.HasJavaScriptActions(document));
        Verify(PdfOptimizationChangeKind.RemovePageThumbnails,
            tree.Pages.All(page => !page.Dictionary.ContainsKey(new PdfName("Thumb"u8))));
        Verify(PdfOptimizationChangeKind.FlattenOptionalContent,
            PdfOptionalContentReader.Read(document).Groups.Count == 0);
        Verify(PdfOptimizationChangeKind.PruneUnusedPageResources,
            PdfOptimizer.UnusedResourcePages(document).Count == 0);
        Verify(PdfOptimizationChangeKind.PruneUnreachableObjects,
            PdfDocumentWriter.CountUnreachableObjects(document) == 0);
        Verify(PdfOptimizationChangeKind.CompressUnfilteredStreams,
            PdfDocumentWriter.CountCompressibleStreams(document) == 0);
        return Array.AsReadOnly(verified.ToArray());

        void Verify(PdfOptimizationChangeKind kind, bool absent)
        {
            if (!Changes.Contains(kind)) return;
            if (!absent)
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
            PdfOptimizationChangeKind.PruneUnusedPageResources);
        if (!removesAttachments && !removesOpenAction && !removesBookmarks
            && !removesFormFields && !removesXfaData && !removesComments && !removesDocumentJavaScript
            && !removesPageThumbnails && !flattensOptionalContent
            && !prunesUnusedResources)
            return document;
        PdfDocument formSanitized = flattensOptionalContent
            ? PdfDocument.Open(PdfOptionalContentEditor.FlattenPageContent(document))
            : document;
        if (_attachmentNames.Length > 0 || removesOpenAction || removesBookmarks || removesFormFields
            || removesXfaData
            || removesDocumentJavaScript || removesPageThumbnails || prunesUnusedResources)
        {
            var editor = new PdfIncrementalPageEditor(formSanitized);
            if (prunesUnusedResources)
            {
                var content = new PdfPageContentReader(formSanitized);
                foreach (int pageIndex in _resourcePages)
                    editor.SetPageContentAndPruneResources(
                        pageIndex, PdfOptimizer.ConsolidateResourceAliases(
                            formSanitized, pageIndex,
                            content.ReadInstructions(pageIndex)));
            }
            if (removesAttachments)
                foreach (string name in _attachmentNames) editor.RemoveAttachment(name);
            if (removesOpenAction) editor.ClearOpenAction();
            if (removesBookmarks) editor.ClearBookmarks();
            if (removesFormFields)
                foreach (string name in _formFieldNames) editor.RemoveFormField(name);
            if (removesXfaData) editor.RemoveXfa();
            if (removesDocumentJavaScript) editor.ClearDocumentJavaScript();
            if (removesPageThumbnails)
                for (int pageIndex = 0; pageIndex < PdfPageTree.Read(formSanitized).Pages.Count; pageIndex++)
                    editor.ClearPageThumbnail(pageIndex);
            formSanitized = PdfDocument.Open(editor.Build());
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
        if (removalTargets.Length == 0) return formSanitized;
        foreach ((int pageIndex, int annotationIndex) in removalTargets)
            annotationEditor.RemoveAnnotationAt(pageIndex, annotationIndex);
        return PdfDocument.Open(annotationEditor.Build());
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
        var changes = new List<PdfOptimizationChangeKind> { PdfOptimizationChangeKind.ConsolidateRevisions };
        PdfSaveRepairChange[] repairs = options.RepairHarmlessArtifacts
            ? [.. PdfSaveSanitizer.CreateRepairPlan(document).Changes] : [];
        if (repairs.Length > 0)
            changes.Add(PdfOptimizationChangeKind.RepairHarmlessArtifacts);
        PdfPageTree tree = PdfPageTree.Read(document);
        string[] attachmentNames = options.RemoveAttachments
            ? [.. PdfAttachmentReader.Read(document).Select(attachment => attachment.FileName)] : [];
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
        if (attachmentNames.Length > 0 || hasAttachmentAnnotations)
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
            ? [.. UnusedResourcePages(document)] : [];
        if (resourcePages.Length > 0)
            changes.Add(PdfOptimizationChangeKind.PruneUnusedPageResources);
        if (options.PruneUnreachableObjects
            && PdfDocumentWriter.CountUnreachableObjects(document) > 0)
            changes.Add(PdfOptimizationChangeKind.PruneUnreachableObjects);
        if (options.CompressUnfilteredStreams
            && PdfDocumentWriter.CountCompressibleStreams(document) > 0)
            changes.Add(PdfOptimizationChangeKind.CompressUnfilteredStreams);
        if (options.PackObjects) changes.Add(PdfOptimizationChangeKind.PackObjects);
        if (options.CompressStructure) changes.Add(PdfOptimizationChangeKind.CompressStructure);
        return new PdfOptimizationPlan(document, options, changes, attachmentNames,
            formFieldNames, resourcePages, thumbnailPages,
            optionalContentGroups.Select(group => group.Name),
            optionalContentGroups.Where(group => !group.IsInitiallyVisible)
                .Select(group => group.Name),
            repairs, comments.Length);
    }

    internal static IReadOnlyList<int> UnusedResourcePages(PdfDocument document)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        var reader = new PdfPageContentReader(document);
        var result = new List<int>();
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            if (!page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? value)
                || Resolve(document, value) is not PdfDictionary resources)
                continue;
            IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions =
                reader.ReadInstructions(page.Index);
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
                    && item.Operands.LastOrDefault() is PdfName)
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
                    || DuplicateAliases(document, dictionary).Count > 0);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    internal static bool HasJavaScriptActions(PdfDocument document)
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        return Visit(document.Trailer[new PdfName("Root"u8)]);

        bool Visit(PdfObject value)
        {
            if (value is PdfIndirectReference reference)
            {
                if (!visited.Add((reference.ObjectNumber, reference.Generation))) return false;
                value = document.Resolve(reference);
            }
            if (IsJavaScriptAction(document, value)) return true;
            return value switch
            {
                PdfDictionary dictionary => dictionary.Values.Any(Visit),
                PdfArray array => array.Any(Visit),
                PdfStream stream => Visit(stream.Dictionary),
                _ => false
            };
        }
    }

    internal static PdfDocument RemoveJavaScriptActions(PdfDocument document)
    {
        var update = new PdfIncrementalUpdateBuilder(document);
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        bool changed = false;
        Visit(document.Trailer[new PdfName("Root"u8)]);
        return changed ? PdfDocument.Open(update.Build()) : document;

        PdfObject Visit(PdfObject value)
        {
            if (value is PdfIndirectReference reference)
            {
                if (!visited.Add((reference.ObjectNumber, reference.Generation))) return reference;
                PdfObject resolved = document.Resolve(reference);
                PdfObject sanitized = VisitDirect(resolved);
                if (!ReferenceEquals(resolved, sanitized))
                {
                    update.ReplaceObject(reference.ObjectNumber, sanitized);
                    changed = true;
                }
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
            IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions)
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
            PdfObject[] operands = instruction.Operands.ToArray();
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
            return DuplicateAliases(document, dictionary);
        }
    }

    private static HashSet<PdfName> UsedNames(
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions,
        string operation, int operandIndex) => [.. instructions
            .Where(item => item.Operator == operation
                && item.Operands.Count > operandIndex)
            .Select(item => item.Operands[operandIndex]).OfType<PdfName>()];

    private static Dictionary<PdfName, PdfName> DuplicateAliases(
        PdfDocument document, PdfDictionary dictionary)
    {
        var canonical = new Dictionary<string, PdfName>(StringComparer.Ordinal);
        var aliases = new Dictionary<PdfName, PdfName>();
        foreach ((PdfName name, PdfObject value) in dictionary)
        {
            string identity = Convert.ToHexString(PdfObjectWriter.Write(
                Resolve(document, value)));
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
