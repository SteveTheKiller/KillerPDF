# Memory ownership checkpoint

Historical development checkpoint, September 7, 2026. Memory parity was not
achieved here. The later [runtime-policy comparison](../1.9.0-memory-parity/README.md)
meets the memory target on both measured batch workloads.

The engine now stores fractional rectangular clips compactly, allocates transparency
and knockout surfaces within their bounds, bounds soft-mask samples, and returns
temporary clip buffers when their graphics state ends. Scratch pools have total
retention budgets. Parsed streams borrow immutable document storage, and JPEG,
Flate, and cross-reference recovery avoid duplicate encoded payloads. Public
caller-owned input still gets copied. A new caller-owned rendering API lets batch
encoding reuse pixels and write PNGs directly to files.

## Initial direct comparison

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
settings were added. Large-file source allocation remains a follow-up ownership
target. The app render-result follow-up below removes another copy. Interactive memory,
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

## Normal application rendering follow-up

Normal page rendering and exact-page comparison now render directly into the
application's owned output array. The viewer already caches finished images, so
keeping a second engine bitmap was unnecessary. Callers still receive independent
mutable pixels. Disposing a session clears its renderer and page references as
well as returning the encoding buffer.

A reflection harness loaded the actual before and after application DLLs and
called `PdfPageRenderSession.OpenEngineFirst(path, 2048, 2048)` and `RenderPage`
on the same 40-file, 74-page sample. All output hashes matched. Summed allocations
on the calling thread fell from 3,376,488,224 to 2,492,556,440 bytes (26.2%). This
is allocation volume during render calls, not peak memory, interactive latency,
or a PDFium comparison. The batch path already used caller-owned storage, so this
does not establish any further improvement to the batch peaks above.

The harness also checked independent pixels after caller mutation, cancellation,
exact-page rendering, repeated disposal, cleared document references, and rejected
rendering after disposal. The Release build passed without warnings or errors;
all 341 app tests passed. Engine source is unchanged from the 3,147-test checkpoint.
See [per-page allocation and hash evidence](app-session.csv).

Before app SHA-256: `16105CA76842DF81D227A6AF51A4A28007E411AC6EECD5912B041AF22D7A2E31`.
After app SHA-256: `9492591AB34B320839F0D079D529A6A849AC07F55A75D4AFDC9DA4B89E5BBB7B`.
