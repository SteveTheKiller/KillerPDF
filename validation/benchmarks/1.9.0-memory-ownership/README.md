# Memory ownership checkpoint

Development checkpoint, September 7, 2026. Memory parity is not achieved.

The engine now stores fractional rectangular clips compactly, allocates transparency
and knockout surfaces within their bounds, bounds soft-mask samples, and returns
temporary clip buffers when their graphics state ends. Scratch pools have total
retention budgets. Parsed streams borrow immutable document storage, and JPEG,
Flate, and cross-reference recovery avoid duplicate encoded payloads. Public
caller-owned input still gets copied. A new caller-owned rendering API lets batch
encoding reuse pixels and write PNGs directly to files.

## Latest direct comparison

Both builds received identical inputs. These are single development passes, with
PDFium first, not repeated median results or a release acceptance run.

| Workload | Build | Peak working set | Sampled peak private bytes | Render | Wall |
| --- | --- | ---: | ---: | ---: | ---: |
| 600 first pages, 1024 px | PDFium 1.8.5 | 622.3 MiB | 573.9 MiB | 12.697 s | 23.581 s |
| Same | Engine | 644.2 MiB | 650.9 MiB | 12.113 s | 23.731 s |
| 40 files, 74 pages, 2048 px | PDFium 1.8.5 | 268.7 MiB | 212.6 MiB | 4.804 s | 10.048 s |
| Same | Engine | 400.1 MiB | 353.2 MiB | 13.276 s | 18.697 s |

Every requested page completed without failures. Decoded RGBA pixels match the
previous corrected engine build on all 674 pages. This verifies preservation of
that output, not visual equivalence with PDFium. The full engine suite passes
3,147 tests; the app suite passes 341. Release build: zero warnings and errors.

The earlier engine checkpoint measured medians of 770.4 MiB on the shared set and
590.3 MiB on the difficult set. The current single pass is materially lower, but
the difficult set still uses about 49% more working set than PDFium. Its rendering
also remains much slower. Neither gate passes. Shared private-byte usage also
remains higher. Do not promote the latest single result into a median claim.

## Diagnosis and limitations

A separate live heap snapshot of the intermediate ownership build contained
266,414,576 managed bytes, of which 24,405,586 were reachable. Most large float
and byte buffers were already dead and awaiting collection. This is evidence of
temporary allocation pressure, not proof that every process peak has that cause.
A separate intermediate budget-build trace estimated 3,461,834,424 allocated
bytes and 33 generation-2 collections, versus 5,461,253,856 and 44 in the previous
corrected-build trace. Tracing and heap capture were excluded from timing runs.

Intermediate trials fluctuated as allocation patterns changed collection timing.
The raw iteration record is retained; individual small changes are not each
claimed as a measured win. No forced garbage collection or runtime compilation
settings were added. The remaining large-file source allocation and normal app
render-result copying are follow-up ownership targets. Interactive memory,
first-page display, scrolling, and zoom remain unverified.

## Reproduction and identity

Use `--batch-render <input> <output> --size <1024-or-2048> --pages <1-or-3>
--log <pages.csv> --quiet`. Inputs are the shared and broad manifests from the
[render validation checkpoint](../1.9.0-render-validation/README.md). Each process
was fresh; working-set high water and private bytes were sampled every 50 ms.
Private-byte sampling can miss shorter peaks. PNG encoding is outside page render
timing and inside wall timing. No build or test ran during these comparisons.

Measured binaries were built from the memory-ownership working tree based on
`fe197acf5642545c3353abbb685ea168a299df5a`. Later changes only added a PNG stream
test, corrected a source comment, and recorded documentation.

| DLL | SHA-256 |
| --- | --- |
| Engine application | `16105CA76842DF81D227A6AF51A4A28007E411AC6EECD5912B041AF22D7A2E31` |
| Engine library | `B0BE146ED6BE61B9E4F83604029817FCCD8981FCF7CE5E6A4BBA3EA836CB2F4F` |
| PDFium application, reports 1.8.5 | `BDA8460C2AC194D705C1D3D06D36B29190FBC27F65B299E076741F9173E9E551` |

See [all iteration measurements](iterations.csv) and the
[open release gates](../../RELEASE-GATES-1.9.md). These local results do not
authorize publication or release.
