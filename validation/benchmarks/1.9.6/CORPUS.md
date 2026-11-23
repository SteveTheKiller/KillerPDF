# KillerPDF 1.9.6 corpus report

Test date: 2026-11-22

The KillerPDF 1.9.6 candidate build saved 41,505 of the 46,944 inputs in the three normal collections, with every input giving exactly the same outcome as 1.9.5 in the same session. A separate gate tested all 80 damaged inputs and recorded 0 crashes and 0 timeouts, identical to 1.9.5 file for file. All 817 rendered images are pixel-identical to 1.9.5. Opening, saving and rendering times are within 1.9.5's measured range except Shared rendering, which a recheck traced to batch scheduling rather than the code. Render memory is lower on two page sets, because tiled JPEG 2000 images now decode one tile at a time.

## Results

Counts are per pass, not the sum of repeated passes. Every pass returned the same outcome for every input, and the same outcome as 1.9.5.

| Collection | Inputs | Saved | Skipped | Save failures | 1.9.6 median s | 1.9.6 range | 1.9.5 median s | 1.9.5 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,478 | 1,069 | 149 | 223.085 | 221.414 to 224.580 | 224.836 | 222.666 to 250.399 |
| Standards and color | 649 | 399 | 230 | 20 | 6.330 | 6.062 to 6.739 | 6.024 | 6.005 to 10.880 |
| Private stress | 29,599 | 25,628 | 2,712 | 1,259 | 478.814 | One pass | 511.936 | One pass |
| Damaged-file safety | 80 | 0 | 78 | 2 | Separate timeout gate | | | |

Peak working set on regression was 704.8 to 705.0 MB against 1.9.5's 704.4 to 704.7 MB. The 1.9.6 median of 704.9 MB is 0.2 MB (0.03%) above 1.9.5's highest pass, which met the rule for investigation. 1.9.6's own three passes spread by the same 0.2 MB, so the difference is no larger than the run-to-run spread. Standards was lower at 704.8 MB against 706.6 to 706.8 MB, and stress was lower at 1,133.8 MB against 1,168.3 MB.

## Rendering

Summed per-page render time for the same three page sets as the [1.9.5 report](../1.9.5/CORPUS.md), three measured passes per build in rotating order:

| Set | Pages | Rendered | 1.9.6 median s | 1.9.6 range | 1.9.5 median s | 1.9.5 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Shared | 600 | 570 | 32.609 | 31.990 to 33.024 | 30.903 | 30.694 to 32.540 |
| Broad | 74 | 72 | 8.283 | 7.556 to 8.892 | 8.500 | 8.153 to 8.845 |
| Color | 175 | 175 | 17.062 | 16.683 to 17.886 | 16.367 | 14.754 to 17.632 |

Shared's 1.9.6 median is above 1.9.5's range, which met the rule for investigation. Summed per-page medians were 31.113 s for 1.9.5 and 32.113 s for 1.9.6, and two pages carried 612 ms of that 1,000 ms difference: `447403.pdf` (+358 ms) and `mipeng_poster_w24.pdf` (+254 ms). Neither file holds a JPEG 2000 image. Rendered alone, five alternating passes per build, `447403.pdf` had a median of 1,526 ms on 1.9.6 against 1,569 ms on 1.9.5, and `mipeng_poster_w24.pdf` had 528 ms against 549 ms. Both pages take about 2.5 to 3 times as long inside the full batch as alone, on both builds, so the batch difference comes from pages competing for the processor rather than from slower rendering. [shared-render-recheck.csv](shared-render-recheck.csv) has every pass.

Render peak memory is lower on two sets: Shared's median peak was 458.8 MB against 1.9.5's 519.8 to 555.2 MB, and Broad's was 333.2 MB against 458.1 to 515.3 MB. Color was inside 1.9.5's range at 483.4 MB against 474.9 to 492.5 MB.

The 1.9.5 report traced its render memory increase to `balloon_a1b_jp2k.pdf`, whose page holds a 2717 by 3701 tiled JPEG 2000 image. Rendered alone at the Broad settings, three alternating passes per build, its peak was 135.6 to 135.7 MB on 1.9.6 against 362.7 to 363.0 MB on 1.9.5, and its render time was 235 to 242 ms against 375 to 381 ms. 1.9.6 decodes tiled JPEG 2000 images one tile at a time instead of holding a 32-bit sample for every pixel of the whole image. The file is in both the Shared and Broad sets. [jpeg2000-render-memory.csv](jpeg2000-render-memory.csv) has every pass.

Rendered pixels are identical to 1.9.5 on all 817 images: 570 in Shared, 72 in Broad and 175 in Color. Pixel hashes are recorded, not judged, so no rendering parity claim is made.

## Changes from the previous published record

Against 1.9.5, measured in the same session, no input changed outcome in any collection, and no damaged input changed status. [outcome-transitions.csv](outcome-transitions.csv) is empty for that reason.

## Build and input identity

- Application: commit `17351464bc8c9636bb5c1ba786d7386d118fce87` on `main`, untagged, file version `1.9.6.0`, built from `git archive` as a Release, framework-dependent, ReadyToRun publish with the codec and engine published the same way, the same flags as the 1.9.5 report. This is a candidate build, not the signed published binary.
- Tested executable SHA-256: `66C85C1039F45D9AAD2DA56D5C0C0EEA7E17A1D6822900CB132C300675322C27`.
- Baseline: tag `v1.9.5`, commit `aab0c566c92f86dace2fbf9cc056d3d1b06cddec`, executable SHA-256 `5955876CF892745E58B114A55AF76C8C2093A325C9B7CCC388E3108F11D03D2C`, the same executable hash as the 1.9.5 report.
- Inputs: the same 47,024 files by collection count as the 1.9.5 report. The render sets were copied from the corpus and matched by SHA-256, with 0 mismatches.

## Method and limits

The run used SHREDDER with its AMD Ryzen 7 5700X (8 cores, 16 threads), 31.9 GiB RAM, Windows 11 Pro build 26300 and .NET 10.0.15. The host was not certified idle, and caches were not flushed.

Before any measurement, the rules for "worse" were written down: any input or page 1.9.5 handled that 1.9.6 does not, any damaged-file crash or timeout, or a 1.9.6 median outside 1.9.5's range on the slow or heavy side, for time or for peak memory. 1.9.5 and 1.9.6 were measured together with no warmup. Regression, standards and the render sets received three measured passes per build, rotating the build order each pass; stress received one pass per build; each damaged input ran alone with a 30-second limit, once per build. The commands were `KillerPDF.App.exe --batch-resave` and `--batch-render` as in the 1.9.5 report. The two single-file rechecks ran after the measured passes, with the same builds and the same render settings as their sets. The CSVs carry both builds' passes from this session, marked by version, because the 1.9.5 figures above are this session's baseline rather than the 1.9.5 report's.

This measures opening, resaving and rendering time and outcomes. It does not establish rendering fidelity, editing behavior or PDF standards compliance.

- [Summary by collection](benchmark-summary.csv)
- [Measured runs](benchmark-runs.csv)
- [Render summary](render-summary.csv) and [render runs](render-runs.csv)
- [Shared render recheck](shared-render-recheck.csv)
- [JPEG 2000 render memory](jpeg2000-render-memory.csv)
- [Damaged-file status summary](fuzz-summary.csv)
- [Outcome transitions from 1.9.5](outcome-transitions.csv)
