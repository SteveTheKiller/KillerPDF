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
| Memory at parity or close to the PDFium pipeline | Shared batch lower; difficult batch higher; interactive use unverified | September 9 vectorized installed-layout median peak working set is 534.7 versus 614.8 MiB shared and 266.3 versus 260.4 MiB difficult. Difficult engine peaks range from 265.4 to 279.0 MiB, with a median about 2% above PDFium. Verify representative interactive document use and an explicit acceptable tolerance before release. |
| Rendering and whole-pass speed without regression | Open | Three alternating installed-layout measured runs put shared render medians at 11.978 versus 12.364 seconds; shared wall time is 23.754 versus 23.112 seconds. Difficult render time is 9.136 versus 4.737 seconds and wall time is 14.271 versus 9.870 seconds. The focused optimizations do not establish general speed parity. |
| Rendering fidelity without regression | Open | Scaled geometry and crop fixes leave two one-pixel height differences among 600 shared outputs and none among 74 difficult outputs. The CMYK display fix matches 6,561 legacy swatches and lowers mean RGB pixel error on all 59 changed sample images. Color, compositing, font, and fine-detail differences still need disposition; RGB-to-CMYK conversion remains an approximation. |
| Startup, first-page display, scrolling, and zoom without regression | Unverified | Controlled interactive measurements; the startup marker does not measure first-page completion or scrolling. |
| Existing 1.8 functionality and maintenance fixes preserved | Partially verified; open for release | Recorded 33 verified maintenance ports against local main at 5dd609f. The guard still reports three brochure and website commits requiring disposition. The stable source link and development release-date metadata are corrected. Applicable feature workflows still need release-build verification. |
| Builds and regression suites | Passing development checkpoint | September 9: 3,737 engine tests, 352 app tests, and the Release payload publish pass with vectorized inverse interpolation. The exhaustive RGB check also passes with hardware intrinsics disabled. The packed engine works in an isolated JPEG 2000 consumer. Repeat required checks for the final release build; these checks alone do not close other gates. |

Current evidence is archived locally at `C:/Users/steve/kp-bench-render/review-20260909/README.md`,
with all warmups, alternating runs, retained images, and DLL hashes. The latest paired
timings are in `rgb-vector-paired-results.csv`, from the rendering code committed as
`5d0d8ec`, including vectorized interpolation. All 16 passes completed without
failures; run zero is excluded as warmup. The timing and memory figures above
describe this payload. All 674 engine outputs match its retained images; the
comparison and timing ranges are in `rgb-vector-paired-analysis.json`. Earlier
pixel checks link these images to the missing-font checkpoint. Both applications were
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
application outputs. The later paired application measurements above include this
change. Both builds ran faster than in the earlier session, so the difference
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
PDFium measurements above, which predate area averaging. Much of the added cost
is in 42828.0001.001.pdf and balloon_a1b_jp2k.pdf. Optimize the averaging path
while preserving the recovered detail; speed parity remains open.
