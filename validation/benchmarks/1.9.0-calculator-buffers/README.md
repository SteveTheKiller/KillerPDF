# Calculator buffer checkpoint

KillerPDF 1.9.0 calculator color functions now use small stack buffers instead
of allocating input and output arrays for every sample. Separate component
functions write into the same output span. DeviceN conversions that require an
array retain that interface. Calculator arithmetic, range checks, and gradient
sampling are unchanged.

All 3,223 engine tests and 341 app tests pass. The app builds with zero warnings
or errors. All 74 difficult-sample pages at 2048 pixels remain pixel-identical
to the preceding build.

## Targeted allocation verification

Two tests render a 256 by 256 radial gradient again into a caller-owned buffer.
Both fail on the old implementation and pass after the change, with identical
pixels and expected gradient colors:

| Function form | Old allocated bytes | Verified new limit |
| --- | ---: | ---: |
| One RGB calculator | 5,247,856 | Below 262,144 |
| Three component calculators | 20,456,272 | Below 262,144 |

The preceding JPEG 2000 change also caches separated color and alpha planes.
Its repeated-paint test fails before at 2,251,080 allocated bytes and passes
below 524,288 afterward, preserving expected transparency and pixels.

## App comparison

The same 40-file, 74-page difficult sample was run twice per build, reversing
the order on the second pass. Every page rendered successfully in every run.

| Build | Median render seconds | Median wall seconds | Median peak MiB |
| --- | ---: | ---: | ---: |
| Before calculator change | 13.346 | 18.501 | 298.0 |
| Calculator buffers | 13.718 | 19.298 | 285.3 |
| PDFium 1.8.5 | 4.613 | 9.745 | 268.9 |

Peak working set fell in both comparisons, by about 12.7 MiB at the median.
Rendering times for the changed build were 14.163 and 13.273 seconds. These
runs do not establish a speed improvement. They include first-run effects and
are not a warmup-controlled timing study. The engine remains substantially
slower than PDFium on this deliberately difficult sample, and its peak remains
about 16.4 MiB higher. Earlier rendering differences also remain unresolved.

## Evidence

[measurements.csv](measurements.csv) records the exact untraced observations.
Local logs, images, and scripts are under
`C:/Users/steve/killerpdf-benchmark/cmyk-compositing-20260907/`:

- `calculator-fixed-results.csv` and `calculator-fixed-{1,2}-{Before,After,PDFium}`
  contain the final fixed-buffer comparison.
- `calculator-buffers-results.csv` records the earlier variable-size stack
  buffer version; it is not the final code's timing result.
- `jpx-app` is the saved baseline after the JPEG 2000 change.
- `compare-raster.ps1 -Prefix calculator-fixed -Baseline jpx-app` runs the
  comparison, using a fresh output prefix to avoid overwriting prior results.

Final tested app DLL SHA-256:
`287842A9FB2923E0466EF54EC50A0EBCE09D0A4B7A43DE7E1C2DA35DBA425334`.
Baseline app DLL SHA-256:
`8A32D5BCB9BA9710FD517FDFCC24BB8EED72742A11C1FC6210BB9097DD17B007`.
