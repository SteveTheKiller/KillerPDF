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
| Memory at parity or close to the PDFium pipeline | Shared batch lower; difficult batch higher; interactive use unverified | September 9 installed-layout median peak working set is 533.6 versus 614.2 MiB shared and 274.3 versus 260.2 MiB difficult. Difficult engine peaks range from 265.3 to 274.3 MiB, with a median about 5% above PDFium. Verify representative interactive document use and an explicit acceptable tolerance before release. |
| Rendering and whole-pass speed without regression | Open | Three alternating installed-layout measured runs put shared render medians at 12.532 versus 12.598 seconds; shared wall time is 24.525 versus 23.489 seconds. Difficult render time is 9.335 versus 4.838 seconds and wall time is 14.485 versus 10.046 seconds. The focused optimizations do not establish general speed parity. |
| Rendering fidelity without regression | Open | Scaled geometry and crop fixes leave two one-pixel height differences among 600 shared outputs and none among 74 difficult outputs. The CMYK display fix matches 6,561 legacy swatches and lowers mean RGB pixel error on all 59 changed sample images. Color, compositing, font, and fine-detail differences still need disposition; RGB-to-CMYK conversion remains an approximation. |
| Startup, first-page display, scrolling, and zoom without regression | Unverified | Controlled interactive measurements; the startup marker does not measure first-page completion or scrolling. |
| Existing 1.8 functionality and maintenance fixes preserved | Partially verified; open for release | Recorded 29 verified maintenance ports against local main at 5dd609f. The guard still reports seven release-history, brochure, and website commits requiring disposition. Applicable feature workflows still need release-build verification. |
| Builds and regression suites | Passing development checkpoint | September 9: 3,732 engine tests, 352 app tests, and the Release payload publish pass with the missing-font fallback. The packed engine works in an isolated JPEG 2000 consumer. Repeat required checks for the final release build; these checks alone do not close other gates. |

Current evidence is archived locally at `C:/Users/steve/kp-bench-render/review-20260909/README.md`,
with all warmups, alternating runs, retained images, and DLL hashes. The current
timings are in `payload-cmyk-results.csv`, from `cc4d0a1` with the rendering
code from `42a1571`, before the missing-font fallback. Both applications were
published locally with the framework-dependent Windows payload settings used
by the packaging script. These supersede the woven development-build timings
in `committed-cmyk-results.csv`; all 674 retained images per application match
between the two layouts. No installer or release was created.

The separate ReadyToRun experiment in `r2r-cmyk-results.csv` was not adopted:
shared render time fell to 11.829 seconds, but difficult render time rose to
10.643 seconds and shared whole-pass time rose to 24.836 seconds. Its retained
images are also unchanged. No precompilation setting was changed in the project.
The narrowly scoped color comparison is described in `engine/docs/color-review.md`.

The later missing-font fix at `bb300ef` uses a diagnosed Helvetica fallback with
Windows ANSI encoding for unknown missing resources in compatibility mode.
`font-ansi-pixels.json` records lower mean RGB error against PDFium for all ten
changed outputs and identical pixels for the other 664 outputs. All 674 renders
complete successfully. This recovers visible text from malformed resources;
it cannot reconstruct an absent original font. Strict behavior is preserved.

Unmeasured interactive behavior and known speed and fidelity gaps remain
release gates.
