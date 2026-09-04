using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Writing;

/// <summary>One material change proposed by a document optimization plan.</summary>
public enum PdfOptimizationChangeKind
{
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
    /// <summary>Remove every annotation that contains review text.</summary>
    RemoveComments,
    /// <summary>Remove the document-level JavaScript name tree.</summary>
    RemoveDocumentJavaScript,
    /// <summary>Remove embedded page thumbnail images.</summary>
    RemovePageThumbnails,
    /// <summary>Preserve initially visible optional content and remove hidden layer content.</summary>
    FlattenOptionalContent,
    /// <summary>Remove unreferenced and duplicate page font and XObject resources.</summary>
    PruneUnusedPageResources,
    /// <summary>Write eligible objects into compressed object streams.</summary>
    PackObjects,
    /// <summary>Compress structural streams.</summary>
    CompressStructure
}

/// <summary>Explicit lossless optimization and sanitization choices.</summary>
public sealed record PdfOptimizationOptions
{
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
    /// <summary>Gets whether annotations containing review text are removed.</summary>
    public bool RemoveComments { get; init; }
    /// <summary>Gets whether the document-level JavaScript name tree is removed.</summary>
    public bool RemoveDocumentJavaScript { get; init; }
    /// <summary>Gets whether embedded page thumbnail images are removed.</summary>
    public bool RemovePageThumbnails { get; init; }
    /// <summary>Gets whether initially visible optional content is flattened and hidden content removed.</summary>
    public bool FlattenOptionalContent { get; init; }
    /// <summary>Gets whether unreferenced and duplicate page font and XObject resources are removed.</summary>
    public bool PruneUnusedPageResources { get; init; }
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
        VerifiedRemovals
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

    internal PdfOptimizationPlan(PdfDocument document, PdfOptimizationOptions options,
        IEnumerable<PdfOptimizationChangeKind> changes, IEnumerable<string> attachmentNames,
        IEnumerable<string> formFieldNames, IEnumerable<int> resourcePages)
    {
        _document = document;
        _options = options;
        _attachmentNames = attachmentNames.ToArray();
        _formFieldNames = formFieldNames.ToArray();
        _resourcePages = resourcePages.ToArray();
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    /// <summary>Gets the original byte count.</summary>
    public int OriginalSize => _document.Source.Length;
    /// <summary>Gets every material change in application order.</summary>
    public IReadOnlyList<PdfOptimizationChangeKind> Changes { get; }

    /// <summary>Applies the previewed plan and verifies that the result reopens with the same page count.</summary>
    public PdfOptimizationResult Apply()
    {
        int pageCount = PdfPageTree.Read(_document).Pages.Count;
        PdfDocument source = ApplySelectiveSanitization();
        byte[] output = PdfDocumentWriter.Write(source, new PdfDocumentWriteOptions
        {
            MetadataPolicy = _options.RemoveMetadata
                ? PdfMetadataPolicy.RemoveDocumentInformationAndXmp : PdfMetadataPolicy.Preserve,
            CrossReferenceFormat = _options.PackObjects || _options.CompressStructure
                ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table,
            UseObjectStreams = _options.PackObjects,
            CompressStructuralStreams = _options.CompressStructure,
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
        Verify(PdfOptimizationChangeKind.RemoveComments,
            PdfCommentReader.Read(document).Count == 0);
        bool hasJavaScript = tree.Catalog.TryGetValue(new PdfName("Names"u8),
            out PdfObject? namesValue)
            && Resolve(document, namesValue) is PdfDictionary names
            && names.ContainsKey(new PdfName("JavaScript"u8));
        Verify(PdfOptimizationChangeKind.RemoveDocumentJavaScript, !hasJavaScript);
        Verify(PdfOptimizationChangeKind.RemovePageThumbnails,
            tree.Pages.All(page => !page.Dictionary.ContainsKey(new PdfName("Thumb"u8))));
        Verify(PdfOptimizationChangeKind.FlattenOptionalContent,
            PdfOptionalContentReader.Read(document).Groups.Count == 0);
        Verify(PdfOptimizationChangeKind.PruneUnusedPageResources,
            PdfOptimizer.UnusedResourcePages(document).Count == 0);
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

    private PdfDocument ApplySelectiveSanitization()
    {
        bool removesAttachments = Changes.Contains(PdfOptimizationChangeKind.RemoveAttachments);
        bool removesOpenAction = Changes.Contains(PdfOptimizationChangeKind.RemoveOpenAction);
        bool removesBookmarks = Changes.Contains(PdfOptimizationChangeKind.RemoveBookmarks);
        bool removesFormFields = Changes.Contains(PdfOptimizationChangeKind.RemoveFormFields);
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
            && !removesFormFields && !removesComments && !removesDocumentJavaScript
            && !removesPageThumbnails && !flattensOptionalContent
            && !prunesUnusedResources)
            return _document;
        PdfDocument formSanitized = flattensOptionalContent
            ? PdfDocument.Open(PdfOptionalContentEditor.FlattenPageContent(_document))
            : _document;
        if (_attachmentNames.Length > 0 || removesOpenAction || removesBookmarks || removesFormFields
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
        if (comments.Length > 0)
            changes.Add(PdfOptimizationChangeKind.RemoveComments);
        if (options.RemoveDocumentJavaScript
            && tree.Catalog.TryGetValue(NamesName, out PdfObject? namesValue)
            && Resolve(document, namesValue) is PdfDictionary names
            && names.ContainsKey(JavaScriptName))
            changes.Add(PdfOptimizationChangeKind.RemoveDocumentJavaScript);
        if (options.RemovePageThumbnails
            && tree.Pages.Any(page => page.Dictionary.ContainsKey(Name("Thumb"))))
            changes.Add(PdfOptimizationChangeKind.RemovePageThumbnails);
        if (options.FlattenOptionalContent
            && PdfOptionalContentReader.Read(document).Groups.Count > 0)
            changes.Add(PdfOptimizationChangeKind.FlattenOptionalContent);
        int[] resourcePages = options.PruneUnusedPageResources
            ? [.. UnusedResourcePages(document)] : [];
        if (resourcePages.Length > 0)
            changes.Add(PdfOptimizationChangeKind.PruneUnusedPageResources);
        if (options.PackObjects) changes.Add(PdfOptimizationChangeKind.PackObjects);
        if (options.CompressStructure) changes.Add(PdfOptimizationChangeKind.CompressStructure);
        return new PdfOptimizationPlan(document, options, changes, attachmentNames,
            formFieldNames, resourcePages);
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
            if (NeedsCleanup("Font", fonts) || NeedsCleanup("XObject", xObjects))
                result.Add(page.Index);

            bool NeedsCleanup(string category, HashSet<PdfName> used) =>
                resources.TryGetValue(Name(category), out PdfObject? categoryValue)
                && Resolve(document, categoryValue) is PdfDictionary dictionary
                && (dictionary.Keys.Any(key => !used.Contains(key))
                    || DuplicateAliases(dictionary).Count > 0);
        }
        return Array.AsReadOnly(result.ToArray());
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
        return Array.AsReadOnly(instructions.Select(instruction =>
        {
            Dictionary<PdfName, PdfName>? aliases = instruction.Operator switch
            {
                "Tf" => fonts,
                "Do" => xObjects,
                _ => null
            };
            if (aliases is null || instruction.Operands.Count == 0
                || instruction.Operands[0] is not PdfName name
                || !aliases.TryGetValue(name, out PdfName? canonical))
                return instruction;
            PdfObject[] operands = instruction.Operands.ToArray();
            operands[0] = canonical;
            return new KillerPdf.Engine.Parsing.PdfContentInstruction(
                instruction.Operator, instruction.Offset, operands,
                instruction.InlineImageData);
        }).ToArray());

        Dictionary<PdfName, PdfName> Aliases(string category)
        {
            if (!resources.TryGetValue(Name(category), out PdfObject? categoryValue)
                || Resolve(document, categoryValue) is not PdfDictionary dictionary)
                return [];
            return DuplicateAliases(dictionary);
        }
    }

    private static Dictionary<PdfName, PdfName> DuplicateAliases(PdfDictionary dictionary)
    {
        var canonical = new Dictionary<(int ObjectNumber, int Generation), PdfName>();
        var aliases = new Dictionary<PdfName, PdfName>();
        foreach ((PdfName name, PdfObject value) in dictionary)
        {
            if (value is not PdfIndirectReference reference) continue;
            var identity = (reference.ObjectNumber, reference.Generation);
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
