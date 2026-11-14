# KillerPDF 1.9.3 corpus report

Test date: 2026-11-13

The KillerPDF 1.9.3 release candidate saved 41,505 of the 46,944 inputs in the three normal collections, with every input giving exactly the same outcome as 1.9.2 in the same session. A separate gate tested all 80 damaged inputs and recorded 0 crashes and 0 timeouts, also identical to 1.9.2 file for file. Opening, saving and rendering times are within 1.9.2's measured range. Memory is level: render peaks are within 1.9.2's range, and resave peaks are within about 1 MB of it.

## Results

Counts are per pass, not the sum of repeated passes. Every pass returned the same outcome for every input, and the same outcome as 1.9.2.

| Collection | Inputs | Saved | Skipped | Save failures | 1.9.3 median s | 1.9.3 range | 1.9.2 median s | 1.9.2 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,478 | 1,069 | 149 | 221.619 | 214.656 to 252.140 | 217.616 | 206.066 to 225.700 |
| Standards and color | 649 | 399 | 230 | 20 | 6.287 | 5.811 to 6.458 | 6.323 | 6.097 to 6.470 |
| Private stress | 29,599 | 25,628 | 2,712 | 1,259 | 515.196 | 512.311 to 518.081 | 523.356 | 516.442 to 530.269 |
| Damaged-file safety | 80 | 0 | 78 | 2 | Separate timeout gate | | | |

Peak working set on regression was 706.1 to 706.5 MB against 1.9.2's 705.1 to 705.6 MB, about 1 MB (0.1%) more; standards was level at about 708 MB, and stress was lower at about 1,164 MB against 1,170 MB. Render peaks were inside 1.9.2's per-pass range on all three sets. The regression median is 1.8% above 1.9.2's and inside its range; one 1.9.3 pass at 252 s sat beside passes of 221 and 222 s. In an earlier same-day session on a prior candidate the two were level.

## Rendering

Summed per-page render time for the same three page sets as the [1.9.2 report](../1.9.2/CORPUS.md), six measured passes alternating with 1.9.2:

| Set | Pages | Rendered | 1.9.3 median s | 1.9.3 range | 1.9.2 median s | 1.9.2 range |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Shared | 600 | 570 | 36.484 | 32.630 to 41.848 | 34.389 | 30.528 to 39.597 |
| Broad | 74 | 72 | 9.746 | 9.039 to 10.698 | 10.599 | 9.209 to 13.764 |
| Color | 175 | 175 | 17.677 | 16.529 to 18.554 | 17.147 | 16.000 to 21.001 |

The same pages render in both versions. Rendered pixels differ on purpose, because 1.9.3 shows CMYK colors through the working CMYK profile and draws edges with a new rasterizer, so no rendering parity claim is made. An earlier 1.9.3 candidate rendered CMYK pages up to 27% slower than 1.9.2. That was traced to display conversions computed and thrown away for ink paints and to the exact profile evaluation not being precompiled; both were removed before this candidate, with output identical to the slower build on all 817 rendered images.

## Changes from the previous published record

Against 1.9.2, measured in the same session, no input changed outcome in any collection. [outcome-transitions.csv](outcome-transitions.csv) is empty for that reason.

## Build and input identity

- Application: release candidate commit `c10c8c1aa16d1d4aca38c1a6c237d404b9cc0cbc`, file version `1.9.3.0`, built as a Release, framework-dependent, ReadyToRun publish with the codec and engine published the same way, the same flags as the 1.9.2 build.
- Tested executable SHA-256: `A8F5CC9CB045EFADDFDB3E613E2B2D86FE570A163FC53C44074BECB9FF13EBB3`.
- Regression and render were measured on `c10c8c1`. Standards, stress and the damaged-file gate were measured on `1331be2`, the commit before it; the only changes between the two are in the application's window page list and its tests, not in the engine or the command-line code these runs exercise.
- Inputs: the same 47,024 files as the 1.9.1 and 1.9.2 reports, hashed and matched file for file.

## Method and limits

The run used SHREDDER with its AMD Ryzen 7 5700X (8 cores, 16 threads), 31.9 GiB RAM, Windows 11 Pro build 26300 and .NET 10.0.15. Timings are not comparable with reports before 1.9.2, which ran on a Ryzen 5 3600. The host was not certified idle, and caches were not flushed.

Before any measurement, the rules for "worse" were written down: any input or page 1.9.2 handled that 1.9.3 does not, any damaged-file crash or timeout, or a 1.9.3 median outside 1.9.2's range on the slow side, for time or for peak memory. Regression and standards received one warmup and five measured passes and stress two, alternating with 1.9.2; render sets received one warmup and six. The commands were `KillerPDF.App.exe --batch-resave` and `--batch-render` as in the 1.9.2 report, run by a same-session alternating script rather than the corpus runner. Reports after this one use three measured passes.

This measures opening, resaving and rendering time and outcomes. It does not establish rendering fidelity, editing behavior or PDF standards compliance.

- [Summary by collection](benchmark-summary.csv)
- [Warmups and measured runs](benchmark-runs.csv)
- [Render summary](render-summary.csv) and [render runs](render-runs.csv)
- [Damaged-file status summary](fuzz-summary.csv)
- [Outcome transitions from 1.9.2](outcome-transitions.csv)
