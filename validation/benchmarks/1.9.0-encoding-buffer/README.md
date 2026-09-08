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
