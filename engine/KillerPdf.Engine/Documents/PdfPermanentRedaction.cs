using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text;
using System.Text.Json;

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
public sealed record PdfRedactionVerificationReport(IReadOnlyList<PdfRedactionVerificationFinding> Findings)
{
    /// <summary>Gets whether the output passed every redaction safety check.</summary>
    public bool Succeeded => Findings.Count == 0;

    /// <summary>Exports the verification result as stable machine-readable JSON.</summary>
    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        return JsonSerializer.Serialize(new { Version = 1, Succeeded, Findings }, options);
    }

    /// <summary>Exports a readable verification result with page and object locations.</summary>
    public string ToText()
    {
        var output = new StringBuilder()
            .Append("Redaction verification: ")
            .AppendLine(Succeeded ? "Passed" : "Failed");
        if (Findings.Count == 0) return output.AppendLine("No findings.").ToString();
        foreach (PdfRedactionVerificationFinding finding in Findings)
        {
            output.Append(finding.Code);
            if (finding.PageIndex is int pageIndex)
                output.Append(" | Page ").Append(pageIndex + 1);
            if (finding.ObjectNumber is int objectNumber)
                output.Append(" | Object ").Append(objectNumber);
            output.Append(": ").AppendLine(finding.Message);
        }
        return output.ToString();
    }
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
public sealed record PdfCommentRedactionResult(
    ReadOnlyMemory<byte> Document, IReadOnlyList<string> RemovedIds, int RemainingComments)
{
    /// <summary>Exports a data-safe result without document bytes or comment text.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        Version = 1,
        RemovedCount = RemovedIds.Count,
        RemovedIds,
        RemainingComments
    }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    });
}

/// <summary>The verified result of permanently removing reviewed attachments.</summary>
public sealed record PdfAttachmentRedactionResult(
    ReadOnlyMemory<byte> Document, IReadOnlyList<string> RemovedIds,
    IReadOnlyList<string> RemainingAttachmentNames)
{
    /// <summary>Exports a data-safe result without document bytes or attachment payloads.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        Version = 1,
        RemovedCount = RemovedIds.Count,
        RemovedIds,
        RemainingAttachmentNames
    }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    });
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
        PdfRedactionMatch[] selected = review.Included.ToArray();
        if (selected.Any(match => match.TargetKind != PdfRedactionTargetKind.Attachment))
            throw new ArgumentException(
                "Reviewed attachment removal accepts only attachment targets.", nameof(review));
        PdfAttachmentInfo[] current = PdfAttachmentReader.Read(document).ToArray();
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
        if (targets.Count == 0)
            return new PdfAttachmentRedactionResult(document.Source, [],
                Array.AsReadOnly(current.Select(item => item.FileName).ToArray()));

        var editor = new PdfIncrementalPageEditor(document);
        foreach (var target in targets)
            editor.RemoveAttachment(target.Attachment.FileName);
        byte[] output = PdfDocumentWriter.Write(PdfDocument.Open(editor.Build()),
            new PdfDocumentWriteOptions
            {
                PruneUnreachableObjects = true,
                AllowSignatureInvalidation = allowSignatureInvalidation
            });
        PdfDocument reopened = PdfDocument.Open(output);
        string[] remaining = [.. PdfAttachmentReader.Read(reopened)
            .Select(item => item.FileName)];
        string[] expected = [.. current.Select(item => item.FileName)
            .Except(targets.Select(item => item.Attachment.FileName), StringComparer.Ordinal)];
        if (!remaining.SequenceEqual(expected, StringComparer.Ordinal)
            || reopened.CrossReferences.Sections.Count != 1)
            throw new InvalidOperationException(
                "The saved document did not preserve the reviewed attachment-removal result.");
        return new PdfAttachmentRedactionResult(output,
            Array.AsReadOnly(selected.Select(match => match.Id).ToArray()),
            Array.AsReadOnly(remaining));

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
        PdfRedactionMatch[] selected = review.Included.ToArray();
        if (selected.Any(match => match.TargetKind != PdfRedactionTargetKind.Comment))
            throw new ArgumentException(
                "Reviewed comment removal accepts only comment targets.", nameof(review));
        if (selected.Any(match => !string.IsNullOrEmpty(match.OverlayText)))
            throw new NotSupportedException(
                "Comment redaction overlay text requires a rasterized page workflow.");
        if (selected.Length == 0)
            return new PdfCommentRedactionResult(document.Source,
                Array.Empty<string>(), PdfCommentReader.Read(document).Count);

        PdfCommentInfo[] current = PdfCommentReader.Read(document).ToArray();
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

        var editor = new PdfIncrementalAnnotationEditor(document);
        foreach (var target in targets.OrderByDescending(item => item.Comment.PageIndex)
                     .ThenByDescending(item => item.Comment.AnnotationIndex))
            editor.RemoveAnnotationAt(
                target.Comment.PageIndex, target.Comment.AnnotationIndex);
        PdfDocument edited = PdfDocument.Open(editor.Build());
        byte[] output = PdfDocumentWriter.Write(edited, new PdfDocumentWriteOptions
        {
            PruneUnreachableObjects = true,
            AllowSignatureInvalidation = allowSignatureInvalidation
        });
        PdfDocument reopened = PdfDocument.Open(output);
        PdfCommentInfo[] remaining = PdfCommentReader.Read(reopened).ToArray();
        string[] expected = [.. current.Except(targets.Select(item => item.Comment))
            .Select(item => item.Contents).Order(StringComparer.Ordinal)];
        string[] actual = [.. remaining.Select(item => item.Contents)
            .Order(StringComparer.Ordinal)];
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal)
            || reopened.CrossReferences.Sections.Count != 1)
            throw new InvalidOperationException(
                "The saved document did not preserve the reviewed comment-removal result.");
        return new PdfCommentRedactionResult(output,
            Array.AsReadOnly(selected.Select(match => match.Id).ToArray()), remaining.Length);
    }

    /// <summary>Builds a new PDF without copying any object or revision from the source document.</summary>
    public static byte[] RebuildFromSanitizedPages(IEnumerable<PdfSanitizedRasterPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        PdfSanitizedRasterPage[] values = pages.ToArray();
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
        if (expectedPageCount <= 0) throw new ArgumentOutOfRangeException(nameof(expectedPageCount));
        string[] prohibited = (prohibitedText ?? []).Where(value => !string.IsNullOrEmpty(value)).ToArray();
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
        foreach (var dataStore in CatalogDataStores)
            CheckCatalog(dataStore.Key, dataStore.Code, dataStore.Message);
        if (tree.Pages.Count != expectedPageCount)
            findings.Add(new("PageCount", $"Expected {expectedPageCount} pages but found {tree.Pages.Count}."));
        var reader = new PdfPageContentReader(document);
        for (int pageIndex = 0; pageIndex < tree.Pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tree.Pages[pageIndex].Dictionary.ContainsKey(AnnotationsName))
                findings.Add(new("Annotations", "The output page contains annotations.", pageIndex));
            foreach (var dataStore in PageDataStores)
                if (tree.Pages[pageIndex].Dictionary.ContainsKey(dataStore.Key))
                    findings.Add(new(dataStore.Code, dataStore.Message, pageIndex));
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
                            Check(DecodeText(PdfStreamDecoder.Decode(
                                stream, document.Resolve, 64 * 1024 * 1024)));
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
