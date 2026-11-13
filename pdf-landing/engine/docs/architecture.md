# Architecture

The KillerPDF.Engine is a reusable document library inside the KillerPDF monorepo. The project reference enforces a UI-free boundary while the shared repository lets application integration, library behavior, tests, and corpus gates evolve together.

## Library boundary

KillerPDF 1.9 uses The KillerPDF.Engine for rendering, text extraction, and OCR. Tesseract remains available in the desktop app as a fallback OCR provider. See the developer guide for current engine APIs.

## Internal layers

| Layer | Responsibility |
| --- | --- |
| `Syntax` and `Objects` | Binary-safe tokens and the typed PDF object model |
| `CrossReference`, `Filters`, and `Documents` | Revision discovery, stream decoding, lazy resolution, and authenticated access |
| `Authoring` | Typed construction of pages, content, resources, metadata, interactive features, and standards structures |
| `Editing` | High-level byte-preserving changes and safe cross-document graph imports |
| `Writing` | Incremental revisions, deterministic rewrites, metadata policy, and output sanitization |
| `Security` and `Signing` | Authentication, permissions, password and certificate encryption, signatures, timestamps, and revision verification |
| `Rendering` and `Fonts` | BGRA32 and float32 page rendering, image decoding, font decoding, and glyph outlines |
| `ColorManagement` | ICC profile loading, rendering intents, printer proof settings and presets, printer gamut checks, and the bundled working CMYK profile |
| `Formats` | Native EPUB and DjVu readers, which `PdfReadSnapshot` in `Documents` presents through one read surface and which convert to PDF through `Authoring` |
| `Diagnostics` and `Validation` | Bounded inspection, implementation limits, structural reports, and round-trip checks |

## Read flow

1. Parse the header and locate the final `startxref` marker.
2. Read the cross-reference revision chain.
3. Authenticate encrypted documents before resolving protected objects.
4. Resolve indirect objects lazily through the cross-reference table.
5. Present common document features through typed readers.

## Write flow

For a deterministic rewrite, `PdfDocumentWriter` walks the resolved graph and serializes a normalized document. Repeating the write with equivalent input produces reproducible output.

For an incremental edit, a high-level editor records the intended changes, imports dependent graphs when needed, validates the result, and appends one new revision without modifying the original byte prefix.

## Native format flow

EPUB and DjVu documents do not pass through the PDF object model. Their own readers parse the package or container, and `PdfReadSnapshot` exposes pages, text, outlines, and rendering for them. A PDF exists only when you call a `ConvertToPdf` method, which builds a separate document with `PdfDocumentBuilder`.

Content a reader cannot represent is reported through diagnostics, or the reader refuses it.

## Fail-closed editing

Some operations intentionally refuse incomplete global structures. Examples include unsafe partial imports from tagged documents, ambiguous shared object graphs, unsupported XFA merges, missing credentials, and edits forbidden by authenticated user permissions.

A refusal is safer than producing a file that opens but has silently damaged navigation, forms, accessibility, optional content, or signatures.

## Architecture decisions

- [ADR-001: PDF engine boundary](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/docs/architecture/ADR-001-pdf-engine-boundary.md)
- [ADR-002: Modern Windows shell integration](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/docs/architecture/ADR-002-integrate-engine-through-modern-windows-shell.md)
- [ADR-003: Installed and portable packages](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/docs/architecture/ADR-003-split-installed-and-portable-packages.md)
