# Raster allocation checkpoint

KillerPDF 1.9.0 now reuses its raster-sort comparison delegate and feeds glyph
edges directly from cached outlines, without allocating transformed point arrays.
The preceding image change also avoids redundant rectangular clip sampling.
These changes reduce allocation churn but do not establish memory or speed parity.

All 3,220 engine tests and 341 app tests pass, including four new rotated,
reflected, skewed, and stretched glyph cases. The app builds with zero warnings
or errors. All 74 difficult-sample pages at 2048 pixels remain pixel-identical
to the CMYK compositing checkpoint.

## Allocation evidence

Verbose CLR allocation traces of the same 40-file, 74-page app batch recorded:

| Implementation | Sampled allocated bytes | Sort delegate bytes | Glyph point-copy bytes | Gen2 collections |
| --- | ---: | ---: | ---: | ---: |
| Rectangular image clip change only | 1,821,906,464 | 71,909,208 | 109,752,192 | 46 |
| Reused sort delegate | 1,749,459,720 | 0 | 115,481,224 | 46 |
| Reused delegate and direct glyph edges | 1,631,022,504 | 0 | 0 | 42 |

These are sampled allocation totals, not retained heap or process working set.
The zero columns mean no allocation samples for those removed paths. Tracing
changes runtime behavior; traced timings are not used for performance claims.

## Untraced app comparison

Two runs per build, with the order reversed on the second pass, compare the
saved CMYK checkpoint, these allocation changes, and retained PDFium 1.8.5.
Every build rendered all 74 pages successfully in every run.

| Build | Median render seconds | Median wall seconds | Median peak MiB |
| --- | ---: | ---: | ---: |
| CMYK checkpoint | 14.123 | 19.390 | 287.2 |
| Raster allocation changes | 13.494 | 18.644 | 297.6 |
| PDFium 1.8.5 | 4.681 | 9.877 | 269.2 |

The roughly 4% rendering difference is near the previously observed machine
noise. Peak memory increased despite fewer allocations. This is an allocation
checkpoint with an unresolved memory regression, not a parity claim. Remaining
color and compositing differences from the preceding checkpoint also remain.

An in-place row-grouping experiment removed the second cell buffer but did not
resolve peak memory and produced inconsistent timings. It was removed.

## Reproduction

Inputs are `input-Broad` from the preceding validation. The app command is
`KillerPDF.exe --batch-render INPUT OUTPUT --size 2048 --pages 3 --log CSV --quiet`.
Raw measurements are in [measurements.csv](measurements.csv). Local traces,
per-page logs, images, and scripts remain under
`C:/Users/steve/killerpdf-benchmark/cmyk-compositing-20260907/`:

- `image-clip-allocation.nettrace`, `cell-sort-allocation.nettrace`, and
  `glyph-stream-allocation.nettrace` contain the allocation profiles.
- `glyph-stream-broad` contains the verified pixels.
- `raster-results.csv` and `raster-{1,2}-{Before,After,PDFium}` contain the
  untraced comparison. `compare-raster.ps1` runs the comparison.
- `inplace-results.csv` records the discarded row-grouping experiment.

The tested allocation-change app DLL SHA-256 was
`EB7774602F33C1104F9F7698504B92165DAB22D8184558FB5ED705F25F0290E3`.
The retained engine baseline is the `native-app` snapshot documented in the
[CMYK checkpoint](../1.9.0-cmyk-compositing/README.md).
