# KillerPDF 1.8.70 corpus benchmark

Test date: 2026-09-24

KillerPDF 1.8.70 reproduced every 1.8.6 file outcome and diagnostic detail. The warmup and all five measured passes agreed on every file's path, status, diagnostic detail, and exit code. The damaged-file gate had zero crashes and zero timeouts.

## Results

| Collection | Inputs | Saved | Skipped | Save failures | Median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,171 | 1,233 | 292 | 172.706 |
| Standards and color | 649 | 377 | 235 | 37 | 5.583 |
| Private stress | 29,599 | 25,015 | 2,957 | 1,627 | 516.800 |
| Damaged-file safety | 80 | 0 | 78 | 2 | Not timed as a batch |

The three normal collections contain 46,944 inputs, with 40,563 saved, 4,425 skipped, and 1,956 save failures per pass. Including the separate safety collection gives 47,024 inputs.

These are hostile collections, not a zero-failure workload. All normal batch processes returned exit code 1 because their logs contain save failures. The runner completed normally; this result is based on the recorded per-file results, not on treating the batch exit code as success.

## Comparison with 1.8.6

| Collection | 1.8.6 median seconds | 1.8.70 median seconds | Change |
| --- | ---: | ---: | ---: |
| Public regression | 169.360 | 172.706 | 2.0% longer |
| Standards and color | 5.527 | 5.583 | 1.0% longer |
| Private stress | 538.026 | 516.800 | 3.9% shorter |

All timing changes are below the 10% investigation threshold. Every path, status, diagnostic detail, and exit code matches 1.8.6 across the warmup and five measured passes. Damaged-file results also match exactly: 78 skips and two reported failures, with no missing logs, crashes, or timeouts.

## Build and method

- Source commit: `8c774b3c547b09d99e8201bbf22064dd420d227e`.
- Version: 1.8.70 Release, win-x64.
- Executable SHA-256: `A7DC86CA95247BF4345407D362AF4CD676F2F4E0ABE14B5CF51CFB7CED355CFC`.
- Runner: `KillerPDF-Corpus/scripts/benchmark_corpus.ps1`, existing local collections, downloads disabled.
- One warmup and five measured passes per normal collection, run sequentially.
- All 80 damaged inputs tested individually with a 30-second timeout.
- Backups were paused and no other builds or test jobs ran during measured passes.
- Full per-file logs remain outside the application repository at `C:\Users\steve\kp-bench-render\release-1.8.70-preflight-clean-20260924-1630`.

The committed [run data](benchmark-runs.csv) and [summary](benchmark-summary.csv) preserve the release-level measurements. The previous release record is [KillerPDF 1.8.6](../1.8.6/CORPUS.md).

This run tests batch opening and saving plus malformed-input process safety. It does not replace interactive rendering checks or a separate qpdf/veraPDF before-and-after conformance sweep.
