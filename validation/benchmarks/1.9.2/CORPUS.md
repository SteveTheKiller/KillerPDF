# KillerPDF 1.9.2 corpus report

Test date: 2026-11-13

KillerPDF 1.9.2 saved 41,505 of the 46,944 inputs in the three normal collections, the same number as the 1.9.1 record, with no previously saved input becoming unsavable. A separate gate tested all 80 damaged inputs and recorded 0 crashes and 0 timeouts.

This report was measured late, on the day 1.9.3 was prepared, so that 1.9.3 had a same-session baseline. The 1.9.2 build was compiled from the `v1.9.2` tag rather than taken from the signed installer, and it ran alternately with the 1.9.3 candidate. See the [1.9.3 report](../1.9.3/CORPUS.md) for that comparison.

## Results

Counts are per pass, not the sum of repeated passes. Every pass returned the same outcome for every input.

| Collection | Inputs | Saved | Skipped | Save failures | Median seconds | Range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,478 | 1,069 | 149 | 217.616 | 206.066 to 225.700 |
| Standards and color | 649 | 399 | 230 | 20 | 6.323 | 6.097 to 6.470 |
| Private stress | 29,599 | 25,628 | 2,712 | 1,259 | 523.356 | 516.442 to 530.269 |
| Damaged-file safety | 80 | 0 | 78 | 2 | Separate timeout gate | |

Peak working set was about 706 MB on regression and standards and about 1,170 MB on stress. The damaged-file gate's status counts are in [fuzz-summary.csv](fuzz-summary.csv).

## Rendering

The run also timed `--batch-render` on three fixed page sets, which earlier reports did not measure: Shared (600 first pages at 1024 px), Broad (40 files, up to three pages at 2048 px) and Color (97 color and CMYK test files, up to three pages at 1024 px). Summed per-page render time, six measured passes:

| Set | Pages | Rendered | Median seconds | Range |
| --- | ---: | ---: | ---: | ---: |
| Shared | 600 | 570 | 34.389 | 30.528 to 39.597 |
| Broad | 74 | 72 | 10.599 | 9.209 to 13.764 |
| Color | 175 | 175 | 17.147 | 16.000 to 21.001 |

## Changes from the previous published record

The comparison is per file against the retained logs of the [1.9.1 report](../1.9.1/CORPUS.md).

| Collection | Previously failed, now skipped | Previously saved, now unsaved |
| --- | ---: | ---: |
| Private stress | 38 | 0 |

The 38 stress inputs are encrypted PDFs that 1.9.2 refuses up front with "batch mode does not strip encryption" instead of failing during the save. Regression and standards outcomes are identical to 1.9.1 file for file. [outcome-transitions.csv](outcome-transitions.csv) has the counts.

## Build and input identity

- Application: built from tag [`v1.9.2`](https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.9.2), commit `7ecbb6c9e0d26c567f38989936c8b6ef1019aa19`, file version `1.9.2.0`, as a Release, framework-dependent, ReadyToRun publish with the codec and engine published the same way. This is a source build of the tag, not the signed release binary.
- Tested executable SHA-256: `E08495488D99BA0FA333233D5DCE764A02D705E23C416DA5C5DA36B5E5ED6FA3`.
- Inputs: the same 47,024 files as the 1.9.1 report. Every one was hashed again and matched the 1.9.1 inventory file for file, and all 17,425 files covered by the public and color-import manifests matched them.

## Method and limits

The run used SHREDDER after its processor upgrade: AMD Ryzen 7 5700X (8 cores, 16 threads), 31.9 GiB RAM, Windows 11 Pro build 26300 and .NET 10.0.15. Timings are not comparable with reports before 1.9.2, which ran on a Ryzen 5 3600; outcome counts are. The host was not certified idle, and caches were not flushed.

Regression and standards received one warmup and five measured passes and stress two, alternating with 1.9.3. Render sets received one warmup and six measured passes, alternating with 1.9.3. Each damaged input was resaved alone with a 30-second limit. The commands were `KillerPDF.App.exe --batch-resave <collection> <output> --log <log.csv> --quiet` and `KillerPDF.App.exe --batch-render <set> <output> --size <px> --pages <n> --log <log.csv> --quiet`, run by a same-session alternating script rather than the corpus runner. Reports after this one use three measured passes.

This measures opening, resaving and rendering time and outcomes. It does not establish rendering fidelity, editing behavior or PDF standards compliance.

- [Summary by collection](benchmark-summary.csv)
- [Warmups and measured runs](benchmark-runs.csv)
- [Render summary](render-summary.csv) and [render runs](render-runs.csv)
- [Damaged-file status summary](fuzz-summary.csv)
- [Outcome transitions from 1.9.1](outcome-transitions.csv)
