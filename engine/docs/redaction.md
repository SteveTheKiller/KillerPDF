# Permanent redaction

Permanent page-content redaction is a review and raster-rebuild workflow. The
engine finds candidate text, images, or regions, but the host must render each
page and paint every approved region into the pixels. The engine then builds a
new image-only PDF and verifies that no source document stores were copied.

Do not use a redaction annotation, a filled rectangle, or an overlay as proof of
removal. Those can leave the original text or image recoverable underneath.

## Find and review candidates

Extract page content, search it, and present the proposed matches to a person or
an application policy before changing anything.

```csharp
using KillerPdf.Engine.Documents;

static PdfRedactionReview FindAccountNumbers(
    byte[] sourceBytes,
    CancellationToken cancellationToken)
{
    PdfDocument document = PdfDocument.Open(sourceBytes);
    var reader = new PdfPageContentReader(document);
    var pages = new List<PdfPageContent>();
    for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        pages.Add(reader.Read(pageIndex, cancellationToken));

    return PdfRedactionSearch.Find(pages, new PdfRedactionSearchOptions
    {
        Kind = PdfRedactionSearchKind.RegularExpression,
        Query = @"\bACCT-\d{6}\b",
        Timeout = TimeSpan.FromMilliseconds(250),
        ReasonCode = "ACCOUNT"
    }, cancellationToken);
}
```

`Matches` contains every candidate. `Included` contains the candidates still
selected after calls to `Exclude` or `Include`. Reviews are immutable, so each
change returns a new review. Search and review do not modify the document.

`ToText()` and `ToJson()` omit matched text by default. This makes the normal
report safer to log, but IDs, coordinates, reason codes, and overlay text can
still be sensitive. Pass `includeMatchedText: true` only when the destination is
approved for the source data.

Search kinds cover exact text, caller-supplied regular expressions, email
addresses, North American phone numbers, U.S. Social Security numbers, payment
card numbers with a valid checksum, and IBANs with a valid checksum. Pattern
matching proposes candidates. It does not establish that a value is genuine or
that every sensitive value was found. The regular-expression timeout must be
greater than zero and no more than ten seconds.

You can also create reviews with `PdfRedactionReview.FromRegions`,
`FromImages`, `FromComments`, or `FromAttachments`. Page indices are zero-based.
Page bounds use the same unrotated, crop-relative PDF coordinates as extraction.

## Sanitize pixels and rebuild

For text, images, and page regions, use the approved review to paint the target
areas into a full-page raster outside the engine. Overlay text must also be
painted into that raster by the host. Pass only the finished pixels and the
intended PDF page size to the rebuild API.

```csharp
using KillerPdf.Engine.Documents;

static byte[] BuildVerifiedRedactedPdf(
    IReadOnlyList<PdfSanitizedRasterPage> sanitizedPages,
    IReadOnlyList<string> prohibitedText,
    CancellationToken cancellationToken)
{
    byte[] output = PdfPermanentRedaction.RebuildFromSanitizedPages(sanitizedPages);
    PdfRedactionVerificationReport report =
        PdfPermanentRedaction.VerifySanitizedOutput(
            output,
            sanitizedPages.Count,
            prohibitedText,
            cancellationToken);

    if (!report.Succeeded)
        throw new InvalidOperationException(report.ToText());

    return output;
}
```

Each `PdfSanitizedRasterPage` contains a complete `PdfImage` plus its output
width and height in PDF points. Use a resolution appropriate for the document's
smallest important detail. Keep the page aspect ratio unless the workflow
intentionally changes it.

The result is a new, single-revision PDF with one image per page. It does not
retain searchable text, vector artwork, tags, forms, annotations, bookmarks,
attachments, metadata, optional-content data, encryption, or source revision
history. This loss is the safety boundary of page-content redaction, not a
format-preserving edit.

## Remove reviewed comments or attachments

Comments and document-level attachments have selective removal paths that do
not require rasterizing every page:

```csharp
PdfDocument document = PdfDocument.Open(sourceBytes);
PdfRedactionReview review = PdfRedactionReview.FromAttachments(document);

// Retain an attachment that policy approved.
PdfRedactionMatch retained = review.Matches.Single(
    match => match.Text == "public.txt");
review = review.Exclude(retained.Id);

PdfAttachmentRedactionResult result =
    PdfPermanentRedaction.ApplyReviewedAttachments(document, review);
byte[] output = result.Document.ToArray();
```

`ApplyReviewedComments` follows the same review pattern. It rejects overlay text
because replacing comment contents requires the raster workflow. Both methods
confirm that the reviewed targets still match the current document, perform a
pruned full rewrite, reopen the result, and verify the selected removal. A stale
review fails instead of applying to a different object.

A full rewrite of a signed document invalidates existing signatures. The write
is rejected unless `allowSignatureInvalidation: true` is passed explicitly. The
host should explain that consequence and obtain approval before enabling it.

## Verification and failure behavior

`VerifySanitizedOutput` checks page count, single-revision structure, one raster
image per page, and the absence of known recoverable stores such as text,
metadata, annotations, forms, bookmarks, attachments, structure data, actions,
and optional content. When prohibited strings are supplied, it also scans
recoverable strings and non-image streams, including common UTF-16 and
hexadecimal forms.

Verification is a defense-in-depth check, not proof that arbitrary binary
encodings or sensitive pixels are absent. It cannot tell whether the host
painted the correct rectangle or whether a secret remains visible in the image.
The host must visually review sanitized pages and choose prohibited strings that
are useful for the document.

`RebuildBatch` preserves input order and returns a result for each supported
document-specific failure. Cancellation stops the batch instead of becoming a
failed item. `PdfRedactionSearch.FindBatch` has the same isolation behavior for
search. Authentication and parsing failures can occur while opening or
extracting source documents, before either batch API receives its input.

Pass cancellation through extraction, search, and verification. For untrusted
or large files, also use process isolation, a host timeout, and memory limits.
The engine's regular-expression timeout does not bound parsing, rendering, or
the total document workflow.

The engine returns bytes and reports. File paths, overwrite policy, secure
temporary storage, audit retention, and deletion of source or intermediate
rasters remain host responsibilities. See the [security guide](security.md) for
signature policy and the [rendering guide](rendering.md) for pixel handling and
resource limits.
