# PDFium memory comparison

September 7, 2026. The memory target passes on both measured batch workloads.
This is not release approval or a claim of universal speed, memory, or visual parity.

## Three rotating comparisons

| Median of three runs | PDFium 1.8.5 | Engine, default GC | Engine, conserve memory |
| --- | ---: | ---: | ---: |
| Shared 600 pages, peak working set | 622.4 MiB | 647.3 MiB | 477.3 MiB |
| Shared, sampled peak private bytes | 577.3 MiB | 654.1 MiB | 450.3 MiB |
| Shared, render time | 12.155 s | 12.133 s | 12.439 s |
| Shared, process wall time | 22.795 s | 23.605 s | 24.000 s |
| Difficult 74 pages, peak working set | 269.1 MiB | 397.9 MiB | 259.2 MiB |
| Difficult, sampled peak private bytes | 221.8 MiB | 350.2 MiB | 194.2 MiB |
| Difficult, render time | 4.721 s | 13.066 s | 13.029 s |
| Difficult, process wall time | 9.915 s | 18.067 s | 18.156 s |

Conserved engine working-set peaks were 475.6, 479.1, and 477.3 MiB on the shared
set, versus 622.6, 622.2, and 622.4 MiB for PDFium. On the difficult set they were
261.1, 258.9, and 259.2 MiB, versus 269.3, 269.1, and 268.7 MiB. Every measured
engine working-set and private-byte peak was below its PDFium comparison.
Shared median working set is 23.3% lower; the difficult result is memory parity
within the approximately 5% noise used for these development comparisons.

The shared engine render median rose 2.5% and wall median rose 1.7% against the
engine control. Difficult render time fell 0.3% and wall time rose 0.5%. These
differences are within that noise allowance, not a speed win. A small runtime cost
is not ruled out. The 5.3% shared wall gap and the much slower difficult rendering
against PDFium remain explicit release concerns. Interactive pauses are unmeasured.

All runs completed their expected 600 or 74 pages with no failures. Every output
from all six conserved-engine passes matches the previous corrected engine's
decoded pixels: 2,022 page comparisons with zero changes. Matching the previous
engine does not resolve its remaining color differences against PDFium.

## What changed

The preceding [ownership changes](../1.9.0-memory-ownership/README.md) removed
copies, bounded surfaces, and reused buffers. JPEG now combines one block row
at a time instead of retaining full component sample planes. Progressive JPEG
keeps its required coefficients but uses row-sized output scratch too. Forty new
geometry cases cover grayscale, RGB, CMYK, subsampling, odd sizes, progressive
output, and every supported reduction. An allocation test checks that baseline
scratch does not grow with image height.

Those changes alone did not close the process-peak gap. A heap snapshot had shown
mostly dead large arrays waiting for collection. The application now sets
`System.GC.ConserveMemory` to `9` in `runtimeconfig.template.json`. This supported
runtime policy favors a smaller heap and compacts a fragmented large-object heap.
It is not a fixed memory cap or a call to force collection after every file.
See [Microsoft's GC configuration documentation](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#conserve-memory).

The two engine variants in the final comparison have identical application and
engine DLL hashes. The runtime configuration is the controlled difference.
The large peak reduction therefore comes from the runtime policy, rather than
being attributed to another small decoder optimization.

## Verification and reproduction

- Full engine suite: 3,188 pass. Full app suite: 341 pass. Both passed with the
  default policy and with memory conservation enabled for their test processes.
- Release build succeeded with zero warnings or errors. The SDK-generated
  `KillerPDF.runtimeconfig.json` contains the setting.
- The local loose-payload publish also contains the setting in
  `KillerPDF.App.runtimeconfig.json`, which is already in the payload manifest.
  Its 74-page smoke run completed without failures and preserved every compared pixel.
- Inputs, sizes, and page limits match the earlier validation: 600 shared first
  pages at 1024 pixels and the 40-file, 74-page difficult set at 2048 pixels.
  All builds received the same input directories, which were not modified.
- Each invocation was a fresh process. Run order rotated PDFium, conserved engine,
  and default engine so each ran first, second, and third once. Earlier diagnostic
  passes warmed filesystem caches; the recorded comparison has no extra warmup rows.
- Commands used `--batch-render <input> <output> --size <1024-or-2048>
  --pages <1-or-3> --log <pages.csv> --quiet`. Working-set high water and private
  bytes were sampled every 50 ms. Short private-byte peaks can be missed.
- No builds, tests, tracing, or other benchmark runs overlapped the comparison.
  Ordinary desktop background activity was uncontrolled. Runtime: .NET 10.0.11.
- No environment override was used for the final comparison. Initial environment
  probes and the preceding default-policy JPEG checks are recorded separately.

Raw data: [final comparisons](comparisons.csv), [diagnostic probes](probes.csv).
The [input manifest](inputs.csv) records both sets and SHA-256 hashes. In the
comparison CSV, `Engine` is the conserved build, `EngineBefore` is the same DLLs
with the default runtime policy, and `PDFium` is the retained 1.8.5 application.

| Measured artifact | SHA-256 |
| --- | --- |
| Both engine application DLLs | `9054BA30F82D61608D8402EBFE7E50ECBAB34FECD403FE9CAD5873ADFCE7EB44` |
| Both engine library DLLs | `B78F906B5424990DD32CD3BECF650C53DAE735BA2653054B337CA569ED985884` |
| Conserved application runtime configuration | `B352C8E64483463733B352EA430C3B0B692BF5C402E06300F6BB38673B37803D` |
| PDFium application DLL | `BDA8460C2AC194D705C1D3D06D36B29190FBC27F65B299E076741F9173E9E551` |

The measured product source is committed locally as `47843d7` (JPEG row storage)
and `b666ba5` (application runtime policy), following `3a0cf73` (app pixel ownership).

The [release gates](../../RELEASE-GATES-1.9.md) remain open for difficult-page speed,
color fidelity, interactive behavior, and complete maintenance and feature coverage.
