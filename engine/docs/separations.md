# Separation inspection and preview planning

The separation APIs inventory process and named spot colorants used by page
content, then validate a plate and page selection for a host preview workflow.
They produce reports and selection metadata. They do not render individual
plates, calculate ink coverage, or simulate a press proof.

## Inspect used colorants

Open an authenticated document and inspect it without changing it:

```csharp
using KillerPdf.Engine.Documents;

PdfDocument document = PdfDocument.Open(sourceBytes);
PdfSeparationReport report = PdfSeparationInspection.Inspect(document);

foreach (PdfSeparationColorant colorant in report.Colorants)
{
    string kind = colorant.IsProcess ? "Process" : "Spot";
    Console.WriteLine($"{kind}: {colorant.Name}");
    Console.WriteLine($"Pages: {string.Join(", ", colorant.PageIndexes)}");
}
```

`PageIndexes` are zero-based. Results are deterministic: process plates come
first, then spot plates, with names sorted ordinally inside each group. A
declared spot color space is not reported unless page or nested form content
selects it with a nonzero tint.

The process names are `Cyan`, `Magenta`, `Yellow`, and `Black`. A process plate
is reported when a nonzero component appears in a `k` or `K` content
instruction. Spot names come from active `Separation` and `DeviceN` color-space
instructions. The name `None` is ignored.

## Build a preview selection

Use the inspected names to let the user choose plates, then create a validated
selection for all pages or a unique subset:

```csharp
PdfSeparationPreview preview = PdfSeparationPreview.Create(
    document,
    plateNames: ["Black", "Killer Orange"],
    pageIndexes: [0, 2]);

string text = preview.ToText();
string json = preview.ToJson(indented: true);
```

`Plates` preserves the inspection order and reports which selected pages use
each plate. `Pages` preserves the requested page order and reports the selected
plates present on each page. A selected plate remains in `Plates` even when none
of the selected pages use it, so the host can distinguish an empty selection
result from an unknown plate.

Plate names are exact and case-sensitive. Blank, duplicate, or unknown names
are rejected. Page indices must be in range, unique, and nonempty. Omit
`pageIndexes` to select every page.

The text report displays page numbers as one-based values. JSON retains
zero-based indices and includes a version number. Neither format contains page
content or ICC profile bytes, but file names and custom plate names can still be
sensitive in a larger host report.

## Render in the host

`PdfSeparationPreview` is a plan, not pixels. The current engine renderer
produces a composite page through alternate color spaces. It does not expose a
public API that suppresses all but one process or spot plate. A host must use a
separation-capable renderer or print pipeline to produce actual plate images.

Keep the plan tied to the exact source bytes that were inspected. If the
document changes, inspect it again before rendering or exporting plates. When a
host renderer creates plate images, label them with both the plate name and
one-based page number, and make an empty plate visibly different from a failed
render.

Do not treat a composite alternate-color rendering as a contract proof. Spot
alternate colors describe an on-screen approximation, not the physical ink.
Overprint, trapping, transparency blending, output intent, substrate, and press
conditions require a color-managed production workflow and independent review.

## Inspection limits

Inspection is intentionally narrower than full rasterization. It recognizes
nonzero process `k` and `K` instructions plus `Separation` and `DeviceN` tint
instructions in page content and nested Form XObjects. It does not calculate
pixel coverage or decide whether paint is later clipped, hidden, overprinted,
made transparent, or covered by other content.

The inventory is not a complete preflight of every possible color source. In
particular, do not rely on it alone to classify process channels inside image
samples, patterns, shadings, ICC-based conversions, or other alternate color
paths. The [print-production report](print-production.md) combines page box,
output-intent, and separation facts, but an independent prepress tool is still
required when complete production evidence matters.

## Failure behavior

Encrypted documents must be authenticated before inspection. Malformed color
instructions, missing resources, unsupported stream filters, unbalanced
graphics state, cyclic references or forms, excessive form nesting, and content
size limits can stop inspection with a format or decoding exception. An error
means the inventory is unavailable, not that the document has no plates.

The APIs are synchronous and do not accept a cancellation token. Run large or
untrusted documents in an isolated process with host-enforced time and memory
limits. See the [reading guide](reading.md) for authentication and batch-failure
handling, and the [rendering guide](rendering.md) for composite rendering limits.
