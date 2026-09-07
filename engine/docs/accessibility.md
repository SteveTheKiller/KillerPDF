# Accessibility inspection and tagged content

The engine can inspect selected accessibility requirements, read logical
structure order, propose semantic regions for review, and apply specific
corrections. These are separate operations. Passing the implemented checks does
not establish PDF/UA conformance or prove that a document is usable with
assistive technology. See [preflight](conformance.md) for validation scope.

## Inspect an existing document

Open and authenticate the PDF before inspection. A null password makes no
authentication attempt; an empty string attempts the empty password.

```csharp
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;

static PdfAccessibilityReport InspectAccessibility(byte[] source, string? password = null)
{
    PdfDocument document = password is null
        ? PdfDocument.Open(source)
        : PdfDocument.Open(source, password);
    return PdfAccessibilityInspector.Inspect(document);
}
```

`Findings` contains a stable code, severity, message, and optional page and object
location. Page indices are zero-based. `ToText()` displays one-based page numbers;
`ToJson(indented: true)` retains API indices and string enum names.
`PassesImplementedChecks` means no finding has Error severity.

Checks cover missing or malformed document language, missing structure trees,
the catalog's marked-content declaration, unsupported structure traversal,
missing figure alternate text, missing form and link descriptions, and tables
with data cells but no header cells. A nonempty description can still be wrong.
A table with a header can still have incorrect associations. Custom role mapping,
complete parent-tree consistency, and semantic correctness are not established
by this report.

## Author a small tagged page

Marked content associates painted content with a page-local marked-content ID
(MCID). The structure tree supplies its semantic role and logical placement.
This example creates both parts:

```csharp
using KillerPdf.Engine.Authoring;

static byte[] CreateTaggedExample()
{
    var content = new PdfContentStreamBuilder()
        .BeginMarkedContent(PdfStructureType.Paragraph, 0)
        .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
        .MoveText(20, 70).ShowLatin1Text("A tagged paragraph.").EndText()
        .EndMarkedContent();
    return new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "Tagged example", Language = "en-US"
        })
        .AddPage(200, 100, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Paragraph, 0, 0, 1)
        .Build();
}
```

The final three arguments to `AddStructureElement` select page zero, MCID zero,
and nesting level one beneath the preceding level-zero Document container.
The level is a hierarchy depth, not an element ID or PDF object number. Keep every MCID associated with the
intended page and content. The example uses a standard font and does not enable
PDF/UA authoring mode. For a conforming output, supply all required fonts,
descriptions, structure relationships, and validation described in
[authoring](authoring.md) and [fonts](fonts.md).

## Read and review logical order

```csharp
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;

static PdfAccessibilityReadingOrderReport ReadLogicalOrder(byte[] source)
{
    return PdfAccessibilityReadingOrder.Read(PdfDocument.Open(source));
}
```

`Items` follows the existing structure tree, not inferred visual order. Each item
includes its sequence, literal role, page, MCID or referenced object, available
structure object number, alternate description, and ActualText. Sequence and
page indices are zero-based. An MCID is not a globally unique object identity.
The report does not include extracted paragraph text or page geometry.

Missing structure, repeated references, invalid page or content identities,
and excessive nesting can throw. Do not treat failure as an empty valid order.
Compare logical order with the actual page and intended reading experience.

For an explicitly reviewed child sequence, call
`PdfAccessibilityRepair.PreviewReadingOrder(document, parentObjectNumber, orderedChildObjectNumbers)`.
The requested list must contain every existing child exactly once. The current
implementation requires a nonempty direct child array containing indirect
generation-zero references. It does not infer order or move content between
parents. If `WillChange` is true and the preview is accepted, use
`ApplyReadingOrder(document, preview)`. Applying checks the original child order
again and verifies the saved sequence. Reordering tags does not move painted
page content or repair unrelated structure relationships.

## Propose regions for semantic review

`PdfAccessibilityTaggingProposal.Inspect(document)` returns proposed headings,
paragraphs, list items, figures, links, form fields, and repeated header/footer
artifacts. `ToJson(document, indented: true)` exports the proposals and reruns
inspection. The algorithm uses extracted text sizes, simple list prefixes,
repeated marginal text, image placements, and annotation information. It orders
regions by descending top position, then left position and role.

Every proposal has `RequiresReview = true`. Confidence values are heuristic
scores, not measured probabilities. Bounding boxes use unrotated, crop-relative
PDF points with a bottom-left origin. Multi-column layouts, decorative images,
tables, reading direction, and meaningful alternate text require review.
Inspection does not write tags, run OCR, or guarantee that existing tagged pages
are excluded. It currently extracts all pages into memory and has no cancellation
parameter. Isolate large or expensive inputs in the host.

## Preview and apply a specific correction

This helper corrects a missing or invalid language in an unencrypted PDF using a
language already selected by the caller:

```csharp
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;

static byte[] RepairMissingLanguage(byte[] source, string reviewedLanguage)
{
    PdfDocument document = PdfDocument.Open(source);
    var preview = PdfAccessibilityRepair.PreviewDocumentLanguage(document, reviewedLanguage);
    if (!preview.WillChange) return source;
    var result = PdfAccessibilityRepair.ApplyDocumentLanguage(document, preview);
    return result.Document.ToArray();
}
```

An existing valid language is not replaced by this repair. The other preview/apply
pairs accept caller-supplied semantic decisions:

| Preview and corresponding Apply suffix | Target and effect |
| --- | --- |
| `FigureAlternateDescription` | Reported figure object; adds missing alternate text. |
| `FormFieldDescription` | Field name; supplies a tooltip while preserving a consistent export mapping name. |
| `LinkDescription` | Page and annotation index; fills missing annotation description and checks object identity. |
| `TableHeader` | Reported table and descendant data-cell objects; promotes the selected cell to TH with Row, Column, or Both scope. |
| `ReadingOrder` | Parent structure object; applies the reviewed permutation described above. |

Retain the preview with the exact source revision. `WillChange` is a prerequisite,
not permission to guess descriptions or semantics. Apply methods recheck their
target conditions, write an incremental update, reopen it, and return `Document`,
`Before`, and `After`. A successful targeted repair can leave other findings.
The current reopen steps have no password callback, so these examples are not a
general encrypted-document repair workflow.

Save a separate result, review the before/after findings and logical order, and
verify the affected pages and interactive descriptions. Incremental preservation
does not erase prior bytes or establish signature validity. See
[editing](editing.md) and [security](security.md) for save and authentication
boundaries.
