# KillerPDF 1.8.6 corpus benchmark

Test date: 2026-09-22

KillerPDF 1.8.6 produced the same result as the 1.8.4 baseline for every input. No previously successful input became a skip or failure. All five measured passes agreed on every file's status and diagnostic detail. The damaged-file gate had zero crashes and zero timeouts.

## Results

| Collection | Inputs | Saved | Skipped | Save failures | Median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,171 | 1,233 | 292 | 169.360 |
| Standards and color | 649 | 377 | 235 | 37 | 5.527 |
| Private stress | 29,599 | 25,015 | 2,957 | 1,627 | 538.026 |
| Damaged-file safety | 80 | 0 | 78 | 2 | Not timed as a batch |

The three normal collections contain 46,944 inputs, with 40,563 saved, 4,425 skipped, and 1,956 save failures per pass. Including the separate safety collection gives 47,024 inputs.

These are hostile collections, not a zero-failure workload. All normal batch processes returned exit code 1 because their logs contain save failures. The runner completed normally; this result is based on per-file comparison, not on treating the batch exit code as success.

## Comparison with 1.8.4

| Collection | 1.8.4 median seconds | 1.8.6 median seconds | Change |
| --- | ---: | ---: | ---: |
| Public regression | 207.099 | 169.360 | 18.2% shorter |
| Standards and color | 5.635 | 5.527 | 1.9% shorter |
| Private stress | 556.740 | 538.026 | 3.4% shorter |

Every path, status, and diagnostic detail matches 1.8.4 across the warmup and five measured passes. Damaged-file statuses, details, and exit codes also match exactly: 78 skips and two reported failures, with no missing logs, crashes, or timeouts.

## Build and method

- Source commit: `37e080884c841c701c57d90e361304cbbf7518a2`.
- Version: 1.8.6 Release, win-x64.
- Executable SHA-256: `43B4B79B34DBBEB61B0AB7886271A42A0538DA8893D140FD065BEF9B0A95F983`.
- Runner: `KillerPDF-Corpus/scripts/benchmark_corpus.ps1`, existing local collections, downloads disabled.
- One warmup and five measured passes per normal collection, run sequentially.
- All 80 damaged inputs tested individually with a 30-second timeout.
- Backups were paused and no other builds or test jobs ran during measured passes.
- Full per-file logs remain outside the application repository at `C:\Users\steve\kp-bench-render\release-1.8.6-preflight-clean-20260921-2333`.

The committed [run data](benchmark-runs.csv) and [summary](benchmark-summary.csv) preserve the release-level measurements. The comparison baseline is [KillerPDF 1.8.4](../1.8.4/CORPUS.md).

This run tests batch opening and saving plus malformed-input process safety. It does not replace interactive rendering checks or a separate qpdf/veraPDF before-and-after conformance sweep.
