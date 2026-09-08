# KillerPDF 1.9.0 rendering validation

Measured September 7, 2026. Repository checkpoint: `fa206ba`, with tuning product
code recorded at `604aa27`. No product code changed during this validation.

## Tests

The five `SidebarPageBindingTests` passed first in a focused run. The full engine
suite then passed 3,113 tests and the full app suite passed 338 tests, with zero
failures or skips. Commands used `dotnet test` with `--no-restore` against each
existing test project. The previous WPF FontCache exception did not reproduce.
This establishes a successful current run, not the root cause of the old failure.

## Identical-input comparison

Both builds processed the same 600 successful files from the earlier conformance
comparison. Inputs were hard-linked into a new scratch tree and never modified.
Page one was rendered at a maximum dimension of 1024 pixels. Each build received
one warmup and three measured runs, alternating build order. PNG output and logs
were retained. Tests and other validation renders did not run concurrently with
the timing passes. Ordinary desktop background activity was not controlled.

| Median of three measured runs | PDFium 1.8.5 | Engine 1.9.0 |
| --- | ---: | ---: |
| Render time | 11.634 s | 11.593 s |
| Process wall time | 21.989 s | 22.650 s |
| Peak working set | 620.3 MiB | 808.0 MiB |

Every run rendered 600 pages with no skips or failures. The ratio of median
render times is 0.9965. Whole-pass time is 3.0% longer and peak working set is
30.3% higher. Wall time includes opening, rendering, PNG encoding, and process
startup/exit. Process completion was polled every 100 ms, so wall readings can
include up to roughly one polling interval of observation delay. Peak working
set was read from the process's peak counter during those polls; final unseen
activity can still be missed. This is resident process memory, not live managed
heap size or an allocation count.

The result supports rendering parity on this input set. It does not prove that
the remaining wall-time gap is JIT, nor that the engine matches PDFium on every
workload. Raw runs: [Shared-results.csv](Shared-results.csv).

## Broader rendering checks

The union of the prior text, shading, and worst-file lists contained 40 files.
Up to three pages per file produced 74 pages at each of 512 and 2048 pixels.
Both builds completed all pages at both sizes without failures or skips.

| Single diagnostic pass | PDFium 1.8.5 | Engine 1.9.0 |
| --- | ---: | ---: |
| 512 px render time | 2.911 s | 6.226 s |
| 512 px wall time | 3.937 s | 7.421 s |
| 512 px peak working set | 136.8 MiB | 344.0 MiB |
| 2048 px render time | 4.854 s | 13.300 s |
| 2048 px wall time | 10.101 s | 18.566 s |
| 2048 px peak working set | 267.2 MiB | 746.4 MiB |

These runs were not warmed or repeated and were deliberately selected for
expensive content. They locate follow-up work, not a general performance ratio.
Raw runs: [Broad-results.csv](Broad-results.csv). Input identifiers and hashes:
[Broad-inputs.csv](Broad-inputs.csv).

Numerical comparison found 89 of 148 page pairs had dimensions differing by one
pixel in one or both axes. Those pairs were excluded from direct pixel statistics
instead of resizing either output. Geometry rounding needs a separate comparison.
The remaining 59 pairs were compared in RGB with mean channel differences and the
percentage of pixels having any channel difference above 16. These are triage
metrics, not pass/fail tolerances. See [Broad-pixel-comparison.csv](Broad-pixel-comparison.csv).

Visual inspection established:

- `GWG1610_Softmasks_Text_part1_X4.pdf`, page 1: dark rectangular backgrounds
  around the engine's text effects. Its supplied reference row also exposes the
  mismatch. The retained earlier engine shows the same visible problem.
- `064034.pdf` in `govdocs1-error-pdfs/error_set_2`, page 1: materially different
  background color and distorted colors in the antelope photograph. The retained
  earlier engine shows the same problem. Color conversion and image decoding
  need isolation before selecting a fix.
- `CompactedPDFSyntaxTest.pdf`, page 1: the engine paints translucent shapes
  where PDFium shows opaque shapes. The intended behavior needs confirmation
  against the file's operands before calling either rendering correct.
- `GWG061_Shading_x1a.pdf` at 2048 px: differences remain between some rendered
  shading patches and their embedded reference images. No gradient quantization
  was introduced or proposed as a remedy.
- `facct2025-final168.pdf`, page 2 at 512 px: the inspected text columns and page
  layout are present in both outputs, with visible text rasterization differences.

The retained earlier engine also rendered these 74 pages at 512 px. Eight pages
were byte-identical in RGBA to the current engine, and 66 differed. Because the
glyph cache intentionally changes text-edge coverage, this is not an exact-pixel
regression gate. Only the two inspected defects above were established as present
in both engine payloads; the other differences have not all been classified.

At 2048 px the largest per-page render-time excess was page 2 of
`Ghent_PDF-Output-Test-V50_ALL_X4.pdf`: 1,217 ms for the engine versus 143 ms for
PDFium. Follow-up profiling should include this page, image-heavy posters,
Altona color pages, and `balloon_a1b_jp2k.pdf`.

## Startup and interactive limits

The existing `build/measure-startup.ps1` measured five fresh processes for each
build, with its process launch set to hidden for this diagnostic invocation.
No runtime settings were changed. The engine batch ran before the PDFium batch;
these startup runs were not alternating.

| Process-to-ready marker | PDFium 1.8.5 | Engine 1.9.0 |
| --- | ---: | ---: |
| First observed launch | 2,904.5 ms | 2,619.7 ms |
| Median of four subsequent launches | 2,119.0 ms | 2,387.2 ms |

The marker records the beginning of the window reveal, not a completed first-page
render or the end of its animation. These measurements do not establish cold-disk
startup: no reboot or operating-system cache flush was performed. First-page
display latency, scrolling frame times, and sustained interactive memory remain
unmeasured. Native UI control was unavailable in this task. Raw traces were
summarized by the existing script into [startup-engine.csv](startup-engine.csv)
and [startup-baseline.csv](startup-baseline.csv).

## Build identity and retained evidence

The 1.8.5 payload is stored under the locally named `KillerPDF-1.8.4` directory;
its executable reports product version 1.8.5. The engine executable reports 1.9.0.
Executable hashes alone do not identify all code in these loose-file builds:
the retained earlier engine shares the current engine's apphost hash while its
application DLL differs. The following DLL hashes distinguish the measured files.

| File | SHA256 |
| --- | --- |
| 1.8.5 KillerPDF.exe | `16A0E080FEEE71D4AEE06285C7D99B4158265F2F632578511992454EDDD7BB68` |
| 1.8.5 KillerPDF.dll | `BDA8460C2AC194D705C1D3D06D36B29190FBC27F65B299E076741F9173E9E551` |
| 1.9.0 KillerPDF.exe | `93B53A97AD8B1853699B5A55C00565FA985E887AE031D99C02207DE4BE6693B4` |
| 1.9.0 KillerPDF.dll | `02869794585F77846CDBDCB28C9AE367863855F3D84FA7BFE122A29901F1714D` |
| Retained earlier KillerPDF.dll | `90F92D9E664B83C12378F8A81B3790EFFD1958C34254A8FE937425340205EF46` |

Full PNG output, CLI logs, scratch measurement scripts, and the identical-input
manifest remain locally at `C:\Users\steve\killerpdf-benchmark\validation-20260907`.
The earlier payload's exact source commit was not re-established, so its hash
and observed output are the evidence used here.

## Next work

1. Isolate soft-mask compositing and image/color conversion defects with small
   reproductions before changing renderer behavior.
2. Profile retained and temporary memory on the shared set and the 2048 px
   difficult pages; distinguish live caches, pooled buffers, and application copies.
3. Add first-page completion and scrolling measurements in a controlled UI session.
4. Revisit the [band-parallel plan](../../../engine/docs/band-parallel-rendering-plan.md)
   after correctness and memory baselines are satisfactory. The plan is complete
   as a proposal; implementation remains separate work.
