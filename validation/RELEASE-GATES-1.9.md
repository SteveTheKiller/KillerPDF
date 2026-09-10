# KillerPDF 1.9 release gates

Release remains blocked while a known regression from the 1.8 pipeline exists.
An improvement over an earlier 1.9 build does not establish parity with 1.8.
Missing measurements are unverified, not passes. Test counts and successful
batch completion do not establish visual or interactive equivalence.

The retained PDFium application reports version 1.8.5 in its local
`KillerPDF-1.8` directory. Use identified application DLLs and identical
inputs when comparing pipelines. Record all alternating runs, warmups, peaks,
outliers, and measurement variability. Do not hide a slower or larger workload
behind an overall average.

| Requirement | Current status | Evidence still needed |
| --- | --- | --- |
| Memory at parity or close to the PDFium pipeline | Shared batch lower; difficult batch higher; interactive use unverified | September 9 installed-layout median peak working set is 495.5 versus 614.6 MiB shared and 283.7 versus 260.3 MiB difficult, including complete large-map rendering. Shared engine peaks range from 461.9 to 501.1 MiB; difficult peaks range from 278.6 to 288.9 MiB. Verify representative interactive document use and an explicit acceptable tolerance before release. |
| Rendering and whole-pass speed without regression | Open | Three alternating installed-layout measured runs put shared render medians at 15.687 versus 12.240 seconds; shared wall time is 27.350 versus 23.023 seconds. Difficult render time is 10.356 versus 5.479 seconds and wall time is 15.749 versus 11.011 seconds. Timing varies substantially across sessions; these paired results do not establish general speed parity. |
| Rendering fidelity without regression | Open | All 600 shared and 74 difficult output dimensions match PDFium. The latest stencil correction improves pixel agreement on 12 pages and leaves 662 unchanged. Installed-font aliases and pattern fixes are also retained. Complete large-map rendering, CMYK swatch compatibility, and engine-correct Ghent softmask effects remain preserved. Absolute color, font, other compositing, and fine-detail differences still need disposition; RGB-to-CMYK conversion remains an approximation. |
| Startup, first-page display, scrolling, and zoom without regression | Unverified | Controlled interactive measurements; the startup marker does not measure first-page completion or scrolling. |
| Existing 1.8 functionality and maintenance fixes preserved | Partially verified; open for release | Recorded 35 verified maintenance ports against local main at 5dd609f. The guard now reports only the 1.8-series brochure PDF commit 4cd5096 as missing. All five landing pages match maintenance except for the translation cache version; all 14 translated footers and the package summary match exactly. The stable source link and development release-date metadata are corrected. Applicable feature workflows still need release-build verification. |
| Builds and regression suites | Passing development checkpoint | September 9: 3,886 engine tests, 370 app tests, and the Release payload publish pass, including pattern transparency, zero-length dash, reduced stencil, and packed coverage regressions. Earlier blend tests and the exhaustive RGB check pass with hardware intrinsics disabled. The packed engine works in an isolated JPEG 2000 consumer. Repeat required checks for the final release build; these checks alone do not close other gates. |

Current paired evidence is archived locally under
`C:/Users/steve/kp-bench-render/review-20260909/stencil-area-paired*`.
The measured engine payload includes rendering changes through `249072b`,
including installed-font aliases, pattern text and transparency, zero-length
dash caps, and reduced stencil coverage. Its engine SHA-256 is
`BE2B3A818A9FE932FA491D911DFC6D5EEA04327F8F1D5EEDE8802B63011F7132`.
All 16 passes completed successfully. Run zero is warmup; runs one through three
alternate application order. All 5,392 images match their respective current
engine and PDFium baselines. The measurements, ranges, and page rankings are in
`stencil-area-paired-analysis.json`; the individual runs are in
`stencil-area-paired-results.csv`. Both applications use the retained
framework-dependent Windows payload layout. No installer or release was created.

Earlier checkpoints below document individual fixes and historical measurements.
They do not supersede the current paired results above.

On the profiled response-to-fiber-concerns page, two reversed-order pairs of
80 single-threaded fresh-document renders, each excluding the first 40,
reduced steady medians from 446-452 to 290-301 milliseconds with identical
pixel hashes. This improvement is specific to that workload and harness;
it does not establish whole-application speed parity.

The separate ReadyToRun experiment in `r2r-cmyk-results.csv` was not adopted:
shared render time fell to 11.829 seconds, but difficult render time rose to
10.643 seconds and shared whole-pass time rose to 24.836 seconds. Its retained
images are also unchanged. No precompilation setting was changed in the project.
The narrowly scoped color comparison is described in `engine/docs/color-review.md`.

The missing-font fix at `bb300ef` uses a diagnosed Helvetica fallback with
Windows ANSI encoding for unknown missing resources in compatibility mode.
`font-ansi-pixels.json` records lower mean RGB error against PDFium for all ten
changed outputs and identical pixels for the other 664 outputs. All 674 renders
complete successfully. This recovers visible text from malformed resources;
it cannot reconstruct an absent original font. Strict behavior is preserved.

Unmeasured interactive behavior and known speed and fidelity gaps remain
release gates.

The later vectorized inverse at `5d0d8ec` matches all 16,777,216 RGB inputs to
the scalar reference, including a separate test with hardware intrinsics disabled.
Two reversed-order pairs on the same profiled page reduce steady medians from
308.052 and 281.686 to 277.619 and 246.561 milliseconds, respectively, with
identical hashes. The accelerated path lazily adds about 154 KiB of shared lookup
storage. `rgb-vector-pixels.json` confirms unchanged pixels for all 674 retained
application outputs. The earlier `rgb-vector-paired-results.csv` measurements include this
change. Both builds ran faster than in the preceding session, so the difference
between sessions is not evidence of a general gain from vectorization.

The largest mean RGB difference in the 74-page difficult set is
`pdf-cos-syntax/CompactedPDFSyntaxTest.pdf`. Its content selects an empty-name
graphics-state resource with `/ca 0.33` and `/CA 0.66`. Engine 1.9 applies those
fill and stroke opacities; the retained PDFium output paints the shapes opaque.
This particular difference preserves the document's requested transparency and
is not a target for pixel matching. A focused regression checks both opacities
with empty and ordinary resource names, using direct and indirect dictionaries.
All 3,741 engine tests pass after adding these four cases. No rendering code
changed in this checkpoint. The other visual differences remain open.

In `altona_technical_1v2_x3.pdf`, the right-hand text sample is a 947 by 301
pixel image (object 201), painted through nested Forms at 245.2755 by 78.56151
page units. Runtime tracing confirmed that clipping sends it through the
general image path with direct device samples and integer grid reduction.
Area averaging now preserves its thin strokes and visibly smooths the text.
Unmasked converted images and plain gray/RGB reductions also use area averaging;
masked images retain their existing sampling. This does not dispose of the
page's other differences.

The area-averaging checkpoint renders all 674 pages successfully. Seven difficult
outputs and 61 shared outputs have lower mean RGB error against PDFium; 604 are
unchanged. Two shared outputs have small increases: the Altona drop-shadow page
changes from 8.663269 to 8.682427 and Arakawa-2025-Lab_Animal from 5.215518 to
5.215681. Visual inspection of the former shows smoother raster text. Raw scores
are in `area-complete-Broad-analysis.json` and `area-complete-Shared-analysis.json`.
All 3,747 engine tests and 352 app tests pass, including reduction under clipping,
quarter-turn rotation, and fractional edge weighting.

This fidelity improvement has an unresolved performance cost. A separate
baseline/new/new/baseline difficult-batch check records render totals of
8.905/10.291/10.252/9.150 seconds and wall times of
13.954/15.358/15.352/14.282 seconds in `area-timing-results.csv`. These four
passes are a focused before/after check, not a replacement for the full paired
PDFium measurements in `rgb-vector-paired-results.csv`, which predate area averaging. Much of the added cost
is in 42828.0001.001.pdf and balloon_a1b_jp2k.pdf. Optimize the averaging path
while preserving the recovered detail; speed parity remains open.

The later one-bit reduction path counts packed bits with exact integer coverage
weights, then interpolates the two converted colors. In the final
baseline/new/new/baseline check, the three 42828.0001.001.pdf pages take
1.187/0.739/0.773/1.179 seconds combined. This recovers about 36% of their render
time relative to the initial averaging implementation. Whole difficult-batch
render totals are 10.365/10.521/9.955/10.115 seconds and wall times are
15.526/16.228/15.038/15.261 seconds, so these runs do not establish a batch-wide
speed improvement. See `area-count-final-timing-results.csv` and
`area-count-final-verification.json`. The plain-byte shortcut experiment was
removed because its benefit was unclear.

All 674 final outputs match the bit-count candidate. Relative to initial area
averaging, all 74 difficult outputs and 591 shared outputs are unchanged. Exact
coverage rounding changes 1,614 pixels across nine shared images by at most one
channel level; `area-count-pixels.json` retains their scores against PDFium.
Eight added cases cover packed row padding, fractional reduction, rotation,
clipping, both bit polarities, and an independent integer coverage-grid oracle.
All 3,755 engine tests and 352 app tests pass. The remaining area-averaging cost,
other fidelity differences, and full-pipeline parity still require work.

The balloon JPEG 2000 page remains a major speed gap. A 100-render sampled
thread-time profile of the current renderer, examining only time after 35 seconds,
attributes about 18.65 of 26.36 seconds to decoding, including memory operations,
and 6.48 seconds directly to area sampling. Its source is 2,717 by 3,701 pixels;
the next lower JPEG 2000 level is smaller than the requested output. Preserve
the current resolution while investigating area-sampling overhead and decoder
memory traffic. The raw profile and summary are `balloon-current.nettrace`,
`balloon-current.speedscope.json`, and `balloon-profile-summary.json` in the
adjacent `parity-20260909-ghent` directory.

A direct vector-load/store experiment for wavelet column copies was discarded.
Two reversed-order 80-render pairs, excluding the first 40 renders of each run,
gave baseline/new medians of 608.443/595.071 and 581.420/595.482 milliseconds.
All 320 hashes matched with zero diagnostics, but the timing change reversed
direction. `vectorcopy-*.csv` retains all iterations. No decoder or rendering
implementation change was retained from this experiment.

Small RGB area footprints now reuse horizontal weights across rows and unroll
up to three columns while preserving accumulation order and cancellation.
All 674 application outputs remain pixel-identical to the one-bit checkpoint;
`areaweights-pixels.json` records the comparison. A retained scalar digest covers
388,960 gray/RGB footprints, including boundaries and the wider fallback path.
The isolated 3,078,144-sample check matches every pixel and reduces sampling
time by about 27%; this is a kernel measurement, not application throughput.

On the balloon page, baseline/new/new/baseline runs of 80 fresh-document renders,
excluding the first 40 per run, give medians of 591.797/558.250/558.135/581.445
milliseconds. Both run orders improve by about 4% to 6%, with all 320 hashes
unchanged and zero diagnostics. `areaweights-summary.json` and the four
`areaweights-*.csv` files in `parity-20260909-ghent` retain the measurements.
All 3,757 engine tests, 352 app tests, and the Release payload publish pass.
Decoder cost and broader rendering, performance, and interactive gates remain open.

JPEG 2000 reconstruction now allocates buffers above the sample pool's retention
limit directly, avoiding a second clearing of already-zeroed arrays. Reused
buffers still clear every tile sample, without clearing unused bucket capacity.
The pool limit and temporary-memory accounting are unchanged. For the balloon
page, this removes 120,667,404 redundant bytes of clearing per decode.
All 674 retained application outputs remain identical (`frame-clear-pixels.json`),
and 3,757 engine tests, 352 app tests, and the Release payload publish pass.

The corresponding 80-render baseline/new/new/baseline medians, excluding the
first 40 of each run, are 567.285/555.692/572.878/576.186 milliseconds. All 320
hashes match with zero diagnostics. The small differences and overlapping ranges
do not establish a general speed improvement. `frame-clear-summary.json` and
the four `frame-clear-*.csv` files retain the data in `parity-20260909-ghent`.
The published and measured engine SHA-256 is
`A208E82F48852EB4474C345049BA97E65A522B692E49C336B73719D225DBE855`.

The earlier `frame-clear-paired-results.csv` run confirms that balloon and Ghent combined-suite page 2
remain major difficult-page gaps (968/426 and 641/132 milliseconds respectively).
The shared set also identifies `GWG182_16Bit_Images_ICCbasedGray_x4.pdf` at
295/45 milliseconds before grayscale lookup caching. Its 684 by 684, 16-bit ICC
grayscale image used the uncached conversion path. A 300-render profile attributes
20.67 of 44.83 seconds of rendering in its latter half to color conversion and
8.73 seconds to TIFF prediction (`gray16-profile-summary.json`).

Single-component lookup caching now includes 16-bit samples. Each converter uses
320 KiB for values and validity flags, plus 64 KiB when matte alpha is tracked;
parallel row workers own separate converters. Eight retained-reference cases
cover the full 65,536-value range repeated with differing alpha, inverted decode,
nonlinear calibrated gray, and reduction. All 674 corpus outputs remain identical
in `gray16-lookup-pixels.json`. Tests and the Release payload publish pass.

Two reversed-order 80-render pairs at size 1024, excluding the first 40 renders,
give baseline/new medians of 288.868/122.397 and 287.692/121.944 milliseconds.
All 320 hashes match with zero diagnostics, a roughly 58% reduction on this page.
`gray16-lookup-summary.json` and `gray16-timing-*.csv` retain the data in
`parity-20260909-ghent`. The published and measured engine SHA-256 is
`E2EDB20F6EEC616AAE2009347FBC0579A6086ED95617D9DF317F0703E7EDDB52`.
The earlier `frame-clear-paired-results.csv` measurements predate this change. The same
profile identified bit-by-bit TIFF prediction as another substantial cost.

TIFF prediction now reconstructs eight-bit bytes and big-endian 16-bit words
directly, preserving modular arithmetic and channel spacing. Packed samples
retain the existing path. Six test cases cover 1, 3, and 4 channels across three
row widths, including single-column rows, wraparound, and independent row starts;
two more cases check rejection of incomplete rows. No additional buffers are used.
All 3,773 engine tests, 352 app tests, and the Release payload publish pass.
All 674 corpus images remain identical (`tiff-direct-pixels.json`).

Against the grayscale-lookup build, two reversed-order 80-render pairs excluding
the first 40 give baseline/new medians of 123.959/65.931 and 122.102/67.201
milliseconds. All 320 hashes match with zero diagnostics, a 45% to 47% reduction
on the profiled page. `tiff-direct-summary.json` and `tiff-timing-*.csv` retain the
measurements in `parity-20260909-ghent`. The published and measured engine SHA-256
is `530B72A65095D0E50A3E36D10B67962B227BC72B179659B9773D75BDDFD56121`.
The earlier `frame-clear-paired-results.csv` comparison predates both changes; overall speed,
fidelity, and interactive parity remain open.

Ghent combined-suite page 2 has distributed costs. The latter half of a
100-render sampled profile attributes 1.49 of 12.02 rendering seconds directly
to `SetInkPixel`, 1.45 seconds to GC polling, and 1.39 seconds directly to Form
processing (`ghent-page2-profile-summary.json`). A separate branch probe counts
296,457 general-path calls with a transparent backdrop among 913,203 ink calls.
The probe counters were removed before validation and timing.

Transparent-backdrop CMYK compositing now omits zero-weight backdrop and blend
calculations while preserving source-alpha multiplication, division, and rounding.
Six regression cases pass both before and after the change across all 16 authoring
blend modes, three opacities, images, and filled paths. All 674 corpus outputs
remain identical (`ink-transparent-pixels.json`), and all 3,779 engine tests,
352 app tests, and the Release payload publish pass. No additional buffers are used.

Two reversed-order 80-render pairs, excluding the first 40, give baseline/new
medians of 214.998/208.737 and 213.645/210.444 milliseconds, a modest 1.5% to 3%
page improvement with overlapping ranges. All 320 hashes match with zero
diagnostics. `ink-transparent-summary.json` and `ink-transparent-timing-*.csv`
retain the measurements in `parity-20260909-ghent`. The published and measured
engine SHA-256 is `F55A523A3D9EE001DDF730DF590C0F2612C9E1B92CD8338753DDC749E0F29520`.
This does not establish general speed parity. Form processing, memory management,
and the other release gates remain open.

The current full application run includes these changes and confirms a median
70/46 milliseconds for the targeted 16-bit grayscale page. It also confirms that
the overall shared and difficult workloads remain slower; there is no established
batch-wide speed gain relative to the preceding application checkpoint.
The largest shared-set gap is now the first page of
`eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf`, at 1,048/687
milliseconds. It contains a 6,784 by 3,392, eight-bit DeviceCMYK Flate image
(object 1239), plus two smaller CMYK images. Profile its decoding and painting
before selecting the next change. All 5,392 repeated output images match their
respective retained images; this consistency check does not establish visual parity.

The Altona profile attributes 31.66 of 44.11 rendering seconds in its latter half
to area conversion, including 14.48 seconds directly in averaging and substantial
ICC conversion (`altona-large-profile-summary.json`). Its large image has 430,118
distinct colors among 23,011,328 pixels. A linear-scan cache probe records
1,519,180 misses with 4,096 entries and 860,096 with 16,384 entries. This probe
is supporting evidence only; renderer traversal differs.

Images with at least 1 MiB of sample data now use 16,384 conversion-cache entries;
smaller images retain 4,096. The increase is 108 KiB per converter, or 120 KiB
with matte alpha tracking. Full keys and alpha are still checked before reuse.
Four new cases match uncached 16-bit references under clipping, rotation,
reduction, and matte correction. All 674 corpus outputs remain identical
(`image-cache14-pixels.json`), and 3,783 engine tests, 352 app tests, and the
Release payload publish pass.

Two reversed-order 80-render pairs at size 1024, excluding the first 40, give
baseline/new medians of 1,034.055/897.156 and 1,081.929/912.226 milliseconds,
a 13% to 16% improvement on this Altona page. All 320 hashes match with zero
diagnostics. `image-cache14-summary.json` and `image-cache14-timing-*.csv` retain
the data in `parity-20260909-ghent`. The published and measured engine SHA-256 is
`7B0C7632C0A35E798C69327DAC55722FA553533CCEFF587347CBA1AE127D62D6`.
The earlier `cf976c1` application comparison predates this change. Averaging overhead,
remaining decoding costs, and broader parity gates remain open.

A profile of the larger-cache build attributes 16.13 of 41.35 rendering seconds
in its latter half directly to area averaging, with 27.91 seconds including
called conversion work (`altona-cache14-profile-summary.json`). Reusing horizontal
weights through a bounded stack buffer was tested and removed: two reversed-order
60-render pairs, excluding the first 30, produced baseline/candidate medians of
1,009.161/1,299.699 and 1,099.236/1,128.199 milliseconds. The wide timing ranges
do not support a precise regression estimate, but neither pair showed a gain.
All 240 output hashes match with zero diagnostics. `area-horizontal-summary.json`
and its timing CSVs retain this rejected experiment outside the repository.
The next averaging experiment should address accumulation or sample access
without adding a per-footprint weight buffer. No renderer change was retained.

Packed cache-key reads for eight-bit RGB and CMYK samples reduce repeated sample
addressing without changing conversion or adding storage. Four additional large
CMYK cases match uncached 16-bit references, including matte correction. All 674
corpus outputs match the larger-cache build (`packed-key-pixels.json`). The full
3,787 engine tests, 352 app tests, and Release payload publish pass.

Two reversed-order 60-render pairs on the Altona page at size 1024, excluding
the first 30, give baseline/new medians of 974.440/891.219 and
1,012.365/853.370 milliseconds. This is about 9% to 16% faster on this page;
the varying timings do not establish a broader application gain. All 240 hashes
match with zero diagnostics (`packed-key-summary.json` and timing CSVs).
The published and measured engine SHA-256 is
`B8DF5FDF2AE4FA8B17DA64D9B880EAC68B1854DE1A7CCF83E26003AB6064C334`.
The earlier `cf976c1` application comparison predates this change. Overall performance,
rendering fidelity, and interactive parity gates remain open.

The full `47edfd8` application comparison now includes both image-cache changes.
All 16 passes succeed, and all 5,392 output images match their respective retained
images. Shared measured render ranges are 15.463-16.514 seconds for the engine
and 13.169-14.757 for PDFium. Difficult ranges are 9.935-10.090 and 4.948-5.390.
The within-series median gaps remain about 12% shared and 98% difficult; do not
infer a speedup or regression by subtracting timings from the earlier session.

The largest difficult-page gaps are balloon JPEG 2000 (1,144/464 milliseconds),
Ghent combined test page 2 (619/145), and response-to-fiber-concerns (502/87).
The large Altona page remains a shared-workload outlier (1,288/708). Fresh-process
batch timings differ substantially from warmed single-page harness timings.
Investigate the actual application batch profile before choosing another isolated
averaging optimization. The application SHA-256 is
`79B0F821D244957745CE277E365E5FF368F96E2C8D6BAE962E8C2A338EE6C264`;
the engine SHA-256 remains the packed-read hash recorded above.

The actual difficult-page application batch profile completes all 74 pages
(`packed-key-app-broad.nettrace`). Restricting analysis to managed `CPU_TIME`
samples, coverage painting contributes 957 milliseconds directly, image area
conversion 793, and RGB-to-CMYK conversion 464. Rendering contributes 9,032
milliseconds inclusively across sampled threads. These values are profile
attribution, not independent wall-clock timings. Unmanaged intervals and thread
waits are excluded from `packed-key-app-managed-profile.json`; the unfiltered
summary must not be presented as CPU time. Coverage painting is the next engine
target to investigate, alongside the still-open fidelity and interactive gates.

Opaque CMYK shapes now fill fully covered interior runs in bulk, retaining the
original compositor for partial edges and the existing paths for masked clips,
soft masks, overprint, and knockout. Four new cases compare disjoint antialiased
shapes against the general transparent-backdrop compositor with group opacity.
All 674 corpus outputs remain identical, including 444 repeated difficult outputs
and 600 shared outputs (`ink-runs-pixels.json`). The 3,791 engine tests, 352 app
tests, and Release payload publish pass.

The focused Ghent page-2 timing is inconclusive: baseline/new medians are
260.259/237.8205 and 237.0305/238.197 milliseconds in two reversed-order
80-render pairs, excluding the first 40. All 320 hashes match with zero
diagnostics (`ink-runs-summary.json`). In the actual difficult-page application
batch, two measured reversed-order pairs after warmup give baseline/new render
times of 10,278/10,079 and 10,155/10,053 milliseconds, about 1% to 2% lower.
Warmup favored the baseline, so this is modest evidence rather than an established
general speed gain. All six batch passes succeed (`ink-runs-ab-results.csv`).
The published and measured engine SHA-256 is
`5D2EE68CEB19D07F8E4F114EC4EDE9C24CB6A7784557AB01898E30A5801D4CB8`.
The full PDFium comparison remains the earlier `47edfd8` checkpoint; overall
performance, fidelity, and interactive parity remain open.

Visual inspection of Ghent 16.10 and 16.11 distinguishes effects from the pages'
remaining background and text differences. Comparing each actual effect against
its embedded reference image within the same renderer gives lower mean RGB error
for the engine in all nine patches. Engine/PDFium errors are: drop shadow
1.637/2.273, inner shadow 1.739/2.629, outer glow 1.589/2.718, inner glow
1.589/4.511, bevel/emboss 1.445/4.869, satin 1.246/1.794, basic feather
0.881/5.496, directional feather 1.351/3.879, and gradient feather 0.674/2.470.

`ghent-text-softmask-reference-method.json` records all nine crop regions and
measurements, using the same horizontal -3 to 3 and vertical 349 to 355 pixel
alignment search for both renderers at size 2048. The inputs are the retained
`ink-runs-ab-Broad-1-Engine` and `payload-cmyk-Broad-0-PDFium` images. These are
local effect-consistency checks, not absolute color measurements: errors shared
by an effect and its reference image can cancel. The effects should not be
changed merely to reduce whole-page differences from PDFium. Background color,
registration-color text, font rasterization, and other pages remain open.
No renderer code changed during this visual review.

The FAccT paper exposes a separate, confirmed Type 1 outline regression: letters
have triangular gaps because `setcurrentpoint` prematurely finishes the contour
after flex. [Adobe's Type 1 specification, section 6.4](https://adobe-type-tools.github.io/font-tech-notes/pdfs/T1_SPEC.pdf)
defines this as setting the current point without a moveto. Removing the contour
finish preserves the following path segments. The extended flex regression test
fails before the change with two contours instead of one, then passes after it.
Visual inspection confirms the broken letters are repaired on FAccT page 2.

All 674 corpus renders succeed. Ten outputs change, all with lower mean RGB
error against PDFium; the other 664 remain identical. The three difficult-set
FAccT pages improve from 5.943/7.436/6.871 to 4.289/5.283/4.993 mean error.
Seven shared outputs also improve (`type1-contour-pixels.json`). Residual font
rasterization differences remain; this does not establish overall text parity.
All 3,792 engine tests, 352 app tests, and the Release payload publish pass.
The engine SHA-256 is
`3B996C604C07D9D2AAA3190080805ACC1F98925B31DA78B06F118DCFB95604AE`.

Bundled font fallback now reads serif, fixed-width, italic, and forced-bold
descriptor flags. Exact standard fonts retain precedence, and known Arial,
Helvetica, Verdana, and Trebuchet families remain proportional despite misleading
fixed-width flags. Eleven cases cover descriptor traits, exact standard fonts,
and these name-based safeguards. The initial six descriptor cases failed before
the change. The final 3,803 engine tests, 352 app tests, and Release publish pass.

The unembedded Bembo title in `210260.pdf` now uses a serif fallback; its difficult
pages 2 and 3 improve from 6.497/5.602 to 5.131/4.972 mean RGB error. Across all
674 outputs, eight improve against PDFium, 665 remain identical, and one has a
small increase: `303226.pdf` changes from 6.408115 to 6.412256. Its MIonic font
is explicitly marked serif, and visual inspection confirms the corrected family;
glyph width and spacing still differ from PDFium. The width-fitting follow-up below
improves this page beyond both earlier builds; residual font differences remain open.
The first candidate's Trebuchet regression is removed, and that page is identical
to the baseline in the final run (`font-traits-v3-pixels.json`). The engine hash is
`9480652F6A525BBCF0BAC1878D3F5C8FE4E88A654B57342267AC323325DEA049`.

Bundled substitutes for nonstandard simple fonts now fit their outlines horizontally
to explicit positive PDF widths. Standard metrics, embedded and resolved outlines,
vertical and composite fonts, and absent or nonpositive widths retain their previous
behavior. Fitted outlines are cached by source character code, so two codes mapping
to the same glyph can retain different declared widths. Eight tests cover narrower
and wider glyphs, repeated access, remapping, standard aliases, nonpositive widths,
and embedded and resolved font precedence.

All 674 application renders succeed. All 22 changed images have lower mean RGB
error against PDFium, and 652 remain identical (`font-width-pixels.json`). The
MIonic page `303226.pdf` improves from 6.412256 to 4.7236; visual inspection confirms
better letter spacing, with weight and rasterization differences remaining. Bembo
pages 2 and 3 improve from 5.131/4.972 to 4.837/3.372. This is fidelity evidence,
not a timing measurement or overall text-parity claim. All 3,811 engine tests,
352 app tests, and the Release publish pass. The Release payload is
`parity-20260909-ghent/payload-font-width`, with engine SHA-256
`254BC52B289DEBCDEAB3FD1F7C49681A90B625B29A912E1B9B9E34AB8E7501B4`.

A four-channel Vector256 accumulation experiment for converted-image area
averaging was rejected. Two reversed-order pairs on the large Altona image at
size 1024 used 60 renders per process, excluding the first 30. Scalar/vector
medians were 896.026/974.740 and 834.264/963.722 ms. All 240 outputs have SHA-256
`3B05C5B420F741F18129C2017D50600B304C530D0ECA84E6783AD2F09C165364`
and zero diagnostics. The 9% to 16% slowdown is recorded in
`parity-20260909-ghent/area-vector-summary.json` and the two timing CSVs.
The experiment is removed; renderer source matches the verified width-fitting
checkpoint. No performance gain is claimed from this attempt.

A bounded converted-row cache was also rejected. It retained at most 1 MiB of
pixel samples per converter, avoiding repeated conversion across neighboring
footprints. On the same Altona workload, two reversed-order pairs of 60 renders
(first 30 excluded) produced scalar/cache medians of 893.599/866.656 and
874.098/889.435 ms. All 240 hashes match the scalar reference, but the ordering
reverses the small timing benefit. Extra row storage is not retained. Results
are in `parity-20260909-ghent/area-rows-summary.json` and its two timing CSVs.
Three independent CMYK area-average checks cover fractional dimensions and
both sides of the attempted cache limit; these remain as regression coverage.

A fresh balloon-page sampled-thread-time trace attributes 13.784 of 18.774
seconds of sampled CPU time to JPEG 2000 decoding, including 2.149 seconds
exclusive to sample output. Only intervals containing `CPU_TIME` are counted,
and the first half of the trace is excluded (`balloon-width-cpu-summary.json`).
Direct 8-bit and 16-bit rows now clamp signed values before adding their output
bias, preserving bounds without 64-bit arithmetic. A separate arithmetic check
covers 1,110 signed-limit and clamp-boundary combinations across shifts 0 to 30.

Two reversed-order pairs on the balloon page at size 2048 use 80 renders per
process, excluding the first 40. Baseline/changed medians are 593.033/578.208
and 588.672/576.494 ms, a small 2% to 2.5% page-level improvement. All 320 hashes
are `391277DC61978A0AB08C8F51C2CE6611DDDF266110525B51228FCC8AD09AD18F`,
with zero diagnostics (`jp2-int-clamp-summary.json` and its timing CSVs).
These profiling files are in `parity-20260909-ghent`. The change adds no buffers.
The full application timings above predate this optimization; overall parity
remains open. All 3,814 engine tests, 352 app tests, and the Release publish pass.
All 674 corpus images remain pixel-identical (`jp2-int-clamp-pixels.json`).
The payload is `parity-20260909-ghent/payload-jp2-int-clamp`, with engine SHA-256
`0CDAB4B817FED5DFB64B01A2CA7600CD7895BFDF9D54B02424CF3C2C0444C61E`.

The remaining maintenance brochure port, `4cd5096`, has been reviewed but is
not applied. The maintenance PDF has 50 pages with unchanged page dimensions,
18 fields, 22 widgets, and unchanged field values. Extracted text changes only
the applicable 1.8.3 labels to the 1.8 series, retaining historical result labels.
All 50 pages were rendered at 72 DPI and visually inspected for text overlap
and clipping, including page 40. Page 46 has doubled radio-button outlines in
both the current and maintenance copies; this is an existing visual issue.
Rendered evidence is in `review-20260909/brochure-maintenance` under the scratch
root. The reviewed maintenance PDF SHA-256 is
`4C3BBD6FB871F7045154FCADCEACDC20D8B53F589D561BD7E5503595AAEE79A3`.
It remains a 1.8 brochure and does not describe the 1.9 rendering architecture.
The binary replacement is pending clarification of the atomic-tool-only rule
against the separate verified binary-write procedure. No PDF was overwritten.

A fresh 80-render profile of response-to-fiber-concerns attributes 5.419 of
11.127 sampled CPU seconds to image painting after excluding the first half
of the trace and counting only `CPU_TIME` intervals. Its RGB image has a
fully opaque image soft mask inside a CMYK page group. Fully opaque mask
samples now permit the existing direct ink write; partial samples retain
normal compositing, and each mask sample is reused for matte conversion.
The trace is `parity-20260909-ghent/fiber-clamp-current.speedscope.json`.

Two reversed-order pairs of 80 renders at size 2048, excluding the first 40
per process, produce baseline/changed medians of 238.933/207.043 and
241.906/207.267 ms. This is a 13% to 14% page-level improvement without new
buffers. All 320 hashes are
`87E83A736E5D6C2D350A6CB84AAE1AD4597855F7E0ECC5E512DE76DF4918262F`,
with zero diagnostics. Timing CSVs and `opaque-image-mask-summary.json` are
in `parity-20260909-ghent`. All 3,818 engine tests, 352 app tests, and the
Release publish pass. All 674 corpus outputs remain pixel-identical to the
sample-clamping checkpoint (`review-20260909/opaque-image-mask-pixels.json`).
The payload is `parity-20260909-ghent/payload-opaque-image-mask`, with engine
SHA-256 `CC0D65060B95F08C0F4E5D05B3FB70F1F8A4F90EC0642D81ABDA4030AAB042F7`.
The earlier whole-application paired timings do not include this optimization;
overall speed and fidelity parity remain open.

A fresh 60-render profile of scan `42828.0001.001.pdf` attributes 4.809 of
11.639 sampled CPU seconds to area conversion and 4.777 seconds to JBIG2
generic-region line decoding. The first half is excluded and only `CPU_TIME`
intervals are counted (`parity-20260909-ghent/scan-42828-cpu-summary.json`).
Binary area conversion now reuses each row's exact vertical bounds across its
columns. Each converter owns its current row state; no image-sized buffer is
added, and integer coverage and rounding are unchanged.

Two reversed-order pairs at size 2048 use 60 renders per process and exclude
the first 30. Baseline/changed medians are 390.602/355.997 and 377.123/354.294
ms, a 6% to 9% page-level gain. All 240 hashes are
`A71BE1DB46524A81F558A807DE137923EF838F0E932F048CD3A3E96FAA7EA0E4`,
with zero diagnostics. The two timing CSVs and `binary-row-bounds-summary.json`
are in `parity-20260909-ghent`. All 3,818 engine tests, 352 app tests, and the
Release publish pass. All 674 images remain pixel-identical to the opaque-mask
checkpoint (`review-20260909/binary-row-bounds-pixels.json`). The payload is
`parity-20260909-ghent/payload-binary-row-bounds`, with engine SHA-256
`872E864EE61B0E4365487123BD7FB37EBBD6935A79984E5E6D3815B4358857E7`.
The current paired application table describes `5bddb6c` before this row-bound
optimization. Overall parity remains open, including the JBIG2 decoding gap.

JBIG2 arithmetic contexts now store the predicted bit alongside the seven-bit
probability state in one byte. State updates preserve the prediction, toggles
preserve the state, and copies remain independent. Two regression tests cover
all 128 stored state values, alternating predictions, truncation, and copying.
The standalone `ContextAllocation` probe constructs 100 contexts after warmup:
each 65,536-entry context allocates 65,592 bytes instead of 131,160 bytes.
This halves context storage, not total decoder or application memory.

On the same scan, two reversed-order pairs of 60 renders at size 2048 exclude
the first 30 per process. Baseline/changed medians are 369.615/360.868 and
365.914/362.119 ms, a small 1% to 2.4% page-level gain. All 240 hashes remain
`A71BE1DB46524A81F558A807DE137923EF838F0E932F048CD3A3E96FAA7EA0E4`,
with zero diagnostics. Timing and allocation evidence is retained in
`parity-20260909-ghent/jbig2-packed-context-summary.json` and the timing CSVs.
All 3,820 engine tests, 352 app tests, and the Release publish pass. All 674
corpus outputs match the row-bound checkpoint exactly
(`review-20260909/jbig2-packed-context-pixels.json`). The payload is
`parity-20260909-ghent/payload-jbig2-packed-context`, with engine SHA-256
`3A10201A3E3E83FCE67E43F1C9E19801DF56539A0609B9B09F7FAEBE295B861E`.
The current paired application table predates this context change; overall
speed, memory, visual, and interactive parity still require verification.

A contiguous JBIG2 probability-table experiment was rejected. It preserved
all 47 rows and 188 values, and reused the selected row during arithmetic
decoding. Two reversed-order pairs on the scan, with 60 renders per process
and the first 30 excluded, produced baseline/candidate medians of
333.706/332.104 and 345.048/355.548 ms. The small first-pair improvement
reversed to a roughly 3% slowdown. All 240 hashes matched, with zero
diagnostics, but the runtime change was removed. Decoder source again matches
the verified compact-context checkpoint. The candidate build and timing CSVs
remain in `parity-20260909-ghent/jbig2-flat-table*` for reference.

A fresh comparison of all 674 current images is retained in
`review-20260909/jbig2-context-fidelity.json`. Mean pixel differences rank
investigation targets, not correctness. The two largest shared differences,
`pdfa2-6-1-13-bfo-t04-fail.pdf` and `pdfa2-6-1-13-bfo-t06-pass.pdf`, each
contain equal and opposite extreme translations followed by a red rectangle.
The engine preserves the cancellation and rectangle; PDFium renders blank.
Do not remove this content merely to reduce pixel differences.

The next shared case is a confirmed missing-content defect:
`preservation/openpreserve-format-corpus/govdocs1-error-pdfs/error_set_1/447403.pdf`.
The engine renders only a small terrain image instead of the full earthquake
map, charts, and labels. Its single Flate content stream has 7,211,743 encoded
bytes and expands completely to 171,848,462 bytes. A bounded chunked zlib
inspection reached the final text operators and closing graphics-state restore.
`PdfPageContentReader` requests the 64 MiB content limit; compatibility Flate
decoding returns that bounded prefix, and the content reader trims it further
to append a separator. The rest is lost. The profiled render reports zero
diagnostics, so successful batch status does not prove complete page output.
The current shared mean RGB error is 42.4904. Bounded large-content processing
and an explicit diagnostic whenever content is truncated remain required;
raising the cap alone would not address the memory goal or silent truncation.

A streaming layout probe now validates all 190 raw inline-image sample lengths
against their actual EI boundaries, reaching the complete decoded stream with
1 MiB reads. Image samples account for 160,728,260 bytes; the largest individual
image contains 9,450,000 bytes. The remaining 11,120,202 bytes contain content
syntax and image dictionaries. Results, offsets, and source/decoded hashes are
in `review-20260909/large-content-layout.json`. This supports incremental
instruction processing without increasing the existing per-image limit. Some
images omit whitespace before EI, so the existing compatibility recovery must
remain available across buffer boundaries.

The rendering path accepts an instruction enumerable, but its page reader first
materializes a list and the page-instruction cache limits entries to 32 without
a byte budget. Inline-image instructions copy their payloads, and rendering
copies them again into PdfStream objects. A complete large-page fix must avoid
retaining the entire decoded image set through this cache. These are verified
implementation constraints, not a completed streaming implementation or a new
rendering result.

The internal content reader now supports resumable prefixes and bounded stream
enumeration. Incomplete operands, comments, operators, and inline images remain
in the pending buffer; completed instructions keep their original global offsets.
The original map passes a direct streaming parser probe: 734,227 instructions,
all 190 images and 160,728,260 sample bytes, ending at Q at byte 171,848,460.
The scratch probe is `parity-20260909-ghent/LargeContent.csproj`. Its single
observed run took 989.887 ms and peaked at 241.863 MiB process working set;
these are parser-only observations, not application performance claims.
Six new parser tests cover every split position in representative content,
binary image boundaries, repeated buffer growth, global offsets, limits, and
cancellation. All 3,826 engine tests pass. Renderer integration, decoded-stream
filter handling, cache retention, and explicit truncation diagnostics remain
unfinished; this change alone does not restore the rendered map.

Renderer integration now restores the complete earthquake map. Large pages
bypass the materialized instruction cache and use sequential content streams,
preserving pending operands and graphics state across the stream array. Raw
content and ordinary Flate streams decode incrementally; other filter pipelines
retain bounded decoding and report failures or streaming limits as diagnostics
in compatibility mode. The public materialized editing API retains its limit.
Three additional tests verify rendering beyond 64 MiB in strict and compatibility
modes, operands crossing content-stream boundaries, and a streaming decode diagnostic.
All 3,829 engine tests and 352 app tests pass, and the Release payload publishes.

The new payload is `parity-20260909-ghent/payload-streaming-content`, with engine
SHA-256 `FCB4716668ED85050A3E0A40CB24FE02C13363FD52BBDB11F4D24C5760A44466`.
All 674 corpus outputs succeed. `review-20260909/streaming-content-pixels.json`
records 673 identical images and only the repaired map changed. Its mean RGB
difference from PDFium falls from 42.4904 to 5.0726; the fraction of pixels with
maximum channel error above 32 falls from 34.47% to 6.27%. Visual inspection
confirms the maps, charts, labels, and photos are restored. Remaining pixel
differences still need review. The direct render takes 2,703.845 ms with zero
diagnostics; the old 708.534 ms observation rendered incomplete content and is
not a valid full-page speed baseline. The earlier opaque-mask timing and memory
checkpoint predates this repair and is superseded by the paired refresh below.

The paired checkpoint has now been refreshed against the retained PDFium app.
All 16 passes and all 5,392 image comparisons pass. The table at the top uses
these new medians and ranges. Shared render time remains about 12.4% slower,
with median peak working set about 24.8% lower. Difficult render time remains
about 92.6% slower, with median peak working set about 0.8% higher. These are
batch observations, not an interactive memory or overall parity claim.
The repaired map itself measures 2,452 versus 2,251 ms median per page.
The largest difficult-corpus gaps remain balloon JPEG 2000 (1,148 versus 513 ms),
Ghent ALL page 2 (653 versus 146 ms), and mipeng poster (975 versus 526 ms).
Shared Altona's large technical page remains 1,131 versus 766 ms. These measured
gaps guide the next work; the map repair does not complete the parity goal.

A current Ghent page-2 CPU profile is retained as
`parity-20260909-ghent/ghent-streaming.nettrace` and the matching speedscope and
CPU summary files. Counting only CPU_TIME intervals after the first half gives
8,300.264 ms sampled CPU time. Major exclusive costs include GC polling
(1,118.433 ms), RenderForm (961.992 ms), SetInkPixel (885.296 ms), coverage
painting (640.296 ms), and memory copying (529.526 ms). These samples identify
where to investigate; they are not independent elapsed-time measurements.

An unchanged-pixel shortcut in non-isolated CMYK group compositing was tested
and removed. Two reversed-order pairs of 60 renders, excluding the first 30
per process, produced baseline/experiment medians of 225.208/217.288 and
217.227/220.130 ms. The first improvement reversed in the second pair.
All 240 pixel hashes matched, with zero diagnostics. Evidence remains in
`parity-20260909-ghent/unchanged-group*`; renderer source matches the validated
large-content checkpoint. No runtime optimization was retained from this test.

A separate `AllocationProfile.csproj` probe initially suggested about 50.6 MB
of managed allocation per steady Ghent page-2 render. Its assembly resolution
was subsequently found to select another scratch DLL; the hash-verified
measurements below supersede that initial observation.
`ghent-allocations.nettrace` and `ghent-allocation-types.json` provide a separate
allocation-tick sample over thirty fresh-document renders. Byte arrays dominate
the sampled allocation weights, followed by renderer Point arrays and double
arrays. The type sample includes document opening and is statistical, so its
weights must not be treated as exact render-only byte totals. Allocation call
stacks are the next investigation target before choosing a buffer or cache change.

Allocation stacks are now available in `ghent-allocation-stacks.json`, decoded
from the existing trace with `AllocationStacks.csproj`. All 5,505 sampled events
in the retained half have stacks. The largest byte-array samples belong to the
document's input copy and the rendered output buffer. Those have ownership
requirements and are not automatically removable. JPEG decoding and flattened
glyph Point arrays follow. The trace includes opening the document, whereas the
separate allocation harness measures only Render.

An ICC intent-table sharing experiment passed 173 focused tests, including a
synthetic allocation and color-equivalence check, but was removed after the
real-page allocation probe showed little benefit. Hash-verified median allocated
bytes over the last ten of twenty renders were 50,730,356/50,624,580 on Ghent
and 154,102,432/154,102,688 on Altona (baseline/experiment). All 80 page hashes
matched within their workload, with zero diagnostics. The result is too small
to pursue as the explanation for Ghent's allocation pressure. Production source
and tests again match the validated large-content checkpoint.

The allocation harness now searches explicit HintPath references first and uses
distinct output/intermediate directories. MSBuild's candidate-assembly search
had selected another DLL from the scratch directory despite the intended hint.
Only `icc-shared-verified-allocation.csv` and its summary are valid comparisons:
baseline engine hash is `D7B83C01599F8A074C3FA4FA71B1C80ECABBB4DA5867725B27E07F185C0021D0`,
experiment hash is `9B9E2D3EF12B3827C5A1FDEDBD0585ADE0D83DB31EB1BA8CD1A577FF1E812664`.
The earlier `icc-shared-allocation.csv` and `icc-shared-altona-allocation.csv`
are invalid for before/after claims and are retained only as investigation history.

A per-glyph contour scratch-buffer experiment was also removed. On Ghent page 2,
average Render allocations over the last ten of twenty passes decreased from
50,616,240 to 50,003,197 bytes (1.2%). Two reversed-order timing pairs of sixty
renders, excluding the first thirty per process, gave baseline/experiment medians
of 227.432/239.238 and 229.234/229.687 ms. The small allocation saving did not
produce a timing benefit. All 280 rendered pixel hashes matched with zero
diagnostics. The experiment built successfully; production source was then
verified identical to the validated baseline, so full tests were not repeated.
Evidence is in `parity-20260909-ghent/glyph-scratch-allocation.csv` and
`glyph-scratch-timing.csv`. The measured experiment engine hash was
`BF0F9C678C9F4573E68FEBE71A9A200081E6468F671CA14B421AFACF8F823130`;
both allocation harness DLL copies were verified against their intended builds.

### Direct CMYK JPEG output

JPEGs with four horizontally full-resolution components and no color transform
now interleave component rows directly. Vertical sampling still uses the existing
row mapping; transformed and horizontally subsampled images retain the general
path. Added sixteen odd-size baseline/progressive sampling cases across all four
reductions. All 133 focused JPEG tests passed. The full run first hit the ICC
destination-curve allocation assertion (7,752 unexpected bytes); that unrelated
test passed alone and the complete rerun passed all 3,845 engine tests. All 352
app tests and the Release payload publish passed.

Two reversed-order pairs of sixty Ghent page-2 renders, excluding the first
thirty per process, produced baseline/new medians of 227.496/224.481 and
221.642/220.547 ms (0.5% to 1.3% lower). All 240 hashes matched with zero
diagnostics. This is a small page-specific observation, not overall speed parity.
The 74 difficult and 600 shared corpus pages all completed and matched the
previous validated output pixel for pixel, including the complete large map.
Evidence remains under `C:/Users/steve/kp-bench-render`: timing and test logs in
`parity-20260909-ghent/jpeg-direct*`, payload in `payload-jpeg-direct` beneath
that directory, and corpus results in `review-20260909/jpeg-direct*`.
The measured and published engine DLL hash is
`F6DC0FC8830430DD744E588BDC13C0AA802E446075C8E1282BC1F95DE9B538D1`.

Extending the RGB row-based group interpolation loop to uniformly opaque CMYK
surfaces was tested and removed. Reversed-order Ghent pairs gave baseline/test
medians of 224.539/241.205 and 225.142/225.878 ms after thirty warmup renders
per process. All 240 pixel hashes matched with zero diagnostics, but there was
no timing benefit. The renderer was verified identical to the validated CMYK
JPEG checkpoint after removal; no additional full-suite run was needed.
Evidence is in `parity-20260909-ghent/opaque-group-rows*`; the tested engine hash
was `F34059A9BF3FD79DBD79C266884F82F57BD54209D161AD4056B1C7A4748686DA`.

### Vectorized opaque CMYK blending

A temporary diagnostic build counted 903,961 SetInkPixel calls on Ghent page 2.
Partial-opacity normal paint over an opaque backdrop without overprint accounted
for 296,871 calls; transparent backdrops accounted for another 296,457 calls.
The diagnostic counters were removed before production measurements. The probe's
render hash matched the baseline; its timing is not a performance measurement.

The opaque normal-blend case now processes four channels together when AVX and
SSE2 are available, retaining the existing scalar fallback. Operation order,
double precision, clamping, and nearest-even rounding are preserved. Standalone
scalar/vector comparisons passed 17,039,360 cases, including subnormal opacity.
Nine additional tests check every byte pair with distinct channel values and
representative opacity boundaries; all pass with intrinsics enabled and disabled.
All 44 focused rendering tests, 3,854 engine tests, 352 app tests, and the Release
payload publish passed. All 674 corpus pages match the preceding JPEG checkpoint.

Two reversed-order pairs of sixty Ghent renders, excluding the first thirty,
gave baseline/new medians of 287.546/285.415 and 286.498/282.578 ms, a 0.7% to
1.4% reduction. All 240 hashes matched with zero diagnostics. Session timings
are higher than the earlier JPEG session, so cross-session comparisons are not
valid. The standalone blend is roughly twice as fast; the much smaller measured
page gain is the relevant rendering result and does not establish overall parity.
Evidence under `C:/Users/steve/kp-bench-render/parity-20260909-ghent` includes
`ink-blend-probe-ghent-result.txt`, `ink-vector-packed-check-results.json`,
`ink-vector-timing.csv`, the test logs, and `payload-ink-vector`.
Corpus evidence is in `review-20260909/ink-vector*` under the benchmark root.
Measured and published engine SHA-256:
`AC9B6CC44CCE66B469319FC889A758DE415539B5D234EFCD320ABFDEC8A143A6`.

### Transparent CMYK backdrop shortcut

Paint over a zero-alpha CMYK backdrop now copies native sample bytes when source
opacity is between 1e-300 and 1. The threshold keeps nonzero sample products away
from underflow, so normalization cannot change the final rounded byte. Smaller
opacity retains the original arithmetic. An independent numeric check covered
26,426,880 sample/opacity combinations over all byte values, exponent boundaries,
threshold neighbors, and deterministic random floating-point values. It found
5,354 differing tiny-opacity cases below the threshold, confirming the need for
the fallback, and no differing cases in the shortcut's range.

All 35 focused rendering tests, 3,854 engine tests, 352 app tests, and the Release
payload publish passed. All 674 corpus pages match the preceding vector-blend
checkpoint pixel for pixel. Two reversed-order Ghent pairs, sixty renders each
with thirty warmups, produced baseline/new medians of 288.228/283.514 and
288.504/286.234 ms (0.8% to 1.6% lower). All 240 hashes matched with zero
diagnostics. Overall parity remains open.

Evidence is under `C:/Users/steve/kp-bench-render`: numeric checks, timings, test
logs, and `payload-transparent-ink` in `parity-20260909-ghent`, and corpus output
in `review-20260909/transparent-ink*`. The measured and published engine hash is
`89AD727550B0CD6F12D2030D3DE20653B33D083286A5C4A33DC7F4432DBEA0D0`.

The refreshed whole-application comparison completed sixteen passes with no
failures. All 5,392 images match their respective engine/PDFium baselines.
The largest difficult-page render gaps are balloon_a1b_jp2k (1,158/446 ms),
Ghent ALL page 2 (659/140 ms), and Ghent ALL page 1 (530/124 ms). Shared leaders
are the complete large map (2,194/1,824 ms), Altona measure (398/72 ms), and the
balloon document (486/167 ms). Values are paired median engine/PDFium times.
These are the next performance priorities; microbenchmark improvements have
not closed the overall gap. The report script uses explicit UTF-8 CSV decoding
for corpus filenames. Its first analysis reached the page-ranking step after
image verification, then failed on default Windows decoding; the corrected
analysis completed and repeated all image checks successfully.

Primary-page rendering now retains one engine session per viewer pane across
zoom sizes. Document switches, immutable-document invalidation, view-mode
changes, and pane cache flushes release it. Bitmap invalidation advances a
document revision so a subsequent render cannot reuse the old snapshot.
Background render tasks retain independent session ownership.

The engine-level zoom probe uses RenderInto, avoiding rendered-page cache hits.
On balloon_a1b_jp2k.pdf, fresh/reused/reused/fresh medians are
771.255/190.131/202.389/750.554 ms across three sizes. All 48 images match, with
no diagnostics. Three document probes retain an additional 28 to 36 MiB of
managed data, which becomes collectible after releasing the renderer. These
are not interactive latency or working-set measurements. Evidence is in
`parity-20260909-ghent/session-reuse-summary.json` and
`session-memory-summary.json` under the local benchmark root.

All 356 app tests and the Release payload publish pass after integration.
Four new cases cover snapshot reuse, painted zoom output, independent pixel
ownership, file rewrites, explicit clearing, and switching files. The existing
boundary test now recognizes the retained primary path. Engine source is
unchanged; the earlier 3,854-test engine checkpoint remains the engine test
evidence. The new payload is `payload-primary-reuse`; interactive first-page,
scrolling, zoom, and edit/reload validation remain open.

The published application's own rendering boundary confirms the reuse gain on
the balloon document: fresh/reused/reused/fresh medians are
736.186/182.069/183.422/695.986 ms. All 48 renders match across the three zoom
sizes with no diagnostics. This invokes the retained primary helper and fresh
session through the published assembly, including the application font resolver
and render parallelism. It does not measure WPF presentation or compare with
PDFium. The measured assemblies and limits are recorded in
`parity-20260909-ghent/app-reuse-summary.json`.

Continuous base rendering, continuous zoom sharpening, and secondary tiles now
reuse sessions through exclusive task leases. Each pane retains at most one
idle background session; overlapping tasks retain separate active sessions.
Requests capture the document path and revision on the UI thread. Cache clears
invalidate outstanding requests, and late returns from those requests are
disposed instead of becoming the current cached session. Opening and rendering
never hold the cache lock. Cancellation reaches both lease acquisition and
the engine render operation.

All 362 app tests and the Release payload publish pass. Six new cases cover
exclusive ownership, idle reuse, late requests and returns after invalidation,
one-idle-session retention, and cancellation. The payload and build log are
`parity-20260909-ghent/payload-background-reuse` and
`background-reuse-publish.log`. Background interactive speed, screen
presentation, and working-set measurements remain open; the earlier primary
rendering timings do not prove those gates.

The published background lease boundary takes 254.567/75.978/76.413/253.301 ms
in a fresh/reused/reused/fresh balloon-page comparison. Each run cycles three
continuous-style width/height limits and returns its lease after every render.
All 48 outputs match with no diagnostics. Measurements and the assembly hash
are in `parity-20260909-ghent/background-reuse-summary.json`. This sequential
boundary probe excludes concurrent task scheduling and WPF presentation;
interactive and PDFium parity gates remain open.

A coordinated two-worker regression now acquires separate leases, replaces
the document while both workers are outstanding, then lets both render in
parallel. Their original painted pixels remain identical and independently
owned. Late returns leave the replacement document's idle session intact.
All 363 app tests pass. This verifies the cache race without claiming UI
scheduling or presentation coverage; no runtime code changed in this check.

Application bitmap sizing now applies the measured legacy decimal coordinate
conversion to page boxes, preserving accurate engine parsing and rendering
geometry. All 674 published application outputs match PDFium dimensions.
Only hisn13056.pdf and UA1_Tpdf-G1_F01.pdf change from the earlier engine
baseline; the remaining 672 decoded images are identical. Every batch row
reports OK. Outputs are under `review-20260909/legacy-geometry-*` in the local
benchmark root; the payload and test/build logs use `legacy-geometry` in
`parity-20260909-ghent`.

All 370 app tests and the Release publish pass. Seven coordinate cases match
direct native probes. The scaled-page test for height 301.999999 now expects
150 pixels at half scale: the retained native library reports 301.9999694824219
points, confirming that the former 151-pixel expectation used a direct cast
instead of the legacy parser. Geometry compatibility is verified at the corpus
sizes; overall visual, speed, and interactive parity remain open.

### Pattern text fill checkpoint (2026-09-09)

PatternTextInsideText.pdf now paints its red text pattern inside the blue
outlined glyphs. Across the 674 Broad and Shared pages, only this image changes;
the other 673 remain pixel-identical and every output retains its dimensions.
The published app renders all batch rows successfully. Local evidence is under
`C:\Users\steve\kp-bench-render\review-20260909\pattern-text-*`, with the
payload under `parity-20260909-ghent/payload-pattern-text`.

Both new pattern clipping tests pass. All 3,856 engine tests pass with test
parallelism disabled, and all 370 app tests pass. The parallel engine suite
exposes a clip-buffer allocation failure that passes alone and in the serial
suite. The subsequent pool investigation below identifies the underlying cause.
Overall visual, speed, and interactive parity remain open.

### Scratch pool eviction checkpoint (2026-09-09)

The 64-item limit previously evicted the largest idle buffer even when the byte
budget had room. Small idle arrays could therefore displace nearly all later
large buffers. Count-limit eviction now removes the smallest idle buffer;
byte-budget eviction still removes the largest. Both limits are unchanged.
A deterministic regression verifies reuse of 32 large buffers after filling the
pool with 64 tiny buffers. Allocation-sensitive surface tests also use the
existing isolated collection to avoid unrelated concurrent pool traffic.

All 3,857 engine tests pass in the normal parallel suite, all 370 app tests pass,
and Release publish succeeds. This corrects buffer reuse, without establishing
a whole-page speed improvement or overall engine parity.
All 674 Broad and Shared renders are pixel-identical to the pattern-text
checkpoint, with unchanged dimensions and OK batch rows. Evidence is under
`review-20260909/pool-eviction-*` and `parity-20260909-ghent/payload-pool-eviction`
in the local benchmark root.

### Unknown image filter checkpoint (2026-09-09)

UnknownFilter-ImageXObject.pdf no longer paints encoded bytes as a noisy black
rectangle. Unknown image filters produce a diagnostic; general-stream recovery
still permits unknown-filter pass-through. Both direct and array filter forms
have rendering regressions that failed before the fix and now pass.

All 3,859 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 corpus rows report OK with unchanged output dimensions. Only this
malformed-image page changes; the other 673 images remain pixel-identical.
The remaining red text is visually consistent with the native reference,
although the complete images are not byte-identical. Evidence is under
`review-20260909/unknown-image-*` and `parity-20260909-ghent/payload-unknown-image`
in the local benchmark root. Broader visual and performance gates remain open.

### Installed standard-font aliases checkpoint (2026-09-09)

The desktop resolver now maps Helvetica and Times aliases to installed Arial
and Times New Roman faces, preserving styles and exact requested-family
precedence. Missing installed families still use the engine's bundled fallback.
No additional font data is shipped. Engine source is unchanged.

All 33 standalone resolver checks and 370 app tests pass; Release publish passes.
All 674 corpus rows report OK and retain the same dimensions. Of 220 changed
pages, 219 have lower mean absolute RGB error against PDFium. The other 454
pages are pixel-identical. The one higher score changes only nine pixels on a
horizontal line in 507618.pdf, increasing the total absolute channel error by
24. OverlappingGlyphClipping.pdf drops from 16.9909 to 0.2237 mean error, and
PatternTextInsideText.pdf drops from 28.8898 to 3.0027. These scores support
the specific font-selection improvement, not overall visual parity.

Evidence is under `standard-latin-probe-20260909/corpus-comparison.json` and
`review-20260909/standard-alias-*` in the local benchmark root. The payload is
`parity-20260909-ghent/payload-standard-alias`. Earlier paired speed results
predate this desktop resolver change.

### Pattern text stroke checkpoint (2026-09-09)

Glyph strokes now honor the selected pattern, preserving ordinary solid-stroke
rendering. Both new regressions failed with zero pattern-colored pixels before
the fix and pass afterward. A four-page native comparison covers stroke-only,
fill-and-stroke, and both text-clipping modes; visual inspection confirms the
pattern, fill, and subsequent clipped artwork in each mode.

All 3,861 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 Broad and Shared pages remain pixel-identical to the installed-font
alias checkpoint, with unchanged dimensions and OK batch rows. The corpus
does not exercise this missing stroke behavior; the dedicated comparison does.
Evidence is under `pattern-stroke-probe-20260909` and
`review-20260909/pattern-stroke-*` in the local benchmark root, with the payload
under `parity-20260909-ghent/payload-pattern-stroke`. Overall parity remains open.

### Tiling-pattern transparency checkpoint

Tiling patterns now apply the painted object's transparency once after drawing
their cells, with default transparency inside the pattern and the existing
backdrop available for internal blending. Patterned strokes use stroke opacity.
Four regressions cover overlapping marks, distinct fill and stroke opacity,
and internal Normal and Multiply blending. The first two failed before the fix.
This follows PDF 32000-1 section 11.6.7; the retained native renderer ignores
outer stroke opacity in the dedicated fixture, so its opaque result is not the
expected reference for that case. The rendered translucent stroke was inspected.

All 3,865 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 Broad and Shared pages remain pixel-identical to the pattern-stroke
checkpoint, with unchanged dimensions and OK batch rows. Evidence is under
`pattern-stroke-probe-20260909/opacity-*` and
`review-20260909/pattern-opacity-*` in the local benchmark root; the payload is
`parity-20260909-ghent/payload-pattern-opacity`. Shading-pattern transparency,
broader visual parity, and performance parity remain open.

### Shading-pattern stroke opacity checkpoint

Shading-pattern strokes now use stroke opacity instead of fill opacity. Two
regressions independently vary CA and ca, verify both the stroke and fill,
and fail before the correction. All 3,867 engine tests and 370 app tests pass;
Release publish succeeds. All 674 Broad and Shared pages remain pixel-identical
to the tiling-opacity checkpoint with unchanged dimensions and OK batch rows.
Evidence is retained as `shading-opacity-*` logs, the
`pattern-stroke-probe-20260909/shading-opacity-corpus-comparison.json` comparison,
and `review-20260909/shading-opacity-*` renders in the local benchmark root.
The payload is `parity-20260909-ghent/payload-shading-opacity`. Shading-pattern
graphics-state overrides and overlapping mesh transparency remain unverified.

### Zero-length dash checkpoint

Zero-length painted dash entries now retain round or square caps instead of
disappearing. Square marks retain the line direction, including diagonal paths.
Nine regressions cover all three cap styles, horizontal and diagonal lines,
positive and negative phases, and subpath restarts. Four cap regressions fail
before the fix. The dedicated three-page native comparison confirms restored
round and square marks; their rendered shapes were inspected. Native spacing
drifts slightly on this fixture, while the engine preserves the declared cycle.

All 3,876 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 Broad and Shared pages remain pixel-identical to the shading-opacity
checkpoint with unchanged dimensions and OK batch rows. The negative-phase
corpus page remains unchanged; its odd-length cycle differs from the native
renderer and was not replaced with native spacing. Evidence is retained under
`zero-dash-20260909` and `review-20260909/zero-dash-*` in the local benchmark root.
The payload is `parity-20260909-ghent/payload-zero-dash`. Overall parity remains open.

### Reduced stencil detail checkpoint

Reduced one-bit stencil images now average source coverage into a bounded alpha
plane instead of selecting individual bits. This restores thin text strokes in
the scanned All Quiet on the Third Coast article. Eight regressions cover both
decode directions, clipping, and partial nonstroking opacity; all fail before
the fix and pass afterward. The article and affected Ghent mask and font pages
were visually inspected. Article mean RGB error against native falls from
11.9876 to 2.8804; that remaining difference is not visual parity.

All 3,884 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 Broad and Shared pages retain matching dimensions and OK batch rows.
Twelve pages change, each with lower mean RGB error against native; 662 remain
pixel-identical. Evidence is retained as `stencil-area-*` logs and comparison
JSON, plus `review-20260909/stencil-area-*` renders in the local benchmark root.
The payload is `parity-20260909-ghent/payload-stencil-area`. The subsequent paired
timing results at the top include this change and leave speed parity open.

### Packed coverage edge counting checkpoint

Partial bytes in binary-image coverage now use masked population counts instead
of reading each bit separately. Exact coverage-grid regressions include short
footprints spanning byte boundaries. All 3,886 engine tests and 370 app tests
pass, Release publish succeeds, and all 674 corpus images remain pixel-identical
to the stencil-area checkpoint, with unchanged dimensions and OK batch rows.

Two fresh-render workloads were measured in A/B/B/A and B/A/A/B order. Runs use
80 renders with 40 discarded, then 160 with 80 discarded. All 1,920 renders
have matching hashes per workload and zero diagnostics. Six of eight pairs
favor the change; two do not. Median run medians fall from 60.320 to 56.542 ms
for the article at 1024 pixels and from 66.376 to 62.826 ms for the unknown-filter
fixture at 2048 pixels. Timing ranges overlap, so these are limited workload
results, not a whole-application speed claim. The earlier full paired benchmark
at the top remains the current overall comparison.

Evidence is under `binary-count-20260909`, `binary-count-*` logs, and
`review-20260909/binary-count-*` in the local benchmark root. The payload is
`parity-20260909-ghent/payload-binary-count`. Overall parity remains open.
