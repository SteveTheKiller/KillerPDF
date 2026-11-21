# KillerPDF 1.9.4 corpus report

Test date: 2026-11-21

The KillerPDF 1.9.4 tag build saved 41,505 of the 46,944 inputs in the three normal collections, with every input giving exactly the same outcome as 1.9.3 in the same session. A separate gate tested all 80 damaged inputs and recorded 0 crashes and 0 timeouts, identical to 1.9.3 file for file. Opening, saving and rendering times are within 1.9.3's measured range, and rendered pixels are identical to 1.9.3 on every image. Memory is level, apart from one render set whose median peak sat 5.2 MB above 1.9.3's highest pass.

## Results

Counts are per pass, not the sum of repeated passes. Every pass returned the same outcome for every input, and the same outcome as 1.9.3.

| Collection | Inputs | Saved | Skipped | Save failures | 1.9.4 median s | 1.9.4 range | 1.9.3 median s | 1.9.3 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,478 | 1,069 | 149 | 249.159 | 232.605 to 252.456 | 229.801 | 228.449 to 267.630 |
| Standards and color | 649 | 399 | 230 | 20 | 7.803 | 6.729 to 7.944 | 8.308 | 7.132 to 10.979 |
| Private stress | 29,599 | 25,628 | 2,712 | 1,259 | 498.321 | One pass | 533.632 | One pass |
| Damaged-file safety | 80 | 0 | 78 | 2 | Separate timeout gate | | | |

The regression median is inside 1.9.3's range but 8.4% above 1.9.3's median. These passes ran while other work was using the machine, and the same session put 1.9.5 well outside 1.9.3's range until a quiet recheck brought it back inside (see the [1.9.5 report](../1.9.5/CORPUS.md)). The only engine changes between the 1.9.3 and 1.9.4 tags are in Rasterize Region, which batch resave does not run.

Peak working set on regression was 703.0 to 703.7 MB against 1.9.3's 703.1 to 703.5 MB, level. Standards was lower at 704.3 to 706.3 MB against 706.7 to 708.1 MB, and stress was lower at 1,168.5 MB against 1,180.0 MB.

## Rendering

Summed per-page render time for the same three page sets as the [1.9.3 report](../1.9.3/CORPUS.md), three measured passes per build in rotating order:

| Set | Pages | Rendered | 1.9.4 median s | 1.9.4 range | 1.9.3 median s | 1.9.3 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Shared | 600 | 570 | 32.701 | 32.084 to 42.006 | 31.863 | 31.356 to 40.734 |
| Broad | 74 | 72 | 9.536 | 8.376 to 10.984 | 9.493 | 8.329 to 10.907 |
| Color | 175 | 175 | 16.068 | 15.871 to 17.517 | 15.550 | 15.446 to 17.145 |

The same pages render in both versions, and rendered pixels are identical to 1.9.3 on all 817 images. Render peaks were inside 1.9.3's range on Shared and Broad. Color's median peak was 530.1 MB against 1.9.3's 487.0 to 524.9 MB, 5.2 MB (1%) above its highest pass. The rendering code is unchanged between the two tags and the output is identical, so this reads as pass-to-pass variation; in this session, 1.9.3's own Shared peaks swung from 428.8 to 537.3 MB.

## Changes from the previous published record

Against 1.9.3, measured in the same session, no input changed outcome in any collection. [outcome-transitions.csv](outcome-transitions.csv) is empty for that reason.

## Build and input identity

- Application: tag `v1.9.4`, commit `34a8ba6dfd41a16cb070ed487a8326e07bc0fe01`, file version `1.9.4.0`, built from `git archive` as a Release, framework-dependent, ReadyToRun publish with the codec and engine published the same way, the same flags as the 1.9.3 report. This is a tag build, not the signed published binary.
- Tested executable SHA-256: `AE1CD1E95AECEF2BB012416BAF1BF6CA4B008D049BA291E2343D3E568C91ACAC`.
- Baseline: tag `v1.9.3`, commit `5b52f33ab62fe7c5f165e705f6dd1d5831474b8e`, executable SHA-256 `A8F5CC9CB045EFADDFDB3E613E2B2D86FE570A163FC53C44074BECB9FF13EBB3`, the same executable hash as the 1.9.3 report.
- Inputs: the same 47,024 files by collection count as the 1.9.3 report. The render sets were copied from the corpus and matched by SHA-256, with 0 mismatches.

## Method and limits

The run used SHREDDER with its AMD Ryzen 7 5700X (8 cores, 16 threads), 31.9 GiB RAM, Windows 11 Pro build 26300 and .NET 10.0.15. The host was not certified idle, and caches were not flushed.

Before any measurement, the rules for "worse" were written down: any input or page 1.9.3 handled that 1.9.4 does not, any damaged-file crash or timeout, or a 1.9.4 median outside 1.9.3's range on the slow or heavy side, for time or for peak memory. 1.9.3, 1.9.4 and 1.9.5 were measured together with no warmup. Regression, standards and the render sets received three measured passes per build, rotating the build order each pass; stress received one pass per build; each damaged input ran alone with a 30-second limit, once per build. The commands were `KillerPDF.App.exe --batch-resave` and `--batch-render` as in the 1.9.3 report.

This measures opening, resaving and rendering time and outcomes. It does not establish rendering fidelity, editing behavior or PDF standards compliance.

- [Summary by collection](benchmark-summary.csv)
- [Measured runs](benchmark-runs.csv)
- [Render summary](render-summary.csv) and [render runs](render-runs.csv)
- [Damaged-file status summary](fuzz-summary.csv)
- [Outcome transitions from 1.9.3](outcome-transitions.csv)
