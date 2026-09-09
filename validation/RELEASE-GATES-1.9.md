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
| Memory at parity or close to the PDFium pipeline | Shared batch lower; difficult batch higher; interactive use unverified | September 9 installed-layout median peak working set is 525.6 versus 614.6 MiB shared and 266.3 versus 260.4 MiB difficult, including grayscale lookup, TIFF prediction, and transparent-backdrop compositing improvements. Difficult engine peaks range from 265.1 to 270.6 MiB, with a median about 2% above PDFium. Verify representative interactive document use and an explicit acceptable tolerance before release. |
| Rendering and whole-pass speed without regression | Open | Three alternating installed-layout measured runs put shared render medians at 13.499 versus 11.877 seconds; shared wall time is 25.190 versus 22.501 seconds. Difficult render time is 9.715 versus 4.763 seconds and wall time is 14.850 versus 9.886 seconds. The focused optimizations do not establish general speed parity. |
| Rendering fidelity without regression | Open | Scaled geometry and crop fixes leave two one-pixel height differences among 600 shared outputs and none among 74 difficult outputs. The CMYK display fix matches 6,561 legacy swatches and lowers mean RGB pixel error on all 59 changed sample images. Color, compositing, font, and fine-detail differences still need disposition; RGB-to-CMYK conversion remains an approximation. |
| Startup, first-page display, scrolling, and zoom without regression | Unverified | Controlled interactive measurements; the startup marker does not measure first-page completion or scrolling. |
| Existing 1.8 functionality and maintenance fixes preserved | Partially verified; open for release | Recorded 33 verified maintenance ports against local main at 5dd609f. The guard still reports three brochure and website commits requiring disposition. The stable source link and development release-date metadata are corrected. Applicable feature workflows still need release-build verification. |
| Builds and regression suites | Passing development checkpoint | September 9: 3,787 engine tests, 352 app tests, and the Release payload publish pass with packed image color-cache reads. The earlier exhaustive RGB check also passes with hardware intrinsics disabled. The packed engine works in an isolated JPEG 2000 consumer. Repeat required checks for the final release build; these checks alone do not close other gates. |

Current evidence is archived locally at `C:/Users/steve/kp-bench-render/review-20260909/README.md`,
with all warmups, alternating runs, retained images, and DLL hashes. The latest paired
timings are in `ink-transparent-paired-results.csv`, from the rendering code committed as
`cf976c1`, including grayscale lookup, TIFF prediction, and transparent-backdrop compositing.
All 16 passes completed without
failures; run zero is excluded as warmup. The timing and memory figures above
describe this payload. All 5,392 outputs across four passes of both applications
match their respective retained images. The comparison, timing ranges, and
per-page gap rankings are in `ink-transparent-paired-analysis.json`. Both applications were
published locally with the framework-dependent Windows payload settings used
by the packaging script. These supersede the woven development-build timings
in `committed-cmyk-results.csv`; all 674 retained images per application match
between the two layouts in the earlier `payload-cmyk` layout comparison.
No installer or release was created.

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
The full application comparison above predates this change. Averaging overhead,
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
The paired application comparison above predates this change. Overall performance,
rendering fidelity, and interactive parity gates remain open.
