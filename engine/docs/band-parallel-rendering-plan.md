# Band-parallel rendering plan

Status: proposed implementation plan. No parallel rendering is enabled by this document.

## Objective

Reduce large-page rendering latency in the real application while preserving the current
serial renderer's pixels, diagnostics, cancellation, and resource limits. The current
rendering-parity checkpoint is the reference. Do not introduce shading sample tables,
additional glyph quantization, runtime configuration changes, or new dependencies.

## Current constraints

`PdfPageRenderer.RenderUncached` allocates a full BGRA page and interprets content in order.
Its nested processing functions also render appearances, soft masks, and transparency groups.
`RasterFrame.PixelY` uses the full page height. Replacing that height with a band height would
move content and change sampling coordinates.

`CoverageMask` already stores a bounded rectangle, but text clipping and graphics soft masks
still use page-sized arrays. `KnockoutState` stores per-pixel object identifiers and may copy
a full backdrop. Transparency processing temporarily switches the target pixel buffer.
Each of these must gain explicit surface bounds and an origin before band execution.

The bounded caches protect dictionary operations with locks, but execute value factories
outside those locks. That permits duplicate work and does not establish that all parsing,
font resolution, document access, or cached values are safe to use concurrently. Audit those
dependencies before sharing prepared data. `PdfPageRenderSession` currently copies the final
pixel buffer, which must remain in total-memory measurements.

## Implementation sequence

1. **Separate page coordinates from storage coordinates.** Introduce an internal surface
   description containing full page dimensions, storage stride, and owned row range. Preserve
   all transforms and sample positions in full-page coordinates. Keep the public API and the
   serial path unchanged. Verify exact pixels before proceeding.
2. **Render bands sequentially first.** Replay the same ordered operations into one owned band
   at a time. Restrict writes, not the geometry used to establish winding, stroke joins, or
   antialias coverage. Preserve contributions from edges outside a band's rows. Compare the
   assembled page byte-for-byte with the full-surface path at many split positions.
3. **Make temporary surfaces band-aware.** Include text clips, image and graphics soft masks,
   group backdrops, knockout identifiers, annotations, and form appearances. A group must see
   the same per-pixel backdrop and painting order as serial rendering. Until a feature is
   verified, select the full serial page path before starting work on that page.
4. **Prepare and audit shared inputs.** Resolve instruction streams, resources, fonts, and
   decoded images with explicit ownership. Share immutable results only. Give each band its
   own graphics stack, rasterizer, diagnostics accumulator, and rented mutable buffers.
   Account for duplicated cache factories and prepared-data memory before choosing this
   design over isolated renderer instances.
5. **Add bounded scheduling.** Start with an internal experimental path and one or two workers.
   Limit workers by both available CPU and an explicit temporary-memory budget. Coordinate
   with concurrent page renders in the viewer so pages do not each consume all processors.
   Small pages should retain serial execution when scheduling overhead outweighs the gain.
6. **Join safely.** Each worker writes disjoint final rows. Do not cache or publish a page until
   all bands complete. Cancellation or failure cancels siblings, waits for their exit, and
   returns every owned buffer exactly once. Combine diagnostics deterministically. Do not
   silently swallow failures or return a partially rendered page.
7. **Enable only after the gates below pass.** Keep a serial reference mode for reproduction
   and a predictable fallback for unsupported page features or insufficient memory budget.

## Memory budget

The final output costs `4 * width * height` bytes regardless of worker count. A band of
`height / workers` rows should own only its share of coverage, group, mask, and knockout
storage. Nested groups multiply temporary storage, so worker count cannot be chosen from
CPU count alone. Count actual rented-array lengths, decoded images, prepared instructions,
cached pages, and the application's output copy. Do not allocate a full-page temporary
buffer independently for every worker.

## Correctness gates

- Exact output against the current serial engine with 1, 2, and 4 workers; odd page sizes;
  narrow bands; and splits through glyphs, strokes, images, and gradient transitions.
- Nonzero and even-odd fills, dashed joins, fractional text placement, transformed clips,
  page rotations and crop origins, tiling patterns, axial/radial/mesh shadings, image masks,
  isolated and non-isolated groups, supported knockout behavior, and nested soft masks.
- Transparent and opaque output; annotations and forms enabled and disabled; cache hits,
  eviction, repeated renders, cancellation, malformed input, and worker failure.
- The same diagnostics and exception behavior as the serial path. Full engine and app suites
  must pass, followed by multipage corpus comparisons at several output resolutions.

## Performance gate

Benchmark the real app with identical inputs and output work, alternating serial and parallel
runs with warmups. Record render time, full wall time, first-page latency, memory, and cold
process startup separately. Include small text pages, expensive shadings, large images, and
several pages queued together. Report all measured runs, including regressions.

A proposed promotion target is a repeatable improvement larger than machine noise, preferably
at least 10% on the intended large-page workload, without a material regression on small pages
or unbounded memory growth. This is an experiment's acceptance target, not a speed promise.
Do not describe band rendering as a proven way to beat PDFium until those measurements exist.
