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
| Memory at parity or close to the PDFium pipeline | Shared batch lower; difficult batch slightly higher; interactive use unverified | September 9 median peak working set is 534.4 versus 620.0 MiB shared and 275.0 versus 266.2 MiB difficult. Difficult engine peaks range from 264.9 to 277.9 MiB. Verify representative interactive document use and an explicit acceptable tolerance before release. |
| Rendering and whole-pass speed without regression | Open | Three alternating measured runs put shared render medians at 12.309 versus 12.705 seconds, with engine variability overlapping PDFium; shared wall time is 2.3% longer. Difficult render time is 9.298 versus 4.891 seconds and wall time is 42.7% longer. The focused JPEG 2000 improvement does not establish general speed parity. |
| Rendering fidelity without regression | Open | Scaled geometry and crop fixes reduce dimension mismatches from 215 to 2 of 600 shared outputs and 43 to 0 of 74 difficult outputs. Two one-pixel height differences remain. Color, compositing, font, and fine-detail differences still need disposition; unprofiled CMYK is an approximation. |
| Startup, first-page display, scrolling, and zoom without regression | Unverified | Controlled interactive measurements; the startup marker does not measure first-page completion or scrolling. |
| Existing 1.8 functionality and maintenance fixes preserved | Unverified for release | Verify the maintenance forward-port record and applicable feature workflows on the release build. |
| Builds and regression suites | Passing development checkpoint | September 9: 3,709 engine tests, 352 app tests, and the Release application build pass. The packed engine works in an isolated JPEG 2000 consumer. Repeat required checks for the final release build; these checks alone do not close other gates. |

Current evidence is archived locally at `C:/Users/steve/kp-bench-render/review-20260909/README.md`,
with all warmups, alternating runs, retained images, and DLL hashes. It supersedes
the earlier batch-memory conclusion for this build. Unmeasured interactive behavior
and known speed and fidelity gaps remain release gates.
