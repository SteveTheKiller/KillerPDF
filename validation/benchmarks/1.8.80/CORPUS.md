# KillerPDF 1.8.80 corpus benchmark

Test date: 2026-10-04

KillerPDF 1.8.80 reproduced every 1.8.71 file outcome and diagnostic detail. Each version received one warmup and five measured passes in alternating order. Every paired pass agreed on every file's path, status, diagnostic detail, and exit code. The damaged-file gate had zero crashes and zero timeouts.

## Results

| Collection | Inputs | Saved | Skipped | Save failures | 1.8.80 median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,171 | 1,233 | 292 | 236.493 |
| Standards and color | 649 | 377 | 235 | 37 | 5.785 |
| Private stress | 29,599 | 25,015 | 2,957 | 1,627 | 563.538 |
| Damaged-file safety | 80 | 0 | 78 | 2 | Not timed as a batch |

The three normal collections contain 46,944 inputs, with 40,563 saved, 4,425 skipped, and 1,956 save failures per pass. Including the separate safety collection gives 47,024 inputs.

These are hostile collections, not a zero-failure workload. All normal batch processes returned exit code 1 because their logs contain save failures. The runner completed normally; this result is based on the recorded per-file results, not on treating the batch exit code as success.

## Comparison with 1.8.71

| Collection | 1.8.71 median seconds | 1.8.80 median seconds | Change |
| --- | ---: | ---: | ---: |
| Public regression | 234.392 | 236.493 | 0.90% longer |
| Standards and color | 5.765 | 5.785 | 0.35% longer |
| Private stress | 566.869 | 563.538 | 0.59% shorter |

All timing changes are below the 10% investigation threshold. Every path, status, diagnostic detail, and exit code matches 1.8.71 across the paired warmups and five measured passes. Damaged-file results also match exactly: 78 skips and two reported failures, with no missing logs, crashes, or timeouts.

## Builds and method

- Baseline source commit: `25244f0ed8300caefd63b0af68416855bf8ca93e`.
- Candidate source commit: `a3f18871d4f9bbaa6ab694d3f2cba9b28fadee97`.
- Baseline: KillerPDF 1.8.71 Release, win-x64.
- Baseline executable SHA-256: `32AB58753963B4EBEA01EC26558196C53E5E2416246F59B422A6D82013A41424`.
- Candidate: KillerPDF 1.8.80 Release, win-x64.
- Candidate executable SHA-256: `99F783290FEC96900C6EFCA931DE1A8C67577E6C6CCF3332E22840B3F6A4226C`.
- Runner: `validation/Benchmark-Versions.ps1`, using the existing local collections with downloads disabled.
- One warmup and five measured passes per version and normal collection, with run order alternated.
- All 80 damaged inputs tested individually with a 30-second timeout.
- Full per-file logs remain outside the application repository at `C:\Users\steve\kp-bench-render\release-1.8.80-vs-1.8.71-alternating-20261004`.

The committed [run data](benchmark-runs.csv) and [summary](benchmark-summary.csv) preserve the release-level measurements. The previous published corpus record is [KillerPDF 1.8.70](../1.8.70/CORPUS.md).

This run tests batch opening and saving plus malformed-input process safety. It does not replace interactive rendering checks or a separate qpdf/veraPDF before-and-after conformance sweep.
