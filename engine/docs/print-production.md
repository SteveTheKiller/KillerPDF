# Print-production reports

`PdfPrintProductionReport` combines the effective page boxes, detected process
and spot colorants, and validated document-level output intents in one read-only
report. Use it to populate an inspection screen or attach factual evidence to a
prepress review.

The report is not a pass or fail decision. Use print-production preflight and an
independent production tool for policy enforcement and press-ready approval.

## Inspect a document

Authenticate the document if needed, then inspect it:

```csharp
using KillerPdf.Engine.Documents;

PdfDocument document = PdfDocument.Open(sourceBytes);
PdfPrintProductionReport report = PdfPrintProductionReport.Inspect(document);

string text = report.ToText();
string json = report.ToJson(indented: true);
```

`Pages` is in page order. `Colorants` lists process plates first, followed by
spot names in ordinal order. `OutputIntents` preserves catalog order. The text
report displays one-based page numbers, while object properties and JSON use
zero-based page indices.

Inspection does not render or modify the PDF. Keep the report tied to the exact
source bytes that produced it. If the source changes, inspect it again.

## Interpret page boxes

Each `PdfPageBoxInformation` exposes the effective page boundaries in PDF
points:

| Box | Meaning | Fallback |
| --- | --- | --- |
| `MediaBox` | Physical page boundary | Required, or compatibility default for recoverable malformed files |
| `CropBox` | Viewer and print clipping boundary | `MediaBox` |
| `BleedBox` | Boundary for bleed content | `CropBox` |
| `TrimBox` | Intended finished-page boundary | `CropBox` |
| `ArtBox` | Meaningful artwork boundary | `CropBox` |

Bounds can have nonzero or negative origins. Use `Left`, `Bottom`, `Right`, and
`Top` rather than assuming a page starts at `(0, 0)`. `Width` and `Height` are
calculated from those bounds.

`HasExplicitCropBox`, `HasExplicitBleedBox`, `HasExplicitTrimBox`, and
`HasExplicitArtBox` mean the box appears directly in that page dictionary. A
false value can mean an inherited value or the documented fallback. It does not
mean the effective box is missing.

The report exposes geometry. It does not by itself decide whether bleed and trim
relationships satisfy a job ticket or PDF standard. Run preflight for those
checks.

## Interpret colorants

Each `PdfSeparationColorant` contains a name, an `IsProcess` flag, and the pages
where inspection found nonzero use. The standard process names are `Cyan`,
`Magenta`, `Yellow`, and `Black`; other names represent detected spot colorants.

This is instruction-level inventory, not ink coverage. It does not render
individual plates or account for every image, pattern, shading, clipping,
transparency, overprint, or color-conversion path. See
[separation inspection and preview planning](separations.md) for the exact
recognition boundary and host-rendering requirements.

## Interpret output intents

Each `PdfOutputIntentInformation` contains the intent subtype, output condition
identifier, optional descriptive fields, and a validated embedded
`PdfIccProfile`. The profile exposes its color-space signature, component count,
and original bytes.

The report's `ToText()` and `ToJson()` methods include profile color space,
component count, and byte count but omit the ICC bytes. The in-memory
`OutputIntents` property still contains `Profile.Data`, so do not serialize or
log the object graph with a general-purpose serializer if profile disclosure is
not intended.

An output intent describes the intended output condition. Its presence does not
prove that all page content is color managed, that the profile matches a print
provider's current requirement, or that the file conforms to PDF/X or PDF/A.

## Add a pass or fail preflight

Run the built-in print-production profile separately when the host needs
findings and severities:

```csharp
using KillerPdf.Engine.Diagnostics;

PdfPreflightReport preflight = PdfPreflightRunner.Run(
    sourceBytes,
    PdfPreflightProfile.PrintProduction);

if (!preflight.Passed || !preflight.Complete)
    Console.Error.WriteLine(preflight.ToText());
```

Preflight checks page-box relationships, output intents, color usage, font
embedding, image resolution, transparency, overprint, and related production
facts according to the profile. A clean built-in report is still not a contract
proof for a specific printer, substrate, binding, or finishing process. Add host
policy for the actual job ticket and validate with the receiving print system.

Do not silently add or replace an output intent to make a check pass. A profile
must come from an approved source and match the intended output condition.
Applying a correction changes the document and affects signature status. See
the [preflight guide](conformance.md) for reviewed correction plans and the
[security guide](security.md) for signed-document policy.

## Failure behavior

Encrypted documents must be authenticated before output-intent or separation
inspection. Invalid page boxes, malformed color instructions, missing or cyclic
resources, unsupported stream filters, absent destination profiles, oversized
profile streams, and invalid ICC bytes can stop the combined inspection. Treat
an exception as an unavailable report, never as a clean document.

`PdfPrintProductionReport.Inspect` is synchronous and has no cancellation
parameter. Large or untrusted documents should be processed in an isolated
worker with host-enforced time and memory limits. Batch workflows should retain
one failure per document rather than replacing failed reports with empty lists.

The engine returns report data and text. File naming, storage, overwrite policy,
job-ticket comparison, and approval remain host responsibilities.
