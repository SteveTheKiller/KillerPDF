# Performance validation results

Results are listed newest first. Earlier release benchmarks remain here so changes
between releases can be compared against their original measurements.

## KillerPDF 1.9.0 symbolic TrueType glyph selection

Validation date: 2026-09-06. Embedded symbolic TrueType fonts without a PDF
encoding now use encoded character codes when their selected font character map
is non-Unicode. This avoids collisions between Unicode values and unrelated
encoded glyphs. Unicode extraction and Unicode font maps retain their behavior.

The author and affiliation in `kateking_presentation_WN23.pdf` now render correctly.
Its page RGB mean absolute error against PDFium improves from 1.3666 to 1.1428;
the author/affiliation rectangle (x 90..939, y 410..489) improves from 5.8705 to
3.9296. Remaining edge and image differences are not treated as pixel parity.

The regression's Macintosh-map case failed before the fix, while its Unicode-map
control passed. All 59 font-resource tests, 2,695 engine tests, and 338 app tests
pass afterward. The Release build has zero warnings or errors.

Conformance retains 614 OK, 35 SKIP, zero FAIL. Of the images, 610 are byte-identical
to the preceding mask build. Four change, all with lower RGB error against PDFium:
the presentation, `EngHonorsCapstoneReport-Final.pdf`, and the two OpenOffice 3.2
lorem-ipsum exports. The latter differ by one pixel in width from PDFium, so their
shared-area errors are diagnostic rather than aligned visual-quality gates.

The isolated presentation render took 281 ms in a fresh process. This includes JIT
and is not a comparative performance measurement. See the
[validation record](records/symbolic-font-1.9.0.json) and
[raw coverage log](benchmarks/1.9.0-symbolic-font/conformance-engine.csv).

## KillerPDF 1.9.0 packed soft masks and independent sampling

Validation date: 2026-09-06. Image soft masks retain packed samples and use cached
area reduction instead of expanding every source pixel to a byte. Masks are
sampled independently of the color image at painting time. The existing 64 MiB
image-cache budget includes packed and reduced masks; reduced masks are capped at
four million samples. Strict parsing, stream validation, and authentication remain intact.

PDF.js `issue16263.pdf` repeats a 34,862 by 4,332 one-bit mask forty times. Its
151 MB expanded representation exceeded the cache budget and was repeatedly
recreated. The prior build timed out at 20 seconds. The final build renders it in
372, 357, and 361 ms in three fresh processes, with no diagnostics. Thin arrows
are restored; text edges and sampling still differ from PDFium. The earlier PDFium
sample was 3,623 ms, but these are not alternating paired throughput measurements.

Seven new cases cover mask detail at all five supported bit depths, thin-line
reduction, and color-sample stability under an opaque mask. All 2,693 engine and
338 app tests pass, and the Release build has zero warnings or errors.

Conformance retains 614 OK, 35 SKIP, and zero FAIL. Of the rendered images, 592
are byte-identical and 22 change. Shared-area RGB error against PDFium improves
on fourteen and worsens on eight. These overlap metrics are diagnostic, especially
where dimensions differ. Visual checks covered the repaired PDF.js page, the
presentation, and the fiber and GPS posters. Existing incorrect glyphs on
`kateking_presentation_WN23.pdf` were confirmed in the preceding build and remain
an open rendering issue. This increment does not establish corpus-wide visual parity.

See the [raw conformance log](benchmarks/1.9.0-soft-mask/conformance-engine.csv),
[build and pixel records](records/soft-mask-1.9.0.json), and
[rendering guide](../engine/docs/rendering.md). No public landing-page update is included.

## KillerPDF 1.9.0 attachment-only encrypted pages

Validation date: 2026-09-06. Page rendering and extraction can read unencrypted
default strings and streams in Standard Security documents without authenticating
their encrypted attachments. Explicit encrypted streams remain protected, including
previously cached streams and streams reached through decoder dependencies.
`IsDecrypted`, password role, permission reporting, attachment reads, and both writer
guards retain their authenticated meanings.

Five focused cases cover implicit/explicit Identity content, EFOpen attachments,
cached protected streams, explicit named crypt filters, authenticated attachment
decryption, writer rejection, and ordinary password-encrypted page rejection.
All 81 encryption-focused tests pass. Full suites pass 2,686 engine and 338 app
tests; the separate Release build has zero warnings or errors.

`auth-event-ef-open.pdf` and `encrypted-attachment.pdf` both move from SKIP to OK.
Both display the expected Example text with no diagnostics. Their 791 by 1024
images are identical to each other and have mean absolute RGB error 0.062935
against PDFium. Text-edge differences remain. Fresh-process render samples were
232 and 211 ms; these are not throughput comparisons.

The 649-file conformance check retains 614 OK, 35 SKIP, zero FAIL, with all 614
PNGs byte-identical to the preceding shading build. The full PDF.js comparison
uses the preceding fixed executable while this separate build is tested; its
coverage record must not be mislabeled as including the attachment change.
Raw records are in [benchmarks/1.9.0-attachment-access](benchmarks/1.9.0-attachment-access)
and [attachment-access-1.9.0.json](records/attachment-access-1.9.0.json).

## KillerPDF 1.9.0 shading endpoints and DeviceN colors

Validation date: 2026-09-06. Stitching functions accept endpoint stops and evaluate
zero-width segments without division by zero. Exponential shading functions now
apply DeviceN tint transforms to every output channel instead of treating the
components as device gray, RGB, or CMYK. Seven focused cases pass, including
one-, three-, and nine-channel gradients and invalid boundary rejection. Full
suites pass 2,681 engine and 338 app tests; Release builds without warnings or errors.

PDF.js `bug1703683_page2_reduced.pdf` now renders without diagnostics. Its dark
adapter illustrations are restored. Against the existing 1.8.5 image at identical
791 by 1024 dimensions, mean absolute RGB error falls from 3.6804 after the endpoint
fix alone to 0.8703 after the tint fix. The lower-device rectangle (x 130..399,
y 700..979) improves from 35.7501 to 6.9482. Remaining color differences are visible;
this is not complete visual parity.

The 649-file conformance pass retains 614 renders, 35 skips, and zero failures.
Compared with the endpoint-only build, 604 PNGs are unchanged and ten Ghent color
pages change. All ten have lower RGB error over their shared top-left image area
against PDFium. These pages differ by one pixel in width or height between engines,
so those overlap measurements are diagnostic, not an aligned visual-quality gate.
GWG060 was visually checked against its embedded reference images and PDFium.

The corrected page took 734 ms in one fresh process. This includes JIT and is not
a speed comparison. The three-pass JPEG timings below describe the preceding
build; throughput has not been remeasured for the shading changes.
Raw CSVs are in [benchmarks/1.9.0-shading](benchmarks/1.9.0-shading), with hashes and
pixel measurements in [shading-1.9.0.json](records/shading-1.9.0.json).

## KillerPDF 1.9.0 constant-block JPEG optimization

Benchmark date: 2026-09-06. Constant-color JPEG blocks bypass the general inverse
transform while retaining its rounding order. Eight independent baseline/progressive
JPEG test cases cover reductions of 1, 2, 4, and 8. Full suites pass 2,675 engine
and 338 app tests; the Release build has zero warnings or errors.

The same 649 conformance PDFs were rendered at page 1 inside 1024 pixels, with
annotations/forms enabled, one warmup per build, and three measured passes in
alternating build order. All 614 engine PNGs are byte-identical to the pattern fix.

| Build | Rendered | Skipped | Failed | Median render seconds | Median wall seconds |
|---|---:|---:|---:|---:|---:|
| 1.8.5 PDFium | 600 | 41 | 8 | 12.779 | 23.802 |
| 1.9.0 engine | 614 | 35 | 0 | 14.070 | 28.203 |

On the shared 600 files, engine median render time is 14.014 seconds, a 1.10 ratio
to PDFium's 12.779 seconds. Individual pass totals are retained below the aggregate
in the JSON record because timings vary. This still falls short of the speed target.
Wall time includes startup, opening, rendering, PNG writing, and exit; packaged
cold startup and memory remain separate work.

The plans page renders in 1,254, 1,246, and 1,245 ms in three fresh processes,
compared with 1,894 ms in the preceding isolated sample. Its PNG is byte-identical.
This focused comparison includes first-process JIT and is not a throughput claim.

The pattern checkpoint's image count below was corrected after checking literal
filenames: two images changed, not three. A bracketed filename had been incorrectly
classified by a wildcard-aware hash lookup; its image bytes were unchanged.

Raw measured CSVs are in [benchmarks/1.9.0-jpeg-dc](benchmarks/1.9.0-jpeg-dc).
Build hashes and individual timings are in
[jpeg-dc-1.9.0.json](records/jpeg-dc-1.9.0.json).

## KillerPDF 1.9.0 pattern coordinates and PDF.js regression sample

Validation date: 2026-09-06. Tiling and shading patterns now use the initial
coordinate space of their parent stream instead of the current content transform.
The PDF.js 22060_A1_01_Plans page previously repeated small floor-plan fragments;
the floor plans now occupy their intended positions. Fine image detail still differs
from PDFium, so this is a placement correction, not visual parity.

The 649-file conformance check retained 614 OK, 35 SKIP, zero FAIL. Of the 614 PNGs,
612 are byte-identical to the preceding JPEG 2000 build. Two changed: the MICOM
lighthouse poster and mipeng_poster_w24. The RecovAir
poster gradient is closer to PDFium (RGB mean absolute difference 2.7420 before,
1.2726 after, on a 0 to 255 scale). This metric is diagnostic, not a quality gate.
Two additional pattern cases pass; full suites pass 2,667 engine and 338 app tests,
and the Release build has zero warnings or errors.

The first 200 PDF.js files were compared with a fresh process for each build and
file, page 1 fitted inside 1024 pixels, annotations/forms enabled, and 15-second
per-file timeouts. Both before and after the fix, the sample rendered 196 files
in each build, with 193 shared successes and three unique to each. No process
timed out. Engine
results included two skips and two failures; PDFium had four skips. Remaining
engine cases include attachment-only authentication, a shading boundary array,
and a decoded-stream safety limit. None is treated as permission to relax safety.

A CPU trace of the corrected plans page attributes 48.68% of exclusive samples
to JPEG block reconstruction and 12.64% to JPEG output conversion. Trace overhead
and cold processes make these diagnostic measurements, not throughput benchmarks.
The earlier three-pass conformance timing remains below; no new aggregate speed
claim is made for this increment.

Raw comparisons and conformance outcomes are in
[benchmarks/1.9.0-pattern-space](benchmarks/1.9.0-pattern-space).
Build identities and validation totals are in
[pattern-space-1.9.0.json](records/pattern-space-1.9.0.json).

## KillerPDF 1.9.0 JPEG 2000 tile correction

Benchmark date: 2026-09-06. The same 649 conformance PDFs were rendered at page 1
fitted inside 1024 pixels with annotations and forms enabled. One warmup preceded
three measured passes per build, alternating build order and retaining PNGs.

| Build | Rendered | Skipped | Failed | Median render seconds | Median wall seconds |
|---|---:|---:|---:|---:|---:|
| 1.8.5 PDFium | 600 | 41 | 8 | 12.516 | 23.438 |
| 1.9.0 engine | 614 | 35 | 0 | 15.870 | 30.385 |

All 600 PDFium successes remain included, plus 14 engine-only successes. On the
shared files, engine median render time is 15.814 seconds, a 1.26 ratio to PDFium.
Twenty-nine engine pages retain diagnostics. Wall time includes startup, opening,
rendering, PNG encoding, and exit; this is not a packaged cold-start measurement.

The previously corrupt balloon_a1b_jp2k page now shows the complete image. Three
isolated engine processes produced identical PNGs. Its measured render medians are
478 ms for the engine and 167 ms for PDFium. Its fitted width and resampling still
differ from PDFium, so this does not establish pixel parity. The other 613 conformance
PNGs are byte-identical to the preceding graphics-state font build.

The decoder now owns resolution-aware tile geometry and initialized wavelet storage.
The existing CoreJ2K dependency still handles codestream decoding and component
transforms. Five new tests cover reduced gray/RGB tiles, partial edges, exact 16-bit
samples, and repeated lossy decoding against OpenJPEG 2.5.4 references. Lossy samples
differ by at most one level; 16-bit samples match exactly. All 2,665 engine and 338 app
tests pass, and the Release build has no warnings or errors.

Per-page CSVs are in [benchmarks/1.9.0-jpeg2000](benchmarks/1.9.0-jpeg2000).
Build identities, individual pass totals, and the repeated image hash are in
[jpeg2000-1.9.0.json](records/jpeg2000-1.9.0.json). Earlier results remain below.

## KillerPDF 1.9.0 graphics-state font rendering

On 2026-09-06, NegativeFontSize.pdf was rendered before and after the graphics-state
font fix and compared with 1.8.5, using page 1 fitted inside 1024 pixels. The missing
lower text is now present and its incomplete-text diagnostic is gone. Seven focused
tests compare graphics-state font selection with equivalent Tf instructions, covering
positive, negative, and zero sizes, indirect arrays, font changes, and q/Q restoration.
All 2,660 engine tests and 338 app tests pass; the Release build is warning-free.

A full conformance coverage pass retained 614 rendered, 35 skipped, and zero failures.
Diagnostic pages fell from 30 to 29. This was a coverage check, not a new timing study.
PDFium uses different glyph shapes for the lower Times-Roman run; the engine follows
the file's explicit Times-Roman dictionary. The one-pixel fitted-height difference
also remains. This is a content fix, not a claim of pixel parity.

The PNG comparison also exposed variable corrupt tiles in balloon_a1b_jp2k.pdf,
including before this font change and on an isolated repeat. It reports no diagnostic
and was an open visual-correctness problem at this checkpoint, corrected in the
JPEG 2000 increment above. The other 612 PNGs were byte-identical between these passes.

See [font-rendering-1.9.0.json](records/font-rendering-1.9.0.json) for identities
and [the coverage log](benchmarks/1.9.0-gs-font/engine.csv) for every outcome.

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
[render-recovery-1.9.0.json](records/render-recovery-1.9.0.json).
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
[`jpeg-1.9.0-huffman.json`](records/jpeg-1.9.0-huffman.json).
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
