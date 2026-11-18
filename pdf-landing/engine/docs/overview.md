# The KillerPDF.Engine

The KillerPDF.Engine is an independent, UI-free .NET 10 library for reading, extracting text, rendering, searching, validating, authoring, editing, redacting, signing, encrypting, and writing PDF files. It also reads EPUB and DjVu documents natively, converts them to PDF, and renders with ICC color management and printer proofing. It gives applications a modern PDF 2.0, PDF/A, and PDF/UA foundation without tying them to the KillerPDF desktop interface.

## Start building

Install the package from [NuGet.org](https://www.nuget.org/packages/KillerPdf.Engine):

```powershell
dotnet add package KillerPdf.Engine
```

[Getting started](getting-started.html)

Use the task guides in the sidebar when you already know what you need to do.

## What the engine covers

- PDF syntax, objects, streams, cross-reference tables, cross-reference streams, object streams, trailers, and incremental revisions
- Deterministic full rewrites and byte-preserving incremental updates
- PDF 2.0 authoring with pages, content streams, fonts, images, color spaces, patterns, transparency, and resources
- Bookmarks, named destinations, page labels, links, attachments, annotations, optional content, and AcroForm fields
- Tagged PDF, PDF/UA-2, PDF/A-4, PDF/A-4e, and PDF/A-4f authoring safeguards
- RC4, AES-128, AES-256, and AES-GCM password security, and certificate-recipient encryption
- Detached CMS signatures, certification permissions, field locks, RFC 3161 timestamps, verification with trust and revocation policy, PAdES validation evidence, and signed revision analysis
- Page text, word, and table extraction, and bounded BGRA32 page rendering without a native renderer
- OCR with local models, searchable text layers, and reviewed corrections
- Content-level redaction with sensitive-data search and read-back verification
- AcroForm editing, FDF and XFDF interchange, XFA inspection and conversion, and data merge
- Export to plain text, HTML, Markdown, JSON, Word, Excel, and PowerPoint formats, and to reflowable EPUB 3 books
- Native EPUB and DjVu reading (in development): metadata, text, outlines, page and region rendering, explicit conversion to PDF, and link regions for EPUB
- ICC color management: float32 page rendering, monitor-profile output, a bundled working CMYK profile, and printer proofing with all four rendering intents, black-point compensation, paper and black simulation, overprint simulation, and gamut warnings. HDR output and physical color accuracy are still in progress
- Picture editing: move, resize, and remove placed images, detached image assets, and rectangle or color-area pixel selection and extraction
- Repair and lossless optimization plans, preflight, accessibility, and print-production reports
- PDF/UA-1 checks against the 31 Matterhorn Protocol checkpoints, with each failure named by its condition ID
- Imposition, headers, footers, Bates numbering, calibrated measurement, layers, attachments, and structural comparison
- Structural diagnostics, bounded parsing, implementation limits, and round-trip validation

## Project scale

| Measure | Current 1.9.0 development tree |
| --- | --- |
| Engine source files | 386 |
| Physical C# lines | 130,218 |
| Engine tests | 6,427 |
| Corpus PDFs | 47,024 |

## A real library boundary

KillerPDF 1.9 uses The KillerPDF.Engine for rendering, text extraction, and OCR. Tesseract remains available in the desktop app as a fallback OCR provider. See the developer guide for current engine APIs.

The monorepo keeps the engine, application integration, tests, and corpus gates in one reviewable change. The project boundary still enforces a reusable library that other .NET applications can reference through NuGet.

## Design principles

- Preserve existing bytes when an operation can be represented as an incremental revision.
- Fail closed when required structure cannot be interpreted or preserved safely.
- Emit deterministic output so regressions are reproducible.
- Enforce explicit implementation limits before allocating or serializing unbounded structures.
- Keep public APIs typed instead of exposing KillerPDF application state.
- Treat conformance as validator-backed behavior, not a label inferred from the PDF header.

## Source and license

The engine is developed in the [KillerPDF repository](https://github.com/SteveTheKiller/KillerPDF/tree/main/engine) and licensed under the [Mozilla Public License 2.0](https://www.mozilla.org/MPL/2.0/). The KillerPDF desktop app stays GPLv3.

MPL 2.0 works file by file. You can use the engine in an app of your own, open or closed source, under whatever license you choose. If you change one of the engine's own files and ship it, that file's source stays open under MPL 2.0. Your code around it stays yours.

Detailed capability history is recorded in the [engine changelog](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/CHANGELOG.md).
