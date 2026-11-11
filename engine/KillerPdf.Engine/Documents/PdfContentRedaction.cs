using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Matrix = KillerPdf.Engine.Parsing.PdfTextContentReader.Matrix;

namespace KillerPdf.Engine.Documents;

/// <summary>How a redacted page is rewritten.</summary>
public enum PdfRedactionPageMethod
{
    /// <summary>Content under the regions is removed and the rest of the page stays editable and searchable.</summary>
    Content,
    /// <summary>The page cannot be edited safely and must be replaced by a sanitized image.</summary>
    Raster
}

/// <summary>
/// The redaction analysis for one page that has a region, or that hides content another page's
/// region covers and can only give it up by being replaced with an image.
/// </summary>
public sealed record PdfRedactionPageAnalysis(
    int PageIndex,
    PdfRedactionPageMethod Method,
    IReadOnlyList<string> RasterReasons,
    int RemovedGlyphs,
    int RemovedImages,
    int RemovedPaths,
    int RemovedAnnotations)
{
    /// <summary>Gets the number of partly covered images whose covered pixels are painted over.</summary>
    public int RedactedImages { get; init; }
}

/// <summary>The per-page redaction analysis for a set of regions.</summary>
public sealed record PdfContentRedactionPlan(IReadOnlyList<PdfRedactionPageAnalysis> Pages)
{
    /// <summary>
    /// Gets whether the document carries an XFA form packet, which the redaction removes because its
    /// datasets repeat every field value. The result keeps working as a plain AcroForm.
    /// </summary>
    public bool RemovesXfaForm { get; init; }
    /// <summary>Gets the zero-based pages that can only be redacted by replacing them with an image.</summary>
    public IReadOnlyList<int> RasterPages => Array.AsReadOnly(Pages
        .Where(page => page.Method == PdfRedactionPageMethod.Raster)
        .Select(page => page.PageIndex).ToArray());
}

/// <summary>Options for content-level redaction.</summary>
public sealed record PdfContentRedactionOptions
{
    /// <summary>
    /// Gets the pages the caller approved for image replacement. Every page the analysis marks as
    /// raster must be listed, including one with no region; listing a content page that has a
    /// region replaces it with an image as well.
    /// </summary>
    public IReadOnlyCollection<int> ApprovedRasterPages { get; init; } = [];
    /// <summary>Gets the resolution of replacement page images.</summary>
    public double RasterDpi { get; init; } = 200;
    /// <summary>Gets whether document information and XMP metadata are removed.</summary>
    public bool ClearDocumentMetadata { get; init; } = true;
    /// <summary>Gets whether signed signature fields may be invalidated by the rewrite.</summary>
    public bool AllowSignatureInvalidation { get; init; }
    /// <summary>Gets whether password encryption is removed from an authenticated source.</summary>
    public bool RemoveEncryption { get; init; }
    /// <summary>Gets text that must not remain anywhere in the output.</summary>
    public IReadOnlyList<string> ProhibitedText { get; init; } = [];
    /// <summary>Gets the font resolver used when rendering replacement page images.</summary>
    public IPdfFontResolver? FontResolver { get; init; }
}

/// <summary>A verified content-level redaction.</summary>
public sealed record PdfContentRedactionResult(
    ReadOnlyMemory<byte> Document,
    PdfContentRedactionPlan Plan,
    PdfRedactionVerificationReport Verification);

/// <summary>Raised when a rewritten document still exposes redacted content.</summary>
/// <param name="report">The failed verification report the exception is created from.</param>
public sealed class PdfRedactionVerificationException(PdfRedactionVerificationReport report)
    : InvalidOperationException("The redacted document failed verification." + Environment.NewLine + report.ToText())
{
    /// <summary>Gets the failed verification report.</summary>
    public PdfRedactionVerificationReport Report { get; } = report;
}

/// <summary>
/// Removes text, images, and drawings under redaction regions at the content level, replaces
/// only approved unsafe pages with sanitized images, and verifies the rewritten file.
/// </summary>
/// <remarks>Regions use unrotated, crop-relative PDF points, the coordinates of <see cref="PdfPageContentReader"/>.</remarks>
public static partial class PdfContentRedaction
{
    internal const string RedactionImageName = "KPRedactImage";
    private const int MaximumFieldDepth = 256;
    private const int MaximumStructureDepth = 256;
    // Each level of a structure tree is usually an element, an array and a reference, four frames at most.
    private const int MaximumStructureFrames = 4 * MaximumStructureDepth;
    /// <summary>The most a retained stream may decode to during prohibited-text inspection.</summary>
    internal static int InspectionDecodeLimit { get; set; } = 64 * 1024 * 1024;
    private static readonly PdfName ContentsName = Name("Contents");
    private static readonly PdfName ResourcesName = Name("Resources");
    private static readonly PdfName AnnotsName = Name("Annots");
    private static readonly PdfName RectName = Name("Rect");
    private static readonly PdfName SubtypeName = Name("Subtype");
    private static readonly PdfName PopupName = Name("Popup");
    private static readonly PdfName ParentName = Name("Parent");
    private static readonly PdfContentBounds EverythingDrawn = new(-1e7, -1e7, 1e7, 1e7);
    private static readonly PdfName InReplyToName = Name("IRT");
    private static readonly PdfName ReplyTypeName = Name("RT");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName FieldsName = Name("Fields");
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName XfaName = Name("XFA");
    private static readonly PdfName[] FieldValueKeys = [Name("V"), Name("DV"), Name("RV"), Name("I")];
    private static readonly PdfName StructTreeRootName = Name("StructTreeRoot");
    private static readonly PdfName MetadataName = Name("Metadata");
    private static readonly PdfName InfoName = Name("Info");
    private static readonly PdfName[] RemovedPageKeys = [Name("Thumb"), Name("PieceInfo"), Name("Metadata")];
    // /Contents is the form Acrobat's Header & Footer tool writes, and it repeats the page text.
    private static readonly PdfName[] ReplacementTextKeys = [Name("ActualText"), Name("Alt"), Name("E"), Name("Contents")];
    // A structure element also has a title. Marked-content descriptions have none, so it stays off the list above.
    private static readonly PdfName[] ElementTextKeys = [.. ReplacementTextKeys, Name("T")];
    private static readonly PdfName AttributesName = Name("A");
    private static readonly PdfName[] PrunedCategories =
        [Name("Font"), Name("XObject"), Name("ExtGState"), Name("ColorSpace"), Name("Pattern"), Name("Shading"), Name("Properties")];

    /// <summary>
    /// Determines, without changing the document, how each page with regions will be redacted, and
    /// which other pages hide what those regions cover and must become images to give it up.
    /// </summary>
    public static PdfContentRedactionPlan Analyze(PdfDocument document,
        IEnumerable<PdfRedactionRegion> regions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        return AnalyzePages(document, tree, ReachingPastTheCrop(document, tree, GroupRegions(regions, tree.Pages.Count)),
            cancellationToken, out _);
    }

    // A region over the whole visible page means the page, and a crop box only hides what lies past it:
    // that content would come back as soon as the crop was reset.
    private static Dictionary<int, PdfContentBounds[]> ReachingPastTheCrop(PdfDocument document, PdfPageTree tree,
        Dictionary<int, PdfContentBounds[]> byPage)
    {
        foreach (int pageIndex in byPage.Keys.ToArray())
        {
            PageGeometry geometry = Geometry(document, tree.Pages[pageIndex]);
            byPage[pageIndex] = [.. byPage[pageIndex].Select(bounds => WholePage(bounds, geometry))];
        }
        return byPage;
    }

    private static PdfContentBounds WholePage(PdfContentBounds bounds, PageGeometry geometry) =>
        bounds.Left <= 0.5 && bounds.Bottom <= 0.5
        && bounds.Right >= geometry.Width - 0.5 && bounds.Top >= geometry.Height - 0.5 ? EverythingDrawn : bounds;

    private static PdfContentRedactionPlan AnalyzePages(PdfDocument document, PdfPageTree tree,
        Dictionary<int, PdfContentBounds[]> byPage, CancellationToken cancellationToken, out RetiredContent retired)
    {
        var fonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        var pages = new List<PdfRedactionPageAnalysis>();
        retired = new RetiredContent(document, cancellationToken);
        foreach ((int pageIndex, PdfContentBounds[] pageRegions) in byPage.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageGeometry geometry = Geometry(document, tree.Pages[pageIndex]);
            var rewriter = new ContentRewriter(document, null, pageRegions, textOnly: false, fonts, cancellationToken,
                retired: retired);
            rewriter.RewritePage(tree.Pages[pageIndex], geometry);
            int annotations = IntersectingAnnotations(document, tree.Pages[pageIndex], geometry, pageRegions).Count;
            pages.Add(new PdfRedactionPageAnalysis(pageIndex,
                rewriter.Reasons.Count == 0 ? PdfRedactionPageMethod.Content : PdfRedactionPageMethod.Raster,
                Array.AsReadOnly(rewriter.Reasons.Order(StringComparer.Ordinal).ToArray()),
                rewriter.RemovedGlyphs, rewriter.RemovedImages, rewriter.RemovedPaths, annotations)
            {
                RedactedImages = rewriter.RedactedImages
            });
        }
        retired.Seal();
        // A page can hide what another page's regions cover, with no region of its own.
        HashSet<int> shared = SharedContentPages(document, tree, byPage, fonts, retired, cancellationToken);
        for (int index = 0; index < pages.Count; index++)
            if (shared.Remove(pages[index].PageIndex))
                pages[index] = pages[index] with
                {
                    Method = PdfRedactionPageMethod.Raster,
                    RasterReasons = Array.AsReadOnly(pages[index].RasterReasons.Append(SharedContentReason)
                        .Distinct().Order(StringComparer.Ordinal).ToArray())
                };
        pages.AddRange(shared.Select(pageIndex => new PdfRedactionPageAnalysis(pageIndex, PdfRedactionPageMethod.Raster,
            Array.AsReadOnly([SharedContentReason]), 0, 0, 0, 0)));
        return new PdfContentRedactionPlan(Array.AsReadOnly(pages.OrderBy(page => page.PageIndex).ToArray()))
        {
            RemovesXfaForm = HasXfaForm(document, tree)
        };
    }

    private static bool HasXfaForm(PdfDocument document, PdfPageTree tree) =>
        tree.Catalog.TryGetValue(AcroFormName, out PdfObject? value)
        && Resolve(document, value) is PdfDictionary acroForm && acroForm.ContainsKey(XfaName);

    /// <summary>Applies and verifies the redaction, returning a single-revision document.</summary>
    /// <exception cref="InvalidOperationException">A page needs image replacement that was not approved.</exception>
    /// <exception cref="PdfRedactionVerificationException">The rewritten document still exposes redacted content.</exception>
    public static PdfContentRedactionResult Apply(PdfDocument document,
        IEnumerable<PdfRedactionRegion> regions, PdfContentRedactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(regions);
        options ??= new PdfContentRedactionOptions();
        if (!double.IsFinite(options.RasterDpi) || options.RasterDpi < 36 || options.RasterDpi > 600)
            throw new ArgumentOutOfRangeException(nameof(options), "The raster resolution must be from 36 through 600 dpi.");
        if (document.IsEncrypted && !options.RemoveEncryption)
            throw new NotSupportedException("Redacting an encrypted document requires removing its encryption.");
        PdfRedactionRegion[] regionList = [.. regions];
        PdfPageTree tree = PdfPageTree.Read(document);
        Dictionary<int, PdfContentBounds[]> byPage = ReachingPastTheCrop(document, tree, GroupRegions(regionList, tree.Pages.Count));
        PdfContentRedactionPlan plan = AnalyzePages(document, tree, byPage, cancellationToken, out RetiredContent retired);
        var approved = new HashSet<int>(options.ApprovedRasterPages);
        int[] unapproved = [.. plan.RasterPages.Where(page => !approved.Contains(page))];
        if (unapproved.Length != 0)
            throw new InvalidOperationException("Pages " + string.Join(", ", unapproved.Select(page => page + 1))
                + " cannot be redacted without replacing them with an image.");
        // A page that hides another page's covered content can need its image with no region of its own.
        var planned = new HashSet<int>(plan.RasterPages);
        var rasterPages = new HashSet<int>(approved.Where(page => byPage.ContainsKey(page) || planned.Contains(page)));
        Dictionary<int, PdfRedactionRegion[]> covers = GroupCovers(regionList);
        foreach (int page in rasterPages)
            if (byPage.TryAdd(page, [])) covers[page] = [];

        PdfDocumentWriteResult written;
        IReadOnlyList<PdfReferenceRepair> preflightRepairs = [];
        var rasterHidden = new Dictionary<int, HiddenArea[]>();
        try { written = Rewrite(document, tree, byPage, covers, rasterPages, retired, options, rasterHidden, cancellationToken); }
        catch (Exception error) when (error is InvalidOperationException or FormatException
            or NotSupportedException)
        {
            // A damaged cross-reference table or object graph can block the incremental rewrite.
            // Normalize the source with a full rewrite and redact that copy instead.
            PdfDocument source = document.IsEncrypted ? document
                : PdfDocument.OpenWithCompatibilityRecovery(document.Source);
            PdfDocumentWriteResult normalizedWrite = PdfDocumentWriter.WriteWithReport(source, new PdfDocumentWriteOptions
            {
                PruneUnreachableObjects = true,
                AllowSignatureInvalidation = options.AllowSignatureInvalidation,
                RemoveEncryption = options.RemoveEncryption,
                ReplaceDanglingReferences = true
            });
            preflightRepairs = normalizedWrite.ReferenceRepairs;
            PdfDocument normalized = PdfDocument.Open(normalizedWrite.Data);
            written = Rewrite(normalized, PdfPageTree.Read(normalized), byPage, covers, rasterPages,
                retired.For(normalized), options, rasterHidden, cancellationToken);
        }
        byte[] output = written.Bytes;
        PdfRedactionVerificationReport verification = Verify(output, regionList, rasterPages,
            options.ProhibitedText, options.ClearDocumentMetadata, rasterHidden, cancellationToken) with
        {
            ReferenceRepairs = Array.AsReadOnly(preflightRepairs.Concat(written.ReferenceRepairs).ToArray())
        };
        if (!verification.Succeeded) throw new PdfRedactionVerificationException(verification);
        return new PdfContentRedactionResult(output, plan, verification);
    }

    /// <summary>
    /// Removes the text drawn under opaque covers, such as the cover of an in-place text edit, and
    /// returns the document with an incremental update, or null when no text lies under them.
    /// </summary>
    /// <remarks>
    /// A glyph goes when its center lies inside a region. Images, drawings, annotations and every
    /// page resource stay, and text in a font that cannot be decoded is kept. Regions use unrotated
    /// PDF points relative to the corner given by <see cref="PdfPageInformation.Left"/> and
    /// <see cref="PdfPageInformation.Bottom"/>.
    /// </remarks>
    public static byte[]? RemoveCoveredText(PdfDocument document, IEnumerable<PdfRedactionRegion> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(regions);
        PdfPageTree tree = PdfPageTree.Read(document);
        Dictionary<int, PdfContentBounds[]> byPage = GroupRegions(regions, tree.Pages.Count);
        if (byPage.Count == 0) return null;
        IReadOnlyList<PdfPageInformation> boxes = PdfPageInformation.Read(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        var fonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        bool changed = false;
        foreach ((int pageIndex, PdfContentBounds[] pageRegions) in byPage.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageTreeEntry page = tree.Pages[pageIndex];
            PdfPageInformation box = boxes[pageIndex];
            var rewriter = new ContentRewriter(document, update, pageRegions, textOnly: false, fonts,
                cancellationToken, glyphsOnly: true);
            List<PdfContentInstruction> content = rewriter.RewritePage(page,
                new PageGeometry(box.Left, box.Bottom, box.Width, box.Height, box.Rotation));
            if (rewriter.RemovedGlyphs == 0) continue;
            var instructions = new List<PdfContentInstruction>(content.Count + 2) { Op("q") };
            instructions.AddRange(content);
            instructions.Add(Op("Q"));
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(PdfContentStreamWriter.Write(instructions));
            PdfIndirectReference contentReference = update.AddObject(new PdfStream(
                new PdfDictionary(new Dictionary<PdfName, PdfObject> { [Name("Filter")] = Name("FlateDecode") }),
                compressed.ToArray()));
            var entries = page.Dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
            entries[ContentsName] = contentReference;
            entries[ResourcesName] = rewriter.PageScope!.Build();
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
            changed = true;
        }
        return changed ? update.Build() : null;
    }

    private static PdfDocumentWriteResult Rewrite(PdfDocument document, PdfPageTree tree,
        Dictionary<int, PdfContentBounds[]> byPage, Dictionary<int, PdfRedactionRegion[]> covers,
        HashSet<int> rasterPages, RetiredContent retired, PdfContentRedactionOptions options,
        Dictionary<int, HiddenArea[]> rasterHidden, CancellationToken cancellationToken)
    {
        Dictionary<int, PdfImage> images = rasterPages.Count == 0 ? []
            : RenderRasterPages(document, tree, covers, rasterPages, options, cancellationToken);
        PdfIndirectReference? overlayFont = null;

        var update = new PdfIncrementalUpdateBuilder(document);
        var fonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        var removedAnnotations = new HashSet<int>();
        var removedWidgets = new HashSet<int>();
        var retiredStreams = new HashSet<int>();
        var retiredDescriptions = new HashSet<string>(StringComparer.Ordinal);
        var touchedContent = new Dictionary<int, (HashSet<int> Mcids, bool Whole)>();
        foreach ((int pageIndex, PdfContentBounds[] pageRegions) in byPage.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageTreeEntry page = tree.Pages[pageIndex];
            PageGeometry geometry = Geometry(document, page);
            bool raster = rasterPages.Contains(pageIndex);
            var rewriter = new ContentRewriter(document, update, pageRegions, raster, fonts, cancellationToken,
                fills: [.. covers[pageIndex].Select(cover => cover.FillColor ?? DefaultFill)], retired: retired);
            List<PdfContentInstruction> content = rewriter.RewritePage(page, geometry);
            if (page.Dictionary.TryGetValue(ContentsName, out PdfObject? originalContents))
                CollectSourceStreams(document, originalContents, retiredStreams);
            retiredStreams.UnionWith(rewriter.RetiredSourceStreams);
            retiredDescriptions.UnionWith(rewriter.RetiredDescriptions);
            if (!raster && rewriter.Reasons.Count != 0)
                throw new InvalidOperationException($"Page {pageIndex + 1} changed while it was being redacted.");
            if (raster) rasterHidden[pageIndex] = [.. rewriter.HiddenAreas];
            var instructions = new List<PdfContentInstruction>(content.Count + 16) { Op("q") };
            instructions.AddRange(content);
            instructions.Add(Op("Q"));
            PdfDictionary resources = rewriter.PageScope!.Build();
            if (raster)
            {
                PdfIndirectReference imageReference = update.AddObject(PdfImageXObjectFactory.Create(images[pageIndex], null));
                resources = WithEntry(resources, Name("XObject"), Name(RedactionImageName), imageReference, document);
                instructions.Add(Op("q"));
                instructions.Add(Op("cm", geometry.Width, 0, 0, geometry.Height, geometry.CropLeft, geometry.CropBottom));
                instructions.Add(new PdfContentInstruction("Do", 0, [Name(RedactionImageName)]));
                instructions.Add(Op("Q"));
            }
            // Each area is filled with its color, and its overlay text is drawn upright as real text.
            PdfRedactionRegion[] pageCovers = covers[pageIndex];
            if (pageCovers.Any(cover => Overlay(cover, geometry) is not null))
            {
                overlayFont ??= update.AddObject(new PdfDictionary(new Dictionary<PdfName, PdfObject>
                {
                    [Name("Type")] = Name("Font"),
                    [Name("Subtype")] = Name("Type1"),
                    [Name("BaseFont")] = Name(OverlayFontFamily),
                    [Name("Encoding")] = Name("WinAnsiEncoding")
                }));
                resources = WithEntry(resources, Name("Font"), Name(OverlayFontName), overlayFont, document);
            }
            foreach (PdfRedactionRegion cover in pageCovers)
                AddCover(instructions, cover, geometry);
            PdfIndirectReference contentReference = update.AddObject(
                new PdfStream(new PdfDictionary([]), PdfContentStreamWriter.Write(instructions)));
            var entries = page.Dictionary
                .Where(entry => !RemovedPageKeys.Contains(entry.Key) && !entry.Key.Equals(ContentsName)
                    && !entry.Key.Equals(ResourcesName) && !entry.Key.Equals(AnnotsName))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            entries[ContentsName] = contentReference;
            entries[ResourcesName] = resources;
            List<PdfObject> removed = IntersectingAnnotations(document, page, geometry, pageRegions);
            removed.AddRange(HiddenAnnotations(document, page, geometry, rewriter.HiddenPageAreas));
            PdfArray? annotations = RemainingAnnotations(document, page, removed, removedAnnotations, removedWidgets);
            if (annotations is not null) entries[AnnotsName] = annotations;
            // A widget the field tree places on this page but the page does not list cannot be shown to be
            // clear of the areas, so it leaves with its value instead of blocking the page.
            foreach (PdfObject widget in CoveredFieldWidgets(document, tree, pageIndex, geometry, pageRegions))
                if (widget is PdfIndirectReference orphan)
                {
                    removedWidgets.Add(orphan.ObjectNumber);
                    removedAnnotations.Add(orphan.ObjectNumber);
                }
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
            touchedContent[page.Reference.ObjectNumber] = (rewriter.TouchedMcids, raster || rewriter.FormMarkedContentTouched);
        }
        HashSet<int> survivors = RewriteSurvivorPages(document, tree, update, byPage, fonts, retired, touchedContent,
            retiredStreams, retiredDescriptions, removedAnnotations, removedWidgets, cancellationToken);
        MaterializeInheritedResources(document, tree, update, [.. byPage.Keys, .. survivors], cancellationToken);
        ReleaseReplies(document, tree, update, removedAnnotations);

        PdfDictionary catalog = tree.Catalog;
        var catalogEntries = catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
        bool catalogChanged = false;
        if (catalog.TryGetValue(AcroFormName, out PdfObject? acroFormValue))
            catalogChanged |= PruneFields(document, update, acroFormValue, removedWidgets, catalogEntries);
        if (catalog.TryGetValue(StructTreeRootName, out PdfObject? structureValue))
            catalogChanged |= CleanStructure(document, update, structureValue, touchedContent, removedAnnotations,
                retiredStreams, catalogEntries);
        if (catalogChanged)
            update.ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalogEntries));

        PdfDocument edited = PdfDocument.Open(update.Build());
        var writeOptions = new PdfDocumentWriteOptions
        {
            PruneUnreachableObjects = true,
            ReplaceDanglingReferences = true,
            AllowSignatureInvalidation = options.AllowSignatureInvalidation,
            RemoveEncryption = options.RemoveEncryption,
            MetadataPolicy = options.ClearDocumentMetadata
                ? PdfMetadataPolicy.RemoveDocumentInformationAndXmp : PdfMetadataPolicy.Preserve,
            PreserveDocumentIdentifiers = !options.ClearDocumentMetadata,
            CompressUnfilteredStreams = true
        };
        PdfDocumentWriteResult output = PdfDocumentWriter.WriteWithReport(edited, writeOptions);
        PdfDocument written = PdfDocument.Open(output.Data);
        VerifySurvivors(written, retired, cancellationToken);
        try { VerifyRetiredStreams(written, retiredStreams, retiredDescriptions, cancellationToken); }
        catch (PdfRedactionVerificationException error)
            when (error.Report.Findings.All(finding => finding.Code == "RetainedSourceStream"))
        {
            // The original may only be listed in a resource dictionary that unredacted pages share.
            PdfDocument? cleaned = DropUndrawnRetiredResources(edited, written, retiredStreams, cancellationToken);
            if (cleaned is null) throw;
            output = PdfDocumentWriter.WriteWithReport(cleaned, writeOptions);
            written = PdfDocument.Open(output.Data);
            VerifySurvivors(written, retired, cancellationToken);
            VerifyRetiredStreams(written, retiredStreams, retiredDescriptions, cancellationToken);
        }
        return output;
    }

    /// <summary>
    /// Reads a redacted output back and reports any text, vector, image, shading, or annotation that still
    /// intersects a region, any revision history, and any remaining prohibited text.
    /// </summary>
    public static PdfRedactionVerificationReport Verify(ReadOnlyMemory<byte> output,
        IEnumerable<PdfRedactionRegion> regions, IReadOnlyCollection<int> rasterPages,
        IEnumerable<string>? prohibitedText = null, bool expectMetadataCleared = true,
        CancellationToken cancellationToken = default) =>
        Verify(output, regions, rasterPages, prohibitedText, expectMetadataCleared,
            new Dictionary<int, HiddenArea[]>(), cancellationToken);

    /// <summary>
    /// The same checks, also holding each replaced page to the areas its draws hid: a letter may stay
    /// in its invisible layer over one of them only when the draw showed that letter whole.
    /// </summary>
    internal static PdfRedactionVerificationReport Verify(ReadOnlyMemory<byte> output,
        IEnumerable<PdfRedactionRegion> regions, IReadOnlyCollection<int> rasterPages,
        IEnumerable<string>? prohibitedText, bool expectMetadataCleared,
        IReadOnlyDictionary<int, HiddenArea[]> rasterHidden, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(rasterPages);
        PdfRedactionRegion[] regionList = [.. regions];
        string[] prohibited = [.. (prohibitedText ?? []).Where(value => !string.IsNullOrWhiteSpace(value))];
        var findings = new List<PdfRedactionVerificationFinding>();
        PdfDocument document = PdfDocument.Open(output);
        if (document.CrossReferences.Sections.Count != 1)
            findings.Add(new("IncrementalRevisions", "The output contains incremental revision history."));
        PdfPageTree tree = PdfPageTree.Read(document);
        if (expectMetadataCleared && document.Trailer.ContainsKey(InfoName))
            findings.Add(new("DocumentInformation", "The output contains a document-information dictionary."));
        if (expectMetadataCleared && tree.Catalog.ContainsKey(MetadataName))
            findings.Add(new("XmpMetadata", "The output contains XMP metadata."));
        if (HasXfaForm(document, tree))
            findings.Add(new("XfaFormData", "The output still carries an XFA form packet, whose datasets repeat field values."));
        Dictionary<int, PdfContentBounds[]> byPage = GroupRegions(regionList, tree.Pages.Count);
        Dictionary<int, PdfRedactionRegion[]> covers = GroupCovers(regionList);
        // A page turned into an image for what it hid has no region, and is held to the same checks.
        foreach (int page in rasterPages.Where(page => page >= 0 && page < tree.Pages.Count))
            if (byPage.TryAdd(page, [])) covers[page] = [];
        var reader = new PdfPageContentReader(document);
        var vectorFonts = new Dictionary<PdfDictionary, PdfExtractionFont?>(ReferenceEqualityComparer.Instance);
        foreach ((int pageIndex, PdfContentBounds[] pageRegions) in byPage.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageContent content = reader.Read(pageIndex, cancellationToken);
            PageGeometry pageGeometry = Geometry(document, tree.Pages[pageIndex]);
            var vectors = new ContentRewriter(document, null, pageRegions, textOnly: false, vectorFonts, cancellationToken);
            vectors.RewritePage(tree.Pages[pageIndex], pageGeometry, covers[pageIndex]);
            if (vectors.RemovedPaths != 0 || vectors.Reasons.Contains("Vector"))
                findings.Add(new("VectorInRegion", "Vector geometry remains inside a redacted area.", pageIndex));
            if (vectors.Reasons.Contains("RedactionCover"))
                findings.Add(new("RedactionCover", "An expected redaction cover is missing or changed.", pageIndex));
            if (TextRemains(content.Letters, pageRegions, covers[pageIndex], pageGeometry)
                || rasterHidden.TryGetValue(pageIndex, out HiddenArea[]? hidden)
                    && HiddenTextRemains(content.Letters, hidden, covers[pageIndex], pageGeometry))
                findings.Add(new("TextInRegion", "Text remains inside a redacted area.", pageIndex));
            bool raster = rasterPages.Contains(pageIndex);
            foreach (PdfExtractedImage image in content.Images)
            {
                if (raster && image.ResourceName == RedactionImageName) continue;
                PdfRedactionRegion[] hits = [.. covers[pageIndex].Where(cover => Overlaps(image.BoundingBox, cover.Bounds))];
                // An image may stay over a region only when every covered pixel is painted with the fill.
                if (hits.Length != 0 && !IsPainted(document, image, hits))
                {
                    findings.Add(new("ImageInRegion", "An image remains inside a redacted area.", pageIndex));
                    break;
                }
            }
            if (raster && content.Images.Count(image => image.ResourceName == RedactionImageName) != 1)
                findings.Add(new("RasterPage", "The replaced page does not contain exactly one sanitized image.", pageIndex));
            foreach (PdfExtractedShading shading in content.Shadings)
                if (pageRegions.Any(region => Overlaps(shading.BoundingBox, region)))
                {
                    findings.Add(new("ShadingInRegion", "A shading remains inside a redacted area.", pageIndex));
                    break;
                }
            if (IntersectingAnnotations(document, tree.Pages[pageIndex], pageGeometry, pageRegions).Count != 0)
                findings.Add(new("AnnotationInRegion", "An annotation remains over a redacted area.", pageIndex));
            if (FieldWidgetRemains(document, tree, pageIndex, pageGeometry, pageRegions))
                findings.Add(new("FieldInRegion", "A form field widget remains over a redacted area.", pageIndex));
            foreach (PdfName key in RemovedPageKeys)
                if (tree.Pages[pageIndex].Dictionary.ContainsKey(key))
                    findings.Add(new("PageData", $"The redacted page still contains /{key.ValueAsLatin1()}.", pageIndex));
        }
        InspectStructure(document, tree, byPage.Keys, findings, cancellationToken);
        if (prohibited.Length != 0)
        {
            for (int pageIndex = 0; pageIndex < tree.Pages.Count; pageIndex++)
            {
                string text = reader.Read(pageIndex, cancellationToken).Text;
                foreach (string value in prohibited)
                    if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                        findings.Add(new("ProhibitedText", $"Page text still contains '{value}'.", pageIndex));
            }
            InspectObjects(document, prohibited, findings, cancellationToken);
        }
        return new PdfRedactionVerificationReport(Array.AsReadOnly(findings.ToArray()));
    }

    private static void InspectObjects(PdfDocument document, string[] prohibited,
        List<PdfRedactionVerificationFinding> findings, CancellationToken cancellationToken)
    {
        var reported = new HashSet<(int, string)>();
        foreach (PdfCrossReferenceEntry entry in document.CrossReferences.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Type is not (PdfCrossReferenceEntryType.InUse
                or PdfCrossReferenceEntryType.Compressed)) continue;
            PdfObject value;
            try { value = document.Resolve(entry.ObjectNumber); }
            catch (Exception error) when (error is FormatException or InvalidOperationException)
            {
                findings.Add(new("ObjectInspectionFailure", $"Object {entry.ObjectNumber} could not be read.",
                    ObjectNumber: entry.ObjectNumber));
                continue;
            }
            Inspect(value, entry.ObjectNumber, 0);
        }

        void Inspect(PdfObject value, int objectNumber, int depth)
        {
            if (depth > 256) return;
            switch (value)
            {
                case PdfString text:
                    Check(PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "Object text"), objectNumber);
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array) Inspect(item, objectNumber, depth + 1);
                    break;
                case PdfStream stream:
                    Inspect(stream.Dictionary, objectNumber, depth + 1);
                    if (IsNamed(stream.Dictionary, SubtypeName, "Image")) break;
                    byte[] decoded;
                    try { decoded = PdfStreamDecoder.Decode(stream, document.Resolve, InspectionDecodeLimit); }
                    catch (Exception error) when (error is PdfFilterException or FormatException
                        or InvalidDataException or NotSupportedException)
                    {
                        // Data that cannot be decoded cannot be shown to be free of the prohibited text.
                        findings.Add(new("ObjectInspectionFailure",
                            $"Stream object {objectNumber} could not be inspected: {error.Message}",
                            ObjectNumber: objectNumber));
                        break;
                    }
                    Check(Encoding.Latin1.GetString(decoded), objectNumber);
                    Check(Encoding.BigEndianUnicode.GetString(decoded), objectNumber);
                    break;
                case PdfDictionary dictionary:
                    foreach (PdfObject item in dictionary.Values) Inspect(item, objectNumber, depth + 1);
                    break;
            }
        }

        void Check(string candidate, int objectNumber)
        {
            foreach (string blocked in prohibited)
                if (candidate.Contains(blocked, StringComparison.OrdinalIgnoreCase) && reported.Add((objectNumber, blocked)))
                    findings.Add(new("ProhibitedObjectText",
                        $"Object {objectNumber} still contains '{blocked}'.", ObjectNumber: objectNumber));
        }
    }

    private static Dictionary<int, PdfImage> RenderRasterPages(PdfDocument document, PdfPageTree tree,
        Dictionary<int, PdfRedactionRegion[]> covers, HashSet<int> rasterPages,
        PdfContentRedactionOptions options, CancellationToken cancellationToken)
    {
        // Render from a copy whose replaced pages carry the filled regions, so the boxes are in the pixels.
        var boxed = new PdfIncrementalUpdateBuilder(document);
        foreach (int pageIndex in rasterPages)
        {
            PdfPageTreeEntry page = tree.Pages[pageIndex];
            PageGeometry geometry = Geometry(document, page);
            var items = new List<PdfObject> { boxed.AddObject(new PdfStream(new PdfDictionary([]), "q\n"u8)) };
            if (page.Dictionary.TryGetValue(ContentsName, out PdfObject? contents))
            {
                if (Resolve(document, contents) is PdfArray array) items.AddRange(array);
                else items.Add(contents);
            }
            var tail = new List<PdfContentInstruction> { Op("Q") };
            foreach (PdfRedactionRegion cover in covers[pageIndex])
            {
                PdfContentBounds region = cover.Bounds;
                PdfRgbColor fill = cover.FillColor ?? DefaultFill;
                tail.Add(Op("rg", fill.Red, fill.Green, fill.Blue));
                tail.Add(Op("re", region.Left + geometry.CropLeft, region.Bottom + geometry.CropBottom, region.Width, region.Height));
                tail.Add(Op("f"));
            }
            items.Add(boxed.AddObject(new PdfStream(new PdfDictionary([]), PdfContentStreamWriter.Write(tail))));
            var entries = page.Dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
            entries[ContentsName] = new PdfArray(items);
            boxed.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
        }
        PdfDocument boxedDocument = PdfDocument.Open(boxed.Build());
        var renderer = new PdfPageRenderer(boxedDocument, options.FontResolver);
        var images = new Dictionary<int, PdfImage>();
        foreach (int pageIndex in rasterPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageGeometry geometry = Geometry(document, tree.Pages[pageIndex]);
            bool quarter = geometry.Rotation is 90 or 270;
            double dpi = options.RasterDpi;
            int width, height;
            while (true)
            {
                width = Math.Max(1, (int)Math.Ceiling(geometry.Width * dpi / 72));
                height = Math.Max(1, (int)Math.Ceiling(geometry.Height * dpi / 72));
                if (width <= PdfRenderOptions.MaximumDimension && height <= PdfRenderOptions.MaximumDimension
                    && (long)width * height * 4 <= PdfRenderOptions.MaximumPixelBytes) break;
                dpi /= 2;
            }
            var render = new PdfRenderOptions(quarter ? height : width, quarter ? width : height,
                includeAnnotations: false, includeFormFields: false) { CacheResult = false };
            PdfRenderedPage rendered = renderer.Render(pageIndex, render, cancellationToken);
            byte[] rgb = Unrotate(rendered, geometry.Rotation, width, height);
            // The covers drawn in the content run after the file's own state, which an unmatched q can
            // leave clipped, moved or transparent, so the pixels are covered here as well.
            foreach (PdfRedactionRegion cover in covers[pageIndex])
                FillPixels(rgb, width, height, cover.Bounds, cover.FillColor ?? DefaultFill, geometry);
            images[pageIndex] = PdfImage.FromRgb(width, height, rgb);
        }
        return images;
    }

    private static void FillPixels(byte[] rgb, int width, int height, PdfContentBounds area, PdfRgbColor fill, PageGeometry geometry)
    {
        double scaleX = width / geometry.Width, scaleY = height / geometry.Height;
        int left = Math.Clamp((int)Math.Floor(area.Left * scaleX), 0, width);
        int right = Math.Clamp((int)Math.Ceiling(area.Right * scaleX), 0, width);
        int top = Math.Clamp((int)Math.Floor((geometry.Height - area.Top) * scaleY), 0, height);
        int bottom = Math.Clamp((int)Math.Ceiling((geometry.Height - area.Bottom) * scaleY), 0, height);
        byte red = (byte)Math.Round(fill.Red * 255), green = (byte)Math.Round(fill.Green * 255), blue = (byte)Math.Round(fill.Blue * 255);
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int offset = (y * width + x) * 3;
                rgb[offset] = red;
                rgb[offset + 1] = green;
                rgb[offset + 2] = blue;
            }
    }

    /// <summary>Converts a display-oriented BGRA page bitmap into unrotated RGB samples.</summary>
    internal static byte[] Unrotate(PdfRenderedPage rendered, int rotation, int width, int height)
    {
        ReadOnlySpan<byte> source = rendered.Pixels.Span;
        int sourceWidth = rendered.Width;
        var rgb = new byte[checked(width * height * 3)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                (int sx, int sy) = rotation switch
                {
                    90 => (height - 1 - y, x),
                    180 => (width - 1 - x, height - 1 - y),
                    270 => (y, width - 1 - x),
                    _ => (x, y)
                };
                int from = (sy * sourceWidth + sx) * 4, to = (y * width + x) * 3;
                rgb[to] = source[from + 2];
                rgb[to + 1] = source[from + 1];
                rgb[to + 2] = source[from];
            }
        return rgb;
    }

    private static List<PdfObject> IntersectingAnnotations(PdfDocument document, PdfPageTreeEntry page,
        PageGeometry geometry, PdfContentBounds[] regions)
    {
        var result = new List<PdfObject>();
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? value)
            || Resolve(document, value) is not PdfArray annotations) return result;
        foreach (PdfObject item in annotations)
        {
            if (Resolve(document, item) is not PdfDictionary annotation
                || IsNamed(annotation, SubtypeName, "Popup")) continue;
            // An annotation that cannot be placed cannot be shown to be clear of the area, so it counts as covered.
            if (!TryAnnotationBounds(document, annotation, geometry, out PdfContentBounds bounds)
                || regions.Any(region => OverlapsOrTouches(bounds, region))) result.Add(item);
        }
        return result;
    }

    private static bool TryAnnotationBounds(PdfDocument document, PdfDictionary annotation, PageGeometry geometry,
        out PdfContentBounds bounds)
    {
        bounds = default;
        if (!annotation.TryGetValue(RectName, out PdfObject? rectValue)
            || Resolve(document, rectValue) is not PdfArray rect || rect.Count != 4) return false;
        double[] values = new double[4];
        for (int index = 0; index < 4; index++)
        {
            switch (Resolve(document, rect[index]))
            {
                case PdfInteger integer: values[index] = integer.Value; break;
                case PdfReal real when double.IsFinite(real.Value): values[index] = real.Value; break;
                default: return false;
            }
        }
        bounds = new PdfContentBounds(Math.Min(values[0], values[2]) - geometry.CropLeft,
            Math.Min(values[1], values[3]) - geometry.CropBottom,
            Math.Max(values[0], values[2]) - geometry.CropLeft, Math.Max(values[1], values[3]) - geometry.CropBottom);
        return true;
    }

    // Walks the whole field tree, since a widget dropped from the page can stay reachable through it.
    private static bool FieldWidgetRemains(PdfDocument document, PdfPageTree tree, int pageIndex,
        PageGeometry geometry, PdfContentBounds[] regions) =>
        CoveredFieldWidgets(document, tree, pageIndex, geometry, regions).Any();

    // Yields every field-tree node that is a widget of the page and overlaps a region or cannot be placed.
    private static IEnumerable<PdfObject> CoveredFieldWidgets(PdfDocument document, PdfPageTree tree, int pageIndex,
        PageGeometry geometry, PdfContentBounds[] regions)
    {
        if (!tree.Catalog.TryGetValue(AcroFormName, out PdfObject? acroFormValue)
            || Resolve(document, acroFormValue) is not PdfDictionary acroForm
            || !acroForm.TryGetValue(FieldsName, out PdfObject? fieldsValue)
            || Resolve(document, fieldsValue) is not PdfArray fields) yield break;
        int pageNumber = tree.Pages[pageIndex].Reference.ObjectNumber;
        var visited = new HashSet<int>();
        var pending = new Stack<PdfObject>(fields);
        while (pending.Count != 0)
        {
            PdfObject item = pending.Pop();
            if (item is PdfIndirectReference reference && !visited.Add(reference.ObjectNumber)) continue;
            if (Resolve(document, item) is not PdfDictionary field) continue;
            if (field.TryGetValue(KidsName, out PdfObject? kidsValue) && Resolve(document, kidsValue) is PdfArray kids)
                foreach (PdfObject kid in kids) pending.Push(kid);
            if (!field.TryGetValue(Name("P"), out PdfObject? owner)
                || owner is not PdfIndirectReference { ObjectNumber: var ownerNumber } || ownerNumber != pageNumber
                || !(IsNamed(field, SubtypeName, "Widget") || field.ContainsKey(RectName))) continue;
            if (!TryAnnotationBounds(document, field, geometry, out PdfContentBounds bounds)
                || regions.Any(region => OverlapsOrTouches(bounds, region))) yield return item;
        }
    }

    // A kept reply names the comment it answers, and that reference alone would keep the removed
    // comment in the file with its text. The reply stays as a comment of its own.
    private static void ReleaseReplies(PdfDocument document, PdfPageTree tree, PdfIncrementalUpdateBuilder update,
        HashSet<int> removedAnnotations)
    {
        if (removedAnnotations.Count == 0) return;
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? value)
                || Resolve(document, value) is not PdfArray annotations) continue;
            foreach (PdfObject item in annotations)
            {
                if (item is not PdfIndirectReference { ObjectNumber: var number } || removedAnnotations.Contains(number)
                    || Resolve(document, item) is not PdfDictionary annotation
                    || !annotation.TryGetValue(InReplyToName, out PdfObject? parent)
                    || parent is not PdfIndirectReference { ObjectNumber: var parentNumber }
                    || !removedAnnotations.Contains(parentNumber)) continue;
                update.ReplaceObject(number, new PdfDictionary(annotation
                    .Where(entry => !entry.Key.Equals(InReplyToName) && !entry.Key.Equals(ReplyTypeName))
                    .ToDictionary(entry => entry.Key, entry => entry.Value)));
            }
        }
    }

    private static PdfArray? RemainingAnnotations(PdfDocument document, PdfPageTreeEntry page,
        List<PdfObject> removed, HashSet<int> removedAnnotations, HashSet<int> removedWidgets)
    {
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? value)) return null;
        if (Resolve(document, value) is not PdfArray annotations) return null;
        var drop = new HashSet<PdfObject>(removed, ReferenceEqualityComparer.Instance);
        var droppedNumbers = new HashSet<int>();
        foreach (PdfObject item in removed)
        {
            if (item is PdfIndirectReference reference)
            {
                droppedNumbers.Add(reference.ObjectNumber);
                removedAnnotations.Add(reference.ObjectNumber);
            }
            if (Resolve(document, item) is not PdfDictionary annotation) continue;
            if (IsNamed(annotation, SubtypeName, "Widget") && item is PdfIndirectReference widget)
                removedWidgets.Add(widget.ObjectNumber);
            if (annotation.TryGetValue(PopupName, out PdfObject? popup) && popup is PdfIndirectReference popupReference)
            {
                droppedNumbers.Add(popupReference.ObjectNumber);
                removedAnnotations.Add(popupReference.ObjectNumber);
            }
        }
        var kept = new List<PdfObject>();
        foreach (PdfObject item in annotations)
        {
            if (drop.Contains(item)) continue;
            if (item is PdfIndirectReference reference && droppedNumbers.Contains(reference.ObjectNumber)) continue;
            if (Resolve(document, item) is PdfDictionary annotation
                && annotation.TryGetValue(ParentName, out PdfObject? parent)
                && parent is PdfIndirectReference parentReference
                && droppedNumbers.Contains(parentReference.ObjectNumber)
                && IsNamed(annotation, SubtypeName, "Popup")) continue;
            kept.Add(item);
        }
        return kept.Count == 0 ? null : new PdfArray(kept);
    }

    // Drops removed widgets from the field tree, clears the value of a field that lost one of its widgets,
    // and removes the XFA packet, whose datasets repeat every field value. The form keeps working as a
    // plain AcroForm, which is also what Acrobat requires before it redacts.
    private static bool PruneFields(PdfDocument document, PdfIncrementalUpdateBuilder update,
        PdfObject acroFormValue, HashSet<int> removedWidgets, Dictionary<PdfName, PdfObject> catalogEntries)
    {
        if (Resolve(document, acroFormValue) is not PdfDictionary acroForm) return false;
        var removedFields = new HashSet<int>(removedWidgets);
        var emptiedWidgets = new HashSet<int>();
        PdfArray? pruned = removedWidgets.Count != 0 && acroForm.TryGetValue(FieldsName, out PdfObject? fieldsValue)
            && Resolve(document, fieldsValue) is PdfArray fields ? PruneKids(fields, 0, out _) : null;
        if (pruned is null && !acroForm.ContainsKey(XfaName)) return false;
        var entries = acroForm.Where(entry => !entry.Key.Equals(XfaName))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        if (pruned is not null) entries[FieldsName] = pruned;
        PdfName calculationOrder = Name("CO");
        if (acroForm.TryGetValue(calculationOrder, out PdfObject? orderValue)
            && Resolve(document, orderValue) is PdfArray order)
            entries[calculationOrder] = new PdfArray(order.Where(item =>
                item is not PdfIndirectReference reference || !removedFields.Contains(reference.ObjectNumber)));
        var replacement = new PdfDictionary(entries);
        if (acroFormValue is PdfIndirectReference acroFormReference)
        {
            update.ReplaceObject(acroFormReference.ObjectNumber, replacement);
            return false;
        }
        catalogEntries[AcroFormName] = replacement;
        return true;

        PdfArray? PruneKids(PdfArray kids, int depth, out bool removedKid)
        {
            // Stopping early would leave a removed widget, and its value, reachable from the form.
            if (depth > MaximumFieldDepth)
                throw new NotSupportedException("The form field hierarchy is too deep to redact safely.");
            bool changed = false;
            removedKid = false;
            var kept = new List<PdfObject>();
            foreach (PdfObject kid in kids)
            {
                if (kid is PdfIndirectReference reference && removedFields.Contains(reference.ObjectNumber))
                {
                    changed = true;
                    removedKid = true;
                    continue;
                }
                if (kid is PdfIndirectReference fieldReference
                    && Resolve(document, kid) is PdfDictionary field
                    && field.TryGetValue(KidsName, out PdfObject? childrenValue)
                    && Resolve(document, childrenValue) is PdfArray children)
                {
                    PdfArray? prunedChildren = PruneKids(children, depth + 1, out bool removedChild);
                    if (prunedChildren is not null)
                    {
                        changed = true;
                        removedKid |= removedChild;
                        if (prunedChildren.Count == 0)
                        {
                            removedFields.Add(fieldReference.ObjectNumber);
                            removedKid = true;
                            continue;
                        }
                        // A field inherits its value from any ancestor, so it goes from every field above the widget.
                        var fieldEntries = field.Where(entry => !removedChild || !FieldValueKeys.Contains(entry.Key))
                            .ToDictionary(entry => entry.Key, entry => entry.Value);
                        fieldEntries[KidsName] = prunedChildren;
                        update.ReplaceObject(fieldReference.ObjectNumber, new PdfDictionary(fieldEntries));
                        // The remaining widgets drew that value from their appearance streams.
                        if (removedChild) EmptyInheritedAppearances(prunedChildren, depth + 1);
                    }
                }
                kept.Add(kid);
            }
            return changed ? new PdfArray(kept) : null;
        }

        // A widget that inherited its value from a field that just lost it keeps drawing the old value from
        // its appearance streams, so each gets an empty normal appearance in their place. A widget or
        // subfield with a value of its own keeps its appearance.
        void EmptyInheritedAppearances(PdfArray kids, int depth)
        {
            if (depth > MaximumFieldDepth)
                throw new NotSupportedException("The form field hierarchy is too deep to redact safely.");
            foreach (PdfObject kid in kids)
            {
                if (kid is not PdfIndirectReference reference
                    || Resolve(document, kid) is not PdfDictionary field
                    || field.ContainsKey(Name("V"))) continue;
                if (field.TryGetValue(KidsName, out PdfObject? childrenValue)
                    && Resolve(document, childrenValue) is PdfArray children)
                {
                    EmptyInheritedAppearances(children, depth + 1);
                    continue;
                }
                if (!emptiedWidgets.Add(reference.ObjectNumber)) continue;
                PdfName appearanceName = Name("AP");
                if (!field.ContainsKey(appearanceName)) continue;
                double width = 0, height = 0;
                try
                {
                    if (field.TryGetValue(RectName, out PdfObject? rectValue)
                        && Resolve(document, rectValue) is PdfArray { Count: 4 } rect)
                    {
                        width = Math.Abs(Number(document, rect[2]) - Number(document, rect[0]));
                        height = Math.Abs(Number(document, rect[3]) - Number(document, rect[1]));
                    }
                }
                catch (FormatException) { width = height = 0; }
                PdfIndirectReference blank = update.AddObject(new PdfStream(new PdfDictionary(new Dictionary<PdfName, PdfObject>
                {
                    [Name("Type")] = Name("XObject"),
                    [SubtypeName] = Name("Form"),
                    [Name("BBox")] = new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfReal(width), new PdfReal(height)])
                }), ""u8));
                var entries = field.Where(entry => !entry.Key.Equals(appearanceName) && !entry.Key.Equals(Name("AS")))
                    .ToDictionary(entry => entry.Key, entry => entry.Value);
                entries[appearanceName] = new PdfDictionary(new Dictionary<PdfName, PdfObject> { [Name("N")] = blank });
                update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries));
            }
        }
    }

    private static bool CleanStructure(PdfDocument document, PdfIncrementalUpdateBuilder update,
        PdfObject rootValue, Dictionary<int, (HashSet<int> Mcids, bool Whole)> touched,
        HashSet<int> removedAnnotations, HashSet<int> retiredStreams, Dictionary<PdfName, PdfObject> catalogEntries)
    {
        if (Resolve(document, rootValue) is not PdfDictionary root
            || !root.TryGetValue(Name("K"), out PdfObject? kids)) return false;
        var visited = new HashSet<int>();
        (PdfObject rootKids, bool rootChanged, _) = Clean(kids, null, 0, 0);
        if (!rootChanged) return false;
        var entries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[Name("K")] = rootKids;
        var replacement = new PdfDictionary(entries);
        if (rootValue is PdfIndirectReference rootReference)
        {
            update.ReplaceObject(rootReference.ObjectNumber, replacement);
            return false;
        }
        catalogEntries[StructTreeRootName] = replacement;
        return true;

        // Returns the possibly rewritten value, whether it changed, and whether it covers redacted content.
        (PdfObject Value, bool Changed, bool Touched) Clean(PdfObject value, int? page, int depth, int frames)
        {
            // Stopping early would leave every description of a deep tree in place, so the limit refuses.
            // Only elements count toward it; references and arrays are bounded by the frame count and
            // by the stack the calling thread has left.
            if (depth > MaximumStructureDepth || frames > MaximumStructureFrames
                || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
                throw new NotSupportedException("The document structure is too deeply nested to redact safely.");
            if (value is PdfIndirectReference reference)
            {
                if (!visited.Add(reference.ObjectNumber)) return (value, false, false);
                (PdfObject inner, bool changed, bool hit) = Clean(document.Resolve(reference), page, depth, frames + 1);
                if (changed) update.ReplaceObject(reference.ObjectNumber, inner);
                return (value, false, hit);
            }
            if (value is PdfInteger mcid)
                return (value, false, page is int pageNumber && touched.TryGetValue(pageNumber, out var hits)
                    && (hits.Whole || hits.Mcids.Contains((int)mcid.Value)));
            if (value is PdfArray array)
            {
                bool changed = false, hit = false;
                var items = new List<PdfObject>();
                foreach (PdfObject item in array)
                {
                    if (IsRemovedReference(item)) { changed = true; hit = true; continue; }
                    (PdfObject cleanedItem, bool itemChanged, bool itemHit) = Clean(item, page, depth, frames + 1);
                    changed |= itemChanged;
                    hit |= itemHit;
                    items.Add(cleanedItem);
                }
                return (changed ? new PdfArray(items) : value, changed, hit);
            }
            if (value is not PdfDictionary dictionary) return (value, false, false);
            int? ownPage = dictionary.TryGetValue(Name("Pg"), out PdfObject? pg) && pg is PdfIndirectReference pageReference
                ? pageReference.ObjectNumber : page;
            if (IsNamed(dictionary, Name("Type"), "MCR"))
            {
                bool inForm = dictionary.ContainsKey(Name("Stm"));
                bool hit = ownPage is int mcrPage && touched.TryGetValue(mcrPage, out var mcrHits)
                    && (mcrHits.Whole || inForm
                        || dictionary.TryGetValue(Name("MCID"), out PdfObject? id) && id is PdfInteger number
                            && mcrHits.Mcids.Contains((int)number.Value));
                return (value, false, hit);
            }
            if (IsNamed(dictionary, Name("Type"), "OBJR")) return (value, false, false);
            bool childChanged = false, childHit = false;
            PdfObject? newKids = null;
            if (dictionary.TryGetValue(Name("K"), out PdfObject? elementKids))
            {
                if (IsRemovedReference(elementKids))
                {
                    childChanged = true;
                    childHit = true;
                    newKids = new PdfArray([]);
                }
                else
                {
                    (newKids, childChanged, childHit) = Clean(elementKids, ownPage, depth + 1, frames + 1);
                }
            }
            // An element with nothing left on a redacted page describes nothing that remains there.
            bool emptied = newKids is not null && Resolve(document, newKids) is PdfArray { Count: 0 }
                && (ownPage is not int elementPage || touched.ContainsKey(elementPage));
            bool strip = (childHit || emptied) && (ElementTextKeys.Any(dictionary.ContainsKey) || dictionary.ContainsKey(AttributesName));
            if (!childChanged && !strip) return (value, false, childHit);
            var entries = dictionary.Where(entry => !strip || !ElementTextKeys.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            if (newKids is not null) entries[Name("K")] = newKids;
            if (strip && dictionary.TryGetValue(AttributesName, out PdfObject? attributes))
            {
                if (WithoutAttributeText(attributes, 0) is { } kept) entries[AttributesName] = kept;
                else entries.Remove(AttributesName);
            }
            return (new PdfDictionary(entries), true, childHit);
        }

        // Attribute objects come direct, indirect, or in an array with revision numbers. One that another
        // element shares is cleaned where it lives, which costs that element a summary and nothing else.
        // A stream can hold an attribute object too, and is left out rather than read.
        PdfObject? WithoutAttributeText(PdfObject value, int depth)
        {
            if (depth > 8) return null;
            switch (value)
            {
                case PdfIndirectReference reference:
                    PdfObject target = document.Resolve(reference);
                    if (target is PdfStream) return null;
                    if (WithoutAttributeText(target, depth + 1) is { } cleaned && !ReferenceEquals(cleaned, target))
                        update.ReplaceObject(reference.ObjectNumber, cleaned);
                    return value;
                case PdfArray array:
                    return new PdfArray(array.Select(item => WithoutAttributeText(item, depth + 1)).OfType<PdfObject>());
                case PdfDictionary attribute:
                    PdfName summary = Name("Summary"), properties = Name("P");
                    bool userProperties = IsNamed(attribute, Name("O"), "UserProperties");
                    if (!attribute.ContainsKey(summary) && !attribute.ContainsKey(Name("UserProperties")) && !userProperties) return value;
                    var entries = attribute.Where(entry => !entry.Key.Equals(summary) && !entry.Key.Equals(Name("UserProperties"))
                        && !(userProperties && entry.Key.Equals(properties))).ToDictionary(entry => entry.Key, entry => entry.Value);
                    if (userProperties) entries[properties] = new PdfArray([]);
                    return new PdfDictionary(entries);
                default:
                    return value;
            }
        }

        // A reference to an annotation that was removed, or into a form or content stream that was replaced.
        bool IsRemovedReference(PdfObject item)
        {
            PdfObject resolved = item is PdfIndirectReference reference ? document.Resolve(reference) : item;
            if (resolved is not PdfDictionary marker) return false;
            if (IsNamed(marker, Name("Type"), "MCR"))
                return marker.TryGetValue(Name("Stm"), out PdfObject? stream) && stream is PdfIndirectReference streamReference
                    && retiredStreams.Contains(streamReference.ObjectNumber);
            return IsNamed(marker, Name("Type"), "OBJR")
                && marker.TryGetValue(Name("Obj"), out PdfObject? target)
                && target is PdfIndirectReference targetReference
                && removedAnnotations.Contains(targetReference.ObjectNumber);
        }
    }

    // An element whose content is gone must not keep the text that described it. Elements of other
    // pages keep theirs, since the redaction says nothing about them.
    private static void InspectStructure(PdfDocument document, PdfPageTree tree, ICollection<int> redactedPages,
        List<PdfRedactionVerificationFinding> findings, CancellationToken cancellationToken)
    {
        if (!tree.Catalog.TryGetValue(StructTreeRootName, out PdfObject? rootValue)
            || Resolve(document, rootValue) is not PdfDictionary root
            || !root.TryGetValue(Name("K"), out PdfObject? kids)) return;
        Dictionary<int, int> pageIndexes = tree.Pages.ToDictionary(page => page.Reference.ObjectNumber, page => page.Index);
        var visited = new HashSet<int>();
        var pending = new Stack<(PdfObject Value, int? Page)>();
        pending.Push((kids, null));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Value is PdfIndirectReference reference && !visited.Add(reference.ObjectNumber)) continue;
            PdfObject value = Resolve(document, item.Value);
            if (value is PdfArray array)
            {
                foreach (PdfObject child in array) pending.Push((child, item.Page));
                continue;
            }
            if (value is not PdfDictionary element || !element.TryGetValue(Name("K"), out PdfObject? content)) continue;
            // A page reference that names no page of the document matches no redacted page.
            int? page = !element.TryGetValue(Name("Pg"), out PdfObject? pg) || pg is not PdfIndirectReference pageReference
                ? item.Page : pageIndexes.GetValueOrDefault(pageReference.ObjectNumber, -1);
            if (Resolve(document, content) is PdfArray { Count: 0 })
            {
                if ((page is not int number || redactedPages.Contains(number))
                    && ElementTextKeys.Any(element.ContainsKey))
                    findings.Add(new("StructureDescription", "A structure element with no content keeps its replacement text.", page));
                continue;
            }
            pending.Push((content, page));
        }
    }

    private static Dictionary<int, PdfContentBounds[]> GroupRegions(IEnumerable<PdfRedactionRegion> regions, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var result = new Dictionary<int, List<PdfContentBounds>>();
        foreach (PdfRedactionRegion region in regions)
        {
            ArgumentNullException.ThrowIfNull(region, nameof(regions));
            PdfContentBounds bounds = region.Bounds;
            if (region.PageIndex < 0 || region.PageIndex >= pageCount)
                throw new ArgumentOutOfRangeException(nameof(regions), "A redaction region refers to a missing page.");
            if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom) || !double.IsFinite(bounds.Right)
                || !double.IsFinite(bounds.Top) || bounds.Width <= 0 || bounds.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(regions), "A redaction region must have a positive finite size.");
            if (region.FillColor is PdfRgbColor fill && !(fill.Red is >= 0 and <= 1 && fill.Green is >= 0 and <= 1
                && fill.Blue is >= 0 and <= 1))
                throw new ArgumentOutOfRangeException(nameof(regions), "A redaction fill color must use components from 0 through 1.");
            if (!result.TryGetValue(region.PageIndex, out List<PdfContentBounds>? list))
                result[region.PageIndex] = list = [];
            list.Add(bounds);
        }
        return result.ToDictionary(item => item.Key, item => item.Value.ToArray());
    }

    private static bool IsPainted(PdfDocument document, PdfExtractedImage image, PdfRedactionRegion[] hits)
    {
        if (image.Source is not PdfStream source || image.Placement is not Matrix placement
            || PdfImageRedaction.Decode(document, source) is not PdfImageRedaction.Samples samples) return false;
        PdfImageRedaction.Plan? plan = PdfImageRedaction.Covers(placement, samples,
            hits.Select(hit => (hit.Bounds, hit.FillColor ?? DefaultFill)));
        return plan is not null && PdfImageRedaction.IsPainted(samples, plan);
    }

    private static Dictionary<int, PdfRedactionRegion[]> GroupCovers(IEnumerable<PdfRedactionRegion> regions) =>
        regions.GroupBy(region => region.PageIndex).ToDictionary(group => group.Key, group => group.ToArray());

    private const string OverlayFontFamily = "Helvetica";
    private const string OverlayFontName = "KPRedactFont";
    private const double MaximumOverlaySize = 12;
    private const double MinimumOverlaySize = 3;
    private const double OverlayPadding = 2;
    private static readonly PdfRgbColor DefaultFill = new(0, 0, 0);

    /// <summary>True when overlay text can be drawn exactly with the standard overlay font.</summary>
    public static bool SupportsOverlayText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PdfWinAnsiText.CanEncode(text);
    }

    private readonly record struct OverlayPlacement(byte[] Text, double Size, double[] TextMatrix);

    // The overlay text, sized to fit and centered, reads upright on the displayed page.
    private static OverlayPlacement? Overlay(PdfRedactionRegion region, PageGeometry geometry)
    {
        string? text = (region.OverlayText ?? region.ReasonCode)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        byte[] bytes = PdfWinAnsiText.Encode(text);
        double unit = PdfWinAnsiText.Width(OverlayFontFamily, bytes);
        PdfContentBounds bounds = region.Bounds;
        bool quarter = geometry.Rotation is 90 or 270;
        double across = quarter ? bounds.Height : bounds.Width, up = quarter ? bounds.Width : bounds.Height;
        double size = Math.Min(MaximumOverlaySize, up * 0.7);
        if (unit > 0) size = Math.Min(size, (across - 2 * OverlayPadding) / unit);
        if (!(size >= MinimumOverlaySize)) return null;
        double u = (across - unit * size) / 2, v = (up - size * 0.7) / 2;
        double x = bounds.Left + geometry.CropLeft, y = bounds.Bottom + geometry.CropBottom;
        double w = bounds.Width, h = bounds.Height;
        (double tx, double ty, double cos, double sin) = geometry.Rotation switch
        {
            90 => (x + w - v, y + u, 0d, 1d),
            180 => (x + w - u, y + h - v, -1d, 0d),
            270 => (x + v, y + h - u, 0d, -1d),
            _ => (x + u, y + v, 1d, 0d)
        };
        return new OverlayPlacement(bytes, size, [cos, sin, -sin, cos, tx, ty]);
    }

    private static void AddCover(List<PdfContentInstruction> instructions, PdfRedactionRegion region, PageGeometry geometry)
    {
        PdfContentBounds bounds = region.Bounds;
        double x = bounds.Left + geometry.CropLeft, y = bounds.Bottom + geometry.CropBottom;
        PdfRgbColor fill = region.FillColor ?? DefaultFill;
        instructions.Add(Op("q"));
        instructions.Add(Op("rg", fill.Red, fill.Green, fill.Blue));
        instructions.Add(Op("re", x, y, bounds.Width, bounds.Height));
        instructions.Add(Op("f"));
        if (Overlay(region, geometry) is OverlayPlacement overlay)
        {
            instructions.Add(Op("re", x, y, bounds.Width, bounds.Height));
            instructions.Add(Op("W"));
            instructions.Add(Op("n"));
            double ink = 0.2126 * fill.Red + 0.7152 * fill.Green + 0.0722 * fill.Blue < 0.5 ? 1 : 0;
            instructions.Add(Op("rg", ink, ink, ink));
            instructions.Add(Op("BT"));
            instructions.Add(new PdfContentInstruction("Tf", 0, [Name(OverlayFontName), new PdfReal(overlay.Size)]));
            instructions.Add(Op("Tm", overlay.TextMatrix));
            instructions.Add(new PdfContentInstruction("Tj", 0, [new PdfString(overlay.Text, PdfStringForm.Hexadecimal)]));
            instructions.Add(Op("ET"));
        }
        instructions.Add(Op("Q"));
    }

    // Text in a redacted area fails verification unless it is exactly the overlay text drawn there:
    // every letter must sit in an area with overlay text, and together they must spell those texts.
    private static bool TextRemains(IReadOnlyList<PdfExtractedLetter> letters, PdfContentBounds[] regions,
        PdfRedactionRegion[] covers, PageGeometry geometry)
    {
        PdfContentBounds[] overlaid = [.. covers.Where(cover => Overlay(cover, geometry) is not null)
            .Select(cover => cover.Bounds)];
        var found = new List<char>();
        foreach (PdfExtractedLetter letter in letters)
        {
            if (!regions.Any(region => Overlaps(letter.BoundingBox, region))) continue;
            if (!overlaid.Any(region => ContainsCenter(letter.BoundingBox, region))) return true;
            found.AddRange(letter.Value.Where(character => !char.IsWhiteSpace(character)));
        }
        if (found.Count == 0) return false;
        var expected = new List<char>();
        foreach (PdfRedactionRegion cover in covers)
            if (Overlay(cover, geometry) is OverlayPlacement overlay)
                expected.AddRange(PdfWinAnsiText.Decode(overlay.Text).Where(character => !char.IsWhiteSpace(character)));
        found.Sort();
        expected.Sort();
        // Letters may only come from the overlays, never more than they draw.
        int next = 0;
        foreach (char character in found)
        {
            while (next < expected.Count && expected[next] < character) next++;
            if (next >= expected.Count || expected[next] != character) return true;
            next++;
        }
        return false;
    }

    // The new bytes of a form on a raster page match nothing the redaction recorded, so the areas its
    // draws hid are checked directly. An overlay is the cover's own, and TextRemains accounts for it.
    private static bool HiddenTextRemains(IReadOnlyList<PdfExtractedLetter> letters, HiddenArea[] hidden,
        PdfRedactionRegion[] covers, PageGeometry geometry)
    {
        if (hidden.Length == 0) return false;
        PdfContentBounds[] overlaid = [.. covers.Where(cover => Overlay(cover, geometry) is not null)
            .Select(cover => cover.Bounds)];
        return letters.Any(letter => !overlaid.Any(region => ContainsCenter(letter.BoundingBox, region))
            && ContentRewriter.Hidden(letter.BoundingBox, hidden) != HiddenOverlap.None);
    }

    internal readonly record struct PageGeometry(double CropLeft, double CropBottom, double Width, double Height, int Rotation);

    private static PageGeometry Geometry(PdfDocument document, PdfPageTreeEntry page)
    {
        PdfContentBounds box = PdfPageInformation.EffectiveBox(document, page);
        int rotation = page.InheritedValues.TryGetValue(Name("Rotate"), out PdfObject? rotate)
            && Resolve(document, rotate) is PdfInteger degrees ? (int)(((degrees.Value % 360) + 360) % 360) : 0;
        if (rotation % 90 != 0) rotation = 0;
        return new PageGeometry(box.Left, box.Bottom, box.Width, box.Height, rotation);
    }

    private static PdfDictionary WithEntry(PdfDictionary resources, PdfName category, PdfName key,
        PdfObject value, PdfDocument document)
    {
        var entries = resources.ToDictionary(entry => entry.Key, entry => entry.Value);
        var inner = entries.TryGetValue(category, out PdfObject? existing) && Resolve(document, existing) is PdfDictionary dictionary
            ? dictionary.ToDictionary(entry => entry.Key, entry => entry.Value) : [];
        inner[key] = value;
        entries[category] = new PdfDictionary(inner);
        return new PdfDictionary(entries);
    }

    // Overlaps smaller than this are rounding noise from rewritten text positions, not visible content.
    internal const double OverlapTolerance = 1e-4;

    internal static bool Overlaps(PdfContentBounds content, PdfContentBounds region) =>
        content.Right > region.Left + OverlapTolerance && content.Left < region.Right - OverlapTolerance
        && content.Top > region.Bottom + OverlapTolerance && content.Bottom < region.Top - OverlapTolerance;

    private static bool OverlapsOrTouches(PdfContentBounds content, PdfContentBounds region) =>
        content.Right >= region.Left && content.Left <= region.Right
        && content.Top >= region.Bottom && content.Bottom <= region.Top;

    private static bool ContainsCenter(PdfContentBounds content, PdfContentBounds region)
    {
        double x = (content.Left + content.Right) / 2, y = (content.Bottom + content.Top) / 2;
        return x >= region.Left && x <= region.Right && y >= region.Bottom && y <= region.Top;
    }

    private static bool Inside(PdfContentBounds content, PdfContentBounds region) =>
        content.Left >= region.Left && content.Right <= region.Right
        && content.Bottom >= region.Bottom && content.Top <= region.Top;

    private static bool IsNamed(PdfDictionary dictionary, PdfName key, string value) =>
        dictionary.TryGetValue(key, out PdfObject? item) && item is PdfName name && name.ValueAsLatin1() == value;

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32) throw new FormatException("Reference nesting limit exceeded.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static double Number(PdfDocument document, PdfObject value) => Resolve(document, value) switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new FormatException("Expected a number.")
    };

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static PdfContentInstruction Op(string op, params double[] values) =>
        new(op, 0, values.Select(value => value == Math.Truncate(value) && Math.Abs(value) < 1e15
            ? (PdfObject)new PdfInteger((long)value) : new PdfReal(value)));

    /// <summary>Resource names used by one rewritten content scope.</summary>
    private sealed class ResourceScope(PdfDocument document, PdfDictionary resources)
    {
        private readonly Dictionary<PdfName, HashSet<PdfName>> _used = [];
        private readonly Dictionary<PdfName, Dictionary<PdfName, PdfObject>> _added = [];
        private readonly HashSet<PdfName> _replacedProperties = [];
        private int _counter;

        internal PdfDictionary Resources { get; } = resources;
        internal bool KeepEverything { get; set; }
        // Content this scope cannot read the names of draws with these resources, so they all
        // stay, except a description the redaction replaced.
        internal bool KeepInherited { get; init; }
        internal bool ReplacedNamedDescription => _replacedProperties.Count != 0;

        internal void ReplaceDescription(PdfName name) => _replacedProperties.Add(name);

        internal PdfObject? Lookup(PdfName category, PdfName key) =>
            Resources.TryGetValue(category, out PdfObject? value) && Resolve(document, value) is PdfDictionary items
            && items.TryGetValue(key, out PdfObject? item) ? item : null;

        internal void Use(PdfName category, PdfName key)
        {
            if (!_used.TryGetValue(category, out HashSet<PdfName>? names)) _used[category] = names = [];
            names.Add(key);
        }

        internal PdfName Add(PdfName category, string prefix, PdfObject value)
        {
            PdfName key;
            do key = Name(prefix + ++_counter);
            while (Lookup(category, key) is not null
                || _added.TryGetValue(category, out var existing) && existing.ContainsKey(key));
            if (!_added.TryGetValue(category, out Dictionary<PdfName, PdfObject>? entries))
                _added[category] = entries = [];
            entries[key] = value;
            return key;
        }

        internal PdfDictionary Build()
        {
            var entries = new Dictionary<PdfName, PdfObject>();
            foreach (KeyValuePair<PdfName, PdfObject> entry in Resources)
            {
                if (KeepEverything || !PrunedCategories.Contains(entry.Key))
                {
                    entries[entry.Key] = entry.Value;
                    continue;
                }
                if (Resolve(document, entry.Value) is not PdfDictionary items) continue;
                HashSet<PdfName> used = _used.GetValueOrDefault(entry.Key) ?? [];
                bool properties = entry.Key.Equals(Name("Properties"));
                var kept = items.Where(item => used.Contains(item.Key)
                        || KeepInherited && !(properties && _replacedProperties.Contains(item.Key)))
                    .ToDictionary(item => item.Key, item => item.Value);
                if (kept.Count != 0) entries[entry.Key] = new PdfDictionary(kept);
            }
            foreach ((PdfName category, Dictionary<PdfName, PdfObject> added) in _added)
            {
                var merged = entries.TryGetValue(category, out PdfObject? existing) && Resolve(document, existing) is PdfDictionary items
                    ? items.ToDictionary(item => item.Key, item => item.Value) : [];
                foreach ((PdfName key, PdfObject value) in added) merged[key] = value;
                entries[category] = new PdfDictionary(merged);
            }
            return new PdfDictionary(entries);
        }
    }

    private sealed record TextState(Matrix Ctm, PdfContentBounds Clip, PdfExtractionFont? Font = null,
        bool FontMissing = false, double Size = 0, double CharacterSpacing = 0, double WordSpacing = 0,
        double Scale = 1, double Leading = 0, double Rise = 0)
    {
        // The font as a resource value and, when Tf selected it, its resource name.
        internal PdfObject? FontObject { get; init; }
        internal PdfName? FontResource { get; init; }
        // The operators that set the current nonstroking color, replayed to restore it.
        internal PdfContentInstruction[] Fill { get; init; } = [];
        // A Form can shadow color names without changing the inherited current color.
        internal ResourceScope? FillScope { get; init; }
        // The graphics states applied so far, as an id the rewriter hands out for each distinct
        // sequence, so paints can be compared without reading each one.
        internal int ExtGStates { get; init; }
        // False once the clip comes from anything but one axis-aligned rectangle, since the
        // clip bounds then say less than the real clip does.
        internal bool ClipIsRectangle { get; init; } = true;
        internal bool SoftMasked { get; init; }
        internal double? NonstrokingAlpha { get; init; } = 1;
        internal string? BlendMode { get; init; } = "Normal";
        internal bool HighlightPaintKnown { get; init; } = true;
        internal int RenderMode { get; init; }
        internal double LineWidth { get; init; } = 1;
        internal double MiterLimit { get; init; } = 10;
        internal double LineJoin { get; init; }
        internal double LineCap { get; init; }
    }

    /// <summary>Rewrites page and form content so nothing drawn inside a region survives.</summary>
    /// <remarks>With <paramref name="glyphsOnly"/> only text goes: a glyph whose center lies in a
    /// region is removed, while images, drawings and every resource stay as they were. When
    /// <paramref name="glyphFilter"/> is given, a glyph in a region stays unless the filter accepts its
    /// bounds, its state and its text matrix.</remarks>
    private sealed class ContentRewriter(PdfDocument document, PdfIncrementalUpdateBuilder? update,
        PdfContentBounds[] regions, bool textOnly, Dictionary<PdfDictionary, PdfExtractionFont?> fonts,
        CancellationToken cancellationToken, bool glyphsOnly = false, PdfRgbColor[]? fills = null,
        LineInsertion? insertion = null, Dictionary<object, Dictionary<uint, PdfDecodedCharacter>>? usedCodes = null,
        RetiredContent? retired = null, Func<PdfContentBounds, TextState, Matrix, bool>? glyphFilter = null)
    {
        private static readonly PdfName FontCategory = Name("Font");
        private static readonly PdfName XObjectCategory = Name("XObject");
        private static readonly PdfName ExtGStateCategory = Name("ExtGState");
        private static readonly PdfName ColorSpaceCategory = Name("ColorSpace");
        private static readonly PdfName PatternCategory = Name("Pattern");
        private static readonly PdfName ShadingCategory = Name("Shading");
        private static readonly PdfName PropertiesCategory = Name("Properties");
        private readonly HashSet<PdfStream> _activeForms = new(ReferenceEqualityComparer.Instance);
        private int _instructions;
        private readonly Dictionary<(int Previous, string Name), int> _graphicsStateIds = [];
        private readonly List<string> _graphicsStateNames = [""];
        // Retired content that the draw being rewritten must not hide.
        private HiddenArea[] _hidden = [];
        // How many draws of retired content have been met, to tell a form that holds none.
        private int _retiredDraws;
        private readonly Dictionary<PdfDictionary, bool> _wholeResources = new(ReferenceEqualityComparer.Instance);
        private const int MaximumHighlightProfileBytes = 4 * 1024 * 1024;
        private int _highlightProfileBytes;
        private readonly Dictionary<PdfStream, PdfColorProfile?> _highlightProfiles = new(ReferenceEqualityComparer.Instance);

        internal HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        // Every retired area the draws of this page hid, for the check of a page image's invisible layer.
        internal HashSet<HiddenArea> HiddenAreas { get; } = [];
        internal HashSet<int> RetiredSourceStreams { get; } = [];
        internal HashSet<string> RetiredDescriptions { get; } = new(StringComparer.Ordinal);
        internal HashSet<int> TouchedMcids { get; } = [];
        internal bool FormMarkedContentTouched { get; private set; }
        internal int RemovedGlyphs { get; private set; }
        internal int RemovedImages { get; private set; }
        internal int RedactedImages { get; private set; }
        internal int RemovedPaths { get; private set; }
        internal bool ContentChanged { get; private set; }
        internal PdfContentBounds[] HiddenPageAreas { get; private set; } = [];
        internal ResourceScope? PageScope { get; private set; }

        private bool Recording => retired is { Sealed: false };
        private bool Judging => retired is { Sealed: true };

        // A form is often drawn many times, and what it lists does not change between draws.
        private bool KeepsWholeResources(PdfDictionary resources)
        {
            if (!_wholeResources.TryGetValue(resources, out bool whole))
                _wholeResources[resources] = whole = PdfInheritedResources.ConsumesPageResources(document, resources);
            return whole;
        }

        internal List<PdfContentInstruction> RewritePage(PdfPageTreeEntry page, PageGeometry geometry,
            IReadOnlyList<PdfRedactionRegion>? excludedCovers = null)
        {
            PdfDictionary resources = page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? value)
                && Resolve(document, value) is PdfDictionary dictionary ? dictionary : new PdfDictionary([]);
            PageScope = new ResourceScope(document, resources)
            {
                KeepEverything = glyphsOnly,
                KeepInherited = PdfInheritedResources.ConsumesPageResources(document, resources, page.Dictionary)
            };
            byte[] bytes = page.Dictionary.TryGetValue(ContentsName, out PdfObject? contents) ? PageBytes(contents) : [];
            if (excludedCovers is not null)
            {
                // Exempt only the exact appended covers, never an earlier matching source path.
                var expected = new List<PdfContentInstruction>();
                foreach (PdfRedactionRegion cover in excludedCovers) AddCover(expected, cover, geometry);
                IReadOnlyList<PdfContentInstruction> instructions = PdfContentStreamReader.Read(bytes,
                    resolveColorComponents: name => ColorComponents(PageScope, name),
                    cancellationToken: cancellationToken, compatibilityRecovery: document.UsesCompatibilityRecovery);
                int start = instructions.Count - expected.Count;
                if (start >= 0 && PdfContentStreamWriter.Write(instructions.Skip(start)).AsSpan()
                        .SequenceEqual(PdfContentStreamWriter.Write(expected)))
                    bytes = PdfContentStreamWriter.Write(instructions.Take(start));
                else
                    Reasons.Add("RedactionCover");
            }
            var initial = new TextState(new Matrix(1, 0, 0, 1, -geometry.CropLeft, -geometry.CropBottom),
                new PdfContentBounds(0, 0, geometry.Width, geometry.Height))
            { HighlightPaintKnown = !page.Dictionary.ContainsKey(Name("Group")) };
            if (Recording) retired!.CoverPage(bytes, resources, regions.Select(region => new PdfContentBounds(
                region.Left + geometry.CropLeft, region.Bottom + geometry.CropBottom,
                region.Right + geometry.CropLeft, region.Top + geometry.CropBottom)));
            else if (Judging)
            {
                HiddenPageAreas = retired!.PageAreas(bytes, resources, geometry);
                _hidden = [.. HiddenPageAreas.Select(area => new HiddenArea(area, initial.Clip, true))];
                if (textOnly) HiddenAreas.UnionWith(_hidden);
            }
            List<PdfContentInstruction> content = Rewrite(bytes, PageScope, initial, 0, out bool changed);
            ContentChanged = changed;
            return content;
        }

        // The covered part of a draw in its own space: each region it overlaps, cut to what it shows,
        // through the inverse of its matrix. A draw that cannot be inverted is covered whole.
        private IEnumerable<PdfContentBounds> OwnAreas(PdfContentBounds visible, Matrix matrix)
        {
            if (visible.Width <= 0 || visible.Height <= 0) yield break;
            foreach (PdfContentBounds region in regions)
                if (Overlaps(visible, region)) yield return ThroughInverse(Intersect(visible, region), matrix);
        }

        private static PdfContentBounds ThroughInverse(PdfContentBounds area, Matrix matrix)
        {
            double determinant = matrix.A * matrix.D - matrix.B * matrix.C;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
                return new PdfContentBounds(-1e9, -1e9, 1e9, 1e9);
            var inverse = new Matrix(matrix.D / determinant, -matrix.B / determinant, -matrix.C / determinant,
                matrix.A / determinant, (matrix.C * matrix.F - matrix.D * matrix.E) / determinant,
                (matrix.B * matrix.E - matrix.A * matrix.F) / determinant);
            return Transform(area, inverse);
        }

        private byte[] PageBytes(PdfObject contents)
        {
            using var output = new MemoryStream();
            PdfObject resolved = Resolve(document, contents);
            IEnumerable<PdfObject> items = resolved is PdfArray array ? array : [resolved];
            foreach (PdfObject item in items)
            {
                if (Resolve(document, item) is not PdfStream stream) continue;
                output.Write(document.DecodeStream(stream, PdfContentStreamReader.MaximumSourceBytes));
                output.WriteByte((byte)'\n');
                if (output.Length > PdfContentStreamReader.MaximumSourceBytes)
                    throw new FormatException("Page content exceeds the redaction limit.");
            }
            return output.ToArray();
        }

        private List<PdfContentInstruction> Rewrite(byte[] bytes, ResourceScope scope, TextState initial,
            int depth, out bool changed)
        {
            changed = false;
            var output = new List<PdfContentInstruction>();
            var stack = new Stack<TextState>();
            var marked = new Stack<(int OutputIndex, PdfDictionary? Properties, bool Touched, int? Mcid)>();
            var pendingPath = new List<PdfContentInstruction>();
            var pathPoints = new List<PdfPoint>();
            // Where in pathPoints each subpath starts.
            var subpaths = new List<int>();
            bool pendingClip = false;
            TextState state = initial;
            Matrix text = Matrix.Identity, line = Matrix.Identity;
            bool insideText = false;
            IReadOnlyList<PdfContentInstruction> instructions = PdfContentStreamReader.Read(bytes,
                resolveColorComponents: name => ColorComponents(scope, name),
                compatibilityRecovery: document.UsesCompatibilityRecovery, cancellationToken: cancellationToken);
            bool localChanged = false;
            // A page with neither regions nor hidden areas here is only looked at for what it draws.
            bool drawsOnly = Judging && regions.Length == 0 && _hidden.Length == 0 && !textOnly;
            foreach (PdfContentInstruction instruction in instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++_instructions > 2_000_000) throw new FormatException("Redaction instruction limit exceeded.");
                IReadOnlyList<PdfObject> args = instruction.Operands;
                switch (instruction.Operator)
                {
                    case "q":
                        stack.Push(state); Emit(instruction); break;
                    case "Q":
                        if (stack.TryPop(out TextState? restored)) state = restored;
                        Emit(instruction); break;
                    case "cm":
                        if (args.Count == 6)
                            state = state with { Ctm = new Matrix(N(args[0]), N(args[1]), N(args[2]), N(args[3]), N(args[4]), N(args[5])).Then(state.Ctm) };
                        Emit(instruction); break;
                    case "BT":
                        insideText = true; text = line = Matrix.Identity; Emit(instruction);
                        if (textOnly) { output.Add(Op("Tr", 3)); localChanged = true; }
                        break;
                    case "ET":
                        insideText = false; Emit(instruction);
                        if (insertion?.AfterText is { } after && insertion.AfterTextDepth == depth)
                        {
                            output.AddRange(after);
                            insertion.AfterText = null;
                        }
                        break;
                    case "Tf":
                        if (args.Count == 2 && args[0] is PdfName fontName)
                        {
                            scope.Use(FontCategory, fontName);
                            PdfObject? fontObject = scope.Lookup(FontCategory, fontName);
                            PdfExtractionFont? font = ReadFont(fontObject);
                            state = state with
                            {
                                Font = font, FontMissing = font is null, Size = N(args[1]),
                                FontObject = fontObject, FontResource = fontName
                            };
                        }
                        Emit(instruction); break;
                    case "Tc": state = state with { CharacterSpacing = Arg(args, 0) }; Emit(instruction); break;
                    case "Tw": state = state with { WordSpacing = Arg(args, 0) }; Emit(instruction); break;
                    case "Tz": state = state with { Scale = Arg(args, 0) / 100 }; Emit(instruction); break;
                    case "TL": state = state with { Leading = Arg(args, 0) }; Emit(instruction); break;
                    case "Ts": state = state with { Rise = Arg(args, 0) }; Emit(instruction); break;
                    case "Tr":
                        if (textOnly) { output.Add(Op("Tr", 3)); localChanged = true; }
                        else
                        {
                            // Modes 4 to 7 add the text to the clip, which is never a rectangle.
                            if (args.Count == 1)
                                state = state with
                                {
                                    RenderMode = (int)N(args[0]),
                                    ClipIsRectangle = state.ClipIsRectangle && N(args[0]) is < 4 or > 7
                                };
                            Emit(instruction);
                        }
                        break;
                    case "Tm":
                        if (args.Count == 6) text = line = new Matrix(N(args[0]), N(args[1]), N(args[2]), N(args[3]), N(args[4]), N(args[5]));
                        Emit(instruction); break;
                    case "Td":
                        if (args.Count == 2) text = line = line.Move(N(args[0]), N(args[1]));
                        Emit(instruction); break;
                    case "TD":
                        if (args.Count == 2) { state = state with { Leading = -N(args[1]) }; text = line = line.Move(N(args[0]), N(args[1])); }
                        Emit(instruction); break;
                    case "T*":
                        text = line = line.Move(0, -state.Leading); Emit(instruction); break;
                    case "Tj":
                        if (args.Count == 1) ShowAndEmit(instruction, [], args[0]);
                        else Emit(instruction);
                        break;
                    case "'":
                        if (args.Count != 1) { Emit(instruction); break; }
                        text = line = line.Move(0, -state.Leading);
                        ShowAndEmit(instruction, [Op("T*")], args[0]);
                        break;
                    case "\"":
                        if (args.Count != 3) { Emit(instruction); break; }
                        state = state with { WordSpacing = N(args[0]), CharacterSpacing = N(args[1]) };
                        text = line = line.Move(0, -state.Leading);
                        ShowAndEmit(instruction, [new PdfContentInstruction("Tw", 0, [args[0]]),
                            new PdfContentInstruction("Tc", 0, [args[1]]), Op("T*")], args[2]);
                        break;
                    case "TJ":
                        if (args.Count == 1 && args[0] is PdfArray array) ShowArray(instruction, array);
                        else Emit(instruction);
                        break;
                    case "gs":
                        GraphicsState(instruction);
                        break;
                    case "ri":
                        if (textOnly) { localChanged = true; break; }
                        if (args.Count != 1 || args[0] is not PdfName renderingIntent
                            || renderingIntent.ValueAsLatin1() != "RelativeColorimetric")
                            state = state with { HighlightPaintKnown = false };
                        Emit(instruction); break;
                    case "Do":
                        Draw(instruction);
                        break;
                    case "BI":
                        InlineImage(instruction);
                        break;
                    case "sh":
                        if (args.Count == 1 && args[0] is PdfName shadingName)
                        {
                            if (textOnly) { MarkRemoved(); break; }
                            insertion?.Paint(state.Clip);
                            if (!glyphsOnly && regions.Any(region => Overlaps(state.Clip, region)))
                            {
                                if (regions.Any(region => Inside(state.Clip, region))) { MarkRemoved(); RemovedPaths++; break; }
                                Reasons.Add("Shading");
                            }
                            scope.Use(ShadingCategory, shadingName);
                        }
                        Emit(instruction); break;
                    case "m": case "l": case "c": case "v": case "y": case "re": case "h":
                        PathSegment(instruction); break;
                    case "W": case "W*":
                        pendingClip = true; pendingPath.Add(instruction); break;
                    case "n": case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*":
                        PaintPath(instruction); break;
                    case "CS": case "cs":
                        if (textOnly) { localChanged = true; break; }
                        if (args.Count == 1 && args[0] is PdfName space && !IsDeviceSpace(space))
                            scope.Use(ColorSpaceCategory, space);
                        if (instruction.Operator == "cs") state = state with { Fill = [instruction], FillScope = scope };
                        Emit(instruction); break;
                    case "SC": case "SCN": case "sc": case "scn": case "G": case "g": case "RG": case "rg": case "K": case "k":
                        if (textOnly) { localChanged = true; break; }
                        if (args.Count > 0 && args[^1] is PdfName pattern) scope.Use(PatternCategory, pattern);
                        if (instruction.Operator is "g" or "rg" or "k")
                            state = state with { Fill = [instruction], FillScope = scope };
                        else if (instruction.Operator is "sc" or "scn")
                            state = state with
                            {
                                Fill = state.Fill.Length > 0 && state.Fill[0].Operator == "cs"
                                    ? [state.Fill[0], instruction] : [instruction]
                            };
                        Emit(instruction); break;
                    case "BMC":
                        marked.Push((output.Count, null, false, null)); Emit(instruction); break;
                    case "BDC":
                        MarkedContent(instruction); break;
                    case "EMC":
                        EndMarkedContent(); Emit(instruction); break;
                    case "MP":
                        Emit(instruction); break;
                    case "DP":
                        if (args.Count == 2 && args[1] is PdfName point) scope.Use(PropertiesCategory, point);
                        Emit(instruction); break;
                    case "w": case "J": case "j": case "M":
                        if (textOnly) { localChanged = true; break; }
                        if (!glyphsOnly || args.Count == 1 && args[0] is PdfInteger or PdfReal) state = instruction.Operator switch
                        {
                            "w" => state with { LineWidth = Arg(args, 0) },
                            "J" => state with { LineCap = Arg(args, 0) },
                            "j" => state with { LineJoin = Arg(args, 0) },
                            _ => state with { MiterLimit = Arg(args, 0) }
                        };
                        Emit(instruction); break;
                    case "d": case "i":
                        if (textOnly) { localChanged = true; break; }
                        Emit(instruction); break;
                    case "BX": case "EX": case "d0": case "d1":
                        Emit(instruction); break;
                    default:
                        if (args.Any(item => item is PdfName)) scope.KeepEverything = true;
                        Emit(instruction); break;
                }
            }
            while (marked.Count > 0) EndMarkedContent();
            if (!glyphsOnly && scope.KeepEverything && scope.ReplacedNamedDescription)
            {
                Reasons.Add("MarkedContentResource");
                if (Judging) Reasons.Add(SharedContentReason);
            }
            changed = localChanged;
            return output;

            void Emit(PdfContentInstruction instruction) => output.Add(instruction);

            double N(PdfObject value) => value switch
            {
                PdfInteger integer => integer.Value,
                PdfReal real => real.Value,
                _ => throw new FormatException("Expected a numeric content operand.")
            };

            double Arg(IReadOnlyList<PdfObject> values, int index) =>
                values.Count > index ? N(values[index]) : throw new FormatException("Missing content operand.");

            void MarkRemoved()
            {
                localChanged = true;
                var frames = marked.ToArray();
                marked.Clear();
                for (int index = frames.Length - 1; index >= 0; index--)
                {
                    var frame = frames[index];
                    if (frame.Mcid is int mcid)
                    {
                        if (depth == 0) TouchedMcids.Add(mcid);
                        else FormMarkedContentTouched = true;
                    }
                    marked.Push(frame with { Touched = true });
                }
                if (depth > 0 && frames.Length == 0) FormMarkedContentTouched = true;
            }

            // Forms are reusable, a layer can hide the text, and replacement text would go stale.
            bool BarredFromParagraphs() => depth > 0 || marked.Any(frame => frame.Properties is { } properties
                && (ReplacementTextKeys.Any(properties.ContainsKey)
                    || IsNamed(properties, Name("Type"), "OCG") || IsNamed(properties, Name("Type"), "OCMD")));

            void MarkedContent(PdfContentInstruction instruction)
            {
                IReadOnlyList<PdfObject> args = instruction.Operands;
                PdfDictionary? properties = null;
                if (args.Count == 2 && args[1] is PdfDictionary inline) properties = inline;
                else if (args.Count == 2 && args[1] is PdfName propertyName)
                {
                    properties = scope.Lookup(PropertiesCategory, propertyName) is { } found
                        ? Resolve(document, found) as PdfDictionary : null;
                }
                int? mcid = properties is not null && properties.TryGetValue(Name("MCID"), out PdfObject? id)
                    && id is PdfInteger number ? (int)number.Value : null;
                marked.Push((output.Count, properties, false, mcid));
                Emit(instruction);
            }

            void EndMarkedContent()
            {
                if (!marked.TryPop(out var frame)) return;
                PdfContentInstruction original = output[frame.OutputIndex];
                if (!frame.Touched || frame.Properties is null
                    || !ReplacementTextKeys.Any(frame.Properties.ContainsKey))
                {
                    KeepNamedProperty();
                    return;
                }
                var stripped = frame.Properties.Where(entry => !ReplacementTextKeys.Contains(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value);
                PdfObject sanitized = stripped.Values.Any(ContainsReference)
                    ? scope.Add(PropertiesCategory, "KPRP", new PdfDictionary(stripped))
                    : new PdfDictionary(stripped);
                output[frame.OutputIndex] = new PdfContentInstruction("BDC", 0,
                    [original.Operands[0], sanitized]);
                RetiredDescriptions.Add(DescriptionIdentity(frame.Properties));
                if (original.Operands.Count == 2 && original.Operands[1] is PdfName replaced)
                {
                    scope.ReplaceDescription(replaced);
                }
                localChanged = true;

                void KeepNamedProperty()
                {
                    if (original.Operands.Count == 2 && original.Operands[1] is PdfName name)
                        scope.Use(PropertiesCategory, name);
                }
            }

            void ShowAndEmit(PdfContentInstruction instruction, PdfContentInstruction[] prefix, PdfObject value)
            {
                if (!insideText || drawsOnly || value is not PdfString source) { Emit(instruction); return; }
                List<PdfObject>? items = Show(source, out bool removed);
                if (!removed || items is null) { Emit(instruction); return; }
                output.AddRange(prefix);
                output.Add(new PdfContentInstruction("TJ", 0, [new PdfArray(Merge(items))]));
                insertion?.WritePending(output, state, text, line, scope, depth);
            }

            void ShowArray(PdfContentInstruction instruction, PdfArray array)
            {
                if (!insideText || drawsOnly) { Emit(instruction); return; }
                var items = new List<PdfObject>();
                bool removedAny = false;
                foreach (PdfObject item in array)
                {
                    if (item is PdfString source)
                    {
                        List<PdfObject>? shown = Show(source, out bool removed);
                        removedAny |= removed;
                        if (shown is null) items.Add(item);
                        else items.AddRange(shown);
                    }
                    else if (item is PdfInteger or PdfReal)
                    {
                        double adjustment = N(item);
                        text = state.Font?.IsVertical == true ? text.Move(0, -adjustment / 1000 * state.Size)
                            : text.Move(-adjustment / 1000 * state.Size * state.Scale, 0);
                        items.Add(item);
                    }
                }
                if (!removedAny) { Emit(instruction); return; }
                output.Add(new PdfContentInstruction("TJ", 0, [new PdfArray(Merge(items))]));
                insertion?.WritePending(output, state, text, line, scope, depth);
            }

            List<PdfObject>? Show(PdfString source, out bool removed)
            {
                removed = false;
                PdfExtractionFont? font = state.Font;
                if (font is null)
                {
                    if (source.Bytes.Length > 0)
                    {
                        Reasons.Add("UnreadableFont");
                        RefuseUnplacedText();
                    }
                    return null;
                }
                IReadOnlyList<PdfDecodedCharacter> characters;
                try { characters = font.Decode(source.Bytes); }
                catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException)
                {
                    Reasons.Add("UndecodableText");
                    RefuseUnplacedText();
                    return null;
                }
                if (characters.Sum(character => character.ByteLength) != source.Bytes.Length)
                {
                    Reasons.Add("UndecodableText");
                    RefuseUnplacedText();
                    return null;
                }
                var items = new List<PdfObject>();
                var kept = new List<byte>();
                int offset = 0;
                Dictionary<uint, PdfDecodedCharacter>? used = null;
                if (usedCodes is not null && FontKey(document, state.FontObject) is { } key
                    && !usedCodes.TryGetValue(key, out used))
                    usedCodes[key] = used = [];
                foreach (PdfDecodedCharacter character in characters)
                {
                    used?.TryAdd(character.Code, character);
                    double width = font.GetWidth(character.Code) / 1000 * state.Size * state.Scale;
                    PdfVerticalGlyphMetrics vertical = font.GetVerticalMetrics(character.Code);
                    double advanceY = font.IsVertical ? vertical.Advance / 1000 * state.Size : 0;
                    double originX = font.IsVertical ? -vertical.OriginX / 1000 * state.Size * state.Scale : 0;
                    double originY = font.IsVertical ? -vertical.OriginY / 1000 * state.Size : 0;
                    Matrix transform = text.Then(state.Ctm);
                    PdfGlyphBounds? glyph = font.GetGlyphBounds(character.Code);
                    double left = (glyph?.Left ?? 0) / 1000 * state.Size * state.Scale + originX;
                    double right = (glyph?.Right ?? font.GetWidth(character.Code)) / 1000 * state.Size * state.Scale + originX;
                    double bottom = (glyph?.Bottom ?? font.Descent) / 1000 * state.Size + state.Rise + originY;
                    double top = (glyph?.Top ?? font.Ascent) / 1000 * state.Size + state.Rise + originY;
                    PdfPoint[] corners = [transform.Point(left, bottom), transform.Point(right, bottom),
                        transform.Point(left, top), transform.Point(right, top)];
                    var bounds = new PdfContentBounds(corners.Min(p => p.X), corners.Min(p => p.Y),
                        corners.Max(p => p.X), corners.Max(p => p.Y));
                    double spacing = state.CharacterSpacing;
                    if (character.ByteLength == 1 && character.Code == 32) spacing += state.WordSpacing;
                    bool covered = glyphsOnly
                        ? regions.Any(region => ContainsCenter(bounds, region))
                        : regions.Any(region => Overlaps(bounds, region));
                    if (covered && glyphFilter is not null) covered = glyphFilter(bounds, state, text);
                    // A glyph this draw shows stays, even with part of it past the edge of what it shows.
                    // The page image of a raster page keeps what shows, so its invisible layer only
                    // keeps a glyph the draw shows whole.
                    if (!covered)
                    {
                        HiddenOverlap part = HiddenPart(bounds);
                        if (part == HiddenOverlap.Whole || textOnly && part != HiddenOverlap.None) covered = true;
                        else if (part == HiddenOverlap.Unclear) Reasons.Add(SharedContentReason);
                    }
                    if (covered)
                    {
                        if (state.Size == 0) Reasons.Add("ZeroSizeText");
                        insertion?.Observe(state, text, BarredFromParagraphs(), bounds);
                        Flush();
                        double displacement = font.IsVertical
                            ? vertical.Advance + (state.Size == 0 ? 0 : spacing * 1000 / state.Size)
                            : font.GetWidth(character.Code) + (state.Size == 0 ? 0 : spacing * 1000 / state.Size);
                        items.Add(new PdfReal(-displacement));
                        removed = true;
                        RemovedGlyphs++;
                    }
                    else
                    {
                        kept.AddRange(source.Bytes.Slice(offset, character.ByteLength).ToArray());
                        // Text painted after a replacement is opaque too, where it leaves a mark. The middle of
                        // the glyph stands for it: the next line's ascenders can reach into the box around this
                        // one's descenders without either touching the other.
                        if (insertion is not null && state.RenderMode is not (3 or 7) && character.Text.Any(letter => !char.IsWhiteSpace(letter))
                            && Visible(bounds) is { Width: > 0, Height: > 0 } shown)
                            insertion.Paint(new PdfContentBounds(shown.Left + shown.Width * 0.2, shown.Bottom + shown.Height * 0.2,
                                shown.Right - shown.Width * 0.2, shown.Top - shown.Height * 0.2));
                    }
                    offset += character.ByteLength;
                    text = font.IsVertical ? text.Move(0, advanceY + spacing) : text.Move(width + spacing * state.Scale, 0);
                }
                Flush();
                if (removed) MarkRemoved();
                return items;

                void Flush()
                {
                    if (kept.Count == 0) return;
                    items.Add(new PdfString(kept.ToArray(), source.Form));
                    kept.Clear();
                }
            }

            void GraphicsState(PdfContentInstruction instruction)
            {
                IReadOnlyList<PdfObject> args = instruction.Operands;
                if (args.Count != 1 || args[0] is not PdfName name)
                {
                    state = state with { HighlightPaintKnown = false };
                    Emit(instruction); return;
                }
                PdfDictionary? gs = scope.Lookup(ExtGStateCategory, name) is { } value
                    ? Resolve(document, value) as PdfDictionary : null;
                if (gs is not null)
                {
                    foreach (string transfer in new[] { "TR", "TR2" })
                        if (gs.TryGetValue(Name(transfer), out PdfObject? function)
                            && (Resolve(document, function) is not PdfName identity || identity.ValueAsLatin1() != "Identity"))
                            state = state with { HighlightPaintKnown = false };
                    foreach (string overprint in new[] { "OP", "op" })
                        if (gs.TryGetValue(Name(overprint), out PdfObject? enabled)
                            && Resolve(document, enabled) is not PdfBoolean { Value: false })
                            state = state with { HighlightPaintKnown = false };
                    if (gs.TryGetValue(Name("RI"), out PdfObject? intent)
                        && (Resolve(document, intent) is not PdfName intentName || intentName.ValueAsLatin1() != "RelativeColorimetric"))
                        state = state with { HighlightPaintKnown = false };
                    if (gs.TryGetValue(Name("ca"), out PdfObject? alpha))
                        state = state with
                        {
                            NonstrokingAlpha = Resolve(document, alpha) switch
                            {
                                PdfInteger integer => integer.Value,
                                PdfReal real when double.IsFinite(real.Value) => real.Value,
                                _ => null
                            }
                        };
                    if (gs.TryGetValue(Name("BM"), out PdfObject? blend))
                        state = state with { BlendMode = (Resolve(document, blend) as PdfName)?.ValueAsLatin1() };
                    if (gs.TryGetValue(Name("SMask"), out PdfObject? highlightMask))
                    {
                        PdfObject resolvedMask = Resolve(document, highlightMask);
                        if (resolvedMask is not PdfDictionary
                            && (resolvedMask is not PdfName maskName || maskName.ValueAsLatin1() != "None"))
                            state = state with { HighlightPaintKnown = false };
                    }
                    if (gs.TryGetValue(Name("LW"), out PdfObject? width))
                        state = state with { LineWidth = Number(document, width) };
                    if (gs.TryGetValue(Name("ML"), out PdfObject? miter))
                        state = state with { MiterLimit = Number(document, miter) };
                    if (gs.TryGetValue(Name("LJ"), out PdfObject? join))
                        state = state with { LineJoin = Number(document, join) };
                    if (gs.TryGetValue(Name("LC"), out PdfObject? cap))
                        state = state with { LineCap = Number(document, cap) };
                    if (gs.TryGetValue(Name("SMask"), out PdfObject? softMask))
                        state = state with { SoftMasked = Resolve(document, softMask) is PdfDictionary };
                    string applied = "/" + name.ValueAsLatin1();
                    if (_graphicsStateNames[state.ExtGStates] != applied)
                    {
                        if (!_graphicsStateIds.TryGetValue((state.ExtGStates, applied), out int id))
                        {
                            id = _graphicsStateNames.Count;
                            _graphicsStateNames.Add(applied);
                            _graphicsStateIds[(state.ExtGStates, applied)] = id;
                        }
                        state = state with { ExtGStates = id };
                    }
                }
                else state = state with { HighlightPaintKnown = false };
                PdfObject? fontValue = null;
                if (gs is not null && gs.TryGetValue(Name("Font"), out PdfObject? fontEntry)
                    && Resolve(document, fontEntry) is PdfArray fontArray && fontArray.Count == 2)
                {
                    fontValue = fontArray[0];
                    PdfExtractionFont? font = ReadFont(fontValue);
                    state = state with
                    {
                        Font = font, FontMissing = font is null, Size = Number(document, fontArray[1]),
                        FontObject = fontValue, FontResource = null
                    };
                }
                if (textOnly)
                {
                    localChanged = true;
                    if (fontValue is not null)
                    {
                        PdfName alias = scope.Add(FontCategory, "KPRF", fontValue);
                        output.Add(new PdfContentInstruction("Tf", 0, [alias, new PdfReal(state.Size)]));
                    }
                    return;
                }
                if (gs is not null && gs.TryGetValue(Name("SMask"), out PdfObject? maskValue)
                    && Resolve(document, maskValue) is PdfDictionary mask
                    && mask.TryGetValue(Name("G"), out PdfObject? groupValue)
                    && Resolve(document, groupValue) is PdfStream group
                    && group.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boxValue)
                    && Resolve(document, boxValue) is PdfArray box && box.Count == 4)
                {
                    PdfContentBounds maskBounds = Transform(new PdfContentBounds(
                        Math.Min(Number(document, box[0]), Number(document, box[2])),
                        Math.Min(Number(document, box[1]), Number(document, box[3])),
                        Math.Max(Number(document, box[0]), Number(document, box[2])),
                        Math.Max(Number(document, box[1]), Number(document, box[3]))), state.Ctm);
                    if (regions.Any(region => Overlaps(maskBounds, region))) Reasons.Add("SoftMask");
                }
                scope.Use(ExtGStateCategory, name);
                Emit(instruction);
            }

            void Draw(PdfContentInstruction instruction)
            {
                IReadOnlyList<PdfObject> args = instruction.Operands;
                if (args.Count != 1 || args[0] is not PdfName name) { Emit(instruction); return; }
                PdfObject? value = scope.Lookup(XObjectCategory, name);
                if (value is null || Resolve(document, value) is not PdfStream xobject)
                {
                    scope.Use(XObjectCategory, name);
                    Emit(instruction);
                    return;
                }
                if (IsNamed(xobject.Dictionary, SubtypeName, "Image"))
                {
                    PdfContentBounds bounds = Visible(Transform(new PdfContentBounds(0, 0, 1, 1), state.Ctm));
                    insertion?.Paint(bounds);
                    if (Recording) retired!.CoverImage(xobject, OwnAreas(bounds, state.Ctm));
                    // A dropped or repainted image is retired with its masks, so the original samples
                    // cannot stay behind through a resource dictionary another page shares or inherits.
                    if (textOnly)
                    {
                        MarkRemoved();
                        if (update is not null) CollectImageStreams(document, value, RetiredSourceStreams);
                        return;
                    }
                    if (!glyphsOnly && bounds.Width > 0 && bounds.Height > 0
                        && regions.Any(region => Overlaps(bounds, region)))
                    {
                        if (regions.Any(region => Inside(bounds, region)))
                        {
                            RemovedImages++;
                            MarkRemoved();
                            if (update is not null) CollectImageStreams(document, value, RetiredSourceStreams);
                            return;
                        }
                        if (PaintImage(xobject, bounds))
                        {
                            if (update is not null) CollectImageStreams(document, value, RetiredSourceStreams);
                            return;
                        }
                        Reasons.Add("Image");
                    }
                    else if (HideSamples(value, xobject, bounds)) return;
                    scope.Use(XObjectCategory, name);
                    Emit(instruction);
                    return;
                }
                if (!IsNamed(xobject.Dictionary, SubtypeName, "Form"))
                {
                    scope.Use(XObjectCategory, name);
                    Emit(instruction);
                    return;
                }
                if (depth >= 32 || !_activeForms.Add(xobject))
                {
                    Reasons.Add("FormNesting");
                    if (depth >= 32 && Judging)
                    {
                        _retiredDraws++;
                        Reasons.Add(SharedContentReason);
                    }
                    scope.Use(XObjectCategory, name);
                    Emit(instruction);
                    return;
                }
                try
                {
                    Matrix formMatrix = Matrix.Identity;
                    if (xobject.Dictionary.TryGetValue(Name("Matrix"), out PdfObject? matrixValue)
                        && Resolve(document, matrixValue) is PdfArray matrix && matrix.Count == 6)
                        formMatrix = new Matrix(Number(document, matrix[0]), Number(document, matrix[1]),
                            Number(document, matrix[2]), Number(document, matrix[3]),
                            Number(document, matrix[4]), Number(document, matrix[5]));
                    Matrix formCtm = formMatrix.Then(state.Ctm);
                    PdfContentBounds formClip = state.Clip;
                    if (xobject.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boxValue)
                        && Resolve(document, boxValue) is PdfArray box && box.Count == 4)
                        formClip = Intersect(formClip, Transform(new PdfContentBounds(
                            Math.Min(Number(document, box[0]), Number(document, box[2])),
                            Math.Min(Number(document, box[1]), Number(document, box[3])),
                            Math.Max(Number(document, box[0]), Number(document, box[2])),
                            Math.Max(Number(document, box[1]), Number(document, box[3]))), formCtm));
                    PdfDictionary? formResources = xobject.Dictionary.TryGetValue(ResourcesName, out PdfObject? resourcesValue)
                        ? Resolve(document, resourcesValue) as PdfDictionary : null;
                    bool ownResources = formResources is not null;
                    ResourceScope formScope = formResources is not null
                        ? new ResourceScope(document, formResources)
                        {
                            KeepEverything = glyphsOnly,
                            KeepInherited = KeepsWholeResources(formResources)
                        }
                        : scope;
                    // A form that holds no retired content is the same wherever it is drawn, so one look
                    // does for all its draws. In a rewrite, one that shares this scope still has to
                    // mark the names it uses.
                    bool looksOnce = drawsOnly && (ownResources || update is null);
                    if (looksOnce && retired!.HoldsNothingRetired(xobject, formScope.Resources))
                    {
                        scope.Use(XObjectCategory, name);
                        Emit(instruction);
                        return;
                    }
                    int retiredDraws = _retiredDraws;
                    byte[] formBytes = document.DecodeStream(xobject, PdfContentStreamReader.MaximumSourceBytes);
                    if (Recording) retired!.CoverForm(xobject, formBytes, OwnAreas(formClip, formCtm));
                    // Clips inside the form and its box are the same on every draw, so only the clip
                    // this draw adds can hide part of what another draw showed.
                    HiddenArea[] outerHidden = _hidden;
                    if (Judging && retired!.FormAreas(xobject, formBytes) is { } formAreas)
                    {
                        _retiredDraws++;
                        _hidden = [.. outerHidden, .. formAreas.Select(area =>
                            new HiddenArea(Transform(area, formCtm), state.Clip, state.ClipIsRectangle))];
                        if (textOnly) HiddenAreas.UnionWith(_hidden);
                    }
                    List<PdfContentInstruction> rewritten;
                    bool formChanged;
                    try
                    {
                        rewritten = Rewrite(formBytes, formScope, state with
                        {
                            Ctm = formCtm, Clip = formClip,
                            HighlightPaintKnown = state.HighlightPaintKnown && !xobject.Dictionary.ContainsKey(Name("Group"))
                        },
                            depth + 1, out formChanged);
                    }
                    finally { _hidden = outerHidden; }
                    if (looksOnce && retiredDraws == _retiredDraws) retired!.MarkHoldsNothingRetired(xobject, formScope.Resources);
                    if (!formChanged)
                    {
                        scope.Use(XObjectCategory, name);
                        Emit(instruction);
                        return;
                    }
                    MarkRemoved();
                    if (update is null) return;
                    CollectSourceStreams(document, value, RetiredSourceStreams);
                    var entries = xobject.Dictionary
                        .Where(entry => entry.Key.ValueAsLatin1() is not ("Length" or "Filter" or "DecodeParms")
                            && !PdfImageRedaction.CopyKeys.Contains(entry.Key))
                        .ToDictionary(entry => entry.Key, entry => entry.Value);
                    if (ownResources) entries[ResourcesName] = formScope.Build();
                    PdfIndirectReference copy = update.AddObject(new PdfStream(new PdfDictionary(entries),
                        PdfContentStreamWriter.Write(rewritten)));
                    PdfName alias = scope.Add(XObjectCategory, "KPRX", copy);
                    output.Add(new PdfContentInstruction("Do", 0, [alias]));
                }
                finally { _activeForms.Remove(xobject); }
            }

            // A partly covered image keeps its picture with the covered pixels painted over,
            // drawn from a new image so the original samples leave the page.
            bool PaintImage(PdfStream xobject, PdfContentBounds bounds)
            {
                if (PdfImageRedaction.Decode(document, xobject) is not PdfImageRedaction.Samples samples) return false;
                PdfImageRedaction.Plan? plan = PdfImageRedaction.Covers(state.Ctm, samples,
                    regions.Select((region, index) => (region, fills is not null && index < fills.Length
                        ? fills[index] : DefaultFill)));
                if (plan is null || plan.Image.Count == 0) return false;
                RedactedImages++;
                MarkRemoved();
                // What this draw hides of the areas covered elsewhere goes from the same new image.
                List<PdfContentBounds> elsewhere = Judging ? [.. CoveredSamples(xobject).Except(OwnAreas(bounds, state.Ctm))] : [];
                if (elsewhere.Count != 0)
                {
                    PdfImageRedaction.Plan hidden = PdfImageRedaction.HiddenPlan(state.Ctm, samples, elsewhere, state.Clip,
                        out bool shown);
                    if (shown && !state.ClipIsRectangle) Reasons.Add(SharedContentReason);
                    plan = hidden.Then(plan);
                }
                if (update is null) return true;
                PdfImageRedaction.Paint(samples, plan);
                PdfIndirectReference? mask = samples.Mask is PdfImageRedaction.Samples alpha
                    ? update.AddObject(PdfImageRedaction.Encode(alpha)) : null;
                PdfIndirectReference copy = update.AddObject(PdfImageRedaction.Encode(samples, mask));
                PdfName alias = scope.Add(XObjectCategory, PdfImageRedaction.ResourcePrefix, copy);
                output.Add(new PdfContentInstruction("Do", 0, [alias]));
                return true;
            }

            // The areas of this image's unit square that some draw covers or this content hides.
            List<PdfContentBounds> CoveredSamples(PdfStream image)
            {
                List<PdfContentBounds> areas = [.. retired!.ImageAreas(image) ?? []];
                PdfContentBounds placed = Transform(new PdfContentBounds(0, 0, 1, 1), state.Ctm);
                foreach (HiddenArea hidden in _hidden)
                    if (Overlaps(placed, hidden.Area)) areas.Add(ThroughInverse(Intersect(placed, hidden.Area), state.Ctm));
                if (areas.Count != 0) _retiredDraws++;
                return areas;
            }

            // A draw no region touches can still hold samples covered on another draw. It gets its own
            // copy with only the samples it does not show painted, so its picture does not change. Paint
            // that this cannot be done to is left for the page image, as with any other unsafe content.
            bool HideSamples(PdfObject value, PdfStream image, PdfContentBounds bounds)
            {
                if (!Judging || glyphsOnly) return false;
                List<PdfContentBounds> areas = CoveredSamples(image);
                if (areas.Count == 0) return false;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    MarkRemoved();
                    if (update is not null) CollectImageStreams(document, value, RetiredSourceStreams);
                    return true;
                }
                if (PdfImageRedaction.Size(document, image) is not (int width, int height))
                {
                    Reasons.Add(SharedContentReason);
                    return false;
                }
                bool any = PdfImageRedaction.HasHidden(state.Ctm, width, height, areas, state.Clip, out bool shown);
                if (shown && !state.ClipIsRectangle)
                {
                    Reasons.Add(SharedContentReason);
                    return false;
                }
                if (!any) return false;
                if (PdfImageRedaction.Decode(document, image) is not PdfImageRedaction.Samples samples)
                {
                    Reasons.Add(SharedContentReason);
                    return false;
                }
                MarkRemoved();
                if (update is null) return true;
                CollectImageStreams(document, value, RetiredSourceStreams);
                PdfImageRedaction.Plan plan = PdfImageRedaction.HiddenPlan(state.Ctm, samples, areas, state.Clip, out _);
                PdfIndirectReference copy = retired!.PrivateCopy(image, plan.Key, () =>
                {
                    PdfImageRedaction.Paint(samples, plan);
                    PdfIndirectReference? mask = samples.Mask is PdfImageRedaction.Samples alpha
                        ? update.AddObject(PdfImageRedaction.Encode(alpha)) : null;
                    return update.AddObject(PdfImageRedaction.Encode(samples, mask));
                });
                PdfName alias = scope.Add(XObjectCategory, PdfImageRedaction.ResourcePrefix, copy);
                output.Add(new PdfContentInstruction("Do", 0, [alias]));
                return true;
            }

            void InlineImage(PdfContentInstruction instruction)
            {
                if (textOnly) { MarkRemoved(); return; }
                PdfContentBounds bounds = Visible(Transform(new PdfContentBounds(0, 0, 1, 1), state.Ctm));
                insertion?.Paint(bounds);
                HiddenOverlap part = HiddenPart(Transform(new PdfContentBounds(0, 0, 1, 1), state.Ctm));
                if (part == HiddenOverlap.Whole) { MarkRemoved(); return; }
                if (part != HiddenOverlap.None) Reasons.Add(SharedContentReason);
                if (!glyphsOnly && bounds.Width > 0 && bounds.Height > 0
                    && regions.Any(region => Overlaps(bounds, region)))
                {
                    if (regions.Any(region => Inside(bounds, region)))
                    {
                        RemovedImages++;
                        MarkRemoved();
                        return;
                    }
                    Reasons.Add("Image");
                }
                if (instruction.Operands.Count == 1 && instruction.Operands[0] is PdfDictionary image)
                    foreach (string key in new[] { "CS", "ColorSpace" })
                        if (image.TryGetValue(Name(key), out PdfObject? space) && space is PdfName spaceName
                            && !IsDeviceSpace(spaceName))
                            scope.Use(ColorSpaceCategory, spaceName);
                Emit(instruction);
            }

            void PathSegment(PdfContentInstruction instruction)
            {
                IReadOnlyList<PdfObject> args = instruction.Operands;
                if (subpaths.Count == 0 || instruction.Operator is "m" or "re") subpaths.Add(pathPoints.Count);
                if (instruction.Operator == "re" && args.Count == 4)
                {
                    double x = N(args[0]), y = N(args[1]), w = N(args[2]), h = N(args[3]);
                    pathPoints.AddRange([state.Ctm.Point(x, y), state.Ctm.Point(x + w, y),
                        state.Ctm.Point(x + w, y + h), state.Ctm.Point(x, y + h)]);
                }
                else
                {
                    for (int index = 0; index + 1 < args.Count; index += 2)
                        pathPoints.Add(state.Ctm.Point(N(args[index]), N(args[index + 1])));
                }
                pendingPath.Add(instruction);
            }

            // What a stroke really paints, edge by edge. Some producers draw a table rule as a very wide,
            // very short line, so the box around the path and its width says little about where it paints.
            IEnumerable<PdfContentBounds> StrokeEdges(PdfContentBounds whole, string paint)
            {
                double half = Math.Abs(state.LineWidth) * 0.5;
                double reachX = half * (Math.Abs(state.Ctm.A) + Math.Abs(state.Ctm.C));
                double reachY = half * (Math.Abs(state.Ctm.B) + Math.Abs(state.Ctm.D));
                var edges = new List<PdfContentBounds>();
                PdfPoint? current = null, start = null;
                void Edge(PdfPoint from, PdfPoint to)
                {
                    bool level = Math.Abs(from.Y - to.Y) < 1e-6, upright = Math.Abs(from.X - to.X) < 1e-6;
                    double x = level && !upright ? 0 : reachX, y = upright && !level ? 0 : reachY;
                    if (level && !upright && state.LineCap != 0) x = reachX;
                    if (upright && !level && state.LineCap != 0) y = reachY;
                    edges.Add(new PdfContentBounds(Math.Min(from.X, to.X) - x, Math.Min(from.Y, to.Y) - y,
                        Math.Max(from.X, to.X) + x, Math.Max(from.Y, to.Y) + y));
                }
                foreach (PdfContentInstruction segment in pendingPath)
                {
                    IReadOnlyList<PdfObject> values = segment.Operands;
                    switch (segment.Operator)
                    {
                        case "m" when values.Count == 2: current = start = state.Ctm.Point(N(values[0]), N(values[1])); break;
                        case "l" when values.Count == 2 && current is { } from:
                            PdfPoint to = state.Ctm.Point(N(values[0]), N(values[1]));
                            Edge(from, to); current = to; break;
                        case "h" when current is { } last && start is { } first: Edge(last, first); current = first; break;
                        case "re" when values.Count == 4:
                            double left = N(values[0]), bottom = N(values[1]), right = left + N(values[2]), top = bottom + N(values[3]);
                            PdfPoint a = state.Ctm.Point(left, bottom), b = state.Ctm.Point(right, bottom),
                                c = state.Ctm.Point(right, top), d = state.Ctm.Point(left, top);
                            Edge(a, b); Edge(b, c); Edge(c, d); Edge(d, a);
                            current = start = a; break;
                        case "W": case "W*": break;
                        default: return [state.LineWidth == 0 ? whole : StrokeBounds(whole, state with { MiterLimit = 1 })];
                    }
                }
                // These operators close the path themselves, with an edge no segment wrote.
                if (paint is "s" or "b" or "b*" && current is { } open && start is { } closing) Edge(open, closing);
                return edges;
            }

            // What a path paints at most: each subpath apart and cut to the clip. A region in a gap
            // between subpaths, or past the clip, has none of the path's ink under it.
            IEnumerable<PdfContentBounds> PaintedParts(bool stroked)
            {
                for (int index = 0; index < subpaths.Count; index++)
                {
                    int first = subpaths[index], end = index + 1 < subpaths.Count ? subpaths[index + 1] : pathPoints.Count;
                    if (first == end) continue;
                    PdfPoint start = pathPoints[first];
                    double left = start.X, right = start.X, bottom = start.Y, top = start.Y;
                    for (int point = first + 1; point < end; point++)
                    {
                        left = Math.Min(left, pathPoints[point].X);
                        right = Math.Max(right, pathPoints[point].X);
                        bottom = Math.Min(bottom, pathPoints[point].Y);
                        top = Math.Max(top, pathPoints[point].Y);
                    }
                    var part = new PdfContentBounds(left, bottom, right, top);
                    PdfContentBounds visible = Visible(stroked ? StrokeBounds(part, state) : part);
                    if (visible.Right >= visible.Left && visible.Top >= visible.Bottom) yield return visible;
                }
            }

            // A solid rectangle that runs past a region on every side has no geometry under it, so the
            // cover hides all it can tell. A pattern or a soft mask puts detail in the fill, and an
            // edge in the region is geometry, whether a stroke, the rectangle or a clip draws it.
            bool IsPlainBackground(PdfContentInstruction paint, PdfContentBounds bounds)
            {
                if (paint.Operator is not ("f" or "F" or "f*") || pendingClip || state.SoftMasked || !state.ClipIsRectangle
                    || HasPatternFill(state) || !IsAxisAlignedRectangle(pendingPath, pathPoints)) return false;
                PdfContentBounds visible = Visible(bounds);
                return regions.Where(region => Overlaps(visible, region)).All(region => Inside(region, visible));
            }

            void PaintPath(PdfContentInstruction paint)
            {
                PdfContentBounds bounds = pathPoints.Count == 0 ? default : new PdfContentBounds(
                    pathPoints.Min(point => point.X), pathPoints.Min(point => point.Y),
                    pathPoints.Max(point => point.X), pathPoints.Max(point => point.Y));
                bool stroked = paint.Operator is "S" or "s" or "B" or "B*" or "b" or "b*";
                PdfContentBounds paintedBounds = !textOnly && !glyphsOnly && pathPoints.Count > 0 && stroked
                    ? StrokeBounds(bounds, state) : bounds;
                bool translucentHighlight = insertion is { Written: true, ProtectedBox: { } protectedBox }
                    && Overlaps(Visible(bounds), protectedBox) && paint.Operator is "f" or "F" or "f*"
                    && state.HighlightPaintKnown && state.BlendMode == "Multiply"
                    && state.NonstrokingAlpha is > 0 and <= 1 && !state.SoftMasked
                    && !pendingClip && state.ClipIsRectangle && !HasPatternFill(state)
                    && IsAxisAlignedRectangle(pendingPath, pathPoints)
                    && (state.NonstrokingAlpha < 1 || IsKnownNonblackHighlight(state, scope));
                if (!translucentHighlight && pathPoints.Count > 0 && paint.Operator is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*")
                    insertion?.Paint(Visible(bounds));
                // The stroke's own width, without the most a miter corner could reach: a rule beside the
                // text is not over it. A hairline has no width in page units, so its path stands for it.
                if (insertion is not null && pathPoints.Count > 0 && stroked)
                    foreach (PdfContentBounds edge in StrokeEdges(bounds, paint.Operator)) insertion.Paint(Visible(edge));
                bool painted = !textOnly && !glyphsOnly && paint.Operator != "n" && pathPoints.Count > 0;
                bool removable = painted && !pendingClip && regions.Any(region => Inside(paintedBounds, region));
                if (textOnly)
                {
                    if (paint.Operator != "n" || pendingPath.Count > 0) MarkRemoved();
                }
                // A clip-only path (W n) paints nothing, so it is neither recoverable geometry
                // nor a reason to rasterize; it stays so later content keeps its clip.
                else if (removable)
                {
                    RemovedPaths++;
                    MarkRemoved();
                }
                else if (painted && regions.Any(region => Overlaps(paintedBounds, region))
                    && PaintedParts(stroked).Any(part => regions.Any(region => Overlaps(part, region)))
                    && !IsPlainBackground(paint, bounds))
                {
                    Reasons.Add("Vector");
                    output.AddRange(pendingPath);
                    output.Add(paint);
                }
                // A path with a hidden part goes whole, and one that also shows cannot be split.
                else if (paint.Operator != "n" && pathPoints.Count > 0
                    && HiddenPart(paintedBounds) is var hiddenPart and not HiddenOverlap.None)
                {
                    if (hiddenPart == HiddenOverlap.Whole && !pendingClip) MarkRemoved();
                    else
                    {
                        Reasons.Add(SharedContentReason);
                        output.AddRange(pendingPath);
                        output.Add(paint);
                    }
                }
                else
                {
                    output.AddRange(pendingPath);
                    output.Add(paint);
                }
                if (pendingClip && pathPoints.Count > 0)
                    state = state with
                    {
                        Clip = Intersect(state.Clip, bounds),
                        ClipIsRectangle = state.ClipIsRectangle && IsAxisAlignedRectangle(pendingPath, pathPoints)
                    };
                pendingPath.Clear();
                pathPoints.Clear();
                subpaths.Clear();
                pendingClip = false;
            }

            PdfContentBounds Visible(PdfContentBounds bounds) => Intersect(bounds, state.Clip);

            HiddenOverlap HiddenPart(PdfContentBounds bounds) => Hidden(bounds, _hidden);

            // Text that cannot be placed cannot be shown to be clear of the hidden areas.
            void RefuseUnplacedText()
            {
                if (_hidden.Length != 0) Reasons.Add(SharedContentReason);
            }
        }

        // How content lies against the retired areas a draw does not show.
        internal static HiddenOverlap Hidden(PdfContentBounds bounds, IEnumerable<HiddenArea> areas)
        {
            HiddenOverlap result = HiddenOverlap.None;
            foreach (HiddenArea hidden in areas)
            {
                if (!Overlaps(bounds, hidden.Area)) continue;
                if (hidden.Clip.Width <= 0 || hidden.Clip.Height <= 0 || !Overlaps(bounds, hidden.Clip))
                    return HiddenOverlap.Whole;
                PdfContentBounds part = Intersect(bounds, hidden.Area);
                if (!hidden.ClipIsRectangle) result = HiddenOverlap.Unclear;
                else if (result == HiddenOverlap.None && (part.Left < hidden.Clip.Left - OverlapTolerance
                    || part.Right > hidden.Clip.Right + OverlapTolerance
                    || part.Bottom < hidden.Clip.Bottom - OverlapTolerance
                    || part.Top > hidden.Clip.Top + OverlapTolerance)) result = HiddenOverlap.Crossing;
            }
            return result;
        }

        // A path is one axis-aligned rectangle when it is a single re, or four straight sides that close
        // up, and every side runs along the page's horizontal or vertical.
        private static bool IsAxisAlignedRectangle(List<PdfContentInstruction> path, List<PdfPoint> points)
        {
            PdfContentInstruction[] shape = [.. path.Where(item => item.Operator is not ("W" or "W*" or "h"))];
            PdfPoint[] corners = [.. points];
            if (shape.Length == 1 && shape[0].Operator == "re") { }
            else if (shape.Length is 4 or 5 && shape[0].Operator == "m" && shape.Skip(1).All(item => item.Operator == "l"))
            {
                if (corners.Length == 5)
                {
                    if (Math.Abs(corners[0].X - corners[4].X) > 1e-6 || Math.Abs(corners[0].Y - corners[4].Y) > 1e-6) return false;
                    corners = corners[..4];
                }
            }
            else return false;
            if (corners.Length != 4) return false;
            bool horizontalFirst = Math.Abs(corners[0].Y - corners[1].Y) <= 1e-6;
            for (int index = 0; index < 4; index++)
            {
                PdfPoint from = corners[index], to = corners[(index + 1) % 4];
                bool horizontal = Math.Abs(from.Y - to.Y) <= 1e-6, vertical = Math.Abs(from.X - to.X) <= 1e-6;
                if (!(index % 2 == 0 == horizontalFirst ? horizontal : vertical)) return false;
            }
            return true;
        }

        // A pattern is selected by name, and a fill set to the Pattern space with none selected is no plain color.
        private static bool HasPatternFill(TextState state) => state.Fill.Any(item =>
            item.Operator is "sc" or "scn" && item.Operands.Count > 0 && item.Operands[^1] is PdfName
            || item.Operator == "cs" && item.Operands.Count == 1 && item.Operands[0] is PdfName space
                && space.ValueAsLatin1() == "Pattern");

        private bool IsKnownNonblackHighlight(TextState state, ResourceScope scope)
        {
            if (state.Fill.Length == 0) return false;
            try
            {
                PdfContentInstruction color = state.Fill[^1];
                PdfObject? space = color.Operator switch
                {
                    "g" => Name("DeviceGray"),
                    "rg" => Name("DeviceRGB"),
                    "sc" or "scn" when state.Fill.Length == 2 && state.Fill[0].Operator == "cs"
                        && state.Fill[0].Operands.Count == 1 => state.Fill[0].Operands[0],
                    _ => null
                };
                if (space is null || color.Operands.Count is not (1 or 3)) return false;
                double[] components = new double[color.Operands.Count];
                for (int index = 0; index < components.Length; index++)
                {
                    components[index] = Number(document, color.Operands[index]);
                    if (!double.IsFinite(components[index])) return false;
                }
                if (!TryHighlightRgb(space, components, state.FillScope ?? scope, 0, useDefaults: true, out var rgb)) return false;
                return double.IsFinite(rgb.Red) && double.IsFinite(rgb.Green) && double.IsFinite(rgb.Blue)
                    && Math.Max(Math.Clamp(rgb.Red, 0, 1),
                        Math.Max(Math.Clamp(rgb.Green, 0, 1), Math.Clamp(rgb.Blue, 0, 1))) > 1d / 255;
            }
            catch (Exception error) when (error is FormatException or NotSupportedException
                or ArgumentException or InvalidDataException or PdfFilterException)
            {
                return false;
            }
        }

        private bool TryHighlightRgb(PdfObject space, double[] components, ResourceScope scope, int depth,
            bool useDefaults, out (double Red, double Green, double Blue) rgb)
        {
            rgb = default;
            if (depth >= 32) return false;
            space = Resolve(document, space);
            if (space is PdfName name)
            {
                string kind = name.ValueAsLatin1();
                if (kind is "DeviceRGB" or "RGB" or "DeviceGray" or "G")
                {
                    bool gray = kind is "DeviceGray" or "G";
                    if (useDefaults && scope.Lookup(ColorSpaceCategory, Name(gray ? "DefaultGray" : "DefaultRGB")) is { } replacement)
                        return TryHighlightRgb(replacement, components, scope, depth + 1, false, out rgb);
                    if (components.Length != (gray ? 1 : 3)) return false;
                    rgb = gray ? (components[0], components[0], components[0])
                        : (components[0], components[1], components[2]);
                    return true;
                }
                if (scope.Lookup(ColorSpaceCategory, name) is not { } alias) return false;
                return TryHighlightRgb(alias, components, scope, depth + 1, useDefaults, out rgb);
            }
            if (space is not PdfArray { Count: 2 } array || Resolve(document, array[0]) is not PdfName family
                || family.ValueAsLatin1() != "ICCBased" || Resolve(document, array[1]) is not PdfStream stream
                || stream.Dictionary.ContainsKey(Name("Range"))) return false;
            var profile = HighlightProfile(stream);
            if (profile is null || profile.DeviceColorSpaceSignature is not ("RGB " or "GRAY")
                || profile.ComponentCount != components.Length) return false;
            Span<double> xyz = stackalloc double[3];
            profile.ToXyz(components, xyz);
            if (!double.IsFinite(xyz[0]) || !double.IsFinite(xyz[1]) || !double.IsFinite(xyz[2])) return false;
            rgb = PdfPageRenderer.PreciseConnectionToRgb(xyz[0], xyz[1], xyz[2]);
            return true;
        }

        private PdfColorProfile? HighlightProfile(PdfStream stream)
        {
            if (_highlightProfiles.TryGetValue(stream, out var cached)) return cached;
            _highlightProfiles[stream] = null;
            int remaining = MaximumHighlightProfileBytes - _highlightProfileBytes;
            if (remaining <= 0) return null;
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data;
            try { data = document.DecodeStream(stream, remaining); }
            catch (Exception error) when (error is FormatException or NotSupportedException
                or ArgumentException or InvalidDataException or PdfFilterException)
            {
                _highlightProfileBytes = MaximumHighlightProfileBytes;
                return null;
            }
            _highlightProfileBytes += data.Length;
            cancellationToken.ThrowIfCancellationRequested();
            var profile = PdfColorProfile.Load(data);
            _highlightProfiles[stream] = profile;
            return profile;
        }

        private static PdfContentBounds StrokeBounds(PdfContentBounds bounds, TextState state)
        {
            // A device hairline has no width in page units, so a point either side of its path stands for it.
            if (state.LineWidth == 0)
                return new PdfContentBounds(bounds.Left - 1, bounds.Bottom - 1, bounds.Right + 1, bounds.Top + 1);
            double extent = Math.Abs(state.LineWidth) * 0.5
                * Math.Max(state.LineCap == 2 ? Math.Sqrt(2) : 1,
                    state.LineJoin == 0 ? Math.Max(1, Math.Abs(state.MiterLimit)) : 1);
            double x = extent * (Math.Abs(state.Ctm.A) + Math.Abs(state.Ctm.C));
            double y = extent * (Math.Abs(state.Ctm.B) + Math.Abs(state.Ctm.D));
            if (!double.IsFinite(x) || !double.IsFinite(y))
                throw new FormatException("The redaction stroke bounds are not finite.");
            return new PdfContentBounds(bounds.Left - x, bounds.Bottom - y, bounds.Right + x, bounds.Top + y);
        }

        private PdfExtractionFont? ReadFont(PdfObject? value)
        {
            if (value is null || Resolve(document, value) is not PdfDictionary dictionary) return null;
            if (fonts.TryGetValue(dictionary, out PdfExtractionFont? cached)) return cached;
            PdfExtractionFont? font;
            try { font = PdfFontResourceReader.Read(document, dictionary); }
            catch (Exception error) when (error is FormatException or NotSupportedException
                or InvalidOperationException or ArgumentException or PdfFilterException or InvalidDataException)
            {
                font = null;
            }
            fonts[dictionary] = font;
            return font;
        }

        internal int? ColorComponents(ResourceScope scope, PdfName name) => ColorComponents(scope, name, 0);

        private int? ColorComponents(ResourceScope scope, PdfName name, int depth)
        {
            // Names can alias each other in a loop, which the page reader refuses at the same depth.
            if (depth >= 32) return null;
            string value = name.ValueAsLatin1();
            int? standard = value switch
            {
                "DeviceGray" or "G" => 1,
                "DeviceRGB" or "RGB" => 3,
                "DeviceCMYK" or "CMYK" => 4,
                _ => null
            };
            if (standard.HasValue) return standard;
            PdfObject? space = scope.Lookup(ColorSpaceCategory, name);
            if (space is null) return null;
            PdfObject resolved = Resolve(document, space);
            if (resolved is PdfName alias) return alias.Equals(name) ? null : ColorComponents(scope, alias, depth + 1);
            if (resolved is not PdfArray array || array.Count == 0 || Resolve(document, array[0]) is not PdfName family) return null;
            return family.ValueAsLatin1() switch
            {
                "CalGray" or "Indexed" or "I" or "Separation" => 1,
                "CalRGB" or "Lab" => 3,
                "DeviceN" when array.Count > 1 && Resolve(document, array[1]) is PdfArray names => names.Count,
                "ICCBased" when array.Count > 1 && Resolve(document, array[1]) is PdfStream profile
                    && profile.Dictionary.TryGetValue(Name("N"), out PdfObject? count) => (int)Number(document, count),
                _ => null
            };
        }

        private static bool IsDeviceSpace(PdfName name) => name.ValueAsLatin1() is
            "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern" or "G" or "RGB" or "CMYK" or "I" or "Indexed";

        private static bool ContainsReference(PdfObject value) => value switch
        {
            PdfIndirectReference => true,
            PdfArray array => array.Any(ContainsReference),
            PdfDictionary dictionary => dictionary.Values.Any(ContainsReference),
            _ => false
        };

        private static List<PdfObject> Merge(List<PdfObject> items)
        {
            var merged = new List<PdfObject>();
            double pending = 0;
            bool hasPending = false;
            foreach (PdfObject item in items)
            {
                if (item is PdfInteger or PdfReal)
                {
                    pending += item is PdfInteger integer ? integer.Value : ((PdfReal)item).Value;
                    hasPending = true;
                    continue;
                }
                if (hasPending) { merged.Add(new PdfReal(pending)); pending = 0; hasPending = false; }
                merged.Add(item);
            }
            if (hasPending) merged.Add(new PdfReal(pending));
            return merged;
        }

        private static PdfContentBounds Transform(PdfContentBounds box, Matrix matrix)
        {
            PdfPoint[] points = [matrix.Point(box.Left, box.Bottom), matrix.Point(box.Right, box.Bottom),
                matrix.Point(box.Left, box.Top), matrix.Point(box.Right, box.Top)];
            return new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        }

        private static PdfContentBounds Intersect(PdfContentBounds a, PdfContentBounds b) =>
            new(Math.Max(a.Left, b.Left), Math.Max(a.Bottom, b.Bottom), Math.Min(a.Right, b.Right), Math.Min(a.Top, b.Top));
    }
}
