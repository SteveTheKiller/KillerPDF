# KillerPDF 1.9.7 corpus report

Test date: 2026-11-26

The KillerPDF 1.9.7 candidate build saved 41,505 of the 46,944 inputs in the three normal collections, with every input giving exactly the same outcome as 1.9.6 in the same session. A separate gate tested all 80 damaged inputs and recorded 0 crashes and 0 timeouts, identical to 1.9.6 file for file. Of the 817 rendered images, 811 are pixel-identical to 1.9.6, and the other 6 are pages whose text is drawn as filled letters with a thin outline, which 1.9.7 now draws at the outline's true width. Opening and saving times are within or below 1.9.6's measured range. Three render medians fell just outside 1.9.6's range, and rechecks put all three back inside the run-to-run spread.

## Results

Counts are per pass, not the sum of repeated passes. Every pass returned the same outcome for every input, and the same outcome as 1.9.6.

| Collection | Inputs | Saved | Skipped | Save failures | 1.9.7 median s | 1.9.7 range | 1.9.6 median s | 1.9.6 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,478 | 1,069 | 149 | 198.466 | 197.309 to 201.541 | 201.197 | 198.398 to 245.543 |
| Standards and color | 649 | 399 | 230 | 20 | 6.867 | 6.714 to 7.256 | 7.898 | 7.237 to 7.978 |
| Private stress | 29,599 | 25,628 | 2,712 | 1,259 | 453.849 | One pass | 479.122 | One pass |
| Damaged-file safety | 80 | 0 | 78 | 2 | Separate timeout gate | | | |

Peak working set on regression was 702.1 to 702.5 MB against 1.9.6's 702.3 to 703.3 MB, and on standards 703.0 to 706.6 MB against 702.7 to 706.4 MB, a median of 703.7 MB against 705.4 MB. Stress, measured once per build, peaked at 1,157.9 MB against 1,122.8 MB. That is 3.1% higher, under the 5% limit the single-pass rule allows, and the same 1.9.6 executable peaked at 1,133.8 MB in the 1.9.6 report.

## Rendering

Summed per-page render time for the same three page sets as the [1.9.6 report](../1.9.6/CORPUS.md), three measured passes per build in rotating order:

| Set | Pages | Rendered | 1.9.7 median s | 1.9.7 range | 1.9.6 median s | 1.9.6 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Shared | 600 | 570 | 30.708 | 30.351 to 33.279 | 29.966 | 29.807 to 30.441 |
| Broad | 74 | 72 | 7.400 | 7.350 to 7.630 | 7.593 | 7.504 to 8.639 |
| Color | 175 | 175 | 15.303 | 14.647 to 15.773 | 14.231 | 13.837 to 15.574 |

Shared's 1.9.7 median is above 1.9.6's range, which met the rule for investigation. Wall time for the whole Shared batch overlapped completely, at 9.424 to 10.043 s for 1.9.7 against 9.116 to 10.066 s for 1.9.6. Summed per-page medians differed by 1,464 ms, and the largest single page was `447403.pdf` at +246 ms, followed by `mipeng_poster_w24.pdf` at +115 ms. These are the same two pages that led the 1.9.6 report's Shared difference. Rendered alone, five alternating passes per build, `447403.pdf` had a median of 1,533 ms on 1.9.7 against 1,577 ms on 1.9.6, and `mipeng_poster_w24.pdf` had 509 ms (482 to 553) against 490 ms (487 to 493). The two next largest movers, `GWG180_16Bit_Images_ICCbasedRGB_x4.pdf` and `GWG081_DeviceN-Support_5c_X1a.pdf`, were within 1 ms of 1.9.6 alone. As in the 1.9.6 report, the batch difference comes from pages competing for the processor rather than slower rendering. [shared-render-recheck.csv](shared-render-recheck.csv) has every pass.

Render peak memory was above 1.9.6's range on two sets: Broad's median was 330.0 MB against 320.0 to 328.6 MB, and Color's was 510.6 MB against 480.1 to 491.2 MB. Shared was inside the range at 490.5 MB against 422.9 to 569.1 MB. Both sets were rendered again after the measured passes, five alternating passes per build. Broad's median peak was 335.5 MB (326.2 to 358.6) on 1.9.7 against 336.8 MB (331.9 to 351.4) on 1.9.6, and Color's was 498.6 MB (485.5 to 503.5) against 500.5 MB (473.6 to 517.9). With more passes, both builds land in the same range, so the measured-pass difference is run-to-run spread. [render-memory-recheck.csv](render-memory-recheck.csv) has every pass.

## Pixel changes

Six Shared images changed, and Broad and Color were identical on all 247 images. [pixel-changes.csv](pixel-changes.csv) lists each image with both hashes.

| Page | Changed pixels | Outline width |
| --- | ---: | ---: |
| `UA1_Tpdf-G2_F01.pdf` | 343 | 0.514 pt |
| `UA1_Tpdf-G2_F03.pdf` | 341 | 0.514 pt |
| `UA1_Tpdf-G5_F04.pdf` | 372 | 0.514 pt |
| `UA1_Tpdf-L_07.pdf` | 182 | 0.343 pt |
| `UA1_Tpdf-L_08.pdf` | 182 | 0.343 pt |
| `kateking_presentation_WN23.pdf` | 9,905 | Text rendering mode 2 |

Every changed page draws text with rendering mode 2, which fills each letter and strokes its outline, the way many PDF writers fake a bold weight. 1.9.6 drew those hairline outlines at a minimum of one device pixel, which is heavier than the PDF asks for at this render size. 1.9.7 draws a stroke over its own fill at its true width. The changes sit only on that text: the "FAIL" labels in the three PDF/UA fundamentals pages, the check marks in the two list pages, and the title on the presentation page. Each one is lighter in 1.9.7 and matches the weight of the letters beside it. Every other pixel on these pages is unchanged.

## Changes from the previous published record

Against 1.9.6, measured in the same session, no input changed outcome in any collection, and no damaged input changed status. [outcome-transitions.csv](outcome-transitions.csv) is empty for that reason.

## Build and input identity

- Application: commit `c284035b822821dae8ba1b5bc864c375c86d81bf` on `main`, untagged, file version `1.9.7.0`, built from `git archive` as a Release, framework-dependent, ReadyToRun publish with the codec and engine published the same way, the same flags as the 1.9.6 report. This is a candidate build, not the signed published binary.
- Tested executable SHA-256: `755CBD75FEEA0904F8C03D712A951A27EC77E713D568793FF6CE8EB9E148579C`.
- Baseline: tag `v1.9.6`, commit `28dde9055c761091958ce5e17e60c5fbf16fcef6`, executable SHA-256 `66C85C1039F45D9AAD2DA56D5C0C0EEA7E17A1D6822900CB132C300675322C27`, the same executable hash as the 1.9.6 report.
- Inputs: the same 47,024 files by collection count as the 1.9.6 report. The render sets were copied from the corpus and matched by SHA-256, with 0 mismatches.

## Method and limits

The run used SHREDDER with its AMD Ryzen 7 5700X (8 cores, 16 threads), 31.9 GiB RAM, Windows 11 Pro build 26300 and .NET 10.0.15. The host was not certified idle, and caches were not flushed.

Before any measurement, the rules for "worse" were written down: any input or page 1.9.6 handled that 1.9.7 does not, any damaged-file crash or timeout, or a 1.9.7 median outside 1.9.6's range on the slow or heavy side, for time or for peak memory. 1.9.6 and 1.9.7 were measured together with no warmup. Regression, standards and the render sets received three measured passes per build, rotating the build order each pass; stress received one pass per build; each damaged input ran alone with a 30-second limit, once per build. The commands were `KillerPDF.App.exe --batch-resave` and `--batch-render` as in the 1.9.6 report. The rechecks ran after the measured passes, with the same builds and the same render settings as their sets. The CSVs carry both builds' passes from this session, marked by version, because the 1.9.6 figures above are this session's baseline rather than the 1.9.6 report's.

This measures opening, resaving and rendering time and outcomes. It does not establish rendering fidelity, editing behavior or PDF standards compliance.

- [Summary by collection](benchmark-summary.csv)
- [Measured runs](benchmark-runs.csv)
- [Render summary](render-summary.csv) and [render runs](render-runs.csv)
- [Shared render recheck](shared-render-recheck.csv)
- [Render memory recheck](render-memory-recheck.csv)
- [Pixel changes](pixel-changes.csv)
- [Damaged-file status summary](fuzz-summary.csv)
- [Outcome transitions from 1.9.6](outcome-transitions.csv)
