# The KillerPDF.Engine

The KillerPDF.Engine is an independent, UI-free .NET library for reading, validating, authoring, structurally editing, signing, encrypting, and writing PDF files. It gives KillerPDF a modern PDF 2.0, PDF/A, and PDF/UA foundation while exposing a public API designed for use in other applications.

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

KillerPDF itself now uses those typed content APIs for its complete annotation and stamp burn-in pipeline. Text boxes, highlights, redactions, freehand ink, filled shapes, signatures, placed images, page numbers, and watermarks are written as isolated page overlays with rotation-aware placement and self-contained resources.

### Edit document structure

Pages can be inserted, imported, removed, reordered, rotated, cropped, resized, trimmed, and assigned page boxes, labels, transitions, thumbnails, annotations, form widgets, and structure relationships. Object graphs and dependent resources are imported with collision handling rather than copied as isolated dictionaries.

### Build and edit interactive PDFs

The KillerPDF.Engine supports bookmarks, destinations, links, attachments, visual and editorial annotations, replies, popups, redactions, optional-content groups, and AcroForm fields. Its bookmark reader exposes the complete hierarchy with decoded titles, stable object identity, presentation state, and resolved local or named destinations without leaking parser objects. Its native link reader exposes normalized page geometry, annotation indices, decoded URI actions, and resolved direct or named page targets. Its form-widget reader exposes inherited hierarchical field state, interactive values and options, button states, crop-aware geometry, and rotation. Text fields, checkboxes, radio buttons, combo boxes, list boxes, push buttons, and signature fields have typed authoring and incremental-editing APIs.

### Handle standards and accessibility

The engine includes PDF/A-4, PDF/A-4e, PDF/A-4f, tagged PDF, and PDF/UA-2 authoring safeguards. Structure trees, parent trees, semantic roles, alternate descriptions, output intents, embedded fonts, metadata, annotations, forms, and associated files are validated as coordinated document features.

### Protect and sign documents

Password security covers RC4, AES-128, AES-256, crypt filters, permission flags, authenticated imports, incremental updates, and rewrites. Digital-signature support includes detached CMS signing, signature fields, certification permissions, field locks, seed constraints, timestamp attributes, signature discovery, cryptographic verification, and signed-revision analysis.

### Validate before trusting output

Structural diagnostics, bounded parsing, explicit implementation limits, round-trip validation, and fail-closed graph imports prevent ambiguous or unsafe input from being silently rewritten. Generated conformance fixtures are checked with independent tools rather than accepted because the header contains a particular version number.

## Capability summary

- PDF syntax, objects, streams, classic cross-reference tables, cross-reference streams, object streams, trailers, and incremental revisions
- Page text, word and glyph geometry, font information, and image placements, including nested forms and inline images
- Bounded BGRA32 rendering for blank pages and transformed device-color rectangle fills
- Deterministic full rewrites and byte-preserving incremental updates
- PDF 2.0 document authoring with pages, content streams, graphics state, fonts, images, color spaces, shadings, patterns, transparency, and resources
- Navigation, bookmarks, named destinations, page labels, viewer preferences, transitions, optional content, and attachments
- Visual annotations, text markup, links, replies, popups, redactions, file attachments, and annotation editing
- AcroForm creation and editing for text fields, checkboxes, radio buttons, choice fields, push buttons, and signature fields
- Tagged PDF and PDF/UA-2 structure authoring and editing
- PDF/A-4, PDF/A-4e, and PDF/A-4f authoring safeguards
- RC4, AES-128, and AES-256 password security, crypt filters, authenticated imports, incremental updates, and rewrites
- Detached CMS signatures, certification permissions, field locks, seed constraints, signature discovery, cryptographic verification, and signed-revision analysis
- Structural diagnostics, bounded parsing, implementation limits, round-trip validation, and fail-closed import validation

## Rendering status

The engine has the first bounded CPU-rendering slice for blank pages and transformed DeviceGray,
DeviceRGB, and DeviceCMYK rectangle fills. Text, images, general paths, clipping, transparency,
annotations, and forms are not rendered yet, so KillerPDF continues using PDFium for complete
application rendering while coverage expands. The engine does not provide UI controls.

## Repository layout

```text
engine/
  KillerPdf.Engine/          Reusable library
  KillerPdf.Engine.Tests/    Unit and regression tests
  KillerPdf.Engine.Corpus/   Corpus gates and standards smoke generators
  docs/                      Architecture records
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

KillerPDF directly references The KillerPDF.Engine for document parsing, text extraction, writing, and editing. The Windows application no longer references PdfPig or PdfSharpCore. Its rendering calls now pass through one replaceable boundary; PDFium remains the active complete renderer while engine coverage expands.

See [The KillerPDF.Engine changelog](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/CHANGELOG.md) for detailed capability history.

## License

The KillerPDF.Engine is licensed under GPLv3 as part of the KillerPDF repository. See the repository [LICENSE](https://github.com/SteveTheKiller/KillerPDF/blob/main/LICENSE).

Bundled font metrics and character maps retain their original licenses in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), also included in the NuGet package.
