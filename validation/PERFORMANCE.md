# Performance validation results

Results are listed newest first. Earlier release benchmarks remain here so changes
between releases can be compared against their original measurements.

## KillerPDF 1.9.0 cross-reference recovery comparison

Benchmark date: 2026-09-06. Same 649 conformance files, page 1 fitted inside
1024 pixels, annotation/form rendering enabled. One unmeasured warmup per build
preceded three measured passes, alternating build order. PNGs were retained.

| Build | Rendered | Skipped | Failed | Median render seconds | Median wall seconds |
|---|---:|---:|---:|---:|---:|
| 1.8.5 PDFium | 600 | 41 | 8 | 12.353 | 23.287 |
| 1.9.0 engine | 614 | 35 | 0 | 16.497 | 31.497 |

All 600 PDFium successes now render in the engine, with 14 additional successes.
On the common set, median render time is 16.433 seconds for the engine and 12.353
for PDFium, a 1.33 ratio. These measurements do not establish the performance goal.
Thirty engine successes retain incomplete-render diagnostics and need visual review.
Wall time includes process startup, opening, JIT, rendering, PNG encoding, and exit.
Render totals exclude failed and skipped rows. These are source Release builds, not
packaged startup measurements. Test system is the Ryzen 5 3600 machine below.

Compared with the prior 609-file engine result, the newly rendered files are
UnknownFilter-Linearized, UnknownFilter-xrefstm, UnknownFilter-OutlineObjStm, and
two corpus copies of T04_011_trailer-no-root-key-value-pair. The three UnknownFilter
pages were visually compared with PDFium: expected text, images, and shapes are
present, with the existing one-pixel fitted-width rounding difference.

The tested recovery code rebuilds readable object-stream registrations and recovers
a catalog when the selected cross-reference companion has no Root. Unknown compressed
payloads remain undecodable. An aggregate registration limit was added after timing;
the measured build hashes identify the exact pre-limit binaries. A final-build coverage
pass retained 614 rendered, 35 skipped, and zero failures. All 2,653 engine tests and
338 app tests passed, and the Release app build had no warnings or errors.

Per-page measured CSVs are in [benchmarks/1.9.0-recovery](benchmarks/1.9.0-recovery).
Build hashes and wall measurements are in
[render-recovery-1.9.0.json](../pdf-landing/render-recovery-1.9.0.json).
Earlier benchmarks below are retained as historical measurements.

## KillerPDF 1.9.0 JPEG Huffman lookup improvement

Benchmark date: 2026-09-06

Compared the JPEG decoder at `3e40eb7` with the Huffman lookup change in this commit.
Both source revisions were compiled into one .NET 10 Release process under separate
internal class names, with `DOTNET_TieredCompilation=0`. Decoded samples matched exactly
for all 40 streams at reductions 1, 2, 4, and 8. These streams include 38 unique inputs.

Selection used the first 40 decodable JPEG streams of at least 1,000 bytes found by
SOI/EOI scanning of the local conformance PDFs in directory enumeration order, with a
64 MiB decoded-output limit. This is a convenience sample, not a representative corpus
claim. Each resolution had an untimed equality pass, a discarded warmup timing pass,
then five measured passes alternating which decoder ran first. The machine is the
Ryzen 5 3600 system described below. SDK: 10.0.400.

| Reduction | Before median ms | After median ms | Time reduction |
|---|---:|---:|---:|
| 1 | 829.759 | 765.311 | 7.8% |
| 2 | 370.825 | 307.179 | 17.2% |
| 4 | 226.317 | 162.636 | 28.1% |
| 8 | 176.987 | 110.463 | 37.6% |

All measured passes are preserved in
[`jpeg-1.9.0-huffman.json`](../pdf-landing/jpeg-1.9.0-huffman.json).
This measures decoding only. The earlier 649-file render comparison below has not
been rerun after this change, and its timings remain the earlier measurements.

## KillerPDF 1.9.0 engine rendering compared with 1.8.5 PDFium rendering (informal)

Benchmark date: 2026-09-06

This is an informal development baseline, not a release benchmark. It compares the
1.9.0 engine-owned page renderer against the PDFium renderer that 1.8.x ships, using the
`--batch-render` command added to both lines and `Benchmark-Versions.ps1 -Mode Render`.
Both builds were Release builds from source on the same day; neither was a packaged release.

On the 649-file conformance collection, first page only, fitted inside 1024 px, three
alternating measured runs after one warmup each. The first pass was measured before the
engine's anti-aliased rasterizer, one-pass image conversion, and viewer-style file
recovery landed; the second pass was measured after them on the same day.

| Version | Median wall seconds | Pages rendered | Skipped | Failed | Median ms per page |
|---|---:|---:|---:|---:|---:|
| 1.8.5 (PDFium) | 23.192 | 600 | 41 | 8 | 5 |
| 1.9.0 (engine), before | 52.281 | 527 | 96 | 26 | 14 |
| 1.9.0 (engine), after | 29.248 | 609 | 40 | 0 | 7 |

Wall seconds include process startup, JIT warmup on the first file, file enumeration,
and PNG encoding for every file. Render-only time summed from the per-page log was 11.5
to 11.9 seconds per pass for PDFium and 15.4 seconds for the engine after the changes,
down from 30 to 36 seconds before them. On the 597 files both renderers accept, the engine
spends 15.4 seconds against PDFium's 11.5 seconds, about 1.3 times.

Per-file status transitions from PDFium to the engine after the changes: 597 rendered by
both, 10 skipped by PDFium but rendered by the engine (headers with no version or no
`%PDF`, trailers with missing keywords or brackets), 2 failed by PDFium but rendered by
the engine, 31 skipped by both, 6 failed by PDFium and skipped by the engine, and 3
rendered by PDFium but skipped by the engine (the `UnknownFilter` cross-reference and
object-stream cases, whose essential objects sit behind an undecodable filter).

Before the changes the engine took 1.05 seconds on `OverlappingGlyphClipping.pdf` where
PDFium took 13 milliseconds, 329 milliseconds on `LargeMitreLimit.pdf` against 2, and 1.1
to 2.3 seconds on the Altona and OpenPreserve posters against 0.5 to 0.9. After them
those pages take 119, 11, and 0.4 to 0.7 seconds. The remaining gap is JPEG and JPEG 2000
entropy decoding in managed code and first-page JIT warmup.

The 16,696-file regression collection was started with the same settings. PDFium spent
about five minutes on each of the pdfcpu Unifont SMP test files
(`regression\pdfcpu\fonts\user`), which the engine renders in about 1.4 seconds, so that
run was left to complete in the background and its results are not recorded here.

### Test system

| Component | Value |
|---|---|
| Operating system | Windows 11 Pro 10.0.26200 |
| CPU | AMD Ryzen 5 3600 6-Core Processor |
| Memory | 32 GB DDR4-3200 |
| Storage | 2 TB SPCC M.2 PCIe NVMe SSD |

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
