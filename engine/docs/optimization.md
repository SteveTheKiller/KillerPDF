# Repair, optimization, and selective removal

Use `PdfSaveSanitizer` for its small set of incremental structural repairs.
Use `PdfOptimizer` to preview a full rewrite with explicit compression and
removal choices. Neither API writes a file; the host receives output bytes.

The examples target current 1.9 source and unencrypted, unsigned documents.
For protected input, first establish authentication, modification permissions,
and the intended output protection policy using the [security guide](security.md).
The optimizer's internal reopen steps do not accept a password callback; these
examples are not an encrypted-document optimization recipe.

## Preview lossless compression

Creating a plan inspects the document without applying its changes. Retain the
same immutable document and source bytes while reviewing and applying the plan.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;

static PdfOptimizationPlan PreviewCompression(PdfDocument document)
{
    return PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
    {
        CompressUnfilteredStreams = true,
        PruneUnreachableObjects = true,
        PackObjects = true,
        CompressStructure = true
    });
}
```

This compresses eligible unfiltered streams only when the encoded result is
smaller and omits objects unreachable from the output trailer. It does not
resample images or introduce lossy image compression. Unreachable-object removal
does not remove a reachable attachment, annotation, or metadata entry.

Every optimization plan consolidates revisions into a full rewrite, even with
all optional flags false. `PackObjects` and `CompressStructure` default to true;
other options default to false. Output is not guaranteed to be smaller.
The optimizer exposes no target-version option. For older PDFs that cannot
use object or cross-reference streams, disable both packing and structural
compression or use a deliberate writer version policy separately.

`PruneUnusedPageResources` is a separate opt-in for unused resources and
equivalent resource aliases. Content and structure guards still apply. Do not
assume every malformed or tagged document can use every optimization.

## Inspect the plan and apply it

A host can present `ToText()` or `ToJson(indented: true)` as a preview. The typed
properties expose attachment and field names, comment counts, resource and
thumbnail pages, hidden layers, proposed repairs, and object numbers affected by
compression or unreachable-object pruning. Page-index properties are zero-based;
the text report displays page numbers starting at one.

After the host has chosen the plan, apply that same instance:

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;

static PdfOptimizationResult ApplySelectedPlan(PdfOptimizationPlan plan)
{
    PdfOptimizationResult result = plan.Apply();
    PdfDocument reopened = PdfDocument.Open(result.Data);
    _ = new PdfPageContentReader(reopened).PageCount;
    return result;
}
```

`Apply` checks that the output reopens, has the original page count, has one
cross-reference revision, and satisfies the removal checks corresponding to the
reported changes. It can throw during inspection, editing, writing, or verification.
A preview does not guarantee that application will succeed.

`OriginalSize`, `OutputSize`, and `SizeDifference` measure actual bytes.
The difference is output minus input, so a negative value means a smaller file.
Object counts describe active cross-reference objects. `VerifiedRemovals`
lists checks that passed after saving; `Repairs` lists structural repairs.
Result JSON contains these measurements and descriptions, not the PDF bytes.

## Deliberately remove document features

Removal choices can change behavior and visible content. Select them for the
intended output, independently of compression. This example removes document
information/XMP, attachments, and the opening action:

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;

static PdfOptimizationPlan PreviewRemoval(PdfDocument document)
{
    return PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
    {
        RemoveMetadata = true,
        RemoveAttachments = true,
        RemoveOpenAction = true,
        PruneUnreachableObjects = true
    });
}
```

Unreachable-object pruning is selected so detached objects are not retained just
because they remain in the active cross-reference set.

| Option | Intended change |
| --- | --- |
| `RemoveMetadata` | Remove document information and catalog XMP |
| `RemoveAttachments` | Remove embedded files, including attachment annotations |
| `RemoveOpenAction` | Remove the catalog opening action |
| `RemoveBookmarks` | Remove the outline hierarchy |
| `RemoveFormFields` | Remove interactive fields and widgets; this is not flattening |
| `RemoveXfaData` | Remove XFA packets while retaining ordinary AcroForm fields |
| `RemoveComments` | Remove annotations identified as review comments |
| `RemoveDocumentJavaScript` | Remove the JavaScript name tree and reachable JavaScript actions |
| `RemovePageThumbnails` | Remove embedded thumbnail images |
| `FlattenOptionalContent` | Keep initially visible layer content and remove hidden layer content |

A requested option may be absent from `Changes` when the corresponding feature
is absent. The preview is based on the engine's supported readers and graph
traversal, not a universal classification of every embedded payload.

Metadata removal does not scrub personal information from page text, images,
document identifiers, or every custom dictionary. JavaScript removal does not
certify a document free of all active content. Neither successful optimization
nor `VerifiedRemovals` is a general redaction or malware-safety guarantee.

## Preview a small save repair

The save sanitizer removes empty or dangling outline roots and page-level crop
boxes that are degenerate or outside the effective media box. It does not perform
the renderer's broad compatibility recovery or reconstruct missing page content.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;

static byte[] ApplySaveRepair(PdfDocument document)
{
    PdfSaveRepairPlan plan = PdfSaveSanitizer.CreateRepairPlan(document);
    foreach (PdfSaveRepairChange change in plan.Changes)
        Console.WriteLine(change.Description);
    return plan.Apply();
}
```

Inspect `HasChanges` and `Changes` before applying when a host needs a preview.
With no repairs, output bytes equal the original. With repairs, an incremental
revision preserves the original byte prefix. Earlier content remains in those
bytes. `RepairHarmlessArtifacts(document)` combines preview and application;
`PdfOptimizationOptions.RepairHarmlessArtifacts` includes these repairs in an
optimization plan followed by a full rewrite.

## Validate the intended result

Retain the source until the output has passed the host's checks. Compare page
geometry, text, images, and appearances before and after lossless optimization.
For selective removal, verify both the removed feature and the content that
should remain. Recheck accessibility and conformance when those are requirements.

Signed files require a separate decision: full rewrites invalidate signatures,
and `AllowSignatureInvalidation` defaults to false. Incremental preservation
alone does not establish compliance with certification or field-lock permissions.

Planning and application are synchronous and expose no cancellation token.
For corpus or service workloads, place expensive documents behind a bounded
process timeout. Avoid sharing mutable source bytes or assuming that a plan
remains applicable to a different document revision.
