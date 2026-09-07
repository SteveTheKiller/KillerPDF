# Performance validation results

## KillerPDF 1.9.0 development checkpoint

Measured September 7, 2026, using page one at a maximum dimension of 1024 pixels
on the same 649-file conformance collection. Each build had one warmup and three
alternating measured runs.

| Measurement | PDFium 1.8.5 | Engine 1.9.0 |
| --- | ---: | ---: |
| Rendered / skipped / failed | 600 / 41 / 8 | 614 / 35 / 0 |
| Median render time on 600 shared files | 11.707 s | 13.260 s |
| Median whole-pass wall time | 22.241 s | 27.176 s |
| Median sampled peak working set | 641.7 MB | 790.3 MB |

Product code: `a5e4c07`. App SHA256:
`A61BCA22055D9AFD943EC53507385D94A0C245E47910D4F2DB00997ECB16022F`.
The engine passes 3,068 tests; the application passes 338 tests.

The shared render ratio is 1.1327. All 614 successful engine images match the
preceding recovery build. An earlier run measured 1.1582; run variation prevents
attributing that difference to the intervening OCR confidence correction.
Working set is sampled every 100 ms. Whole-pass wall time includes startup and
image encoding and covers each build's accepted files; it does not isolate cold startup.

Rendering without an exception does not prove complete page content or visual
parity. Broader multipage coverage, color and font differences, OCR accuracy,
memory use and performance remain ongoing work. Internal run logs and
investigation notes are maintained outside the application repository.

## KillerPDF 1.8.2 and 1.8.3 release benchmarks

Five measured open/save passes were recorded for each release. These are separate
release sessions, not an alternating same-session comparison.

| Collection | Inputs | 1.8.2 median seconds | 1.8.3 median seconds |
| --- | ---: | ---: | ---: |
| Public regression | 16,696 | 167.891 | 182.093 |
| Standards and color | 649 | 5.695 | 5.666 |
| Private stress | 29,599 | 512.967 | 523.400 |

Version 1.8.3 saved 1,485 additional files without losing any previously successful
input. Both releases recorded zero crashes and timeouts in the separate damaged-file
safety collection. See the
[1.8.2 release record](https://github.com/SteveTheKiller/KillerPDF-Corpus/blob/main/benchmarks/killerpdf-v1.8.2.md)
and [1.8.3 report and measured runs](benchmarks/1.8.3/CORPUS.md).

The 1.8.4 release tag preserves these earlier records; it contains no separate
1.8.4 benchmark. No 1.8.4 timing is inferred from an earlier release.

## KillerPDF 1.8.1 compared with 1.8.0

Benchmark date: 2026-08-29

KillerPDF 1.8.1 completed the shared 2,236-file batch-resave workload in a median
7.057 seconds, compared with 7.015 seconds for KillerPDF 1.8.0. The 0.6% throughput
difference is within ordinary run-to-run variation and far below the 10% slowdown
threshold that requires investigation.

This is a regression benchmark for KillerPDF's real batch-resave path. It is not a
claim that one PDF engine will be faster for every document or workload.

### Builds under test

| Version | Build | SHA-256 |
|---|---|---|
| KillerPDF 1.8.0 | Installed final release | `3D3C53B66A165C9BD26F1DCC1679AF131E69FB0305FF84BB52F53412EA657FCF` |
| KillerPDF 1.8.1 | Release candidate built from the current source | `B80687668D9ADA6DF4E01D7772E909B90CA8DA9A4B4ACEAC37702AE5FF43F4E9` |

Both executables reported their expected product versions. The 1.8.1 candidate used
the same installed-payload build shape as the official 1.8.0 application.

### Test system

| Component | Value |
|---|---|
| Operating system | Windows 11 Pro 10.0.26200 |
| CPU | AMD Ryzen 5 3600 6-Core Processor |
| Memory | 32 GB DDR4-3200 |
| Storage | 2 TB SPCC M.2 PCIe NVMe SSD |

### Corpus

The input was the same 2,236-file public conformance subset used for the 1.8.0
release benchmark. Keeping the input fixed makes the two runs directly comparable.

The shared input contains public veraPDF, Isartor, and TWG conformance files. The
same input directory was supplied to both versions for every run.

### Method

1. Verify that both executables report the intended final product versions.
2. Verify the release asset hashes and count the input PDFs recursively.
3. Give each version one unmeasured warmup run.
4. Run each version five measured times.
5. Alternate which version runs first to reduce ordering bias from caching, machine
   temperature, and background activity.
6. Use a fresh output directory for every run.
7. Record elapsed wall-clock time, successful output count, process exit code, and
   files processed per second.
8. Compare the median of the five measured runs instead of selecting the best run.

Every measured run processed all 2,236 files and returned exit code 0.

### Results

| Version | Runs | Median time | Minimum | Maximum | Median files per second |
|---|---:|---:|---:|---:|---:|
| KillerPDF 1.8.0 | 5 | 7.015 seconds | 6.916 seconds | 7.217 seconds | 318.73 |
| KillerPDF 1.8.1 | 5 | 7.057 seconds | 6.815 seconds | 7.326 seconds | 316.86 |

The raw measurements are preserved in
[`benchmarks/1.8.1/benchmark-results.csv`](benchmarks/1.8.1/benchmark-results.csv),
with the calculated medians in
[`benchmarks/1.8.1/benchmark-summary.csv`](benchmarks/1.8.1/benchmark-summary.csv).

## KillerPDF 1.8.0 compared with 1.7.5

Benchmark date: 2026-08-28

KillerPDF 1.8.0 completed the shared 2,236-file batch-resave workload in a median
10.013 seconds, compared with 16.167 seconds for KillerPDF 1.7.5. That is a 38.1%
reduction in elapsed time. Median throughput increased from 138.31 to 223.32 files
per second.

### Builds under test

| Version | Build | SHA-256 |
|---|---|---|
| KillerPDF 1.7.5 | Official GitHub release executable | `C53B34C5847ABE24228226656C00EFF5F76396D2BD8AD1BE8FDBAC56B153B136` |
| KillerPDF 1.8.0 | Installed final release | `3D3C53B66A165C9BD26F1DCC1679AF131E69FB0305FF84BB52F53412EA657FCF` |

The downloaded 1.7.5 executable matched the digest published for the v1.7.5 GitHub
release asset. Both executables reported their expected final product versions.

### Corpus note

KillerPDF 1.7.5 could not process the PDF 2.0 files in the full 2,907-file corpus,
so those files were excluded from both runs to keep the comparison equivalent.

### Results

| Version | Runs | Median time | Minimum | Maximum | Median files per second |
|---|---:|---:|---:|---:|---:|
| KillerPDF 1.7.5 | 5 | 16.167 seconds | 15.675 seconds | 16.687 seconds | 138.31 |
| KillerPDF 1.8.0 | 5 | 10.013 seconds | 9.451 seconds | 11.518 seconds | 223.32 |

The raw measurements are preserved in
[`benchmarks/1.8.0/benchmark-results.csv`](benchmarks/1.8.0/benchmark-results.csv),
with the calculated medians in
[`benchmarks/1.8.0/benchmark-summary.csv`](benchmarks/1.8.0/benchmark-summary.csv).

## Reproducing the benchmark

[`Benchmark-Versions.ps1`](Benchmark-Versions.ps1) runs the complete warmup,
alternating measurement, logging, cleanup, and summary process. Run it from
PowerShell with two KillerPDF executables and one shared input tree:

```powershell
.\Benchmark-Versions.ps1 `
    -BaselineExe 'C:\path\to\KillerPDF-previous.exe' `
    -CandidateExe 'C:\path\to\KillerPDF-current.exe' `
    -InputDirectory 'C:\path\to\shared-corpus' `
    -OutputDirectory "$env:USERPROFILE\killerpdf-benchmark\measured" `
    -Runs 5 `
    -BaselineLabel 'KillerPDF previous' `
    -CandidateLabel 'KillerPDF current'
```

The output directory must be inside the current user's profile. The script creates
and removes only its own `output-*` run directories. It keeps the per-run logs,
`benchmark-results.csv`, and `benchmark-summary.csv`.

For a useful comparison, close other demanding applications, keep the machine on
the same power plan, use the same corpus and storage device, and do not compare
absolute numbers from different computers. The relative change between two builds
measured in the same session is the result that matters.

## Release baseline

For each final release:

1. Compare the previous official release with the proposed final build.
2. Use the same machine, corpus, run count, and method.
3. Save the raw CSV files under `validation/benchmarks/<version>/`.
4. Add the new release result to this report.
5. Investigate a median slowdown of 10% or more before release. A slowdown is not
   automatically a failure, but it must be understood and documented.
