# Structural document comparison

`PdfStructuralComparison` compares interpreted page content, effective page
resources, and page geometry. It reports what category changed without claiming
that two pages look different. The host decides whether a reported category is
acceptable for its workflow.

## Compare two documents

Open both inputs with the credentials required to read their page content, then
pass a cancellation token through the comparison.

```csharp
using KillerPdf.Engine.Documents;

static PdfStructuralComparison CompareDocuments(
    byte[] originalBytes,
    byte[] changedBytes,
    CancellationToken cancellationToken)
{
    PdfDocument original = PdfDocument.Open(originalBytes);
    PdfDocument changed = PdfDocument.Open(changedBytes);
    return PdfStructuralComparison.Compare(original, changed, cancellationToken);
}
```

`HasChanges` is true when at least one category differs. `Changes` is ordered by
zero-based page index and category. `ToText()` produces a readable summary, and
`ToJson(indented: true)` produces a versioned machine-readable report.

```csharp
PdfStructuralComparison comparison = CompareDocuments(
    originalBytes, changedBytes, cancellationToken);

if (comparison.HasChanges)
    File.WriteAllText(reportPath, comparison.ToJson(indented: true));
```

The engine returns the report text or bytes to the host. File naming, overwrite
policy, storage, and user approval remain host responsibilities.

## Interpret change categories

| Category | Compared evidence |
| --- | --- |
| `PageAdded`, `PageRemoved` | Page counts beyond the shared page range |
| `PageSize` | Effective page width and height |
| `Text` | Extracted letters, geometry, and font metadata |
| `Images` | Extracted image placements |
| `Paths` | Paint operators, clipping state, bounds, segments, and points |
| `Shadings` | Extracted shading placements |
| `Resources` | Effective page-resource graph, compared by content rather than object number |
| `Instructions` | Decoded content operators, operands, and inline-image bytes |

`OriginalCount` and `ChangedCount` are category item counts. They help locate a
difference but do not measure its visual size or importance. A resource-only
change can be harmless, while a one-item instruction change can alter a whole
page.

## Scope and limits

This comparison does not render pages or compare pixels. It does not compare
document metadata, signatures, encryption settings, bookmarks, attachments,
form values, annotations, layer configuration, or every object reachable from
the trailer. Use the feature-specific readers and comparison APIs for those
domains. Use the rendering guide when visual equivalence matters.

Equivalent content stored under different indirect object numbers compares by
resolved resource content. Instruction comparison remains intentionally exact:
two operator sequences that paint the same pixels can still report a change.
Conversely, matching structure is not proof that two renderers will produce
identical pixels.

The comparison reads every shared page and has no page-count shortcut. Run large
or untrusted batches in an isolated process with a host timeout and cancellation.
Cancellation is checked between pages, while reading page content, and while
walking resource graphs. A resource graph deeper than 256 levels is rejected.

Malformed or unsupported content can raise the same parsing, filtering, or
format exceptions as normal page extraction. Authentication failures occur when
opening protected inputs, before comparison starts. Preserve these failures as
unavailable results rather than reporting no changes.

Comparison is read-only. It does not rewrite either document, invalidate a
signature, or establish that a later revision is authorized by certification
permissions or field locks. See the
[security guide](security.md) for signature and permission checks and the
[reading guide](reading.md) for authentication and batch-failure handling.
