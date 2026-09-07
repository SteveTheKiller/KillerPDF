# Preflight and conformance checks

Preflight runs a selected set of checks without changing the source bytes.
It reports findings with stable codes, severity, and available page or object
locations. The current 1.9 implementation checks supported boundaries; successful
opening, rendering, or preflight does not certify full PDF-standard conformance.

## Run a profile

```csharp
using KillerPdf.Engine.Diagnostics;

static PdfPreflightReport CheckDocument(byte[] source, string? password = null)
{
    var profile = new PdfPreflightProfile("Document review",
    [
        PdfPreflightCheck.StructuralIntegrity,
        PdfPreflightCheck.PageBoxes,
        PdfPreflightCheck.DocumentLanguage,
        PdfPreflightCheck.ConformanceDeclarations
    ]);
    return PdfPreflightRunner.Run(source, profile, password);
}
```

A null password makes no authentication attempt. An empty string attempts an
empty password. Other values authenticate using the supplied password.
Structural or authentication failures can make document checks unavailable;
retain those findings instead of classifying the input as conforming.

| Result | Meaning |
| --- | --- |
| `Passed` | No finding has `Error` severity. Warnings and unsupported checks can remain. |
| `Complete` | No finding has `Unsupported` severity. Errors can still remain. |
| `Findings` | The evidence needed to interpret the selected checks. |

Check both flags and review findings. Neither flag expands the profile's scope
or establishes that every requirement of a PDF standard was tested.
`PageIndex` is zero-based when present. `ObjectNumber` identifies a source object
when available. `ToText()` and `ToJson(indented: true)` export reports.

## Select and share checks

`PdfPreflightProfile.General` selects structural integrity only.
`Attachments` combines structural and attachment-safety checks.
`PrintProduction` also checks page boxes, output intents, image resolution,
font embedding, transparency, color use, conformance declarations, layers,
and metadata.

Custom profiles can select language, tagged structure, form and link descriptions,
measurement annotations, and the individual print-production checks. Image-DPI
and total ink-coverage thresholds default to 300. Set `minimumImageDpi` and
`maximumInkCoveragePercent` in the profile constructor for the intended workflow.
The maximum ink threshold must be positive and no greater than 400 percent.

Use `profile.ToJson(indented: true)` and `PdfPreflightProfile.FromJson(json)` to
save and reload the exact profile. Retain the profile, source hash, engine build,
and report together when comparing runs. A profile requires at least one check;
unknown checks and invalid thresholds are rejected.

## Understand declared conformance

The declaration check reads conformance identifiers in catalog XMP metadata.
An absent declaration produces no declaration-specific finding. That absence
does not establish compliance or automatically select a target standard.

For a PDF/A-4 declaration, this check examines encryption and output-intent
boundaries. For PDF/UA-2, it uses the accessibility inspector. Other declared
PDF/A or PDF/UA parts produce unsupported findings. These implemented checks
are not exhaustive validators for either standard.

A PDF/X declaration checks encryption and output-intent boundaries and reports
full PDF/X validation as unavailable. Preserve that incomplete result.
Adding a declaration to metadata does not convert a document into a conforming
file. Standard-specific certification needs validation appropriate to the full
target standard, including requirements outside the engine's implemented checks.

## Preview a correction

Create a plan from the exact document being reviewed, inspect its properties,
and apply it only after the host has accepted the proposed change. The host must
supply the correct language; a missing-language finding does not infer it.

```csharp
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;

static PdfPreflightCorrectionPlan PreviewLanguage(PdfDocument document,
    string confirmedLanguage)
{
    return PdfPreflightCorrectionPlan.SetDocumentLanguage(document, confirmedLanguage);
}
```

The plan exposes `Kind`, `Language`, and `ChangesDocument`. After acceptance,
`plan.Apply()` returns updated bytes and verifies the saved language. If the
requested value is already present, it returns the source bytes without a new
revision. Rerun the same profile against the output and check unrelated findings.

Other correction plans cover missing descriptive metadata, explicit production
page boxes, and missing output intents. They retain their own guards: metadata
correction cannot overwrite a different existing value, and production-box
clearing is limited to bleed, trim, and art boxes. A correction is not blanket
authorization to rewrite or sanitize the document. See the
[editing guide](editing.md) for source preservation and the
[security guide](security.md) for authenticated writing and signature constraints.

## Bound validation work

`PdfDocumentInspector.Inspect` and `InspectAuthenticated` expose a
`maximumInspectedObjects` limit, defaulting to 100,000. Preflight uses the default
structural inspection limit and does not expose a cancellation-token parameter.
For untrusted or expensive batches, the host should isolate each file and impose
a process timeout. Report timeouts and unavailable checks separately from passes.

Keep conformance results separate from visual comparisons and performance
measurements. A render can succeed with missing content, a structurally valid
file can fail a selected profile, and a profile pass can leave visual differences.
The [results summary](../../validation/PERFORMANCE.md) describes the
scope and limits of corpus measurements.
