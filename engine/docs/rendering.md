# Rendering with KillerPDF.Engine

The CPU renderer produces page bitmaps independently of a UI framework. The caller
owns display, image encoding, scheduling, and the choice of output dimensions.
The 1.9 implementation is under development. Successful rendering does not establish
visual parity with another viewer.

## Render a page

This example requests an exact output size. Choose dimensions with the displayed
page's aspect ratio to avoid stretching it. Page indexes start at zero.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

CancellationToken cancellationToken = CancellationToken.None;
byte[] bytes = File.ReadAllBytes("input.pdf");
PdfDocument document = PdfDocument.Open(bytes);
var renderer = new PdfPageRenderer(document);
var options = new PdfRenderOptions(1024, 768,
    transparentBackground: false,
    includeAnnotations: true,
    includeFormFields: true);
PdfRenderedPage page = renderer.Render(0, options, cancellationToken);
ReadOnlyMemory<byte> pixels = page.Pixels;
```

Supply a `CancellationToken` from the host application. Reuse the renderer for an
immutable document to benefit from its instruction, font, image, and page caches.
An optional `IPdfFontResolver` constructor argument lets the host resolve fonts.

Text extraction and glyph selection use distinct mappings. For an embedded symbolic
TrueType font without a PDF encoding, a non-Unicode font character map is addressed
with the original character code. Its `ToUnicode` map still supplies extracted
text. Fonts with Unicode character maps retain Unicode-based glyph lookup.

## Pixels and geometry

`PdfRenderedPage` exposes `Width`, `Height`, `Pixels`, and `Diagnostics`. Pixels
are tightly packed BGRA32, with a top-left origin and a row stride of `Width * 4`.
The renderer applies page crop and rotation geometry. `PdfRenderOptions` specifies
the exact pixel width and height, rather than a maximum dimension or DPI.

The default background is opaque white. With `transparentBackground: true`,
unpainted pixels retain zero alpha. Annotation and form appearance rendering can
be selected independently through the options.

## Opening and authentication

`PdfDocument.Open` uses strict parsing. For viewer compatibility with damaged
inputs, explicitly choose `PdfDocument.OpenWithCompatibilityRecovery`. Recovery
is bounded and does not change the strict default or authorize unsafe writing.

An unfiltered `ToUnicode` stream with a valid zlib header can be inflated once
in recovery mode. Authentication runs before this recovery. The decoder retains
checksum validation and the 32 MiB font-stream output limit; corrupt or oversized
data is rejected. Strict font reading does not infer the missing compression filter.

Recovery also tolerates a stray closing angle delimiter after a `CMapName` name
when followed by `def`, before the first mapping block. It does not apply that
repair inside mapping data or to unrelated names. Strict font reading retains
the original lexical validation.

For non-pattern `sc`, `scn`, `SC`, and `SCN` color operations with an invalid
component count, recovery consumes the required leading operands when extra
values are present. An incomplete operation retains the current paint color.
The renderer records a diagnostic in either case. Strict rendering rejects the
same component-count mismatch.

Check `CanReadPageContent` before creating a renderer. Password-encrypted pages
require authentication. Standard Security documents whose default strings and
streams are unencrypted can expose pages while encrypted attachments remain
protected. In that case `CanReadPageContent` can be true while `IsDecrypted` is
false. Page access does not grant attachment access, authenticated permissions,
or permission to rewrite the document.

## Diagnostics and failures

Inspect `Diagnostics` after rendering. They describe compatibility or incomplete
rendering conditions and should remain available to the host for troubleshooting.
An empty diagnostic list is not a visual correctness guarantee. Compare expected
text, images, clipping, colors, and annotation appearances when validating changes.

Invalid options, inaccessible encrypted content, malformed structures, and
implementation limits can reject a request. Cancellation can interrupt a render.
For corpus work, also impose a process timeout so a single expensive input cannot
block the remaining files.

## Memory and image masks

Output dimensions are limited to 32,768 pixels per axis and 512 MiB per pixel
buffer. `maximumPixelBytes` can lower the output allocation limit. This is not a
limit on total process memory: decoding, masks, fonts, and other render state also
consume memory.

Decoded images and masks share a cache with a 64 MiB weight limit. Rendered pages
have a separate 64 MiB cache, and flattened glyph outlines have a 16 MiB cache.
Oversized entries can be used without being retained in a cache.

Image soft masks retain packed samples at 1, 2, 4, 8, or 16 bits per component.
Mask detail is sampled independently of the color image dimensions. During
reduction, area averages preserve thin features that a single source sample can
miss. Reduced masks share the image cache and are limited to four million samples.
These bounds do not relax stream-length validation or authentication checks.

## Validation and remaining work

The [validation history](../../validation/PERFORMANCE.md) and
[machine-readable records](../../validation/records) retain measured coverage,
build identities, and known differences. Compare identical inputs, pages, output
dimensions, and appearance settings. Track render time, total wall time, cold
startup, and memory separately. Fresh-process measurements include JIT costs;
concurrent builds can invalidate performance comparisons.

Coverage beyond the measured corpus, color conversion, fine image detail, and
unsupported content remain active development work. OCR accuracy requires its
own held-out validation; page-render success is not an OCR quality gate.
