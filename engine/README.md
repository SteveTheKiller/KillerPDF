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

`PdfPageContentReader.ReadImagePlacements` returns immutable occurrence selections
bound to that exact document instance. `PdfImagePlacementEditor.Remove` removes one
supported top-level image invocation in a detached revision, preserving repeated
uses of the same image resource and unrelated page content. `CanRemove` describes
content eligibility; editing separately checks permissions, certification and
tagged structure. `SetBounds` moves or resizes a complete axis-aligned occurrence
while retaining its orientation, source pixels and shared resource. `CanSetBounds`
and `BoundsUnsupportedReason` describe that narrower geometry capability. Partly
cropped, skewed and quarter-turned occurrences remain refused for bounds editing.
Nested, inline, masked and optional-content images, recovery documents and complex
page state remain explicitly unsupported.

`PdfImagePlacementExtractor.Extract` creates an immutable `PdfImageAsset` from a
complete simple occurrence. This initial subset retains eight-bit DeviceRGB samples
without converting them, including raw or single-filter Flate-encoded images.
Flate samples are validated within the decoded-size budget and against their zlib
checksum while retaining the original encoded bytes. `ToPdfBytes` returns a separate
single-image PDF whose page size matches the original pixel dimensions, containing
the original samples and no neighboring page content or resources. Copies own their
bytes and enforce content-copy permission independently of modification permission.
RGB ICC v2.1 images retain the original profile and image stream bytes, including
single Flate encoding. Extraction captures page rendering intent through `q`/`Q`
and `ri`, with an image's explicit intent taking precedence. The detached image
keeps that intent when reused under a different destination state. Profiles must
provide the captured transform, RGB input, unit ranges and no extra dependencies;
unsupported revisions, alternates and metadata remain diagnosed.
DeviceRGB images can also retain a raw or single-Flate-encoded, same-size eight-bit
DeviceGray soft mask. Its original encoded alpha bytes survive extraction and reuse;
Flate masks validate decoded length and their zlib checksum. Supported RGB ICC
profiles and captured source intent remain intact with these masks. Mask Decode,
Matte, predictors, other filters and nested masks remain refused. Encoded and decoded image
plus mask data share the 5 MiB limit; serialized assets have the same limit.
Other color spaces, default color substitutions, other filters, predictors,
other masks and uncaptured context remain refused.
These backend APIs do not yet provide application image-selection or Copy/Cut tools.

### Author complete documents

The authoring model covers pages, content streams, graphics state, paths, text, fonts, images, color spaces, shadings, tiling patterns, transparency, resources, metadata, navigation, optional content, attachments, and viewer behavior. The API uses typed PDF concepts instead of exposing raw application state.

`PdfImage.FromCalibratedGray` retains original eight-bit one-component samples
with an immutable `PdfCalGrayColorSpace`, including its white point, black point
and gamma. Flate/predictor encoding requires PDF 1.2 across pages and nested image
resources. `FromGray` continues to create DeviceGray images.
Float rendering accepts direct 8/16-bit CalGray images at aligned natural size or
axis-aligned enlargement, with zero black, default Decode and relative colorimetric
intent. Unmasked bilinear enlargement interpolates source samples before calibration.
Raw color-key masks and matching DeviceGray 8/16-bit soft masks retain alpha with
natural or nearest sampling. Named aliases, integer translations, rectangular clips
and diagonal reflections retain precision. Natural-size soft-mask Matte is removed
before calibration without quantizing the source or mask. Reductions, other image geometry,
nonzero black, other Decode arrays, default color substitutions, masked bilinear
sampling and enlarged soft-mask Matte remain diagnosed.
Natural-size images also retain source precision with reversed Decode `[1,0]`;
enlarged images still require default Decode.

Unmasked ordinary DeviceRGB, DeviceGray and supported CalGray images retain fractional axis-aligned
edge coverage at natural size or enlargement. Covered pixels keep straight source
color even when their centers lie just outside the image. Reflection preserves
source orientation. Fractional ICC, mask and reduction cases remain
diagnosed.

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

`PdfContentStreamBuilder.DrawImage(PdfImageAsset, x, y, width, height)` reuses a
detached raw or Flate-encoded eight-bit DeviceRGB or supported RGB ICC image on pages, inside forms,
colored patterns and caller-authored stamp appearances. Resources are shared by
asset identity, retain encoded bytes and interpolation, and exclude the source
page's unrelated content. Negative dimensions flip the image. Raw assets require
PDF 1.0; Flate assets require PDF 1.2, supported ICC assets require PDF 1.3,
and soft-mask assets require PDF 1.4, including inside forms.
Uncolored patterns reject these colored
assets, including through nested forms and patterns. This bounded authoring slice
does not implement application Copy/Cut or additional source color spaces.

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
bytes of integer elements plus bounded region storage. The Windows shell's captured
read lease retains the immutable native snapshot and links text reads to caller and
document closure cancellation without reopening the source path. Application search,
selection and accessibility integration still require implementation and runtime acceptance.

## Native EPUB layout (1.9.3 development)

Initial unordered lists support text items with outside disc markers, 40px left
padding and 1em vertical list margins. Markers follow the first line across page
breaks without repeating on continuation pages. Generated discs add no source text
or offsets; literal bullets, selection and item destinations keep their identity.
Native rendering and reopened PDF output preserve the separate marker paint.
Empty items, nested or ordered lists, unsupported list styles and items without a
supported first line remain diagnosed. Explicit block or inline item display
suppresses generated markers.

Structural `ReadText` preserves spaces, tabs and line breaks inside `pre`, including
trailing whitespace and inline style boundaries. Normal paragraphs still collapse
whitespace. Native block `pre` layout and PDF conversion preserve mapped spaces,
line breaks and source offsets with cascading monospace defaults and 1em vertical
margins. Blank lines, terminal spaces and supported inline styles survive page
continuations and independently reopened output. Nested blocks, preserved tabs,
literal carriage returns, incompatible display/reset modes and oversized unwrapped
lines remain diagnosed.

Semantic `header`, `footer`, `main`, `nav`, `section`, `article` and `aside`
containers retain normal block flow, their CSS identity and supported box styles.
`blockquote` defaults to 1em vertical margins and 40px horizontal margins; nested
quotes collapse adjoining vertical margins and retain cumulative horizontal insets.
Author margins override these defaults. Margin shorthands and individual sides
accept `initial` and `unset` resets. Native layout and PDF conversion share these
rules; unsupported nesting and foreign namespaces remain diagnosed.

Named head metadata, a case-insensitive `charset=utf-8` head declaration and the
legacy `Content-Style-Type=text/css` declaration
remain outside page text and do not execute directives. Other pragmas, unknown
metadata attributes and metadata in body content remain diagnosed. Original
XHTML resources remain unchanged.
Empty `title=""` attributes add no advisory text and remain available to CSS
matching. Advisory titles with text remain diagnosed except for image metadata.

CSS `height:auto` and bounded nonnegative percentages retain content-driven height
on supported nonroot elements. All admitted normal-flow containing heights are
automatic; [CSS percentage height rules](https://www.w3.org/TR/CSS22/visudet.html#the-height-property)
therefore use automatic height. Visible root percentage heights, definite sizes,
floats and positioned boxes remain diagnosed. Adding definite containing heights
requires extending this bounded policy rather than discarding their declarations.

Text anchors in paragraphs and grouping containers preserve canonical package-relative
or absolute HTTP, HTTPS and mailto `LinkTarget` values on native text placements.
External targets are captured without fetching them. Initial `:link` matching uses
XHTML anchors with an unqualified href and class-level specificity. PDF conversion
writes local page destinations or URI annotations and groups contiguous visible text
into line rectangles. Hidden
anchors do not create links. `:visited` is admitted without consulting browsing
history: native reading and conversion treat all links as unvisited. Captured
layouts admit trailing `:hover` on supported simple selectors without applying
hover declarations. Dynamic pointer-driven restyling is not implemented. Nested
anchors, linked images, external resources and other pseudo-classes remain
diagnosed; targets retain the 4,096-character
navigation limit. `PdfReadSnapshot.ReadLinkRegions` retains grouped target geometry
in unrotated bottom-left PDF points and honors capture ownership and cancellation.
The Windows reader has initial mouse and Enter-key link activation across its five
view modes. Supported external targets use the existing link-opening policy after
checking the captured document owner, including the optional confirmation setting.
Full reading, accessibility and color-status acceptance remain
in progress.

CSS `all:inherit` and `all:initial` participate in each supported property's cascade.
Inheritance uses the actual parent's computed values, including computed pixel lengths
for em margins. Initial values reset paragraph gaps, font family, weight, normal line
height and color while retaining the separate reader page inset. Other shorthand values
and unsupported longhand values remain diagnosed.

`background:initial` resets the supported background state to transparent without
resetting text color, typography or box geometry. It participates in the ordinary
background cascade, including declaration order, specificity and inactive selectors.

Initial `@media screen` blocks preserve nested rule scope and stylesheet order.
Both native layout and explicit PDF conversion use these reading styles. Media
blocks are limited to 16 nested levels and bounded scanning; malformed blocks and
other media types or queries remain diagnosed.

Inline `strong` and `b` text select real font faces using the parent's relative
weight, following the [HTML rendering defaults](https://html.spec.whatwg.org/multipage/rendering.html)
and [CSS weight thresholds](https://www.w3.org/TR/css-fonts-4/#font-weight-prop).
Author weight declarations and shorthand resets override these defaults. Nested
emphasis retains computed weights and existing font matching; no synthetic bold
painting is added. The CSS `bolder` and `lighter` longhand values remain diagnosed.

Inline `i` and `em` defaults and `font-style:italic` select real italic faces.
Upright resets and inherited styles compose with weight, including bold italic.
The selected style must match even when the host supplies a nearest weight; missing
italic faces cannot fall back to an upright default. The Windows resolver checks
the captured font's declared style before reporting a match. Oblique styles remain
diagnosed. Native rendering and PDF conversion retain the chosen outlines.

Inline `span` elements and explicit `br` line breaks also work directly inside the
body or supported grouping containers. Leading breaks reserve a line without adding
source text, and native and converted text retain their original offsets and styles.

Block paragraph/container margins accept `auto`. An explicit content width shares
remaining horizontal space between automatic margins, including padding and fixed
opposite margins. Automatic widths use zero automatic margins; vertical automatic
margins are zero. Replaced-image automatic margins remain explicitly diagnosed.

PNG and supported JPEG images support `max-width` in packaged and inline styles, using px, em or
containing-block percentages, with `none` resetting the constraint. Images shrink
proportionally without enlarging or resampling stored pixels. Native rendering and
PDF conversion use the same placement dimensions; zero maximum width omits paint.
The normal specificity cascade applies independently of display mode. Maximum
widths on non-image elements remain explicitly unsupported.

Initial JPEG layout accepts eight-bit Gray/RGB sequential and progressive sources,
with supported JFIF square-pixel and Adobe color metadata. ICC, EXIF/orientation,
thumbnails and additional metadata remain explicitly unsupported. Preflight bounds
source pixels, padded component storage, progressive coefficients and retained
image buffers before decoding. Original resources remain unchanged.

Percentage image widths use the remaining line extent directly, avoiding a rounded
coordinate sum that could refuse an exactly fitting image on the default A4 paper.

Static SVG cover wrappers support one packaged raster image, a zero-origin viewBox,
and the manifest's `svg` resource declaration. Other resource properties retain
strict diagnostics. Supported wrappers use
100% page-body viewport dimensions and centered `xMidYMid meet` scaling on both the
wrapper and image. Body margins and padding constrain the viewport. Block container
parents, inherited hiding, source dimensions and explicit PDF conversion share the
same placement. Active content, external references, other drawing elements,
transforms, custom SVG styles and additional image attributes are diagnosed.
This is a bounded cover subset, not general SVG rendering.

Initial `letter-spacing` accepts nonnegative px/em lengths and `normal`. Lengths
compute on the declaring font before inheritance. Adjacent inline values contribute
half their distance, with no spacing at line edges. Whole-word lookahead includes
tracking; consecutive inline images form one atomic run. Nonzero tracking suppresses
optional Latin ligatures while retaining selected font-provided small caps.
Negative lengths and percentages remain diagnosed. Inline image backgrounds use
the displayed image height, preserving the original raster dimensions for PDF.

Initial `word-spacing` accepts nonnegative px/em lengths and `normal`, computed
before inheritance. It expands collapsed word separators without changing glyph
widths; whole-word wrapping discards the separator at the line edge. It combines
with letter spacing in native layout and PDF conversion. Negative lengths,
percentages and other spacing keywords remain diagnosed.

Initial `text-decoration` supports underline, none and CSS-wide reset keywords.
Ancestor decorations retain their own font metrics and color across descendants;
a child reset does not erase an ancestor's underline. Visibility applies to the
decorated text, including a visible child beneath a hidden decorating ancestor.
Contiguous glyph fragments share one underline strip to avoid fractional seams.
Inline ancestors contribute their font ascent, descent and leading to line sizing,
including ancestors containing only smaller descendant text. Native and reopened
PDF controls cover these rules; other decoration styles remain unsupported.

Native layout omits `display:none` subtrees before font resolution, box creation,
visible page text and PDF text emission. A descendant cannot restore display inside
a suppressed ancestor. Hidden computed font sizes, geometry and body backgrounds
are not applied; syntax and resource checks remain bounded. Structural `ReadText`
continues to return source text without CSS evaluation. `visibility:hidden` retains
layout dimensions while suppressing text, image and box painting. Visible descendants
can override inherited visibility. Logical layout text and offsets remain available;
reader snapshots omit explicit hidden ranges and remap selectable regions, so search,
copy and reading text exclude hidden characters. Navigation retains hidden text positions
without making them selectable. Visible supplementary characters and substituted glyph
clusters retain their complete UTF16 ranges.

`EpubDocument.Open` reads a bounded native package. `CreateLayout` currently supports
paragraphs and nested spans in linear, reflowable XHTML spine items with an explicit
fallback font. Text supports printable ASCII, precomposed Latin/Greek/Cyrillic blocks,
Latin presentation ligatures U+FB00 through U+FB06,
and supplementary mathematical alphanumeric letters and digits when the font has
their glyphs. Text retains UTF-16 source ranges when scalars share a supported ligature,
including after wrapping, pagination, anchor resolution and explicit PDF conversion.
Mapped trademark signs, punctuation characters in the General Punctuation block and mapped characters
in the Currency Symbols block are also supported. Combining marks, complex scripts,
bidi controls, joiners, variation selectors and other unsupported characters still
require implementation and are diagnosed explicitly.
When no selected family maps U+00A0, a mapped blank space glyph in the same style
and weight can supply its advance without adding a line-break opportunity. Real
U+00A0 mappings in later families take precedence over this fallback. Source text,
UTF-16 offsets and converted PDF Unicode retain U+00A0, including repeated and
leading/trailing occurrences. Word spacing contributes to its placement, word
lookahead and intrinsic table widths. Missing or visibly painted space glyphs do
not qualify. Other missing supported glyphs report their full code point.
Chapters follow declared spine order and
begin on fresh pages. It returns immutable pages and logical UTF-16 text
placements in CSS pixels. Whole words wrap at spaces, and default block paragraphs have one font-size
gap between their line boxes. Wrapped lines and paragraphs continue onto new pages
when the next line box does not fit. Each page exposes its normalized, display-filtered text
slice, `SpineIndex` and `TextStart` offset; placement offsets remain relative to
that chapter's display-filtered text. These offsets can differ from structural `ReadText`
when hidden content is present. The layout accepts up to 1,000,000 UTF-16 text
characters across all chapters, with explicit refusal above that limit. Existing
per-spine processing, page, font and image limits still apply.
Headings `h1` through `h6` reuse paragraph box layout and retain their source
identity. All levels default to real weight 700 and zero horizontal margins.
Default font sizes for `h1` through `h6` are 2, 1.5, 1.17, 1, .83 and .67em;
their vertical margins are .67, .83, 1, 1.33, 1.67 and 2.33em respectively.
Author declarations override each default using the actual parent size; bare inline
headings omit default box margins. Default bold requires the typed font resolver,
with no synthetic weight. Other heading names remain diagnosed.
Empty `hr` elements reuse paragraph top-border layout. The native reader defaults
to a one-pixel current-color rule with .5em vertical margins and automatic
horizontal margins. Author declarations can override or reset these defaults.
This bounded rule style does not implement browser inset-border painting.
Rules reject text and child elements. Unspecified border color follows the
layout caller's text color; PDF conversion keeps its own default text color.
Explicit `br` elements within supported paragraphs and spans advance lines without
paragraph gaps. Consecutive breaks retain blank line heights through pagination,
and styled text keeps its source offsets. Structural normalization still omits
leading breaks before any text and trims trailing breaks.
Words wider than the content box are diagnosed explicitly.
Initial tables require rows and cells, optionally grouped in `tbody`, plain or
linked cell text, zero spacing and no cell padding. Explicit table and cell widths
must be positive. Automatic text-only tables share intrinsic word and line widths
across all rows, shrink columns between those limits to fit, and honor automatic
horizontal margins. Omitted widths and explicit `width:auto` behave alike.
Text links retain separate hit regions across column gutters.
Body groups retain source order and ancestry for inherited styles and selectors;
columns and whole-row pagination remain shared across groups. Empty groups add no text.
Empty table `summary` attributes remain inert and available to CSS selectors.
Only ASCII formatting whitespace is ignored between table parts. Other text,
including nonbreaking spaces, requires a cell and is diagnosed outside one.
Cell percentages resolve against the table once. Every row must use the same
column count and resolved widths, filling the table within 0.0000001 CSS pixels.
Cells wrap independently; row height follows the tallest cell, and whole rows
move to the next page while keeping contiguous chapter text ranges. Empty cells
retain their columns without adding text. Native and converted PDF output retain
the supported outer container background and preceding paragraph margins.
Explicit `display:table` on a table element retains the same typed grid.
Mixed explicit/automatic columns, automatic empty columns or replaced content,
header/footer groups, spans, nested tables, anonymous table boxes,
row-group backgrounds, nonzero spacing and row/cell/group page-break requests
remain diagnosed. Cells taller than a
fresh page are refused before retaining additional cell pages.
CSS `display:block` and `display:inline` follow specificity, declaration order and
inline overrides without inheriting the parent's display value. A bounded formatting
tree separates source elements from generated block containers and inline sequences.
Blocks inside spans split their inline continuations; block backgrounds fill the
containing width while inline backgrounds follow actual fragments. Default block
paragraphs retain the established paragraph gap. Inline paragraphs keep structural
extraction separators and source offsets without forcing visual line breaks, and
word measurement follows the visual sequence across those separators. Source spaces
discarded by structural extraction remain collapsed layout advances where needed,
without inventing extraction characters or selection placements. Block-edge spaces
do not paint empty advances. Other display values remain explicitly diagnosed.
Packaged eight-bit RGB/RGBA PNG images, untagged or explicitly sRGB, can
paint inline at intrinsic CSS-pixel dimensions, aligned to the shared baseline.
Images can appear directly in the body or a div, including standalone covers,
as well as supported paragraphs and spans. Image title metadata and the manifest
`cover-image` property are inert; other image manifest properties remain diagnosed.
With `display:block`, images consume their intrinsic height without an inline font
strut and remain in source order through pagination and explicit PDF conversion.
Supported sRGB, gAMA and square-pixel pHYs metadata is validated for length,
values, placement, duplication and checksum. Authoritative sRGB takes precedence
over a supplied positive gamma value. Physical density does not rescale the
intrinsic CSS-pixel geometry. Gamma without sRGB, non-square or undefined pixel
ratios, and other color/profile chunks remain explicitly unsupported.
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
refer to the chapter start. Body, paragraph, span and line-break IDs retain display-filtered offsets;
nonempty fragments decode once and resolve their actual paginated logical position. IDs retain
case and Unicode identity. Empty elements use the next text placement or the chapter's final
logical cursor. Duplicate spine targets or anchor IDs are diagnosed as ambiguous, and missing
anchors are rejected. A layout owns at most 100,000 anchors. Desktop chapter and within-page
outline navigation are available in Single mode; broader native workflows still need acceptance.
Explicit `ConvertToPdf` preserves EPUB 2 NCX and EPUB 3 navigation labels, order,
hierarchy and resolved page/anchor destinations in PDF points. Unlinked grouping
headings remain unlinked PDF bookmarks; the authoring API exposes this through
`PdfDocumentBuilder.AddBookmarkHeading`. Missing or ambiguous targets are diagnosed
instead of silently dropping bookmarks. The original EPUB resources remain unchanged.
`RenderPage` produces an owned linear-sRGB float frame through shared glyph rendering,
without creating a PDF. Inline and packaged CSS `font-size` and `line-height` support
positive `px`, `em` and percentage values that fit the content height. Font sizes resolve
against the parent after cascade selection. Relative line heights compute against the
element's font and inherit as lengths; unitless line-height factors recalculate for each
descendant's font. Mixed sizes share a baseline; collapsed spaces retain their original
inline style. Default normal line height follows each inline font's ascent and descent.
`text-indent` supports signed `px`, `em`, percentages, unitless zero and explicit
`inherit`, `unset` and `initial`. An em length computes on the declaring element
before inheritance; percentages resolve against the containing content width.
Only the first formatted line is indented, including across wrapping and page
continuations. A positive indent can put the first word beyond the line or page
edge; subsequent words wrap normally. Native and converted PDF coordinates retain
that overflow. Values and computed lengths are bounded to an absolute 32768;
`calc()`, `hanging`, `each-line`, general long-word overflow and RTL indentation
remain outside this initial slice.
`font-family` preserves ordered unquoted identifiers, multiword names and quoted names
(including commas). Generic requests are distinct from quoted names such as `"serif"`.
The optional `FontFamilyResolver` on layout and conversion options receives an
`EpubFontFamily` and returns a captured `TrueTypeFont`, or null for an unavailable face.
The first face containing each supported scalar supplies its glyphs and advances;
otherwise the explicit default font applies. Native outlines, mixed-face baselines,
GSUB runs and saved PDF resources retain the selected face. Each list is limited to
32 names of 256 characters; each layout permits 256 distinct lookups, 64 retained
faces (including the default), 64 MiB of additional font bytes and bounded matching
work. Backslash escapes and CSS-wide family keywords remain diagnosed.
`font-weight` accepts normal (400), bold (700), and numeric weights from 1 through
1000, with property-specific cascade and inline overrides. Both `font-size` and
`font-weight` accept `inherit` and `unset` using the immediate computed parent,
and `initial` using the reader's initial size and normal weight. The optional
`FontFaceResolver` replaces the legacy resolver and receives the requested family,
computed weight, style and explicit default font. A null family requests a variant of that
default. Return the actual font, weight and style with `Exact` or `Nearest`; inconsistent
results are rejected. Real selected metrics and outlines flow into native rendering
and PDF embedding. Without this resolver, the legacy callback and direct default
remain available only for upright weight 400; unresolved other weights or styles are diagnosed.
KillerPDF connects native opening and conversion to installed faces using their actual
OS/2 numeric weights and CSS nearest-weight ordering. The font cache retains all
registered candidates while preserving existing PDF editor style choices. Relative weights,
packaged `@font-face`, oblique matching, complex-script clusters and full bidi still
require implementation; this is not complete typography.
Initial Latin GSUB 1.0 processing selects default-language `liga` and, when requested,
`smcp` features in font lookup order. `font-variant:small-caps` inherits, with normal,
initial, inherit and unset handling. Font-provided small capitals preserve the original
text, source ranges and CSS line metrics while using selected glyph advances and ink.
Native advances use the font's original design units through `GetAdvanceWidth` and
`UnitsPerEm`. Embedded PDF widths retain fractional normalized values, avoiding
premature rounding. The existing `GetPdfAdvanceWidth` API still returns rounded
1000-unit widths. Presentation ligatures preserve their original Unicode text even
when a selected font expands one character into several glyphs.
Single substitution supports type 1 format 2 with coverage 1 or 2, and delta format 1
with coverage 1. Multiple substitution supports type 2 format 1 with coverage 1 or 2,
nonempty sequences of at most 256 glyphs and retained original source ownership.
Every expanded glyph is painted; subsequent lookups operate on the glyph sequence,
and overlapping source spans merge without duplicating original text. PDF conversion
emits each logical cluster once, preserving its original text through ActualText.
Ligature substitution supports type 4, flags 0, format 1 and coverage formats 1 or 2.
Type 7 extension format 1 can wrap supported type 1, 2 or 4 tables; all extensions in
one lookup must wrap the same type. Single-component identity and replacement
records preserve their source character. Fonts without the
selected small-caps feature, synthetic fallback, broader scripts/languages and the
font-variant-caps longhand still require implementation and are diagnosed where used.
Nested extensions, other selected lookup types and nonzero flags remain diagnosed.
Original UTF-16 cluster ranges and
selected glyph advances drive both native ink and explicit PDF conversion. Style,
block and explicit-break boundaries remain separate. Natural text and single-glyph
ligatures keep their Unicode font mappings as well as logical replacement text. Parsing
and shaping have separate work limits and cancellation. Other GSUB routes, positioning,
Arabic/Indic shaping, bidi processing and per-character carets remain incomplete.
Foreground `color` accepts six-digit sRGB hex values and the 16 basic CSS color
keywords and the `grey` alias, case-insensitively. `color:initial` resets to the
reader's black. Other values inherit and follow property-specific cascade
precedence. Individual glyphs retain their foreground color through rendering.
The neutral `font-style:normal` declaration keeps the selected face; italic and
oblique face selection remain explicitly unsupported.
Body `background-color` uses the same color syntax, also accepts `transparent`, and paints the whole reading
canvas, including margins, at natural and requested pixel sizes. Paragraph backgrounds
fill each content-width line, including reserved blank lines; span backgrounds follow
their fragments across wrapping and page breaks. Backgrounds do not inherit.
Transparent descendants reveal ancestor paint. Paragraph and ancestor-ordered span
backgrounds paint before image and glyph ink, including tight line-height overlap.
Other element backgrounds remain explicitly diagnosed. Explicit transparent body declarations clear earlier
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

Text and body background colors accept legacy comma-separated `rgb()` with three
integers or three percentages, including signed values and decimal percentages.
Channels clamp to their valid ranges and retain source precision until rendering.
Inline and packaged styles follow the existing cascade and remain independent of
Windows region settings. Mixed units, malformed values, alpha/HSL and modern
space-separated syntax remain diagnosed; complete CSS color support is still pending.

Body margins accept nonnegative CSS pixels, em and percentages, including unitless
zero, one-to-four-value shorthand and side-specific longhands. Each side follows
stylesheet specificity/source order and inline overrides without inheriting to children.
Percentages use the configured page content width; em uses the body's computed font size.
Horizontal insets persist across pages and reduce wrapping width. The initial top
margin is not repeated on continuation pages. An auto-height terminal body's bottom
margin does not reserve space on every page or create an extra empty page.
Native pixels and reopened PDF geometry cover these cases. Negative/auto margins,
inline child margins, other units, exhausted content geometry and advanced fragment
semantics remain explicitly unsupported; complete CSS layout remains in progress.

Block paragraphs support nonnegative horizontal margins and padding in px, em and
percentages. Paragraph and basic div content widths accept positive px/em/percentage
lengths or auto, with independent cascade and inline overrides. Specified widths
exclude padding; percentage widths and vertical margins use the actual containing
content width before child side insets. Nested backgrounds retain their own bounds,
and following siblings recover the parent's full available width. Native and saved
PDF checks cover alignment, background edges, wrapping and page continuation.
Horizontal insets use the existing per-side cascade. Percentages use the parent's
content width; em uses the paragraph's own font size. Text wraps, aligns and resumes
after page breaks inside its content box. Backgrounds include padding and exclude
margins, and sibling paragraphs recover their own bounds. Explicit zero top and
bottom paragraph margins suppress their respective default spacing; an unspecified
adjacent side retains the reader's default gap. Empty zero-margin paragraphs add no
extra line. Nonnegative vertical paragraph margins accept px/em/percentages and
collapse adjoining sibling and empty-block margins to their maximum. A body's top
margin collapses with its initial paragraph chain when top padding does not separate
them. Percentages use the containing content width before paragraph side insets.
Basic block `div` containers also support nonnegative margins, including adjoining
child and sibling collapse, empty containers and top margins after forced breaks.
They add no default paragraph gap.
Forced breaks discard the preceding margin and retain the next top margin; automatic
continuations do not repeat the paragraph's top margin. These rules also apply to
explicit PDF conversion. Paragraph vertical padding supports these same lengths,
separates empty-block margins and retains initial padding with the first line at
automatic page breaks. Initial padding too tall to share a page with its first line
flows across content areas before that line. Bottom padding also continues through
content areas, including pages without text. Basic nested `div` containers retain block identity and solid
backgrounds across transparent child paragraph padding. Layout retains at most
10000 pages across the document, including pages created only by padding, and checks
cancellation before retaining each page. Signed/auto margins, inline
box insets, complete container CSS and resource acceptance
still require implementation or verification.

Body padding accepts the same nonnegative px/em/percentage shorthand and longhand
forms, with per-side cascade and no inheritance. Percentages use the original
configured content width before margins; padding reduces wrapping width. Top padding
appears on the initial fragment, and fitting bottom padding only on the terminal
fragment. Terminal body padding overflow remains explicitly unsupported.
Border shorthand accepts zero used widths, including `border:0`,
`border:none` and a width with initial style none, with proper shorthand resets.
Solid paragraph top borders accept px/em lengths and thin/medium/thick widths,
RGB, transparent or the paragraph text color. Their cascade includes border shorthand
resets; margins stay outside the border, backgrounds extend beneath it and first-line
page movement retains the border with top padding. Other visible borders and border
longhands remain unsupported. Public native and reopened PDF checks cover
empty and text-containing paragraphs with oversized top borders across three pages.
Complete container border fragmentation remains unverified. Public native and reopened
PDF tests cover placement, wrapping, complete pixel comparisons and two-page controls.

Font-size keywords use the configured initial reader size as `medium`, independent
of author sizes on ancestors. Absolute `xx-small` through `xxx-large` follow the
CSS Fonts guideline scale table. Relative `larger` and `smaller` scale the computed
parent size by 1.2 and its reciprocal. These are the reader's documented user-agent
policies; they do not clamp author sizes to a minimum pixel height. Inline and
packaged declarations retain cascade, wrapping, pagination and computed-height
guards. The keywords apply to font size, not line height.

Initial CSS namespace support resolves per-sheet prefix/default declarations and
qualified type/class selectors against expanded XML names. Prefix spelling adds no
specificity, and namespace names remain literal without fetching resources. Explicit
any/empty namespace forms, rebinding and independent property cascade are covered.
Declarations accept bounded ASCII identifier prefixes and quoted or url(...) names.
Class-only selectors and combinations match whitespace-separated class tokens.
ID predicates match the exact, case-sensitive unqualified `id` value and combine
with types, classes and descendant ancestors. ID count outranks class count,
which outranks type count; equal specificity follows source order, and properties
still cascade independently. The default namespace constrains the
implied universal selector. Identifiers use the bounded letter-leading ASCII
subset. Type selectors with optional classes also support one terminal attribute
presence, exact-value or whitespace-token predicate, such as
`span[epub|type~='bridgehead']` or `h2[title='']`. Exact matching compares the full
case-sensitive value, including empty strings; empty whitespace tokens never match.
Attribute matching uses case-sensitive expanded names; namespace aliases bind by
URI, and default element namespaces do not apply to unqualified attribute names.
Inert `type` metadata is accepted on supported paragraphs and spans. Attribute
predicates add class-level specificity and retain the selector work limits.
Descendant combinators match distinct ancestors in order, allowing intervening
elements. Child combinators (`>`) require the immediate parent, with optional CSS
whitespace. Mixed chains search for a valid ancestry within the selector work budget.
Each compound retains its namespace, type, IDs, classes and supported attribute
predicate. Ancestor traversal shares the selector work budget, and selector trimming
uses only CSS whitespace. Other attribute operators, class-only attribute selectors, multiple predicates,
escapes, comments, general identifiers, sibling and other pseudo selectors retain
strict diagnostics. This is partial CSS support.

Text alignment supports left, center, right and LTR justification, with inherited
values, stylesheet cascade and inline overrides. Alignment uses the body content
box after wrapping and pagination. Justification expands spaces on wrapped
nonfinal lines; final and forced-break lines remain left aligned. Native pixels
and independently reopened PDFs cover placement, backgrounds, anchors and inline
images. Conversion retains text/image paint order, including glyph overhangs.
Full bidi, shaping and CSS layout remain in progress.

Block `page-break-before:always` and `page-break-after:always` force page boundaries;
`auto` retains natural pagination. Their independent cascade and inline overrides
apply without inheritance. After-breaks wait for following content or box extent,
preserving nested inline text, empty padding boxes and images without terminal
blank pages. Zero-width images do not consume a pending boundary. Ordered block
events retain empty boxes, anchors and image order without adding extraction text.
Blank lines count as page content even when they paint no ink. Native layout
and independently reopened PDFs cover geometry, source positions, backgrounds,
images and paper dimensions. Other break values remain explicitly unsupported.

`ReadText`, `ReadNavigation` and the supported plain native layout accept the canonical HTML5
`<!DOCTYPE html>` and XHTML 1.1 external declarations without loading or processing
a DTD. Original resource bytes, Unicode and UTF-8/UTF-16 encodings stay unchanged.
HTML5 external identifiers, internal subsets and unknown declarations remain
rejected. EPUB3 navigation diagnoses script elements, event attributes and resources
declared as scripted. Scripts are never executed, and navigation targets remain local.

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
text, resolved CSS text colors, RGB/RGBA PNG images and solid body/paragraph/span backgrounds
from the supported native layout. PNG transparency remains a PDF soft mask. It leaves the native package and existing read
snapshots unchanged. Native EPUB/DjVu sessions expose an explicit Document tools
conversion action. It publishes a separate new PDF atomically, opens an independent
PDF tab and preserves the original native tab. EPUB offers paper and margin options,
initially A4 with 15 mm margins. Existing destinations are not overwritten, and
unsupported content is diagnosed before any PDF is published. Full format and
workflow acceptance remains in progress.

`DjvuDocument.ConvertToPdf()` initially supports bitonal Smmr and grayscale IW44 pages and supported
text with complete leaf-zone coverage.
It embeds lossless one-bit images or original eight-bit grayscale samples at the
page's recorded DPI. Grayscale uses D65 CalGray with the recorded INFO gamma,
without quantizing a rendered preview. Conversion preserves intrinsic
orientation, bundled page order and local bookmark labels, hierarchy and page
destinations. The source, native rendering and navigation stay unchanged. Relative,
external, invalid or unrepresentable bookmark entries reject the conversion rather
than being omitted. Invisible text retains logical Unicode and separator strings
at the recorded zone bounds through all four intrinsic orientations. One glyph
transports each logical zone, without inventing individual character positions.
The bundled font must support every non-whitespace source character. Unlocated,
incompletely covered, overlapping or out-of-page text is diagnosed. Conversion is
limited to 4096 nonempty leaf zones per page and 65536 UTF16 units per logical
replacement. Color conversion and full format preservation remain unfinished.

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
identity and an independent generation.

`PdfProofPreset.Capture(name, settings)` snapshots named requested preferences without
embedding ICC bytes or file paths. `ToJson()` and `FromJson()` use preset schema 1,
independent of the application's eventual settings schema. Imports require exact,
unique properties, a nonblank name of at most 128 UTF-16 code units without controls,
and a null or 64-character ASCII hexadecimal profile content reference. References
use uppercase SHA256. JSON is limited to 1 MiB of UTF-8 and eight nesting levels.
Malformed input produces sanitized `FormatException`; unsupported schema versions
produce `NotSupportedException`. Import never reads a profile or fetches resources.
`ResolveProfile(profile)` accepts only the referenced content; a different valid
profile is rejected. Passing null preserves the unavailable reference in returned
`PrinterProfileSha256` settings and later preset captures. Applying a different
reference or changing profile availability advances the document/view proof stamp;
equivalent available bytes or equivalent unavailable references do not.
Matching content alone does not establish transform availability or effective proofing.
Desktop profile-copy and named-preset storage are separate application services.
The Color Management panel imports validated ICC copies and saves or loads named
requested preferences. Saving and loading operate on draft controls; Apply updates
the captured document or comparison view. Saved presets do not change open documents.
Preset storage supports explicit replacement and recovery. Unchanged plain PDFs and native documents
restore their owner's requested proof settings from matching paths and captured
contents before view bootstrap; explicit initial copy settings take precedence.
The ordinary PDF save tail records owner preferences against the captured
published path and contents, including a new Save As destination. A storage
failure leaves the published PDF and current viewing request usable.
Different contents at the same path start with proof off. The panel offers the
previous file's preferences for explicit draft review; Close discards the draft,
and Apply adopts it without rewriting the PDF.
Comparison overrides do not replace stored owner defaults. Storage failures leave
document opening and viewing available, with a status and panel notice. Protected
and repaired opens, specialized save paths and full management
controls remain in progress.

`PdfDocumentProofView` shares its logical owner's request unless an explicit
`ComparisonOverride` is selected. Its immutable capture includes view/document
identities and document/override generations. An unchanged comparison stays current
when the owner's preferences change; clearing it adopts the latest owner request.
Equivalent profile content and options do not advance the override generation.
Desktop document sessions retain their own view wrapper across tab switches.
Initial native Single-page rendering applies captured proof preferences.
Primary presenter requests carry a generation derived from the captured view stamp;
unchanged proof requests retain that generation.
WPF PDF detail regions use the captured proof settings and reject publication
when the view's proof stamp changes. Initial Single-page source and visible-region
checks cover a generated printer profile. Native detail frame requests also retain
those settings and the primary proof generation, verified through a recording
presenter boundary. This does not establish GPU submission or physical color accuracy.
Initial PDF Two-page viewing applies the document's proof request to both page
rasters, with proof-aware cache and publication checks. Generated-profile source
and visible-screen checks cover proof on and off; other mode/profile coverage remains open.
The PDF Continuous and Two-page continuous application-managed SDR paths also
carry captured proof settings. Generated-profile source and owned-screen checks
cover both pages and proof on/off. Continuous compatibility viewing under
compositor-owned policy also retains proof, verified with a synthetic display
observation and actual source/screen on/off checks. Native EPUB Continuous,
Two-page and Grid viewing also forward the captured proof request, with both-page source
and owned-screen on/off checks. Native secondary tiles use captured read revisions
and preserve proof diagnostics. Other formats, modes and profiles remain open.
Unsupported native Single-page and Continuous proof requests retain their preferences
and show normal viewing with a proof diagnostic. Continuous missing-profile checks
cover both pages and verify that disabling proof clears those diagnostics.
Native EPUB Continuous, Two-page, Two-page continuous and Grid SDR viewing apply the captured monitor
converter and reject stale conversions. Native facing/Grid monitor bytes bypass
the cache shared across displays; secondary publication checks display identity.
Independent identity-XYZ monitor endpoints and actual page captures cover both
pages with proof on/off. Unreadable monitor-profile checks retain normal viewing
and diagnostics in Single, Continuous and Two-page modes. These are simulated
display observations, not physical measurements or all-profile acceptance.
Owned-app display notifications exercise valid, unavailable and recovered profile
observations in Two-page, Continuous and Two-page continuous views. Continuous
checks retain scroll position, view mode and the public proof-view owner.
Proof controls, other rendering paths and persistence remain unfinished.

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
Supported catalog CMYK output intents bind DeviceCMYK source colors in float RGB
viewing; resource DefaultCMYK remains authoritative. This source-managed path
retains ordinary RGB/Gray viewing values rather than implicitly simulating the
catalog printer destination. Explicit printer proofing remains separate, and
compatibility byte rendering retains its existing destination-profile behavior.
Catalog CMYK images use the existing precise ICC image conditions, including
relative-colorimetric A2B1 LUT conversion without black-point compensation at
natural size. Supported DeviceRGB page/Form groups remain available. Named process
and spot spaces, DeviceN, separation frames and unsupported groups remain diagnosed;
nonpainting None colorants stay omitted.
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
Annotation appearances, including saved widget appearances, use the same supported paint operations as Form
XObjects, including their placement, clipping, region rendering and proof conversion.
Hidden, screen-only and print-only flags retain their existing filtering behavior.
Requested form-field regeneration and unsupported appearance paint are diagnosed
rather than silently quantized. Supported saved widgets retain fractional colors,
region placement, selective signature/button inclusion and printer-proof alpha.
Explicit neutral graphics-state defaults retain precise painting.
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
Ordinary float viewing honors BMC/BDC/EMC marked content, including nested optional-content visibility.
The supplied `PdfRenderOptions` still validate the full page's byte-raster dimensions and limit.
Supported RGB/Gray matrix/TRC image profiles can also replace DeviceRGB/Gray through
resource `DefaultRGB` and `DefaultGray`, preserving their source precision and extended RGB.
Direct matrix RGB/Gray ICC images at aligned natural size also retain matching
DeviceGray 8/16-bit soft-mask alpha with default Decode and relative intent.
Masked default substitutions, enlargement, reduction, Matte and other intents remain diagnosed.
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
