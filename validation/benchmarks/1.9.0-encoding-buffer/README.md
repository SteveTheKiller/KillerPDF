# Encoding buffer checkpoint

KillerPDF 1.9.0 now retains one spare encoding output buffer, up to 16 MiB,
instead of one buffer in every power-of-two size bucket. New allocations use
the requested length, and smaller pages reuse a larger available buffer.
Concurrent sessions keep separate active buffers. Owned viewer output is unchanged.

The same 40-file, 74-page difficult sample was rendered at 2048 pixels.
Each build ran three times in alternating order. Run 1 is warmup; the table
uses the medians of measured runs 2 and 3. Every run completed all 74 pages.

| Build | Render seconds | Wall seconds | Peak working set MiB |
| --- | ---: | ---: | ---: |
| Before encoding change | 13.281 | 18.509 | 288.5 |
| Single encoding spare | 13.259 | 18.471 | 281.3 |
| PDFium 1.8.5 | 4.695 | 9.865 | 268.8 |

The change reduces measured peak by 7.2 MiB. Timing is unchanged within
machine noise. Memory remains 12.5 MiB above PDFium on this sample, and
difficult-page speed and known color differences remain unresolved.
This is engine-pipeline development, not release preparation.

All 74 decoded page images match the preceding engine build exactly. The app
build has zero warnings or errors, and all 341 app tests pass. Engine source is
unchanged from the preceding 3,223-test checkpoint. A separate harness compiled the production
pool source and passed exact-allocation, cross-size reuse, growth, retention,
invalid-length, active-ownership, and 1,000 concurrent-rental checks.

## Evidence

[measurements.csv](measurements.csv) preserves all observations, including warmup.
The local comparison command is `compare-raster.ps1 -Prefix encoding-spare
-Baseline calculator-app -Runs 3`, under
`C:/Users/steve/killerpdf-benchmark/cmyk-compositing-20260907/`.
Its `encoding-spare-*` directories contain logs and images. Buffer checks are
under `C:/Users/steve/killerpdf-benchmark/encoding-buffer-checks/` and run with
`dotnet run --project Checks.csproj -c Release`.

Before app DLL SHA-256:
`287842A9FB2923E0466EF54EC50A0EBCE09D0A4B7A43DE7E1C2DA35DBA425334`.
After app DLL SHA-256:
`55BC22178B4619F70AD55AAC260576D0ADE0309411990EAEDFD9E069409DFE5C`.

## Uniform graphics soft-mask follow-up

A subsequent engine change stores one exact value for uniform graphics soft
masks instead of allocating a full pixel plane. Masks with differing samples
retain all their original bytes. Bounds, outside values and transfer functions
are preserved. Two 1024 by 1024 tests failed before at 1,055,104 and 1,054,416
allocated bytes and pass below 65,536 bytes after the change.

All 3,229 engine tests and 341 app tests pass, including partial-mask and
inverted-transfer cases. Allocation tests run without other test collections
evicting their warmed shared scratch buffers. The app build is clean, and
all 74 Broad pages match `encoding-spare-3-After` pixel-for-pixel. The new
outputs are in `uniform-mask-final` under the same scratch root.
This check does not establish a further whole-app speed or peak-memory gain.

Follow-up app DLL SHA-256:
`09C4E84E5BB8BD7FD468A8FFD900F7E9BF004437E2BC477EF92062A9A6B528BF`.

## Packed explicit image-mask follow-up

Explicit one-bit image masks now retain packed rows in the image cache instead
of expanding every bit to a byte. Default and reversed decoding, row padding,
and cached repeated rendering are covered by tests. Standard one-bit sample
decoding uses exact integer results for zero and full opacity.

A 2049 by 1024 mask retains 263,168 sample bytes instead of 2,098,176.
Its first-render allocation test failed before at 2,448,544 bytes and passes
below 1,048,576 after the change. This is a targeted storage and allocation
result, not a fresh whole-app peak-memory or speed comparison.

All 3,232 engine tests and 341 app tests pass, the app builds cleanly, and all
74 Broad outputs match `uniform-mask-final` exactly. New images and logs are
in `packed-mask-final` under the same scratch root.

Packed-mask app DLL SHA-256:
`B7BF02008CAC15CC47B63B8C550EBE0BBF02DA9AECC06F57F2A4151F104FE208`.

## Stencil painting and combined app check

Stencil painting now samples its original packed bits without constructing a
temporary color image and CMYK alpha plane. The sample grid and existing
opacity rounding are unchanged. A cold 1024-pixel CMYK stencil test observed
7,351,128 allocated bytes before and 2,107,888 after removing those planes.
The final tests allow below 3 MiB for parsing and required page storage.
RGB and CMYK checkerboards, clipping, half opacity, reversed decoding and
quarter-turn rotation pass. All 3,238 engine tests and 341 app tests pass,
and the app builds with zero warnings or errors.

A combined comparison against the original calculator-buffer build includes
the encoding spare and all three mask changes. One warmup and two measured
alternating runs per build completed all 74 Broad pages. The final decoded
pixels match the preceding packed-mask build on every page.

| Build | Median render seconds | Median wall seconds | Median peak MiB |
| --- | ---: | ---: | ---: |
| Calculator-buffer baseline | 13.168 | 18.333 | 284.4 |
| Encoding and mask changes | 13.447 | 18.595 | 282.2 |
| PDFium 1.8.5 | 4.609 | 9.709 | 269.5 |

The whole-app memory gap remains about 12.7 MiB. The small baseline-to-current
peak difference is not a substantial gain, and timing differences remain
within the previously observed noise. Targeted allocation savings do not
establish whole-app parity. This comparison does not isolate the stencil change.

[combined-measurements.csv](combined-measurements.csv) preserves all runs.
Local logs and images use the `mask-storage-*` prefix under the same scratch
root. The command was `compare-raster.ps1 -Prefix mask-storage -Baseline
calculator-app -Runs 3`.

Combined app DLL SHA-256:
`D852B5DA0CD055C25102EE8B2AE4BA06AAC6A6041511E50D996C0DD2C72E552B`.

## Uniform CMYK alpha storage

CMYK surfaces now retain one alpha value until pixels have different opacity,
then allocate the alpha plane. Opaque 1024-pixel page tests allocate below
128 KiB instead of about 1 MiB. Transparent and opaque group backdrops,
group opacity, and knockout copying are covered. All 3,244 engine tests and
341 app tests pass, and the app build has zero warnings or errors.

All 74 Broad outputs match the preceding stencil build exactly. Three
alternating runs completed every page; run 1 is warmup. Measured medians:

| Build | Render seconds | Wall seconds | Peak working set MiB |
| --- | ---: | ---: | ---: |
| Before uniform alpha | 13.539 | 18.788 | 281.6 |
| Uniform alpha | 13.390 | 18.554 | 281.3 |
| PDFium 1.8.5 | 4.698 | 9.879 | 268.8 |

These small differences do not establish a whole-app gain. The memory gap
remains about 12.6 MiB, and difficult-page speed remains unresolved.
[uniform-alpha-measurements.csv](uniform-alpha-measurements.csv) preserves
all observations. The local command was
`compare-raster.ps1 -Prefix uniform-alpha -Baseline stencil-app -Runs 3`.

Before app DLL SHA-256:
`D852B5DA0CD055C25102EE8B2AE4BA06AAC6A6041511E50D996C0DD2C72E552B`.
After app DLL SHA-256:
`827FB277AB73FB100BDB95D14B304797F7AB86CE2770FFF75086F01D4ED06152`.
