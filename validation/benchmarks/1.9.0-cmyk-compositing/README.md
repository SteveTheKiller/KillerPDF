# CMYK and transparency-group compositing checkpoint

This development checkpoint corrects ink-component blending and preserves an outer
blend mode when a non-isolated group's contents select Normal blending. It does
not establish speed or memory parity with PDFium. Engine work remains active.

## Correctness

- Original CMYK components survive painting, image conversion, and group compositing.
  Black-only and process-black sources remain distinct under blending.
- Non-isolated, non-knockout groups retain their initial backdrop for internal
  blends. Their own accumulated alpha allows that initial backdrop contribution
  to be removed before applying the outer blend, opacity, and mask once.
- CMYK surfaces and converted images use four ink bytes plus one alpha byte per
  pixel. RGB display copies are no longer maintained alongside those ink planes.
- Full engine suite: 3,216 pass. App suite: 341 pass. Release-configuration app
  build: zero warnings and errors. Fifteen added cases cover ink identity, mixed
  group spaces, masked CMYK images, translated knockout groups, outer blending,
  backdrop preservation, and partial opacity.
- All 74 difficult-sample pages render at 2048 pixels; all 600 shared first pages
  render at 1024 pixels. No batch failures or missing outputs.
- Against the preceding group-opacity build, 65 of 74 difficult pages and 577 of
  600 shared pages are pixel-identical. Changed shared pages comprise 20 color
  fixtures and three preservation documents. Comparison against the older memory
  checkpoint also includes four pages changed by the earlier opacity fixes.
- All 74 difficult pages remain pixel-identical across the image-plane and native
  surface-storage optimizations. The storage reduction retains the compositing fix.

Ghent combined test page 2 loses the large error crosses in its two top blend
panels. Faint outlines remain in several isolated-group patches. Altona's CMYK
shadow patch and the Arakawa article's mouse illustrations were also inspected;
the latter regain fills missing in the preceding build. These targeted checks
are not a complete visual signoff on the corpus. ICC-dependent colors still
differ because ICC profiles are not applied.

## Performance remains open

The [measurements](measurements.csv) include two rotating comparisons of an
intermediate implementation and one final check of each input set. They use fresh
app processes, normal PNG output, and `System.GC.ConserveMemory=9`. Builds and tests
were finished before these measurements. Working-set peaks were polled every 50 ms.

| Build and sample | Render | Wall | Peak working set |
| --- | ---: | ---: | ---: |
| Preceding engine, difficult sample, median of two | 13.088 s | 18.356 s | 284.9 MiB |
| PDFium 1.8.5, difficult sample, median of two | 4.772 s | 10.511 s | 269.6 MiB |
| Final engine, difficult sample, one check | 14.247 s | 20.135 s | 287.2 MiB |
| Final engine, 600 shared pages, one check | 12.692 s | 29.261 s | 552.1 MiB |

The difficult sample is still substantially slower than PDFium and slower than
the preceding engine. Its final memory peak is also above PDFium. Historical
lower peaks in the memory-parity checkpoint do not establish current parity.
The final checks are single observations, not new timing-parity claims. Most of
the added rendering time is concentrated in the Ghent pages and ColorBurn patch.
Further compositor performance work and comparable memory checks are required.

The input selections are the same `input-Broad` and `input-Shared` sets used by
the earlier validation and memory checkpoints. Broad uses up to three pages per
file at 2048 pixels; Shared uses one page per file at 1024 pixels. Commands follow
`KillerPDF.exe --batch-render INPUT OUTPUT --size SIZE --pages COUNT --log CSV --quiet`.
Exact page logs, comparison scripts, and rendered outputs are retained under
`C:/Users/steve/killerpdf-benchmark/cmyk-compositing-20260907/`, including
`final-Broad`, `final-Shared`, `before-Shared`, `memory-results.csv`, and
`final-results.csv`.

## Tested build identity

SHA-256:

- Final app DLL: `56BF378AAE5DAE2BA2DD0B099F30647E8A86459FA996C6FE45CE26AD2E32423D`
- Final engine DLL: `1B964C60A0D6CB46A9F2C31C283C2D0B74382C7C5E3B72B080057965458C252C`
- Preceding app DLL (group-opacity checkpoint): `C2BCF981885537CCA073348A8C5C124D17A45E6DFE715D2F73B712CF430D77FC`
- Retained PDFium app DLL: `BDA8460C2AC194D705C1D3D06D36B29190FBC27F65B299E076741F9173E9E551`

CMYK nonseparable blend rules follow Adobe's
[blend-mode addendum](https://printtechnologies.org/standards/files/pdf-reference-1.6-addendum-blend-modes.pdf).
This does not add ICC profile transforms or extend the existing unsupported
non-isolated knockout combinations.
