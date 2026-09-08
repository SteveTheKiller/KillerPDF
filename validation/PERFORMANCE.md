# Performance validation results

## KillerPDF 1.9.0 memory ownership follow-up

Bounded surfaces, compact clips, explicit scratch lifetimes, shared immutable
stream storage, and reusable batch pixels reduce allocation pressure. The latest
single direct comparison used 644.2 MiB versus PDFium's 622.3 MiB on the shared
600-page set, and 400.1 versus 268.7 MiB on the difficult 74-page set. All 674
compared pages match the previous engine pixels. Suites pass 3,147 engine and
341 app tests. These are development checks, not median or release acceptance
results. Memory parity remains open, and difficult-page rendering remains slower.

See the [memory ownership record](benchmarks/1.9.0-memory-ownership/README.md)
for raw trials, build hashes, heap evidence, and limits.

Normal app rendering now also avoids a duplicate page bitmap and releases session
references on disposal. An actual app-boundary check on the 74-page sample kept
all pixels identical and reduced render-call allocation volume by 26.2%. This is
not a further batch peak-memory result or proof of interactive parity.

## KillerPDF 1.9.0 correctness and memory follow-up

The observed outer soft-mask reset defect is fixed, and unprofiled CMYK
conversion preserves photograph shadow detail. Profile-dependent color
differences remain. All 3,119 engine tests and 340 app tests pass.

On the 40-file, 74-page sample at 2048 pixels, avoiding a page copy before PNG
encoding reduced median peak working set from 783.2 to 590.3 MiB across three
alternating runs. Wall time was unchanged (17.794 versus 17.801 seconds), and
all 74 compared output pages were pixel-identical. These are engine-to-engine
results on the difficult sample, not a new PDFium comparison.

The final 600-file shared-set check completed every file in all three alternating
runs. All 600 compared pages were pixel-identical. Median peak working set fell
from 785.1 to 770.4 MiB, only 1.9%, while timing remained within machine noise.
Separate high-resolution tracing estimates 14.8% fewer allocated bytes after
both allocation changes. First-page display and scrolling remain unmeasured
because native UI control was unavailable.

See the [correctness and memory record](benchmarks/1.9.0-render-correctness/README.md)
for the implementation, profile, raw results, and remaining measurement limits.

## KillerPDF 1.9.0 rendering validation follow-up

Measured September 7, 2026, using the retained tuning payload and PDFium 1.8.5.
The same 600 successful first-page inputs were supplied to both builds at 1024
pixels, with one warmup and three alternating measured runs each.

| Measurement | PDFium 1.8.5 | Engine 1.9.0 |
| --- | ---: | ---: |
| Successful pages in every run | 600 | 600 |
| Median render time | 11.634 s | 11.593 s |
| Median whole-pass wall time | 21.989 s | 22.650 s |
| Median peak working set | 620.3 MiB | 808.0 MiB |

Render time remains at parity. The identical-input whole-pass gap is 3.0%, and
peak working set is 30.3% higher. This run does not isolate JIT, PNG encoding,
document opening, or other contributors to the remaining wall-time difference.

The full engine suite passes 3,113 tests and the full app suite passes 338 tests.
All five previously unverified sidebar tests pass in this session without code
changes. The earlier FontCache exception was not reproduced; its cause remains
unconfirmed.

A separate 40-file sample selected from the prior text, shading, and slow-file
lists rendered 74 pages per build at both 512 and 2048 pixels with no failures.
It is deliberately biased toward expensive content. At 2048 pixels its one-pass
wall time was 18.566 s for the engine versus 10.101 s for PDFium. Visible soft-mask
and color differences remain, including dark rectangles around Ghent text effects
and distorted image colors. These were also visible in a retained earlier engine
payload. Successful rendering does not establish visual equivalence.

See the [complete follow-up record](benchmarks/1.9.0-render-validation/README.md)
for raw timings, startup results, build hashes, visual findings, and limitations.
The [band-parallel rendering plan](../engine/docs/band-parallel-rendering-plan.md)
is a proposal only. Correctness and memory work take priority over enabling it.

## KillerPDF 1.9.0 engine tuning checkpoint

Measured September 7, 2026, after the glyph mask cache, compiled calculator
functions, rectangular-clip fast paths, pooled raster and Flate buffers, and the
cross-reference recovery screening landed. Same 649-file conformance collection,
page one at a maximum dimension of 1024 pixels, one warmup and three alternating
measured runs per build.

| Measurement | PDFium 1.8.5 | Engine 1.9.0 |
| --- | ---: | ---: |
| Rendered / skipped / failed | 600 / 41 / 8 | 614 / 35 / 0 |
| Median render time on 600 shared files | 12.280 s | 12.196 s |
| Median document open time on 600 shared files | not logged | 1.483 s |
| Median whole-pass wall time | 23.209 s | 24.210 s |

Product code: `604aa27`. App SHA256:
`93B53A97AD8B1853699B5A55C00565FA985E887AE031D99C02207DE4BE6693B4`.
The engine passes 3,113 tests.

The per-run shared render ratios were 0.930, 1.042, and 0.976; the ratio of the
medians is 0.993. The 1.8.5 build does not log open time, so its open cost is
only inside the whole-pass figure. The engine's open time on the same files was
3.7 s before the recovery screening change. Raw runs and the per-file comparison
are in [benchmarks/1.9.0-render-informal/2026-09-07-engine-tuning](benchmarks/1.9.0-render-informal/2026-09-07-engine-tuning).

Output changes: the glyph mask cache places cached glyphs at quarter-pixel
positions, so antialiased text edges can differ from the previous build by up to
about one eighth of a pixel of coverage; every other change in this checkpoint
was verified pixel-identical on 190 sampled files. Run-to-run variation on this
machine is about five percent, so a single run cannot separate the two builds.

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

## KillerPDF 1.8.2 through 1.8.4 release benchmarks

Five measured open/save passes were recorded for each release. These are separate
release sessions, not an alternating same-session comparison.

| Collection | Inputs | 1.8.2 median seconds | 1.8.3 median seconds | 1.8.4 median seconds |
| --- | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 167.891 | 182.093 | 207.099 |
| Standards and color | 649 | 5.695 | 5.666 | 5.635 |
| Private stress | 29,599 | 512.967 | 523.400 | 556.740 |

Version 1.8.3 saved 1,485 additional files without losing any previously successful
input. Version 1.8.4 reproduced every 1.8.3 outcome and diagnostic detail. All three
releases recorded zero crashes and timeouts in the separate damaged-file safety
collection. See the
[1.8.2 release record](https://github.com/SteveTheKiller/KillerPDF-Corpus/blob/main/benchmarks/killerpdf-v1.8.2.md)
and the reports and measured runs for [1.8.3](benchmarks/1.8.3/CORPUS.md) and
[1.8.4](benchmarks/1.8.4/CORPUS.md).

The 1.8.4 regression median was 13.7% longer than the separately recorded 1.8.3
session, while standards was 0.5% shorter and stress was 6.4% longer. The 1.8.3
record used a framework-dependent payload and the official 1.8.4 portable release
contains a self-contained .NET 10.0.11 payload. The two releases were not rerun in
alternating passes, so the difference is not isolated to application code.

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
