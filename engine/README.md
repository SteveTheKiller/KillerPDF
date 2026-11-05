# The KillerPDF.Engine

The KillerPDF.Engine is an independent, UI-free .NET library for reading, rendering, searching, validating, authoring, editing, redacting, signing, encrypting, and writing PDF files. It gives KillerPDF a modern PDF 2.0, PDF/A, and PDF/UA foundation while exposing a public API designed for use in other applications.

“The KillerPDF.Engine” is the formal display name. Ordinary prose may use “KillerPDF.Engine” or “the engine” when it reads more naturally. The assembly, package identifier, and C# namespaces use `KillerPdf.Engine`.

## Five-minute start

The KillerPDF.Engine targets .NET 10. [Get KillerPdf.Engine from NuGet.org](https://www.nuget.org/packages/KillerPdf.Engine), or install it with:

```powershell
dotnet add package KillerPdf.Engine
```

When working directly from a KillerPDF repository checkout, use a project reference instead:

```xml
<ProjectReference Include="path\to\KillerPDF\engine\KillerPdf.Engine\KillerPdf.Engine.csproj" />
```

Create a PDF 2.0 document:

```csharp
using KillerPdf.Engine.Authoring;

byte[] pdf = new PdfDocumentBuilder()
    .SetMetadata(new PdfDocumentMetadata
    {
        Title = "Hello from The KillerPDF.Engine",
        Author = "Example application",
        Language = "en-US"
    })
    .AddBlankPage(612, 792)
    .Build();

File.WriteAllBytes("hello.pdf", pdf);
```

Open and deterministically rewrite an existing document:

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;

byte[] source = File.ReadAllBytes("input.pdf");
PdfDocument document = PdfDocument.Open(source);
byte[] rewritten = PdfDocumentWriter.Write(document);

File.WriteAllBytes("output.pdf", rewritten);
```

Extract text and image placements from a page:

```csharp
using KillerPdf.Engine.Documents;

PdfDocument document = PdfDocument.Open(File.ReadAllBytes("input.pdf"), string.Empty);
var reader = new PdfPageContentReader(document);
PdfPageContent page = reader.Read(0);
foreach (PdfExtractedWord word in page.Words)
    Console.WriteLine($"{word.Text}: {word.BoundingBox}");
```

Page indices are zero-based. Bounds use unrotated, crop-relative PDF points with a
bottom-left origin. Letters include the font name, raw font size, effective point size,
and baseline endpoints. `Diagnostics` reports compatibility recoveries. Text extraction
does not certify visual visibility or PDF conformance; image clipping uses bounding rectangles.

## Major capabilities

### Read and understand real PDF files

The KillerPDF.Engine parses the PDF file itself, including headers, tokens, objects, streams, classic cross-reference tables, cross-reference streams, object streams, trailers, incremental revisions, page trees, name trees, and number trees. Objects are resolved lazily, malformed structures are bounded, and diagnostics retain useful file offsets.

### Preserve or deliberately rewrite

Existing files can be changed through byte-preserving incremental updates or rewritten deterministically. Incremental editing retains the original byte prefix, which is essential for signatures, auditability, and preservation-sensitive workflows. Deterministic rewrites make output reproducible and regression testing practical.

Application save pipelines can also repair harmless serialization artifacts without rebuilding a document. The save sanitizer removes empty outline roots and invalid direct crop boxes through one bounded incremental revision while preserving valid files byte for byte.

### Author complete documents

The authoring model covers pages, content streams, graphics state, paths, text, fonts, images, color spaces, shadings, tiling patterns, transparency, resources, metadata, navigation, optional content, attachments, and viewer behavior. The API uses typed PDF concepts instead of exposing raw application state.

`DrawGlyphCluster(font, fontSize, logicalText, glyphs)` paints explicitly selected
embedded-font glyphs while recording logical text once through inline `ActualText`.
Each `PdfPositionedGlyph` supplies a font-owned glyph ID and baseline origin in current
user space before the CTM. Call outside a text object and emit clusters in logical order;
visual coordinates may differ. The operation preserves surrounding graphics/text state,
embedding rights and substituted/composite glyphs. It accepts 1..4096 nonzero glyphs and
1..65536 well-formed UTF-16 code units, validating requests before changing output.
Single-glyph clusters also retain their full logical string in ToUnicode, with codes
keyed by glyph and text. Multi-glyph clusters leave individual glyph codes unmapped;
readers must honor their `ActualText` span to recover logical text without duplicates.
No empty or invented ToUnicode destinations are emitted. This is caller-supplied transport, not a shaping engine,
per-character caret policy or completed native EPUB shaping integration.

KillerPDF uses these typed content APIs for annotations, stamps and print/export burn-in. Text boxes can save as standard FreeText annotations with logical contents and caller-authored appearances. `PdfIncrementalAnnotationEditor.AddFreeText` and `AddStamp` import the appearance's fonts, images and nested forms, preserve insertion order above existing annotations, and add logical descriptions when the source is tagged. Caller-authored annotations with empty contents and the explicit `Hidden` flag remain outside tagged reading order and are neither displayed nor printed. Other visibility flags do not waive required descriptions. `PdfCommentReader.ReadPageAnnotationCount` reports raw array length, including entries omitted by comment extraction.

`PdfEditablePageOverlayEditor.Append` keeps opaque editing data with a dedicated page-content overlay, preserving its ordinary rendering and searchable text. `Read` returns intact tail batches in content order; `Remove` revalidates them and removes only their drawing, isolation wrappers and owned tagged descriptions. Current foreign form values, markup and other page data remain intact. Changed resources, malformed ownership data, a foreign content tail or a changed page frame prevent extraction. Editing payloads are limited to 8 MiB per batch and resource fingerprints to 32 MiB, 16,384 visits and depth 64. The optional `expectedRotation` supports saves that apply final page rotation later. Authentication, copying/modification permissions and certification restrictions still apply.

### Edit document structure

Pages can be inserted, imported, removed, reordered, rotated, cropped, resized, trimmed, and assigned page boxes, labels, transitions, thumbnails, annotations, form widgets, and structure relationships. Object graphs and dependent resources are imported with collision handling rather than copied as isolated dictionaries.

### Build and edit interactive PDFs

The KillerPDF.Engine supports bookmarks, destinations, links, attachments, visual and editorial annotations, replies, popups, redactions, optional-content groups, and AcroForm fields. Its bookmark reader exposes the complete hierarchy with decoded titles, stable object identity, presentation state, and resolved local or named destinations without leaking parser objects. Its native link reader exposes normalized page geometry, annotation indices, decoded URI actions, and resolved direct or named page targets. Its form-widget reader exposes inherited hierarchical field state, interactive values and options, button states, crop-aware geometry, and rotation. Text fields, checkboxes, radio buttons, combo boxes, list boxes, push buttons, and signature fields have typed authoring and incremental-editing APIs.

### Handle standards and accessibility

The engine includes PDF/A-4, PDF/A-4e, PDF/A-4f, tagged PDF, and PDF/UA-2 authoring safeguards. Structure trees, parent trees, semantic roles, alternate descriptions, output intents, embedded fonts, metadata, annotations, forms, and associated files are validated as coordinated document features. Removing untagged form widgets preserves the page's existing content tags; removing tagged widgets updates their structure and parent-tree mappings.

### Protect and sign documents

Password security covers RC4, AES-128, AES-256, crypt filters, permission flags, authenticated imports, incremental updates, and rewrites. AES-128 and AES-256 certificate-recipient encryption and revision 7 AES-GCM password encryption are also supported, and `PdfDocumentEncryptionWriter` rewrites an existing document with new protection. Digital-signature support includes detached CMS signing, signature fields, certification permissions, field locks, seed constraints, RFC 3161 signature and document timestamps, signature discovery, cryptographic verification, trust, revocation, and PAdES validation evidence, and signed-revision analysis.

### Redact, review, and produce

Redaction removes covered text, images, drawings, annotations, fields, and alternate text at the content level, replaces only approved unsafe pages with images, and verifies the result by reading it back. Sensitive-data searches propose candidates for review. Other review and production workflows cover comments, attachments, layers, bookmarks and links, header and footer and Bates numbering, data merge, imposition, calibrated measurement, structural comparison, and print-production reports.

### Forms, XFA, and export

AcroForm reading, editing, FDF and XFDF interchange, and flattening are joined by safe XFA inspection, data editing, calculation, validation, and conversion to AcroForm. Documents can be exported to plain text, HTML, Markdown, JSON, Word, Excel, and PowerPoint formats with loss reports, and made searchable with the engine's OCR.

### Repair and optimize

Inspectable repair and lossless optimization plans preview exact removals and compression, then verify the saved result. Bounded recovery handles damaged cross-references, page trees, streams, and fonts, and strict parsing remains the default.

### Validate before trusting output

Structural diagnostics, bounded parsing, explicit implementation limits, round-trip validation, and fail-closed graph imports prevent ambiguous or unsafe input from being silently rewritten. Generated conformance fixtures are checked with independent tools rather than accepted because the header contains a particular version number.

## Capability summary

- PDF syntax, objects, streams, classic cross-reference tables, cross-reference streams, object streams, trailers, and incremental revisions
- Page text, word and glyph geometry, font information, and image placements, including nested forms and inline images
- Bounded BGRA32 rendering with crop, rotation, nested clipping, both PDF fill rules, graphics-state opacity, complete device-color paths, recursive Form XObjects, image XObjects, inline and stencil images, and selectable annotation and form appearances
- Direct engine-rendered OCR with bounded preprocessing, layout analysis, versioned recognition models, CPU glyph inference, and PDF-coordinate results
- Deterministic full rewrites and byte-preserving incremental updates
- PDF 2.0 document authoring with pages, content streams, graphics state, fonts, images, color spaces, shadings, patterns, transparency, and resources
- Navigation, bookmarks, named destinations, page labels, viewer preferences, transitions, optional content, and attachments
- Visual annotations, text markup, links, replies, popups, redactions, file attachments, and annotation editing
- AcroForm creation and editing for text fields, checkboxes, radio buttons, choice fields, push buttons, and signature fields
- Tagged PDF and PDF/UA-2 structure authoring and editing
- PDF/A-4, PDF/A-4e, and PDF/A-4f authoring safeguards
- RC4, AES-128, AES-256, and AES-GCM password security, certificate-recipient encryption, crypt filters, authenticated imports, incremental updates, and rewrites
- Detached CMS signatures, certification permissions, field locks, seed constraints, RFC 3161 timestamps, signature discovery, cryptographic verification, trust and revocation policy, PAdES validation evidence, and signed-revision analysis
- Content-level redaction with sensitive-data search, review selections, and read-back verification
- Text, word, table, and layout extraction, and search across annotations and form values
- Form interchange (FDF, XFDF), XFA inspection and conversion, field detection, data merge, and flattening
- Export to plain text, HTML, Markdown, JSON, Word, Excel, and PowerPoint formats with loss reports
- Inspectable repair and lossless optimization plans, and bounded recovery of damaged files
- Preflight, accessibility, and print-production reports with previewed corrections
- Imposition, headers, footers, Bates numbering, calibrated measurement, layers, attachments, bookmarks and links, macros, and structural comparison
- Structural diagnostics, bounded parsing, implementation limits, round-trip validation, and fail-closed import validation

## Native DjVu text (1.9.3 development)

`DjvuDocument.ReadText` preserves page text and embedded separators without changing
the original. Optional zone labels remain in the documented 1..7 range, including
reference-readable legacy trees with repeated or decreasing labels. Validation uses
an explicit 64-level structural limit, at most 65,536 complete records, resolved
global text ranges and nonempty rectangles. Parent and previous-sibling offsets use
the format's signed relative coordinates. Reading is limited to 4 MiB of text and
supports cancellation. Compressed text permits virtual final arithmetic bits within
checked decision and input budgets derived from the decoded byte and block limits.
BZZ headers and speed decisions use ordinary unmodeled arithmetic, independently of
IW44's image-specific operation, including across compressed block boundaries.
`ReadTextPage` exposes immutable encoded-order zones, structural parent indexes,
UTF16 source ranges and unrotated bottom-left pixel rectangles. Encoded separators
remain part of their original ranges. UTF8 endpoints inside a scalar are rejected.
Mapping uses at most 16,777,220 bytes of integer elements plus bounded zone records;
it is omitted when there are no zones. Application selection/search integration,
broader BZZ coverage and mixed visual layers still need acceptance.
Text extraction does not prove visual fidelity.

## Native text snapshots (1.9.3 development)

`PdfReadSnapshot.ReadTextPage` exposes original page text and immutable regions with
page-local UTF16 ranges and unrotated bottom-left bounds in PDF points. EPUB uses its
existing shaped placements; DjVu uses the finest encoded zones and recorded DPI.
Intrinsic rotation remains in `GetPageGeometry` for the caller to apply once.
Missing zones do not create artificial character boxes. Separator-only DjVu zones
remain in the source text but do not become selectable page-sized rectangles.
Selection filtering is linear in text plus zones, with at most another 16,777,220
bytes of integer elements plus bounded region storage. Application search, selection
and accessibility integration still require implementation and runtime acceptance.

## Native EPUB layout (1.9.3 development)

`EpubDocument.Open` reads a bounded native package. `CreateLayout` currently supports
paragraphs and nested spans in linear, reflowable XHTML spine items with an explicit
fallback font. Text supports printable ASCII, precomposed Latin/Greek/Cyrillic blocks
and supplementary mathematical alphanumeric letters and digits when the font has
their glyphs. Text retains UTF-16 source ranges when scalars share a supported ligature,
including after wrapping, pagination, anchor resolution and explicit PDF conversion.
Mapped punctuation characters in the General Punctuation block and mapped characters
in the Currency Symbols block are also supported. Combining marks, complex scripts,
bidi controls, joiners, variation selectors and other unsupported characters still
require implementation and are diagnosed explicitly.
Missing supported glyphs report their full code point. Chapters follow declared spine order and
begin on fresh pages. It returns immutable pages and logical UTF-16 text
placements in CSS pixels. Whole words wrap at spaces, and paragraphs have one font-size
gap between their line boxes. Wrapped lines and paragraphs continue onto new pages
when the next line box does not fit. Each page exposes its original structural text
slice, `SpineIndex` and `TextStart` offset; placement offsets remain relative to
that chapter's spine text. The text-work limit applies across all chapters.
Explicit `br` elements within supported paragraphs and spans advance lines without
paragraph gaps. Consecutive breaks retain blank line heights through pagination,
and styled text keeps its source offsets. Structural normalization still omits
leading breaks before any text and trims trailing breaks.
Words wider than the content box are diagnosed explicitly.
Packaged, untagged eight-bit RGB/RGBA PNG images with only IHDR/IDAT/IEND chunks can
paint inline at intrinsic CSS-pixel dimensions, aligned to the shared baseline.
Image-only paragraphs retain line boxes and paragraph spacing. Explicit breaks
keep image-only lines and intervening blank lines in source order without
inventing structural text characters. Images do not
invent text offsets or alt-text glyphs, and preceding collapsed spaces keep source
order. PNG alpha composites over the reading canvas before the final sRGB decode.
The layout owns decoded image data and preserves original resource bytes.
Each image is limited to 1,048,576 pixels; retained samples and decoded working
buffers have a combined 32 MiB layout budget, separate from captured package bytes. The decoder itself is bounded but
does not yet poll cancellation within a decode. Other PNG variants and metadata,
JPEG, CSS image dimensions, image wrapping and complete image navigation still
need implementation and acceptance. This is initial image support, not the full format matrix.
`EpubLayoutDocument.ResolveNavigationTarget` maps resolved chapter targets from EPUB 2 NCX
and EPUB 3 navigation to the chapter's first native page and logical margin coordinates.
Targets use exact, case-sensitive package paths and a 4,096-character limit. Empty fragments
refer to the chapter start. Body, paragraph, span and line-break IDs retain structural offsets;
nonempty fragments decode once and resolve their actual paginated logical position. IDs retain
case and Unicode identity. Empty elements use the next text placement or the chapter's final
logical cursor. Duplicate spine targets or anchor IDs are diagnosed as ambiguous, and missing
anchors are rejected. A layout owns at most 100,000 anchors. Desktop chapter and within-page
outline navigation are available in Single mode; broader native workflows still need acceptance.
`RenderPage` produces an owned linear-sRGB float frame through shared glyph rendering,
without creating a PDF. Inline and packaged CSS `font-size` and `line-height` support
positive `px`, `em` and percentage values that fit the content height. Font sizes resolve
against the parent after cascade selection. Relative line heights compute against the
element's font and inherit as lengths; unitless line-height factors recalculate for each
descendant's font. Mixed sizes share a baseline; collapsed spaces retain their original
inline style. Default normal line height follows each inline font's ascent and descent.
Initial Latin ligatures use the font's GSUB 1.0 default-language `liga` feature with
type 4, flags 0, format 1 and coverage format 1. Original UTF-16 cluster ranges and
selected glyph advances drive both native ink and explicit PDF conversion. Style,
block and explicit-break boundaries remain separate. Natural text and single-glyph
ligatures keep their Unicode font mappings as well as logical replacement text. Parsing
and shaping have separate work limits and cancellation. Other GSUB routes, positioning,
Arabic/Indic shaping, bidi processing and per-character carets remain incomplete.
Foreground `color` accepts six-digit sRGB hex values and the 16 basic CSS color
keywords, case-insensitively. It inherits and follows property-specific cascade
precedence. Individual glyphs retain their foreground color through rendering.
Body `background-color` uses the same color syntax, also accepts `transparent`, and paints the whole reading
canvas, including margins, at natural and requested pixel sizes. Backgrounds do
not inherit; backgrounds on other elements are explicitly diagnosed until box
painting is implemented. Explicit transparent body declarations clear earlier
opaque backgrounds and expose the white reading canvas, including inline overrides.
Packaged rules support grouped type selectors and compound classes such as
`p, span.small.keep`, with class specificity and declaration order applied separately
to each property. Inline declarations override packaged rules. Each chapter limits
selector candidate and class comparison work to 1,000,000 units, including compared
class-name characters, and diagnoses excessive work before returning pages.
Other styling, character breaking, complex scripts and other unsupported content
produce explicit diagnoses; this initial API does not establish full EPUB support.
Packaged head links with `rel="icon"` are validated as local references without
decoding or painting the icon in body pages. Packaged UTF-8 CSS stylesheets accept
the canonical `@charset "utf-8";` header and support ASCII type selectors, source order,
inheritance and inline overrides. Loading stays
inside the package, with shared limits of 1,000,000 stylesheet bytes and 1024 references
per layout, including repeated empty resources. Other CSS syntax remains diagnosed.
Head metadata after the body is rejected as malformed before layout returns.

`ReadText` and the supported plain native layout accept the canonical XHTML 1.1
external declaration without loading or processing a DTD. Original resource bytes
stay unchanged. Internal subsets and unknown declarations remain rejected.

```csharp
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;

var book = EpubDocument.Open(File.ReadAllBytes("book.epub"));
var font = TrueTypeFont.Load(File.ReadAllBytes("fallback.ttf"));
var layout = book.CreateLayout(new EpubLayoutOptions(800, 1000, font));
PdfRenderedFrame frame = layout.RenderPage(0);
```

Options bound page dimensions, margins and typography. Combined working and returned
pixel buffers are limited to 128 MiB, and layout limits source text and placement work.
Cancellation is supported during layout and rendering. Logical text geometry remains
separate from glyph ink, which can extend beyond a font's logical line box.

`book.ConvertToPdf(new EpubPdfConversionOptions(font))` explicitly creates an
independent PDF with A4 paper and 15 mm margins. Paper geometry and fallback font
size are configurable in points. Initial conversion retains selectable embedded
text, resolved CSS text colors, RGB/RGBA PNG images and solid canvas backgrounds
from the supported native layout. PNG transparency remains a PDF soft mask. It leaves the native package and existing read
snapshots unchanged. Native EPUB/DjVu sessions expose an explicit Document tools
conversion action. It publishes a separate new PDF atomically, opens an independent
PDF tab and preserves the original native tab. EPUB offers paper and margin options,
initially A4 with 15 mm margins. Existing destinations are not overwritten, and
unsupported content is diagnosed before any PDF is published. Full format and
workflow acceptance remains in progress.

`DjvuDocument.ConvertToPdf()` initially supports text-free, navigation-free bitonal
Smmr pages. It embeds lossless one-bit images at the page's recorded DPI, preserves
intrinsic orientation and bundled page order, and leaves the source and native
rendering unchanged. It rejects text and navigation rather than dropping them.
Other native decoder diagnoses propagate; grayscale/color conversion and complete
text/navigation preservation remain unfinished.

The EPUB `RenderPage(pageIndex, renderOptions)` overload rasterizes the original
glyph outlines at the requested pixel dimensions without changing pagination or
text placement. It checks the caller's combined float-buffer budget before
allocating pixels. Native separation plates are unsupported. The DjVu overload
resizes opaque linear frames with bilinear enlargement and area reduction,
preserving intrinsic orientation. Combined source/output pixel storage and
weighted sampling work are checked before decoding.

`PdfReadSnapshot.OpenEpub(bytes, options)` and `OpenDjvu(bytes)` capture source
identity and native read state together, expose explicit page units and physical
point dimensions, and render the same float frames. EPUB uses CSS pixels at 96 DPI;
DjVu retains each page's recorded resolution. Geometry describes the page before
intrinsic rotation, which `RenderPage` already applies. `PdfLogicalDocument` gives
each independent
open a fresh identity and retains an explicitly supplied snapshot. Equal source
hashes do not share logical identity. Native decoder limits still apply; desktop
opening and the PDF read adapter remain in development.

`PdfReadSnapshot.ReadNavigation` returns captured, read-only outline trees for EPUB and DjVu.
Invalid or unsupported outline metadata is reported when requested without preventing page reading.
`ResolveNavigationTarget` returns a common page index and top-left position in the already oriented
native page's units. It uses the captured EPUB layout for chapter and anchor targets. DjVu matches exact component
identifiers before ordinal page numbers, decodes percent escapes once with strict UTF-8, and supports
signed page displacements when a current page is supplied. Viewer commands and external resources
are not executed or fetched. DjVu title aliases remain unfinished.

`new PdfLogicalDocument()` creates logical identity without retaining reading
resources; its optional `ReadSnapshot` is null. Supplying a native snapshot to
the other constructor still retains that snapshot explicitly.

Each logical owner has immutable requested `Proof` preferences, initially disabled,
and a `ProofGeneration` that changes only when the requested selections change.
Profile content identity avoids invalidation when identical bytes are reloaded.
`CaptureProof()` returns matching immutable settings and generation for a render request.
`CreateIndependentCopy()` retains the snapshot and proof preferences with a new
identity and an independent generation. These portable ownership contracts do not
yet connect desktop proof controls, rendering or persistence.

## Bundled CMYK viewing assumption (1.9.3 development)

`PdfCmykViewingDefault.LoadProfile()` loads an owned copy of the bundled CC0
CGATS001Compat-v2-micro profile without accessing a file or network resource.
It represents CGATS TR 001-1995, an older North American web-printing condition,
and supports perceptual forward conversion only. Use its `EffectiveIntent` for
that conversion and identify the assumed printing condition to the reader.

`PdfColorProfile.GetTransformSelection` reports the requested intent, direction,
actual table signature, table intent, fallback and media-white scaling separately.
For this profile, a relative-colorimetric forward request selects perceptual
`A2B0` and reports fallback; reverse conversion is unavailable. This diagnostic
API reports the existing conversion policy without changing it. Matrix/TRC
transforms have no table intent, and absolute media-white scaling is reported
separately from the colorimetric table it uses.

`ProfileClassSignature`, `DeviceColorSpaceSignature`, `ConnectionColorSpaceSignature`
and `MajorVersion` expose declared ICC metadata independently of transform selection.
The bundled viewing profile reports input class `scnr`, device `CMYK`, connection
`Lab ` and version 2. These declarations do not establish printer suitability.

```csharp
using KillerPdf.Engine.ColorManagement;

PdfColorProfile assumedCmyk = PdfCmykViewingDefault.LoadProfile();
```

`RenderFrame` applies this assumption to untagged CMYK path fills and strokes set by
`k`/`K` or `/DeviceCMYK cs`/`CS` when no resource default supplies a profile.
It uses the profile's perceptual forward table and retains out-of-gamut values in
the linear frame. Resource defaults and embedded profiles remain authoritative.
Untagged 8/16-bit CMYK images use the same assumption at aligned natural size
or axis-aligned nearest or bilinear enlargement and integer-factor reduction in both axes.
Interpolation and reduction retain fractional source samples before profile conversion.
Matching 8/16-bit DeviceGray soft masks retain fractional alpha with natural or nearest sampling.
Their finite device-range Matte colors are removed before the CMYK profile transform.
Raw color-key masks also support natural or eligible nearest sampling.
CMYK indexed images, other reductions,
gradients, other group semantics and compatibility byte rendering still need broader adoption.
Pages with OutputIntents remain outside the current float path.
Callers can also apply the source assumption explicitly.
Printer proofing uses its selected printer/paper profile. The package includes the unmodified profile and its complete
CC0 license in `color_profiles/Resources/ColorProfiles`.

`PdfPrinterGamutCheck` evaluates intended relative D50 XYZ through an actual
relative-colorimetric printer inverse/forward pair, without compensation, paper simulation
or display clipping. It returns a separate CIE1976 Lab difference and classification.
The application warning threshold is 2 with a closed boundary band from 1.95 through 2.05.
These are profile-based warning estimates, separate from monitor limits and physical measurements.
The [CIE Lab definition](https://cie.co.at/eilvterm/17-23-076) and
[color-difference definition](https://cie.co.at/eilvterm/17-23-077) define the calculation;
the warning threshold is application policy. Intended color remains unchanged.
Supported RGB page-group proof frames expose an immutable `PrinterGamutWarnings` mask
when requested. Classification uses intended color before compensation and proof conversion.
Frame-local coordinates, printer-profile identity and warning/boundary counts stay separate
from color pixels and byte/half output. Rendering counts both mask planes in its storage limit;
half output counts the retained mask. Fully transparent pixels are `NotEvaluated` and
do not contribute warnings or boundary counts. Desktop warning overlays still require integration.

## Rendering status

DjVu raw arithmetic decoding includes equality in the most-probable interval.
A frozen native-reference column exercises this boundary through public page rendering.
This repair does not resolve the separate patterned IW44 reconstruction discrepancy;
the original full-image regression and independent reference disagreement remain open.

The engine owns bounded CPU rendering for paths, text, images, clipping, shadings, transparency,
annotations, and forms. It returns platform-neutral BGRA32 pixel buffers and diagnostics without
referencing a UI toolkit or native PDF renderer. The engine does not provide UI controls.

Compatibility documents can render when empty optional-content metadata omits `/OCGs`.
Strict layer parsing still rejects a missing required group list; present malformed lists remain errors.

`PdfRenderedFrame.ToDisplayBgra32` converts encoded or linear sRGB frames through D50 XYZ
to an RGB monitor profile using relative colorimetric intent. It retains straight alpha
and clips only at the final device-byte boundary. Use these monitor device bytes only
when the application owns final monitor conversion, not with compositor-owned conversion.
The combined storage limit counts input floats, output bytes and a retained gamut mask;
cancellation and unsupported profile types are diagnosed. This portable conversion does
not select an operating-system profile or establish desktop integration or physical accuracy.

The 1.9.3 development `RenderFrame` API also retains floating-point source colors for its
supported path, text, gradient and image cases, including ordinary Form XObjects with
their matrices and bounding-box clips. It rejects unsupported precision features
with a reason instead of returning colors quantized through the BGRA32 surface.
Text widgets excluded by render options do not disable precise page rendering.
Explicit neutral graphics-state defaults retain precise painting.
Included annotation and form-field appearances remain explicitly unsupported.
Eligible isolated and non-isolated, non-knockout DeviceRGB Form groups retain floating-point
colors and alpha through outer Normal, Multiply or Screen compositing and apply invoking opacity once.
Non-isolated groups track source alpha separately when their outer blend or parent requires it.
Internal Normal, Multiply and Screen paints retain backdrop alpha and color. They require an unprofiled
effective blend space and rectangular clips without fractional coverage. Their exact pixel
arrays share the render budget across active nested groups, including four extra bytes per pixel
for each required source-alpha plane. Unprofiled, non-knockout DeviceRGB page groups retain
precision through final Normal imposition on white for opaque output. Transparent output
keeps the page group's alpha, and direct page output ignores its isolation flag.
Other blend spaces, knockout, fractional group clips and other group blend modes remain unsupported.
Optional `PdfRenderOptions.Proof` initially supports pages without explicit groups and unprofiled DeviceRGB page groups
with direct RGB/Gray paths, all four intents, optional source BPC and independent paper/black simulation,
and separate gamut-warning masks. Paper simulation requires reliable explicit media white and uses absolute colorimetric
appearance. Paper-only viewing preserves paper tint while mapping estimated printer black to reference zero;
it is a viewing aid rather than an ICC intent. Compensation uses bounded ICC estimates against
mathematical zero-black RGB reference, with unavailable or degenerate conditions diagnosed.
Supported calibrated `DefaultGray` resources are applied to fill and stroke colors before proof conversion.
Basic embedded TrueType text retains its color through proof conversion after glyph painting.
Text matrices, strings and positioned arrays support filled and stroked glyphs.
Proof rendering also preserves character and word spacing, horizontal scale, text rise,
line positioning, explicit or move-defined leading, and both quote text-showing operators.
Direct DeviceRGB/Gray and supported RGB/Gray/CMYK ICC image XObjects use the existing precise image path
before proof conversion, retaining supported source sample depths, Decode mapping and alpha.
Embedded profiles and resource defaults are applied before the printer transform; extended reference
RGB is not clipped at that boundary. DeviceCMYK proof images require a supported four-channel
ICCBased DefaultCMYK resource; missing, invalid and non-CMYK defaults are diagnosed.
Direct ICC image profiles take precedence over device defaults. Ordinary Form XObjects preserve supported paints through
composed matrices, bounding-box clips and inherited alpha before final proof conversion.
Explicit Form groups, other XObject types and unsupported source color spaces remain diagnosed
pending source-aware integration.
The selected output profile must have actual colorimetric transforms in both directions.
The requested inverse must be available without intent fallback. Absolute source conversion
also requires explicit valid media white; source BPC is an identity for that intent.
Conversion occurs after group compositing through D50 XYZ, retaining extended reference RGB.
Transparent output converts straight color while retaining alpha and unpainted hidden RGB;
premultiplication occurs only when requested by the output format.
Other requests are diagnosed. Byte rendering rejects enabled proof.
Native EPUB layout and EPUB/DjVu snapshot `RenderPage(options)` also apply supported
proof settings to their normalized linear-sRGB frames, preserving the source snapshot.
Paper/black simulation, BPC, intent and separate gamut-mask policy match the PDF proof
path. Combined storage limits count original, mutable and returned frame buffers,
temporary and owned masks, and any retained natural-size DjVu frame during resizing.
Missing or unusable printer transforms are diagnosed; disabled proof preserves normal
output. Native runtime fixtures currently cover gray output profiles and opaque frames.
Original CMYK ink preservation, broader profile/native format acceptance and desktop
proof controls still require the full source-aware pipeline.
`RenderFrameRegion` returns an owned float32 crop on the full page's pixel grid. Its
combined pixel-storage limit counts only the requested region, and precision validation,
diagnostics and cancellation match `RenderFrame`. Desktop refinement adoption remains in progress.
The supplied `PdfRenderOptions` still validate the full page's byte-raster dimensions and limit.
Supported RGB/Gray matrix/TRC image profiles can also replace DeviceRGB/Gray through
resource `DefaultRGB` and `DefaultGray`, preserving their source precision and extended RGB.
Natural aligned CMYK images and eligible nearest enlargements retain 8/16-bit precision through supported colorimetric
ICC source profiles, including a resource `DefaultCMYK` replacement for DeviceCMYK.
Interpolated or reduced CMYK images, masks and unsupported profile directions remain diagnosed separately.

`PdfDisplayPresentationPolicy.Resolve` interprets Windows display observations without
accessing the operating system. Conventional SDR requires application monitor conversion;
Advanced Color uses compositor conversion. HDR scales document white from the reported
SDR-white setting, while Advanced Color SDR uses display-relative white at one. Missing
HDR white data and ambiguous outputs leave the policy unavailable. A decision does not
load a monitor profile, initialize a transport or establish physical accuracy. Desktop
viewer integration remains in progress.

See the [rendering guide](docs/rendering.md) for integration, pixel layout, authentication,
diagnostics, resource limits, and validation guidance.

See [opening and extracting documents](docs/reading.md) for memory ownership,
authentication, permissions, page coordinates, extraction results, and batch failures.

See [authoring documents](docs/authoring.md) for typed page content, fonts, images,
interactive objects, resource ownership, and output validation.

See [encryption and signature integration](docs/security.md) for protected
authoring, authenticated rewrites, permission checks, and signature trust policy.

See [editing and saving documents](docs/editing.md) for page edits, typed overlays,
imports, incremental preservation, and output validation.

See [repair and optimization](docs/optimization.md) for inspectable plans,
compression, selective removal, measured results, and preservation limits.

See [preflight and conformance checks](docs/conformance.md) for profiles,
report interpretation, supported boundaries, and previewed corrections.

See [OCR integration and review](docs/ocr.md) for models, provider selection,
coordinates, correction, searchable output, and held-out validation.
New pixel-space searchable text fits known TrueType word ink heights to recognized
boxes, preserving the line placement of short words. Fonts without usable glyph
bounds retain the em-based placement fallback.

See [AcroForm values and interchange](docs/forms.md) for widget reading,
XFDF export, reviewed imports, incremental saves, and flattening boundaries.

See [XFA inspection, data, and conversion](docs/xfa.md) for packets, repeated
values, restricted behavior evaluation, layout plans, and conversion limits.

See [bookmarks, links, and opening destinations](docs/navigation.md) for navigation
authoring, readers, host behavior, and initial-view edits.

See [PDF layers and optional content](docs/layers.md) for layer authoring,
saved visibility, content assignment, configuration metadata, and flattening limits.

See [attachments and PDF portfolios](docs/attachments.md) for embedded files,
page icons, extraction, replacement, comparison, and collection metadata.

See [fonts, text codes, and outlines](docs/fonts.md) for resource inspection,
standard-font coverage, host resolvers, embedding, and glyph geometry.

See [annotations and review comments](docs/annotations.md) for threaded notes,
source identities, exports, incremental edits, and appearance boundaries.

See [accessibility inspection and tagged content](docs/accessibility.md) for
logical reading order, reviewed semantic proposals, and targeted repairs.

See [structured text and Office export](docs/exporting.md) for output formats,
representation losses, font maps, reviewed OCR, and batch outcomes.

See [reusable macros and batch execution](docs/macros.md) for typed steps,
host callbacks, previews, cancellation, contextual values, and resuming runs.

See [imposition plans and sheet PDFs](docs/imposition.md) for N-up, booklet,
manual ordering, poster tiles, marks, and output boundaries.

See [headers, footers, and Bates numbering](docs/page-furniture.md) for templates,
placement review, recognized marks, replacement, and continuous numbering.

See [data merge and template generation](docs/data-merge.md) for record mapping,
previews, text and image targets, batch outcomes, and combined output.

See [structural document comparison](docs/comparison.md) for interpreted page
differences, report categories, cancellation, and visual-equivalence limits.

See [permanent redaction](docs/redaction.md) for candidate review, content-level
removal, approved image replacement, comment and attachment removal, and verification limits.

See [calibrated measurement](docs/measurement.md) for profiles, geometry,
snapping, editable dimension annotations, reports, and trust boundaries.

See [separation inspection and preview planning](docs/separations.md) for used
plate inventory, validated page selections, rendering boundaries, and failures.

See [print-production reports](docs/print-production.md) for effective page
boxes, colorants, output intents, preflight, and approval boundaries.

See [RFC 3161 timestamp integration](docs/timestamping.md) for signature
timestamps, document-timestamp verification, host duties, and writer limits.

## Repository layout

```text
engine/
  KillerPdf.Engine/          Reusable library
  KillerPdf.Engine.Tests/    Unit and regression tests
  KillerPdf.Engine.Corpus/   Corpus gates and standards smoke generators
  docs/                      Integration guides and architecture records
  CHANGELOG.md               Engine-only release history
  README.md                  This developer entry point
```

The KillerPDF.Engine remains in the KillerPDF monorepo so library changes, application integration, tests, and corpus gates can evolve atomically. Its dependency boundary is deliberately independent: the library does not reference WPF, KillerPDF application code, PDFium, PdfPig, PdfSharpCore, or PDFsharp.

## Build and test

From the repository root:

```powershell
dotnet build engine\KillerPdf.Engine\KillerPdf.Engine.csproj -c Release
dotnet test engine\KillerPdf.Engine.Tests\KillerPdf.Engine.Tests.csproj -c Release
```

The project treats compiler warnings as errors and generates XML API documentation during normal builds.

## Release validation

The release gate includes:

- The full engine test suite
- A strict Release build with zero warnings
- A 2,907-file incremental structural corpus gate
- A 2,907-file selected-page import corpus gate with zero unexpected failures
- qpdf structural validation and veraPDF PDF/A-4 and PDF/UA-2 smoke validation for generated fixtures
- OpenSSL verification for real detached CMS signature fixtures

Corpus files are intentionally malformed or nonconforming in many cases. A refusal is expected when the source is structurally unsafe, credential-protected, or depends on unsupported global state. The gate distinguishes those intentional boundaries from unexpected engine failures.

## Design principles

- Preserve existing bytes when an operation can be represented as an incremental revision.
- Fail closed when required structure cannot be interpreted or preserved safely.
- Emit deterministic output so regressions are reproducible.
- Enforce explicit implementation limits before allocating or serializing unbounded structures.
- Keep public APIs typed and reusable instead of exposing KillerPDF application state.
- Treat conformance as validator-backed behavior, not a label inferred from the PDF header.

The original architecture decision is recorded in [ADR-001](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/docs/architecture/ADR-001-pdf-engine-boundary.md).

## KillerPDF integration

KillerPDF directly references The KillerPDF.Engine for document parsing, text extraction, writing, editing, and rendering. The Windows application no longer references PDFium, Docnet.Core, PdfPig, or PdfSharpCore.

See [The KillerPDF.Engine changelog](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/CHANGELOG.md) for detailed capability history.

## License

The KillerPDF.Engine is licensed under GPLv3 as part of the KillerPDF repository. See the repository [LICENSE](https://github.com/SteveTheKiller/KillerPDF/blob/main/LICENSE).

Bundled font metrics and character maps retain their original licenses in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), also included in the NuGet package.
