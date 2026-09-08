# KillerPDF 1.9.0 rendering correctness and memory

Measured September 7, 2026. This follows the
[rendering validation](../1.9.0-render-validation/README.md), which remains a
historical record of the earlier build.

## Correctness

Commit `a95eb6b` preserves an outer soft mask when a non-isolated transparency
group resets its internal mask. Four regression cases cover the reset, an
internal multiply blend, transparent backdrops, and overlapping objects. The
dark rectangles in the Ghent soft-mask text page are gone in the real app.

Commit `3a53463` preserves shadow detail in unprofiled CMYK conversion. Two
regressions cover mixed inks in paths and images and a cyan ramp with black ink.
The JPEG decoder's samples agreed closely with a reference decoder after
accounting for its inverted CMYK convention; the conversion was clipping dark
channels. The affected Angola report photograph visibly improves. Against the
PDFium image, mean absolute RGB error in the inspected photo band falls from
26.07 to 20.19 on a 0 to 255 scale. This is supporting evidence for that crop,
not a perceptual quality score or proof of color accuracy. ICC color management
remains outside this change, and profile-dependent color differences remain.

The corrected app rendered all 74 pages of the existing 40-file sample at
512 pixels without failures. The full engine suite passes 3,119 tests.

## Batch export memory

Commit `00e71a2` sends read-only engine pixels directly to PNG encoding. Other
application render callers retain independent mutable pixel buffers. Tests
verify encoding a nonzero-offset slice without modifying its source and
rejecting an undersized slice. All 340 application tests pass.

The same 40-file sample produced 74 pages at 2048 pixels per run. Three
alternating runs compare the corrected CMYK build with the export change.
Both payloads were already built and exercised; there was no separate timed
warmup in this comparison. No tests or builds ran during timing. Peak working
set was sampled every 100 ms using the process high-water counter.

| Median | Before export change | After export change |
| --- | ---: | ---: |
| Peak working set | 783.2 MiB | 590.3 MiB |
| Render time | 12.901 s | 12.887 s |
| Whole-pass wall time | 17.794 s | 17.801 s |

Peak working set fell 24.6% on this sample. Timing is unchanged within machine
noise. Every run completed all 74 pages without failures. All 74 corresponding
decoded PNGs in the first run are pixel-identical. This is an engine-to-engine
comparison; it does not replace the earlier PDFium timing comparison or claim
the difficult sample now matches PDFium's speed. See the
[six raw runs](export-broad2048.csv).

## Final shared-set verification

The final build includes both allocation changes. Three alternating runs at
1024 pixels rendered all 600 shared files successfully in every pass. Both
payloads had already been exercised; no separate warmup was timed. All 600
decoded output PNGs in the first run are pixel-identical to the corrected CMYK
build. Together with the high-resolution comparison, that verifies 674 page
outputs after the allocation changes.

| Median | Corrected CMYK build | Final build |
| --- | ---: | ---: |
| Peak working set | 785.1 MiB | 770.4 MiB |
| Render time | 11.554 s | 11.373 s |
| Whole-pass wall time | 22.711 s | 22.407 s |

The small timing difference is within machine noise. The shared-set memory
reduction is only 1.9%; the larger 24.6% reduction applies to the 2048-pixel
sample. PDFium was not rerun in this comparison. See the
[six shared-set runs](final-shared1024.csv).

## Profile and limits

A separate allocation and CPU trace of the corrected CMYK build at 2048 pixels
recorded 47 generation-2 collections. Sampled allocations included about
1.71 GiB in coverage-mask sweeping and 755 MiB attributed directly to the page
copy in the application render boundary. Sampled allocation amounts are
estimates, not retained heap sizes. The CPU sample leaders were PNG encoding,
image painting, graphics-state soft masks, and fills. This trace is diagnostic;
its wall time is not included in the timing table.

Commit `c002d37` avoids creating reference-cycle tracking for direct operands;
indirect references retain the same cycle and depth checks. A final trace with
both allocation changes recorded 5,461,253,856 sampled allocated bytes versus
6,410,862,752 before, a 14.8% reduction, with 44 rather than 47 generation-2
collections. Reference tracking attributed to renderer resolution fell from
65,857,480 to 1,380,008 bytes. All 74 final high-resolution PNGs remain
pixel-identical to the corrected CMYK build. Full engine and app suites pass.
See the [profile totals](allocation-profile.csv). A single traced comparison
does not establish a repeatable collection-count or latency improvement.

An experiment extending the compact rectangle path to off-page bounds passed
focused checks but did not improve the real-app run. It was removed. Gradient
quantization, runtime compilation settings, and band-parallel rendering remain
unchanged.

Interactive first-page display and scrolling still require a controlled UI
session. Native UI control was unavailable. The earlier window-reveal startup
measurements are not first-page or scrolling measurements.

## Retained evidence

Raw PNGs, logs, traces, and measurement scripts remain under
`C:\Users\steve\killerpdf-benchmark\correctness-20260907`.
Inputs are the unchanged `input-Broad` and `input-Shared` links from the previous
validation. The scratch scripts retain the run order, output logs, process peak
sampling, and decoded PNG comparisons. No source PDF was modified.

| Application DLL | SHA256 |
| --- | --- |
| Corrected CMYK build | `AE39AE9A5F6921E9B88F347DB12C2C35E4D57065A953357D77EB332D219D3637` |
| Export build | `3FB78239FE0A0B224561EDB7C78BC6A80E3CB38FA45AC739C5C65E1767E137E7` |
| Final build | `507975F354E4A9E7F12E4C94357095F082B7A74E4958671CCFF1BD16EF0CE14F` |
