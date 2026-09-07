# Rendering with KillerPDF.Engine

The CPU renderer produces page bitmaps independently of a UI framework. The caller
owns display, image encoding, scheduling, and the choice of output dimensions.
The 1.9 implementation is under development. Successful rendering does not establish
visual parity with another viewer.

In compatibility recovery, a page `/Resources` value that resolves to something
other than a dictionary is treated as empty, with a page diagnostic. Drawing that
does not require named resources can survive; missing fonts and images are not
reconstructed. Strict rendering still rejects the invalid value, and failures
while resolving the resource reference retain their existing limits.

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

The Windows app tries the requested emoji font first, then installed Segoe UI
Emoji when the requested family is unavailable. The engine uses its monochrome
outlines with the page's text paint settings; this does not enable color-font
layer rendering. Library hosts must provide their own resolver for installed
fonts. Missing fonts or glyphs can still produce outline diagnostics.

For unembedded Courier faces, the Windows app tries the requested family first,
then installed Courier New with the requested regular, bold, italic, or bold-italic
style. If neither is installed, the engine retains its bundled fallback. This
host mapping does not change library-only rendering or embedded font outlines.

Text extraction and glyph selection use distinct mappings. For an embedded symbolic
TrueType font without a PDF encoding, a non-Unicode font character map is addressed
with the original character code. Its `ToUnicode` map still supplies extracted
text. Fonts with Unicode character maps retain Unicode-based glyph lookup.

## Pixels and geometry

Device color spaces accept both a name and a one-element array containing
`DeviceGray`, `DeviceRGB`, or `DeviceCMYK`, including transparency-group blending
spaces. Both forms use the same color conversion. Parameterized color spaces
still require their defining entries. These forms follow section 8.6.3 of the
[PDF reference](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf).

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

Recovery skips an image whose declared width or height is missing or is not a
positive supported integer, and reports a diagnostic. Surrounding page content
continues. Extraction records zero for a nonnumeric pixel dimension with its own
diagnostic, retaining the image's placement geometry. This does not reconstruct
the missing image or weaken strict rendering and extraction checks.

Recovery skips Form XObjects whose decoded content contains no instructions,
including whitespace-only and comment-only streams. Invalid unused resources
on these empty Forms do not prevent subsequent page content from rendering.
Stream decoding limits still apply. Nonempty Forms and strict rendering retain
their resource validation.

Cyclic Forms can expand in recovery mode until a repeated call reaches nesting
depth 16 or the page consumes 64 repeated-call expansions. Further recursive
calls are skipped with a diagnostic while surrounding content continues.
Extraction uses the same recovery bounds. This permits shrinking recursive
artwork without unbounded recursion; truncated recursion is not complete content
recovery. Strict extraction and rendering still reject the first cyclic call.

An unfiltered `ToUnicode` stream with a valid zlib header can be inflated once
in recovery mode. Authentication runs before this recovery. The decoder retains
checksum validation and the 32 MiB font-stream output limit; corrupt or oversized
data is rejected. Strict font reading does not infer the missing compression filter.

Recovery also tolerates a stray closing angle delimiter after a `CMapName` name
when followed by `def`, before the first mapping block. It does not apply that
repair inside mapping data or to unrelated names. Strict font reading retains
the original lexical validation.

Before mappings begin, recovery can also discard a `CMapName` definition whose
name contains unescaped spaces and simple ASCII name fragments. The terminating
`def` must occur on the same line, within 4,096 bytes after the initial name.
Line breaks, delimiters, mapping operators, and missing terminators prevent this
repair. Character mappings are still parsed normally; strict mode is unchanged.

Before mapping blocks begin, recovery can discard a `CIDSystemInfo` definition
whose value is a PDF indirect reference. It does not resolve that reference or
alter CID and Unicode mappings. Only a positive object number, valid generation,
`R`, and terminating `def` qualify. Other names, malformed definitions, and
references inside mapping data retain their validation. Strict mode rejects the
indirect reference in the CMap program.

If a TrueType compound glyph is cyclic or exceeds the outline nesting limit,
compatibility rendering omits that glyph and reports a diagnostic. Its text
advance is preserved. An omitted clipping glyph contributes an empty outline,
so a text object containing only damaged clipping glyphs still clips away later
painting until the graphics state is restored. Other page content can continue.
Strict rendering and direct outline reads retain the exception; unrelated font
errors and outline limits are not relaxed. Missing glyph shapes are not rebuilt.

For embedded Type 1 fonts with invalid segment lengths, recovery can locate the
`currentfile eexec` token pair within the first 1 MiB of decoded font data. It
requires a following line ending and uses the existing charstring decoder, which
stops at `closefile`. Comments and quoted strings do not supply that boundary.
Successful recovery retains the embedded outlines and reports a font diagnostic.
Strict parsing retains its original length checks; stream and program limits
remain in force. A missing boundary or unusable program is not reconstructed.

An Identity-H or Identity-V composite font with an embedded TrueType Unicode
character map can recover from a lexically unreadable `ToUnicode` stream that
contains no `begin`, `end`, or `usecmap` markers. Glyph selection still uses the
font's CID-to-glyph mapping; extraction uses the existing embedded-font reverse
mapping. Unmapped glyphs remain replacement characters. This does not reconstruct
the damaged Unicode map or guarantee complete text semantics.

The recovery appears in `PdfExtractionFont.Diagnostics`, page extraction
diagnostics, and rendered-page diagnostics. It does not suppress filter decoding
failures, map inheritance errors, mapping validation, or allocation limits.
Strict mode continues to reject the unreadable map.

When a `ToUnicode` range's destination array has the wrong length, recovery uses
only supplied entries within the declared range. Missing entries remain unmapped,
and surplus entries do not create mappings beyond the range. The declared range
still must fit the mapping limit. Strict parsing rejects the array-length mismatch.

Recovery includes explicit mappings outside a declared code space by adding
bounded ranges of the same byte width and merging overlapping ranges of that
width. Unmapped codes still have no Unicode value. Existing or newly introduced
ambiguity between character widths is rejected for standalone maps, and the 256-code-space limit
remains in force. Strict parsing requires mappings to fit the declared spaces.

For an Identity-H or Identity-V font, recovery uses the encoding's two-byte code
space when every supplied Unicode mapping has a two-byte source and at least one
mapping exists. This resolves conflicting code-space metadata without changing
the source codes or Unicode destinations. Inherited maps use the same font
context. Mixed mapping widths are not normalized, and strict parsing is unchanged.

Recovery skips a `bfrange` entry whose source endpoints have different byte
widths or descending values. Other entries remain available, and no Unicode
mapping is invented for the rejected entry. Invalid code-space declarations and
otherwise valid ranges exceeding the expansion limit still fail. Strict parsing
rejects invalid mapping ranges.

A Type0 font with missing, null, or empty descendant data can use fallback
metrics in recovery mode when it has an Identity-H or Identity-V encoding and
a readable `ToUnicode` stream. The default advance is 1000 units; original
descendant metrics cannot be reconstructed. Unicode mappings and the existing
font resolver or bundled substitutes supply available glyphs. Composite font
substitution does not reinterpret a source CID as an unrelated Unicode value
when its mapped character has no glyph. Unavailable painted outlines remain
render diagnostics. Strict reading still requires one descendant dictionary.

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

Run-length decoding validates the encoded runs and total output size before
allocating one exact-sized sample buffer. It does not grow a second output
buffer while expanding repetitions. Truncated runs, missing end markers, and
output above the configured stream limit still fail.

Image soft masks retain packed samples at 1, 2, 4, 8, or 16 bits per component.
Mask detail is sampled independently of the color image dimensions. During
reduction, area averages preserve thin features that a single source sample can
miss. Reduced masks share the image cache and are limited to four million samples.
These bounds do not relax stream-length validation or authentication checks.

Eight-bit masks with the default decode range use grouped integer sums during
reduction. The averages and rounding match the scalar sample path exactly;
custom decode ranges and other sample depths retain their existing conversions.

## JPEG 2000 decoding and opacity channels

Tile reconstruction reuses integer and floating-point sample arrays. Every rented
array is cleared before decoding so incomplete code blocks cannot reuse samples
from an earlier image. Buffers are returned at tile changes and decoder cleanup,
including failures. Actual rented lengths count toward the 256 MiB temporary
sample limit; exact-sized allocations are used when pool rounding would exceed
the remaining allowance. This limit excludes other decoder state and buffers
retained by the shared array pool, so it is not a process memory cap.

The renderer reads a single global opacity channel from the JP2 channel-definition
box and separates it from the color samples. `SMaskInData` controls its use: absent
or zero ignores opacity, one applies it, and two applies it with preblended-color
recovery. Color-space inference excludes the declared opacity channel. Explicit
PDF soft masks can still apply when embedded opacity is ignored.

Channel indices and box lengths are checked. Multiple or component-specific
opacity channels are unsupported. Compatibility rendering uses the codestream's
sample depth when it differs from the PDF dictionary. Strict rendering retains
the sample-depth mismatch check. Image dimensions, component counts, supported
sample depths, and decoder allocation limits remain checked in both modes.

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
