# KillerPDF 1.9 engine progress: testing and ideas welcome

I'm preparing an early testing milestone for KillerPDF 1.9 and its own PDF engine.

Recent work includes antialiased rendering, faster clipping and image decoding,
lower-allocation document loading, improved font and form handling, and bounded
recovery that preserves later content when individual objects are damaged.

On the 649-file conformance set, the engine renders 614 files, skips 35 and has
zero failures. The PDFium 1.8.5 comparison renders 600, skips 41 and fails on eight.
These checks use page one at a maximum dimension of 1024 pixels. Opening without
an exception doesn't prove that every part of a page is correct.

The latest paired run measures 13.26 seconds of engine render time versus 11.71
seconds for PDFium on the 600 files both accept, about 13% slower. Whole-pass wall
time is 27.18 versus 22.24 seconds. Startup and memory use remain improvement targets.
The [benchmark records](https://github.com/SteveTheKiller/KillerPDF/blob/dev/1.9-overkill/validation/PERFORMANCE.md) include the measurements and limitations.

The preview checks also caught and corrected an OCR fallback failure. All 3,068
engine tests and 338 app tests pass. There's still work on visual differences,
broader corpus coverage and held-out OCR accuracy.

I'd like ideas from anyone interested in PDF rendering, codecs, fonts, caching
or performance profiling. Specific algorithms, source references, benchmark
methods and difficult PDFs would help. I'm studying other open-source
implementations for ideas and building improvements into this engine.

For testing, use `dev/1.9-overkill` and follow the
[testing guide](https://github.com/SteveTheKiller/KillerPDF/blob/dev/1.9-overkill/validation/ENGINE-PREVIEW.md). Include your commit, the page number, what
looks wrong or feels slow, and which viewer handles it correctly. Share a sample
you have permission to distribute when possible.

This is early 1.9 development. The goal is broad compatibility and accurate
rendering, with performance that eventually beats the 1.8.x path.
