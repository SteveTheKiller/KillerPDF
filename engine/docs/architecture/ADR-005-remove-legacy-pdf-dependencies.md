# ADR-005: Remove legacy PDF processing dependencies after engine replacement

**Status:** Accepted
**Date:** 2026-09-06
**Decider:** Steve the Killer

## Context

KillerPDF previously depended on several separate PDF libraries across its desktop pipeline. PdfSharpCore owned document state and save operations, PDFsharp supported detached signing, PdfPig supplied text extraction, and Docnet.Core with its bundled PDFium native library supplied page rendering. The split gave the application proven capability while The KillerPDF.Engine was being built, but it also created overlapping document models, managed and native dependency chains, different parser and rendering behavior, and separate update and security responsibilities.

ADR-001 established an independent .NET 10 document engine. ADR-002 chose an incremental migration so each replacement could be tested without combining every PDF behavior change into one unreviewable cutover. That migration is now complete for the desktop application: the engine provides document parsing, text extraction, structural editing, writing, signing, and bounded CPU rendering. The final rendering migration removed Docnet.Core and the PDFium native runtime from the application package.

The goal is not to eliminate every dependency. Some focused dependencies remain where they provide capabilities outside the legacy PDF processing stack, including OCR, JPEG 2000 decoding, ONNX Runtime, cryptographic platform support, fonts, and build tooling. The decision concerns the retired PDF document, extraction, signing, and rendering libraries only.

## Decision

KillerPDF will use The KillerPDF.Engine as its sole PDF processing boundary for the Windows application. The application will not retain direct references to PdfSharpCore, PDFsharp, PdfPig, Docnet.Core, or PDFium.

The engine owns PDF parsing, text and image extraction, document editing, deterministic and incremental writing, detached signing, and page rendering. The WPF shell owns user interaction, display composition, file workflows, and platform-specific OCR bootstrapping, but it passes PDF work through the engine's typed APIs rather than reaching into a legacy library or native renderer.

Do not add a retired dependency back as a silent fallback. A proposed fallback or replacement must identify the unmet engine capability, its user impact, its licensing and security cost, its package and native payload impact, and the validation that proves it is necessary. It requires a new architecture decision before becoming a production dependency.

## Options considered

### Retain the legacy libraries as fallbacks

Keeping the old implementations would offer a familiar escape path for edge cases, but it would preserve duplicate PDF models and native payloads. It would also make output behavior dependent on which path happened to run, reducing the value of engine-focused validation and leaving each dependency in the security maintenance scope.

### Replace one dependency at a time but retain the rest indefinitely

This lowered the risk of each migration slice and was the correct transitional approach under ADR-002. It is not the final architecture because the remaining libraries still impose duplicate APIs, package weight, update work, and unclear ownership boundaries.

### Remove the legacy PDF stack after equivalent engine coverage is validated

This produces one maintained PDF processing boundary, removes the PDFium native runtime and the managed wrappers that depended on it, and makes validation results attributable to one implementation. It requires strong focused, corpus, package, and launch testing before each removal. This is the selected option.

## Consequences

- KillerPDF has one PDF processing implementation to test, audit, package, and evolve.
- The application package no longer carries Docnet.Core or the PDFium native runtime.
- PDF behavior fixes and capability work belong in The KillerPDF.Engine unless they are strictly WPF interaction or presentation concerns.
- An engine regression has no production legacy fallback. It must be corrected in the engine, covered by a focused regression test when practical, and revalidated against the applicable corpus gate.
- Security review can focus on the remaining declared dependencies and vendored assets instead of maintaining retired PDF libraries and native binaries.
- Removing a dependency does not prove visual equivalence by itself. Rendering, extraction, editing, signing, packaging, and launch gates remain required evidence for their respective surfaces.

## Implementation requirements

1. Keep the Windows application free of direct package, project, P/Invoke, and runtime-file references to PdfSharpCore, PDFsharp, PdfPig, Docnet.Core, and PDFium.
2. Keep the engine UI-free and independent of WPF, KillerPDF application code, PDFium, PdfPig, PdfSharpCore, and PDFsharp.
3. Validate a dependency removal with focused replacement tests, the applicable engine and application test suites, corpus gates, package-manifest inspection, and a clean launch check.
4. Preserve the distinction between a deliberate engine limitation and an unexpected failure in corpus and release results. Do not reintroduce a legacy fallback merely to hide either result.
5. Record any future exception as a new ADR before adding a replacement PDF processing dependency or native renderer.
