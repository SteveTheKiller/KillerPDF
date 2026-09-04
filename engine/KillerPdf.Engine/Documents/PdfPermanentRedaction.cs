using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;
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
public sealed record PdfRedactionVerificationFinding(string Code, string Message, int? PageIndex = null);

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

/// <summary>Creates and verifies PDFs that contain only sanitized page images.</summary>
public static class PdfPermanentRedaction
{
    private static readonly PdfName InformationName = Name("Info");
    private static readonly PdfName MetadataName = Name("Metadata");
    private static readonly PdfName NamesName = Name("Names");
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName OutlinesName = Name("Outlines");
    private static readonly PdfName AnnotationsName = Name("Annots");

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
        PdfPageTree tree = PdfPageTree.Read(document);
        CheckCatalog(MetadataName, "XmpMetadata", "The output contains XMP metadata.");
        CheckCatalog(NamesName, "NameTrees", "The output contains document name trees.");
        CheckCatalog(AcroFormName, "AcroForm", "The output contains form data.");
        CheckCatalog(OutlinesName, "Outlines", "The output contains bookmarks.");
        if (tree.Pages.Count != expectedPageCount)
            findings.Add(new("PageCount", $"Expected {expectedPageCount} pages but found {tree.Pages.Count}."));
        var reader = new PdfPageContentReader(document);
        for (int pageIndex = 0; pageIndex < tree.Pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tree.Pages[pageIndex].Dictionary.ContainsKey(AnnotationsName))
                findings.Add(new("Annotations", "The output page contains annotations.", pageIndex));
            PdfPageContent content = reader.Read(pageIndex, cancellationToken);
            if (content.Images.Count != 1 || content.Letters.Count != 0 || content.Paths.Count != 0)
                findings.Add(new("NonRasterContent", "The output page is not a single image-only page.", pageIndex));
            foreach (string value in prohibited)
                if (content.Text.Contains(value, StringComparison.OrdinalIgnoreCase))
                    findings.Add(new("ProhibitedText", $"The output still exposes prohibited text '{value}'.", pageIndex));
        }
        return new PdfRedactionVerificationReport(Array.AsReadOnly(findings.ToArray()));

        void CheckCatalog(PdfName key, string code, string message)
        {
            if (tree.Catalog.ContainsKey(key)) findings.Add(new(code, message));
        }
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
}
