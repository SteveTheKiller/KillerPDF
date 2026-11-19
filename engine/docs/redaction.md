# Permanent redaction

Permanent redaction removes the covered content from the file, not only from
the picture of the page. The engine finds candidate text, images, annotations,
comments, attachments, or regions, removes the approved content at the content
level, and reads the output back to verify the removal. Pages that cannot be
edited safely can be replaced by a sanitized image, but only when the caller
approves each page.

Do not use a redaction annotation, a filled rectangle, or an overlay drawn by
another tool as proof of removal. Those can leave the original text or image
recoverable underneath.

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
Each text match carries an `Areas` list with one rectangle per text line, so a
match that wraps or covers part of a word marks exactly those characters.

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

Text that a page shows through an annotation instead of its content, such as a
form field value or a FreeText note, is not in the extracted page content. Use
`PdfRedactionSearch.FindInAnnotations` for those. Each match proposes the whole
annotation. A widget with an empty rectangle, such as a hidden field, is
proposed through a one-point area at its position, and an annotation without a
usable rectangle is proposed through a one-point area at the page corner. The
search reads reversed annotation rectangles. It skips a malformed annotation
and lists its page in `UnreadableAnnotationPages` instead of failing.

You can also create reviews with `PdfRedactionReview.FromRegions`,
`FromImages`, `FromComments`, or `FromAttachments`. Page indices are zero-based.
Page bounds use the same unrotated, crop-relative PDF coordinates as extraction.
`PdfRedactionMarkReader.Read` returns the redaction annotations already saved in
a document, so a host can review marks that were placed but not yet applied.

## Analyze and apply

Build a `PdfRedactionRegion` for each included area. A region can carry a
reason code, overlay text, and a `FillColor`. The fill is black when not set.
`Analyze` reports, without changing the document, how each page will be
redacted.

```csharp
using KillerPdf.Engine.Documents;

static byte[] Redact(
    PdfDocument document,
    PdfRedactionReview review,
    IReadOnlyCollection<int> reviewedRasterPages,
    CancellationToken cancellationToken)
{
    var regions = review.Included
        .SelectMany(match => match.Areas.Select(area => new PdfRedactionRegion(
            match.PageIndex, area, match.ReasonCode, match.OverlayText)))
        .ToList();

    PdfContentRedactionPlan plan =
        PdfContentRedaction.Analyze(document, regions, cancellationToken);

    var options = new PdfContentRedactionOptions
    {
        // The pages a person looked at in plan.RasterPages and approved replacing
        // with an image. Never pass plan.RasterPages straight through: that approves
        // every raster fallback without review.
        ApprovedRasterPages = reviewedRasterPages
    };

    PdfContentRedactionResult result =
        PdfContentRedaction.Apply(document, regions, options, cancellationToken);
    return result.Document.ToArray();
}
```

Each page in the plan has a `PdfRedactionPageMethod`. A `Content` page keeps
its remaining text, vector artwork, and structure. The engine removes the text,
images, drawings, annotations, fields, and alternate text that the regions
cover. This works on cropped and rotated pages, inside embedded forms, and on
invisible OCR text. A partly covered image has its covered pixels painted over,
and the plan counts those images in `RedactedImages`. A `Raster` page cannot be
edited safely, and `RasterReasons` explains why. `Apply` throws
`InvalidOperationException` until every raster page is listed in
`ApprovedRasterPages`. A replaced page becomes a sanitized image at
`RasterDpi`, with the remaining text kept searchable.

The result is a new, single-revision PDF. It drops source revision history and,
by default, document information and XMP metadata (`ClearDocumentMetadata`).
`ProhibitedText` lists strings that must not remain anywhere in the output.
Overlay text is drawn into the redacted area and verified.

Two form behaviors matter for review:

- A hybrid form can carry an XFA packet whose datasets repeat every field
  value. Redaction removes that packet and the form continues as a plain
  AcroForm. `PdfContentRedactionPlan.RemovesXfaForm` reports it, and
  verification flags a packet that survives.
- When a field loses one of its widgets, redaction clears the value held on the
  field. It also removes a widget that the field tree places on a redacted page
  but the page does not list, instead of refusing the page.

Redaction keeps an image it retired from one page when another page still draws
that image through a tiling pattern, a Type 3 glyph, or an annotation
appearance.

An encrypted source requires `RemoveEncryption`. A signed source requires
`AllowSignatureInvalidation`, because the rewrite invalidates existing
signatures. The host should explain that consequence and obtain approval before
enabling either option.

Redaction clears the signature value, but a signature widget keeps its saved
appearance. The renderer paints a signature field that still carries a normal
appearance stream with drawn content, even though the field no longer holds a
signature, so the signer's box stays visible the way other readers show it.
`PdfRenderOptions.IncludeSignedSignatureFields` covers these fields as well as
signed ones. An empty signature field whose appearance draws nothing stays
hidden.

`PdfContentRedactionResult.Verification` is the read-back report.
`PdfContentRedaction.Verify` runs the same check on any output. If the output
still exposes redacted content, `Apply` throws `PdfRedactionVerificationException`
and the exception carries the failed report. Reports that list repaired
references identify them without exporting sensitive dictionary keys.

## Remove redacted words from destination names

Authors often name destinations after headings or clauses, so a redacted phrase
can survive as a destination name after the page that showed it is clean.
`PdfNamedDestinationAnonymizer.Apply(document, cancellationToken)` in
`KillerPdf.Engine.Writing` renames every named destination to `D1`, `D2`, and so
on. It reads both the `/Names /Dests` name tree and the legacy catalog `/Dests`
dictionary. A legacy name that spells the same text as a name-tree key gets the
same new name. Each tree node is walked once, even through repeated or cyclic
`/Kids`.

Links, bookmarks, and other `/Dest` entries, plus the `/D` entry of every GoTo
action, are pointed at the new names. An action whose `/S` is an indirect
object is still recognized, and a destination given as an indirect string or
name becomes the direct new name. The catalog receives a flat name tree in the
new order. The result is then rewritten whole with unreachable objects pruned,
so the old names and the objects that spelled them leave the file instead of
surviving in an earlier revision.

That full rewrite always allows signature invalidation, so a signed document
loses its signatures. A document with no named destinations is returned as the
same instance. The redaction pipeline does not call the anonymizer; the host
calls it on the redacted output when destination names may carry removed text.

## Check the finished file for surviving words

`PdfContentRedaction.FindSurvivingText(output, terms, matchCase: false,
cancellationToken)` reports where any of the removed words still appears in the
final bytes. Run it last, after any destination renaming or other cleanup, with
the terms the redaction removed. Blank terms are ignored and matching is a plain
substring, ignoring case unless `matchCase` is true. It returns a
`PdfRedactionVerificationReport`.

The check reads each page's extracted text and reports matches as
`ProhibitedText` with the page index. It reads the names in the legacy catalog
`/Dests` dictionary and reports a match as `ProhibitedObjectText`. It then reads
every object in use: text strings, which include name-tree destination names,
bookmark titles, scripts, and metadata values, and every decodable stream other
than images. A stream that cannot be decoded produces `ObjectInspectionFailure`,
because it cannot be shown to be free of the words.

Embedded font programs are binary, so a short number could match inside one by
chance. The check skips a stream only when a font descriptor names it as
`/FontFile`, `/FontFile2`, or `/FontFile3`, that descriptor belongs to a `/Font`
dictionary reached from the pages or the AcroForm, and the stream could be a font
program. A stream typed as an XObject, embedded file, or metadata is never
skipped, and neither is a stream whose `/Subtype` is not `Type1C`,
`CIDFontType0C`, or `OpenType`. A stray or orphan descriptor therefore cannot
hide a stream from the check.

## Rebuild from host-sanitized pages

When a host prefers to paint the approved areas itself, for example to use its
own renderer, it can rebuild an image-only PDF from the finished pixels. Pass
only the finished pixels and the intended PDF page size.

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
intentionally changes it. Overlay text must also be painted into that raster by
the host.

The result is a new, single-revision PDF with one image per page. It does not
retain searchable text, vector artwork, tags, forms, annotations, bookmarks,
attachments, metadata, optional-content data, encryption, or source revision
history. This loss is the safety boundary of the rebuild path.

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

`PdfContentRedaction.Verify` reads the output back and reports any text,
vector, image, shading, or annotation that still intersects a region, any
revision history, and any remaining prohibited text. It also checks active
objects and document or page data in common encodings.

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

Comment and attachment removal is verified after the rewrite.

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
