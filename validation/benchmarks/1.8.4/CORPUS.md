# KillerPDF 1.8.4 corpus benchmark

Test date: 2026-09-07

The official KillerPDF 1.8.4 release payload produced the same result as the recorded 1.8.3 baseline for every input. No previously successful input became a skip or failure. All five measured passes agreed on every file's status and diagnostic detail. The damaged-file gate had zero crashes and zero timeouts.

## Results

| Collection | Inputs | Saved | Skipped | Save failures | Median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,171 | 1,233 | 292 | 207.099 |
| Standards and color | 649 | 377 | 235 | 37 | 5.635 |
| Private stress | 29,599 | 25,015 | 2,957 | 1,627 | 556.740 |
| Damaged-file safety | 80 | 0 | 78 | 2 | Not timed as a batch |

The three normal collections contain 46,944 inputs, with 40,563 saved, 4,425 skipped, and 1,956 save failures per pass. Including the separate safety collection gives 47,024 inputs.

These are hostile collections, not a zero-failure workload. All normal batch processes returned exit code 1 because their logs contain save failures. The runner completed normally; this result is based on per-file comparison, not on treating the batch exit code as success.

## Comparison with 1.8.3

| Collection | 1.8.3 median seconds | 1.8.4 median seconds | Change |
| --- | ---: | ---: | ---: |
| Public regression | 182.093 | 207.099 | 13.7% longer |
| Standards and color | 5.666 | 5.635 | 0.5% shorter |
| Private stress | 523.400 | 556.740 | 6.4% longer |

Every path, status, and diagnostic detail matches 1.8.3 across the warmup and five measured passes. Damaged-file statuses, details, and exit codes also match exactly: 78 skips and two reported failures, with no missing logs, crashes, or timeouts.

These timings come from separate release sessions. The 1.8.3 record used a framework-dependent payload, while the official 1.8.4 portable release contains a self-contained .NET 10.0.11 payload. The builds were not rerun in alternating passes, so the timing differences should not be attributed solely to application code.

## Build and method

- Source commit: `eac3a045de73b2fe1dd9e327dda09a297d7e5333`.
- Version: 1.8.4 official portable release payload, Release, win-x64, self-contained .NET 10.0.11.
- Portable release SHA-256: `68270198AE4AA316B8C19CB1E3012ABE994EF79966A38604A721A39F1855D8BD`.
- Payload executable SHA-256: `AA0DEC2B1941B04A049AABABD05E6AEF8A922D805F073377DE7F5B236CEAF9E4`.
- Application DLL SHA-256: `822F40F4DBEC8482E55A3BAE8D244C088562F2BF43023610505ACA4DAA8C53FA`.
- Engine DLL SHA-256: `0D0DAB9F48BCC6B8E674AAD76276F6187166DC5DC85BC15A6F547BC5C1F7C288`.
- Runner: `KillerPDF-Corpus/scripts/benchmark_corpus.ps1`, existing local collections, downloads disabled.
- One warmup and five measured passes per normal collection, run sequentially.
- All 80 damaged inputs tested individually with a 30-second timeout.
- No other builds or test jobs ran during measured passes.
- Full per-file logs remain outside the application repository at `C:\Users\steve\code\KillerPDF-Corpus-Work\baseline-v1.8.4-20260907`.
- Extracted release payload remains outside the application repository at `C:\Users\steve\code\KillerPDF-Corpus-Work\release-1.8.4-payload-20260907`.

The committed [run data](benchmark-runs.csv) and [summary](benchmark-summary.csv) preserve the release-level measurements. The comparison baseline is [KillerPDF 1.8.3](../1.8.3/CORPUS.md).

This run tests batch opening and saving plus malformed-input process safety. It does not replace interactive rendering checks or a separate qpdf/veraPDF before-and-after conformance sweep.
