# KillerPDF 1.9 release gates

Release remains blocked while a known regression from the 1.8 pipeline exists.
An improvement over an earlier 1.9 build does not establish parity with 1.8.
Missing measurements are unverified, not passes. Test counts and successful
batch completion do not establish visual or interactive equivalence.

The retained PDFium application reports version 1.8.5 despite its local
`KillerPDF-1.8.4` directory name. Use identified application DLLs and identical
inputs when comparing pipelines. Record all alternating runs, warmups, peaks,
outliers, and measurement variability. Do not hide a slower or larger workload
behind an overall average.

| Requirement | Current status | Evidence still needed |
| --- | --- | --- |
| Memory at parity or close to the PDFium pipeline | Batch comparison passes; interactive use unverified | Three rotating comparisons put all measured engine peaks below PDFium on both sets. Median working set is 477.3 versus 622.4 MiB shared and 259.2 versus 269.1 MiB difficult. Verify representative interactive document use before release. |
| Rendering and whole-pass speed without regression | Open | Shared render timing is within the noise allowance, but shared wall time is 5.3% longer than PDFium and difficult-page rendering remains much slower. Memory conservation also measured a small shared timing increase against the engine control, within noise. |
| Rendering fidelity without regression | Open | Remaining color and compositing differences need disposition against reference output. Unprofiled CMYK is still an approximation. |
| Startup, first-page display, scrolling, and zoom without regression | Unverified | Controlled interactive measurements; the startup marker does not measure first-page completion or scrolling. |
| Existing 1.8 functionality and maintenance fixes preserved | Unverified for release | Verify the maintenance forward-port record and applicable feature workflows on the release build. |
| Builds and regression suites | Passing development checkpoint | Repeat required checks for the final release build. Passing suites alone do not close the other gates. |

The [memory comparison](benchmarks/1.9.0-memory-parity/README.md) closes the measured
batch memory target, not release readiness. Unmeasured interactive behavior and
the known speed and fidelity gaps remain release gates.
