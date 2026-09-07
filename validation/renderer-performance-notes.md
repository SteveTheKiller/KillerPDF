# Renderer performance investigation

Research date: 2026-09-07. These are implementation leads, not measured promises.
The current paired conformance comparison still favors PDFium 1.8.5 by a render
ratio of 1.1352. See [the measured results](PERFORMANCE.md), including run variation.

## Techniques in other implementations

- [OpenJPEG's wavelet reconstruction](https://github.com/uclouvain/openjpeg/blob/master/src/lib/openjp2/dwt.c)
  processes groups of columns with SIMD, including architecture-specific vector
  paths. This suggests batching independent samples after improving memory
  locality. The engine's balloon profile identified 9/7 synthesis as a major cost.
- [libjpeg-turbo](https://github.com/libjpeg-turbo/libjpeg-turbo) uses SIMD for
  JPEG compression and decompression and optimized Huffman coding. Our managed
  JPEG transform now batches independent output samples without changing the
  reconstruction formula. The focused record below measures this change.
- [PDFium's image decoder](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/core/fpdfapi/page/cpdf_dib.cpp)
  selects a decode resolution from the required output size. Our renderer already
  selects JPEG reductions and JPEG 2000 resolution levels. More aggressive
  reduction needs independent visual evidence, especially for fine detail.
- [MuPDF's rendering model](https://mupdf.readthedocs.io/en/latest/reference/c/overview.html)
  supports display lists, shared resource caches, and concurrent rendering of
  independent bands. Our renderer already caches parsed instructions, font
  resources, glyph paths, decoded images, and rendered pages. A resolved display
  list could avoid additional interpretation during zooming, but is not proven
  to improve first-page collection throughput.

## Source review and measured priorities

The expanded reading pass compares selected implementation paths, not entire
repositories. The supplied reading list is a useful inventory, but several paths
and architectural descriptions have changed upstream. In particular, current
[pdf.js JPX](https://github.com/mozilla/pdf.js/blob/master/src/core/jpx.js)
uses OpenJPEG through its WASM image interface, and current
[hayro](https://github.com/LaurenzV/hayro/blob/main/hayro/src/lib.rs) renders through
Vello CPU. Neither should be described as a current pure-JavaScript JPX decoder
or a tiny-skia rendering pipeline, respectively.

### Workload evidence

Per-file medians from the three existing `1.9.0-rect-mask` passes identify these
large positive differences. These medians are diagnostic rankings; their sum is
not the median of the complete collection. Settings remain page 1 at 1024 pixels.

| File | Engine ms | PDFium ms | Difference ms |
| --- | ---: | ---: | ---: |
| balloon_a1b_jp2k.pdf | 423 | 167 | 256 |
| altona_technical_1v2_x3.pdf | 370 | 163 | 207 |
| UnknownFilter/4387ba48...pdf | 215 | 37 | 178 |
| UnknownFilter/38225227...pdf | 200 | 37 | 163 |
| UnknownFilter-Linearized.pdf | 164 | 3 | 161 |
| PDF-HUL-136/42828.0001.001.pdf | 396 | 235 | 161 |
| altona_measure_1v1a.pdf | 202 | 69 | 133 |
| CompactedPDFSyntaxTest.pdf | 123 | 3 | 120 |

A new sampled-thread-time trace of 15 fresh-document renders of the
`4387ba48...pdf` file changes the initial recovery hypothesis. Coverage-mask
intersection has 20.63% exclusive samples; CCITT decoding has 19.35% inclusive
samples, including 5.32% exclusive in its two-dimensional decoder. PNG unfiltering
has another 4.98% exclusive. GC polling and lock frames overlap these execution
paths and must not be added to them as independent costs. The trace includes
first use and tiered compilation; these percentages are experiment-selection
evidence, not a predicted collection speedup.

The scratch harness uses engine SHA256
`C7D9701371C9F77F15A06E6269D0F25E9330ADF269ECFEA21BC0EC72135C8AD2`,
fresh documents and renderers, annotations and forms enabled, and 15 iterations.
The trace is `unknownfilter-source-study-20260907.nettrace` under the local
`kp-bench-render` directory. All 15 untraced output pixel hashes agree:
`B02B4805C6718C7976AF022901AAA9609D28D785A030F7EE7F4DD97729B7D723`.
Opening falls below 0.5 ms after first use while rendering changes substantially
during warmup. Therefore the file's directory name does not establish recovery
as its bottleneck.

### Ranked implementation experiments

1. **Avoid unnecessary clip-mask work.**
   [Splash clipping](https://github.com/innodatalabs/poppler/blob/master/splash/SplashClip.cc)
   distinguishes fully inside, fully outside, and partially clipped spans, and
   represents rectangular clipping separately. Our rectangle rasterization fast
   path already exists, but `CoverageMask.Intersect` still allocates and loops
   over pixels whenever either operand has dense coverage. First test containment
   by a fully covered rectangle, row copies for rectangular crops, and exact
   integer SIMD multiplication for two dense masks. Preserve `(a*b+127)/255` and
   never treat intersecting an antialiased mask with itself as a no-op. Check
   ownership before sharing any coverage array. The new trace directly supports
   this experiment.
2. **Decode CCITT runs with lookahead tables and paint whole bytes.**
   [libtiff's fax tables](https://github.com/libsdl-org/libtiff/blob/master/libtiff/tif_fax3.h)
   provide a reference for table-driven fax decoding. Our `CodeTable.Read` and
   two-dimensional mode reader deserve a focused comparison against a bounded
   peek/consume reader. Partial final bytes, invalid prefixes, fill bits, both
   polarities, and Group 3/4 modes must retain existing behavior. Measure decoded
   byte identity before page rendering. This is a stronger immediate lead than
   another broad JPEG transform change for the traced file.
3. **Cache glyph coverage, not only flattened outlines.**
   [PDFium](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/core/fxge/cfx_glyphcache.cpp)
   has separate glyph path and bitmap caches.
   [MuPDF draw-glyph](https://github.com/ArtifexSoftware/mupdf/blob/master/source/fitz/draw-glyph.c)
   keys rasterized glyphs with font, glyph, transform and subpixel information.
   [Skia strikes](https://github.com/google/skia/blob/main/src/core/SkStrikeCache.cpp)
   have memory/count budgets and LRU eviction.
   [FreeType](https://github.com/freetype/freetype/blob/master/src/cache/ftcbasic.c)
   provides both image and small-bitmap cache interfaces. Our `_glyphPathCache`
   avoids repeated flattening, but `ShowOutlineText` still transforms points and
   rasterizes every occurrence. Count reusable device-space glyphs first. An
   exact cache must preserve fractional placement, transforms and fill/stroke
   semantics; coarse subpixel quantization is a quality change, not a free cache
   optimization. This can help the first rendering of a page, unlike a cache
   that only helps revisiting that page.
4. **Specialize painting outside the pixel loop.**
   [tiny-skia's pipeline](https://github.com/RazrFalcon/tiny-skia/blob/master/src/pipeline/mod.rs)
   selects compatible stages before running spans and provides separate high-
   and low-precision paths. Its own tests note rounding differences. Apply the
   architectural idea to common opaque, normal-blend and masked cases, but retain
   our exact arithmetic and a general fallback. Existing constant-color blend
   reuse already captures one portion of this opportunity.
5. **Reduce work and buffering inside codecs.**
   [libjpeg-turbo's coefficient controller](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/main/src/jdcoefct.c)
   distinguishes single-pass MCU-row output from buffered multi-scan processing.
   Investigate baseline JPEG row processing separately from progressive decoding.
   [OpenJPEG Tier-1](https://github.com/uclouvain/openjpeg/blob/master/src/lib/openjp2/t1.c)
   also exposes codeblock-level work beyond the wavelet SIMD already implemented.
   Re-profile balloon before choosing entropy decoding, wavelet work, color
   conversion, or first-use compilation as the next target.

### Leads that need qualification

- A shared cache budget is a memory-management option, not a demonstrated speed
  gain. Our font and stream cache keys already use `ReferenceEqualityComparer`;
  expensive structural dictionary hashing is not the identified problem.
- [MuPDF compressed streams](https://github.com/ArtifexSoftware/mupdf/blob/master/source/fitz/compressed-buffer.c)
  compose decompression and predictor stages and pass a reduction factor to JPEG.
  [PDFBox PageDrawer](https://github.com/apache/pdfbox/blob/trunk/pdfbox/src/main/java/org/apache/pdfbox/rendering/PageDrawer.java)
  optionally selects subsampling from the image transform. Our JPEG/JPX resolution
  selection is already implemented. Row streaming and reducing intermediate
  arrays remain useful questions, but simply adding lazy decode is not new here.
- [pdf.js evaluator](https://github.com/mozilla/pdf.js/blob/master/src/core/evaluator.js)
  resolves resources while building operator lists. Compare repeat-page and zoom
  workloads separately from fresh-document throughput before expanding our
  instruction caches into a resolved display list.
- [MuPDF object loading](https://github.com/ArtifexSoftware/mupdf/blob/master/source/pdf/pdf-xref.c)
  remembers failed object-stream reads. Our missing-reference scanner and failed
  object-stream handling merit counters, but the new profile does not identify
  them as the principal cost of `4387ba48...pdf`.
- [libdeflate](https://github.com/ebiggers/libdeflate/blob/master/lib/deflate_decompress.c)
  specializes whole-buffer decompression with word-sized bit buffers, Huffman
  tables and match copies. [miniz](https://github.com/richgel999/miniz/blob/master/miniz_tinfl.c)
  is another table-driven reference. Our Flate path uses `ZLibStream`, whose
  [.NET inflater](https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/Inflater.cs)
  wraps a native ZLib API. A managed rewrite would compete with native decoding;
  the research does not justify replacing it or promise that pooling alone wins.
- [jbig2dec](https://github.com/ArtifexSoftware/jbig2dec/blob/master/jbig2_generic.c)
  reuses arithmetic contexts and sliding row windows for nominal templates,
  retaining general handling for other adaptive positions. Prioritize this only
  after a JBIG2-heavy profile, not from its algorithmic appeal alone.

### Reading coverage

Implementation paths examined in this pass include MuPDF, PDFium, pdf.js,
PDFBox, the Poppler Splash mirror, Skia, tiny-skia, FreeType, libdeflate,
miniz, OpenJPEG, libjpeg-turbo, jbig2dec, PDFsharp and the .NET inflater.
Additional entry points were inspected for hayro, stet, PdfPig and its Skia
renderer, fontdue, ttf-parser, stb_image, Rust jpeg-decoder, Grok, zune-jpeg,
Blend2D, and iszak/jpeg2000. Entry-point inspection is not a completed hot-loop
review. The Poppler mirror is historical evidence, not a claim about its latest
release. Pin upstream revisions before an implementation experiment relies on
source-specific details.

Further targeted reading from the supplied inventory remains for xpdf,
Ghostscript banding, Vello CPU internals, Krilla, resvg, zlib-ng, fpng/fpnge,
SharpZipLib, ab_glyph, skrifa/fontations, HarfBuzz, OpenType.js, fonttools,
stb_truetype, font-rs, Cairo/pixman, and the remaining codec hot loops. These are
not being credited with measured benefits until linked to an engine workload.

## Other ongoing experiments

1. Extend the [paired codec SIMD comparison](records/codec-simd-1.9.0.json)
   beyond the conformance set. The engine remains slower than PDFium overall.
2. Separate startup and first-use compilation from warmed JPEG 2000 rendering.
   Grouped column synthesis is verified, but the larger isolated warmed gain
   does not appear in the balloon collection timing.
3. Profile remaining source and decoded-image retention. Block-buffered Flate
   decoding and document-owned stream input reduce median conformance peak
   memory from 1,089.5 to 766.1 MB; complete source and decoded arrays remain.
   See the [stream input record](records/stream-open-1.9.0.json).
4. Profile remaining paint costs after exact axial color and normal alpha-blend
   reuse and compact rectangular masks. The matrix page's focused allocations
   fall from 174.2 to 58.0 MB, but compact syntax still has a large first-use gap.
   See the [rectangle-mask record](records/rect-mask-1.9.0.json).

Track decode time, complete render time, allocation, process memory, and startup
separately. A local improvement must survive representative paired workloads.
Parallel rendering requires a separate memory and thread-safety evaluation.
Existing libraries provide design evidence; these experiments do not require
copying their implementation or adding another dependency.
