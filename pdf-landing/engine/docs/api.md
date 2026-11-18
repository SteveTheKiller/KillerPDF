# API guide

The public API is organized by PDF responsibility. Start with high-level document, authoring, editing, and validation types. Use syntax and object types when you deliberately need the lower PDF layer.

## Primary namespaces

| Namespace | Use it for |
| --- | --- |
| `KillerPdf.Engine.Documents` | Open files, read document, page, text, bookmark, link, form, and tree information, run redaction, export, OCR, comparison, and other document workflows, and read native EPUB and DjVu snapshots |
| `KillerPdf.Engine.Authoring` | Create documents, page content, resources, annotations, forms, navigation, and conformance structures |
| `KillerPdf.Engine.Editing` | Append byte-preserving revisions to existing documents |
| `KillerPdf.Engine.Writing` | Deterministic rewrites, incremental update options, and output sanitization |
| `KillerPdf.Engine.Diagnostics` | Bounded structural inspection and diagnostics |
| `KillerPdf.Engine.Validation` | Deterministic reopen and round-trip verification |
| `KillerPdf.Engine.Security` | Password authentication, permissions, encryption options, and security handlers |
| `KillerPdf.Engine.Signing` | Signature creation, discovery, verification, certification, and revision analysis |
| `KillerPdf.Engine.Rendering` | Render pages to BGRA32 pixel buffers or float32 frames, with printer proof options and gamut masks |
| `KillerPdf.Engine.ColorManagement` | Load ICC profiles, convert color between profiles, describe printer proofs and presets, check printer gamut, and supply the bundled working CMYK profile |
| `KillerPdf.Engine.Formats.Epub` | Open EPUB packages, read metadata, text, and navigation, lay out and render pages, convert books to PDF, and write a PDF as an EPUB 3 book |
| `KillerPdf.Engine.Formats.Djvu` | Open DjVu documents, read page information, text, and navigation, render pages, and convert documents to PDF |
| `KillerPdf.Engine.Fonts` | Font loading, text codes, glyph outlines, and host font resolvers |
| `KillerPdf.Engine.Objects` | Typed PDF objects for lower-level work |
| `KillerPdf.Engine.Syntax` | Headers, tokens, tokenizer behavior, and PDF versions |

## Common entry points

| Task | Start with |
| --- | --- |
| Open a PDF | `PdfDocument.Open` |
| Inspect unknown input | `PdfDocumentInspector.Inspect` |
| Read metadata and page count | `PdfDocumentInformation.Read` |
| Read page dimensions and rotation | `PdfPageInformation.Read` |
| Read bookmarks | `PdfBookmarkReader` |
| Read links | `PdfLinkReader` |
| Read forms | `PdfFormWidgetReader` |
| Extract text and geometry | `PdfPageContentReader` |
| Render a page | `PdfPageRenderer` |
| Render with float32 precision | `PdfPageRenderer.RenderFrame` and `PdfPageRenderer.RenderFrameRegion` |
| Proof a render for a printer | `PdfRenderOptions.Proof`, `PdfDocumentProofSettings`, and `PdfProofPreset` |
| Mark out-of-gamut pixels | `PdfRenderedFrame.WithGamutWarningsMarked` |
| Load the working CMYK profile | `PdfCmykViewingDefault.LoadProfile` |
| Open an EPUB | `EpubDocument.Open`, `PdfReadSnapshot.OpenEpub`, and `PdfReadSnapshot.OpenEpubProgressive` |
| Open a DjVu document | `DjvuDocument.Open` and `PdfReadSnapshot.OpenDjvu` |
| Convert EPUB or DjVu to PDF | `EpubDocument.ConvertToPdf` and `DjvuDocument.ConvertToPdf` |
| Write a PDF as an EPUB 3 book | `PdfEpubExport.Write` |
| Check PDF/UA-1 | `PdfUaValidator.Validate` |
| Redact content | `PdfRedactionSearch` and `PdfContentRedaction` |
| Optimize a file | `PdfOptimizer` |
| Export to Office, HTML, Markdown, or JSON | `PdfStructuredExport` |
| Run preflight | `PdfPreflightRunner` |
| Create a PDF | `PdfDocumentBuilder` |
| Build page content | `PdfContentStreamBuilder` |
| Change pages and forms | `PdfIncrementalPageEditor` |
| Change annotations and links | `PdfIncrementalAnnotationEditor` |
| Move, resize, or remove a placed image | `PdfImagePlacementEditor` |
| Select or extract pixels | `PdfRasterSelection` and `PdfRasterExtraction` |
| Rewrite deterministically | `PdfDocumentWriter.Write` |
| Validate a rewrite | `PdfRoundTripValidator.Validate` |
| Read and verify signatures | `PdfSignatureReader`, `PdfSignatureVerifier`, and `PdfSignatureInspection` |

## High-level before low-level

The high-level APIs coordinate changes across related PDF structures. For example, adding a form field can affect the page annotations, AcroForm field tree, appearance resources, default resources, and document catalog. Prefer the typed method that represents your intent.

Use `PdfObject`, `PdfDictionary`, `PdfArray`, `PdfStream`, `PdfName`, and indirect references when implementing a feature that the high-level surface does not cover. Validate the complete graph before writing.

## API documentation in your IDE

The package includes XML documentation generated from the public source comments. Visual Studio, Rider, and other .NET editors display summaries and parameter help while you type.

For the complete current surface, browse the [engine source](https://github.com/SteveTheKiller/KillerPDF/tree/main/engine/KillerPdf.Engine) or inspect the package with your IDE's object browser.

## Versioning

The package version follows the KillerPDF release that ships it. Pin the version your application has tested. Review the [engine changelog](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/CHANGELOG.md) before upgrading.
