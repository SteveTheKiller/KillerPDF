# KillerPDF 1.9.5 corpus report

Test date: 2026-11-21

The KillerPDF 1.9.5 tag build saved 41,505 of the 46,944 inputs in the three normal collections, with every input giving exactly the same outcome as 1.9.3 in the same session. A separate gate tested all 80 damaged inputs and recorded 0 crashes and 0 timeouts, identical to 1.9.3 file for file. Opening, saving and rendering times are within 1.9.3's measured range. Render memory is higher on two page sets, and all of it traces to one JPEG 2000 file going through the new OpenJPEG decoder.

## Results

Counts are per pass, not the sum of repeated passes. Every pass returned the same outcome for every input, and the same outcome as 1.9.3.

| Collection | Inputs | Saved | Skipped | Save failures | 1.9.5 median s | 1.9.5 range | 1.9.3 median s | 1.9.3 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,478 | 1,069 | 149 | 213.824 | 203.148 to 218.170 | 217.884 | 201.624 to 260.044 |
| Standards and color | 649 | 399 | 230 | 20 | 7.050 | 6.766 to 7.750 | 8.308 | 7.132 to 10.979 |
| Private stress | 29,599 | 25,628 | 2,712 | 1,259 | 512.430 | One pass | 533.632 | One pass |
| Damaged-file safety | 80 | 0 | 78 | 2 | Separate timeout gate | | | |

The regression row comes from a recheck. In the first session, 1.9.5's regression median was 281.367 s against 1.9.3's range of 228.449 to 267.630 s, which met the rule for investigation. Those passes ran while other work was using the machine. A second session on a quiet machine, alternating 1.9.3 and 1.9.5 for three passes each, put 1.9.5's median 1.9% below 1.9.3's and inside its range. Both sessions are in the runs file; the recheck passes are labeled `recheck`.

Peak working set on regression was 704.2 to 704.6 MB in the recheck against 1.9.3's 703.9 to 704.3 MB, about 0.3 MB (under 0.1%) more. Standards was level at about 705 MB, and stress was lower at 1,137.5 MB against 1,180.0 MB.

## Rendering

Summed per-page render time for the same three page sets as the [1.9.3 report](../1.9.3/CORPUS.md), three measured passes per build in rotating order:

| Set | Pages | Rendered | 1.9.5 median s | 1.9.5 range | 1.9.3 median s | 1.9.3 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Shared | 600 | 570 | 33.435 | 31.591 to 34.901 | 31.863 | 31.356 to 40.734 |
| Broad | 74 | 72 | 8.757 | 7.830 to 9.330 | 9.493 | 8.329 to 10.907 |
| Color | 175 | 175 | 16.016 | 15.768 to 16.629 | 15.550 | 15.446 to 17.145 |

The same pages render in all three versions. Render peak memory is higher on two sets: Shared's median peak was 547.3 MB against 1.9.3's 428.8 to 537.3 MB, and Broad's was 496.6 MB against 324.9 to 356.1 MB. Color was inside 1.9.3's range at 492.8 MB.

Rendering the 40 Broad files one at a time traced the increase to a single file, `balloon_a1b_jp2k.pdf`, whose page holds a 2717 by 3701 JPEG 2000 image. Its peak rose from 160.9 MB in 1.9.4 to 361.2 MB in 1.9.5. No other file rose by more than 3.9 MB, and the median change was 0.3 MB. 1.9.5 replaced the CoreJ2K decoder with OpenJPEG, and the same file is in the Shared set. [broad-render-memory-by-file.csv](broad-render-memory-by-file.csv) has every file.

Rendered pixels are identical to 1.9.4 on 812 of 817 images. The five that changed come from three files: `balloon_a1b_jp2k.pdf` and `UnknownFilter-Linearized.pdf` in both Shared and Broad, and `UnknownFilter-xrefstm.pdf` in Shared. Pixel hashes are recorded, not judged, so no rendering parity claim is made.

## Changes from the previous published record

Against 1.9.3, measured in the same session, no input changed outcome in any collection. [outcome-transitions.csv](outcome-transitions.csv) is empty for that reason.

## Build and input identity

- Application: tag `v1.9.5`, commit `aab0c566c92f86dace2fbf9cc056d3d1b06cddec`, file version `1.9.5.0`, built from `git archive` as a Release, framework-dependent, ReadyToRun publish with the codec and engine published the same way, the same flags as the 1.9.3 report. This is a tag build, not the signed published binary.
- Tested executable SHA-256: `5955876CF892745E58B114A55AF76C8C2093A325C9B7CCC388E3108F11D03D2C`.
- Baseline: tag `v1.9.3`, commit `5b52f33ab62fe7c5f165e705f6dd1d5831474b8e`, executable SHA-256 `A8F5CC9CB045EFADDFDB3E613E2B2D86FE570A163FC53C44074BECB9FF13EBB3`, the same executable hash as the 1.9.3 report.
- Inputs: the same 47,024 files by collection count as the 1.9.3 report. The render sets were copied from the corpus and matched by SHA-256, with 0 mismatches.

## Method and limits

The run used SHREDDER with its AMD Ryzen 7 5700X (8 cores, 16 threads), 31.9 GiB RAM, Windows 11 Pro build 26300 and .NET 10.0.15. The host was not certified idle, and caches were not flushed.

Before any measurement, the rules for "worse" were written down: any input or page 1.9.3 handled that 1.9.5 does not, any damaged-file crash or timeout, or a 1.9.5 median outside 1.9.3's range on the slow or heavy side, for time or for peak memory. 1.9.3, 1.9.4 and 1.9.5 were measured together with no warmup. Regression, standards and the render sets received three measured passes per build, rotating the build order each pass; stress received one pass per build; each damaged input ran alone with a 30-second limit, once per build. The commands were `KillerPDF.App.exe --batch-resave` and `--batch-render` as in the 1.9.3 report.

This measures opening, resaving and rendering time and outcomes. It does not establish rendering fidelity, editing behavior or PDF standards compliance.

- [Summary by collection](benchmark-summary.csv)
- [Measured runs, including the regression recheck](benchmark-runs.csv)
- [Render summary](render-summary.csv) and [render runs](render-runs.csv)
- [Broad render memory by file](broad-render-memory-by-file.csv)
- [Damaged-file status summary](fuzz-summary.csv)
- [Outcome transitions from 1.9.3](outcome-transitions.csv)
