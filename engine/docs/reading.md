# Opening documents and extracting page content

This guide describes the 1.9 development API. Use the repository project reference
shown in the [engine README](../README.md) when testing this branch. The library
targets .NET 10 and does not require a UI toolkit for these operations.

## Open a document

Read the file into memory, choose strict parsing or compatibility recovery, then
create the reader for the operation you need:

```csharp
using KillerPdf.Engine.Documents;

byte[] source = File.ReadAllBytes("input.pdf");
PdfDocument document = PdfDocument.Open(source);
IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
Console.WriteLine($"Pages: {pages.Count}");
```

`Open` validates the cross-reference structure and resolves other objects lazily.
A successful open does not establish that every page, font, image, signature, or
conformance requirement is valid. An operation can discover a problem later when
it resolves the relevant objects.

For a viewer that needs bounded recovery from damaged input, explicitly use
`PdfDocument.OpenWithCompatibilityRecovery(source)`. Recovery belongs to that
document instance and is used by its page readers and renderer. It can tolerate
selected malformed structures; it is not a guarantee of complete visible content
or permission to write a damaged file. The [rendering guide](rendering.md) explains
image and font recovery details and their remaining limits.

Recovery derives page counts from the actual page-tree children without resolving
`/Count` metadata, including invalid values or references to damaged streams.
Empty intermediate branches contribute no pages in recovery; neighboring pages
retain their order and inherited geometry. Strict readers still reject empty
intermediate branches and validate the declared count. Tree cycle, repeated-node,
nesting-depth, and maximum-page limits remain enforced in recovery mode.

If the declared root resolves to a dictionary with neither a catalog declaration
nor a page tree, page-tree readers can select one unambiguous uncompressed catalog.
This fallback inspects only documents with at most 4,096 cross-reference entries;
larger documents and multiple candidates retain the failure. The original trailer
is unchanged, so strict readers and writer root validation still reject the damage.

For compressed objects, recovery can match the requested object number to its
validated object-stream header when the cross-reference index is incorrect or
wrapped. Only streams with mismatched indexes allocate the additional lookup.
Duplicate object numbers, invalid offsets, stream ownership, object-count limits,
and strict index validation retain their existing checks.

If a stream's declared length ends inside encoded data, compatibility parsing can
use the existing bounded end-marker search even when those remaining bytes are
not valid PDF tokens. The search extends at most 1 MiB beyond the declared end,
within the input. Encoded bytes are preserved for the filter decoder; this does
not relax numeric or string syntax elsewhere. Strict parsing retains its lexical
errors at the declared boundary.

ASCII85 compatibility decoding also accepts the end of a stream in place of a
missing `~>` marker, including empty streams. Complete tuples and final groups of
two to four digits retain their normal decoding. A lone final digit, invalid
characters, misplaced `z`, overflowing tuples, malformed explicit end markers,
and output-size violations still fail. Strict decoding requires the end marker.

## Memory ownership and concurrency

Both opening methods copy the supplied bytes. The caller can reuse or modify its
input buffer after the call without changing the parsed document. The document
retains its owned copy and fills object caches as needed, so budget for input,
the owned copy, decoded data, and any rendered or extracted results.

`PdfDocument` does not implement `IDisposable`. Release references to the document
and associated readers when finished so their managed buffers can be collected.
Opening a document does not keep the original file open or modify it.

Object resolution populates mutable caches. Serialize operations that share a
document, reader, or renderer. For parallel workers, open separate document
instances and account for the additional memory. Cancellation and allocation
bounds do not replace a host's process or workload limits.

## Passwords, certificates, and permissions

The password overload authenticates while opening. An empty string is an actual
password attempt; it is useful for owner-password-only files with an empty user
password. It does not bypass a nonempty user password.

```csharp
using KillerPdf.Engine.Documents;

byte[] source = File.ReadAllBytes("input.pdf");
PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source, string.Empty);
Console.WriteLine(document.PasswordAuthenticationRole);
if (!document.CanReadPageContent)
    throw new InvalidOperationException("Page content requires authentication.");
```

For a password supplied by the host, pass that string instead. Incorrect passwords
can throw a `System.Security.Cryptography.CryptographicException`; malformed or
unsupported encryption can fail separately. Do not classify every open error as
an incorrect password.

Certificate-recipient encryption uses the overload accepting an
`X509Certificate2`. Supply a matching recipient certificate with the private key
needed to decrypt the file. Certificate loading and lifetime belong to the host.

| Property | Meaning |
| --- | --- |
| `IsEncrypted` | The file declares an encryption handler. This stays true after authentication. |
| `IsDecrypted` | The file is unencrypted or encrypted objects are available after authentication. |
| `CanReadPageContent` | Page content can be accessed, including certain files with unencrypted page streams and encrypted attachments. |
| `PasswordAuthenticationRole` | The authenticated password role, or `None` when no password role applies. |
| `DeclaredPermissions` | Authenticated Standard Security permission flags, or `null` when unavailable. |

Page access does not grant access to encrypted attachments or authorize rewriting
the document. For attachment-only encryption, `CanReadPageContent` may be true
while `IsDecrypted` is false. Inspect the permission flags and apply the host's
operation policy; a readable page alone does not establish permission to copy,
print, annotate, or edit it.

## Page geometry

`PdfPageInformation.Read(document)` returns every page's effective crop or media
geometry. Indices are zero-based. `Left` and `Bottom` identify the effective box
origin; `Width` and `Height` are measured before page rotation. `Rotation` is the
clockwise display rotation in degrees.

Extraction uses unrotated, crop-relative PDF points with a bottom-left origin.
Rendering returns pixels with a top-left origin and accounts for display rotation.
For a 90- or 270-degree page rotation, display width and height exchange roles.
Keep these coordinate systems separate when placing search highlights or overlays.

## Extract text, images, and drawing information

Authenticate first when needed. A reader's constructor checks page-content access
and reads the page tree; these steps can fail before extraction begins.

```csharp
using KillerPdf.Engine.Documents;

PdfDocument document = PdfDocument.Open(File.ReadAllBytes("input.pdf"), string.Empty);
var reader = new PdfPageContentReader(document);
if (reader.PageCount > 0)
{
    PdfPageContent page = reader.Read(0);
    foreach (PdfExtractedWord word in page.Words)
        Console.WriteLine($"{word.Text}: {word.BoundingBox}");
    foreach (string diagnostic in page.Diagnostics)
        Console.WriteLine(diagnostic);
}
```

`Read(pageIndex, cancellationToken)` returns `PdfPageContent`:

| Result | Use |
| --- | --- |
| `Letters` | Decoded text fragments, glyph bounds, font sizes, and baseline endpoints. |
| `Words` | Geometrically grouped words and their constituent letters. |
| `TextRuns` and `Lines` | Runs grouped by font and writing direction, then by visual baseline. |
| `Text` | Words separated by spaces in content order. |
| `Images` | Image placements, resource identifiers, source dimensions, and effective resolution when available. |
| `Paths` and `Shadings` | Interpreted drawing operations and geometry. |
| `Instructions` | Interpreted content instructions, including expanded Form XObjects. |
| `MarkedContent` | Balanced marked-content sequences and available optional-content metadata. |
| `Diagnostics` | Recovery conditions encountered during extraction. |

`ReadInstructions` instead returns unexpanded instructions from the page's decoded
content streams. Choose it when the original page-level instruction sequence is
the desired input to further processing.

Extracted text is not OCR. Image placements do not contain newly decoded image
pixels. Content order is not a guaranteed semantic reading order for multicolumn
documents. Extraction also does not prove that text is visible after clipping,
overpainting, transparency, or optional-content handling. Use rendered output to
check visual content, and validate OCR separately on held-out data.

## Isolate page failures

`PdfPageContentBatch.Read` reads pages sequentially and records ordinary per-page
failures while continuing to later pages:

```csharp
using KillerPdf.Engine.Documents;

PdfDocument document = PdfDocument.Open(File.ReadAllBytes("input.pdf"), string.Empty);
foreach (PdfPageContentBatchResult result in PdfPageContentBatch.Read(document))
{
    if (result.Succeeded)
        Console.WriteLine($"Page {result.PageIndex + 1}: {result.Content!.Text}");
    else
        Console.WriteLine($"Page {result.PageIndex + 1}: {result.Error ?? "Canceled"}");
}
```

An open, authentication, or page-tree failure can still reject the batch before
there are page results. Cancellation can return a partial list: cancellation
before the next page stops iteration, while cancellation during a page records
`WasCanceled` and then stops. Fatal resource/runtime failures are not converted
into ordinary page errors. The batch retains successful content in memory; use
the single-page reader and release results as you go for large documents.

For hostile or expensive corpus files, also isolate files in separate processes
with time limits. A cancellation token is cooperative and does not guarantee
that every low-level operation returns immediately.

## Writing and further reference

Opening and reading do not alter the source file. Writers and editors produce new
bytes, which the host decides where to save. A full rewrite and an incremental
update have different preservation and signature implications; choose the writer
for that operation rather than treating rendering success as a save check.

The public API's XML comments accompany the source. Relevant entry points are
[PdfDocument](../KillerPdf.Engine/Documents/PdfDocument.cs),
[PdfPageInformation](../KillerPdf.Engine/Documents/PdfPageInformation.cs),
[PdfPageContentReader](../KillerPdf.Engine/Documents/PdfPageContentReader.cs), and
[PdfPageContentBatch](../KillerPdf.Engine/Documents/PdfPageContentBatch.cs).
See the [rendering guide](rendering.md) for pixel output and the
[validation history](../../validation/PERFORMANCE.md) for measured coverage and
known differences. The broader authoring, editing, security, and validation APIs
are outlined in the [engine README](../README.md).
