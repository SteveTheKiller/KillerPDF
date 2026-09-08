# Rendering with KillerPDF.Engine

The CPU renderer produces page bitmaps independently of a UI framework. The caller
owns display, image encoding, scheduling, and the choice of output dimensions.
The 1.9 implementation is under development. Successful rendering does not establish
visual parity with another viewer.

In compatibility recovery, a page `/Resources` value that resolves to something
other than a dictionary is treated as empty, with a page diagnostic. Drawing that
does not require named resources can survive. A missing font resource whose name
exactly matches one of the 14 standard PDF fonts uses that standard font, with a
diagnostic. This also applies inside existing form appearances. Declared fonts
retain precedence; unknown font names and missing images are not reconstructed.
Strict rendering still rejects the invalid resource value, and failures
while resolving the resource reference retain their existing limits.

Requested single-line form appearance regeneration and its current limits are
described in the [forms guide](forms.md#values-and-rendered-appearances).

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

Unembedded standard Symbol and ZapfDingbats use bundled CFF outlines in both the
library and app. These cover all 189 Symbol and 202 ZapfDingbats encoding names,
including space, without consulting installed fonts. PDF encoding differences
select the corresponding glyph names. Embedded font programs retain precedence.
The fixed upstream revision and redistribution notice are recorded in
[third-party notices](../THIRD-PARTY-NOTICES.txt).

If an installed TrueType font has no glyph for a requested character, rendering
continues to the bundled outline fallback instead of painting the host font's
missing-glyph box. An embedded font retains its own glyph-zero behavior. A glyph
absent from every available mapping can still remain unpainted or be diagnosed.
Undefined simple-font codes do not borrow unrelated installed glyphs at the same
numeric position.

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

Unprofiled CMYK uses a subtractive approximation: each remaining color-channel
intensity is multiplied by the remaining intensity after black ink. This preserves
shadow detail where adding ink components would clip a channel to zero. It is not
ICC color management; profile-dependent colors can still differ from other viewers.

Pages and isolated groups declaring a CMYK blending space retain all four ink
components through painting and compositing. Black-only and process-black colors
therefore remain distinct even when their displayed RGB colors match. CMYK working
surfaces use four ink bytes per pixel and retain uniform alpha as one value.
An alpha plane is allocated only when samples differ. Finished pages convert
to BGRA. Non-isolated groups inherit their parent's blending space.
An ICCBased CMYK alternate selects this same device approximation; the profile
itself is not applied.

`PdfRenderedPage` exposes `Width`, `Height`, `Pixels`, and `Diagnostics`. Pixels
are tightly packed BGRA32, with a top-left origin and a row stride of `Width * 4`.
The renderer applies page crop and rotation geometry. `PdfRenderOptions` specifies
the exact pixel width and height, rather than a maximum dimension or DPI.

The default background is opaque white. With `transparentBackground: true`,
unpainted pixels retain zero alpha. Annotation and form appearance rendering can
be selected independently through the options.

## Masks on non-isolated groups

Normal-blend, non-knockout transparency groups retain an outer soft mask until
the whole group has been painted. Internal blends use the original backdrop;
an internal mask reset cannot remove the group's outer mask. The final group
result is interpolated in premultiplied color and alpha, preserving transparent
backdrops and applying the outer mask once to overlapping objects.

This interpolation path applies when the parent is not a knockout group.
Non-knockout groups with outer non-normal blend modes track their own accumulated
alpha separately from the initial backdrop. Once the group is painted, the initial
backdrop contribution is removed and the outer blend, opacity, and mask are applied
once. Internal Normal blending cannot erase the outer blend mode. Unsupported
non-isolated knockout combinations retain their existing diagnostic boundaries.

## Memory ownership

Set `CacheResult = false` on `PdfRenderOptions` for a one-pass export. This bypasses
the finished-bitmap cache while retaining reusable fonts, instructions, and decoded
images. The default remains cached rendering for repeated page requests.

For reusable output storage, call `RenderInto(pageIndex, options, destination)`.
The caller owns the byte array, which must hold at least `Width * Height * 4`
bytes. The method returns diagnostics and never caches that buffer. Encode or
consume its pixels before reusing it. Cancellation can leave partial output.
Ordinary `Render` results retain their immutable bitmap ownership.

Fractional axis-aligned rectangles retain three coverage rows instead of a full
mask. The existing rasterizer calculates the edge rows, and the interior row is
reused without changing coverage rounding. Transparency-group working pixels,
knockout bookkeeping, and backdrop copies use group bounds. Drawing coordinates
stay in page space. Graphics-state soft masks store their bounded samples and
the constant outside value, including the transfer function. A uniform mask
stores one inside value instead of a pixel plane. If samples differ, it retains
every original byte; this does not add quantization or change the mask bounds.

Explicit one-bit image masks keep their packed rows in the decoded-image cache.
Sampling respects row padding and normal or reversed decoding without expanding
the whole mask to one byte per source pixel.
Stencil painting also reads packed bits directly with the existing sample grid,
fill color, opacity rounding, and clipping. It does not allocate a temporary
color image or a separate stencil alpha plane.

Complex clip masks use scoped scratch storage. Restoring graphics state returns
the discarded masks; leaving a form or page returns its remaining masks. Saved
and inherited clipping state keeps its storage until that scope ends.

Raster and Flate scratch arrays share a 32 MiB idle byte budget. JPEG 2000 integer
and floating-point frames each have a 16 MiB idle budget. Pools reuse multiple
buffers of the same size and evict the oldest returned arrays when full. This
avoids repeated large allocations without reserving buffers in every size bucket.
Active rentals can exceed these budgets. Oversized arrays are exact-sized and
not retained. No forced collections or page-limit changes are involved.

Parsed streams share immutable document-owned source slices. Keeping a parsed
stream alive therefore also keeps its source storage alive. Public stream
construction and standalone parsing still copy caller-owned payloads. Filter
decoding reads encoded memory directly and returns independent writable output.

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

A Type 3 glyph whose stream filter cannot decode is omitted in compatibility
rendering with a diagnostic. Its declared advance and the empty clipping
contribution are preserved, allowing healthy glyphs and later content to run.
The filter decoder and strict renderer still reject the stream. Output limits
remain enforced; recovery does not enlarge the decode budget or invent a shape.

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

Recovery ignores a `j` operation with a nonnumeric operand, preserving the prior
line join. It also ignores `Td` and `TD` when either position operand is
nonnumeric, preserving both text matrices and text leading. Each case reports a
diagnostic. Strict rendering still rejects these operands; numeric line-join
range checks remain unchanged.

Check `CanReadPageContent` before creating a renderer. Password-encrypted pages
require authentication. Standard Security documents whose default strings and
streams are unencrypted can expose pages while encrypted attachments remain
protected. In that case `CanReadPageContent` can be true while `IsDecrypted` is
false. Page access does not grant attachment access, authenticated permissions,
or permission to rewrite the document.

## Diagnostics and failures

Extended graphics states apply stroke width, cap, join, miter limit, and dash
pattern, including dash phase. Saved graphics states restore these settings.
Strict rendering rejects invalid stroke settings. Compatibility recovery ignores
invalid settings individually and reports diagnostics; a finite miter limit below
one is clamped to one. Other valid settings in the same dictionary still apply.

Negative dash phases use the PDF 2.0 cycle rule in both strict and recovery modes.
For example, `[10 5 60 50] -20` paints the same pattern as phase 230, rather than
phase 20. Older renderers can differ on this case; a difference from their output
does not establish an engine defect. See the
[PDF Association explanation](https://pdfa.org/why-pdf-2-0-is-the-new-pdf-bible/).

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

## JPEG decoding

The managed JPEG decoder supports full-size decoding and reduction factors of
two, four, and eight. Reduced output retains the existing transform samples and
rounding. Reused scale and quantization products reduce transform arithmetic;
the one-eighth path evaluates its single output sample directly. These paths
preserve decoded samples rather than lowering image quality for speed.

Baseline interleaved scans and grayscale scans combine one block row at a time,
so component sample scratch storage scales with image width instead of height.
Progressive output also uses one block row of samples, while retaining the
coefficients needed across scans. Color conversion and transform arithmetic are
unchanged. Noninterleaved baseline color scans retain their existing path.

On supported CPUs, SIMD calculates neighboring output samples together while
retaining the coefficient accumulation order within each sample. Blocks smaller
than the hardware vector width and CPUs without SIMD use the scalar path. The
decoder uses stack scratch space and a small shared cosine table for this path.

Decoder timing depends on image content and reduction. Focused exact-output
checks do not establish performance or visual parity for every JPEG.

## Repeated axial shading samples

Axial gradients whose transformed input is constant across a row or column reuse
the color for exactly matching input bits. Column storage is bounded by the
painted width; row reuse needs one entry. This avoids repeating expensive tint
functions without quantizing gradients. Clipping, opacity, masks, blending, and
knockout still apply separately to every pixel. Other gradient directions keep
the existing evaluation path.

## Translucent painting over opaque pixels

Large translucent fills and strokes can reuse a 1 KiB table for normal blending
over opaque destination pixels. The existing compositor calculates every table
entry, preserving its rounding. Reuse requires full shape and clip coverage,
with no graphics soft mask or knockout. Edge coverage, transparent destinations,
other blend modes, and smaller shapes keep the ordinary compositor.

## JPEG 2000 decoding and opacity channels

Tile reconstruction reuses integer and floating-point sample arrays. Every rented
array is cleared before decoding so incomplete code blocks cannot reuse samples
from an earlier image. Buffers are returned at tile changes and decoder cleanup,
including failures. Actual rented lengths count toward the 256 MiB temporary
sample limit, along with the input line and contiguous column-output scratch
buffers used for wavelet reconstruction. Exact-sized allocations are used when
pool rounding would exceed the remaining allowance. This limit excludes other
decoder state and buffers retained by the shared array pool, so it is not a
process memory cap.

On supported CPUs, floating-point 9/7 synthesis processes adjacent columns in
SIMD lanes, preserving the scalar filter's arithmetic order and boundary rules.
The optional grouped input and output buffers count toward the same sample
budget. Insufficient headroom, unsupported hardware, integer transforms, and
remainder columns use scalar synthesis. No image-quality setting changes.

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

CCITT run codes use bounded prefix lookups when enough input remains. The
original bit reader handles short tails and invalid prefixes, preserving
truncation and invalid-code behavior. Black runs fill complete bytes between
partial boundary bytes while retaining padding and either bit polarity.

## Validation and remaining work

Single rectangular polygons whose edges land on whole pixels after the existing
subpixel rounding use compact bounds instead of a coverage array. This avoids
repeated full-page mask allocation for common rectangular clips. Fractional
edges, multiple polygons, and shapes requiring geometric clipping keep the
scanline rasterizer. Fill rules and anti-aliasing behavior are unchanged.
Intersecting an existing coverage mask with a fully containing rectangle reuses
the mask. A rectangular crop copies only the retained rows. Dense intersections
use row offsets while preserving the same rounded coverage multiplication;
repeated antialiased clipping still multiplies coverage. Dense intersections use
SIMD byte batches with exact rounded division by 255. Short tails and machines
without hardware vectors keep scalar arithmetic; all 65,536 coverage pairs are
checked at aligned and unaligned batch boundaries.

The [results summary](../../validation/PERFORMANCE.md) records measured coverage,
build identity, and known limitations. Compare identical inputs, pages, output
dimensions, and appearance settings. Track render time, total wall time, cold
startup, and memory separately. Fresh-process measurements include JIT costs;
concurrent builds can invalidate performance comparisons.

Coverage beyond the measured corpus, color conversion, fine image detail, and
unsupported content remain active development work. OCR accuracy requires its
own held-out validation; page-render success is not an OCR quality gate.
