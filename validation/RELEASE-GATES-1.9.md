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
| Memory at parity or close to the PDFium pipeline | Shared batch lower; difficult batch higher; interactive use unverified | September 9 committed-CMYK median peak working set is 554.0 versus 620.3 MiB shared and 297.9 versus 266.3 MiB difficult. Difficult engine peaks range from 295.9 to 299.5 MiB, about 12% above PDFium. Verify representative interactive document use and an explicit acceptable tolerance before release. |
| Rendering and whole-pass speed without regression | Open | Three alternating measured runs put shared render medians at 12.121 versus 12.955 seconds; shared wall time is 24.399 versus 24.072 seconds. Difficult render time is 9.551 versus 4.947 seconds and wall time is 14.909 versus 10.363 seconds. The focused optimizations do not establish general speed parity. |
| Rendering fidelity without regression | Open | Scaled geometry and crop fixes leave two one-pixel height differences among 600 shared outputs and none among 74 difficult outputs. The CMYK display fix matches 6,561 legacy swatches and lowers mean RGB pixel error on all 59 changed sample images. Color, compositing, font, and fine-detail differences still need disposition; RGB-to-CMYK conversion remains an approximation. |
| Startup, first-page display, scrolling, and zoom without regression | Unverified | Controlled interactive measurements; the startup marker does not measure first-page completion or scrolling. |
| Existing 1.8 functionality and maintenance fixes preserved | Partially verified; open for release | Recorded 27 verified maintenance ports against local main at 5dd609f. The guard still reports nine release-history, brochure, and website commits requiring disposition. Applicable feature workflows still need release-build verification. |
| Builds and regression suites | Passing development checkpoint | September 9: 3,726 engine tests, 352 app tests, and the Release application build pass at the CMYK checkpoint. The packed engine works in an isolated JPEG 2000 consumer. Repeat required checks for the final release build; these checks alone do not close other gates. |

Current evidence is archived locally at `C:/Users/steve/kp-bench-render/review-20260909/README.md`,
with all warmups, alternating runs, retained images, and DLL hashes. The current
timings are in `committed-cmyk-results.csv`, from the build containing `42a1571`.
The narrowly scoped color comparison is described in `engine/docs/color-review.md`.
This supersedes
the earlier batch-memory conclusion for this build. Unmeasured interactive behavior
and known speed and fidelity gaps remain release gates.
