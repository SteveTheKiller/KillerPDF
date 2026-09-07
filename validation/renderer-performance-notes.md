# Renderer performance investigation

Research date: 2026-09-07. These are implementation leads, not measured promises.
The current paired conformance comparison still favors PDFium 1.8.5 by a render
ratio of 1.2544. See [the measured results](PERFORMANCE.md).

## Techniques in other implementations

- [OpenJPEG's wavelet reconstruction](https://github.com/uclouvain/openjpeg/blob/master/src/lib/openjp2/dwt.c)
  processes groups of columns with SIMD, including architecture-specific vector
  paths. This suggests batching independent samples after improving memory
  locality. The engine's balloon profile identified 9/7 synthesis as a major cost.
- [libjpeg-turbo](https://github.com/libjpeg-turbo/libjpeg-turbo) uses SIMD for
  JPEG compression and decompression and optimized Huffman coding. Our managed
  JPEG transform still calculates output samples individually, making batched
  arithmetic a useful experiment without changing the reconstruction formula.
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

## Next experiments

1. Measure complete page throughput after the verified managed JPEG SIMD change.
   The [focused record](records/jpeg-simd-1.9.0.json) confirms identical samples
   and a selected decode improvement, but does not prove overall superiority.
2. Evaluate multi-column JPEG 2000 synthesis, including odd origins, short edge
   tiles, integer and floating-point transforms, and bounded temporary storage.
3. Profile the largest remaining non-codec gaps, including DeviceN shadings and
   compact syntax pages, before choosing a broader rendering change.

Track decode time, complete render time, allocation, process memory, and startup
separately. A local improvement must survive representative paired workloads.
Parallel rendering requires a separate memory and thread-safety evaluation.
Existing libraries provide design evidence; these experiments do not require
copying their implementation or adding another dependency.
