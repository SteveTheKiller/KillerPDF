# Performance validation results

Results are listed newest first. Earlier release benchmarks remain here so changes
between releases can be compared against their original measurements.

## KillerPDF 1.9.0 paired conformance after JPEG and form updates

Validation date: 2026-09-07. Product `63d8f4c` was compared with PDFium 1.8.5
on the same 649 inputs at 1024 pixels, page one. Each build received a warmup
and three measured passes with alternating order. No builds or heavy checks
overlapped measurement.

| Measure | PDFium 1.8.5 | Engine 1.9.0 |
| --- | --- | --- |
| OK / SKIP / FAIL per pass | 600 / 41 / 8 | 614 / 35 / 0 |
| Median shared-file render time | 12.676 s | 15.352 s |
| Median total render time | 12.676 s | 15.411 s |
| Median wall time | 23.699 s | 29.899 s |
| Median sampled peak working set | 645.0 MB | 1,097.0 MB |

On the 600 shared accepted files, the ratio of median render times is 1.211.
All 614 engine PNGs in every measured pass are identical to the reviewed Courier
alias conformance output. Full suites pass 2,953 engine and 338 app tests.

The engine is still slower and uses more memory. Both builds have higher absolute
times than the previous paired run; the ratio change does not isolate any one
fix or establish a statistically reliable improvement. Cold startup and JIT
remain included in collection wall time rather than measured separately.
Sampled working set includes runtime and caches and can miss a terminal peak.
See the [record](records/conformance-forms-1.9.0.json) and
[raw warmup, measured, and process logs](benchmarks/1.9.0-conformance-forms).

## KillerPDF 1.9.0 Courier PostScript aliases

Validation date: 2026-09-07. The Windows font resolver now recognizes CourierNew
PostScript aliases and styles. In `bug2055455.pdf`, the installed font restores
the thin glyphs seen in PDFium while retaining all four comb-field digits.
Mean absolute channel error falls from 2.301 to 0.017. This is a focused visual
improvement, not a claim of complete rendering parity.

Conformance retains 614 OK, 35 SKIP, and no failures. Of 614 PNGs, 613 are
identical. The remaining AIAA page changes 291 pixels near the bottom; visual
review confirms improved font substitution, with error falling from 4.881 to
4.859. Other visible text discrepancies on that page remain unresolved.

All 21 standalone resolver checks pass, including nine new alias and precedence
cases. The full suites pass 2,946 engine and 338 app tests, and Release builds
without warnings or errors. See the [record](records/courier-postscript-1.9.0.json)
and [raw logs](benchmarks/1.9.0-courier-postscript). No throughput refresh is
included in this increment.

## KillerPDF 1.9.0 comb field appearances

Validation date: 2026-09-07. Requested comb fields now place each simple-font
character in its declared cell. `bug2055455.pdf` restores all four digits in its
previously blank lower field. Only that region changes; the stored value remains
untouched. The glyphs are heavier than PDFium's, so mean absolute channel error
increases from 1.862 to 2.301. This font discrepancy remains unresolved, not an
accepted parity exception.

All 614 accepted conformance PNGs remain identical, with 35 skips and no failures.
The full suites pass 2,946 engine and 338 app tests, including ten new cases for
alignment, missing appearances, cell limits, and preservation. Release builds
without warnings or errors. See the [record](records/comb-fields-1.9.0.json) and
[raw logs](benchmarks/1.9.0-comb-fields). This is not a full-corpus or throughput
refresh.

## KillerPDF 1.9.0 combined form appearance audit

Validation date: 2026-09-07. The isolated PDFjs audit covers all 979 files at
1024 pixels, page one, against PDFium 1.8.5. The engine retains 960 OK, 18 SKIP,
and one FAIL; PDFium retains 957 OK, 21 SKIP, and one FAIL. Every prior status
is unchanged, and no process times out. All paired input hashes and build hashes
were verified against the recorded sources.

Compared with the previous full appearance audit, 950 engine PNGs are identical
and ten change. Nine match previously reviewed focused images exactly. The
remaining field in `issue12750.pdf` was reviewed separately; its pixel error
decreases from 0.074 to 0.049 relative to the prior focused correction. Missing
field text and borders are restored, and corrected font metrics and padding
improve placement. Font shape differences and the unsupported comb field remain.

This audits the product in `8e11b30`, validated by 2,936 engine and 338 app tests.
It is not a refreshed throughput comparison: the isolated processes include
cold startup, and lightweight documentation checks ran during the audit.
See the [record](records/full-form-insets-1.9.0.json) and
[complete raw rows](benchmarks/1.9.0-full-form-insets/render-comparison.csv).

## KillerPDF 1.9.0 form text insets

Validation date: 2026-09-07. Regenerated text now aligns and clips inside the
widget's declared border width. Seven focused files remain OK; six images change
and were visually reviewed. All five changed samples with matching PDFium
dimensions have lower pixel error. The two enlarged Hello World samples improve
from 7.079 to 1.401 and 6.742 to 1.046 mean absolute channel error. The remaining
dimension-mismatched sample was reviewed visually without an unaligned metric.
Font shape differences and unsupported layouts remain unresolved.

All 614 accepted conformance PNGs remain byte-identical, with 35 skips and no
failures. The full suites pass 2,936 engine and 338 app tests, including 13 new
alignment, clipping, and invalid-interior cases. Release builds without warnings
or errors. The [record](records/form-insets-1.9.0.json) and
[raw logs](benchmarks/1.9.0-form-insets) retain this focused validation; it is not
a throughput measurement.

## KillerPDF 1.9.0 bundled font metrics

Validation date: 2026-09-07. Missing descriptor ascent and descent now follow
the bundled TrueType font used for outlines. Explicit, embedded, and host font
metrics retain precedence. Eight new cases bring the full suites to 2,923
engine and 338 app tests, with a clean Release build.

Seven focused files remain OK. Five images change and were visually reviewed:
four have lower pixel error against PDFium and `bug1796741.pdf` has a small
increase. Horizontal padding and font shape differences remain unresolved.
Two outputs remain identical. All 614 accepted conformance PNGs remain identical,
with 35 skips and no failures across 649 inputs. This is a layout correction,
not a new throughput result. See the [record](records/fallback-metrics-1.9.0.json)
and [raw logs](benchmarks/1.9.0-fallback-metrics).

## KillerPDF 1.9.0 missing widget appearances

Validation date: 2026-09-07. Requested single-line appearances can now be
created from widget geometry when no appearance stream is saved. Five focused
PDFjs files were visually compared with the prior engine and PDFium output.
`annotation-tx.pdf` now displays its field text, `bug2055455.pdf` restores its
single-line digit, and the other three cases restore missing field borders or
values. Empty fields retain declared styling without requiring text defaults.

All five remain OK. Pixel error decreases in `issue12504.pdf`, `issue12750.pdf`,
and `issue17540.pdf`, but increases in `annotation-tx.pdf` and `bug2055455.pdf`.
Text placement and font shape remain different; the comb field in
`bug2055455.pdf` remains unsupported. These are not accepted parity exceptions.

Conformance retains 614 OK, 35 SKIP, zero failures, and 614 byte-identical PNGs.
The full suites pass 2,915 engine and 338 app tests, including nine new cases
for missing appearances, border styles, hidden widgets, and empty fields.
The [record](records/missing-appearances-1.9.0.json) and
[raw logs](benchmarks/1.9.0-missing-appearances) retain the measurements.
This is not a refreshed full-corpus or throughput result.

## KillerPDF 1.9.0 full PDFjs form appearance audit

Validation date: 2026-09-07. All 979 inputs retain their paired SHA-256 identities
and prior outcomes: engine 960 OK, 18 SKIP, one failure; PDFium 1.8.5 957 OK,
21 SKIP, one failure. No process timed out. Each build ran in a separate process
with page one at 1024 pixels, so these timings include startup and output work.

Against the full host Identity-font audit, 954 engine PNGs are byte-identical
and six changed. All six were visually reviewed or verified byte-identical to
previously reviewed focused output. Current field values replace stale text in
`bug1796741.pdf` and `bug1844583.pdf`; the earlier missing-text corrections in
`bug1883609.pdf` and `issue17492.pdf` are retained. Password masking remains
intact in `issue19389.pdf`.

Text placement remains different in `bug1844576.pdf`, `bug1844583.pdf`, and
`issue19389.pdf`, with increased pixel error. These are unresolved differences,
not accepted parity exceptions. Missing appearances and additional layouts
remain visible in the diagnostics. The [record](records/full-need-appearances-1.9.0.json)
retains per-image review and metrics; the [raw log](benchmarks/1.9.0-full-need-appearances/render-comparison.csv)
retains all 1,958 rows. This audit does not establish the speed target.

## KillerPDF 1.9.0 requested form text appearances

Validation date: 2026-09-07. `bug1883609.pdf` requests new appearances but saves
empty text sections for values Man, 150, and Red. The renderer now displays
those values using the inherited defaults and AcroForm font resource, while
retaining the saved background artwork. The page was visually compared with
PDFium 1.8.5; synthesized combo-box arrows remain different.

The conformance pass retains 614 OK, 35 SKIP, zero failures, and all 614
unchanged PNGs. The full engine and app suites pass 2,906 and 338 tests,
including nine new form cases. The [record](records/need-appearances-1.9.0.json)
and [raw logs](benchmarks/1.9.0-need-appearances) retain the evidence. This is
a focused content correction, not a new throughput comparison or full form
parity claim. Broader PDFjs image auditing and additional field layouts remain
required; see the [form integration limits](../engine/docs/forms.md).

## KillerPDF 1.9.0 JPEG transform arithmetic

Validation date: 2026-09-07. Reusing JPEG transform scale and quantization
products preserves all decoded samples across 40 extracted JPEG streams at
reductions of one, two, four, and eight. Five alternating decoder passes show
median improvements of approximately 6.5%, 5.1%, 1.6%, and 3.9%, respectively.
The bounded sample is concentrated in Altona and Ghent files and is not a
representative JPEG corpus. All passes, including timing outliers, are retained.

For `22060_A1_01_Plans.pdf`, three fresh-process renders at 1024 pixels, page
one, reduce median logged render time from 1,335 to 1,275 ms. Median wall time
changes from 1,786 to 1,753 ms; the first new process takes 3,018 ms. Startup
has not been isolated. Every before/after PNG is byte-identical. A conformance
pass retains 614 OK, 35 SKIP, zero failures, and all 614 unchanged PNGs.

The [record](records/jpeg-idct-1.9.0.json) retains build identities and timing
samples; the [raw conformance log](benchmarks/1.9.0-jpeg-idct/conformance.csv)
retains all 649 rows. This focused improvement does not replace the paired
PDFium throughput measurement below or demonstrate the overall speed target.

## KillerPDF 1.9.0 paired conformance timing after font corrections

Validation date: 2026-09-07. One warmup per build preceded three measured passes
at 1024 pixels, page one, alternating build order. Every engine pass retains
614 OK, 35 SKIP, zero failures, and the same 614 PNGs. PDFium 1.8.5 retains
600 OK, 41 SKIP, and eight failures.

| Median measurement | PDFium 1.8.5 | Engine 1.9.0 |
| --- | ---: | ---: |
| Logged render time on 600 shared files | 11.772 s | 14.794 s |
| Collection wall time | 22.263 s | 29.167 s |
| Sampled peak working set | 652,906,496 bytes | 1,112,776,704 bytes |

The shared render-time ratio is 1.257. The engine has not met the speed target,
and uses more peak process memory in this workload. This supersedes the earlier
1.243 timing checkpoint without establishing a regression from the small ratio
change. The largest median render gaps include `balloon_a1b_jp2k.pdf` (455 versus
148 ms), `altona_technical_1v2_x3.pdf` (351 versus 159 ms), and
`42828.0001.001.pdf` (374 versus 195 ms). These are profiling leads.

Working set includes the runtime and caches; 100 ms polling can miss the final
sample. Each collection pass uses a fresh process, so first-page JIT remains in
the totals. [Full record](records/conformance-fonts-1.9.0.json) and
[raw warmup, measured, and wall-time logs](benchmarks/1.9.0-conformance-fonts/)
preserve the measurements.

## KillerPDF 1.9.0 missing standard-font resource recovery

Validation date: 2026-09-07. Compatibility rendering now supplies an omitted
resource only when its name exactly matches one of the 14 standard PDF fonts.
The multiline Other Job Experience appearance in `issue17492.pdf` now shows its
stored text, including the blank line. The page reports the recovered Helvetica
resource. Existing fonts and strict rendering retain their previous behavior.

All 614 conformance PNGs are byte-identical, with 614 OK, 35 SKIP, and zero
failures. Seven new cases reproduced the omission before the fix; all ten new
tests and the full suites pass (2,885 engine, 338 app), with a clean Release build.
The [record](records/missing-standard-font-1.9.0.json) and
[raw logs](benchmarks/1.9.0-missing-standard-font/) retain the focused and
conformance evidence. Stale appearances that require regeneration remain open.

## KillerPDF 1.9.0 full host Identity-font audit

Validation date: 2026-09-07. The complete 979-file PDF.js comparison keeps coverage
at 960 OK, 18 SKIP, and one failure for the engine versus 957 OK, 21 SKIP, and
one failure for PDFium 1.8.5, with no timeouts. All paired source hashes were
verified. Of 960 engine PNGs, 948 are unchanged and 12 restore intended text,
including missing final digits, accented letters, Cyrillic, and a pound sign.

All 12 changed images were visually reviewed. Font sizing differences remain,
including higher aggregate error on one Calibri sample, and form-content gaps
remain open. [Per-image evidence](records/full-host-identity-1.9.0.json) and
[raw comparison](benchmarks/1.9.0-full-host-identity/render-comparison.csv) retain
the limitations. These cold per-file measurements do not establish throughput.

## KillerPDF 1.9.0 host Identity-font validation

Validation date: 2026-09-07. Unembedded Adobe-Identity TrueType fonts without
Unicode maps now use their glyph IDs in the requested host font. Five inspected
samples recover the intended Latin, accented, and Cyrillic characters, including
`VAT Code` and `Volumes`. Calibri glyph sizing and placement still differ from
PDFium; one sample has higher aggregate pixel error despite corrected characters.
This is a character-selection correction, with no general visual-parity claim.

Conformance remains 614 OK, 35 SKIP, and zero failures. All 614 PNGs are identical
to the portable Symbol run. Six regression cases failed before the correction;
the complete suites now pass 2,875 engine and 338 app tests, with a clean Release
build. [Evidence](records/host-identity-1.9.0.json) and
[raw logs](benchmarks/1.9.0-host-identity/) retain the measurements. Focused cold
render timings do not establish throughput or a performance improvement.

## KillerPDF 1.9.0 full portable-font corpus audit

Validation date: 2026-09-07. The 979-file PDF.js comparison before the host
Identity correction finished with 960 OK, 18 SKIP, and one failure for the
engine, versus 957 OK, 21 SKIP, and one failure for PDFium 1.8.5. There were no
timeouts. All input hashes were verified; 954 files rendered in both builds.

Compared with the preceding full run, 938 engine PNGs are identical and 22
changed. Visual inspection confirms restored mathematical symbols, checkmarks,
and playing-card suits. It also exposes incorrect Identity-font characters and
missing form values in otherwise successful renders. The subsequent host-font
correction is documented above; form appearance regeneration remains open.
These counts do not establish visual parity or throughput.

[Audit and per-image observations](records/full-portable-symbol-1.9.0.json)
and [raw comparison](benchmarks/1.9.0-full-portable-symbol/render-comparison.csv)
preserve this historical run, including higher-error pages and known gaps.

## KillerPDF 1.9.0 portable Symbol and ZapfDingbats validation

Validation date: 2026-09-07. Bundled standard-font outlines replace incorrect
substitute characters in both the app and standalone engine. All 189 Symbol and
202 ZapfDingbats encoding names resolve to readable outlines. The two font-table
pages render without outline diagnostics; RGB error against PDFium 1.8.5 falls
from 2.37798 to 1.30497 for Symbol and from 6.22068 to 1.42512 for ZapfDingbats.

Conformance remains 614 OK, 35 SKIP, and zero failures, with 612 byte-identical
images. The two changed pages were visually inspected: `CutHereExample.pdf`
now contains scissors and `585_1.pdf` uses the standard Symbol shapes. Both have
lower aggregate error, but dash placement and other rendering differences remain.
The [negative-phase audit](records/negative-dash-phase-1.9.0.json) establishes that
the engine's dash placement follows PDF 2.0. The older baseline is not the correct
reference for that specific feature; all other differences still require review.
This is a correctness check, not a controlled performance result. See the
[record](records/portable-symbol-1.9.0.json) and
[raw results](benchmarks/1.9.0-portable-symbol/engine-conformance.csv).

## KillerPDF 1.9.0 graphics-state stroke validation

Validation date: 2026-09-07. Extended graphics-state stroke settings now render
the round dots in `extgstate.pdf`. Its invalid miter limit produces a recovery
diagnostic. RGB mean absolute error against the saved PDFium 1.8.5 image falls
from 1.75007 to 1.50601; the remaining font difference prevents a parity claim.

Conformance stays at 614 OK, 35 SKIP, and zero failures. Of 614 images, 613 are
byte-identical. The changed `PDF-versions3.pdf` now has the thick rounded border
shown by PDFium; its RGB error falls from 16.26042 to 0.00366. Both changed pages
were visually inspected. This checks correctness, not controlled throughput.
See the [record](records/extgstate-stroke-1.9.0.json) and
[raw conformance results](benchmarks/1.9.0-extgstate-stroke/engine-conformance.csv).

## KillerPDF 1.9.0 full PDF.js recovery and Courier recheck

Validation date: 2026-09-07. At `24c3c71`, the same 979 inputs produce 960 engine
OK, 18 SKIP, and one FAIL, versus PDFium 1.8.5 at 957 OK, 21 SKIP, and one FAIL.
Neither build times out. Both render 954 files; six are engine-only and three
are PDFium-only. Catalog and page-tree recovery add `issue9418.pdf` and
`Pages-tree-refs.pdf`; `bomb_giant.pdf` retains readable text with a truncation
diagnostic rather than decoding its oversized streams.

Of 957 prior engine PNGs, 949 are byte-identical. All eight changed pages were
visually reviewed. Courier shapes improve, but `standard_fonts.pdf` exposes a
missing-glyph box regression despite lower aggregate pixel error. The audit also
confirms existing stroke-state and ZapfDingbats differences. Render success and
lower average error do not establish visual parity.

The subsequent [host-glyph fallback check](records/host-glyph-fallback-1.9.0.json)
removes those boxes from the Courier table while preserving embedded-font behavior.
It renders all 14 font-table pages and leaves all 614 conformance images unchanged;
it does not update this full run's build identity or counts.

Settings remain page one at maximum 1024 px, with annotations and forms, fresh
processes, and a 15-second per-file/build limit. Input paths and hashes match the
previous full run. Documentation checks and a short diagnostic probe overlapped
this coverage run; these timings do not replace controlled throughput results.
See the [record](records/pdfjs-courier-1.9.0.json) and
[per-file results](benchmarks/1.9.0-pdfjs-courier/render-comparison-summary.csv).

## KillerPDF 1.9.0 full PDF.js structural recovery recheck

Validation date: 2026-09-07. At `bb071dc`, all 979 PDF.js inputs match the prior
run by path and SHA256. The engine renders 957, skips 20, and fails two; PDFium
1.8.5 renders 957, skips 21, and fails one. Both render 951 files, with six unique
to each build and 16 accepted by neither. Neither build timed out.

All five status changes are engine SKIP to OK: `bug1978317.pdf`, `issue15150.pdf`,
`issue8088.pdf`, and `poppler-91414-0-53.pdf` / `poppler-91414-0-54.pdf`.
All 952 previously rendered engine PNGs are byte-identical. Focused records retain
the known text-shape, tiny-text darkness, and one-pixel sizing differences.

Settings remain page one, maximum dimension 1024 px, annotation and form appearances
enabled, fresh processes, and a 15-second limit per file and build. This is a
coverage comparison, not collection throughput or proof of all-page visual parity.
The controlled performance target remains open. See the
[record](records/pdfjs-object-index-1.9.0.json) and
[per-file results](benchmarks/1.9.0-pdfjs-object-index/render-comparison-summary.csv).

## KillerPDF 1.9.0 full PDF.js recovery recheck

Validation date: 2026-09-07. All 979 PDF.js inputs have the same paths and SHA256
hashes as the previous full run. At `f9e3ba0`, the engine returns 952 OK, 25 SKIP,
and two FAIL, improving from 935 OK, 25 SKIP, and 19 FAIL. PDFium 1.8.5 returns
957 OK, 21 SKIP, and one FAIL. Neither build timed out with a 15-second limit
per file and build. Both render 946 files; six are engine-only and 11 PDFium-only.

Of the 935 pages the engine rendered previously, 934 PNGs are byte-identical.
The changed page, `bug1028735.pdf`, now displays the equals sign that was missing
before. RGB mean absolute error against PDFium falls from 0.797081 to 0.000526.
The remaining failures are the decoded-size limit in `bomb_giant.pdf` and corrupt
ASCIIHex data in `poppler-90-0-fuzzed.pdf`; PDFium renders the latter page blank.

This run uses page one at a 1024 px maximum dimension, with annotation and form
appearances enabled and a fresh process per file. It measures coverage, not
collection throughput. Documentation example compilation overlapped the run.
Successful rendering does not establish all-page visual parity; focused records
retain known differences. The controlled performance target remains open.
See the [record](records/pdfjs-aes-empty-1.9.0.json) and
[all per-file results](benchmarks/1.9.0-pdfjs-aes-empty/render-comparison-summary.csv).

## KillerPDF 1.9.0 focused PDF.js recovery recheck

Validation date: 2026-09-07. The same 54 previously non-OK PDF.js files were
rerun at `13cdcb1`, with page one, 1024 px maximum dimension, and a 15-second
per-build/file limit. Engine results improved from 14 OK, 25 SKIP, and 15 FAIL
to 20 OK, 25 SKIP, and nine FAIL. PDFium 1.8.5 remains at 38 OK, 15 SKIP,
and one FAIL. Neither build timed out; all input hashes match the prior recheck.

This selected failure subset does not update full-corpus totals or establish
throughput or visual parity. See the [record](records/pdfjs-stream-recheck-1.9.0.json)
and [per-file results](benchmarks/1.9.0-pdfjs-stream-recheck/render-comparison-summary.csv).

## KillerPDF 1.9.0 JPEG 2000 allocation check

Validation date: 2026-09-07. Reusing cleared tile sample buffers reduced warmed
render allocations for `balloon_a1b_jp2k.pdf` from 46,792,488 to 16,594,552 bytes
(about 65 percent). Median render time was 333.764 ms before and 331.885 ms after;
this small difference does not establish a speed improvement.

Each build ran 15 renders in a separate process with a new document and renderer
per iteration, page one at 752 by 1024 pixels, and annotation/form appearances
enabled. The first three iterations were excluded from medians. All 30 pixel
hashes match. The 649-file conformance check remains 614 OK, 35 SKIP, and zero
FAIL, with all 614 PNGs identical to the prior build. Engine 2,775 and app 338
tests pass, including independent JPEG 2000 tile references.

Allocation counts measure the render thread, not peak or retained process memory.
The shared pool can retain returned arrays. This focused check does not replace
the paired PDFium throughput measurement below. See the
[record](records/jp2-frame-pool-1.9.0.json) and
[raw measurements](benchmarks/1.9.0-jp2-frame-pool).

## KillerPDF 1.9.0 focused PDF.js font recheck

Validation date: 2026-09-07. After the Unicode-map, missing descendant, and emoji
fallback changes, the 54 previously non-OK files were rerun at `79c0325` with
page one, 1024 px maximum dimension, and a 15-second per-build/file limit.
The engine returned 14 OK, 25 SKIP, and 15 FAIL; PDFium 1.8.5 returned 38 OK,
15 SKIP, and one FAIL. Neither build timed out, and every input hash matches
the full comparison below.

This is a selected failure recheck, not an updated full-corpus total or a
throughput benchmark. The emoji sample now renders both symbols as monochrome
outlines without diagnostics. See the [focused record](records/pdfjs-font-recheck-1.9.0.json),
[raw results](benchmarks/1.9.0-pdfjs-font-recheck/render-comparison-summary.csv),
and [emoji validation](records/emoji-fallback-1.9.0.json).

## KillerPDF 1.9.0 PDF.js coverage recheck

Validation date: 2026-09-07. The complete current PDF.js source tree contains
979 PDFs. Each build rendered page one at a maximum dimension of 1024 px in a
separate process, with a 15-second limit per file. Neither build timed out.
The engine executable corresponds to `05544b1`.

| Result | PDFium through KillerPDF 1.8.5 | KillerPDF 1.9.0 |
| --- | ---: | ---: |
| Rendered | 957 | 935 |
| Skipped | 21 | 25 |
| Failed | 1 | 19 |

Both builds rendered 929 files; six were engine-only, 28 were PDFium-only, and
16 were accepted by neither. All 977 inputs from the previous comparison retain
their hashes. On that unchanged subset, engine acceptance rose from 923 to 933;
PDFium remains at 955. The two additional files are `test/pdfs/empty#hash.pdf`
and `web/compressed.tracemonkey-pldi-09.pdf`, accepted by both builds.

These are coverage results. Documentation example checks and engine tests ran
concurrently, so timings are diagnostic and do not support a throughput claim.
Page-one success does not establish visual correctness or all-page coverage.
The subsequent Unicode destination-array fix is excluded from these counts.

See the [record](records/pdfjs-1.9.0-mask-sum.json) and
[per-file results](benchmarks/1.9.0-pdfjs-mask-sum/render-comparison-summary.csv).

## KillerPDF 1.9.0 paired conformance recheck

Validation date: 2026-09-07. Three measured passes used the same 649 conformance
inputs, page one, 1024 px maximum dimension, and enabled annotation/form
appearances. Build order alternated, with a fresh process per collection pass.
No builds or tests ran during measurement. Each pass had a 120-second limit;
none timed out. The engine executable corresponds to `05544b1`.

| Measurement | PDFium through KillerPDF 1.8.5 | KillerPDF 1.9.0 |
| --- | ---: | ---: |
| Rendered / skipped / failed per pass | 600 / 41 / 8 | 614 / 35 / 0 |
| Median logged render time, all accepted pages | 11.820 s | 14.751 s |
| Median logged render time, 600 shared pages | 11.820 s | 14.696 s |
| Median collection wall time | 22.317 s | 28.673 s |

The engine is at 1.24 times PDFium's logged render time on the shared set.
The target of outperforming 1.8.x is not yet demonstrated. This fresh comparison
supersedes the earlier 1.10 ratio as the current conformance timing checkpoint;
historical runs remain below. First-page JIT and cold startup have not been
isolated into their own measurements. Successful rendering also remains distinct
from visual parity across the corpus.

The largest first-pass time gaps include `balloon_a1b_jp2k.pdf` (459 versus
148 ms), `altona_technical_1v2_x3.pdf` (341 versus 153 ms), and
`42828.0001.001.pdf` (366 versus 201 ms). These are profiling leads, not proof
that any one decoder accounts for the complete gap.

See the [record](records/conformance-1.9.0-mask-sum.json) and
[all six raw logs](benchmarks/1.9.0-conformance-mask-sum).

## KillerPDF 1.9.0 eight-bit soft-mask averaging

Validation date: 2026-09-06. Default-range eight-bit soft masks use grouped integer
sums during reduction, preserving the exact previous averages and rounding.
Other depths and custom decode ranges retain their existing conversions.

On `issue19517.pdf`, three fresh-process runs reduce median render time from
1,361 to 761 ms (44.1%) after the preceding run-length allocation change. Compared
with the original 1,874 ms median, both improvements together reduce rendering
by 59.4%. Median wall time is 1,221 ms, with a 2,214 ms first sample retained.
Median sampled peak working set is 480,874,496 bytes, essentially unchanged from
the allocation fix. Sampling remains at 100 ms and may miss short-lived peaks.

The focused PNG is byte-identical. Six regression cases exercise full groups,
partial tails, and high sample values. All 2,739 engine and 338 app tests pass;
Release builds cleanly. Conformance retains 614 OK, 35 SKIP, zero FAIL, and all
614 PNGs are identical. These measurements concern one file and do not establish
corpus-wide parity with PDFium.

See the [record](records/mask-sum-1.9.0.json) and
[raw logs](benchmarks/1.9.0-mask-sum); the preceding section's
[run-length record](records/run-length-1.9.0.json) supplies the before samples.

## KillerPDF 1.9.0 run-length decoding allocation

Validation date: 2026-09-06. Run-length decoding validates and counts the output
before one exact allocation, then copies or fills complete runs. Output limits,
truncated-run checks, and the required end marker are preserved.

Profiling `issue19517.pdf` refined the earlier attribution: its external soft
mask is 12,608 by 16,806 eight-bit samples. Run-length expansion consumed 19.81%
of sampled exclusive CPU time and mask averaging another 29.76%; JPEG 2000
decoding was a smaller part of the total than the initial timing suggested.

Three fresh-process runs before and after this change used page one at 1024 px.
Median render time fell from 1,874 to 1,361 ms (27.4%). Median wall time fell from
2,349 to 1,847 ms. Median sampled peak working set fell from 891,113,472 to
480,710,656 bytes (849.8 to 458.4 MiB). Peaks were sampled every 100 ms and may
miss short-lived peaks. The first after-run wall time was 2,803 ms and remains
in the record. Builds and test suites were idle during these samples.

The focused PNG is byte-identical. All 2,733 engine and 338 app tests pass,
including seven new run-length boundary and malformed-input cases. Release
builds cleanly. Conformance retains 614 OK, 35 SKIP, zero FAIL, and all 614 PNGs
are identical to the previous build. The remaining mask-averaging and decoder
costs still need work; this is a focused improvement, not corpus-wide speed parity.

See the [record](records/run-length-1.9.0.json) and
[raw logs](benchmarks/1.9.0-run-length).

## KillerPDF 1.9.0 JPEG 2000 sample-depth recovery

Validation date: 2026-09-06. Compatibility rendering trusts the JPX codestream's
sample depth when the PDF dictionary disagrees. Strict rendering keeps rejecting
the mismatch; dimensions, component counts, supported depths, and allocation
limits remain checked.

PDF.js `issue19326.pdf` moves from FAIL to OK without diagnostics. It renders
visible JPX lettering at 1024 by 626 pixels in a fresh-process 383 ms sample.
The 1.8.5 PDFium baseline produces an entirely white image, so its 19 ms sample
does not represent equivalent visible output. The RGB difference from that blank
baseline is 46.220890 and is retained rather than treated as a fidelity regression.

An independent check with PDFium 153.0.7999.0 through the installed pypdfium2
runtime renders the lettering. At the source image's 551 by 337 dimensions, the
engine's mean absolute grayscale difference from that reference is 0.007426.
No dependency was added to the engine or application for this verification.

Three new tests failed on the prior code and now pass. They check recovered
colors, strict rejection, and retained dimension validation. All 2,726 engine
and 338 app tests pass; Release builds cleanly. Conformance retains 614 OK,
35 SKIP, zero FAIL, and all 614 PNGs are identical to the preceding build.

See the [record](records/jpx-depth-1.9.0.json) and
[raw logs](benchmarks/1.9.0-jpx-depth). These focused samples are not a throughput
comparison or a new full-corpus aggregate.

## KillerPDF 1.9.0 JPEG 2000 channel definitions

Validation date: 2026-09-06. The renderer separates a declared global JP2 opacity
channel from color samples and honors the PDF `SMaskInData` setting. An absent
or zero setting ignores that channel, as required by the PDF specification.

PDF.js `issue19517.pdf` moves from FAIL to OK without diagnostics. Its 768 by
1024 output visually matches PDFium's orange-red image, with RGB MAE 0.054336.
The fresh-process render sample took 1,947 ms; the prior PDFium sample took
425 ms. These isolated samples show remaining decoder cost, not a throughput win.
`issue19326.pdf` still has a separate PDF/codestream sample-depth mismatch.

Thirteen new cases cover ignored opacity, color inference, declared channel
positions, and malformed definitions or box lengths. All 2,723 engine and 338
app tests pass; Release builds cleanly. Conformance retains 614 OK, 35 SKIP,
zero FAIL, and all 614 PNGs are byte-identical to the preceding build.

Before this change, the focused recheck of all 54 prior non-OK PDF.js files
completed with eight engine OK, 25 SKIP, 21 FAIL, and no timeout. PDFium had
38 OK, 15 SKIP, and one FAIL. This diagnostic subset is not a new full-corpus
aggregate. The complete comparison log retains input and build hashes.

See the [record](records/jpx-opacity-1.9.0.json),
[raw logs](benchmarks/1.9.0-jpx-opacity), and
[opacity specification](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.6.pdf).

## KillerPDF 1.9.0 CMap-name metadata recovery

Validation date: 2026-09-06. Compatibility font reading tolerates a stray closing
angle delimiter in a CMapName definition before the first mapping block. The
following def token is required. Unrelated names, mapping data, and strict font
reading retain their validation.

PDF.js `issue11651.pdf` moves from FAIL to OK. Its Hello world and Test text and
red rectangles now render without diagnostics. At matching 1024 by 1024 pixels,
mean absolute RGB error against PDFium is 0.246198, with small edge differences.
The isolated fresh-process render sample is 80 ms, not a paired speed measurement.

Four new cases cover recovered mapping, strict rejection, and rejection outside
the narrowly defined metadata case. All 2,710 engine and 338 app tests pass;
Release builds without warnings or errors. Conformance retains 614 OK, 35 SKIP,
zero FAIL, and all 614 PNGs are byte-identical to the preceding build.

See the [record](records/cmap-name-1.9.0.json) and
[raw logs](benchmarks/1.9.0-cmap-name).

## KillerPDF 1.9.0 undeclared Unicode-map compression

Validation date: 2026-09-06. Compatibility font reading can inflate an unfiltered
ToUnicode stream once when its bytes have a valid zlib header. Authentication
still precedes decoding, and the strict inflater retains checksum validation and
the existing 32 MiB font-stream limit. Other streams and strict font reading do
not infer this missing filter.

PDF.js `issue19802.pdf` moves from FAIL to OK and renders its text without
diagnostics. At matching 1024 by 573 dimensions, mean absolute RGB error against
PDFium is 2.866167. Visual comparison confirms the text, including unusual source
characters; raster edges remain different. The single fresh-process render sample
is 141 ms and does not establish a comparative speed result.

Three cases cover recovered mapping, unchanged strict fallback, corrupt checksums,
and the decoded-size limit. All 2,706 engine and 338 app tests pass. Release builds
without warnings or errors. Conformance retains 614 OK, 35 SKIP, zero FAIL, and
all 614 images are byte-identical to the preceding color-operand build.

See the [record](records/unicode-zlib-1.9.0.json) and
[raw logs](benchmarks/1.9.0-unicode-zlib).

## KillerPDF 1.9.0 color-operand recovery

Validation date: 2026-09-06. Compatibility rendering consumes the required leading
color operands when extra values are supplied and preserves the current paint
color when an operation is incomplete. A diagnostic records the recovery.
Strict rendering continues to reject the same count mismatches.

PDF.js `clippath.pdf`, `issue18894.pdf`, and `issue21570.pdf` move from FAIL to OK.
The gray fill in issue18894 matches PDFium's use of the first operand; it is not
reinterpreted as RGB. RGB mean absolute errors are 0.476410, 0.003877, and 0.106015
respectively. The last measurement uses shared image area because fitted heights
differ by one pixel. These are diagnostic comparisons, not pixel-parity gates.
Final fresh-process render samples are 38, 81, and 98 ms; no paired speed claim
is made.

Eight fill/stroke cases verify strict rejection and recovered pixels for extra
and missing operands. All 2,703 engine and 338 app tests pass. The Release build
has zero warnings or errors. Conformance retains 614 OK, 35 SKIP, zero FAIL,
with all 614 PNGs byte-identical to the preceding symbolic-font build.

See the [record](records/color-operands-1.9.0.json) and
[raw logs](benchmarks/1.9.0-color-operands).

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
