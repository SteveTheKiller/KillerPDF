# KillerPDF 1.9 release gates

Release remains blocked while a known regression from the 1.8 pipeline exists.
An improvement over an earlier 1.9 build does not establish parity with 1.8.
Missing measurements are unverified, not passes. Test counts and successful
batch completion do not establish visual or interactive equivalence.

The current PDFium maintenance checkout reports version 1.8.80 in its local
`KillerPDF-1.8` directory. Earlier retained comparisons used 1.8.72 or 1.8.5
payloads. Use identified application DLLs and identical inputs when
comparing pipelines. Record all alternating runs, warmups, peaks, outliers, and
measurement variability. Do not hide a slower or larger workload behind an
overall average.

| Requirement | Current status | Evidence still needed |
| --- | --- | --- |
| Memory at parity or close to the PDFium pipeline | Shared batch lower; difficult batch above 1.8; visible interactive use unverified | The October 1 installed-payload difficult set peaked at 194.3/194.1 MiB for 1.9 versus 130.2/124.5 MiB for 1.8.80 at 512 pixels, and 274.7/278.7 versus 252.0/252.0 MiB at 2048 pixels. All 74 pages completed in every run. Earlier woven builds inflated the small-file gap. September 9 installed-layout medians were 495.5 versus 614.6 MiB shared and 283.7 versus 260.3 MiB difficult. The current 1024-pixel shared runs peak near 502 versus 620 MiB. A headless three-size balloon zoom sequence peaks at 289.8 to 289.9 MiB with either fresh or retained application sessions. Verify representative visible interaction and an explicit acceptable tolerance before release. |
| Rendering and whole-pass speed without regression | Open | The October 3 comparison of current-source installer payloads found 1.9 slower on 600 shared first pages by 11.4% at 512 pixels and 15.3% at 2048 pixels. The map contributes about 646 ms to the 512-pixel gap; two Altona technical pages contribute about 2.27 seconds to the 2048-pixel gap. The 1.9 payload rendered 614 pages without failure at both sizes; 1.8.80 rendered 600 and failed on eight. A fresh eight-copy CMYK JPEG test found warmed times near parity, while the first 1.9 page remained slower. Whole-pass parity is not established. |
| Rendering fidelity without regression | Open | All 600 shared and 74 difficult output dimensions match PDFium. The bounded-mask optimization preserved the packaged 1.9 PNG hashes for all 614 successful conformance first pages at both 512 and 2048 pixels. The latest stencil correction improves pixel agreement on 12 pages and leaves 662 unchanged. A parallel CMYK alpha race is fixed and the 74 difficult pages match their prior serial hashes at one and four render workers. Installed-font aliases and pattern fixes are also retained. The Ghent GWG080 and GWG081 ReadMe pages require checks on both images and the gradient with no Xs. At 1024 pixels, 1.8.80 omits the image marks, while 1.9 draws a checkmark with a visible X beneath it on the colorized image. Neither branch passes. Native CMYK surfaces now preserve named spots under mixed Black and spot images, but the Ghent pages use the default RGB surface and remain incorrect. Spot plates must survive that surface before either branch can claim those patches pass. Absolute color conformance, font, other compositing, and fine-detail differences remain open; RGB-to-CMYK conversion remains an approximation. |
| Startup, first-page display, scrolling, and zoom without regression | Startup ready and hidden first bitmap attachment measured; visible interactions unverified | September 29 hidden-window warm launches reached the ready marker at 1,201.627 ms for 1.9 versus 1,346.588 ms for 1.8. A pre-JPEG installed-layout headless scan probe has a first-call median of 806.05 ms for 1.9 versus 248.664 ms for 1.8, including the 1.8 dimension lookup. The rebuilt current-payload five-step scan zoom sequence has a first 512-pixel median of 674.110 ms for 1.9 versus 169.543 ms for 1.8; its second 2048-pixel median is 40.599 versus 222.675 ms, with high variability in the 1.9 repeat steps. On a separate CMYK page, the reduced-JPEG change cut the first 512-pixel render from 594.004 to 374.444 ms; 1.8 measured 99.400 ms in its earlier five-size sequence. The earlier technical Altona first render took 783 to 793 ms for 1.9 versus 168 to 187 ms for 1.8. Continuous view now schedules the visible page before neighboring work. Hidden bitmap attachment is slower on two measured pages; visible first-page completion, scrolling, and zoom remain unverified. |
| Existing 1.8 functionality and maintenance fixes preserved | Partially verified; open for release | The seven 1.8 KillerMCP CLI operations pass headless checks on 1.9. A Release CLI smoke covers PDF editing, conversion, inspection, and reporting commands, including rendered page-order checks. The 1.9 MCP page covers the installed PDF tool set, and the site now shows the released 1.8.71 download facts. The desktop working-document path opens the retained #406 reporter PDF. The October 1 local-branch forward-port check passes for 201 maintenance commits. The release synchronization workflow is intentionally excluded. The 98SE recent panel matches maintenance in source and tests; visible inspection remains open. Visible feature workflows still need verification. |
| Builds and regression suites | Passing development checkpoint | October 1: 4,176 engine tests and 487 app tests pass, and the Release application build succeeds with zero warnings and errors. The earlier scratch portable and installer packages pass their file inventories and payload startup checks; the installer also passes isolated installation. A clean build reported 659 nullable warnings from vendored CoreJ2K. The earlier application and transitive package vulnerability check reported no vulnerable packages. Earlier hardware-intrinsics-disabled RGB coverage and isolated JPEG 2000 consumer checks also pass. Repeat required checks for the final release build; these checks alone do not close other gates. |

An October 3 comparison built both installer payloads from the current
1.8.80 and 1.9.0 checkouts, at commits `9ebe9ef` and `2207e82`. Both are
framework-dependent. The 1.9 engine and codec use ReadyToRun, as its
installer script specifies; the 1.8 engine uses its ordinary publish output.
Four serial headless traversals in 1.8/1.9/1.9/1.8 order covered 649 inputs
and 600 common rendered first pages at each size. At 512 pixels, their
shared-page render sums were 10,587/11,954/11,791/10,723 ms. The mean was
10,655 ms for 1.8.80 and 11,872.5 ms for 1.9, an 11.4% gap. At 2048 pixels,
the sums were 17,113/19,187/19,692/16,611 ms, or 16,862 versus 19,439.5 ms
on average, a 15.3% gap. Each 1.9 pass rendered 614 pages with no failures;
each 1.8 pass rendered 600, skipped 41, and failed on eight. The paired mean
gap for `447403.pdf` was 645.5 ms at 512 pixels. At 2048 pixels, the two
Altona technical pages contributed 1,280 and 987.5 ms. Raw outputs are under
`C:/Users/steve/kp-bench-render/packaged-parity-installer-20261003/`.

Within each version, repeat PNG hashes and dimensions matched on all
600 1.8.80 and 614 1.9 outputs at both sizes. Observed peak working sets,
sampled every 100 ms, were 612.5 to 613.3 MiB for 1.8.80 versus 521.8 to
524.4 MiB for 1.9 at 512 pixels, and 640.8 to 641.4 versus 492.4 to
493.0 MiB at 2048 pixels. These batch peaks do not close the difficult-set
or visible-interaction memory gates.

An October 3 eight-copy CMYK JPEG check at 2048 pixels used the same current
installer payloads. The first page took 120/120 ms in 1.8.80 versus
206/233 ms in 1.9. The last seven summed to 802/806 versus 852/780 ms,
so their means were 804 versus 816 ms, a 1.5% gap that changed direction
between pairs. The earlier large warmed gap did not recur in this check.
All eight page dimensions matched, and PNG hashes matched within each
version across both repeats. The versions' pixels differ. Raw results are
under `C:/Users/steve/kp-bench-render/cmyk-eight-current-20261003/`.

The October 1 packaged 512-pixel comparison averages 11,689 ms for 1.8.80
and 12,599 ms for 1.9 across the 600 shared pages, a 910 ms (7.8%) gap.
Those older payload DLLs differ from the current checkouts. The largest page
gaps, slower in both paired passes, are `447403.pdf` (+418.5 ms),
`CompactedPDFSyntaxTest.pdf` (+109.5 ms), the `42828.0001.001.pdf` JBIG2 scan
(+99.5 ms), the Ghent ALL reference (+93.5 ms), and
`UnknownFilter-Linearized.pdf` (+85.5 ms). Those five sum to 806.5 ms before
faster 1.9 pages offset other regressions. The large Altona technical page
is 95.5 ms faster in that packaged 512-pixel run, despite its large gap in
the separate loose serial comparison. These rankings come from the four
`mask-bounds-final-parity-512-*.csv` files under
`C:/Users/steve/kp-bench-render/release-payload-parity-20261001/`.

On eight packaged map copies, 1.9 was slower on the first copy and faster
on the next seven, so its single-page gap is concentrated in first use.
Both versions show the complete map at matching dimensions, but their
pixels differ; the timing result does not establish map fidelity parity.

A cold trace of the October 1 packaged 1.9 payload reproduced the saved
512-pixel map PNG and logged a 1,933 ms render. It recorded 1,982 completed
JIT method loads with 590.1 ms of summed start-to-load latency across
threads, plus about 127 ms of GC suspension intervals. Allocation-tick
intervals totaling about 553 MB were tagged `System.Byte[]`, and 93 MB
were tagged `PdfContentInstruction`.
The tag identifies the last allocation in each sampled interval, not the
exact bytes allocated by that type ([event definition](https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/runtime-garbage-collection-events#gcallocationtick_v3-event)).
These process-wide trace measurements cannot be subtracted from page time,
but they point to first-use compilation and materialization as the next
paths to isolate.
The traced payload predates the October 2 inline-image change, so its image
allocation path does not profile the current source.
The trace and inspector are under
`C:/Users/steve/kp-bench-render/map-cold-breakdown-20261002/`.

An October 2 change avoids retaining decoded inline images whose fresh stream
keys cannot be reused. Two balanced eight-copy map pairs at 512 pixels sampled
peak memory at 393.7/388.8 MiB before and 333.3/342.4 MiB after. Their render
sums were 9,357/9,329 ms before and 9,290/9,340 ms after, so speed is tied.
At 2048 pixels, one reversed-order pair peaked at 402.4 versus 365.6 MiB and
took 11,469 versus 11,150 ms. All eight map PNG hashes matched at both sizes.
The 649-file conformance run rendered 614 pages, skipped 35, failed none, and
matched all 614 saved 1.9 PNG hashes at 512 pixels. The Release build, all
4,188 engine tests, and all 487 app tests pass. Raw results are under
`C:/Users/steve/kp-bench-render/inline-cache-trial-20261002/`. The 1.8
performance, difficult-set memory, and visible-interaction gates remain open.

A fresh headless 1.8.80/1.9/1.9/1.8 comparison of the same 74 difficult pages
at 2048 pixels measured render sums of 4,769/4,792 ms for 1.8 and
8,892/8,593 ms for 1.9. Sampled peaks were 256.9/228.5 MiB versus
295.6/303.5 MiB. Both builds rendered all 74 pages; all 74 current 1.9 PNG
hashes matched the prior 1.9 build. The application DLL hashes begin
`5FA47515A` and `B645A4B6`, respectively. Altona technical page 1 remains
the largest gap at 215/212 versus 1,229/1,127 ms. This direct Release-DLL
comparison does not establish packaged installer or visible-interaction parity.
Raw outputs are under `C:/Users/steve/kp-bench-render/inline-cache-trial-20261002/`.

An October 2 same-DLL Altona probe measured direct first 2048-pixel renders at
1,147/1,210/1,183 ms. Later fresh-document copies in those processes averaged
351/362/354 ms over their last eight renders. A preceding 512-pixel render took
693/666 ms and reduced the next 2048-pixel render to 630/614 ms, but increased
total work. Pre-reading 28,806 page instructions took 85/81 ms and left the
next 2048-pixel render at 1,103/1,094 ms. A sampled trace found overlapping
raster, image, color, text, and font work, without one dominant parser cost.
All 2048-pixel raw output hashes matched. Evidence is under
`C:/Users/steve/kp-bench-render/altona-sequence-20261002/`.

An opaque grayscale overprint correction now preserves named spot ink on CMYK
surfaces. The 649-file 512-pixel run rendered 614 pages, skipped 35, and failed
none. Against the preceding 1.9 build, 610 PNGs were unchanged; the four
changed Altona and Ghent images moved closer to Poppler's overprint output.
A repeat with the final build matched all 614 candidate PNG hashes. Separate
single-run render sums were 12,414 ms before and 12,883/13,486 ms after, so
there is no measured speed gain. The final Altona 2048-pixel first render took
1,396 ms and its last eight averaged 365 ms. The Release build, 4,190 engine
tests, and 487 app tests pass. Altona and Ghent overprint fidelity and the 1.8
speed gate remain open. Results are under
`C:/Users/steve/kp-bench-render/altona-sequence-20261002/`.

An October 2 large binary-image sampling change reduced the focused JBIG2
scan's first 512-pixel page from 634/637 ms to 539/521 ms in balanced
runs. All 614 conformance PNGs at 512 pixels and all 74 difficult-set PNGs at
2048 pixels matched the source-matched baseline. The 614-page render sums were
14,487/14,361 ms before and 14,276/14,618 ms after, so broad speed remains
unproven. A fresh direct 2048-pixel comparison of the 74 difficult pages took
5,837/6,189 ms in 1.8.80 versus 10,822/10,759 ms in 1.9, with sampled peaks
of 231.7 MiB versus 302.0/301.2 MiB. Both versions rendered all 74 pages.
The Release build, 4,188 engine tests, and 487 app tests pass. Raw results are
under `C:/Users/steve/kp-bench-render/binary-row-trial-20261002/`. Performance,
memory, and visible-interaction parity remain open.

Single-colorant DeviceN spot images now carry their named colorant through
ordinary and indexed image paint. Two new repaint tests pass, as do all 4,185
engine tests. The 649-file conformance set rendered 614 pages, skipped 35, and
failed none; all 614 PNG hashes match the preceding 1.9 build at 512 pixels.
The GWG080 and GWG081 ReadMe PNG hashes at 1024 pixels are unchanged. Their
colorized image uses a mixed Black and named-spot DeviceN color space, so its
visible X remains. Trial results are under
`C:/Users/steve/kp-bench-render/spot-image-trial-20261002/`.

Mixed Black and named-spot DeviceN images now retain separate process and spot
values on native CMYK surfaces, including when indexed samples are reduced.
Three focused tests, all 4,188 engine tests, and 487 app tests pass. The
GWG080 ReadMe image still differs from Poppler: the affected image is painted
on the default RGB page surface, where the CMYK spot plates are unavailable.
The 649-file engine sweep completed with 608 rendered pages, 17 encrypted
files skipped, and 24 failures; this run is not directly
comparable with the earlier app conformance PNG run. The scratch render is under
`C:/Users/steve/kp-bench-render/spot-recheck-20261002/mixed-trial-96/`.

Direct lookup of installed Arial, Times New Roman, and Courier New faces avoids
the full font-catalog scan when a standard alias first appears. On the compacted
syntax PDF, four alternating fresh-process 512-pixel pairs measured 1.9 first
renders at 727/309/259/268 ms before and 193/179/165/151 ms after. Three
alternating 2048-pixel pairs measured 297/273/266 ms before and 193/179/169 ms
after. All paired PNG hashes matched. The 649-file conformance set rendered 614
pages, skipped 35, failed none, and matched all 614 prior 1.9 PNG hashes at
512 pixels. A later 1.8/1.9/1.9/1.8 whole-set comparison on their 600 common
pages measured 13,688/14,698/13,652/12,735 ms. Both 1.9 runs completed 614
pages with no failure and identical PNG hashes. The 487 app tests pass. The
compacted PDF still renders faster in 1.8.80, and broad speed and
visible-interaction parity remain open. Raw output
is under `C:/Users/steve/kp-bench-render/standard-font-direct-20261002/`.

A 1.9 binary-image reduction change reuses exact column coverage across output rows. Three alternating 16-copy JBIG2 scan pairs at 512 pixels measured later-15 render sums of 3,724/3,779/3,715 ms before and 3,637/3,513/3,386 ms after. The 2048-pixel pair was effectively tied at 3,518 versus 3,498 ms. All 16 focused PNG hashes matched at both sizes. Two reversed-order 649-file conformance pairs at 512 pixels completed 614 renders and 35 skips per run, with all 614 control and candidate PNG hashes matching. Their summed times were 37,884/37,285 ms before and 36,245/38,325 ms after, so whole-corpus speed is unproven. The Release build, all 4,180 engine tests, and all 487 app tests passed. Raw outputs and logs are under `C:/Users/steve/kp-bench-render/binary-columns-20261002/`; the 1.8 and visible-interaction gates remain open.

Two current 1.8.80 runs on the same 16-copy scan took 207/213 ms for the first page and 2,968/2,939 ms for the later 15. The optimized 1.9 first pages ranged from 736 to 994 ms, and its later-15 sums ranged from 3,386 to 3,637 ms. This focused 1.9 gain leaves both cold and repeated scan rendering slower than 1.8.

A second JBIG2 arithmetic context-state trial kept all eight scan PNG hashes identical in four alternating 512-pixel runs. After the first page, baseline render sums were 1,748/1,751 ms and trial sums were 1,617/1,779 ms. The mixed result did not justify retaining the change. Raw logs are under `C:/Users/steve/kp-bench-render/jbig2-context-state-20261002/`.

A fresh October 2 comparison of the current Release DLLs ran all 649 conformance PDFs at 512 pixels in 1.8/1.9/1.9/1.8 order. On the 600 pages both builds rendered, 1.8.80 summed 14,252/14,202 ms and 1.9 summed 15,063/14,667 ms, leaving a 3.3% to 5.7% gap. The 1.9 runs rendered 614 pages without failure and matched all 614 PNG hashes; 1.8 rendered 600 and still failed eight. The 1.9 and 1.8 application DLL SHA-256 values begin `55A360FC` and `5FA47515A`. Cold page gaps remain largest on compacted syntax, Altona, unknown-filter samples, and the JBIG2 scan. Raw logs and outputs are under `C:/Users/steve/kp-bench-render/current-parity-20261002/`.

A scratch 1.9 payload matching the release script's loose ReadyToRun engine and JPEG 2000 codec was compared with 1.8.80 in another 1.8/1.9/1.9/1.8 sequence. On their 600 common pages at 512 pixels, 1.8 summed 13,603/11,909 ms and 1.9 summed 12,847/12,533 ms. One pair favored 1.9 by 5.6%; the other favored 1.8 by 5.2%, so broad speed parity remains unproven. Both 1.9 runs rendered 614 pages without failure, and all PNG hashes matched the loose 1.9 build. The codec's ReadyToRun image reduced the focused unknown-filter JPEG 2000 page's first render from 261 ms with the ordinary codec to 168 to 183 ms, still above 1.8's 8 ms. Its later seven renders summed 21 to 26 ms versus 1.8's 17 ms. The compacted syntax page also retains a cold gap. The scratch payload is not a signed installer; raw files are under `C:/Users/steve/kp-bench-render/current-parity-20261002/`.

Deferring the bundled fallback font until an embedded font actually needs it passed 294 focused font tests and kept all 614 conformance PNG hashes unchanged. In two alternating 614-page comparisons against the source-matched ReadyToRun payload, the baseline took 14,228/13,151 ms and the trial took 13,296/13,598 ms. The pairs disagreed on speed, so the trial was removed. Scratch payloads, logs, and renders are under `C:/Users/steve/kp-bench-render/font-lazy-20261002/`.

Poppler 26.07.0 rendered both Ghent patches at 1024 pixels with `pdftoppm -overprint`: checks appear on both images and throughout the gradient, with no visible X beneath them. Rendering the same files without overprint omits those marks. These reference PNGs are under `C:/Users/steve/kp-bench-render/spot-overprint-20261002/poppler-overprint/` and `poppler-default/`. GWG080 uses an indexed named spot image and an indexed DeviceN image combining Black, that spot, and inactive channels; the existing overprint unit tests do not cover that combination. This reference establishes the target behavior, not a 1.9 pass.

The GWG080 content streams paint the Pantone X before the indexed DeviceN image and paint the checks before the spot gradient. A trial that marked mixed DeviceN as spot and zeroed `/None` inputs displayed checks but flattened the left turtle image to cyan and left purple Xs. It was removed. The paint order and reference output point to spot-plate knockout and overprint handling, rather than a tint-transform-only change.

A 120 by 100 pixel synthetic page isolates two different named spot colors painted with overprint. At pixel (45, 50), Poppler's overprint output is RGB (30, 103, 67), while the current 1.9 and 1.8.80 headless outputs are both (139, 198, 62). The second spot alone is also (139, 198, 62), so neither branch preserves the first spot in the overlap. The sample and reference are under `C:/Users/steve/kp-bench-render/spot-overprint-20261002/synthetic/`. In 1.9, `OverprintColor` marks a named spot for a four-channel ink blend without carrying its colorant name to `SetInkPixel`; this minimal mismatch gives a direct target for plate-aware compositing before retesting GWG080 and GWG081.

A CMYK transparency-group variant separates same-colorant knockout from different-colorant overprint. At the overlap of two tints of the same spot, Poppler renders RGB (185, 170, 219), 1.8.80 renders (182, 176, 214), and 1.9 renders (105, 98, 171). With two different spots, Poppler renders (30, 103, 67), 1.8.80 renders (139, 198, 62), and 1.9 renders (89, 110, 64). The 1.9 four-channel union improves the different-spot approximation but darkens the same-spot overlap. The samples and PNGs are under `C:/Users/steve/kp-bench-render/spot-overprint-20261002/synthetic/group-trial/`; correcting one case requires identifying the colorant rather than changing the shared blend rule.

Sampling the same page outside the overlap also exposes a separate conversion difference: Poppler renders the first spot as RGB (126, 107, 188) and the second as (127, 203, 40), while 1.9 renders (127, 118, 183) and (139, 198, 62). Its current DeviceCMYK conversion maps the two alternate ink values to those 1.9 colors. Separate named plates are necessary for overprint and same-spot knockout, but matching the reference RGB output also requires checking the final spot-color conversion. An additive four-channel blend alone cannot establish fidelity.

MuPDF's [separation API](https://github.com/ArtifexSoftware/mupdf/blob/master/include/mupdf/fitz/separation.h) keeps spot colors in separate planes; its [draw device](https://github.com/ArtifexSoftware/mupdf/blob/master/source/fitz/draw-device.c) matches source and destination colorant names to decide which planes overprint, and carries separations into group surfaces. This supports a 1.9 design that retains names and tint values through indexed palettes and DeviceN paints, allocates spot planes only when used, and copies or blends them with transparency groups before final color conversion. The existing four-channel union cannot represent same-spot replacement and different-spot preservation at once.

A trial selected CMYK storage for pages whose resources contain named spots and an enabled overprint state. It moved the synthetic overlap from RGB (139, 198, 62) to (89, 110, 64), still short of Poppler's (30, 103, 67), and left the GWG080 and GWG081 ReadMe images unchanged. Direct inspection now confirms that those ReadMe files have no CMYK output intent and render on the default RGB page surface. Against a fresh source-matched control, 31 of 614 conformance first-page PNGs changed at 512 pixels. Whole-page mean absolute RGB error against Poppler improved on 18, worsened on 11, and was effectively tied on two. The trial was removed; selecting a four-channel surface cannot replace separate named spot plates. Scratch renders and the comparison script are under `C:/Users/steve/kp-bench-render/spot-overprint-20261002/`.

A separate calculator change precompiles constant `index` and `roll` operands while retaining stack-limit errors. Three alternating 12-page Ghent Patch pairs at 2048 pixels measured median render sums of 4,095 ms before and 3,607 ms after, with all PNG hashes unchanged. The 74-page difficult set also kept every PNG hash at 512 and 2048 pixels, but its alternating timings were mixed. This is a focused gradient gain, not a whole-corpus speed or spot-color fidelity pass. Raw logs and renders are under `C:/Users/steve/kp-bench-render/spot-overprint-20261002/`.

A fresh serial 2048-pixel comparison of 74 difficult pages against 1.8.80 rendered every page in both builds. Two warmed passes summed 5,015 ms for 1.8 and 8,556 ms for 1.9, with the JBIG2-heavy `42828.0001.001.pdf` contributing about 672 ms of the per-page gap. A sampled trace of eight copies identified JBIG2 region decoding and image area conversion as its main managed costs. Reusing the arithmetic context state in the JBIG2 decoder saved 59 to 214 ms on four of five repeated-scan pairs and lost 42 ms on one; two alternating 74-page pairs saved 244 and 72 ms. All 148 broad comparison PNGs matched by SHA-256. These measurements improve the difficult set but do not establish whole-corpus parity.

Three alternating first-page pairs on `447403.pdf` measured 1.8.80 at 1,693 to 1,875 ms and 1.9 at 2,214 to 2,655 ms at 512 pixels. At 2048 pixels they measured 1,931 to 1,986 ms and 2,420 to 2,529 ms. Both builds produced matching dimensions and visibly similar map layouts. The page has about 172 MB of decoded content and 190 inline images. A sampled 1.9 trace includes content parsing and image area conversion among its larger managed costs. Raising the streaming reader's initial buffer from 64 to 256 KiB produced mixed timing across six balanced pairs, with no changed PNGs; the trial was removed. The gap persists across both output sizes, directing further investigation toward content and image processing. Raw results are under `C:/Users/steve/kp-bench-render/map-gap-20261002/`.

The latest unprofiled CMYK conversion trial reduced the later renders of `064034.pdf` from 135 to 139 ms to 118 to 120 ms in three fresh process pairs. First-render results were mixed. All 74 difficult-page PNGs and all 614 successful 512-pixel conformance PNGs matched the saved 1.9 baseline. This is a focused gain; it does not close the whole-pass speed or fidelity gates.

The October 2 Windows JPEG decoder path reduced six balanced fresh-process first renders of `064034.pdf` at 2048 pixels from 368 to 440 ms to 212 to 239 ms. It applies only to large full-resolution YCCK JPEGs in compatible rendering. The 649-file conformance tree completed at both 512 and 2048 pixels with 614 renders, 35 skips, and no failures. All 512-pixel output hashes matched the saved 1.9 baseline; three 2048-pixel pages changed by small channel values and were visually inspected. Two reversed-order difficult-set pairs showed lower render sums and no higher sampled peak memory. These results narrow the CMYK gap but do not close whole-pass or visible-interaction parity.

A lower-level Windows JPEG path also supports native half, quarter, and eighth-size CMYK output. Six alternating fresh-process first-page pairs on `064034.pdf` at 512 pixels measured 205 to 215 ms with the prior 1.9 decoder and 124 to 138 ms with native scaling; a fresh 1.8.80 render measured 108 ms. Six 2048-pixel pairs measured 203 to 209 ms before and 198 to 212 ms after. The 649-file conformance tree again completed at both sizes with 614 renders, 35 skips, and no failures. All 614 candidate PNGs at 2048 matched the prior 1.9 build; three eligible CMYK pages changed at 512, with a maximum channel difference of 10 and no differences above 16. All 74 difficult-set hashes were stable across repeated runs at each size. Two reversed-order 74-page pairs at 512 pixels measured 5,521/5,006 ms for the prior build and 5,131/5,238 ms for native scaling; sampled peak memory ranged 193.7 to 195.8 MiB before and 195.4 to 198.1 MiB after. At 2048 pixels, the prior sums were 8,246/8,565 ms and peaks 268.1/266.5 MiB; native sums were 8,402/8,391 ms and peaks 268.9/261.9 MiB. Whole-set totals and peaks vary enough that these pairs do not establish broad speed or memory parity. The focused 512-pixel page gain is repeatable; visible interaction remains unverified.

A fresh 512-pixel installed-layout comparison after native scaling used retained 1.8.80 and the new 1.9 candidate in 1.8/1.9/1.9/1.8 order. On the 600 pages both rendered, summed times were 11.229/12.257/12.201/11.405 seconds, leaving 1.9 about 7% to 9% slower in these runs. The 1.9 build rendered 614 pages without failure; 1.8 rendered 600, skipped 41, and failed on eight. The largest remaining 512-pixel timing gaps were the complete `447403.pdf` map, JBIG2 scan `42828.0001.001.pdf`, and Ghent ALL reference page. The large Altona page and `064034.pdf` were faster in 1.9 in both measured passes. This remains a headless first-page comparison, with no visible interaction result.

An eight-copy focused map run separates first-render cost from repeated rendering. In two runs each at 512 pixels, 1.9 took 1,960 and 1,979 ms on the first copy, then averaged 1,031.7 and 1,093.0 ms over the next seven. Retained 1.8.80 took 1,538 and 1,533 ms first, then averaged 1,500.4 and 1,492.4 ms. All eight PNGs within each run had the same hash; the two 1.9 runs also matched each other. The 1.9 map deficit in the one-page corpus comparison is a cold-render issue, while its repeated map rendering is faster. These runs do not establish a general warm-page advantage. A separate first-render sampled trace logged 2,176 ms and placed 675 ms of exclusive intervals in GC polling, 302 ms in area conversion, and 191 ms in content parsing. These are profiler samples under overhead, not additive page-stage timings. Raw logs, PNGs, and the trace are under `C:/Users/steve/kp-bench-render/map-focused-20261002/`. The JBIG2 scan and Ghent page remain cold-render targets.

Two area-weight trials reused first and last column weights for wider RGB footprints on the map. Six paired 512-pixel map batches and three paired 2048-pixel batches favored the first trial with identical PNG hashes. Its two 614-page 2048-pixel corpus pairs were slower by 272 and 81 ms. Restricting the shortcut to footprints wider than eight samples kept 24 paired map PNGs and 614 corpus PNGs identical, but a fresh 2048-pixel corpus pair was 476 ms slower. Both trials were removed. Focused tests and the full 4,180 engine and 487 app tests passed; raw runs remain under `C:/Users/steve/kp-bench-render/map-gap-20261002/`. Broad image-area shortcuts have not closed the first-page speed gap.

The map's unfiltered inline images were copied once when parsed and again when decoded. Reusing the parser-owned bytes only inside image painting removes the second copy without changing public stream decoding. A broader borrowed-buffer trial removed the parser copy but made cold map renders slower, so it was removed. With the retained change, two balanced eight-copy map runs at 512 pixels sampled peaks of 465.5/444.3 MiB for the prior 1.9 payload and 331.7/321.1 MiB for the candidate. At 2048 pixels, peaks were 451.9/451.0 versus 387.3/386.9 MiB, and summed render times were 11,488/12,491 versus 11,040/10,914 ms. All eight map PNG hashes matched at each size. The full 649-file conformance tree rendered 614 pages, skipped 35, and failed none at both sizes; all 614 candidate PNG hashes matched the prior 1.9 payload at each size. A balanced full-tree 512-pixel check measured prior render sums of 11,728/11,413 ms and candidate sums of 11,600/11,627 ms, so a whole-tree speed gain is not established. Its sampled peaks were 498.8/515.9 MiB before and 455.9/502.0 MiB after. All 4,178 engine tests and 487 app tests passed, and the Release app build had zero warnings or errors. Raw trial logs, PNGs, and payloads are under `C:/Users/steve/kp-bench-render/map-borrowed-20261002/`. This reduces one map memory cost but does not close the 512-pixel speed or visible-interaction gates.

The current 1.8.80 checkout was then published into a fresh installed-layout payload. In 1.8/1.9/1.9/1.8 order at 512 pixels, the 600 shared successful pages summed to 13,281/13,000/12,176/11,466 ms. The 1.8 spread is too large to claim speed parity from this comparison. The 1.9 build completed 614 pages with no failures; 1.8 completed 600 and failed on eight. A separate retained 1.8.80 executable comparison gave 12,731/12,954/12,939/12,754 ms on those same shared pages. In that steadier run, the largest 1.9 gaps were the JBIG2 scan, Ghent ALL reference, compacted syntax test, and JPEG 2000 balloon; the map was no longer a consistent top outlier. These are headless first-page measurements and do not verify visible use. The current-checkout and retained-executable CSVs are in the same scratch directory.

Focused eight-copy 512-pixel runs separated two of those gaps. Compacted syntax took 152 to 162 ms on 1.9's first copy versus 10 ms on 1.8.80, then 3 to 7 ms versus 2 to 4 ms per copy. Ghent ALL page 1 stayed slower after first use: 1.9 took 82 to 108 ms per later copy versus 26 to 29 ms on 1.8.80. A sampled 1.9 Ghent trace concentrated renderer work in image conversion and soft-mask reduction. Precomputing horizontal mask-reduction bounds passed 22 focused tests and preserved all 614 successful 512-pixel conformance PNG hashes. Its two balanced full-tree timing pairs disagreed: baseline/trial sums were 18,197/16,783 ms and 14,023/14,562 ms. The trial was removed because it did not establish a repeatable whole-tree gain. Raw results and trial payloads are under `C:/Users/steve/kp-bench-render/ghent-first-20261002/`, `compacted-syntax-20261002/`, and `ghent-mask-bounds-20261002/`.

Ghent ALL page 1 contains seven large 8-bit ICC RGB images with soft masks. A dedicated area-conversion loop for converted three-channel images keeps plain RGB on its existing fast path. In balanced 32-copy 512-pixel runs, the baseline's last 30 copies took 3,118/2,997 ms and the first trial took 2,093/1,870 ms; three guarded runs took 1,902/1,550/1,650 ms. All focused PNG hashes matched. Two balanced full-tree 512-pixel pairs measured baseline sums of 15,220/15,042 ms and guarded sums of 13,261/13,831 ms. Each run rendered 614 pages and skipped 35; all 614 guarded PNG hashes matched the baseline at both 512 and 2048 pixels. At 2048 pixels, guarded full-tree sums of 20,966/20,420 ms overlap the fresh baseline range of 21,882/20,333 ms, so no broad large-page speed gain is claimed. All 4,178 engine tests and 487 app tests passed, and the Release app build had zero warnings or errors. This is a verified 1.9 improvement, but the 1.8 speed, memory, and visible-interaction gates remain open. Raw inputs, outputs, and logs are under `C:/Users/steve/kp-bench-render/ghent-rgb-area-20261002/`.

A current 1.8.80 versus optimized 1.9 comparison on the 600 pages both rendered measured 13,376/13,868/13,237/12,515 ms in 1.8/1.9/1.9/1.8 order at 512 pixels. The two 1.8 runs varied by 861 ms, so these runs do not establish a precise remaining percentage. Version 1.9 is still slower in both neighboring comparisons. It rendered 614 pages without failure; 1.8 rendered 600, skipped 41, and failed on eight. The paired CSVs and PNGs are in the same scratch directory. The 512-pixel speed gate remains open.

The current 1.8.80 and 1.9 payloads rendered eight copies of the JBIG2 scan at 512 pixels in 1.8/1.9/1.9/1.8 order. First-copy times were 249/624/483/212 ms. Later copies varied in both versions, so this supports a cold-render gap more clearly than a steady-render gap. A sampled 1.9 first-render trace placed 136 ms of exclusive sampled intervals in template-0 generic-region decoding, 44 ms in arithmetic decoding, and 27 ms in one-bit area sampling; profiler overhead prevents adding these to the page time. A direct bitmap-array and row-branch trial passed 15 focused JBIG2 tests and preserved all eight scan hashes. Its two first renders took 867 and 765 ms versus 735 and 717 ms for the same-build baseline, so the trial was removed. Results are under `C:/Users/steve/kp-bench-render/scan-current-20261002/` and `scan-generic-row-20261002/`.

On the same eight-copy scan, an alternating ordinary/ReadyToRun/ReadyToRun/ordinary 1.9 comparison measured first renders of 1,098/495/439/725 ms. The application DLLs matched by SHA-256, and all 32 PNGs shared one hash. Later-seven sums were 1,747/1,530/1,729/1,652 ms, so the cold improvement does not prove a sustained gain. A separate trial replaced byte-by-byte compressed-input copying with one array copy. Fifteen JBIG2 tests passed and all 48 paired PNGs matched, but its later-seven sums were 1,629/1,721/1,811 ms versus 1,404/1,595/1,590 ms in the three neighboring controls. The copy trial was removed. Scratch evidence is under `C:/Users/steve/kp-bench-render/scan-r2r-20261002/` and `C:/Users/steve/kp-bench-render/jbig2-span-copy-20261002/`. ReadyToRun narrows the first-render gap; 1.8 speed parity remains open.

Skipping the arithmetic decoder's redundant byte reread on ordinary input passed 15 JBIG2 tests and preserved all 64 scan PNG hashes. Four paired eight-copy runs measured later-seven sums of 1,824/1,750/1,955/1,745 ms with the trial versus 1,694/1,794/1,615/1,626 ms in the controls. First-render ranges overlapped. The trial was removed because it did not improve the scan. Matched application and codec binaries, with only the engine changed, are under `C:/Users/steve/kp-bench-render/jbig2-bytein-20261002/`.

The 16-bit ICC RGB page `GWG180_16Bit_Images_ICCbasedRGB_x4.pdf` remains a separate sustained gap. In alternating eight-copy 512-pixel runs, the later seven copies totaled 89/83 ms in 1.8.80 and 672/573 ms in 1.9. A sampled 1.9 trace concentrated time in ICC lookup conversion. Three bounded 1.9 trials cached RGB values, cached source pixels, or precomputed RGB lookup weights. Each preserved all eight focused PNG hashes, but none improved both paired timing runs, so all three were removed. The lookup-weight trial passed 195 focused ICC tests. The raw runs and trace are under `C:/Users/steve/kp-bench-render/icc16-current-20261002/`, `icc16-cache-20261002/`, `icc16-pixel-cache-20261002/`, and `icc-rgb-weights-20261002/`.

That page has one 16-bit and one 8-bit ICC RGB image placement, both using a small RGB matrix profile. Its CMYK output intent uses a reverse lookup table. The retained 1.9 change sends the source XYZ value directly to that output profile instead of calculating an unused display RGB color first. Two balanced 32-copy 512-pixel pairs measured last-30-copy baseline sums of 2,305/2,310 and 2,337/2,380 ms versus trial sums of 1,775/1,698 and 1,764/1,790 ms. At 2048 pixels, the corresponding focused sums were 3,115/3,221 versus 2,966/2,852 ms. All 32 focused PNG hashes matched in every comparison. The 649-file conformance tree rendered 614 pages and skipped 35 at both sizes, with no failures and all 614 trial PNG hashes matching the prior 1.9 build at each size. Balanced full-tree 512-pixel sums were 14,942/14,784 ms before and 16,191/14,442 ms after, so a broad speed gain is not established. All 4,178 engine tests and 487 app tests passed; the Release app build had zero warnings or errors. The current 1.8 speed and visible-interaction gates remain open. Raw evidence is under `C:/Users/steve/kp-bench-render/icc-direct-ink-20261002/`.

Further 32-copy trials tested vectorized four-channel output and four-corner interpolation in the ICC lookup. The vector trial preserved the page hash but its last-30-copy sums were 2,190/1,774 ms against neighboring scalar runs of 1,682/1,711 ms. Separating the vector loop made it slower at 2,533/2,056 ms against 1,778/1,781 ms. Four-corner interpolation changed pixels and measured 2,443/1,785 ms against 1,695/1,857 ms. Its mean RGB difference from PDFium improved only from 5.55106 to 5.54889 levels on this page. None was retained. Logs and outputs are under `C:/Users/steve/kp-bench-render/icc-vector-output-20261002/` and `icc-tetrahedral-20261002/`.

After that change, a fresh 512-pixel 1.8.80/1.9/1.9/1.8 comparison on 600 common successful pages measured render sums of 14,088/14,959/14,377/13,719 ms. Version 1.9 remains slower in both neighboring comparisons. It rendered 614 pages and skipped 35 with no failures; 1.8 rendered 600, skipped 41, and failed on eight. The largest consistent first-page gap is the JBIG2 scan `42828.0001.001.pdf` at 252/764/641/255 ms. The technical Altona page and the scanned-text `A Defence of the New Tenseless Theory of Time.pdf` are the next large gaps. The paired CSVs and PNGs are under `C:/Users/steve/kp-bench-render/icc-direct-ink-20261002/`.

Longer 512-pixel runs separated those first-page gaps from repeated work. In two alternating runs per version, the last 16 Altona copies averaged 184.8/185.8 ms in 1.8.80 and 106.9/110.8 ms in 1.9. The last six scanned-text copies averaged 112.5/107.5 versus 108.2/111.0 ms. The last eight JBIG2 copies averaged 226.9/218.5 versus 235.4/227.0 ms. Sixteen copies of the simple UnknownFilter page took 244 to 255 ms on 1.9's first render and 2 to 3 ms on most later copies. Every repeated 1.9 page kept one PNG hash. These results move the priority for these pages to first-render cost; they do not close the whole-corpus or visible-interaction speed gates. The paired logs and PNGs are under `C:/Users/steve/kp-bench-render/altona-long-20261002/`, `tenseless-focused-20261002/`, `jbig2-long-20261002/`, and `simple-gap-20261002/`.

A new scratch 1.9 payload uses the same IL app and ReadyToRun engine and codec layout as the portable build. Four alternating fresh-process pairs on `064034.pdf` took 102 to 107 ms in 1.8.80 and 214 to 245 ms in 1.9 at 512 pixels. At 2048 pixels, 1.8.80 took 121 to 130 ms and 1.9 took 373 to 473 ms. The four 1.9 PNGs matched the saved baseline. A cold-only 1.9 sample trace places JPEG decoding ahead of image conversion; a twelve-render trace places image conversion first. One output-row span trial kept pixels identical but did not improve six paired cold renders, so it was removed. The paired logs, traces, and rejected trial are outside the repository under `C:/Users/steve/kp-bench-render/memory-stage-20261002/r2r-parity/`.

A sampled 512-pixel profile of all 649 conformance files completed 614 renders and 35 skips. It identified installed-font indexing as a first-use cost, though sampled thread time includes parallel workers and lock waits. A trial loaded font descriptions in parallel while registering faces in the original order. Eight alternating fresh-process Altona renders measured 625/636/620/617/614/628/617/622 ms in baseline/trial/trial/baseline/trial/baseline/baseline/trial order. All eight PNG hashes matched, but the timing ranges overlap, so the trial was removed. The profile and trial outputs are under `C:/Users/steve/kp-bench-render/corpus-cpu-20261002*` and `C:/Users/steve/kp-bench-render/font-index-parallel-20261002/`.

A bounded initial character-list capacity also kept all 614 conformance PNG hashes at 512 pixels. The four alternating whole-tree render sums were 67,371/65,019/63,183/63,130 ms in baseline/trial/trial/baseline order. The second trial and baseline were effectively equal, so the extra allocation was not retained. Outputs and CSVs are under `C:/Users/steve/kp-bench-render/unicode-capacity-20261002/`.

Moving the plain RGB and gray image-format checks outside the area sampler's pixel loop preserved all 20 Altona PNG hashes. Four alternating 512-pixel render sums were 1,526/1,578/1,614/1,649 ms in baseline/trial/trial/baseline order. The trial showed no speed gain and was removed. Outputs and CSVs are under `C:/Users/steve/kp-bench-render/direct-area-loops-20261002/`.

Instrumented scratch builds from the clean 1.8.72 and 1.9.0 trees opened the
same PDF in separate hidden WPF processes with isolated Continuous-view settings.
The trace marks when page one's bitmap is assigned to its slot. On the JBIG2
scan `42828.0001.001.pdf`, six alternating launches per version had median
attachment times of 2,114.720 ms for 1.8 and 2,589.395 ms for 1.9. Median
peak working sets at attachment were 406.68 and 429.03 MiB. On the CMYK JPEG
`064034.pdf`, four alternating launches per version had median attachment
times of 1,332.614 and 1,680.166 ms, with median peaks of 291.05 and
343.40 MiB. All runs attached page one once and exited after the trace was
captured. This measures an internal WPF bitmap assignment, not a displayed
frame or scroll and zoom behavior. Traces, scratch sources, payloads, and the
runner are under `C:/Users/steve/kp-bench-render/viewer-attach-20260930`.

Additional stage marks on two launches per version locate the CMYK JPEG delay
inside the first backend render: 151 to 164 ms in 1.8 versus 382 to 447 ms in
1.9. Bitmap assignment followed backend completion by 108 to 159 ms in 1.8
and 16 to 23 ms in 1.9. On the JBIG2 scan, both measured 1.8 launches started
a second backend pass before attaching page one; the guarded 1.9 path started
one pass, but that pass still took 937 to 967 ms. These hidden-process marks
support engine work and do not establish a displayed-frame timing.

A scratch 1.9 build timed image decode and paint during one hidden Continuous
startup on `064034.pdf`. Several page-zero renders overlapped. The longest
image decode call took 410.836 ms and its following paint took 43.429 ms;
the marked first backend render took 521.207 ms. The overlap prevents
attributing every image call to the first backend pass or summing them as a
stage breakdown. It does identify JPEG decode as a priority for a controlled
single-page probe. Three fresh, isolated 1541 by 2048 page-zero renders
then took 488 to 516 ms with one pixel hash and no diagnostics. The
instrumented runs measured 223 to 255 ms decoding the main JPEG and 61 to
67 ms painting it. Instruction parsing took 17 to 18 ms. A deeper decoder
trace counted 179,352 JPEG blocks at full resolution, with 67 to 93 ms in
entropy decoding and 74 to 93 ms in block reconstruction. These counters
add per-block timing overhead and leave other page-processing work
unattributed; they are diagnostic, not a baseline performance result. The
traces, probe, and scratch build are in the viewer attachment directory.

A fresh-process single-page probe against the retained loose 1.9 engine
(`CA3B4C4E3A2F354FA674298ED2E738758DB871D7CF5400AAB7A3225C9131D3E2`)
rendered scan `42828.0001.001.pdf` three times per square output size.
Median engine render times were 607.674 ms at 512 pixels and 640.497 ms at
2048 pixels; median document opening was 66.904 and 67.989 ms. The CMYK JPEG
`064034.pdf` measured 405.648 and 477.874 ms of rendering, with 61.625 and
62.312 ms of opening. Each size reproduced one hash with no diagnostics.
The scan's small resolution-dependent difference reinforces decoder and
area-conversion work as the next targets. This standalone probe does not
measure application startup or visible first-page presentation. Its source
and payload reference are under `C:/Users/steve/kp-bench-render/stage-current-20260930`.

A packed-state arithmetic decoder trial kept the scan's 512-pixel hash and
similar peak memory, and all 15 focused JBIG2 tests passed. Four alternating
fresh-process first renders per build gave median scan times of 604.365 ms
for baseline and 599.401 ms for the trial, below the observed run variation.
Repeated renders of the same parsed document were slightly slower with the
trial and mostly reused decoded data, so they cannot validate a decode gain.
The change was removed; current source remains at the retained baseline.
Scratch binaries and probe source are under
`C:/Users/steve/kp-bench-render/jbig-packed-decode-20260930`.

Precomputing exact binary-image column coverage also preserved the scan's
512- and 2048-pixel hashes and similar peaks, with 25 focused image tests
passing. Four alternating fresh-process renders per build measured baseline
and trial medians of 603.040 and 606.924 ms at 512 pixels, and 638.733 and
646.794 ms at 2048 pixels. The trial was removed because neither size
improved. Scratch payloads are under
`C:/Users/steve/kp-bench-render/binary-column-20260930`.

Temporary tracing of scan `42828.0001.001.pdf` found 1,338 arithmetic
generic regions totaling 23,231,936 source pixels. Every region uses
template 0 without an override or skip mask; the largest is 3813 by 5441.
The current decoder already sends these regions through its template 0 fast
loop. Adding fast paths for other templates would not improve this scan.
An ownership trace also found an existing shared context on all 1,338
regions, ruling out a 65,536-entry allocation per symbol.
The trace was removed from source after inspection; its scratch payload is
under `C:/Users/steve/kp-bench-render/jbig-template-trace-20260930`.

A September 30 sampled-thread profile against the retained loose engine
opened and rendered this scan 32 times at 512 pixels, with matching hashes.
Of 7,976 ms of sampled CPU intervals, 4,885 ms were attributed to the
generic-region line decoder, 982 ms to binary area conversion, and 434 ms
to unshifted JBIG2 bitmap blitting. This points the next 512-pixel scan
investigation at the template 0 decode loop. The profile is diagnostic and
does not measure a visible first page or a 1.8 comparison. Trace and
summarizer are under `C:/Users/steve/kp-bench-render/current-scan-cpu-20260930`.

A separate sampled-thread profile of the current loose 1.9 Release payload
completed all 74 difficult pages at 512 pixels, with 9,682 ms of logged
rendering under profiler overhead. Inclusive sampled CPU intervals included
3,692 ms in image handling, 2,443 ms in form rendering, and 2,261 ms in
text display; these call stacks overlap and must not be added together.
Unlike the single scan, the batch has no one decoder loop that dominates
every page. The largest measured 1.8 gaps remain the scan, JPEG 2000
balloon, CMYK JPEG, and Altona pages. Trace, page outputs, and the 74-row
log are under `C:/Users/steve/kp-bench-render/difficult-cpu-20260930`.

A direct full-resolution YCCK output loop for equal-sampled components kept
the existing pixel conversion and added no image buffers. Four alternating
16-copy `064034.pdf` runs per build at 2048 pixels cut median summed rendering
from 3,853 to 3,426 ms, with sampled peak medians of 150.6 and 150.8 MiB.
Three alternating 74-page difficult runs per build cut the median render sum
from 10,999 to 10,618 ms and wall time from 16,895 to 16,450 ms; median
peaks were 268.4 and 267.0 MiB. Two alternating 600-page shared runs per
build cut median rendering from 17,197 to 16,284 ms, with median peaks of
495.9 and 493.8 MiB. The first shared baseline wall run was unusually slow,
so it is not used for a wall-time claim. All 128 targeted, 444 difficult, and
2,400 shared PNGs
matched their paired clean 1.9 baseline hashes. The Release payload built,
135 focused JPEG tests, all 4,163 engine tests, and all 487 app tests passed.
This is a 1.9 improvement, not proof of 1.8 parity or visible interaction.
Raw runs and payloads are under the viewer attachment scratch directory.

The Continuous view scroll-settle timer no longer cancels an active base render
for the same viewport center. A fresh-process headless simulation of the
`42828.0001.001.pdf` first page compared six retained renders with six
250-millisecond cancel-and-restart renders at 1447 by 2048 pixels. Median time
to a completed page was 956.160 versus 1,103.766 milliseconds, and median
peak working set was 147.0 versus 158.0 MB. All twelve pixel hashes matched.
This isolates restart cost; it does not measure WPF presentation or establish
1.8 first-page parity. The scratch probe is under
`C:/Users/steve/kp-bench-render/viewer-primary-20260930`.

Adjacent-page prefetch in Single and Two-Page modes omitted the display-only
night-mode inversion that the direct and secondary render paths apply before
rotation. The 1.9 prefetch path now uses the same inversion and picture
carve-out before caching its bitmap. Version 1.8 has no adjacent-page
prefetch path. Tab and view switches now cancel obsolete prefetch before
starting the newly visible render. The Release build, 21 focused app tests,
and all 485 app tests pass after this change; all 4,163 engine tests passed
with the unchanged engine. Visible night-mode navigation and the interaction
speed effect remain unmeasured while the desktop UI is in use. Cancellation
does not interrupt a JBIG2 stream already being decoded; it stops subsequent
prefetch work after that decode returns.

The desktop working document previously parsed a PDF for its page count, then
the renderer opened and parsed the same file again. The viewer now offers that
compatible parsed document to the existing weak render cache. It drops the
working document's strong parsed reference after the first render session
opens, and a changed file stamp still invalidates the cache. Four alternating
fresh-process scan probes at 512 pixels measured median render-session opening
at 22 ms with reuse versus 44 ms after a separate parse. Thread allocation
during that opening was 4.85 versus 21.43 MiB. When each path ran first, its
process peak was 131.3 to 131.5 versus 151.2 to 151.8 MiB. All page hashes
matched. The full 4,163 engine and 487 app tests pass, as does the Release app
build. This headless parse saving does not close the larger first-render or
visible interaction gates. Probe code and raw output are under
`C:/Users/steve/kp-bench-render/working-cache-20260930`.

The forward-port guard passes against local 1.8.72 commit `d158706b` and the
current 1.9 checkout: 194 maintenance commits are covered. The seven 1.8.71
KillerMCP CLI operations were adapted to the 1.9 engine in `bfb3cf6`, and
`c38f66a` expanded the MCP page beyond merging. The 1.9 site now carries the
published 1.8.71 date and installer digest across five page footers and 15
localized footers. Its 1.8.70 corpus result remains historical. Active 1.9
version metadata stays Unreleased, and its development README does not claim a
stable source archive. The 1.8 release synchronization workflow is recorded
as intentionally excluded because new release automation is not authorized.
The default remote-ref check still depends on pushing both local branches;
use the local 1.8 tip for the current coverage audit.

A September 30 headless Release CLI smoke used the current loose 1.9 payload
and scratch PDFs. Merge, extract, rotate, delete, move, insert blank,
duplicate, optimize, page numbering, booklet, N-up, auto bookmarks, split,
Bates labeling, image conversion, and flattening completed with expected page
counts. Document info, preflight, accessibility, attachments, and text search
returned parseable reports. A two-page PDF made from an original and rotated
page verified move, duplicate, extract, and delete by exact 72-dpi rendered
PNG hashes in the expected order. The accessibility report returned findings
with its documented nonzero status. One malformed corpus PDF failed document
info in both 1.8.72 and 1.9; tagged-to-untagged merge was refused by design.
These checks do not exercise visible editing or establish full 1.8 parity.
Artifacts are under `C:/Users/steve/kp-bench-render/cli-release-smoke-20260930`.

### Difficult-set comparison after YCCK change (2026-09-30)

The current 1.9.0 and local 1.8.72 Release applications rendered the same 40
difficult PDFs, 74 pages total, at 2048 pixels and up to three pages per file.
After one warmup per version, six hidden, low-priority runs followed the order
1.8/1.9/1.9/1.8/1.8/1.9. Each run completed all 74 pages. Render sums were
5.721, 5.584, and 5.624 seconds for 1.8 versus 10.232, 10.086, and 9.829
seconds for 1.9. Wall times were 11.392, 11.286, and 11.325 seconds versus
15.899, 16.004, and 15.482 seconds. Sampled peaks were 260.4, 260.1, and
260.1 MiB versus 286.7, 282.6, and 282.6 MiB. Median 1.9 rendering was
79.3% slower and its median peak was 22.5 MiB higher. The largest median
page gaps were 723 ms on `42828.0001.001.pdf`, 446 ms on
`balloon_a1b_jp2k.pdf`, 363 ms on `064034.pdf`, and 313 ms on
`GWG061_Shading_x1a.pdf`. Raw runs, outputs, and the runner are under
`C:/Users/steve/kp-bench-render/viewer-attach-20260930/current-v18-v19-difficult`.
These absolute timings should not be compared directly with the earlier
batch below, which ran under different conditions. Visible interactions
remain unverified.

The same current applications also rendered the 74 difficult pages at 512
pixels, after one warmup per version and with the same six-run alternating
order. Median render sums were 3.028 seconds for 1.8 and 6.487 seconds for
1.9; median wall times were 3.935 and 7.735 seconds. Sampled peak medians
were 138.4 and 220.6 MiB. All runs completed all pages, each version
reproduced its own 74 PNG hashes, and cross-version dimensions matched.
The largest median page gaps were 408 ms on `42828.0001.001.pdf`, 308 ms
on `balloon_a1b_jp2k.pdf`, 282 ms on `064034.pdf`, and 271 ms on
`altona_measure_1v1a.pdf`. The 3.459-second median batch gap at 512 pixels
is still about three quarters of the 4.462-second gap at 2048 pixels. This
points to substantial work that does not scale with output pixel count, but
the batch timing alone cannot divide it among parsing, decoding, and setup.
Raw output and analysis are under
`C:/Users/steve/kp-bench-render/viewer-attach-20260930/current-v18-v19-difficult-512`.

Separate-process 512-pixel probes of eight difficult files with the woven
development app found 1.9 peak working sets 35 to 109 MiB above 1.8. Two
small one-page files showed a roughly 90 MiB gap, while empty batch
processes were nearly equal. A GC allocation trace of the small syntax file
identified about 62 MiB of sampled byte-array allocations in Costura's
embedded-assembly stream loading and about 19 MiB in related array creation.
This explains why woven development builds exaggerate the release-layout
memory gap. With the current 1.9 loose payload, the two small pages peaked
at 79.2 versus 54.9 MiB and 83.4 versus 54.2 MiB for 1.9 and 1.8. These
are single cold-process observations, not stable medians. Woven observations
are under `C:/Users/steve/kp-bench-render/isolated-memory-512-20260930`;
the GC trace and loose comparison are under
`C:/Users/steve/kp-bench-render/first-render-gc-20260930` and
`C:/Users/steve/kp-bench-render/loose-memory-512-20260930` and
`C:/Users/steve/kp-bench-render/loose-current-20260930`.

The current loose payload still spent about one fifth of sampled CPU time
indexing installed fonts on the small syntax page. A shortcut for standard
Windows font filenames reduced its 12-pair fresh-process median from 230.5
to 150 ms, with identical PNG hashes and peak medians of 79.0 and 76.7 MiB.
On the 74-page difficult set, the two measured render totals were 6,294 and
6,327 ms for baseline versus 6,304 and 6,402 ms for the shortcut; all 74
PNGs matched. The trial was removed because it would bypass the established
precedence of a separately installed Helvetica or Courier family on other
machines. The current source remains unchanged. Trial output is under
`C:/Users/steve/kp-bench-render/font-fastpath-loose-20260930`.

A fresh 512-pixel comparison used the current 1.9 loose payload and the
1.8.72 loose payload, with one warmup each and six alternating measured
runs. All 74 pages completed in every run. Median render sums were 6.348
versus 2.842 seconds, wall times were 7.389 versus 3.693 seconds, and
peak working sets were 188.8 versus 127.1 MiB for 1.9 and 1.8. Each
version reproduced all 74 of its own PNG hashes across runs, and page
dimensions matched across versions. The largest median page gaps were
404 ms on `42828.0001.001.pdf`, 264 ms on `064034.pdf`, 218 ms on
`altona_measure_1v1a.pdf`, and 216 ms on `balloon_a1b_jp2k.pdf`.
This release-layout run confirms that Costura accounts for part, but not
all, of the woven 512-pixel memory gap. Raw logs and outputs are under
`C:/Users/steve/kp-bench-render/loose-v18-v19-difficult-512-20260930`.

A verbose GC allocation trace of the loose 1.9 payload over this difficult
set sampled about 420 MiB of byte arrays, 134 MiB of renderer Point arrays,
and 75 MiB of double arrays. These are sampled allocation volumes, not live
retention or peak working sets. The largest sampled double-array owner was
`PdfIccLut` construction at about 50 MiB. Evidence is under
`C:/Users/steve/kp-bench-render/loose-full-gc-20260930`.

The retained half of that trace attributes its largest Point-array samples
to cubic path growth (about 37.5 MiB across three leading stacks), pixel-space
fill conversion (12.1 MiB), and glyph outline flattening (about 22.2 MiB
across three leading stacks). These are allocations across the batch and do
not establish which path raises peak memory. A prior per-glyph contour buffer
trial reduced allocations on Ghent page 2 by 1.2% without a timing gain, so
repeating that change is not justified by this trace. The decoded stack
summary is `difficult-stacks.json` beside the trace.

Keeping large ICC lookup tables in their original bytes instead of expanding
every sample to a double lowered isolated Ghent and Altona Technical peaks by
about 12 to 14 MiB in six alternating pairs. The raw-byte version preserved
all 74 difficult and 600 shared PNG hashes, and 11 focused ICC tests passed.
It slowed the two-run shared-set median by about 204 ms. Small-table and
precomputed-lookup variants still slowed that set by about 171 and 189 ms,
respectively, despite saving roughly 8 to 11 MiB at its peak. None was
retained because the measured throughput cost conflicts with the performance
gate. Trial payloads and runs are under `icc-lut-raw-20260930`,
`icc-lut-hybrid-20260930`, and `icc-lut-lookup-20260930` in the local
benchmark root. Broader speed and memory parity remain open.

Storing normalized ICC samples as floats instead of doubles failed seven of
263 focused ICC tests, including curve and PCS color results. The trial was
removed before broad benchmarking. The restored source passes all 263 focused
tests, and no float-table change was retained.

Of the retained scan trace's 253 sampled CPU stacks, 67 pass through
`GenericRegion.DecodeTemplate0aFast` and 43 through
`ArithmeticDecoder.Decode`; these inclusive counts overlap. The current
decoder already selects the optimized template-0 path for this scan, so
adding that selection again would not address its gap. Symbol decoding still
needs a measured comparison with 1.8. The trace is under
`C:/Users/steve/kp-bench-render/viewer-primary-20260930`.

An isolated optimized-compilation hint on that template-0 loop passed all
15 focused JBIG2 tests and preserved the scan's 512-pixel page hash. Four
alternating fresh-process renders per build measured 666.665 ms for baseline
versus 672.476 ms for the hint at the median, with overlapping ranges and
roughly 120 MB peaks in both modes. The hint was removed because it did not
improve the target page. The scratch payload is under
`C:/Users/steve/kp-bench-render/jbig2-template-opt-20260930`.

A scratch JBIG2 arithmetic-decoder trial reused the previously read input
byte in the common path. Two isolated Release engines built successfully.
The scan's six measured 512-pixel renders kept one identical PNG hash.
Baseline render times were 787, 852, and 840 ms; trial times were 928,
823, and 941 ms. Their medians were 840 and 928 ms, respectively. Before
these runs, background CPU use measured 48 to 49 percent, so the timing
is noisy. It provides no evidence of a speed gain, and the decoder change
was not retained. Scratch sources, payloads, and logs are under
`C:/Users/steve/kp-bench-render/jbig2-bytein-20260930`.

An isolated trial used a 32-bit register for the JBIG2 arithmetic decoder's
bounded code value. It built and passed all 15 focused JBIG2 tests. Six
alternating 512-pixel difficult-set runs rendered all 74 pages, and every
trial PNG matched the baseline hash. Median render sums were 6.229 seconds
for baseline and 6.384 seconds for the trial; sampled peak medians were
216.4 and 214.9 MiB. The scan page one medians were 572 and 564 ms, too
small a gain to offset the slower full set. The change was not retained.
Runs and scratch source are under
`C:/Users/steve/kp-bench-render/jbig2-uint-register-20260930`.

`altona_measure_1v1a.pdf` exposes a size-specific area-sampling cost: its
three-run page median is 339 ms at 512 pixels but 144 ms at 2048 pixels in
1.9. Three isolated process renders per size reproduced the slower small
render, and a sampled trace attributed about 172 ms to `ConvertArea` at 512;
that path was absent at 2048. An isolated four-worker area trial cut the
Altona first-page median from 473 to 370 ms. The initial broad difficult-set
comparison had mismatched file-worker settings and is excluded. With one
file worker on both sides, its median 74-page render sum rose from 5.926 to
6.395 seconds. A narrower trial restricted parallelism to large, reduced,
8-bit four-channel images and again cut Altona from 467 to 364 ms, with
similar process peaks. Its paired difficult-set median still rose from 6.259
to 6.568 seconds. All 74 PNGs matched across the measured runs. Diagnostics
showed the narrower rule applied only to Altona Measure in this set; the
other page timing changes remain unexplained variability or build effects.
Neither trial established a whole-batch gain, so neither was retained.
Sources and runs are under `C:/Users/steve/kp-bench-render/area-small-parallel-20260930`
and `C:/Users/steve/kp-bench-render/area-cmyk-parallel-20260930`.

An alternating fresh-process balloon probe at the 1.9 page size of 1503 by
2047 pixels measured first renders of 481.503 and 473.333 ms through 1.8.72
versus 927.879 and 945.253 ms through 1.9.0. Four calls in each process kept
one pixel hash per version. The second 1.9 calls took 34.088 and 28.524 ms
because this probe reused the rendered page; second 1.8 calls took 444.188
and 465.873 ms. Thus the repeated 1.9 calls do not establish faster JPEG 2000
decoding. The first-call gap remains the useful target, while this small
process probe cannot establish whole-batch memory parity or visible latency.
Four additional fresh 1.9 processes split session opening from first-page
rendering: opening took 178.636 to 187.082 ms, while rendering took 727.390
to 808.752 ms. The page hash matched the earlier probe in every run. The
first-render stage is the larger target; these boundaries include runtime
initialization and do not isolate JPEG 2000 decode time.
Its raw output is under
`C:/Users/steve/kp-bench-render/viewer-attach-20260930/balloon-current-2048`.

A sampled thread-time trace of 20 fresh-document engine renders at 1504 by
2048 pixels used the current Release engine and kept one page hash with no
diagnostics. Of 13.12 seconds attributed to rendering, 10.57 seconds lie
under JPEG 2000 decode and 2.42 seconds under image painting. Within decode,
8.88 seconds lie under inverse reconstruction and 3.59 seconds under code-block
entropy decoding; these inclusive times overlap. Buffer copying and clearing
appear prominently in the trace, but their stacks include other work and do
not prove that removing any one copy improves whole-page speed. The trace,
probe, and analysis are under
`C:/Users/steve/kp-bench-render/balloon-trace-current-20260930`.

An isolated trial raised only JPEG 2000 decode workers from four to eight.
Two reversed-order fresh-document balloon pairs kept the same pixel hash;
last-ten render medians were 452.22 and 447.45 ms with four workers versus
428.61 and 421.50 ms with eight. Process peaks rose about 1 MiB in that
probe. A separate alternating full difficult-set check was mixed: the two
current-build render sums were 10.124 and 10.260 seconds versus 9.806 and
10.364 seconds for the trial. The balloon page itself measured 893 and 908
ms versus 886 and 928 ms. All 74 output PNGs matched in all four runs.
Neither a whole-batch gain nor a first-page gain is established, so the
eight-worker setting was not retained. Scratch source, build, and runs are
under `C:/Users/steve/kp-bench-render/j2k-eight-workers-20260930`.

A separate scratch build replaced three reconstruction `Array.Copy` sites with
typed span copies, preserving the copied sample ranges. Four alternating
full difficult-set runs rendered all 74 pages with identical PNG hashes.
Baseline render sums were 9.567 and 10.109 seconds; trial sums were 10.213
and 10.165 seconds. The balloon first page measured 893 and 850 ms before
versus 931 and 892 ms after. Sampled peaks overlapped, and the trial did not
establish a speed gain. It was not retained. Its source, build, and runs are
under `C:/Users/steve/kp-bench-render/j2k-copy-20260930`.

A scratch JPEG 2000 trial allocated fresh zeroed reconstruction frames instead
of renting smaller frames from the sample pool. Three alternating 74-page
difficult-set runs per build at 2048 pixels kept all 74 PNG hashes unchanged.
Median render sums rose from 9.191 to 9.779 seconds; sampled peak medians
fell from 283.1 to 279.0 MiB. The balloon page medians were 839 and 939 ms.
The speed regression rules out this pool policy for 1.9. Scratch source and
raw runs are under `C:/Users/steve/kp-bench-render/j2k-no-frame-pool-20260930`.

A guarded contiguous-row loop in the JPEG 2000 float dequantizer avoided
per-row index math when the source block had zero offset and a stride equal
to its width. Three alternating 2048-pixel difficult-set runs per build kept
all 74 PNG hashes unchanged. Median render sums were 9.137 seconds for the
baseline and 9.227 seconds for the trial; sampled peak medians were 288.3
and 286.1 MiB. Balloon page medians were 806 and 862 ms. The speed result
does not support retaining the loop. Scratch source and runs are under
`C:/Users/steve/kp-bench-render/j2k-contiguous-dequant-20260930`.

### Earlier difficult-set comparison (2026-09-30)

The current 1.9 Release build and unchanged local 1.8.72 Release build rendered
the same 40 difficult PDFs, 74 pages in total, at 2048 pixels and up to three
pages per file. The 1.9 batch used one file worker to match the serial 1.8 run.
After one warmup per version, six hidden, low-priority runs followed the order
1.8/1.9/1.9/1.8/1.8/1.9. All six runs completed all 74 pages without errors.
Render sums were 4.581, 4.601, and 4.671 seconds for 1.8 versus 8.309, 8.559,
and 8.422 seconds for 1.9. Wall times were 9.704, 9.700, and 9.878 seconds
versus 13.382, 13.627, and 13.506 seconds. Sampled peaks were 231.8, 260.0,
and 258.3 MiB versus 285.9, 287.0, and 286.5 MiB. Each version reproduced
all 74 of its own PNG hashes across three runs; all 74 output dimensions match
across versions. Cross-version pixel differences remain a separate fidelity
gate. The largest median page gaps were 587 ms on `42828.0001.001.pdf`, 332 ms
on `064034.pdf`, 323 ms on the JPEG 2000 balloon, and 274 ms on
`GWG061_Shading_x1a.pdf`. The 1.8 and 1.9 application DLL SHA-256 values are
`4AB13A69FD06146A5B963F4B5F5DBEC29761DC22940AF001700D0B0853CF05C8`
and `EDF81CF3EB095559652A29986904CC06889269389B047AE9974308E964A3E038`.
Raw logs, PNGs, runner, and analysis are under
`C:/Users/steve/kp-bench-render/difficult-current-20260930`. This headless
comparison does not measure WPF presentation or interaction.

On `GWG061_Shading_x1a.pdf`, the declared CMYK output profile accounts for a
visible color difference from both 1.8 and Poppler. LittleCMS conversion of
the embedded profile independently reproduces the red-channel clipping at
high cyan values and closely matches representative 1.9 colors. Ignoring the
profile removes the contour but loses the declared PDF/X print conversion.
A separate scratch trial kept the profile and forced independent axial-shading
rows onto workers. Four alternating fresh processes per variant measured 611
to 627 ms serial and 612 to 636 ms parallel, with identical PNG hashes and
sampled peaks of 96.5 to 98.0 MiB. No profile or parallelism change was
retained. Source and output are under
`C:/Users/steve/kp-bench-render/gwg061-profile-probe-20260930`.

A fresh-process `42828` probe at 1447 by 2048 pixels measured a 626.674 ms
median without warmup and 583.592 ms after rendering a different binary-image
page first. In a retained render session, low-resolution previews at 128, 256,
and 512 pixels moved the final full render later: median combined times were
738, 698, and 841 ms versus 633 ms for direct full rendering. All measured
full-size pixel hashes matched. A preview may appear sooner, but these checks
do not establish visible first-page behavior and do not improve full-page
latency. No preview or warmup change was retained. Raw probes are under
`C:/Users/steve/kp-bench-render/cold-path-prewarm-20260930`.

A separate headless probe invoked each application's primary viewer raster
backend from its bundled development build for `42828.0001.001.pdf` at 1447
by 2048 pixels. Two alternating
fresh-process runs per version measured the first 1.8 PDFium call at 305.998
and 306.703 ms, versus 943.157 and 1003.059 ms for the first 1.9 engine
call. Across the last ten of 20 calls per process, medians were 226.08 and
233.05 ms for 1.8, versus 48.62 and 55.92 ms for 1.9. Each run kept one
stable pixel hash within its version. Final process peak working sets were
174.5 and 174.6 MiB for 1.8, versus 384.6 and 384.7 MiB for 1.9. These
are backend calls, not full viewer timings: the 1.8 probe omits its Docnet
dimension lookup, and both omit WPF presentation and interaction. The 1.9
probe creates a fresh render session each call but may reuse its weakly cached
parsed document and shared image data. The large first-call gap reinforces
the first-page release gate; the warm calls do not establish visible viewer
speed or memory parity. The measured application DLL hashes are
`4AB13A69FD06146A5B963F4B5F5DBEC29761DC22940AF001700D0B0853CF05C8`
for 1.8 and
`C978281700C969853A09D39904EEE98505DC4AC68883D36FC2F49A0EE3739A13`
for 1.9. Raw output and the probe are under
`C:/Users/steve/kp-bench-render/viewer-primary-20260930`.

An eight-process repeat limited each process to one primary raster call. The
four 1.8 calls took 287.973 to 307.303 ms and peaked at 69.9 to 70.1 MiB;
the four 1.9 calls took 995.664 to 1019.805 ms and peaked at 190.0 to
190.3 MiB. This separates first-call memory from the 20-call cumulative
peaks above. Four more fresh 1.9 processes timed session opening at 207.621
to 220.142 ms and first rendering at 762.326 to 788.472 ms. The same page
hash appeared in every run. First rendering is the larger part of this
measured delay, while session setup also contributes. These process peaks
include runtime and dependency loading, and neither probe measures the full
viewer. The one-call and stage logs are in the same scratch directory.

The release script uses a different layout with loose application and engine
DLLs. Fresh installed-layout payloads from the current 1.8.72 and 1.9.0
checkouts were therefore tested in eight alternating fresh processes. The
1.8 probe included its Docnet page-dimension lookup and PDFium raster call;
the 1.9 probe invoked its primary engine render session. Each process made
one 1447 by 2048 pixel call on `42828.0001.001.pdf`. The four 1.8 total
times were 250.236, 251.097, 247.092, and 243.188 ms, versus 815.316,
801.578, 808.080, and 804.020 ms for 1.9. Median times were 248.664 and
806.050 ms. Process peak working sets were 63.7 MiB for every 1.8 run and
131.4 to 135.8 MiB for 1.9. Each version kept one stable pixel hash.
The release layout removes some of the bundled-build overhead, but the
first-page backend gap remains. WPF presentation and visible interaction
remain unmeasured. Payload DLL SHA-256 values are
`4BC802B6F48F05CA1D596BEE5E93018046A41DB2407D54C8AF2CFBAA980C25C9`
for 1.8 and
`C70693BCAF415B67C42CE77DC0AB354B63DDD2072A863C96A328E9A02700ABC5`
for 1.9. Builds and raw measurements are under
`C:/Users/steve/kp-bench-render/viewer-primary-20260930`.

Two isolated JBIG2 decoder trials were measured against the current loose
1.9 payload on the same first page. Reading and updating each arithmetic
probability state through one array reference passed 15 focused tests and
preserved the page hash, but its four-run median rose from 814.383 to
867.362 ms. Replacing the compressed-input byte-at-a-time copy with one
bulk copy also passed 15 focused tests and preserved the hash. In two
reversed-order four-run pairs, its medians were 814.357 versus 822.105 ms
for baseline and 820.717 versus 813.803 ms for baseline. The mixed result
does not establish a speed gain. Neither trial was retained. Scratch builds
and raw runs are under `C:/Users/steve/kp-bench-render/jbig2-state-trial-20260930`
and `C:/Users/steve/kp-bench-render/jbig2-input-copy-20260930`.

A direct-write trial for an opaque reduced binary image avoided the full
intermediate color plane. Across two alternating first-page pairs, the
scan's pixel hash stayed identical and median process peak fell from 133.6
to 121.6 MiB. First-page medians were effectively unchanged at 813.697 ms
for baseline and 813.657 ms for trial, with one slow outlier in each mode.
Eight alternating hidden application passes then rendered all 74 difficult
pages. Every PNG hash matched across all passes. Median render sums were
8,708 versus 8,591 ms and wall times 13,747 versus 13,621 ms for baseline
and trial, but median batch peak rose from 260.2 to 264.7 MiB. The target
scan page also slowed from a median 824 to 852.5 ms in the batch. The
local first-page memory saving did not hold across the full workload, and
the target page did not get faster, so the path was not retained. Scratch
source, payloads, logs, and PNGs are under
`C:/Users/steve/kp-bench-render/binary-direct-plane-20260930`.

An alternating fresh-process sequence of five fitted sizes on `064034.pdf`
used the current installed-layout application payloads. Four runs per version
rendered 512, 1024, 2048, 1024, and 2048 pixels in that order. The 1.8
medians were 99.400, 101.392, 115.601, 100.012, and 113.779 ms; 1.9
medians were 601.412, 152.008, 230.260, 33.166, and 45.580 ms. Each
version kept one pixel hash per size across all four runs, and all output
dimensions matched across versions. Final process peak ranges were 78.8 to
79.3 MiB for 1.8 and 120.2 to 120.4 MiB for 1.9. The first small render
is the main delay in this sequence; the repeated sizes benefit from the
retained engine session and shared decoded-image cache. These backend calls
omit WPF display work, so visible zoom behavior is still unverified. The
first 512-pixel 1.9 call was split in four more fresh processes: session
opening took 70.627 to 73.661 ms, while rendering took 585.947 to
597.690 ms. The measured delay is therefore mostly in first rendering,
not opening the document. All four stage probes retained the same pixel hash.
The probe and raw CSV are under
`C:/Users/steve/kp-bench-render/viewer-primary-20260930`.

### Reduced JPEG first-render improvement (2026-09-30)

A sampled first 512-pixel trace of `064034.pdf` placed reduced JPEG block
reconstruction and its trailing-zero search above image painting. Replacing
the eight-coefficient vector search with a scalar backward search preserved
the exact render hashes. In four alternating fresh-process pairs, the
`064034.pdf` first-render median fell from 594.004 to 374.444 ms and the
`363_Risk` median fell from 543.408 to 380.060 ms. Sampled peak ranges
were 58.9 to 59.1 versus 58.7 to 59.0 MiB on the CMYK page, and the risk
page peaked at most 69.7 versus 69.4 MiB. Each mode kept one pixel hash
per page. The retained-session CMYK zoom sequence confirmed a 602.910 to
373.894 ms first 512-pixel median. Its first 1024 and 2048-pixel calls
were variable and did not show a gain; repeated sizes were essentially
unchanged.

Three alternating measured runs per mode rendered all 74 difficult pages
with identical PNGs. Median render sums were 8,605 versus 8,309 ms and
wall times 13,660 versus 13,314 ms for baseline and changed builds, but
the ranges overlap. Median sampled peaks were 263.2 versus 263.0 MiB.
Two alternating measured runs per mode rendered all 600 shared pages with
identical PNGs. Their render-sum medians were 13,172.5 versus 13,103 ms,
wall medians 24,470 versus 24,486 ms, and peak ranges 494.2 to 494.6
versus 493.8 to 494.1 MiB. The broader work is effectively tied; the
verified gain is the cold small-page JPEG path. The full 4,163 engine and
485 application tests pass, and the Release application build succeeds
with 659 existing nullable warnings from vendored CoreJ2K. Raw traces,
payloads, run logs, and PNGs are under
`C:/Users/steve/kp-bench-render/jpeg-cold512-trial-20260930`.

Sixteen fresh-process renders each of `42828` and `064034` compared one, two,
four, and eight engine row workers in balanced order. All pixel hashes matched
within each document. On `42828`, four workers had the lowest median at
627.369 ms; one, two, and eight measured 678.252, 735.891, and 650.487 ms.
On `064034`, eight workers measured 468.684 ms versus 474.553 ms for four,
while median peak memory rose from 97.1 to 97.3 MiB. The scan regression and
small JPEG gain do not support changing the worker policy. Raw results are
under `C:/Users/steve/kp-bench-render/binary-worker-sweep-20260930`.

A precomputed masked-column path for one-bit images passed seven focused
sampler tests and preserved all 74 difficult-set PNG hashes. Three alternating
application runs per version gave median render sums of 8.515 seconds for the
baseline and 8.544 seconds for the trial, wall times of 13.619 and 13.639
seconds, and sampled peaks of 286.4 and 290.7 MiB. The isolated scan had a
small warmed gain, but the complete batch did not improve and used more
memory. The path was removed. Raw results are under
`C:/Users/steve/kp-bench-render/binary-packed-columns-20260930`.

Precomputing vertical edge weights per output row passed the same seven tests
and preserved all 74 difficult-set PNG hashes. Three alternating application
runs per version gave median render sums of 8.617 seconds for the baseline and
8.581 seconds for the trial; both median wall times were 13.7 seconds. The
small, variable difference did not justify keeping the change. Raw results
are under `C:/Users/steve/kp-bench-render/binary-row-weights-20260930`.

Four fresh-process engine probes of `42828` separated document open, first
render, and repeat work at 1447 by 2048 pixels with four row workers. Median
first render was 656.3 ms. A second render through the same renderer took
54.1 ms; rendering through a new renderer on the same document took 207.8 ms.
All five renders per process produced the same pixel hash. The runtime's
cumulative JIT compilation metric increased by a median 173 ms during the
first render, versus 1 ms during the second. Compilation contributes to the
cold delay but does not account for the full first-render cost. The probe does
not measure WPF presentation or establish a 1.8 comparison. Source, DLL hashes,
and raw measurements are under
`C:/Users/steve/kp-bench-render/cold-jit-20260930`.

Temporary timing around the actual first-page JBIG2 image path measured four
fresh processes. Median time inside decoded-image retrieval was 299.9 ms on
the first render and 0.7 ms on the second render through the same renderer.
Time from retrieval through image painting was 227.7 and 38.5 ms. A new
renderer on the same document spent 144.1 ms retrieving the image and 34.1 ms
through painting. Every render retained the same pixel hash. A separate
`NoOptimization` hint on `PaintImage` gave first-render medians of 681.1 ms
for baseline and 671.8 ms for trial in four alternating runs each; second
renders were 49.9 and 50.1 ms. The timing ranges overlapped, so the hint and
temporary probes were removed. Raw data and builds are under
`C:/Users/steve/kp-bench-render/image-phase-timing-20260930` and
`C:/Users/steve/kp-bench-render/paint-jit-trial-20260930`.

An `AggressiveOptimization` hint on the JBIG2 arithmetic decoder shortened
isolated first-image decode from a 332.7 ms median to 302.3 ms in four
alternating runs per version, but raised repeat decode from 149.1 to 156.8 ms.
In the direct first-page path, first-render medians were 626.6 ms for baseline
and 649.0 ms for trial. All decoded-image and rendered-page hashes matched.
The hint was removed because the page did not improve. Raw runs are under
`C:/Users/steve/kp-bench-render/jbig2-arith-jit-20260930`.

Skipping redundant byte-input seeks in the JBIG2 arithmetic decoder kept all
74 difficult-set PNG hashes across four alternating runs per build. Median
render time fell from 9,425.5 to 9,233.5 milliseconds and wall time from
14,910.5 to 14,579 milliseconds, but median peak working set rose from
283.32 to 289.73 MiB. The trial was removed because the memory regression
does not meet the release gate. Raw runs and payloads are under
`C:/Users/steve/kp-bench-render/arith-bytein-20260930`.

A loose-payload recheck of that exact byte-input change used three alternating
measured runs per build at each of 512 and 2048 pixels. All 74 PNG hashes
matched across every run at both sizes, and the 15 focused JBIG2 tests passed.
At 512 pixels, baseline and trial median render sums were 6,330 and 6,250 ms;
median peaks were about 189.3 and 188.3 MiB. At 2048 pixels, render sums
were 9,250 and 9,115 ms, but median peaks rose from about 264.0 to 271.0 MiB.
The second measurement again shows a roughly 7 MiB memory cost for a small,
variable speed gain, so the source was restored. Raw runs and PNGs are under
`C:/Users/steve/kp-bench-render/arith-bytein-recheck-20260930`.

A same-payload `DOTNET_TieredCompilation=0` trial used alternating hidden
application runs after warmup. On the 74-page difficult set, baseline and
disabled-tiering medians were 10.545 and 8.720 seconds of rendering, 16.248
and 14.643 seconds wall time, and 296.5 and 265.0 MiB sampled peak. This
session's unchanged 1.9 baseline ran slower than the comparison above, so
these values support only a same-session comparison. On the 600-file shared
set, disabled tiering increased median rendering from 16.390 to 17.506
seconds and wall time from 29.431 to 31.474 seconds, while sampled peak fell
from 525.5 to 505.7 MiB. All 74 difficult and 600 shared output dimensions
and PNG hashes matched across all six runs in each set. The shared-set
regression rules out a process-wide setting change. Raw runs, scripts, and
analyses are under `C:/Users/steve/kp-bench-render/tiered-off-20260930`.

The 1.8 and 1.9 primary viewer paths use the same resolution rule and the
same `WriteableBitmap` construction, pixel copy, and freeze sequence after
rendering. A separate hidden WPF probe timed that sequence with synthetic
BGRA pixels in four fresh processes. Its first bitmap creation took 117 to
128 ms at 724 by 1024 pixels; after warmup, the median was 7.3 ms at 1447 by
2048 pixels and 15.4 ms at 2048 by 2894 pixels. This isolates bitmap handoff
cost, not page layout, screen presentation, or an end-to-end viewer comparison.
The measured multi-hundred-millisecond first-page gap still needs engine and
visible interaction work. Probe source and raw runs are under
`C:/Users/steve/kp-bench-render/viewer-bitmap-boundary-20260930`.

Moving the JPEG 2000 decoder's state clear below its empty-block return
preserved the balloon page hash in 120 renders. Four fresh, hidden engine
processes alternated baseline and trial builds, each opening a new document
for 30 renders. First-render times were 545.75 and 530.98 ms for baseline
versus 542.76 and 540.19 ms for trial. The last-20 medians were 158.41 and
160.69 ms versus 158.59 and 160.66 ms. The trial did not establish a speed
gain, so the source change was removed. Probe source and raw runs are under
`C:/Users/steve/kp-bench-render/j2k-empty-block-20260930`.

A fresh sampled-thread trace of the current headless difficult batch completed
all 74 pages. Every PNG hash matched the retained 1.9 batch. Among 4,867
managed CPU samples, 3,466 include `RenderUncached`, 1,574 include
`TryRenderImage`, 544 include `ImageSampleConverter.ConvertArea`, and 343
include `PaintCoverage`. These inclusive counts overlap and do not measure
wall time; the trace also includes startup, encoding, and runtime work.
Image decoding and painting now lead the sampled renderer work, so the older
coverage-painting profile is no longer a sound basis for the next change.
The trace, output log, and sample-count script are in
`C:/Users/steve/kp-bench-render` with the `current-cpu-20260930` prefix.

Fresh whole-batch sampled-thread traces of the current Release payload at
2048 and 512 pixels each completed all 74 difficult pages. Every output PNG
matched the earlier 1.9 batch at the same size. At 2048 pixels, inclusive
sampled CPU time was about 1.71 seconds in general image area conversion,
1.16 seconds in one-bit area sampling, and 0.65 seconds in JPEG 2000 decode.
At 512 pixels it was about 1.42 seconds in outline text, 0.72 seconds in JPEG
decode, 0.54 seconds in general area conversion, and 0.46 seconds in JBIG2
decode. These are overlapping method times from separate instrumented runs,
not paired wall-time or 1.8 comparisons. They show that output-size-independent
text and image decoding still matter at 512 pixels; another shading-only
microchange is not supported by the whole-batch profile. The traces, batch
logs, PNGs, and analysis are under
`C:/Users/steve/kp-bench-render/difficult-cpu-trace-20260930` and
`C:/Users/steve/kp-bench-render/difficult-cpu-trace-512-20260930`.

Follow-up 512-pixel traces narrowed two different costs. The poster page
`mipeng_poster_w24.pdf` spent about 314 ms of inclusive sampled CPU time
in content parsing, with repeated Form rendering above it; its prior parser,
Form-cache, and soft-mask trials already cover this path. On the first three
pages of `Ghent_PDF-Output-Test-V50_ALL_X4.pdf`, the 1.9 trace attributed
about 374 ms to outline text, including 221 ms in font loading and 154 ms
in ToUnicode parsing. The same instrumented run recorded page render times
of 521, 217, and 516 ms for 1.9 versus 85, 98, and 164 ms for 1.8.
The 1.8 sampled profiler does not expose PDFium's native internals, so
those samples cannot identify its corresponding font cost. The earlier
ToUnicode replacement-string change reduced allocations but did not establish
a meaningful speed gain. Trace artifacts are under
`C:/Users/steve/kp-bench-render/mipeng-text-trace-20260930` and
`C:/Users/steve/kp-bench-render/ghent-text-trace-20260930`.

A fresh first-page trace of `42828.0001.001.pdf` again showed active work
in JBIG2 generic-region decoding and exact one-bit image averaging. A
scratch trial prepared the two decoded display colors once per image instead
of checking them for every averaged pixel. Four alternating fresh-process
pairs kept one page hash, but first-render medians were 725.134 ms for the
current build and 737.724 ms for the trial, with overlapping ranges and no
consistent peak-memory reduction. The trial was not retained. Its source,
payload, and raw timings are under
`C:/Users/steve/kp-bench-render/binary-colors-once-20260930`; the trace is
under `C:/Users/steve/kp-bench-render/viewer-primary-20260930`.

A headless scan-page zoom sequence compared installed-layout 1.8.72 and a
pre-JPEG 1.9.0 payload in four alternating fresh processes per version. Each
process rendered `42828.0001.001.pdf` at 512, 1024, 2048, 1024, and 2048
pixels after opening the document. Median render times for 1.8 were 174.702,
187.796, 221.166, 186.176, and 221.860 ms; for 1.9 they were 645.331,
247.726, 218.255, 266.169, and 42.346 ms. Output dimensions matched at
each size, and each version produced stable hashes across its four runs.
The final process peak was 85.0 to 85.1 MiB for 1.8 versus 160.0 to 160.2
MiB for 1.9. These timings cover backend raster calls after document opening,
not visible WPF zoom or page-display latency. The raw sequence is under
`C:/Users/steve/kp-bench-render/viewer-primary-20260930`.

The same sequence was repeated with a freshly published loose 1.9.0 payload
containing the retained reduced-JPEG change. Four alternating fresh processes
per version gave 1.8 medians of 169.543, 183.135, 219.246, 185.721, and
222.675 ms; current 1.9 medians were 674.110, 235.866, 210.015, 231.981,
and 40.599 ms. The first scan render remains about four times slower in 1.9.
Later 1.9 steps were variable, including 43.557 to 220.583 ms at the first
2048-pixel step. Final process peaks were about 85.0 MiB for 1.8 and 160.0
MiB for 1.9. Each size kept one hash per version across all four runs; the
1.9 hashes also matched the earlier payload. This is backend raster timing,
not visible zoom or page-display latency. Raw results are in
`C:/Users/steve/kp-bench-render/viewer-primary-20260930/42828-zoom-afterjpeg.csv`.

The scan page's raw JBIG2 image stream contains an arithmetic-coded symbol
dictionary with 1,338 new symbols and template 0, followed by a text region.
The engine already has a specialized template-0 generic-region decode loop,
so adding fast loops for other templates would not address this page. The
next renderer comparison should focus on symbol-dictionary decoding and
one-bit page painting. The extracted stream is in the same scratch directory
as `42828-image2354.jb2`.

The viewer's primary session renders into caller-owned pixels, so the engine's
rendered-page cache does not retain a second page bitmap. A headless memory
probe of the scan page found about 1.1 MiB in the reusable byte-buffer pool
after a 512-pixel render and 16.1 MiB after a 2048-pixel render. After
collection, managed heaps were 41.9 and 57.7 MB; the difference closely
matches the pool. Releasing idle buffers would lower retention, but it would
not remove their allocation from the peak. The probe is under
`C:/Users/steve/kp-bench-render/scan-memory-20260930`.

A fresh-process 512-pixel sample trace of the current loose payload confirms
the cold path spends substantial sampled time in JBIG2 generic-region line
decoding and arithmetic decoding. One-bit area sampling also appears, but at
a smaller share than on the 2048-pixel trace. The trace includes startup and
assembly loading, so its percentages are not render-only timings. It is at
`C:/Users/steve/kp-bench-render/viewer-primary-20260930/42828-first512-current.nettrace`.
Counting its 253 sampled CPU stacks finds 85 through symbol-dictionary
decoding and 28 through one-bit area sampling. These inclusive counts overlap
and reinforce symbol decoding as the next cold-render target; they do not
measure exclusive time or predict the size of a possible improvement.

A scratch trial combined the arithmetic decoder's four state arrays into one
table. The decoded scan image and 2048-pixel page hashes stayed unchanged, but
four alternating fresh-process runs per build raised median first-image decode
from 353 to 1,346 ms and the following page render from 643 to 2,098 ms. Process
working set stayed near 135 to 137 MiB. The table was rejected; scratch
source and raw runs are under
`C:/Users/steve/kp-bench-render/jbig2-state-table-20260930`.

A PDFium-inspired scratch trial updated the arithmetic decoder's packed
context byte directly inside the template-0 symbol loop. All 15 focused
JBIG2 tests and the scan pixel hash passed. Four alternating fresh-process
512-pixel renders had medians of 634.848 ms for the current code and
641.668 ms for the trial; two trial peaks were about 4 MiB higher. The
trial was not retained. Its source and raw results are under
`C:/Users/steve/kp-bench-render/arith-packed-20260930`.

A scratch JBIG2 stream path read single bytes directly from the source buffer
instead of through layered stream buffers. All 15 focused JBIG2 tests and the
scan hashes passed. Four alternating fresh-process zoom runs per build gave
first 512-pixel medians of 640.36 ms before and 644.01 ms after the change;
final process peaks were about 160.0 and 164.2 MiB. Direct bulk reads also
failed two focused decoder tests. The stream path was not retained. Source and
raw measurements are under `C:/Users/steve/kp-bench-render/jbig2-direct-stream-20260930`.

A source-size-aware row-worker trial parallelized one-bit reduction for large
source images even when the output page was small. All 22 focused tests passed,
and all five scan zoom sizes kept their baseline hashes. Four alternating
fresh-process runs per build measured the first 512-pixel render at 641.22 ms
for baseline and 675.30 ms for trial; final process peaks were 160.3 and
160.4 MiB. Later zoom steps varied in both directions. The trial was not
retained. Source and raw timings are under
`C:/Users/steve/kp-bench-render/binary-source-parallel-20260930`.

A one-byte fast path for counting interior bits in reduced binary images
preserved scan pixels and improved repeated direct-engine renders by a few
milliseconds. Eight alternating hidden application runs then rendered all 74
difficult pages with identical PNG hashes. Their baseline render sums were
9.989, 9.616, 9.807, and 9.011 seconds; trial sums were 9.337, 9.581, 9.493,
and 9.570 seconds. The targeted `42828` first page took 813 to 846 ms on
baseline and 842 to 911 ms on trial. The application result does not support
the shortcut, so no source change was retained. Scratch builds and raw runs
are under `C:/Users/steve/kp-bench-render/binary-count-trial-20260930`.

### Current difficult-set refresh (2026-09-29)

The current 1.9 Release build and local 1.8.72 Release build rendered the same
40 difficult PDFs, 74 pages in total, at 2048 pixels and up to three pages per
file. The 1.9 batch used one file worker to match the serial 1.8 run. After one
warmup per version, six hidden, low-priority runs followed the order
1.8/1.9/1.9/1.8/1.8/1.9. All runs succeeded on all 74 pages. Render sums were
4.925, 4.969, and 4.933 seconds for 1.8 versus 9.038, 9.303, and 9.021
seconds for 1.9. Wall times were 10.161, 10.280, and 10.274 seconds versus
14.295, 14.670, and 14.315 seconds. Sampled process peaks were 231.9,
260.0, and 260.3 MiB versus 296.6, 289.4, and 285.3 MiB. Both versions
reproduced all 74 of their own PNG hashes across three runs, and all 74 output
dimensions matched across versions. Different cross-version pixels remain a
separate fidelity gate.

The largest median page gaps were 676 ms on `42828.0001.001.pdf`, 375 ms on
the JPEG 2000 balloon, 333 ms on `064034.pdf`, 309 ms on `363_Risk`, and
285 ms on `GWG061_Shading_x1a.pdf`. This ranks the current bottlenecks but
does not isolate their causes. The 1.8 and 1.9 application DLL SHA-256 values
are `4AB13A69FD06146A5B963F4B5F5DBEC29761DC22940AF001700D0B0853CF05C8`
and `C4A2BFCC001F1AAA11D5D2729F91CE601B7222A7F5F745CDE6BB7ADC70F3EE9B`.
Raw logs, PNGs, runner, and analysis are under
`C:/Users/steve/kp-bench-render/difficult-refresh-after-ports-20260929`.
This headless comparison does not measure WPF presentation or interaction.

After the parallel CMYK alpha fix, a fresh hidden, low-priority 1.8/1.9/1.9/1.8
check followed one warmup per version on the same 74 pages. The two 1.8 render
sums were 4.614 and 4.658 seconds versus 8.853 and 9.251 seconds for 1.9;
wall times were 10.026 and 9.794 seconds versus 13.971 and 14.610 seconds.
Sampled peaks were 259.8 and 231.8 MiB versus 289.7 and 285.3 MiB. Every
page succeeded and the two current 1.9 PNG sets match byte for byte. This
confirms the large speed gap persists after the fidelity fix; two measured
runs do not replace the earlier three-run comparison. Raw logs and PNGs are
under `C:/Users/steve/kp-bench-render/post-alpha-parity-20260929`.

### Current cold and repeated page costs (2026-09-29)

Two fresh-process runs per version used eight byte-identical copies of each
page at 2048 pixels, one file worker, and the order 1.8/1.9/1.9/1.8. All
renders succeeded, output dimensions matched across versions, and PNG hashes
were stable within each version. On the JBIG2 scan, first renders took 243
to 245 ms in 1.8 and 713 to 786 ms in 1.9. The median of the last six copies
was 229 to 230 ms in 1.8 and 208.5 to 221 ms in 1.9. On the JPEG 2000
balloon, first renders took 435 to 436 ms in 1.8 and 728 to 765 ms in 1.9;
last-six medians were 435.5 to 441 ms and 369.5 to 378.5 ms. The balloon's
sampled peaks were 321.5 to 322.2 MiB in 1.8 and 223.6 to 225.9 MiB in
1.9. These two pages have a large first-render cost in 1.9 at this size,
but their repeated render cost is lower than 1.8 in these focused runs.

The CMYK JPEG page `064034.pdf` remains slower after warmup: first renders
took 124 to 126 ms in 1.8 and 496 to 505 ms in 1.9; last-six medians were
116 to 118 ms and 179.5 to 186 ms. Its sampled peaks were 143.4 to
145.4 MiB and 184.6 to 184.8 MiB. Raw measurements and PNGs are under
`C:/Users/steve/kp-bench-render/jbig2-cold-current-20260929`,
`C:/Users/steve/kp-bench-render/balloon-cold-current-20260929`, and
`C:/Users/steve/kp-bench-render/cmyk-cold-current-20260929`. These isolated
pages do not explain the entire 74-page difficult-set gap or measure visible
interaction.

The same alternating eight-copy method also separated two other page gaps.
For `363_Risk`, first renders were 140 to 144 ms in 1.8 and 672 to 674 ms
in 1.9; last-six medians were 134 ms and 206 to 210.5 ms. For
`GWG061_Shading_x1a.pdf`, first renders were 33 ms and 613 to 627 ms;
last-six medians were 27.5 to 28 ms and 105.5 to 106 ms. Sampled peaks
were 152.1 to 158.7 MiB versus 196.1 to 196.2 MiB for the risk page,
and 110.9 to 111.1 MiB versus 164.1 to 165.3 MiB for shading. All 64
renders succeeded, dimensions matched across versions, and each version
kept one PNG hash per page. The first and repeated costs both need work.
Raw results are under
`C:/Users/steve/kp-bench-render/risk-shading-cold-current-20260929`.

An unchecked-table-access trial for DeviceCMYK preserved one PNG hash across
64 focused CMYK renders and passed 35 focused tests. In three alternating
pairs, baseline last-six medians were 175 to 180 ms and the trial's were
188.5 to 197 ms, without a memory reduction. The trial was removed; raw
runs are under `C:/Users/steve/kp-bench-render/cmyk-table-ref-trial-20260929`.

Inlining four calculator stack helpers passed 32 focused tests and preserved
one PNG hash across 64 shading renders. The three alternating measured pairs
gave baseline last-six medians of 107.5, 101, and 102.5 ms versus 105, 105,
and 101.5 ms for the trial. The mixed result did not justify retaining the
change. Raw runs are under
`C:/Users/steve/kp-bench-render/calculator-inline-trial-20260929`.

A one-dimensional cosine lookup in reduced JPEG block output passed 135
focused JPEG tests and preserved one PNG hash across 64 renders of `363_Risk`.
Three alternating pairs gave baseline last-six medians of 209.5, 203.5,
and 216 ms versus 204.5, 204, and 215 ms for the trial. The differences
were not repeatable enough to retain it. The restored Release DLL matches
the baseline hash. Raw runs are under
`C:/Users/steve/kp-bench-render/jpeg-vertical-table-trial-20260929`.

A 64-pass sampled-thread-time trace of the current 1.9 engine on `064034.pdf`
at 2048 pixels reproduced one pixel hash with no render diagnostics. The
largest application work in this trace appears in both JPEG decoding and the
image-paint row worker. `PdfJpegDecoder.DecodeScan` accounts for 9.46% of all
stack samples inclusively, while one `PaintImage` row worker accounts for
9.52% exclusively. Most remaining samples are idle thread-pool waits, so
these percentages are not a CPU-time split or a comparison with 1.8. This
points to both decoding and painting for further work rather than another
isolated color-table edit. The trace and scratch probe are under
`C:/Users/steve/kp-bench-render/cmyk-profile-20260929`.

An opaque-plane paint loop removed per-pixel alpha and mask checks for the
plain CMYK image path. Eight alternating baseline/trial runs rendered eight
copies of `064034.pdf` each at 2048 pixels. All 64 outputs shared one pixel
hash, and sampled peaks stayed between 182.1 and 185.2 MiB. The last-six
medians were 188.2 to 195.7 ms for baseline and 186.5 to 191.6 ms for the
trial. The small, inconsistent difference did not justify retaining the
extra loop. The tracked source was restored; raw runs are under
`C:/Users/steve/kp-bench-render/cmyk-opaque-plane-trial-20260929`.

Caching axis-aligned image x samples preserved hashes on the CMYK, JBIG2,
JPEG 2000, and shading probes. Its small CMYK timing difference did not repeat
on the other pages, so the trial was removed. During that check, repeated
four-worker renders of `363_Risk` exposed a pre-existing race: CMYK image rows
sometimes lost alpha at worker boundaries. A synthetic transparent CMYK group
reproduced 20 different hashes in 20 baseline parallel renders. Initializing
the shared alpha plane before publishing it fixes that case; 20 fixed renders
match the serial output. At 2048 pixels, eight renders each at two, three,
four, and eight workers also match the serial `363_Risk` hash.

The fixed engine then rendered all 74 difficult pages with one worker and in
two separate four-worker passes. Every hash and dimension matched the old
engine's serial output; the same two pages reported diagnostics in each pass.
An alternating eight-copy CMYK timing check found overlapping warmed medians:
186.5 to 195.5 ms before the fix and 188.5 to 198.1 ms after it. The sampled
CMYK peaks overlapped at 181.8 to 184.3 and 182.6 to 184.2 MiB. One serial
corpus pass peaked at 486.6 MiB before the fix and 516.4 MiB after it; two
parallel fixed passes peaked at 507.2 and 519.5 MiB. These process peaks need
repeat measurement before making a corpus memory claim. This correctness fix
does not close the speed or memory parity gates. Raw checks are under
`C:/Users/steve/kp-bench-render/risk-repeat-20260929` and
`C:/Users/steve/kp-bench-render/ink-alpha-fix-20260929`.

A 64-render thread sample on `363_Risk` attributed 18.34 percent exclusive
thread time to its axial shading routine, alongside JPEG decoding and CMYK
conversion. Large axial paints now prepare one shared color table before
splitting independent rows; profile-backed paints retain lazy serial color
evaluation. Two alternating eight-copy pairs at 2048 pixels reduced the
`363_Risk` last-six medians from 220.3 to 227.3 ms to 149.5 to 162.5 ms.
All 32 focused outputs matched the prior hash, and sampled peaks stayed near
338 to 339 MiB. A worker-local table trial also improved the page but had a
607 MiB corpus peak in one run, so it was replaced by the shared table.

Three alternating difficult-set pairs with the final shared-table path gave
median engine render sums of 9.419 seconds before and 9.497 seconds after,
and median wall times of 10.725 and 10.802 seconds. Sampled peaks varied from
468.0 to 536.4 MiB before and 512.7 to 519.0 MiB after. All 444 page hashes
and diagnostics matched. Separate alternating checks found CLLASS and Ghent
effectively tied; their earlier apparent slowdowns were run variability.
The Release application rendered all 74 pages and reproduced every prior PNG
hash, with an 8.563-second render sum, 14.210-second wall time, and a 295.6 MiB
sampled peak in one headless pass. The focused shading gain does not close the
whole-pass speed, memory, visual, or interactive release gates. Raw profiles,
trials, and comparisons are under
`C:/Users/steve/kp-bench-render/axial-parallel-trial-20260929`.

Current paired evidence is archived locally under
`C:/Users/steve/kp-bench-render/review-20260909/stencil-area-paired*`.
The measured engine payload includes rendering changes through `249072b`,
including installed-font aliases, pattern text and transparency, zero-length
dash caps, and reduced stencil coverage. Its engine SHA-256 is
`BE2B3A818A9FE932FA491D911DFC6D5EEA04327F8F1D5EEDE8802B63011F7132`.
All 16 passes completed successfully. Run zero is warmup; runs one through three
alternate application order. All 5,392 images match their respective current
engine and PDFium baselines. The measurements, ranges, and page rankings are in
`stencil-area-paired-analysis.json`; the individual runs are in
`stencil-area-paired-results.csv`. Both applications use the retained
framework-dependent Windows payload layout. No installer or release was created.

### Serial conformance comparison (2026-09-29)

Optimizing the first compilation of the DeviceCMYK display converter reduced
the first 2048-pixel render of `064034.pdf` by roughly 180 to 200 ms in three
alternating eight-copy pairs. Later copies had similar timing. On the full
74-page difficult set, three alternating current-1.9/changed-1.9 pairs after
warmup measured median render sums of 10.901/10.142 seconds and wall times of
16.778/15.728 seconds. Peak working sets were at most 267.6/263.2 MiB.
All 222 compared PNGs matched byte for byte and every page succeeded.

On the 649-file conformance corpus at 1024 pixels, three alternating pairs
rendered the same 614 pages and skipped the same 35 files per run. Baseline
and changed medians were 15.018/14.995 seconds of render time and
27.316/27.426 seconds wall time. Peak working sets were at most 494.3/494.6
MiB. All 1,842 compared PNGs matched byte for byte. This is effectively tied
on the broader corpus, not a whole-corpus speed claim. A separate comparison
against 1.8.72 still measured the changed 1.9 difficult render median at
10.032 versus 5.408 seconds. The full 4,160 engine and 484 application tests
pass. Raw runs and retained payload are under
`C:/Users/steve/kp-bench-render/cmyk-aggressive-trial`. Visible interaction
and broader performance parity remain open.

Applying the same first-compilation optimization to the JBIG2 generic-region
fast decoder did not help the repeated scan workload. Three alternating
eight-copy pairs gave first-render baseline/trial times of 805/875, 801/831,
and 769/794 ms, while warmed medians varied in both directions. The trial
was removed. Logs and its payload are under
`C:/Users/steve/kp-bench-render/jbig2-aggressive-trial`.

The latest local 1.9 candidate and 1.8.72 were also compared on the 40-file,
74-page difficult set at 2048 pixels with one file worker. After warmup,
three alternating measured runs gave 1.9 median render and wall times of
12.948 and 19.769 seconds versus 1.8 medians of 6.230 and 12.671 seconds.
The 1.9 render range was 10.817 to 13.419 seconds, so its gap is larger than
run variability. Both versions completed all 74 pages without reported
failures. Peak working sets were 266.4 and 252.1 MiB. The 1.9 output hashes
matched across two measured runs. Large page gaps include `42828.0001.001.pdf`,
`balloon_a1b_jp2k.pdf`, `064034.pdf`, and Altona technical. Raw logs and the
headless runner are in `C:/Users/steve/kp-bench-render/current-parity-20260929`.
This check does not establish visual or interactive parity.

Eight separate hard links to each of two difficult files isolate first-use
cost from repeated document sessions in one process. For the JBIG2 scan
`42828.0001.001.pdf`, 1.9 took 788 ms on the first copy and 232 to 275 ms
on the next seven; 1.8 took 244 to 283 ms across all eight. For CMYK JPEG
`064034.pdf`, 1.9 took 723 ms first and a 196 ms median on the last six,
versus 131 ms first and a 126 ms last-six median for 1.8. Each build produced
one stable PNG hash per file across its eight copies. This narrows the warm
gap to the CMYK path while keeping first-use cost open for both files. Raw
results are in `C:/Users/steve/kp-bench-render/session-cost-20260929`.

An explicit unrolling of `PdfDeviceCmyk.ToRgb` passed 168 focused color tests
and preserved all 24 compared CMYK page hashes, but three alternating
eight-copy pairs did not show a repeatable speed gain. Baseline/trial last-six
medians were 192.5/195, 187.5/199, and 194.5/189 ms. The experiment was
removed. Its payload and logs are in
`C:/Users/steve/kp-bench-render/cmyk-inline-trial`.

Changing the large-page row-worker cap from four to two slowed both focused
image pages in two alternating pairs: CMYK last-six medians rose from
197.5/201.5 to 230/220.5 ms, and the JBIG2 scan rose from 235.5/227.5 to
261.5/252.5 ms. The shading page was essentially unchanged, and all 48
candidate PNGs matched the baseline. Raising the cap to six gave inconsistent
CMYK results and slower or more variable scan and shading results in the same
focused checks. Neither cap change was retained. Raw runs are under
`C:/Users/steve/kp-bench-render/row-cap-two-trial` and
`C:/Users/steve/kp-bench-render/row-cap-six-trial`.

The benchmark runner now forces candidate parallelism to one when requested and
compares the same per-file render timing field in both logs. On the 649-file
conformance corpus at 1024 pixels, three alternating measured runs after warmup
gave median wall times of 21.601 seconds for 1.8 and 24.900 seconds for 1.9.
The ranges were 21.578 to 21.653 and 24.707 to 24.929 seconds. Across 600
pages both versions rendered successfully, sums of per-file median render
times were 11,303 and 13,305 milliseconds. Version 1.8 rendered 600 files,
skipped 41, and failed eight; version 1.9 rendered 614, skipped 35, and failed
none. These results establish a serial performance gap for this corpus, while
the different successful file counts limit whole-pass comparison. The raw
results are in `C:/Users/steve/kp-bench-render/serial-1.9-vs-1.8-20260929`.
This run did not retain pixels and does not establish fidelity or memory parity.

The earlier 1.9 payload, including the JPEG block change, was then
compared with the same 1.8 build and settings. Three alternating measured runs
after warmup gave median wall times of 22.102 versus 25.453 seconds, with
ranges of 21.831 to 22.357 and 25.383 to 25.491 seconds. On the 600 shared
successful pages, sums of per-file median render times were 11,538 and 13,569
milliseconds, a 1.176 ratio. Version 1.8 still rendered 600 files and failed
eight; 1.9 rendered 614 and failed none. The payload's `KillerPDF.dll` SHA-256
is `CC5E62281F368A061C49A6C7214A831C56FB823F099E08ABF0A47AD80080F0F1`.
Raw results are in `C:/Users/steve/kp-bench-render/serial-post-jpeg-20260929`.
The focused JPEG gain has not closed the whole-corpus speed gap.

### Current shared comparison after large-image reuse (2026-09-29)

The committed image-cache change was built as a loose application payload and
compared headlessly with the retained 1.8 application on the current 600-file
shared input at 1024 pixels, one file worker, and one page per file. Three
alternating runs gave 1.8 wall times of 24.231, 24.173, and 24.068 seconds
versus 27.882, 27.314, and 27.171 seconds for 1.9. Median sampled peak
working sets were 620.0 and 502.2 MiB. Both versions rendered all 600 pages
successfully. The sums of per-file median render times were 12,951 and 14,797
milliseconds, a 1.143 ratio. The three Altona files contributed 1,337
milliseconds of the 1,846 millisecond net difference. These timings do not
establish cross-version pixel equivalence or visible interaction parity.

The earlier 649-file conformance input included 49 files absent
from this current shared folder, so its whole-pass totals are not directly
comparable. The current payload's `KillerPDF.App.dll` SHA-256 is
`5CB2B75DD284D3C5D815DE362D6C6B15FBB9F663FCEFB09AC54FDB249743B13D`
and its engine SHA-256 is
`1D68B610349DABF46AF087409F5692EBDBBDD63CE6B2B938803A47A0D2681954`.
Raw logs and per-file comparison are under
`C:/Users/steve/kp-bench-render/post-image-cache-20260929`.

A current one-worker engine probe of the largest Altona page kept one pixel
hash across 12 renders. Its first render took 1,992.845 milliseconds; the
last six had a 1,156.174 millisecond median. A sampled-thread-time trace
attributes 31.56 percent exclusive time to area conversion and 13.52 percent
to ICC lookup transformation, versus 0.94 percent to ink pixel compositing.
The probe and trace are under `C:/Users/steve/kp-bench-render/altona-pages-20260929`.

At 1024 by 1024 pixels the application selects four render workers. Two
fresh four-worker probes of the same page took 1,367.071 and 1,460.967
milliseconds on the first render, then 721.471 and 697.574 milliseconds
at the median of the last six. All 24 renders kept the same pixel hash.
The corresponding last-six one-worker medians were 1,156.174 and 1,200.998
milliseconds. Four workers therefore improve warmed page latency, while
the cold render remains the main gap for a one-pass batch. These engine
probes do not establish visible interaction speed or a memory bound.

An area-sampling trial moved edge-weight multiplication outside each source
sample loop. In two reversed-order four-worker pairs, baseline/trial warmed
medians were 702.890/693.754 and 681.920/700.532 milliseconds. All 48
pixel hashes matched and allocation stayed near 156 MB per render. The pairs
disagree on speed, so the trial was removed. Its binaries and raw timings are
under `C:/Users/steve/kp-bench-render/altona-weight-20260929`.

A temporary counter build then measured 57,781,871 color-cache hits and
1,755,554 misses on one four-worker render of the same page, a 97.05 percent
hit rate. Of the misses, 1,346,118 replaced an occupied slot. The pixel hash
matched the production engine. The counters were removed and the engine was
rebuilt. The probe is under
`C:/Users/steve/kp-bench-render/altona-cache-metrics-20260929`.

A headless output-profile preselection check removed a discarded first pass
on the largest Altona page. Four alternating fresh-process probes measured
normal renders at 1,309 to 1,339 milliseconds and preselected renders at
894 to 1,020 milliseconds, all with the same pixel hash. In two alternating
600-file shared pairs, the current build took 27.394 and 26.952 seconds of
application time versus 28.231 and 27.031 seconds for the prior build.
All 600 current PNG hashes matched the prior build, and sampled peaks were
492.6 to 494.0 MiB versus 491.8 to 492.6 MiB. Most of the small whole-pass
gain came from that Altona page. These paired runs do not establish parity
with 1.8 or visible interaction speed. Raw logs are under
`C:/Users/steve/kp-bench-render/altona-preselect-20260929`.

Prestarting four thread-pool workers before a fresh render also did not help.
Four alternating baseline/warmed pairs gave baseline first-render times of
1,434.615, 1,432.134, 1,445.607, and 1,432.084 milliseconds versus
1,503.582, 1,505.196, 1,467.895, and 1,484.795 with worker prestart.
Every hash matched. A separate cold allocation trace sampled large byte arrays
from Flate output and its scratch pool; it does not isolate the cause of the
first-render latency. The decoder uses pooled one-megabyte blocks and then
copies into an exact-size array. Its current cold trace ranks Flate decode
well below area conversion, so removing that copy has no measured speed case
yet. Raw results are under
`C:/Users/steve/kp-bench-render/altona-thread-warm-20260929`.

ReadyToRun was tested against the loose release payload. On one fresh Altona
technical-page process, normal rendering took 708 to 766 milliseconds versus
382 to 404 milliseconds with ReadyToRun, with identical PNG hashes. Across
two alternating 600-file pairs, ReadyToRun reduced summed rendering by about
522 milliseconds but added about 706 milliseconds to document opening, leaving
average wall time 129 milliseconds slower. Sampled peaks were 489.5 to 491.8
MiB versus 492.3 to 492.4 MiB. Composite ReadyToRun also rendered the 74-page
difficult set more slowly at 10.184 versus 9.510 seconds and raised its sampled
peak from 260.0 to 263.9 MiB. The loose payload grew by about 14.5 MB. Neither
mode was adopted. Raw results are under
`C:/Users/steve/kp-bench-render/r2r-payload-20260929`.

An engine-only ReadyToRun payload used the current source and kept every other
published file byte-identical. Three alternating Altona pairs cut first-page
times from 743 to 756 milliseconds to 373 to 382 milliseconds, but raised the
median 20-page render total from 4,107 to 4,809 milliseconds and wall time
from 4,933 to 5,643 milliseconds. Peak working set was 148.5 versus 150.9
MiB. On the 649-file conformance corpus, median render time was 14,603 versus
14,308 milliseconds, but wall time rose from 26,783 to 27,395 milliseconds.
Peak working set was 494.7 versus 493.8 MiB. On the 74-page difficult set,
median render and wall times rose from 9,418/14,708 to 9,715/14,981
milliseconds, while maximum peak working set fell from 268.5 to 267.2 MiB.
All 60 Altona, 1,842 conformance, and 222 difficult paired PNGs matched;
conformance had the same 614 successes and 35 skips per run. The engine DLL
grew by about 8.5 MB. The cold improvement does not justify the repeated and
whole-pass costs, so the normal payload remains selected. Evidence is under
`C:/Users/steve/kp-bench-render/engine-only-r2r-20260929`.

A September 30 codec-only trial replaced just `CoreJ2K.dll` in the loose
payload with a win-x64 ReadyToRun build. Four alternating fresh-process
512-pixel renders of `balloon_a1b_jp2k.pdf` measured a 378.8 ms baseline
median versus 308.4 ms with the codec replacement. At 2048 pixels the
medians were 854.6 and 794.7 ms. The 20-file balloon batch had median
render sums of 1,969 and 1,823.5 ms; the 74-page difficult and 600-file
shared batches varied enough that neither showed a reliable broad gain.
The 2048-pixel difficult batch was effectively tied. Sampled peak memory
had no consistent increase. All 20 balloon, 74 difficult, and 600 shared
output PNGs matched their respective baselines in every paired run. The
standalone codec publish also passed a separate 20-file app run with matching
page output. Scratch portable and installer packages contain the same tested
codec DLL and pass their file inventories, payload startup checks, and 20-page
headless renders with matching PNG hashes. The installer also passes isolated
installation. The public portable launcher, exact signed installer, and visible
viewer still need verification. The remaining speed gap against 1.8 is open.
Raw runs and payloads are under
`C:/Users/steve/kp-bench-render/corej2k-r2r-only-20260930` and
`C:/Users/steve/kp-bench-render/corej2k-r2r-package-20260930-pass2`.

An alternating same-file check against the retained 1.8.72 loose payload
shows the remaining gap on this page. After one warmup per version, four
fresh-process 512-pixel renders measured 59 to 75 ms for 1.8 versus 284 to
342 ms for the packaged 1.9 codec, with medians of 62.5 and 304 ms. Both
produced 375 by 511 pixels. In two 20-copy runs per version, later-page
averages were 54.9 to 60.2 ms for 1.8 and 80.0 to 84.9 ms for 1.9. Thus
both cold and repeated decoding remain slower. A separate 1.9 scratch probe
measured about 204 to 226 ms of JIT compilation during the first render
after renderer construction, so reducing cold compilation is a distinct
opportunity from the remaining warm decoder cost. This probe does not
measure visible first-page completion. Results are under
`C:/Users/steve/kp-bench-render/r2r-v18-balloon-20260930-pass2` and
`C:/Users/steve/kp-bench-render/r2r-balloon-jit-20260930`.

A JIT event trace of the codec-only package found 87.9 ms of compilation in
seven CoreJ2K methods during a fresh scratch render. Three entropy passes
accounted for 83.1 ms, including 55.4 ms in cleanup. A scratch codec build
removed the immediate-optimization annotations on five decoder passes and
kept all 160 paired balloon PNGs byte-identical. In four alternating 20-file
runs per mode, median first-page rendering fell from 310.5 to 231.5 ms, but
median batch rendering rose from 1,954.5 to 2,096.5 ms; later-page averages
also rose from a median 86.6 to 96.7 ms. The annotation change was rejected
because it traded a cold gain for a larger repeated-render cost. The trace
and trial are under `C:/Users/steve/kp-bench-render/r2r-balloon-jit-20260930`
and `C:/Users/steve/kp-bench-render/j2k-annotations-r2r-20260930`.

A speed-focused ReadyToRun build of the same annotation-free scratch codec
nearly tied the baseline in its first two alternating 20-file pairs, but
regressed in the next two pairs. The four baseline render totals were 2,009,
1,845, 2,369, and 2,253 ms versus 1,916, 1,950, 2,718, and 3,282 ms for
the trial. All 160 PNG hashes matched. A later system sample showed 38 to
41 percent total CPU use, so these runs do not establish a stable gain or
memory bound. The trial remains outside the product tree. Evidence is under
`C:/Users/steve/kp-bench-render/j2k-r2r-optimize-time-20260930`.

A September 30 hidden Continuous-view probe also compared ReadyToRun on the
CMYK JPEG `064034.pdf`. Four alternating launches per mode attached page one
at median 1,696 ms with the normal scratch payload and 1,509 ms with the
full ReadyToRun payload. Median peak working set at attachment rose from
about 311 to 343 MiB. In a separate four-launch-per-mode comparison,
ReadyToRun for only the app or engine attached at medians of 1,645 and
1,622 ms versus 1,661 ms for the normal payload; one app-only launch took
2,059 ms. These are internal bitmap attachment marks, not displayed frames.
The cold gain does not change the earlier whole-pass and memory disposition.
Traces and payloads are under
`C:/Users/steve/kp-bench-render/viewer-attach-20260930`.

A separate scratch build precompiled only the current Release engine, leaving
the application and JPEG 2000 codec unchanged. On the JBIG2 scan at 512 pixels,
three alternating fresh-process runs per mode reduced median page rendering
from 729 to 447 ms. Both 74-page difficult-set runs at 512 pixels improved:
baseline render sums were 7,418 and 7,633 ms versus 6,607 and 6,371 ms with
the precompiled engine. At 2048 pixels the two-run medians moved in the other
direction, from 10,518 to 11,027 ms, and sampled peak medians rose from
261.2 to 268.2 MiB. The 600-page shared set at 1024 pixels was nearly tied
at median render sums of 17,357 and 17,451 ms. All 74 difficult and 600 shared
PNGs matched across each measured run. The first-scan gain repeated in these
headless runs, but the larger difficult-set speed and memory results do not
support precompiling the whole engine for 1.9. Scratch builds and raw runs
are under `C:/Users/steve/kp-bench-render/scan-jit-20260930`.

A JBIG2 arithmetic-state trial reduced repeated context-array access while
preserving all three tested scan-page hashes. Four alternating fresh-process
runs measured median three-page rendering at about 823 milliseconds before
and 840 milliseconds after the change, so the trial was removed. Raw results
are under `C:/Users/steve/kp-bench-render/jbig2-state-20260929`.

The current 1.9 payload was also compared with the retained 1.8.5 payload on
the 40-file, 74-page difficult set at 2048 pixels, three pages per file, and
one file worker. Two alternating measured runs per version gave 1.8 render
times of 4.970 and 5.030 seconds versus 9.834 and 9.881 seconds for 1.9. Wall
times were 10.345 and 10.563 seconds versus 15.195 and 15.249 seconds. Sampled
peaks were 225.2 and 225.6 MiB versus 255.2 and 258.0 MiB. The largest median
page gaps were 678.5 milliseconds on `42828.pdf`, 560 milliseconds on
`064034.pdf`, 477.5 milliseconds on `363_Risk.pdf`, and 388.5 milliseconds on
the JPEG 2000 balloon page. This confirms that difficult-page speed and memory
remain open release gates. Raw results are under
`C:/Users/steve/kp-bench-render/current-difficult-v18-20260929`.

The comparison was refreshed after the current caching and codec work, using
the 1.8.72 and 1.9.0 Release builds, the same 40 inputs and 74 pages, 2048-pixel
output, three pages per file, and one file worker. After one warmup per version,
the alternating 1.8 runs took 4.917 and 5.206 seconds of rendering and 10.014
and 10.508 seconds wall time. Version 1.9 took 9.611 and 9.832 seconds of
rendering and 14.516 and 14.915 seconds wall time. Sampled peaks were 257.5 and
257.8 MiB for 1.8 versus 260.8 and 267.9 MiB for 1.9. All 74 output dimensions
match, and each version reproduced all 74 of its own PNG hashes across both
runs. The largest average page gaps are 662.5 milliseconds for `42828`, 546.5
for `064034`, 461 for `363_Risk`, 339.5 for the JPEG 2000 balloon, and 290 for
`GWG061_Shading_x1a`. The current 1.8 application DLL SHA-256 is
`4AB13A69FD06146A5B963F4B5F5DBEC29761DC22940AF001700D0B0853CF05C8`.
The 1.9 application and engine SHA-256 values are
`0918C18A6A89888FADD849290BCD5BE61BA35BCCB9E23FC43D4F8FDCEDA41B34`
and `BD797E08A6770BD27ECB2639D974CDB106E59D3256AE5A0B67CBA7F6F9115018`.
Raw logs and outputs are under
`C:/Users/steve/kp-bench-render/current-difficult-refresh-20260929`.

The difficult comparison was repeated after the UTF-8 number parser and
eight-bit soft-mask improvements with four alternating measured runs per
version after warmup. Version 1.8.72 had a 5.233-second median render sum,
10.581-second median wall time, and 252.2 MiB maximum sampled peak. Version
1.9.0 had a 10.329-second median render sum, 15.781-second median wall time,
and 266.3 MiB maximum sampled peak. Every run completed all 74 pages without
failure. The four 1.9 runs reproduced all 74 PNG hashes, and those hashes also
match the prior 1.9 baseline. Raw results are under
`C:/Users/steve/kp-bench-render/latest-difficult-20260929`.

The same current 1.9 payload was measured across file-worker counts on the
difficult set. One, two, four, six, eight, and twelve workers took 15.326,
10.392, 6.723, 5.982, 5.818, and 5.216 seconds of wall time, with sampled peaks
of 260.8, 294.5, 394.6, 436.6, 545.2, and 631.3 MiB. Twelve workers saved only
0.766 seconds over six while consuming about 194.7 MiB more. The default was
not changed because this single sweep establishes the tradeoff but does not
define the release memory boundary. The raw table is
`C:/Users/steve/kp-bench-render/latest-difficult-20260929/worker-sweep.csv`.

A shared CPU-budget trial reduced each file's row parallelism as file-worker
count increased. It did not reduce oversubscription in practice. At twelve
file workers, wall time increased from 5.216 to 5.510 seconds and sampled peak
memory increased from 631.3 to 728.1 MiB. Four and six workers also slowed.
The trial was removed. Raw results are under
`C:/Users/steve/kp-bench-render/batch-shared-budget-trial`.

A direct-CMYK cache trial reused exact `PdfDeviceCmyk` results for repeated
four-byte samples in large photographic images. Across three alternating
48-page runs of `064034.pdf`, baseline render sums ranged from 3.625 to 3.945
seconds and the candidate ranged from 3.800 to 4.022 seconds. Candidate peak
memory rose from about 145 MiB to 187 MiB. The trial was removed. Raw results
are under `C:/Users/steve/kp-bench-render/cmyk-cache-trial`.

Several runtime compilation trials were rejected. Method-level aggressive
optimization improved the technical Altona page inconsistently but slowed the
largest Altona page. Applying it only to vector rasterization modestly improved
the 600-file shared runs, but the difficult set slowed from 9.814 to 9.994
seconds and sampled peak memory rose from 260.6 to 265.2 MiB. Disabling tiered
compilation and disabling quick JIT for loops both increased document-opening
time and did not improve the largest page. No JIT policy or method attribute
was retained. Raw results are under
`C:/Users/steve/kp-bench-render/aggressive-opt-20260929`,
`C:/Users/steve/kp-bench-render/tiering-20260929`, and
`C:/Users/steve/kp-bench-render/quickjit-loops-20260929`.

A separate JBIG2 byte-input trial avoided the normal arithmetic decoder rewind
while retaining its marker rewind. All 15 focused JBIG2 tests passed and all
three `42828.pdf` page hashes matched, but paired median three-page rendering
slowed from about 868 to 897 milliseconds. The trial was removed. Raw results
are under `C:/Users/steve/kp-bench-render/jbig2-bytein-20260929`.

A current sampled trace of `42828.0001.001.pdf` attributes its largest active
costs to JBIG2 generic-region decoding and exact one-bit image area sampling.
A direct-reference arithmetic-context trial passed all 15 focused JBIG2 tests
and preserved every measured page hash. Its application-level median appeared
to improve from 822 to 809 milliseconds, but a same-process engine probe
reversed that result: warmed baseline and candidate medians were 209.15 and
217.61 milliseconds across 22 renders each. Median allocation was effectively
unchanged at about 25.9 MiB. The trial was removed. Raw traces and runs are
under `C:/Users/steve/kp-bench-render/42828-profile-20260929` and
`C:/Users/steve/kp-bench-render/jbig2-context-ref-trial`.
Reading the fast generic-region template directly from its backing bitmap also
passed the 15 focused tests and preserved all measured hashes. Across 144
warmed renders, baseline and candidate medians were 193.94 and 191.76
milliseconds, while means were 196.72 and 196.49 milliseconds. Reversed-order
pairs disagreed, so the change was removed. Raw results are under
`C:/Users/steve/kp-bench-render/jbig2-direct-bitmap-trial`.

Moving the binary area sampler's cancellation check outside its source-row
loop retains cancellation before each destination sample and reduces repeated
checks within that sample. Across 72 warmed same-process renders of
`42828.0001.001.pdf`, the median fell from 190.48 to 186.43 milliseconds and
the mean fell from 192.96 to 188.28 milliseconds, with unchanged output and
allocation. A separate warm application comparison improved the median from
213.5 to 207.5 milliseconds across 20 measured renders. Two reversed-order
difficult-set runs completed all 74 pages with byte-identical PNGs; baseline
render totals were 9.676 and 9.716 seconds, while the candidate measured 9.585
and 9.299 seconds. Sampled peak working sets ranged from 258.6 to 265.2 MiB
without a candidate-specific increase. Raw results are under
`C:/Users/steve/kp-bench-render/binary-cancellation-trial`.

A sampled trace of the first page of the difficult file beginning `382252`
attributes 12.01 percent of inclusive CPU time to exact one-bit area sampling,
3.54 percent exclusively to its packed-bit counter, and 3.50 percent to PNG
row reconstruction. Precomputing vertical coverage weights preserved every
measured `382252` and `42828` pixel hash, but the 16-page `382252` workload
slowed from 1.832 to 1.886 seconds into a 2.079 to 2.204 second range. Moving
the PNG filter-zero copy and invalid-filter check outside the byte loop passed
eight focused tests, but slowed the same workload from about 1.69 seconds to
1.81 to 1.83 seconds. Both trials were removed. The trace and raw runs are
under `C:/Users/steve/kp-bench-render/382252-profile-20260929`,
`C:/Users/steve/kp-bench-render/binary-row-weight-trial`, and
`C:/Users/steve/kp-bench-render/png-unfilter-trial`.

Precomputing exact binary-image column bounds kept the `42828` and `382252`
pixel hashes unchanged. Two alternating warmed `42828` pairs had baseline
medians of 163.802 and 162.416 ms versus 160.223 and 161.445 ms for the
trial, with about 35 KiB more allocation per render. The `382252` pairs split:
42.882/42.354 ms and 41.830/42.604 ms (baseline/trial). The mixed result and
extra allocation did not justify retaining the column array. The trial was
removed; raw runs are under `C:/Users/steve/kp-bench-render/binary-column-trial`.

A single-source-row path in the same sampler passed all seven focused tests
and kept both measured page hashes. In two alternating `42828` pairs, warmed
baseline/trial medians were 166.862/164.711 and 164.108/164.210 ms; both
trial first renders were slower than their paired baselines. On `382252`,
warmed medians were 42.660/44.249 and 42.591/43.035 ms. Allocation stayed
effectively unchanged. The trial was removed because it did not improve cold
rendering and slowed the second page. Raw runs are under
`C:/Users/steve/kp-bench-render/binary-single-row-20260930`.

A packed-bit shortcut for spans of at most four source pixels passed the seven
focused sampler tests and preserved both measured page hashes. The two `42828`
warmed pairs split at 163.166/162.197 and 163.657/164.296 ms
(baseline/trial), while both trial first renders were slower. The `382252`
pairs also disagreed at 41.896/42.916 and 43.984/43.773 ms. Allocation was
similar. The shortcut was removed; raw runs and a cold sampled trace are under
`C:/Users/steve/kp-bench-render/binary-short-span-20260930` and
`C:/Users/steve/kp-bench-render/binary-worker-sweep-20260930`.

A sampled trace of the first two `495712.pdf` pages attributes the largest
active renderer cost to vector stroke rasterization, including cell creation
and per-row X sorting. A trial skipped sorting when a distributed row was
already monotonic. All 1,223 rendering tests passed and all 32 measured PNGs
matched the baseline. One baseline/candidate pair measured 3.541/3.753 seconds
and the reversed pair measured 3.447/3.437 seconds, so the result was not
repeatable and the trial was removed. Raw evidence is under
`C:/Users/steve/kp-bench-render/495712-profile-20260929` and
`C:/Users/steve/kp-bench-render/cell-sort-skip-trial`.

A fresh-process profile of both `210260.pdf` pages showed a cold first copy at
189 and 229 milliseconds, while later copies commonly rendered the first page
in 32 to 60 milliseconds and the second in 19 to 32 milliseconds. The earlier
large page-two gap is therefore mainly a cold runtime and cache cost. A
temporary application build forced one row worker instead of the normal four
at 1024 pixels. Thirty fresh processes per variant ran in alternating order.
The normal build had a 422 millisecond median and 421.33 millisecond mean for
both pages together. The one-worker build had a 421 millisecond median and
422.50 millisecond mean. Each page kept one identical hash across all 60
renders. Forcing one worker has no repeatable cold-render benefit and was not
retained. Raw traces, logs, binaries, and PNGs are under
`C:/Users/steve/kp-bench-render/210260-profile-20260929` and
`C:/Users/steve/kp-bench-render/210260-workers-20260929`.

A 16-copy profile of `GWG061_Shading_x1a.pdf` reduced from a 652 millisecond
first render to 103 to 128 milliseconds after warmup. Its main active engine
costs are radial shading, color-function evaluation, and profiled color
conversion. Profiled shading table entries almost always fall back to exact
function evaluation because their connection colors differ. A trial bypassed
the two table samples and evaluated the exact function directly. All 83
focused shading tests passed and all 40 measured PNG hashes matched, but 20
fresh processes per variant measured baseline and trial medians of 678 and
678.5 milliseconds. The trial mean was also slower at 681.35 versus 674.90
milliseconds, so the change was removed. Raw traces and runs are under
`C:/Users/steve/kp-bench-render/gwg061-profile-20260929` and
`C:/Users/steve/kp-bench-render/gwg061-connection-table-trial`.

The calculator compiler now omits `cvr` because every numeric stack value is
already represented as a double. Direct operator coverage and all 4,154 engine
tests pass. Forty fresh `GWG061_Shading_x1a.pdf` processes improved from a
668.5 millisecond median and 673.6 millisecond mean to 664 and 665.85
milliseconds, with one identical PNG hash. After warming both builds, two
alternating difficult-set runs per version averaged 10,033.5 milliseconds of
rendering for the baseline and 9,850.5 for the candidate. The earlier un-warmed
pair averaged 10,443.5 and 10,363 milliseconds. Sampled difficult-set peaks
remained in the existing 263.4 to 269.1 MiB range. All 74 page hashes matched
across both runs of both builds. Twelve additional renders of each
`response-to-fiber-concerns[1].pdf` page also kept one hash with normal and
one-worker rendering; the earlier apparent variation came from treating its
bracketed filename as a wildcard during hashing. The combined small-roll trial
was rejected after its 16-copy median slowed from 2,551.5 to 2,633.5
milliseconds. Raw results are under
`C:/Users/steve/kp-bench-render/gwg061-cvr-trial`,
`C:/Users/steve/kp-bench-render/gwg061-cvr-warm-trial`,
`C:/Users/steve/kp-bench-render/cvr-difficult-trial`, and
`C:/Users/steve/kp-bench-render/cvr-difficult-warmed-trial`. The supplemental
determinism check is under
`C:/Users/steve/kp-bench-render/response-fiber-determinism-20260929`.

A later radial-shading trial moved three geometry products outside the pixel
loop. Three alternating 40-copy runs used identical installed-payload builds.
The baseline and trial render medians were 5,589 and 5,607 milliseconds, their
wall medians were both 8,031 milliseconds, and peak working sets were 113.7
and 113.5 MiB. All 240 output hashes matched. The change had no measured
benefit and was removed. Raw results are under
`C:/Users/steve/kp-bench-render/radial-invariant-trial/payload-comparison`.

A numeric-only calculator stack trial removed type tags and repeated numeric
checks from the exact operator subset used by `GWG061_Shading_x1a.pdf`. Seven
alternating 40-copy runs improved the render median from 5,690 to 5,599
milliseconds, with six pairs favoring the trial and all 560 measured PNGs
matching. The matched-toolchain difficult-set check did not confirm a broad
gain: only one of four pairs improved, while three were flat or slower. Median
render totals were 10,962 and 10,904 milliseconds, sampled peak memory was
266.4 MiB for both variants, and all 592 compared page hashes matched. The
trial was removed. Raw results are under
`C:/Users/steve/kp-bench-render/calculator-numeric-trial`.

The generic image compositor now uses the existing bounded row workers when no
knockout state or shared matte converter makes the paint state mutable across
rows. On 40 repeated copies of `GWG1610_Softmasks_Text_part1_X4.pdf`, three
alternating measured runs reduced the render median from 9,329 to 7,768
milliseconds and the wall median from 11,786 to 10,228 milliseconds. Peak
working set stayed at 129 MiB and all 240 output hashes matched. Two alternating
difficult-set runs with one file worker improved average render time from
10,975.5 to 10,668.5 milliseconds and average wall time from 16,553.5 to
16,260 milliseconds, with all 296 compared page hashes matching and peak memory
slightly lower. At 12 file workers, average wall time improved from 5,317 to
5,083.5 milliseconds and maximum peak working set fell from 676 to 660.6 MiB.
All 4,160 engine tests and 484 app tests pass. Raw evidence is under
`C:/Users/steve/kp-bench-render/gwg1610-profile-20260929` and
`C:/Users/steve/kp-bench-render/gwg1610-row-parallel-trial`.

The generic image loop now also hands its already converted destination ink to
the compositor instead of resolving the same value again for every pixel.
Across seven measured 40-copy runs per build, the retained row-parallel baseline
and this addition had render medians of 7,061 and 6,955 milliseconds and means
of 7,089.14 and 6,995.57 milliseconds. Wall medians were 9,332 and 9,246
milliseconds. Peak working set stayed below 129 MiB and all measured hashes
matched. Two difficult-set pairs disagreed in direction, averaging 10,493 and
10,414.5 milliseconds of rendering and 16,060.5 and 15,946 milliseconds wall
time. The focused 281 tests, all 4,160 engine tests, and all 484 app tests pass.
Raw evidence is under
`C:/Users/steve/kp-bench-render/resolved-image-ink-trial`.

Fully opaque RGB blend paints now write the already calculated blended channels
directly instead of running the general alpha compositor. Across seven
alternating serialized 40-copy Color Burn runs, the render median fell from
3,805 to 3,419 milliseconds and the wall median fell from 6,924 to 6,547
milliseconds. Peak working set stayed within 2 MiB, at 122 and 124 MiB, and all
280 paired output comparisons matched. Two alternating difficult-set pairs
disagreed in direction, averaging 10,680 and 10,810.5 milliseconds, with peak
working sets of 265.5 and 267.5 MiB. All 148 paired page comparisons matched.
The focused 1,225 rendering tests, all 4,160 engine tests, and all 484 app tests
pass. The broader rendering-speed gate remains open.
Raw evidence is under
`C:/Users/steve/kp-bench-render/colorburn-opaque-trial`.

Large non-isolated transparency-group composites now use the existing bounded
row workers because their destination rows are independent. Across seven
alternating serialized 40-copy Color Burn runs, the render median fell from
3,426 to 1,523 milliseconds and the wall median fell from 6,602 to 4,660
milliseconds. Maximum peak working set fell from 122.4 to 115.8 MiB and all
280 paired output comparisons matched. Two alternating difficult-set pairs
averaged 10,551.5 and 10,529.5 milliseconds of rendering and 16,183 and 16,096
milliseconds wall time. Maximum peak working set was 265.2 MiB for both builds,
and all 148 paired page comparisons matched. The focused 1,225 rendering tests,
all 4,160 engine tests, and all 484 app tests pass. Raw evidence is under
`C:/Users/steve/kp-bench-render/group-composite-row-trial`.

Parallel PNG output now resolves the system codec once before file workers use
it. The previous image-format lookup lost three pages across 12 repeated
40-file runs, each with `Value cannot be null. (Parameter 'encoder')`. Twenty
stress runs and seven alternating measured runs after the correction completed
all 1,080 pages without a failure. A 40-page output comparison remained
byte-identical. The three focused PNG tests and all 484 app tests pass. The fix
does not serialize PNG encoding. Raw evidence is under
`C:/Users/steve/kp-bench-render/png-encoder-race`.

A fresh three-run comparison against the installed-layout 1.8.72 payload puts
the retained 1.9 build at a 10,499 millisecond difficult-set render median and
16,062 millisecond wall median, versus 5,678 and 11,292 milliseconds for 1.8.
Peak working sets were 267.1 and 252.4 MiB. Both versions completed all 74
pages without failures. This confirms that the row-parallel improvement reduces
the current 1.9 cost without closing the overall rendering-speed gate. Raw
results are under
`C:/Users/steve/kp-bench-render/row-parallel-v18-v19-20260929`.

Sixteen traced copies of the difficult file beginning `4387` fell from 490
milliseconds on the first render to 48 to 69 milliseconds on the last six,
which matches its measured 1.8 range. The trace includes image decoding,
one-bit area sampling, content parsing, and font loading, but no sustained
renderer cost explains the earlier threefold comparison gap. This file is
therefore another cold runtime and cache case rather than evidence for a safe
steady-state engine change. Raw logs and trace data are under
`C:/Users/steve/kp-bench-render/4387-profile-20260929`.

Eight traced copies of the three-page Ghent output test show a mixed result.
After warmup, page one reaches 130 to 148 milliseconds and matches its 1.8
range. Pages two and three remain around 220 to 235 milliseconds versus the
measured 1.8 ranges near 146 to 152 and 181 to 208 milliseconds. The combined
trace spreads active time across text and font loading, image decoding and
painting, ink conversion, soft masks, and patch mesh shading. It does not yet
isolate one exact, bounded change. Raw logs and trace data are under
`C:/Users/steve/kp-bench-render/ghent-profile-20260929`.

A page-specific engine probe then reused one Ghent document and renderer for
40 uncached renders. After the first pass, page two had a 153.6 millisecond
median with 18.72 MiB mean allocation, and page three had a 79.34 millisecond
median with 17.48 MiB. Each page kept one pixel hash. Page two therefore
reaches its measured 1.8 range and page three becomes faster after its own
resources are loaded. The remaining Ghent gap is first-use page resource work,
which remains relevant to initial scrolling but is not a sustained renderer
regression. Probe code and raw results are under
`C:/Users/steve/kp-bench-render/ghent-page-probe-20260929`.

Separate first-use traces after warming each preceding page narrow the Ghent
work further. Page two spends most of its sampled engine time in JPEG decoding,
image painting, ink conversion, and soft masks; font loading accounts for a
smaller share. Page three introduces a JPEG 2000 image that does not occur on
pages one or two, plus 42 font resources and a 913 KiB Unicode map. Several of
its smaller Unicode maps have identical decoded content under different object
numbers. A bounded exact-content map cache preserved every pixel and passed 803
focused tests, but two 20-copy runs did not improve timing, so it was removed.
The first-use gap remains distributed work with no verified memory-safe source
change. Raw traces, resource inventories, and cache-trial timings are under
`C:/Users/steve/kp-bench-render/ghent-firstuse-probe-20260929` and
`C:/Users/steve/kp-bench-render/ghent-font-resources-20260929`.

Continuous view now schedules its visible center page first, followed by the
nearest forward and backward pages. The previous ascending order could put up
to ten earlier pages ahead of the page being opened or navigated to. Two
focused ordering tests cover the centered and bounded cases. This removes an
avoidable visible-page queue delay without changing engine render time.

A 24-pair alternating fresh-process probe measured time to Ghent ALL page 3
at 1541 by 2048 pixels with four workers. The former ascending schedule had a
2,184.401 millisecond median because pages 1 and 2 rendered first. Directly
prioritizing the requested page had a 1,093.744 millisecond median, a 49.9
percent reduction in time to that page. All 48 requested-page renders had the
same SHA-256 pixel hash. This isolates the scheduling gain but does not measure
WPF presentation, visible scrolling, zoom, memory, or parity with 1.8. Raw
results are under
`C:/Users/steve/kp-bench-render/continuous-visible-20260929`.

A sampled CPU trace of `064034.pdf` identifies the reduced JPEG decoder and
DeviceCMYK display conversion as its main active costs. Reusing the converter's
bounded color cache on the direct CMYK path preserved the page hash and passed
43 focused color tests, but warmed renders were slower, so the trial was
removed. Replacing the reduced transform's eight-value SIMD trailing-zero
search with a fixed scalar scan passed 135 focused JPEG tests and preserved the
page hash, but two reversed-order `064034.pdf` pairs did not show a repeatable
gain. A separate trace attributed more time to that search on `363_Risk`, yet
two reversed-order pairs slowed from 105.4 and 105.7 milliseconds to 111.9 and
121.8 milliseconds. That trial was also removed. The traces and raw runs are
under `C:/Users/steve/kp-bench-render/064034-profile-20260929` and
`C:/Users/steve/kp-bench-render/363-risk-profile-20260929`.

A table-driven DeviceCMYK interpolation trial replaced the converter's nearest
sample, neighbor direction, and interpolation-rate arithmetic with byte-indexed
lookups. All 96 focused renders retained one pixel hash per document and
allocation volume stayed effectively unchanged. On `064034.pdf`, reversed-order
baseline/candidate last-six medians were 119.786/117.758 and 125.906/121.685
milliseconds. On `363_Risk`, they were 107.164/105.929 and 100.810/105.950
milliseconds. The second document reversed the apparent result, so the lookup
change was removed. Raw results are under
`C:/Users/steve/kp-bench-render/cmyk-interpolation-trial-20260929`.

A reduced JPEG reconstruction trial stored the active coefficient rows once
per block instead of testing all eight rows for every output vector. Across
160 focused renders, every `064034.pdf` and `363_Risk` process retained one
pixel hash and allocation volume was unchanged. Reversed-order last-ten
baseline/candidate medians were 117.996/125.907 and 115.418/117.680
milliseconds on `064034.pdf`, and 100.092/98.390 and 102.470/102.088
milliseconds on `363_Risk`. The pairs do not show a repeatable improvement,
so the trial was removed. Raw results are under
`C:/Users/steve/kp-bench-render/jpeg-active-rows-trial-20260929`.

Before the image-cache change, the three Altona files accounted for 1,232
milliseconds of the 2,031 millisecond summed median render gap on the 600
shared successful pages.
The largest file was then tested as 12 repeated inputs at 1024 pixels in
two runs per version. Version 1.8 averaged 710.3 and 731.7 milliseconds
over each run's last six renders; version 1.9 averaged 845.8 and 840.3
milliseconds. The sampled process peak working sets were 149.5 MiB for both
1.8 runs and 666.9 to 667.0 MiB for 1.9. A separate single-file run reached
118.7 versus 510.4 MiB and rendered in 729 versus 1,624 milliseconds.
Every repeated render within each version produced the same PNG hash; the
two versions produced different hashes. These earlier runs confirmed both a
persistent speed gap and a large memory gap on this file, beyond the first
render. The scratch inputs, logs, and PNGs are under
`C:/Users/steve/kp-bench-render/eci-altona-repeat-20260929`.

A separate engine probe opened that file from a stream and rendered its first
page at 1024 by 1024. After opening and collecting garbage, the managed heap
was 128.2 MB. Rendering raised it to 460.1 MB and allocated 337.9 MB during
that stage; another collection reduced the live heap to 213.5 MB while
working set remained 496.9 MB. The open stage therefore retains roughly the
file's 127.7 MB of bytes, and the render stage creates substantial additional
temporary and retained data. The probe's output hash was stable and it
reported no diagnostics. Its source and output are in the same scratch folder.

Of the live data after that collection, the byte scratch pool retained
66.6 MB. A local trial halved that pool's budget from 64 to 32 MiB. It
lowered the collected heap from 213.5 to 179.7 MB, but raised render-stage
allocation from 337.9 to 370.6 MB and working set immediately after render
from 512.9 to 545.3 MB. The two baseline single-pass renders took 2,118
and 2,175 milliseconds, versus 2,118 milliseconds in the trial; all three
pixel hashes matched. This trial
reduced idle retention but worsened peak memory, so the original pool budget
was restored. Both probe logs are in the same scratch folder.

Allocation sampling then identified two 92,045,344-byte Flate output arrays.
The PDF has one 3392 by 6784 image matching that decoded size, and the
persistent decoded-image cache rejects such an image at its 64 MiB limit.
A 128 MiB in-render limit reuses it during
the page, including an output-profile retry, then removes entries above the
original 64 MiB persistent limit after the final pass. On the single-page
engine probe, render-stage allocation fell from 337.9 to 217.4 MB, working
set immediately after rendering fell from 512.9 to 391.9 MB, and collected
heap returned to 213.5 MB. The pixel hash was unchanged.

In two 12-copy application runs, the last-six average fell from 845.8 and
840.3 milliseconds to 742.3 and 752.2 milliseconds. The sampled process
peak rose from baseline values of 666.9 and 667.0 MiB to trial values of
677.9 and 678.0 MiB in that repeated
workload. Three same-layout 600-file shared runs measured baseline peaks of
588.8, 588.4, and 540.9 MiB versus 501.1, 501.5, and 501.0 MiB with the
change. Summed render times were 15,404/15,517/15,215 milliseconds for
baseline and 15,479/14,932/15,395 for the change, so whole-set speed gain
is not established. All 600 shared PNG hashes matched. On the 74-page
difficult set, all PNG hashes matched, render sums were 8,340 and 8,229
milliseconds, and peaks were 209.1 and 210.4 MiB. Full checks pass with
4,150 engine tests, 482 app tests, and a Release build without warnings.
Raw traces, probes, applications, logs, and images are under
`C:/Users/steve/kp-bench-render/eci-altona-repeat-20260929`.

### JPEG worker comparison (2026-09-29)

The current engine rendered `22060_A1_01_Plans.pdf` at 724 by 1024 pixels in
four fresh-process runs ordered one/four/four/one workers. Each process rendered
40 times, with the last 20 used for its median. One-worker medians were 379.667
and 380.269 milliseconds; four-worker medians were 337.733 and 341.973
milliseconds. Median allocations were about 40.6 and 49.5 MB per render,
respectively. Every render retained the same SHA-256 pixel hash and reported no
diagnostics. Four workers improve this page's latency at a memory cost, so a
worker-count reduction is not a speed fix for this workload. The current CPU
trace identifies JPEG Huffman decoding, block output, and soft-mask reduction
as the main active work. The probe and trace are under
`C:/Users/steve/kp-bench-render/jpeg-profile-20260929`.

A second 12-bit Huffman lookup was tested on the same page and removed. Two
reversed-order, one-worker comparisons gave baseline/candidate warmed medians
of 377.292/377.257 and 381.542/381.699 milliseconds. The candidate added
about 0.36 MB of allocation per render, while all 160 pixel hashes remained
identical. The current 8-bit lookup remains in the engine.

Sequential JPEG decoding now carries whether a block has AC coefficients from
its decoded symbols into reconstruction, avoiding a second scan of 63 values.
Progressive reconstruction still checks its stored coefficients. Two
baseline/candidate reversed-order pairs of 40 fresh-document renders, using
the last 20 per process, gave one-worker medians of 374.004/364.895 and
372.557/365.148 milliseconds. Four-worker medians were 336.415/323.252 and
335.833/330.589 milliseconds. All 320 focused hashes match, with essentially
unchanged median allocation. The 600 shared and 74 difficult application PNGs
were initially compared using an older DLL in both app folders, so that result
does not verify the change. A controlled before/after engine probe instead
matched all 618 successfully rendered page hashes and all 52 identical failure
records. The current published app differs from the older preserved app on 19
shared and six difficult PNGs; those payloads include other intervening changes,
so these differences are not attributable to this JPEG edit. All 4,150 engine
tests, 482 app tests, and the Release build pass. This is a page-specific speed
gain, not a whole-corpus fidelity or interactive parity result. Evidence is under
`C:/Users/steve/kp-bench-render/jpeg-profile-20260929`.

### Large-image color-cache trial (2026-09-29)

The large Altona page still spends most of its active CPU time in area and ICC
color conversion. Raising its bounded conversion cache from 16,384 to 65,536
entries gave baseline/candidate warmed medians of 727.333/704.932 and
721.581/713.587 milliseconds in reversed-order pairs, while median allocation
rose from about 277.4 to 291.6 MB per render. A 32,768-entry cache gave
716.870/710.354 and 718.712/710.039 milliseconds, with allocation rising to
about 282.1 MB. All 320 page hashes matched. Neither small page gain justified
the added allocation, so the 16,384-entry limit remains. Trial binaries and
the probe are under `C:/Users/steve/kp-bench-render/altona-cache-20260929`.

### Current startup marker comparison (2026-09-29)

The retained 1.8 Release build and current published 1.9 payload each had one
warmup, then three measured launches in alternating order. Both used the same
opt-in `MainWindow ready` marker and a hidden-window process launch. Warm
medians were 1,346.588 milliseconds for 1.8 and 1,201.627 for 1.9; ranges
were 1,345.293 to 1,381.985 and 1,193.027 to 1,252.956 milliseconds.
Main-window construction medians were 466.001 and 566.935 milliseconds,
respectively. The current total ready marker is faster in this launch mode,
while construction remains slower. Hidden launches and this marker do not
establish visible startup, first-page, scrolling, or zoom parity. Raw traces
and the launch script are under `C:/Users/steve/kp-bench-render` with
`startup-*-20260929` names.

### ICC input-curve lookup trial (2026-09-29)

A 256-value input-curve lookup for four-channel ICC tables was tested on the
large Altona page and removed. Reversed-order warmed medians were
719.731/710.252 and 726.537/721.158 milliseconds for baseline/trial.
Per-render allocation stayed near 277.4 MB, and all 160 page pixel hashes
matched. The gain was small on this page and did not establish a corpus-wide
benefit, so the extra profile allocation and lookup logic were not retained.
Trial binaries and raw timings are under
`C:/Users/steve/kp-bench-render/icc-curve-20260929`.

### Area-loop branch trial (2026-09-29)

Moving image-type checks from each source sample to each source row was tested
on the large Altona page and removed. Reversed-order warmed medians were
819.095/829.391 and 831.646/828.879 milliseconds for baseline/trial.
Per-render allocation remained near 277.4 MB and all 160 page hashes matched.
The two pairs disagree on direction, so this does not show a reliable speed
gain. These below-normal-priority runs occurred under a higher machine load
than the earlier Altona trials. Raw timings and binaries are under
`C:/Users/steve/kp-bench-render/area-branch-20260929`.

### Two-entry color-cache trial (2026-09-29)

A two-entry set at the existing 16,384-entry cache capacity was tested on the
large Altona page and removed. Reversed-order warmed medians were
817.454/928.592 and 820.635/920.447 milliseconds for baseline/trial.
Per-render allocation remained near 277.4 MB and all 160 page hashes matched.
Both pairs show a substantial slowdown, so the direct-mapped cache remains.
Raw timings and binaries are under
`C:/Users/steve/kp-bench-render/two-way-cache-20260929`.

### Altona warmup investigation (2026-09-29)

Twenty identical copies of `altona_technical_1v2_x3.pdf` were rendered through
the current application's headless batch path with one file worker. The first
page took 742 milliseconds and the last ten averaged 155.5 milliseconds;
the 20-page render sum was 4,413 milliseconds. In a separate run with .NET
tiered compilation disabled, the first page took 729 milliseconds, later
pages stabilized near 150 milliseconds, and the sum was 3,599 milliseconds.
All 20 PNGs matched across runs. This indicates that runtime compilation
contributes to the repeated-page warmup, but the first-page cost remains.

On the 600-page shared set, reversed-order default/no-tier runs had wall times
of 26.818/28.221 and 27.703/29.191 seconds, with summed render times of
14,682/15,602 and 15,181/16,152 milliseconds. Every page succeeded and all
600 PNG hashes matched in the first pair. No-tier compilation improved the
three early Altona outliers by about 700 milliseconds combined in each pair,
but slowed the workload overall. The global runtime setting was not retained.
Raw results are under `C:/Users/steve/kp-bench-render/tiered-pgo-20260929`.

Enabling quick JIT for loops made the repeated technical Altona batch slower:
its first page took 850 milliseconds and its 20-page sum was 4,833
milliseconds. Disabling dynamic PGO improved that repeated batch to 631
milliseconds on the first page and 3,798 milliseconds summed, but regressed
the 600-page shared set. In reversed-order no-PGO/default runs, wall times
were 34.593/26.935 and 27.837/26.896 seconds; summed render times were
17,586/14,779 and 15,629/14,738 milliseconds. The first no-PGO run was an
outlier, but both pairs favored the default. All 600 PNG hashes matched in
the first pair. Neither runtime setting was retained. Raw results are under
`C:/Users/steve/kp-bench-render/altona-app-session-20260929` and
`C:/Users/steve/kp-bench-render/pgo-20260929`.

On 20 copies of the technical Altona page, the retained 1.8 application took
170 milliseconds for the first page and averaged 168 milliseconds for the
last ten. The current 1.9 application took 742 and 758 milliseconds on the
first page in two runs, while its last ten averaged 155.5 and 149.1
milliseconds. This isolates a large cold-render gap on this page; the later
renders do not show the same regression. It does not establish visible
first-page timing or parity on other documents.

The September 29 payload after the reduced JPEG 2000 paint change still has
this cold gap. In three alternating 20-copy Altona pairs at 1024 pixels,
1.8 first-page times were 187, 174, and 168 milliseconds; 1.9 took 792,
793, and 783 milliseconds. The median last-ten average was 168.8 versus
154.0 milliseconds, so the gap remains concentrated in first use. Median
20-page render totals were 3,335 versus 4,389 milliseconds, with sampled
peak working sets of 84.1 versus 149.4 MiB. Each version produced one stable
PNG hash across its 60 measured copies. A sampled trace of the exact 1.9
payload placed about 165 milliseconds of its first render under text display,
89 under font reading, 131 under image rendering, and 94 under color-space
reading; these inclusive stack times overlap. The tenth render had much less
text and image work. No single paint loop explains the cold gap. Headless
timing and trace evidence is under
`C:/Users/steve/kp-bench-render/altona-cold-current-20260929`.

Separating Type 3 text handling from the ordinary text function did not
reduce the cold Altona render consistently. Three alternating same-version
20-copy pairs measured first-page baseline/trial times of 762/734, 739/765,
and 738/739 milliseconds. Median full-batch render totals were 4,149/4,608
milliseconds. All 60 paired PNGs matched, and peak working sets stayed near
149 MiB. The split was removed; trial evidence is under
`C:/Users/steve/kp-bench-render/type3-cold-split-20260929`.

Applying optimized-first compilation only to `ConvertArea` did not improve a
direct one-worker probe of that page. Reversed-order baseline/trial first
renders were 784.763/783.762 and 787.815/780.194 milliseconds; the sums of
renders 2 through 13 were 2,545.574/2,526.922 and 2,559.204/2,571.325
milliseconds. Steady medians were 149.877/150.115 and 146.726/152.742
milliseconds. All 160 pixel hashes matched and allocation stayed near 54 MB
per render. The method hint was removed. The app-batch and probe results are
under `C:/Users/steve/kp-bench-render/altona-app-session-20260929` and
`C:/Users/steve/kp-bench-render/area-jit-20260929`.

### Current balloon JPEG 2000 profile (2026-09-29)

Twenty copies of `balloon_a1b_jp2k.pdf` were rendered at 1024 pixels through
the retained 1.8 application and current 1.9 payload, with one file worker.
In reversed-order pairs, 1.8 first-page times were 153 and 155 milliseconds
and last-ten averages were 142.3 and 144.4 milliseconds. Version 1.9 took
534 and 530 milliseconds first, with last-ten averages of 189.6 and 194.4
milliseconds. All 80 batch rows succeeded. Each version produced one stable
PNG hash across its copies and runs; the versions have different output hashes.
This page has both cold and steady render gaps.

A sampled trace of the 1.9 headless application path attributes 13.19 percent
exclusive to JPEG 2000 significance propagation, 8.04 percent to its cleanup
pass, 9.46 percent to inverse wavelet synthesis, 9.55 percent to buffer
zeroing, and 8.21 percent to reduced image painting. Of 326 zeroing samples,
282 are under `StdEntropyDecoder.GetCodeBlock`, which clears its used state
and output ranges. PNG encoding is another 20.03 percent of sampled time but
is outside the logged page-render interval. These sample shares are diagnostic
attribution, not independent wall-clock timings. The decoder and reduction
paths remain the main candidates for a verified speed fix. Raw logs and trace
are under `C:/Users/steve/kp-bench-render/balloon-app-20260929`.

An `AggressiveOptimization` hint on `StdEntropyDecoder.GetCodeBlock` did not
improve the same-version balloon batch. Across three reversed-order pairs of
20 copies at 1024 pixels, retained/trial render medians were 4,462/4,461
milliseconds, wall medians were 6,121/6,134 milliseconds, and maximum peak
working sets were 120.4/120.2 MiB. All 60 paired PNGs matched. The hint was
removed; evidence is under `C:/Users/steve/kp-bench-render/j2k-entry-opt-20260929`.

Large area-sampled JPEG 2000 image paints now use up to four bounded row
workers even below the general page-size threshold. Three alternating
same-version pairs of 20 balloon copies at 1024 pixels reduced median render
time from 4,731 to 4,419 milliseconds and wall time from 6,450 to 6,224
milliseconds. Maximum peak working set rose from 120.4 to 122.2 MiB. All 60
paired PNGs matched. On the 649-file conformance corpus, the same three-pair
protocol reduced median render time from 15,509 to 15,299 milliseconds and
wall time from 28,531 to 28,205 milliseconds. Peak working set was 494.7
versus 495.0 MiB; all 1,842 paired PNGs matched, with 614 successes and 35
skips in each run. The 74-page difficult set at 1024 pixels varied more:
median render time was 8,947 versus 9,129 milliseconds, wall time was 11,067
versus 11,281 milliseconds, and peak working set was 199.5 versus 204.1 MiB.
All 222 difficult PNGs matched. This is a focused JPEG 2000 gain and a modest
conformance gain, not difficult-set speed parity. The 4,160 engine and 484 app
tests pass. Raw evidence is under
`C:/Users/steve/kp-bench-render/j2k-paint-parallel-20260929`.
The final application-option version repeated the balloon gain in three more
pairs: median render time was 4,318 versus 4,050 milliseconds, wall time was
5,932 versus 5,688 milliseconds, and peak working set was 120.1 versus
122.3 MiB. All 60 PNGs matched. The engine option defaults to one worker;
the application explicitly requests the larger JPEG 2000 paint cap.
A final conformance render from that exact option-based payload matched all
614 successful PNGs from the measured trial, with the same 35 skips.

At the application's 751 by 1023 output size, a separate 40-pass engine probe
rendered the same page with one, two, and four workers. Two one-worker runs
had last-20 medians of 184.331 and 180.996 milliseconds; two two-worker runs
had 173.570 and 173.784 milliseconds; two four-worker runs had 160.771 and
160.204 milliseconds. Median allocation rose from about 17.65 MB with one
worker to 17.72 MB with two and 17.81 MB with four. All 240 renders had the
same pixel hash. The application's current output-size policy selects one
worker for this page under its general policy; the focused JPEG 2000 paint cap
was added afterward. A second 20-pass probe on the technical Altona page at
the same output size found last-ten medians of 158.412 and 149.392 milliseconds
with one worker, versus 158.770 and 159.760 milliseconds with four. All 80
Altona renders had the same pixel hash. A general reduction in the worker
threshold is therefore unsupported; concurrent viewer memory and a wider page
set would still need checking before a narrower policy change. Probe output
is under `C:/Users/steve/kp-bench-render/balloon-workers-20260929`.

Skipping local-variable initialization in the three hot entropy-decoder passes
preserved the balloon pixel hash and reduced three last-30 four-worker medians
from 219.460, 218.790, and 215.530 milliseconds to 214.560, 212.350, and
212.870 milliseconds. The broader 74-page difficult runs did not confirm the
gain: candidate render totals were 11.694 and 11.503 seconds versus baseline
totals of 11.640 and 11.479 seconds in the same measurement session. All 74 PNG
hashes matched, and 45 focused JPEG 2000 tests passed. The hint was removed.
Raw results are under
`C:/Users/steve/kp-bench-render/jp2-skip-locals-init-20260929`.

Two ToUnicode cold-path trials on Ghent ALL page 3 were also removed. Forcing
optimized compilation on the parser produced baseline/candidate first-render
medians of 1,077.20 and 1,072.37 milliseconds across 24 alternating pairs.
Skipping the metadata-tokenizer pass when no removable metadata was present
produced medians of 1,063.49 and 1,065.84 milliseconds in a second 24-pair
comparison. Each trial retained one pixel hash, and neither established a
meaningful first-render gain. Raw results are under
`C:/Users/steve/kp-bench-render/tounicode-jit-20260929` and
`C:/Users/steve/kp-bench-render/tounicode-fastpath-20260929`.

The 934,858-byte ToUnicode map on Ghent ALL page 3 contains 65,348 mappings
to the same Unicode replacement character. Reusing that canonical string
reduced median first-render allocation from 151,006,500 to 149,381,036 bytes
across 24 alternating pairs. Median first-render time moved from 1,140.94 to
1,132.84 milliseconds, which is too small to claim as a speed improvement.
All 48 focused renders retained one pixel hash. The current app then rendered
all 74 difficult pages successfully, and every PNG matched the preceding
current-app baseline. All 4,155 engine tests and 484 app tests pass, and the
Release build completes without warnings or errors. Evidence is under
`C:/Users/steve/kp-bench-render/tounicode-replacement-20260929`.

The current loose release payload was then compared with 1.8.72 on the same
40 difficult files and 74 pages after one warmup per version. Two alternating
measured runs took 5.508 and 5.381 seconds of rendering for 1.8 versus 10.719
and 10.814 seconds for 1.9. Wall times were 11.082 and 10.764 seconds versus
16.178 and 16.187 seconds. Sampled peak working sets were 259.6 to 259.9 MiB
for 1.8 and 267.2 MiB for 1.9. Both 1.9 runs completed all pages with no
diagnostics and reproduced all 74 PNG hashes. Two long-running recursive file
searches were stopped before this retained comparison; the contaminated runs
are preserved separately and excluded. Evidence is under
`C:/Users/steve/kp-bench-render/current-payload-clean-paired-20260929`.

A sampled trace of `mipeng_poster_w24.pdf`, the next unprofiled contributor,
attributes its largest active engine costs to repeated Form XObject rendering
and content parsing. Increasing the parsed-stream cache from 128 to 256 entries
kept its existing 64 MiB byte bound and preserved the output hash, but 12
alternating fresh-process pairs measured a 1,556 millisecond baseline median
and a slower 1,597 millisecond candidate median. The trial was removed. Raw
results are under
`C:/Users/steve/kp-bench-render/stream-cache-256-trial`.

Parsing content-stream integers and real numbers directly from their UTF-8
bytes reduced the same poster's median first-page render time from 1,610.5 to
1,579.5 milliseconds across 24 alternating fresh-process pairs. The second
12-pair sample independently measured 1,625 versus 1,572 milliseconds. All 48
renders produced one pixel hash with no failures, while sampled peak working
sets remained 196.8 versus 196.6 MiB. The invariant parser remains as a
compatibility fallback for syntax outside the direct parser. All 4,159 engine
tests and 484 app tests pass. Evidence is under
`C:/Users/steve/kp-bench-render/content-number-parser-trial`.

Caching each Form XObject's resolved matrix, bounds, resources, and transparency
flags in a separate 128-entry renderer cache preserved the poster output and
reduced sampled peak working set from 199.9 to 197.9 MiB. It nevertheless made
12 alternating fresh-process pairs slower: median render time increased from
1,570 to 1,598 milliseconds and mean time increased from 1,571.33 to 1,614.58
milliseconds. The cache was removed. Evidence is under
`C:/Users/steve/kp-bench-render/form-metadata-trial`.

A fresh sampled trace of the retained number parser put content parsing at
3.85% inclusive, down from 4.90% in the preceding sampled trace, while Form
rendering remained the largest active engine stack at 12.35% inclusive. A
temporary diagnostic counted 84 Form calls but 56 unique Form-and-transform
keys; the other 28 keys occurred only twice. This limited reuse, together with
the slower metadata-cache result, does not support retaining rendered Form or
bounding-mask surfaces. All diagnostic source was removed. Evidence is under
`C:/Users/steve/kp-bench-render/mipeng-profile-parser-20260929` and
`C:/Users/steve/kp-bench-render/form-key-diagnostic`.

Directly sampling ordinary eight-bit soft-mask bytes during bilinear
interpolation reduced the poster's median first-page render time from 1,599.5
to 1,576.5 milliseconds across 24 alternating fresh-process pairs. Mean time
fell from 1,616.88 to 1,576.62 milliseconds. The second 12-pair sample
independently remained faster at medians of 1,575.5 versus 1,568 milliseconds.
All 48 renders produced one pixel hash with no failures, and sampled peak
working set remained 194.4 versus 194.2 MiB. All 4,159 engine tests and 484 app
tests pass. Evidence is under
`C:/Users/steve/kp-bench-render/softmask-bilinear-trial`.

Earlier checkpoints below document individual fixes and historical measurements.
They do not supersede the current paired results above.

On the profiled response-to-fiber-concerns page, two reversed-order pairs of
80 single-threaded fresh-document renders, each excluding the first 40,
reduced steady medians from 446-452 to 290-301 milliseconds with identical
pixel hashes. This improvement is specific to that workload and harness;
it does not establish whole-application speed parity.

The separate ReadyToRun experiment in `r2r-cmyk-results.csv` was not adopted:
shared render time fell to 11.829 seconds, but difficult render time rose to
10.643 seconds and shared whole-pass time rose to 24.836 seconds. Its retained
images are also unchanged. No precompilation setting was changed in the project.
The narrowly scoped color comparison is described in `engine/docs/color-review.md`.

The missing-font fix at `bb300ef` uses a diagnosed Helvetica fallback with
Windows ANSI encoding for unknown missing resources in compatibility mode.
`font-ansi-pixels.json` records lower mean RGB error against PDFium for all ten
changed outputs and identical pixels for the other 664 outputs. All 674 renders
complete successfully. This recovers visible text from malformed resources;
it cannot reconstruct an absent original font. Strict behavior is preserved.

Unmeasured interactive behavior and known speed and fidelity gaps remain
release gates.

The later vectorized inverse at `5d0d8ec` matches all 16,777,216 RGB inputs to
the scalar reference, including a separate test with hardware intrinsics disabled.
Two reversed-order pairs on the same profiled page reduce steady medians from
308.052 and 281.686 to 277.619 and 246.561 milliseconds, respectively, with
identical hashes. The accelerated path lazily adds about 154 KiB of shared lookup
storage. `rgb-vector-pixels.json` confirms unchanged pixels for all 674 retained
application outputs. The earlier `rgb-vector-paired-results.csv` measurements include this
change. Both builds ran faster than in the preceding session, so the difference
between sessions is not evidence of a general gain from vectorization.

The largest mean RGB difference in the 74-page difficult set is
`pdf-cos-syntax/CompactedPDFSyntaxTest.pdf`. Its content selects an empty-name
graphics-state resource with `/ca 0.33` and `/CA 0.66`. Engine 1.9 applies those
fill and stroke opacities; the retained PDFium output paints the shapes opaque.
This particular difference preserves the document's requested transparency and
is not a target for pixel matching. A focused regression checks both opacities
with empty and ordinary resource names, using direct and indirect dictionaries.
All 3,741 engine tests pass after adding these four cases. No rendering code
changed in this checkpoint. The other visual differences remain open.

In `altona_technical_1v2_x3.pdf`, the right-hand text sample is a 947 by 301
pixel image (object 201), painted through nested Forms at 245.2755 by 78.56151
page units. Runtime tracing confirmed that clipping sends it through the
general image path with direct device samples and integer grid reduction.
Area averaging now preserves its thin strokes and visibly smooths the text.
Unmasked converted images and plain gray/RGB reductions also use area averaging;
masked images retain their existing sampling. This does not dispose of the
page's other differences.

The area-averaging checkpoint renders all 674 pages successfully. Seven difficult
outputs and 61 shared outputs have lower mean RGB error against PDFium; 604 are
unchanged. Two shared outputs have small increases: the Altona drop-shadow page
changes from 8.663269 to 8.682427 and Arakawa-2025-Lab_Animal from 5.215518 to
5.215681. Visual inspection of the former shows smoother raster text. Raw scores
are in `area-complete-Broad-analysis.json` and `area-complete-Shared-analysis.json`.
All 3,747 engine tests and 352 app tests pass, including reduction under clipping,
quarter-turn rotation, and fractional edge weighting.

This fidelity improvement has an unresolved performance cost. A separate
baseline/new/new/baseline difficult-batch check records render totals of
8.905/10.291/10.252/9.150 seconds and wall times of
13.954/15.358/15.352/14.282 seconds in `area-timing-results.csv`. These four
passes are a focused before/after check, not a replacement for the full paired
PDFium measurements in `rgb-vector-paired-results.csv`, which predate area averaging. Much of the added cost
is in 42828.0001.001.pdf and balloon_a1b_jp2k.pdf. Optimize the averaging path
while preserving the recovered detail; speed parity remains open.

The later one-bit reduction path counts packed bits with exact integer coverage
weights, then interpolates the two converted colors. In the final
baseline/new/new/baseline check, the three 42828.0001.001.pdf pages take
1.187/0.739/0.773/1.179 seconds combined. This recovers about 36% of their render
time relative to the initial averaging implementation. Whole difficult-batch
render totals are 10.365/10.521/9.955/10.115 seconds and wall times are
15.526/16.228/15.038/15.261 seconds, so these runs do not establish a batch-wide
speed improvement. See `area-count-final-timing-results.csv` and
`area-count-final-verification.json`. The plain-byte shortcut experiment was
removed because its benefit was unclear.

All 674 final outputs match the bit-count candidate. Relative to initial area
averaging, all 74 difficult outputs and 591 shared outputs are unchanged. Exact
coverage rounding changes 1,614 pixels across nine shared images by at most one
channel level; `area-count-pixels.json` retains their scores against PDFium.
Eight added cases cover packed row padding, fractional reduction, rotation,
clipping, both bit polarities, and an independent integer coverage-grid oracle.
All 3,755 engine tests and 352 app tests pass. The remaining area-averaging cost,
other fidelity differences, and full-pipeline parity still require work.

The balloon JPEG 2000 page remains a major speed gap. A 100-render sampled
thread-time profile of the current renderer, examining only time after 35 seconds,
attributes about 18.65 of 26.36 seconds to decoding, including memory operations,
and 6.48 seconds directly to area sampling. Its source is 2,717 by 3,701 pixels;
the next lower JPEG 2000 level is smaller than the requested output. Preserve
the current resolution while investigating area-sampling overhead and decoder
memory traffic. The raw profile and summary are `balloon-current.nettrace`,
`balloon-current.speedscope.json`, and `balloon-profile-summary.json` in the
adjacent `parity-20260909-ghent` directory.

A direct vector-load/store experiment for wavelet column copies was discarded.
Two reversed-order 80-render pairs, excluding the first 40 renders of each run,
gave baseline/new medians of 608.443/595.071 and 581.420/595.482 milliseconds.
All 320 hashes matched with zero diagnostics, but the timing change reversed
direction. `vectorcopy-*.csv` retains all iterations. No decoder or rendering
implementation change was retained from this experiment.

Small RGB area footprints now reuse horizontal weights across rows and unroll
up to three columns while preserving accumulation order and cancellation.
All 674 application outputs remain pixel-identical to the one-bit checkpoint;
`areaweights-pixels.json` records the comparison. A retained scalar digest covers
388,960 gray/RGB footprints, including boundaries and the wider fallback path.
The isolated 3,078,144-sample check matches every pixel and reduces sampling
time by about 27%; this is a kernel measurement, not application throughput.

On the balloon page, baseline/new/new/baseline runs of 80 fresh-document renders,
excluding the first 40 per run, give medians of 591.797/558.250/558.135/581.445
milliseconds. Both run orders improve by about 4% to 6%, with all 320 hashes
unchanged and zero diagnostics. `areaweights-summary.json` and the four
`areaweights-*.csv` files in `parity-20260909-ghent` retain the measurements.
All 3,757 engine tests, 352 app tests, and the Release payload publish pass.
Decoder cost and broader rendering, performance, and interactive gates remain open.

JPEG 2000 reconstruction now allocates buffers above the sample pool's retention
limit directly, avoiding a second clearing of already-zeroed arrays. Reused
buffers still clear every tile sample, without clearing unused bucket capacity.
The pool limit and temporary-memory accounting are unchanged. For the balloon
page, this removes 120,667,404 redundant bytes of clearing per decode.
All 674 retained application outputs remain identical (`frame-clear-pixels.json`),
and 3,757 engine tests, 352 app tests, and the Release payload publish pass.

The corresponding 80-render baseline/new/new/baseline medians, excluding the
first 40 of each run, are 567.285/555.692/572.878/576.186 milliseconds. All 320
hashes match with zero diagnostics. The small differences and overlapping ranges
do not establish a general speed improvement. `frame-clear-summary.json` and
the four `frame-clear-*.csv` files retain the data in `parity-20260909-ghent`.
The published and measured engine SHA-256 is
`A208E82F48852EB4474C345049BA97E65A522B692E49C336B73719D225DBE855`.

The earlier `frame-clear-paired-results.csv` run confirms that balloon and Ghent combined-suite page 2
remain major difficult-page gaps (968/426 and 641/132 milliseconds respectively).
The shared set also identifies `GWG182_16Bit_Images_ICCbasedGray_x4.pdf` at
295/45 milliseconds before grayscale lookup caching. Its 684 by 684, 16-bit ICC
grayscale image used the uncached conversion path. A 300-render profile attributes
20.67 of 44.83 seconds of rendering in its latter half to color conversion and
8.73 seconds to TIFF prediction (`gray16-profile-summary.json`).

Single-component lookup caching now includes 16-bit samples. Each converter uses
320 KiB for values and validity flags, plus 64 KiB when matte alpha is tracked;
parallel row workers own separate converters. Eight retained-reference cases
cover the full 65,536-value range repeated with differing alpha, inverted decode,
nonlinear calibrated gray, and reduction. All 674 corpus outputs remain identical
in `gray16-lookup-pixels.json`. Tests and the Release payload publish pass.

Two reversed-order 80-render pairs at size 1024, excluding the first 40 renders,
give baseline/new medians of 288.868/122.397 and 287.692/121.944 milliseconds.
All 320 hashes match with zero diagnostics, a roughly 58% reduction on this page.
`gray16-lookup-summary.json` and `gray16-timing-*.csv` retain the data in
`parity-20260909-ghent`. The published and measured engine SHA-256 is
`E2EDB20F6EEC616AAE2009347FBC0579A6086ED95617D9DF317F0703E7EDDB52`.
The earlier `frame-clear-paired-results.csv` measurements predate this change. The same
profile identified bit-by-bit TIFF prediction as another substantial cost.

TIFF prediction now reconstructs eight-bit bytes and big-endian 16-bit words
directly, preserving modular arithmetic and channel spacing. Packed samples
retain the existing path. Six test cases cover 1, 3, and 4 channels across three
row widths, including single-column rows, wraparound, and independent row starts;
two more cases check rejection of incomplete rows. No additional buffers are used.
All 3,773 engine tests, 352 app tests, and the Release payload publish pass.
All 674 corpus images remain identical (`tiff-direct-pixels.json`).

Against the grayscale-lookup build, two reversed-order 80-render pairs excluding
the first 40 give baseline/new medians of 123.959/65.931 and 122.102/67.201
milliseconds. All 320 hashes match with zero diagnostics, a 45% to 47% reduction
on the profiled page. `tiff-direct-summary.json` and `tiff-timing-*.csv` retain the
measurements in `parity-20260909-ghent`. The published and measured engine SHA-256
is `530B72A65095D0E50A3E36D10B67962B227BC72B179659B9773D75BDDFD56121`.
The earlier `frame-clear-paired-results.csv` comparison predates both changes; overall speed,
fidelity, and interactive parity remain open.

Ghent combined-suite page 2 has distributed costs. The latter half of a
100-render sampled profile attributes 1.49 of 12.02 rendering seconds directly
to `SetInkPixel`, 1.45 seconds to GC polling, and 1.39 seconds directly to Form
processing (`ghent-page2-profile-summary.json`). A separate branch probe counts
296,457 general-path calls with a transparent backdrop among 913,203 ink calls.
The probe counters were removed before validation and timing.

Transparent-backdrop CMYK compositing now omits zero-weight backdrop and blend
calculations while preserving source-alpha multiplication, division, and rounding.
Six regression cases pass both before and after the change across all 16 authoring
blend modes, three opacities, images, and filled paths. All 674 corpus outputs
remain identical (`ink-transparent-pixels.json`), and all 3,779 engine tests,
352 app tests, and the Release payload publish pass. No additional buffers are used.

Two reversed-order 80-render pairs, excluding the first 40, give baseline/new
medians of 214.998/208.737 and 213.645/210.444 milliseconds, a modest 1.5% to 3%
page improvement with overlapping ranges. All 320 hashes match with zero
diagnostics. `ink-transparent-summary.json` and `ink-transparent-timing-*.csv`
retain the measurements in `parity-20260909-ghent`. The published and measured
engine SHA-256 is `F55A523A3D9EE001DDF730DF590C0F2612C9E1B92CD8338753DDC749E0F29520`.
This does not establish general speed parity. Form processing, memory management,
and the other release gates remain open.

The current full application run includes these changes and confirms a median
70/46 milliseconds for the targeted 16-bit grayscale page. It also confirms that
the overall shared and difficult workloads remain slower; there is no established
batch-wide speed gain relative to the preceding application checkpoint.
The largest shared-set gap is now the first page of
`eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf`, at 1,048/687
milliseconds. It contains a 6,784 by 3,392, eight-bit DeviceCMYK Flate image
(object 1239), plus two smaller CMYK images. Profile its decoding and painting
before selecting the next change. All 5,392 repeated output images match their
respective retained images; this consistency check does not establish visual parity.

The Altona profile attributes 31.66 of 44.11 rendering seconds in its latter half
to area conversion, including 14.48 seconds directly in averaging and substantial
ICC conversion (`altona-large-profile-summary.json`). Its large image has 430,118
distinct colors among 23,011,328 pixels. A linear-scan cache probe records
1,519,180 misses with 4,096 entries and 860,096 with 16,384 entries. This probe
is supporting evidence only; renderer traversal differs.

Images with at least 1 MiB of sample data now use 16,384 conversion-cache entries;
smaller images retain 4,096. The increase is 108 KiB per converter, or 120 KiB
with matte alpha tracking. Full keys and alpha are still checked before reuse.
Four new cases match uncached 16-bit references under clipping, rotation,
reduction, and matte correction. All 674 corpus outputs remain identical
(`image-cache14-pixels.json`), and 3,783 engine tests, 352 app tests, and the
Release payload publish pass.

Two reversed-order 80-render pairs at size 1024, excluding the first 40, give
baseline/new medians of 1,034.055/897.156 and 1,081.929/912.226 milliseconds,
a 13% to 16% improvement on this Altona page. All 320 hashes match with zero
diagnostics. `image-cache14-summary.json` and `image-cache14-timing-*.csv` retain
the data in `parity-20260909-ghent`. The published and measured engine SHA-256 is
`7B0C7632C0A35E798C69327DAC55722FA553533CCEFF587347CBA1AE127D62D6`.
The earlier `cf976c1` application comparison predates this change. Averaging overhead,
remaining decoding costs, and broader parity gates remain open.

A profile of the larger-cache build attributes 16.13 of 41.35 rendering seconds
in its latter half directly to area averaging, with 27.91 seconds including
called conversion work (`altona-cache14-profile-summary.json`). Reusing horizontal
weights through a bounded stack buffer was tested and removed: two reversed-order
60-render pairs, excluding the first 30, produced baseline/candidate medians of
1,009.161/1,299.699 and 1,099.236/1,128.199 milliseconds. The wide timing ranges
do not support a precise regression estimate, but neither pair showed a gain.
All 240 output hashes match with zero diagnostics. `area-horizontal-summary.json`
and its timing CSVs retain this rejected experiment outside the repository.
The next averaging experiment should address accumulation or sample access
without adding a per-footprint weight buffer. No renderer change was retained.

Packed cache-key reads for eight-bit RGB and CMYK samples reduce repeated sample
addressing without changing conversion or adding storage. Four additional large
CMYK cases match uncached 16-bit references, including matte correction. All 674
corpus outputs match the larger-cache build (`packed-key-pixels.json`). The full
3,787 engine tests, 352 app tests, and Release payload publish pass.

Two reversed-order 60-render pairs on the Altona page at size 1024, excluding
the first 30, give baseline/new medians of 974.440/891.219 and
1,012.365/853.370 milliseconds. This is about 9% to 16% faster on this page;
the varying timings do not establish a broader application gain. All 240 hashes
match with zero diagnostics (`packed-key-summary.json` and timing CSVs).
The published and measured engine SHA-256 is
`B8DF5FDF2AE4FA8B17DA64D9B880EAC68B1854DE1A7CCF83E26003AB6064C334`.
The earlier `cf976c1` application comparison predates this change. Overall performance,
rendering fidelity, and interactive parity gates remain open.

The full `47edfd8` application comparison now includes both image-cache changes.
All 16 passes succeed, and all 5,392 output images match their respective retained
images. Shared measured render ranges are 15.463-16.514 seconds for the engine
and 13.169-14.757 for PDFium. Difficult ranges are 9.935-10.090 and 4.948-5.390.
The within-series median gaps remain about 12% shared and 98% difficult; do not
infer a speedup or regression by subtracting timings from the earlier session.

The largest difficult-page gaps are balloon JPEG 2000 (1,144/464 milliseconds),
Ghent combined test page 2 (619/145), and response-to-fiber-concerns (502/87).
The large Altona page remains a shared-workload outlier (1,288/708). Fresh-process
batch timings differ substantially from warmed single-page harness timings.
Investigate the actual application batch profile before choosing another isolated
averaging optimization. The application SHA-256 is
`79B0F821D244957745CE277E365E5FF368F96E2C8D6BAE962E8C2A338EE6C264`;
the engine SHA-256 remains the packed-read hash recorded above.

The actual difficult-page application batch profile completes all 74 pages
(`packed-key-app-broad.nettrace`). Restricting analysis to managed `CPU_TIME`
samples, coverage painting contributes 957 milliseconds directly, image area
conversion 793, and RGB-to-CMYK conversion 464. Rendering contributes 9,032
milliseconds inclusively across sampled threads. These values are profile
attribution, not independent wall-clock timings. Unmanaged intervals and thread
waits are excluded from `packed-key-app-managed-profile.json`; the unfiltered
summary must not be presented as CPU time. Coverage painting is the next engine
target to investigate, alongside the still-open fidelity and interactive gates.

Opaque CMYK shapes now fill fully covered interior runs in bulk, retaining the
original compositor for partial edges and the existing paths for masked clips,
soft masks, overprint, and knockout. Four new cases compare disjoint antialiased
shapes against the general transparent-backdrop compositor with group opacity.
All 674 corpus outputs remain identical, including 444 repeated difficult outputs
and 600 shared outputs (`ink-runs-pixels.json`). The 3,791 engine tests, 352 app
tests, and Release payload publish pass.

The focused Ghent page-2 timing is inconclusive: baseline/new medians are
260.259/237.8205 and 237.0305/238.197 milliseconds in two reversed-order
80-render pairs, excluding the first 40. All 320 hashes match with zero
diagnostics (`ink-runs-summary.json`). In the actual difficult-page application
batch, two measured reversed-order pairs after warmup give baseline/new render
times of 10,278/10,079 and 10,155/10,053 milliseconds, about 1% to 2% lower.
Warmup favored the baseline, so this is modest evidence rather than an established
general speed gain. All six batch passes succeed (`ink-runs-ab-results.csv`).
The published and measured engine SHA-256 is
`5D2EE68CEB19D07F8E4F114EC4EDE9C24CB6A7784557AB01898E30A5801D4CB8`.
The full PDFium comparison remains the earlier `47edfd8` checkpoint; overall
performance, fidelity, and interactive parity remain open.

Visual inspection of Ghent 16.10 and 16.11 distinguishes effects from the pages'
remaining background and text differences. Comparing each actual effect against
its embedded reference image within the same renderer gives lower mean RGB error
for the engine in all nine patches. Engine/PDFium errors are: drop shadow
1.637/2.273, inner shadow 1.739/2.629, outer glow 1.589/2.718, inner glow
1.589/4.511, bevel/emboss 1.445/4.869, satin 1.246/1.794, basic feather
0.881/5.496, directional feather 1.351/3.879, and gradient feather 0.674/2.470.

`ghent-text-softmask-reference-method.json` records all nine crop regions and
measurements, using the same horizontal -3 to 3 and vertical 349 to 355 pixel
alignment search for both renderers at size 2048. The inputs are the retained
`ink-runs-ab-Broad-1-Engine` and `payload-cmyk-Broad-0-PDFium` images. These are
local effect-consistency checks, not absolute color measurements: errors shared
by an effect and its reference image can cancel. The effects should not be
changed merely to reduce whole-page differences from PDFium. Background color,
registration-color text, font rasterization, and other pages remain open.
No renderer code changed during this visual review.

The FAccT paper exposes a separate, confirmed Type 1 outline regression: letters
have triangular gaps because `setcurrentpoint` prematurely finishes the contour
after flex. [Adobe's Type 1 specification, section 6.4](https://adobe-type-tools.github.io/font-tech-notes/pdfs/T1_SPEC.pdf)
defines this as setting the current point without a moveto. Removing the contour
finish preserves the following path segments. The extended flex regression test
fails before the change with two contours instead of one, then passes after it.
Visual inspection confirms the broken letters are repaired on FAccT page 2.

All 674 corpus renders succeed. Ten outputs change, all with lower mean RGB
error against PDFium; the other 664 remain identical. The three difficult-set
FAccT pages improve from 5.943/7.436/6.871 to 4.289/5.283/4.993 mean error.
Seven shared outputs also improve (`type1-contour-pixels.json`). Residual font
rasterization differences remain; this does not establish overall text parity.
All 3,792 engine tests, 352 app tests, and the Release payload publish pass.
The engine SHA-256 is
`3B996C604C07D9D2AAA3190080805ACC1F98925B31DA78B06F118DCFB95604AE`.

Bundled font fallback now reads serif, fixed-width, italic, and forced-bold
descriptor flags. Exact standard fonts retain precedence, and known Arial,
Helvetica, Verdana, and Trebuchet families remain proportional despite misleading
fixed-width flags. Eleven cases cover descriptor traits, exact standard fonts,
and these name-based safeguards. The initial six descriptor cases failed before
the change. The final 3,803 engine tests, 352 app tests, and Release publish pass.

The unembedded Bembo title in `210260.pdf` now uses a serif fallback; its difficult
pages 2 and 3 improve from 6.497/5.602 to 5.131/4.972 mean RGB error. Across all
674 outputs, eight improve against PDFium, 665 remain identical, and one has a
small increase: `303226.pdf` changes from 6.408115 to 6.412256. Its MIonic font
is explicitly marked serif, and visual inspection confirms the corrected family;
glyph width and spacing still differ from PDFium. The width-fitting follow-up below
improves this page beyond both earlier builds; residual font differences remain open.
The first candidate's Trebuchet regression is removed, and that page is identical
to the baseline in the final run (`font-traits-v3-pixels.json`). The engine hash is
`9480652F6A525BBCF0BAC1878D3F5C8FE4E88A654B57342267AC323325DEA049`.

Bundled substitutes for nonstandard simple fonts now fit their outlines horizontally
to explicit positive PDF widths. Standard metrics, embedded and resolved outlines,
vertical and composite fonts, and absent or nonpositive widths retain their previous
behavior. Fitted outlines are cached by source character code, so two codes mapping
to the same glyph can retain different declared widths. Eight tests cover narrower
and wider glyphs, repeated access, remapping, standard aliases, nonpositive widths,
and embedded and resolved font precedence.

All 674 application renders succeed. All 22 changed images have lower mean RGB
error against PDFium, and 652 remain identical (`font-width-pixels.json`). The
MIonic page `303226.pdf` improves from 6.412256 to 4.7236; visual inspection confirms
better letter spacing, with weight and rasterization differences remaining. Bembo
pages 2 and 3 improve from 5.131/4.972 to 4.837/3.372. This is fidelity evidence,
not a timing measurement or overall text-parity claim. All 3,811 engine tests,
352 app tests, and the Release publish pass. The Release payload is
`parity-20260909-ghent/payload-font-width`, with engine SHA-256
`254BC52B289DEBCDEAB3FD1F7C49681A90B625B29A912E1B9B9E34AB8E7501B4`.

A four-channel Vector256 accumulation experiment for converted-image area
averaging was rejected. Two reversed-order pairs on the large Altona image at
size 1024 used 60 renders per process, excluding the first 30. Scalar/vector
medians were 896.026/974.740 and 834.264/963.722 ms. All 240 outputs have SHA-256
`3B05C5B420F741F18129C2017D50600B304C530D0ECA84E6783AD2F09C165364`
and zero diagnostics. The 9% to 16% slowdown is recorded in
`parity-20260909-ghent/area-vector-summary.json` and the two timing CSVs.
The experiment is removed; renderer source matches the verified width-fitting
checkpoint. No performance gain is claimed from this attempt.

A bounded converted-row cache was also rejected. It retained at most 1 MiB of
pixel samples per converter, avoiding repeated conversion across neighboring
footprints. On the same Altona workload, two reversed-order pairs of 60 renders
(first 30 excluded) produced scalar/cache medians of 893.599/866.656 and
874.098/889.435 ms. All 240 hashes match the scalar reference, but the ordering
reverses the small timing benefit. Extra row storage is not retained. Results
are in `parity-20260909-ghent/area-rows-summary.json` and its two timing CSVs.
Three independent CMYK area-average checks cover fractional dimensions and
both sides of the attempted cache limit; these remain as regression coverage.

A fresh balloon-page sampled-thread-time trace attributes 13.784 of 18.774
seconds of sampled CPU time to JPEG 2000 decoding, including 2.149 seconds
exclusive to sample output. Only intervals containing `CPU_TIME` are counted,
and the first half of the trace is excluded (`balloon-width-cpu-summary.json`).
Direct 8-bit and 16-bit rows now clamp signed values before adding their output
bias, preserving bounds without 64-bit arithmetic. A separate arithmetic check
covers 1,110 signed-limit and clamp-boundary combinations across shifts 0 to 30.

Two reversed-order pairs on the balloon page at size 2048 use 80 renders per
process, excluding the first 40. Baseline/changed medians are 593.033/578.208
and 588.672/576.494 ms, a small 2% to 2.5% page-level improvement. All 320 hashes
are `391277DC61978A0AB08C8F51C2CE6611DDDF266110525B51228FCC8AD09AD18F`,
with zero diagnostics (`jp2-int-clamp-summary.json` and its timing CSVs).
These profiling files are in `parity-20260909-ghent`. The change adds no buffers.
The full application timings above predate this optimization; overall parity
remains open. All 3,814 engine tests, 352 app tests, and the Release publish pass.
All 674 corpus images remain pixel-identical (`jp2-int-clamp-pixels.json`).
The payload is `parity-20260909-ghent/payload-jp2-int-clamp`, with engine SHA-256
`0CDAB4B817FED5DFB64B01A2CA7600CD7895BFDF9D54B02424CF3C2C0444C61E`.

The remaining maintenance brochure port, `4cd5096`, has been reviewed but is
not applied. The maintenance PDF has 50 pages with unchanged page dimensions,
18 fields, 22 widgets, and unchanged field values. Extracted text changes only
the applicable 1.8.3 labels to the 1.8 series, retaining historical result labels.
All 50 pages were rendered at 72 DPI and visually inspected for text overlap
and clipping, including page 40. Page 46 has doubled radio-button outlines in
both the current and maintenance copies; this is an existing visual issue.
Rendered evidence is in `review-20260909/brochure-maintenance` under the scratch
root. The reviewed maintenance PDF SHA-256 is
`4C3BBD6FB871F7045154FCADCEACDC20D8B53F589D561BD7E5503595AAEE79A3`.
It remains a 1.8 brochure and does not describe the 1.9 rendering architecture.
The binary replacement is pending clarification of the atomic-tool-only rule
against the separate verified binary-write procedure. No PDF was overwritten.

A fresh 80-render profile of response-to-fiber-concerns attributes 5.419 of
11.127 sampled CPU seconds to image painting after excluding the first half
of the trace and counting only `CPU_TIME` intervals. Its RGB image has a
fully opaque image soft mask inside a CMYK page group. Fully opaque mask
samples now permit the existing direct ink write; partial samples retain
normal compositing, and each mask sample is reused for matte conversion.
The trace is `parity-20260909-ghent/fiber-clamp-current.speedscope.json`.

Two reversed-order pairs of 80 renders at size 2048, excluding the first 40
per process, produce baseline/changed medians of 238.933/207.043 and
241.906/207.267 ms. This is a 13% to 14% page-level improvement without new
buffers. All 320 hashes are
`87E83A736E5D6C2D350A6CB84AAE1AD4597855F7E0ECC5E512DE76DF4918262F`,
with zero diagnostics. Timing CSVs and `opaque-image-mask-summary.json` are
in `parity-20260909-ghent`. All 3,818 engine tests, 352 app tests, and the
Release publish pass. All 674 corpus outputs remain pixel-identical to the
sample-clamping checkpoint (`review-20260909/opaque-image-mask-pixels.json`).
The payload is `parity-20260909-ghent/payload-opaque-image-mask`, with engine
SHA-256 `CC0D65060B95F08C0F4E5D05B3FB70F1F8A4F90EC0642D81ABDA4030AAB042F7`.
The earlier whole-application paired timings do not include this optimization;
overall speed and fidelity parity remain open.

A fresh 60-render profile of scan `42828.0001.001.pdf` attributes 4.809 of
11.639 sampled CPU seconds to area conversion and 4.777 seconds to JBIG2
generic-region line decoding. The first half is excluded and only `CPU_TIME`
intervals are counted (`parity-20260909-ghent/scan-42828-cpu-summary.json`).
Binary area conversion now reuses each row's exact vertical bounds across its
columns. Each converter owns its current row state; no image-sized buffer is
added, and integer coverage and rounding are unchanged.

Two reversed-order pairs at size 2048 use 60 renders per process and exclude
the first 30. Baseline/changed medians are 390.602/355.997 and 377.123/354.294
ms, a 6% to 9% page-level gain. All 240 hashes are
`A71BE1DB46524A81F558A807DE137923EF838F0E932F048CD3A3E96FAA7EA0E4`,
with zero diagnostics. The two timing CSVs and `binary-row-bounds-summary.json`
are in `parity-20260909-ghent`. All 3,818 engine tests, 352 app tests, and the
Release publish pass. All 674 images remain pixel-identical to the opaque-mask
checkpoint (`review-20260909/binary-row-bounds-pixels.json`). The payload is
`parity-20260909-ghent/payload-binary-row-bounds`, with engine SHA-256
`872E864EE61B0E4365487123BD7FB37EBBD6935A79984E5E6D3815B4358857E7`.
The current paired application table describes `5bddb6c` before this row-bound
optimization. Overall parity remains open, including the JBIG2 decoding gap.

JBIG2 arithmetic contexts now store the predicted bit alongside the seven-bit
probability state in one byte. State updates preserve the prediction, toggles
preserve the state, and copies remain independent. Two regression tests cover
all 128 stored state values, alternating predictions, truncation, and copying.
The standalone `ContextAllocation` probe constructs 100 contexts after warmup:
each 65,536-entry context allocates 65,592 bytes instead of 131,160 bytes.
This halves context storage, not total decoder or application memory.

On the same scan, two reversed-order pairs of 60 renders at size 2048 exclude
the first 30 per process. Baseline/changed medians are 369.615/360.868 and
365.914/362.119 ms, a small 1% to 2.4% page-level gain. All 240 hashes remain
`A71BE1DB46524A81F558A807DE137923EF838F0E932F048CD3A3E96FAA7EA0E4`,
with zero diagnostics. Timing and allocation evidence is retained in
`parity-20260909-ghent/jbig2-packed-context-summary.json` and the timing CSVs.
All 3,820 engine tests, 352 app tests, and the Release publish pass. All 674
corpus outputs match the row-bound checkpoint exactly
(`review-20260909/jbig2-packed-context-pixels.json`). The payload is
`parity-20260909-ghent/payload-jbig2-packed-context`, with engine SHA-256
`3A10201A3E3E83FCE67E43F1C9E19801DF56539A0609B9B09F7FAEBE295B861E`.
The current paired application table predates this context change; overall
speed, memory, visual, and interactive parity still require verification.

A contiguous JBIG2 probability-table experiment was rejected. It preserved
all 47 rows and 188 values, and reused the selected row during arithmetic
decoding. Two reversed-order pairs on the scan, with 60 renders per process
and the first 30 excluded, produced baseline/candidate medians of
333.706/332.104 and 345.048/355.548 ms. The small first-pair improvement
reversed to a roughly 3% slowdown. All 240 hashes matched, with zero
diagnostics, but the runtime change was removed. Decoder source again matches
the verified compact-context checkpoint. The candidate build and timing CSVs
remain in `parity-20260909-ghent/jbig2-flat-table*` for reference.

A fresh comparison of all 674 current images is retained in
`review-20260909/jbig2-context-fidelity.json`. Mean pixel differences rank
investigation targets, not correctness. The two largest shared differences,
`pdfa2-6-1-13-bfo-t04-fail.pdf` and `pdfa2-6-1-13-bfo-t06-pass.pdf`, each
contain equal and opposite extreme translations followed by a red rectangle.
The engine preserves the cancellation and rectangle; PDFium renders blank.
Do not remove this content merely to reduce pixel differences.

The next shared case is a confirmed missing-content defect:
`preservation/openpreserve-format-corpus/govdocs1-error-pdfs/error_set_1/447403.pdf`.
The engine renders only a small terrain image instead of the full earthquake
map, charts, and labels. Its single Flate content stream has 7,211,743 encoded
bytes and expands completely to 171,848,462 bytes. A bounded chunked zlib
inspection reached the final text operators and closing graphics-state restore.
`PdfPageContentReader` requests the 64 MiB content limit; compatibility Flate
decoding returns that bounded prefix, and the content reader trims it further
to append a separator. The rest is lost. The profiled render reports zero
diagnostics, so successful batch status does not prove complete page output.
The current shared mean RGB error is 42.4904. Bounded large-content processing
and an explicit diagnostic whenever content is truncated remain required;
raising the cap alone would not address the memory goal or silent truncation.

A streaming layout probe now validates all 190 raw inline-image sample lengths
against their actual EI boundaries, reaching the complete decoded stream with
1 MiB reads. Image samples account for 160,728,260 bytes; the largest individual
image contains 9,450,000 bytes. The remaining 11,120,202 bytes contain content
syntax and image dictionaries. Results, offsets, and source/decoded hashes are
in `review-20260909/large-content-layout.json`. This supports incremental
instruction processing without increasing the existing per-image limit. Some
images omit whitespace before EI, so the existing compatibility recovery must
remain available across buffer boundaries.

The rendering path accepts an instruction enumerable, but its page reader first
materializes a list and the page-instruction cache limits entries to 32 without
a byte budget. Inline-image instructions copy their payloads, and rendering
copies them again into PdfStream objects. A complete large-page fix must avoid
retaining the entire decoded image set through this cache. These are verified
implementation constraints, not a completed streaming implementation or a new
rendering result.

The internal content reader now supports resumable prefixes and bounded stream
enumeration. Incomplete operands, comments, operators, and inline images remain
in the pending buffer; completed instructions keep their original global offsets.
The original map passes a direct streaming parser probe: 734,227 instructions,
all 190 images and 160,728,260 sample bytes, ending at Q at byte 171,848,460.
The scratch probe is `parity-20260909-ghent/LargeContent.csproj`. Its single
observed run took 989.887 ms and peaked at 241.863 MiB process working set;
these are parser-only observations, not application performance claims.
Six new parser tests cover every split position in representative content,
binary image boundaries, repeated buffer growth, global offsets, limits, and
cancellation. All 3,826 engine tests pass. Renderer integration, decoded-stream
filter handling, cache retention, and explicit truncation diagnostics remain
unfinished; this change alone does not restore the rendered map.

Renderer integration now restores the complete earthquake map. Large pages
bypass the materialized instruction cache and use sequential content streams,
preserving pending operands and graphics state across the stream array. Raw
content and ordinary Flate streams decode incrementally; other filter pipelines
retain bounded decoding and report failures or streaming limits as diagnostics
in compatibility mode. The public materialized editing API retains its limit.
Three additional tests verify rendering beyond 64 MiB in strict and compatibility
modes, operands crossing content-stream boundaries, and a streaming decode diagnostic.
All 3,829 engine tests and 352 app tests pass, and the Release payload publishes.

The new payload is `parity-20260909-ghent/payload-streaming-content`, with engine
SHA-256 `FCB4716668ED85050A3E0A40CB24FE02C13363FD52BBDB11F4D24C5760A44466`.
All 674 corpus outputs succeed. `review-20260909/streaming-content-pixels.json`
records 673 identical images and only the repaired map changed. Its mean RGB
difference from PDFium falls from 42.4904 to 5.0726; the fraction of pixels with
maximum channel error above 32 falls from 34.47% to 6.27%. Visual inspection
confirms the maps, charts, labels, and photos are restored. Remaining pixel
differences still need review. The direct render takes 2,703.845 ms with zero
diagnostics; the old 708.534 ms observation rendered incomplete content and is
not a valid full-page speed baseline. The earlier opaque-mask timing and memory
checkpoint predates this repair and is superseded by the paired refresh below.

The paired checkpoint has now been refreshed against the retained PDFium app.
All 16 passes and all 5,392 image comparisons pass. The table at the top uses
these new medians and ranges. Shared render time remains about 12.4% slower,
with median peak working set about 24.8% lower. Difficult render time remains
about 92.6% slower, with median peak working set about 0.8% higher. These are
batch observations, not an interactive memory or overall parity claim.
The repaired map itself measures 2,452 versus 2,251 ms median per page.
The largest difficult-corpus gaps remain balloon JPEG 2000 (1,148 versus 513 ms),
Ghent ALL page 2 (653 versus 146 ms), and mipeng poster (975 versus 526 ms).
Shared Altona's large technical page remains 1,131 versus 766 ms. These measured
gaps guide the next work; the map repair does not complete the parity goal.

A current Ghent page-2 CPU profile is retained as
`parity-20260909-ghent/ghent-streaming.nettrace` and the matching speedscope and
CPU summary files. Counting only CPU_TIME intervals after the first half gives
8,300.264 ms sampled CPU time. Major exclusive costs include GC polling
(1,118.433 ms), RenderForm (961.992 ms), SetInkPixel (885.296 ms), coverage
painting (640.296 ms), and memory copying (529.526 ms). These samples identify
where to investigate; they are not independent elapsed-time measurements.

An unchanged-pixel shortcut in non-isolated CMYK group compositing was tested
and removed. Two reversed-order pairs of 60 renders, excluding the first 30
per process, produced baseline/experiment medians of 225.208/217.288 and
217.227/220.130 ms. The first improvement reversed in the second pair.
All 240 pixel hashes matched, with zero diagnostics. Evidence remains in
`parity-20260909-ghent/unchanged-group*`; renderer source matches the validated
large-content checkpoint. No runtime optimization was retained from this test.

A separate `AllocationProfile.csproj` probe initially suggested about 50.6 MB
of managed allocation per steady Ghent page-2 render. Its assembly resolution
was subsequently found to select another scratch DLL; the hash-verified
measurements below supersede that initial observation.
`ghent-allocations.nettrace` and `ghent-allocation-types.json` provide a separate
allocation-tick sample over thirty fresh-document renders. Byte arrays dominate
the sampled allocation weights, followed by renderer Point arrays and double
arrays. The type sample includes document opening and is statistical, so its
weights must not be treated as exact render-only byte totals. Allocation call
stacks are the next investigation target before choosing a buffer or cache change.

Allocation stacks are now available in `ghent-allocation-stacks.json`, decoded
from the existing trace with `AllocationStacks.csproj`. All 5,505 sampled events
in the retained half have stacks. The largest byte-array samples belong to the
document's input copy and the rendered output buffer. Those have ownership
requirements and are not automatically removable. JPEG decoding and flattened
glyph Point arrays follow. The trace includes opening the document, whereas the
separate allocation harness measures only Render.

An ICC intent-table sharing experiment passed 173 focused tests, including a
synthetic allocation and color-equivalence check, but was removed after the
real-page allocation probe showed little benefit. Hash-verified median allocated
bytes over the last ten of twenty renders were 50,730,356/50,624,580 on Ghent
and 154,102,432/154,102,688 on Altona (baseline/experiment). All 80 page hashes
matched within their workload, with zero diagnostics. The result is too small
to pursue as the explanation for Ghent's allocation pressure. Production source
and tests again match the validated large-content checkpoint.

The allocation harness now searches explicit HintPath references first and uses
distinct output/intermediate directories. MSBuild's candidate-assembly search
had selected another DLL from the scratch directory despite the intended hint.
Only `icc-shared-verified-allocation.csv` and its summary are valid comparisons:
baseline engine hash is `D7B83C01599F8A074C3FA4FA71B1C80ECABBB4DA5867725B27E07F185C0021D0`,
experiment hash is `9B9E2D3EF12B3827C5A1FDEDBD0585ADE0D83DB31EB1BA8CD1A577FF1E812664`.
The earlier `icc-shared-allocation.csv` and `icc-shared-altona-allocation.csv`
are invalid for before/after claims and are retained only as investigation history.

A per-glyph contour scratch-buffer experiment was also removed. On Ghent page 2,
average Render allocations over the last ten of twenty passes decreased from
50,616,240 to 50,003,197 bytes (1.2%). Two reversed-order timing pairs of sixty
renders, excluding the first thirty per process, gave baseline/experiment medians
of 227.432/239.238 and 229.234/229.687 ms. The small allocation saving did not
produce a timing benefit. All 280 rendered pixel hashes matched with zero
diagnostics. The experiment built successfully; production source was then
verified identical to the validated baseline, so full tests were not repeated.
Evidence is in `parity-20260909-ghent/glyph-scratch-allocation.csv` and
`glyph-scratch-timing.csv`. The measured experiment engine hash was
`BF0F9C678C9F4573E68FEBE71A9A200081E6468F671CA14B421AFACF8F823130`;
both allocation harness DLL copies were verified against their intended builds.

### Direct CMYK JPEG output

JPEGs with four horizontally full-resolution components and no color transform
now interleave component rows directly. Vertical sampling still uses the existing
row mapping; transformed and horizontally subsampled images retain the general
path. Added sixteen odd-size baseline/progressive sampling cases across all four
reductions. All 133 focused JPEG tests passed. The full run first hit the ICC
destination-curve allocation assertion (7,752 unexpected bytes); that unrelated
test passed alone and the complete rerun passed all 3,845 engine tests. All 352
app tests and the Release payload publish passed.

Two reversed-order pairs of sixty Ghent page-2 renders, excluding the first
thirty per process, produced baseline/new medians of 227.496/224.481 and
221.642/220.547 ms (0.5% to 1.3% lower). All 240 hashes matched with zero
diagnostics. This is a small page-specific observation, not overall speed parity.
The 74 difficult and 600 shared corpus pages all completed and matched the
previous validated output pixel for pixel, including the complete large map.
Evidence remains under `C:/Users/steve/kp-bench-render`: timing and test logs in
`parity-20260909-ghent/jpeg-direct*`, payload in `payload-jpeg-direct` beneath
that directory, and corpus results in `review-20260909/jpeg-direct*`.
The measured and published engine DLL hash is
`F6DC0FC8830430DD744E588BDC13C0AA802E446075C8E1282BC1F95DE9B538D1`.

Extending the RGB row-based group interpolation loop to uniformly opaque CMYK
surfaces was tested and removed. Reversed-order Ghent pairs gave baseline/test
medians of 224.539/241.205 and 225.142/225.878 ms after thirty warmup renders
per process. All 240 pixel hashes matched with zero diagnostics, but there was
no timing benefit. The renderer was verified identical to the validated CMYK
JPEG checkpoint after removal; no additional full-suite run was needed.
Evidence is in `parity-20260909-ghent/opaque-group-rows*`; the tested engine hash
was `F34059A9BF3FD79DBD79C266884F82F57BD54209D161AD4056B1C7A4748686DA`.

### Vectorized opaque CMYK blending

A temporary diagnostic build counted 903,961 SetInkPixel calls on Ghent page 2.
Partial-opacity normal paint over an opaque backdrop without overprint accounted
for 296,871 calls; transparent backdrops accounted for another 296,457 calls.
The diagnostic counters were removed before production measurements. The probe's
render hash matched the baseline; its timing is not a performance measurement.

The opaque normal-blend case now processes four channels together when AVX and
SSE2 are available, retaining the existing scalar fallback. Operation order,
double precision, clamping, and nearest-even rounding are preserved. Standalone
scalar/vector comparisons passed 17,039,360 cases, including subnormal opacity.
Nine additional tests check every byte pair with distinct channel values and
representative opacity boundaries; all pass with intrinsics enabled and disabled.
All 44 focused rendering tests, 3,854 engine tests, 352 app tests, and the Release
payload publish passed. All 674 corpus pages match the preceding JPEG checkpoint.

Two reversed-order pairs of sixty Ghent renders, excluding the first thirty,
gave baseline/new medians of 287.546/285.415 and 286.498/282.578 ms, a 0.7% to
1.4% reduction. All 240 hashes matched with zero diagnostics. Session timings
are higher than the earlier JPEG session, so cross-session comparisons are not
valid. The standalone blend is roughly twice as fast; the much smaller measured
page gain is the relevant rendering result and does not establish overall parity.
Evidence under `C:/Users/steve/kp-bench-render/parity-20260909-ghent` includes
`ink-blend-probe-ghent-result.txt`, `ink-vector-packed-check-results.json`,
`ink-vector-timing.csv`, the test logs, and `payload-ink-vector`.
Corpus evidence is in `review-20260909/ink-vector*` under the benchmark root.
Measured and published engine SHA-256:
`AC9B6CC44CCE66B469319FC889A758DE415539B5D234EFCD320ABFDEC8A143A6`.

### Transparent CMYK backdrop shortcut

Paint over a zero-alpha CMYK backdrop now copies native sample bytes when source
opacity is between 1e-300 and 1. The threshold keeps nonzero sample products away
from underflow, so normalization cannot change the final rounded byte. Smaller
opacity retains the original arithmetic. An independent numeric check covered
26,426,880 sample/opacity combinations over all byte values, exponent boundaries,
threshold neighbors, and deterministic random floating-point values. It found
5,354 differing tiny-opacity cases below the threshold, confirming the need for
the fallback, and no differing cases in the shortcut's range.

All 35 focused rendering tests, 3,854 engine tests, 352 app tests, and the Release
payload publish passed. All 674 corpus pages match the preceding vector-blend
checkpoint pixel for pixel. Two reversed-order Ghent pairs, sixty renders each
with thirty warmups, produced baseline/new medians of 288.228/283.514 and
288.504/286.234 ms (0.8% to 1.6% lower). All 240 hashes matched with zero
diagnostics. Overall parity remains open.

Evidence is under `C:/Users/steve/kp-bench-render`: numeric checks, timings, test
logs, and `payload-transparent-ink` in `parity-20260909-ghent`, and corpus output
in `review-20260909/transparent-ink*`. The measured and published engine hash is
`89AD727550B0CD6F12D2030D3DE20653B33D083286A5C4A33DC7F4432DBEA0D0`.

The refreshed whole-application comparison completed sixteen passes with no
failures. All 5,392 images match their respective engine/PDFium baselines.
The largest difficult-page render gaps are balloon_a1b_jp2k (1,158/446 ms),
Ghent ALL page 2 (659/140 ms), and Ghent ALL page 1 (530/124 ms). Shared leaders
are the complete large map (2,194/1,824 ms), Altona measure (398/72 ms), and the
balloon document (486/167 ms). Values are paired median engine/PDFium times.
These are the next performance priorities; microbenchmark improvements have
not closed the overall gap. The report script uses explicit UTF-8 CSV decoding
for corpus filenames. Its first analysis reached the page-ranking step after
image verification, then failed on default Windows decoding; the corrected
analysis completed and repeated all image checks successfully.

Primary-page rendering now retains one engine session per viewer pane across
zoom sizes. Document switches, immutable-document invalidation, view-mode
changes, and pane cache flushes release it. Bitmap invalidation advances a
document revision so a subsequent render cannot reuse the old snapshot.
Background render tasks retain independent session ownership.

The engine-level zoom probe uses RenderInto, avoiding rendered-page cache hits.
On balloon_a1b_jp2k.pdf, fresh/reused/reused/fresh medians are
771.255/190.131/202.389/750.554 ms across three sizes. All 48 images match, with
no diagnostics. Three document probes retain an additional 28 to 36 MiB of
managed data, which becomes collectible after releasing the renderer. These
are not interactive latency or working-set measurements. Evidence is in
`parity-20260909-ghent/session-reuse-summary.json` and
`session-memory-summary.json` under the local benchmark root.

All 356 app tests and the Release payload publish pass after integration.
Four new cases cover snapshot reuse, painted zoom output, independent pixel
ownership, file rewrites, explicit clearing, and switching files. The existing
boundary test now recognizes the retained primary path. Engine source is
unchanged; the earlier 3,854-test engine checkpoint remains the engine test
evidence. The new payload is `payload-primary-reuse`; interactive first-page,
scrolling, zoom, and edit/reload validation remain open.

The published application's own rendering boundary confirms the reuse gain on
the balloon document: fresh/reused/reused/fresh medians are
736.186/182.069/183.422/695.986 ms. All 48 renders match across the three zoom
sizes with no diagnostics. This invokes the retained primary helper and fresh
session through the published assembly, including the application font resolver
and render parallelism. It does not measure WPF presentation or compare with
PDFium. The measured assemblies and limits are recorded in
`parity-20260909-ghent/app-reuse-summary.json`.

The current September 29 application boundary was then sampled for process
memory while cycling 2048, 2304, and 2560-pixel balloon renders. Two fresh
session processes peaked at 289.8 MiB working set, while two retained-session
processes peaked at 289.8 and 289.9 MiB. After the first render, last-nine
medians were 38.079 and 36.011 milliseconds for fresh sessions versus 37.169
and 37.576 milliseconds with reuse. Per-size medians ranged from 31.204 to
46.578 milliseconds. Every size retained one matching pixel hash across all
four processes. Current shared decoded-image caching makes later fresh sessions
within a process nearly as fast as retained sessions on this document, without
raising the sampled peak. This headless application-boundary result does not
measure WPF presentation or visible scrolling. Evidence is under
`C:/Users/steve/kp-bench-render/interactive-memory-20260929`.

Continuous base rendering, continuous zoom sharpening, and secondary tiles now
reuse sessions through exclusive task leases. Each pane retains at most one
idle background session; overlapping tasks retain separate active sessions.
Requests capture the document path and revision on the UI thread. Cache clears
invalidate outstanding requests, and late returns from those requests are
disposed instead of becoming the current cached session. Opening and rendering
never hold the cache lock. Cancellation reaches both lease acquisition and
the engine render operation.

All 362 app tests and the Release payload publish pass. Six new cases cover
exclusive ownership, idle reuse, late requests and returns after invalidation,
one-idle-session retention, and cancellation. The payload and build log are
`parity-20260909-ghent/payload-background-reuse` and
`background-reuse-publish.log`. Background interactive speed, screen
presentation, and working-set measurements remain open; the earlier primary
rendering timings do not prove those gates.

The published background lease boundary takes 254.567/75.978/76.413/253.301 ms
in a fresh/reused/reused/fresh balloon-page comparison. Each run cycles three
continuous-style width/height limits and returns its lease after every render.
All 48 outputs match with no diagnostics. Measurements and the assembly hash
are in `parity-20260909-ghent/background-reuse-summary.json`. This sequential
boundary probe excludes concurrent task scheduling and WPF presentation;
interactive and PDFium parity gates remain open.

A coordinated two-worker regression now acquires separate leases, replaces
the document while both workers are outstanding, then lets both render in
parallel. Their original painted pixels remain identical and independently
owned. Late returns leave the replacement document's idle session intact.
All 363 app tests pass. This verifies the cache race without claiming UI
scheduling or presentation coverage; no runtime code changed in this check.

Application bitmap sizing now applies the measured legacy decimal coordinate
conversion to page boxes, preserving accurate engine parsing and rendering
geometry. All 674 published application outputs match PDFium dimensions.
Only hisn13056.pdf and UA1_Tpdf-G1_F01.pdf change from the earlier engine
baseline; the remaining 672 decoded images are identical. Every batch row
reports OK. Outputs are under `review-20260909/legacy-geometry-*` in the local
benchmark root; the payload and test/build logs use `legacy-geometry` in
`parity-20260909-ghent`.

All 370 app tests and the Release publish pass. Seven coordinate cases match
direct native probes. The scaled-page test for height 301.999999 now expects
150 pixels at half scale: the retained native library reports 301.9999694824219
points, confirming that the former 151-pixel expectation used a direct cast
instead of the legacy parser. Geometry compatibility is verified at the corpus
sizes; overall visual, speed, and interactive parity remain open.

### Pattern text fill checkpoint (2026-09-09)

PatternTextInsideText.pdf now paints its red text pattern inside the blue
outlined glyphs. Across the 674 Broad and Shared pages, only this image changes;
the other 673 remain pixel-identical and every output retains its dimensions.
The published app renders all batch rows successfully. Local evidence is under
`C:\Users\steve\kp-bench-render\review-20260909\pattern-text-*`, with the
payload under `parity-20260909-ghent/payload-pattern-text`.

Both new pattern clipping tests pass. All 3,856 engine tests pass with test
parallelism disabled, and all 370 app tests pass. The parallel engine suite
exposes a clip-buffer allocation failure that passes alone and in the serial
suite. The subsequent pool investigation below identifies the underlying cause.
Overall visual, speed, and interactive parity remain open.

### Scratch pool eviction checkpoint (2026-09-09)

The 64-item limit previously evicted the largest idle buffer even when the byte
budget had room. Small idle arrays could therefore displace nearly all later
large buffers. Count-limit eviction now removes the smallest idle buffer;
byte-budget eviction still removes the largest. Both limits are unchanged.
A deterministic regression verifies reuse of 32 large buffers after filling the
pool with 64 tiny buffers. Allocation-sensitive surface tests also use the
existing isolated collection to avoid unrelated concurrent pool traffic.

All 3,857 engine tests pass in the normal parallel suite, all 370 app tests pass,
and Release publish succeeds. This corrects buffer reuse, without establishing
a whole-page speed improvement or overall engine parity.
All 674 Broad and Shared renders are pixel-identical to the pattern-text
checkpoint, with unchanged dimensions and OK batch rows. Evidence is under
`review-20260909/pool-eviction-*` and `parity-20260909-ghent/payload-pool-eviction`
in the local benchmark root.

### Unknown image filter checkpoint (2026-09-09)

UnknownFilter-ImageXObject.pdf no longer paints encoded bytes as a noisy black
rectangle. Unknown image filters produce a diagnostic; general-stream recovery
still permits unknown-filter pass-through. Both direct and array filter forms
have rendering regressions that failed before the fix and now pass.

All 3,859 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 corpus rows report OK with unchanged output dimensions. Only this
malformed-image page changes; the other 673 images remain pixel-identical.
The remaining red text is visually consistent with the native reference,
although the complete images are not byte-identical. Evidence is under
`review-20260909/unknown-image-*` and `parity-20260909-ghent/payload-unknown-image`
in the local benchmark root. Broader visual and performance gates remain open.

### Installed standard-font aliases checkpoint (2026-09-09)

The desktop resolver now maps Helvetica and Times aliases to installed Arial
and Times New Roman faces, preserving styles and exact requested-family
precedence. Missing installed families still use the engine's bundled fallback.
No additional font data is shipped. Engine source is unchanged.

All 33 standalone resolver checks and 370 app tests pass; Release publish passes.
All 674 corpus rows report OK and retain the same dimensions. Of 220 changed
pages, 219 have lower mean absolute RGB error against PDFium. The other 454
pages are pixel-identical. The one higher score changes only nine pixels on a
horizontal line in 507618.pdf, increasing the total absolute channel error by
24. OverlappingGlyphClipping.pdf drops from 16.9909 to 0.2237 mean error, and
PatternTextInsideText.pdf drops from 28.8898 to 3.0027. These scores support
the specific font-selection improvement, not overall visual parity.

Evidence is under `standard-latin-probe-20260909/corpus-comparison.json` and
`review-20260909/standard-alias-*` in the local benchmark root. The payload is
`parity-20260909-ghent/payload-standard-alias`. Earlier paired speed results
predate this desktop resolver change.

### Pattern text stroke checkpoint (2026-09-09)

Glyph strokes now honor the selected pattern, preserving ordinary solid-stroke
rendering. Both new regressions failed with zero pattern-colored pixels before
the fix and pass afterward. A four-page native comparison covers stroke-only,
fill-and-stroke, and both text-clipping modes; visual inspection confirms the
pattern, fill, and subsequent clipped artwork in each mode.

All 3,861 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 Broad and Shared pages remain pixel-identical to the installed-font
alias checkpoint, with unchanged dimensions and OK batch rows. The corpus
does not exercise this missing stroke behavior; the dedicated comparison does.
Evidence is under `pattern-stroke-probe-20260909` and
`review-20260909/pattern-stroke-*` in the local benchmark root, with the payload
under `parity-20260909-ghent/payload-pattern-stroke`. Overall parity remains open.

### Tiling-pattern transparency checkpoint

Tiling patterns now apply the painted object's transparency once after drawing
their cells, with default transparency inside the pattern and the existing
backdrop available for internal blending. Patterned strokes use stroke opacity.
Four regressions cover overlapping marks, distinct fill and stroke opacity,
and internal Normal and Multiply blending. The first two failed before the fix.
This follows PDF 32000-1 section 11.6.7; the retained native renderer ignores
outer stroke opacity in the dedicated fixture, so its opaque result is not the
expected reference for that case. The rendered translucent stroke was inspected.

All 3,865 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 Broad and Shared pages remain pixel-identical to the pattern-stroke
checkpoint, with unchanged dimensions and OK batch rows. Evidence is under
`pattern-stroke-probe-20260909/opacity-*` and
`review-20260909/pattern-opacity-*` in the local benchmark root; the payload is
`parity-20260909-ghent/payload-pattern-opacity`. Shading-pattern transparency,
broader visual parity, and performance parity remain open.

### Shading-pattern stroke opacity checkpoint

Shading-pattern strokes now use stroke opacity instead of fill opacity. Two
regressions independently vary CA and ca, verify both the stroke and fill,
and fail before the correction. All 3,867 engine tests and 370 app tests pass;
Release publish succeeds. All 674 Broad and Shared pages remain pixel-identical
to the tiling-opacity checkpoint with unchanged dimensions and OK batch rows.
Evidence is retained as `shading-opacity-*` logs, the
`pattern-stroke-probe-20260909/shading-opacity-corpus-comparison.json` comparison,
and `review-20260909/shading-opacity-*` renders in the local benchmark root.
The payload is `parity-20260909-ghent/payload-shading-opacity`. Shading-pattern
graphics-state overrides and overlapping mesh transparency remain unverified.

### Zero-length dash checkpoint

Zero-length painted dash entries now retain round or square caps instead of
disappearing. Square marks retain the line direction, including diagonal paths.
Nine regressions cover all three cap styles, horizontal and diagonal lines,
positive and negative phases, and subpath restarts. Four cap regressions fail
before the fix. The dedicated three-page native comparison confirms restored
round and square marks; their rendered shapes were inspected. Native spacing
drifts slightly on this fixture, while the engine preserves the declared cycle.

All 3,876 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 Broad and Shared pages remain pixel-identical to the shading-opacity
checkpoint with unchanged dimensions and OK batch rows. The negative-phase
corpus page remains unchanged; its odd-length cycle differs from the native
renderer and was not replaced with native spacing. Evidence is retained under
`zero-dash-20260909` and `review-20260909/zero-dash-*` in the local benchmark root.
The payload is `parity-20260909-ghent/payload-zero-dash`. Overall parity remains open.

### Reduced stencil detail checkpoint

Reduced one-bit stencil images now average source coverage into a bounded alpha
plane instead of selecting individual bits. This restores thin text strokes in
the scanned All Quiet on the Third Coast article. Eight regressions cover both
decode directions, clipping, and partial nonstroking opacity; all fail before
the fix and pass afterward. The article and affected Ghent mask and font pages
were visually inspected. Article mean RGB error against native falls from
11.9876 to 2.8804; that remaining difference is not visual parity.

All 3,884 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 Broad and Shared pages retain matching dimensions and OK batch rows.
Twelve pages change, each with lower mean RGB error against native; 662 remain
pixel-identical. Evidence is retained as `stencil-area-*` logs and comparison
JSON, plus `review-20260909/stencil-area-*` renders in the local benchmark root.
The payload is `parity-20260909-ghent/payload-stencil-area`. The subsequent paired
timing results at the top include this change and leave speed parity open.

### Packed coverage edge counting checkpoint

Partial bytes in binary-image coverage now use masked population counts instead
of reading each bit separately. Exact coverage-grid regressions include short
footprints spanning byte boundaries. All 3,886 engine tests and 370 app tests
pass, Release publish succeeds, and all 674 corpus images remain pixel-identical
to the stencil-area checkpoint, with unchanged dimensions and OK batch rows.

Two fresh-render workloads were measured in A/B/B/A and B/A/A/B order. Runs use
80 renders with 40 discarded, then 160 with 80 discarded. All 1,920 renders
have matching hashes per workload and zero diagnostics. Six of eight pairs
favor the change; two do not. Median run medians fall from 60.320 to 56.542 ms
for the article at 1024 pixels and from 66.376 to 62.826 ms for the unknown-filter
fixture at 2048 pixels. Timing ranges overlap, so these are limited workload
results, not a whole-application speed claim. The earlier full paired benchmark
at the top remains the current overall comparison.

Evidence is under `binary-count-20260909`, `binary-count-*` logs, and
`review-20260909/binary-count-*` in the local benchmark root. The payload is
`parity-20260909-ghent/payload-binary-count`. Overall parity remains open.

### Color-indicator visual disposition

Current and native GWG220 and GWG221 images were inspected. The current engine
avoids the prominent crosses visible in native output, consistent with these
pages' stated conversion and output-intent indicators. GWG220's sampled interior
is uniform; GWG221 retains one-level channel differences. These two large
native pixel differences are positive indicator results, not evidence that the
engine should reproduce native output. See `engine/docs/color-review.md` for
sample values, retained evidence, and the limits of this finding. Absolute color
accuracy and overall visual parity remain open.

### Remaining color-difference disposition

The September 24 2048-pixel comparison confirms matching engine and PDFium
dimensions for every page of the remaining named fixtures. The bug1703683,
issue18529, and two poppler-91414 pages have thumbnail mean RGB deltas from
0.002405 through 0.172219, with no sampled pixel exceeding a 16-level channel
difference. Their earlier one-pixel size differences are no longer present.

The larger CalGray and sampled DeviceCMYK differences were checked against an
independent Poppler render. Poppler agrees with the engine's CalGray transfer
and the sampled CMYK shading's hue layout, while PDFium differs. Existing Ghent
indicator and same-renderer reference checks also show cases where matching
PDFium would reproduce the test page's stated error indicator. No renderer
change is justified by these named differences. Exact comparison values,
reasoning, and evidence locations are recorded in
`engine/docs/color-review.md`. This disposition does not claim full color,
ICC, or Ghent conformance.

The subsequent GWG161 comparison supports all 16 sampled DeviceCMYK knockout
patches: engine center/background differences are at most one channel level,
while native error crosses remain prominent. On the ICCBasedRGB page, 15 engine
pairs differ by at most three levels; Color Dodge retains a 19-level difference
reproduced exactly by isolated engine and unoptimized LittleCMS display conversion.
This sample does not establish a blend defect. The page permits faint color-management crosses,
so neither pixel mismatch alone nor this comparison proves full conformance.
Values and image locations are recorded in the color review.

### Color Dodge and Color Burn endpoint checkpoint

The renderer now preserves a black backdrop channel under Color Dodge and a
white backdrop channel under Color Burn, including the opposite source endpoint.
This follows the June 2009 Adobe correction adopted by PDF 2.0, described in
[the PDF Association's blend formula explanation](https://pdfa.org/why-pdf-2-0-is-the-new-pdf-bible/).
Eight regressions cover RGB and CMYK with full and partial opacity. All eight
fail before the correction and pass afterward. All 3,894 engine tests and 370
app tests pass, and Release publish succeeds.

All 674 corpus pages render successfully with unchanged dimensions. Only the
ColorBurn fixture at both sizes and the shared ColorDodge fixture change; the
other 671 images remain identical. Their mean RGB differences from native grow
from 0.3451 to 3.0210, 0.6923 to 3.3434, and 0.4686 to 5.1080 respectively.
The ColorBurn stripes were visually reviewed: fully saturated backdrop channels
now remain saturated under zero source channels, as the corrected formula requires.
These native differences are intentional standards corrections, not a measured
visual parity win. The separate GWG161 ICCBasedRGB Color Dodge residual is unchanged.

Evidence is retained as `blend-endpoints-*` logs and comparison JSON and
`review-20260909/blend-endpoints-*` images in the local benchmark root. The payload
is `parity-20260909-ghent/payload-blend-endpoints`. Overall parity remains open.

### Tiny transformed dash checkpoint

Dash expansion no longer drops positive lengths using a fixed user-space
tolerance. Four comparisons cover tiny dash coordinates magnified into visible
one-pixel dashes, at two path lengths and two phases. The two short-path cases
failed before the change; the longer paths previously reached a nonadvancing
pattern loop and were added after the correction. All four now match equivalent
ordinary coordinates exactly. A fifth regression verifies the expansion limit.

Expansion checks cancellation, rejects arithmetic that cannot advance, and stops
after one million expansion steps with NotSupportedException. This is an explicit
complexity limit, not successful rendering of arbitrarily dense patterns.
All 3,899 engine tests and 370 app tests pass, Release publish succeeds, and all
674 corpus pages remain pixel-identical with unchanged dimensions and OK rows.
Evidence is retained as `tiny-dash-*` logs and comparison JSON and
`review-20260909/tiny-dash-*` images in the local benchmark root. The payload is
`parity-20260909-ghent/payload-tiny-dash`. Overall parity remains open.

### Shading pattern graphics state checkpoint

The optional shading pattern ExtGState now participates in pattern evaluation.
Its nonstroking opacity and blend mode operate inside the pattern, followed by
the painted object's outer opacity and blend mode. Pattern soft masks use the
pattern matrix and do not leak into following content. This follows Table 76
and clause 11.6.7 of [PDF 32000-1:2008](https://opensource.adobe.com/dc-acrobat-sdk-docs/standards/pdfstandards/pdf/PDF32000_2008.pdf).

Six new regressions cover fill and stroke opacity, the distinction between ca
and CA inside a shading, internal versus outer blending, translated soft masks,
and following-content state. The two ca cases failed before implementation;
the CA controls already passed. Opacity assertions allow one channel level for
eight-bit intermediate group storage. All 3,905 engine tests and 370 app tests
pass, Release publish succeeds, and all 674 corpus images remain pixel-identical
with unchanged dimensions and OK rows.

A separate three-page fixture produces RGB (255, 192, 192) in both half-opacity
cases and blue for internal Screen followed by outer Multiply over blue. The
retained native renderer gives (255, 127, 127) and black, consistent with ignoring
the pattern state. These are bounded standards-based improvements, not overall
parity. Shading Background handling is addressed by the following checkpoint;
overlapping mesh transparency remains unverified.

Evidence is under `pattern-state-*`, `pattern-state-fixture`, and
`review-20260909/pattern-state-*` in the local benchmark root. The published
payload is `parity-20260909-ghent/payload-pattern-state`. Overall parity remains open.

### Shading pattern background checkpoint

The shading Background color now fills the pattern-painted area before the
gradient. Background and gradient are separate objects in an implicit knockout
group, preventing their opacity from accumulating where they overlap. The
gradient's BBox clips the gradient while background remains in the rest of the
painted area. Direct sh operations still ignore Background, as required by
Table 78 and clause 11.6.7 of PDF 32000-1:2008.

Seven regressions cover fills, strokes, outer and internal opacity, BBox and
paint clipping, and the direct-sh control. Four initial pattern cases failed
before the fix; the direct-sh control already passed. All 3,912 engine tests
and 370 app tests pass, Release publish succeeds, and all 674 corpus images
remain pixel-identical with unchanged dimensions and OK rows.

The two-page fixture was rendered at 400 pixels. Engine background samples are
RGB (128, 128, 255) and (192, 192, 255); gradient samples are (255, 128, 128)
and (255, 192, 192). Native backgrounds are opaque blue and white respectively,
with (255, 127, 127) gradients. The engine's bounded and translucent background
was visually inspected. This verifies the focused behavior without establishing
overall visual, performance, or interactive parity.

Evidence is under `shading-background-*`, `shading-background-fixture`, and
`review-20260909/shading-background-*` in the local benchmark root. The payload
is `parity-20260909-ghent/payload-shading-background`. Overall parity remains open.

### Mesh overlap compositing checkpoint

Mesh elements now blend against the backdrop preceding their shading object.
Overlapping triangles and patch subdivisions no longer accumulate opacity or
blend effects. A nested knockout parent prepares each touched pixel before
the mesh captures that pixel's backdrop. Eight regressions cover shading
types 4 through 7 with Normal and Screen blending. The original regression
failed with a 75% opaque result for a shading with 50% opacity.

All 3,920 engine tests and 370 app tests pass, and Release publish succeeds.
The two-page 400-pixel reproduction now produces RGB (255, 128, 128) for
both single and overlapping triangles. Native output is (255, 127, 127).
All 674 corpus images remain pixel-identical to the shading-background
checkpoint, with unchanged dimensions and successful render rows.

An eight-page nested fixture checks isolation and knockout combinations.
Single and duplicate meshes match in all four combinations when outer fill
and stroke opacity are equal. Unequal outer opacity still triggers the
existing unsupported non-isolated knockout-group diagnostic; that case
remains an open parity gap, not a mesh-fix regression.

The backdrop snapshot is bounded by the clip and target surface. Its RGB
storage is eight bytes per bounded pixel (32 MiB for a full 2048-square
surface), plus optional alpha planes and array overhead. Opaque, unclipped,
normal paints without masks, knockout, or overprint bypass it. This cost is
derived from allocations in the implementation, not a measured peak-memory
result. Further allocation and performance work remains open.

Evidence is in `mesh-overlap-*`, `mesh-overlap-probe`, `mesh-nested-probe`,
and `review-20260909/mesh-overlap-*` under the local benchmark root. The
payload is `parity-20260909-ghent/payload-mesh-overlap`.

### Unequal outer knockout opacity checkpoint

Removed the rejection of non-isolated knockout groups solely because outer
fill and stroke opacity differ. The completed group already composites with
nonstroking opacity, as required by PDF 32000-1:2008 clause 11.6.4.4.
The existing group test now covers outer stroke opacity 0, 0.5, and 1 with
fill opacity 0.5. Both unequal cases failed before the correction.

All 3,922 engine tests and 370 app tests pass; Release publish succeeds.
All eight pages of the nested fixture with unequal outer opacity now match
the equal-opacity control byte for byte. The non-isolated knockout samples
retain RGB (64, 192, 0) instead of the previously blank green backdrop.
This checkpoint uses focused fixture validation; the prior 674-page corpus
comparison belongs to the mesh-overlap checkpoint. Non-normal outer blends,
outer soft masks, and nested knockout parents remain restricted for these
non-isolated groups. Overall parity remains open.

Evidence is in `knockout-unequal-*` and `mesh-nested-probe/unequal-fixed`
under the local benchmark root. The payload is
`parity-20260909-ghent/payload-knockout-unequal`.

### Knockout mask and backdrop checkpoint

Non-isolated knockout groups now accept an outer soft mask. The mask applies
once to the completed group, including when outer opacity is one. Group
opacity is tracked independently of the initial backdrop, and the backdrop
contribution is removed before outer compositing. This also corrects an
existing double-backdrop error with partially transparent backgrounds.

Three masked regressions failed before mask support; two additional masked
and unmasked transparent-backdrop regressions failed before backdrop removal.
All 3,927 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 corpus images are unchanged from the mesh-overlap checkpoint, with
successful rows and unchanged dimensions. Eight published mask-fixture samples
match their quarter-opacity controls; the restored knockout triangle was
visually inspected. Its sample is RGB (32, 223, 0), previously (0, 255, 0).

Evidence is in `knockout-mask-*`, `knockout-backdrop-*`, `knockout-mask-probe`,
and `review-20260909/knockout-backdrop-*` in the local benchmark root. The
payload is `parity-20260909-ghent/payload-knockout-backdrop`. Non-normal
outer blends and nested knockout parents remain restricted. Overall
rendering, performance, memory, and interactive parity remain open.

### Outer knockout blend checkpoint

Non-isolated knockout groups now use the completed-group compositor for
non-normal outer blend modes. Full-opacity groups also take this route
when blending is required, instead of painting directly into the parent.
Four regressions cover Screen and Multiply at half and full opacity.
The test sets blend mode and opacity together because separate authoring
graphics-state helpers reset unspecified values. Earlier mesh Screen tests
were corrected to use the same combined state and a gray backdrop that
distinguishes Screen from Normal.

All 3,931 engine tests and 370 app tests pass, and Release publish succeeds.
All 674 corpus images remain unchanged from the knockout-backdrop checkpoint,
with successful rows and unchanged dimensions. A 16-page fixture covers
Screen and Multiply, isolation, knockout, and duplicated meshes. Single and
duplicate samples match in each case. Non-isolated knockout samples are
RGB (64, 255, 0) for Screen and (0, 192, 0) for Multiply; isolated controls
are (64, 255, 0) and (0, 191, 0), respectively.

Evidence is in `knockout-blend-*`, `knockout-blend-probe`, and
`review-20260909/knockout-blend-*` under the local benchmark root. The
payload is `parity-20260909-ghent/payload-knockout-blend`. Nested knockout
parents remain restricted; overall parity remains open.

### Nested knockout backdrop checkpoint

Non-isolated knockout children now render within knockout parents. The child
copies the parent's initial backdrop into its bounded surface without marking
the parent as painted. Only pixels contributed by the completed child prepare
the parent's backdrop for compositing. This preserves earlier parent content
outside the child's painted area and avoids using that content as the child's
initial backdrop. The backdrop copy checks cancellation by row and reuses the
existing child surface instead of allocating an additional surface.

Four regressions cover isolated and non-isolated parents with half and full
child opacity, including untouched parent pixels. All four failed before the
fix. All 3,935 engine tests and 370 app tests pass; Release publish succeeds.
All 674 corpus images remain unchanged from the knockout-blend checkpoint,
with successful rows and unchanged dimensions. The four-page published fixture
now displays its child meshes, previously skipped over a yellow parent fill.
Single and duplicate samples match: RGB (128, 128, 0) for non-isolated parents
and (128, 127, 0) for isolated parents. The restored fixture was visually inspected.

Evidence is in `nested-knockout-*`, `nested-knockout-probe`, and
`review-20260909/nested-knockout-*` under the local benchmark root. The
payload is `parity-20260909-ghent/payload-nested-knockout`. This closes the
explicit nested-group rejection exercised here; it does not establish full
transparency, visual, performance, memory, or interactive parity.

### Transparent nested knockout checkpoint

A touched child pixel now prepares its knockout parent before the zero-alpha
shortcut. Transparent child content therefore clears earlier parent content
within its shape while leaving untouched parent pixels unchanged. This follows
the separation of shape and opacity in PDF 32000-1:2008 section 11.4.6.
Four zero-opacity cases failed before the correction; all eight nested tests
now pass under isolated and non-isolated parents at half and full outer opacity.

All 3,939 engine tests and 370 app tests pass, and Release publish succeeds.
The four-page zero-opacity fixture now exposes RGB (0, 255, 0), the original
backdrop, instead of the intervening yellow fill. Evidence is in
`knockout-zero-*` and `knockout-zero-probe/input-v2`, `before-v2`, and `fixed`
under the local benchmark root. The initial scratch conversion failed before
producing an input PDF; only the v2 fixture is evidence. The payload is
`parity-20260909-ghent/payload-knockout-zero`.

Validation here is focused; the latest 674-page comparison belongs to the
preceding nested-knockout checkpoint. Fractional shape coverage, alpha-is-shape
behavior, and the broader parity gates still require verification.

### Regenerated form appearance audit (2026-09-24)

The remaining apparent font-shape gap in `bug1844576.pdf`, `bug1844583.pdf`,
and `issue19389.pdf` came from a standalone audit renderer that omitted the
desktop app's installed-font resolver. With the production Helvetica-to-Arial
mapping, fresh output exactly matches the existing application corpus output.
The regenerated values, password masking, button caption, borders, and text
placement were visually inspected against PDFium. Remaining differences are
limited to rasterization edges rather than field layout or glyph selection.

The audit also covered all later pages in three focused multi-page form files:
both pages of `form_two_pages.pdf` and `prefilled_f1040.pdf`, plus all three
pages of `text_field_own_canvas_calc.pdf`. Every fresh image matches its
existing application output byte for byte. PDFium thumbnail mean RGB deltas
are 0.046875 and 0.046599 for `form_two_pages.pdf`, 0.783946 and 0.887495 for
`prefilled_f1040.pdf`, and 0, 0, and 0.315479 for
`text_field_own_canvas_calc.pdf`. This is a focused visual and pixel audit, not
a new whole-corpus or performance run. Broader rendering parity remains open.

### Source and decoded-image retention checkpoint (2026-09-24)

A focused forced-GC probe now separates document ownership, renderer lifetime,
shared-cache lifetime, and full release. It opens each file through a stream,
renders into caller-owned buffers, and records live managed memory only. It
does not report timing. Each target was run twice in a fresh process; both runs
produced the same stage values and pixel hashes.

Altona technical page 1 retains 127,898,408 bytes at the document stage for
its 127,724,771-byte source. After 1024, 2048, and 2560 pixel renders, the
document plus shared cache retain 135,249,672 bytes above the warmed baseline.
The page's roughly 92 MiB decoded Flate image therefore does not remain in the
64 MiB shared cache. Full release returns within 233,768 bytes of baseline.

The 698,427-byte JPEG 2000 balloon retains 48,910,648 bytes through the shared
cache, then returns to 10,487,040 bytes above its warmed baseline after full
release. Ghent ALL page 2 retains 35,809,128 bytes through the shared cache,
including its 15,454,633-byte document source, then returns to 4,780,744 bytes
above baseline. These stable post-release values are process-level codec and
runtime retention, not document or renderer ownership. The focused results
confirm one document-owned source buffer, bounded decoded-image caching, and
release of session-owned data. The probe used the engine's null font resolver,
which does not affect these image-retention targets. Broader memory and
performance parity remain open.

### Reusable glyph coverage checkpoint (2026-09-24)

A temporary counter build measured glyph-mask requests and cache misses after
the vectorized dense-clip work. On the source-study UnknownFilter page, the
first 1024-pixel render reused 462 of 740 requested masks and created 278 cache
entries. Every request in four later renders was a cache hit. Compacted PDF
syntax reused 33 of 113 requests on its first render and all 113 afterward.
Ghent ALL page 2 reused 1,226 of 3,586 requests on its first render and all
3,586 afterward. Each target kept one stable pixel hash across its repeated
renders. The counters were removed after the measurement; production source
was verified clean.

Two reversed-order pairs then compared the enabled and disabled cache on the
source-study page. Each process performed 60 renders, with the final 30 used
for warmed medians. Cache-on versus cache-off medians were 8.152/12.748 ms and
8.069/12.865 ms. Median per-render allocation was 2,973,736 bytes with the
cache and 9,202,968 bytes without it, a 67.7 percent reduction. Each mode kept
a stable pixel hash. The different hashes between modes are the existing,
tested quarter-pixel placement tradeoff rather than run-to-run instability.

Cold first renders were recorded separately at 383.325 and 390.511 ms with the
cache, and 418.132 and 412.373 ms without it. They include process startup,
first-use parsing, and tiered compilation, so they do not establish a cold-path
speed claim. The consistent warmed pairs and request counts confirm that the
bounded glyph-mask cache still covers substantial reusable work. Broader
rendering and performance parity remain open.

### Codec and non-axial shading performance checkpoint (2026-09-24)

A six-file non-conformance sample extended paired 2048-pixel application
throughput to function, lattice, and radial shadings plus JPEG, JPEG 2000, and
Flate images. Four alternating passes per version produced 1.8 versus 1.9
medians of 1,432 versus 920 ms for function shading, 907 versus 363 ms for
lattice shading, 101 versus 80.5 ms for radial shading, 337 versus 1,307.5 ms
for JPEG, 635 versus 1,188 ms for JPEG 2000, and 3.5 versus 74 ms for Flate.
The six-file median total remains slower at 3,938.5 versus 3,423.5 ms because
the two image codecs dominate it. Broader throughput parity remains open.

Fresh-process and warmed measurements were separated for the four expensive
targets. At 1024 pixels, first versus warmed medians were 654.674 versus
373.229 ms for function shading, 407.493 versus 208.003 ms for lattice
shading, 797.959 versus 441.064 ms for JPEG 2000, and 1,131.140 versus
379.932 ms for JPEG. CPU traces identify calculator execution as the main
function-shading cost, soft-mask reduction and CoreJ2K packet decoding as the
main JPEG 2000 costs, and Huffman decoding, block output, and soft-mask
reduction as the main JPEG costs.

Reusing one function-input array per shading worker regressed warmed rendering
and was discarded. Bounded row parallelism was retained instead. Two
reversed-order pairs reduced the same function-shading page from 323.567 and
321.562 ms with one worker to 96.251 and 91.397 ms with four workers, with one
stable SHA-256 pixel hash in every run. All six application PNGs also match
the pre-change 1.9 outputs byte for byte. The apparent lattice improvement is
not attributed to this function-shading change because session load and
tiered compilation varied.

The 12,608 by 16,806 eight-bit soft mask in `issue19517.pdf` now splits its
independent reduction rows across the render's bounded workers. At 1024
pixels, the four-worker warmed median fell from 453.282 ms to 375.709 and
359.805 ms in two subsequent runs. One-worker medians remained 463.286,
464.628, and 459.491 ms, isolating the improvement to parallel reduction.
The 2048-pixel application median fell from 1,188 to 1,110.5 ms across four
runs. Every engine render kept pixel hash
`99ACCC3DA687225F51619E0B0C122CF4DB143806A00FD4C1A6E38D9F20E7590E`,
and every application PNG matched the pre-change output byte for byte.

The post-change sampled-thread-time profile reports reduction rows at 6.07
percent exclusive, followed by CoreJ2K tag-tree updates at 2.65 percent,
packet headers at 1.89 percent, entropy code blocks at 1.17 percent,
RunLength expansion at 1.12 percent, and embedded-alpha separation at 1.05
percent. The remaining JPEG 2000 costs are inside the vendored decoder and
remain slower than 1.8. All 4,132 engine tests and 431 app tests pass, and the
Release build completes with no warnings or errors.

### Reduced image soft-mask detail checkpoint (2026-09-24)

Reduced images now interpolate ordinary image soft masks at destination pixel
centers. Images rendered at their source size or enlarged retain their previous
sampling behavior. A focused regression distinguishes the interpolated 32 and
224 alpha samples from the former nearest-neighbor 64 and 255 samples.

At 1448 by 2048 pixels, `22060_A1_01_Plans.pdf` improved from a mean RGB delta
of 6.779707 to 5.834920 against PDFium and from 6.764102 to 6.092859 against
Poppler. The fraction of pixels with any channel more than 16 levels from
PDFium fell from 0.111339 to 0.107007. The Poppler fraction moved from 0.110566
to 0.111645, while its mean delta still improved. PDFium and Poppler have a
mean delta of 2.278972 from each other. The result was visually inspected at
its delivered size and retains the fine plan lines cleanly.

Direct comparison with Pillow's libjpeg decoder already placed the engine's
half-resolution JPEG output within one channel level, so the retained change
is limited to the independently verified soft-mask sampling defect. Full
validation passes with 4,133 engine tests, 431 app tests, and a Release build
with no warnings or errors. This is a focused image-detail checkpoint, not a
new whole-corpus parity run.

### Focused font-shape disposition (2026-09-24)

Fresh 2048-pixel production renders of `issue5244.pdf`, `issue4575.pdf`, and
all three pages of `issue8088.pdf` match the retained application PNG hashes
byte for byte. The production renderer resolves the non-embedded
`TimesNewRoman,Bold` and standard `Times-Roman` resources through the installed
Times New Roman faces. The recovered composite-font text and every expected
numeral are present.

Against PDFium, `issue5244.pdf` has a mean grayscale delta of 0.081142 and a
binary ink-mask disagreement of 0.003640 over the ink union. `issue4575.pdf`
measures 0.068834 and 0.006496. The three `issue8088.pdf` pages measure mean
deltas of 0.139082, 0.143620, and 0.141873, with binary disagreements of
0.011124, 0.011065, and 0.011252. Every glyph's horizontal ink run matches
PDFium, apart from isolated left or right antialias edges crossing the
128-level threshold by one pixel. Page and text bounds otherwise match.

Poppler independently renders the same text and placement, but uses different
substitute-font rasterization and reports that the non-embedded identity font
in `issue5244.pdf` is unavailable. Its larger edge differences therefore do
not justify replacing the production Windows face. Visual inspection confirms
that the remaining differences are rasterizer edge coverage, not font choice,
Unicode recovery, glyph shape, advance width, or numeral selection. No source
change is warranted, and broader visual parity remains open.

### Host Identity-font glyph placement checkpoint (2026-09-24)

Host-resolved Identity fonts now center natural glyph outlines inside CID cells
whose widths are omitted from the PDF. Explicit `/W` entries remain unchanged,
and an explicit `/DW` supplies the cell width used for centering. Outline bounds
receive the same offset so extraction geometry remains aligned with rendering.

At 2048 pixels, `issue15443.pdf` improved from a mean grayscale delta of
10.425529 to 0.115934 against PDFium. `issue15594_reduced.pdf` improved from
12.764485 to 0.064265. The glyph runs now match PDFium apart from isolated
one-pixel antialias edges, while the five explicitly sized glyphs in
`issue15443.pdf` retain their prior placement. Both results were visually
inspected and match the PDFium character selection, spacing, and placement.

Two focused width cases cover explicit default and per-CID metrics. Full
validation passes with 4,135 engine tests, 431 app tests, and a Release build
with no warnings or errors. Broader visual parity remains open.

### Page-tree reference text-width disposition (2026-09-24)

A fresh 2048-pixel production render of `Pages-tree-refs.pdf` matches the
retained application PNG byte for byte. Its full text ink bounds are identical
to PDFium at x 76 through 765 and y 69 through 112. All 20 Courier glyph runs
align. Only the right edge of `c` and the left edge of the second `a` cross the
250-level ink threshold by one pixel between the two rasterizers.

The mean grayscale delta from PDFium is 0.018841, with 0.056589 percent of
pixels differing by more than 16 levels. Poppler independently preserves the
same text, spacing, and final right edge, but its Courier antialiasing moves
several other edge thresholds by one pixel. Visual inspection confirms that
the reported width difference is rasterizer edge coverage, not page geometry,
font selection, glyph advance, or text placement. No source change is warranted.

### Coincident clip coverage checkpoint (2026-09-24)

Geometrically identical paint and clip masks now reuse one antialiased coverage
value instead of multiplying two separately rasterized copies. This preserves
the idempotence of clipping and prevents a coincident fractional edge from
becoming artificially light. Other antialiased clip intersections retain their
existing multiplication behavior.

`bug1978317.pdf` repeats 15,004 half-point blue strokes inside clipping
rectangles with the same bounds. Removing only those redundant clips in a
diagnostic copy made the engine's representative line pixels match PDFium
within one channel level, confirming the cause. The retained fix produces the
same result without altering the document. At 2048 pixels, the whole-page mean
RGB delta from PDFium improved from 2.627582 to 2.138881, and the fraction of
pixels with any channel more than 16 levels apart fell from 0.075619 to
0.062670. In the blue-content region, the mean delta improved from 14.379199
to 11.603420. Visual inspection confirms the repeated blue rules are darker
and match the reference more closely.

A regression compares a fractional-width stroke with and without an identical
clip. Full validation passes with 4,136 engine tests, 431 app tests, and a
Release build with no warnings or errors. Finer glyph-cache positioning and a
no-cache trial changed the remaining embedded Times New Roman edge differences
only marginally, so neither broader performance tradeoff was retained.

### Startup, packaged rendering, and ReadyToRun disposition (2026-09-24)

The retained September 8 startup traces cover five launches of both loose
installed-layout applications. The first measured 1.9 process reached the
main-window ready marker in 2,619.7 ms versus 2,904.5 ms for 1.8. The four warm
1.9 launches have a 2,387.2 ms median versus 2,119.0 ms for 1.8. Warm loader
time is lower at 215.5 versus 237.3 ms, while median main-window construction
is higher at 588.1 versus 458.3 ms. The warm regression is therefore after the
runtime loader. These are first-process and warm-cache measurements, not an OS
cache flush, and the marker does not measure first-page display or interaction.

The retained September 9 installed-layout package runs cover three measured
alternating passes after warmup. Engine 1.9 medians are 15.687 seconds of render
time and 27.350 seconds wall time for the 600-page shared set, versus 12.240 and
23.023 seconds for the PDFium application. The 74-page difficult set measures
10.356 and 15.749 seconds versus 5.479 and 11.011 seconds. These results remain
a release-performance gap, not an optimization claim.

The separate ReadyToRun package was also measured and remains rejected. Its
shared render median was 11.829 seconds, but the difficult median rose to
10.643 seconds and the shared whole-pass median was 24.836 seconds in that
experiment. The 20-file payload grew from approximately 47.15 MiB to 60.29 MiB,
about 28 percent. The mixed workload result and package growth do not justify
changing publish settings. No ReadyToRun property was added. Interactive
first-page, scrolling, and zoom validation remains open in the release table.

### PDF.js duplicate-path disposition (2026-09-24)

The two paths absent from the 977-file PDF.js corpus are intentional exact
duplicates, not missing inputs. The pinned import manifest maps
`test/pdfs/empty#hash.pdf` to retained `test/pdfs/empty.pdf`; both are 4,920
bytes with SHA-256
`FCEE6184C0D776126782CD2799797B106373278C8EA0A4354EE4E33CD8663D51`.
It maps `web/compressed.tracemonkey-pldi-09.pdf` to retained
`test/pdfs/tracemonkey.pdf`; both are 1,016,315 bytes with SHA-256
`3662FF519E485810520552BF301D8C3B2B917FD2F83303F4965D7ABED367E113`.
The source audit therefore remains 979 paths and 977 unique PDFs.

At 2048 pixels, the engine and PDFium each successfully rendered the only page
of `empty.pdf` at 1582 by 2048 and the first three pages of `tracemonkey.pdf` at
1582 by 2048. Since each alias has identical bytes, those four canonical input
pages complete the requested alias coverage without adding duplicate files.

### Malformed image and page-box recovery checkpoint (2026-09-24)

Viewer compatibility recovery now accepts an empty `/DecodeParms` array as
omitted while strict stream decoding still rejects the mismatched array. A
filter chain ending in DCTDecode unwraps its earlier filters before reading the
JPEG frame dimensions and samples. At 2048 pixels, `xobject-image.pdf` now
produces the expected red page with no diagnostic and its PNG is byte-identical
to PDFium.

Compatibility recovery also treats the exact sequential JPEG scan marker
combination `Ss=0`, `Se=0`, and `AhAl=0` as the normal complete sequential scan.
Strict decoding still rejects it. This restores the full photograph in
`issue12841_reduced.pdf` without broadening accepted progressive scan states or
changing writer behavior.

An unusable crop box now falls back to a usable media box before the existing
US Letter fallback. Page two of `boundingBox_invalid.pdf` therefore uses its
800 by 600 media box and matches PDFium's 2048 by 1536 output geometry. All
three targeted renders complete without diagnostics and were visually
inspected. Validation passes with 4,139 engine tests, 431 app tests, and a
Release application build with no warnings or errors. Evidence is retained in
`C:/Users/steve/kp-bench-render/unsupported-recovery-20260924`.

### CCITT and explicit-mask recovery checkpoint (2026-09-24)

Compatibility recovery now retries CCITT data with end-of-line markers when a
malformed image dictionary omits `/EndOfLine true`. Strict decoding still
rejects the mismatch. Fresh 2048-pixel renders of `issue5747.pdf` and
`ccitt_EndOfBlock_false.pdf` complete without diagnostics. Visual comparison
with PDFium confirms the full scanned text page and all six K=-1, K=0, and K=1
fax samples. Their mean RGBA channel deltas are 3.776178 and 0.765086 across
matching output dimensions.

Compatibility recovery also accepts a one-bit DeviceGray image as an explicit
`/Mask` when the malformed mask stream omits `/ImageMask true`. Its grayscale
polarity remains separate from standards-compliant stencil-mask polarity.
`issue6621.pdf` now renders the full West Virginia court seal with no diagnostic
and matching 2048 by 2048 geometry. Its mean RGBA channel delta from PDFium is
0.166742. Strict rendering continues to report the malformed mask.

`issue18042.pdf` contains only four bytes, `1234`, where its image dictionary
claims a 7300 by 7600 JPEG. Both renderers correctly skip that image. The first
three engine pages are byte-identical to PDFium, so no decoder relaxation is
warranted. Validation passes with 4,142 engine tests, 431 app tests, and a
Release application build with no warnings or errors. Evidence is retained in
`C:/Users/steve/kp-bench-render/ccitt-recovery-20260924` and
`C:/Users/steve/kp-bench-render/mask-recovery-20260924`.

### CMYK mode 0 spot overprint and parity checkpoint (2026-10-02)

Opaque CMYK mode 0 overprints now preserve named spot ink while replacing all
four process inks. Two focused regressions failed before the fix and pass now.
The 108 overprint tests, 4,192 engine tests, 487 app tests, and Release app build
pass, with no build warnings or errors.

The 512-pixel corpus rendered 614 of 649 files, skipped 35, and failed none.
Exactly four PNGs changed, all in Altona and Ghent spot tests; the other 610
are byte-identical. Each changed region moved closer to Poppler's overprint
rendering. The Ghent OPM0 patches no longer show the incorrect solid X, though
a thin X outline remains. Evidence is in
`C:/Users/steve/kp-bench-render/cmyk-opm0-20261002`.

An earlier matched pre-fix 512-pixel pair across 600 common pages measured
14,252/14,202 ms for 1.8.80 and 15,063/14,667 ms for 1.9, leaving 1.9
3.3 to 5.7 percent slower. At 2048 pixels, the 74-page difficult set measured
4,769/4,792 ms for 1.8.80 and 8,892/8,593 ms for 1.9. Its Altona technical
page measured 215/212 ms versus 1,229/1,127 ms. Those matched measurements
predate the current overprint correction; its separate corpus pass does not
establish a speed change. The integer spot-mixing trial produced identical
pixels but mixed alternating timings and was removed. Rendering performance,
full-page fidelity, and interactive parity remain open.

### Opaque spot-run performance checkpoint (2026-10-02)

Altona's 2048-pixel technical page makes 960,294 eligible opaque, full-cover
named-spot pixel paints across 105,705 scanline runs. The renderer now shares
spot plate lookup, process mask preparation, and synchronization across each
run while leaving partially covered edges on the original path. A scratch
control and trial rendered 614 of 649 conformance pages at 512 pixels with
identical decoded image hashes and matching skips and dimensions. All 128
images from the focused timing sequences also matched exactly.

Eight alternating fresh processes measured last-eight warm Altona renders at
371.1 ms control versus 344.5 ms trial on average, a 7.2 percent reduction.
First renders averaged 1,211.5 versus 1,192.5 ms with overlapping ranges, so
no cold-render gain is established. Four matched pairs of the 74-page
2048-pixel difficult set produced trial-minus-control totals of +14, +176,
-168, and +54 ms. Their sign changes; no whole-set gain or slowdown is
established. Altona alone averaged 26.75 ms faster in those pairs. All 74 page
hashes and statuses matched across the eight runs. The production branch
passes 4,192 engine tests, 487 app tests, and a Release build with zero
warnings and errors. Evidence is retained under
`C:/Users/steve/kp-bench-render/spot-eligibility-20261002` and
`C:/Users/steve/kp-bench-render/spot-run-74-controlled-reverse-20261002`.

The post-commit matched comparison used current 1.8.80 and 1.9 Release
binaries. Across 600 common corpus pages at 512 pixels, 1.8 render sums were
10,666/10,420 ms and 1.9 sums were 11,666/11,702 ms, leaving 1.9
9.4/12.3 percent slower in the two pairs. The 74-page difficult set at 2048
pixels measured 4,599/4,689 ms in 1.8 and 8,364/8,444 ms in 1.9, a
81.9/80.1 percent gap. Altona Technical page 1 measured 201/244 ms versus
1,080/1,013 ms. The sums cover page rendering in fresh headless processes,
not whole-app wall time or interaction. All 600 shared corpus pages and all 74
difficult pages matched status and dimensions. Overall, 1.8 rendered 600 of
649 corpus files, skipped 41, and failed eight malformed files; 1.9 rendered
614, skipped 35, and failed none. Evidence is under
`C:/Users/steve/kp-bench-render/parity-final-8b2027f-v1880-20261002`.

A fresh headless comparison used the current Release binaries with one file
worker explicitly set for 1.9 (`--parallel 1`); 1.8 renders files serially.
The 649-file conformance run used 512 pixels and one page per file. Across the
600 pages both versions rendered, 1.8 summed 10,744/10,472 ms and 1.9 summed
11,914/11,669 ms in 1.8/1.9/1.9/1.8 order. The paired gaps are 10.9 and
11.4 percent. The 1.9 runs rendered 614 pages with no failures; 1.8 rendered
600 and failed eight malformed files. At 2048 pixels and three pages per file,
the 74-page difficult set summed 4,932/4,636 ms for 1.8 and 10,425/8,563 ms
for 1.9 in the same order, leaving paired gaps of 111.4 and 84.7 percent.
Altona Technical page 1 took 210/199 ms in 1.8 versus 1,258/1,166 ms in
1.9. The first difficult pair varied more than the second, so it is not a
single stable percentage. An earlier attempt overlapped a background file
search and is excluded. These are summed page render times, not application
wall time. Scratch evidence is under
`C:/Users/steve/kp-bench-render/parity-serial-corpus-idle-20261002` and
`C:/Users/steve/kp-bench-render/parity-serial-difficult-idle-20261002`.

An opaque process-color run trial batched spot-plate clearing after named spot
paints. Sixteen repeated Altona pages at 2048 pixels took 6,432/6,508 ms in
control runs and 6,480/6,404 ms in trial runs. The last 15 pages favored the
trial by about 90 ms per run, while first-page times overlapped. The 74-page
difficult set took 8,648/9,264 ms in control runs and 8,809/8,748 ms in trial
runs, changing direction between pairs. All 16 Altona and 74 difficult PNGs
matched their controls. The broad speed gain was not established, so the
renderer trial was removed. A regression test now covers a process fill
between two named spots, including its fractional edge. Scratch results are
under `C:/Users/steve/kp-bench-render/spot-process-run-compare-20261002` and
`C:/Users/steve/kp-bench-render/spot-process-run-difficult-20261002`.

A 64 KiB lookup table reproduced the current named-spot mixing formula exactly
on all 16 repeated Altona PNGs. At 2048 pixels, control render sums were
6,508/6,643 ms and trial sums were 6,480/6,562 ms in balanced order. The
first trial render was slower in one pair and effectively tied in the other;
the last 15 pages favored the trial by 179/36 ms. This did not establish a
useful cold-render improvement, so the table was removed. Evidence is under
`C:/Users/steve/kp-bench-render/spot-blend-table-compare-20261002`.

A current cold trace of `42828.0001.001.pdf` page 1 at 2048 pixels took
513 ms to render. Of the 511 ms sampled inside `RenderUncached`, stream decode
occupied 313 ms, including 307 ms in JBIG2 decode. Main-thread image painting
occupied 60 ms and text handling 74 ms. These are inclusive intervals from one
profiled run, so they are not an untraced parity result. The next focused target
is JBIG2 symbol decoding. Trace and render CSV are under
`C:/Users/steve/kp-bench-render/jbig2-current-cold-profile-20261002`.

A template-0 JBIG2 trial accumulated each output byte as an integer and narrowed
it only when stored. Fifteen focused JBIG2 tests passed, and all eight scan PNGs
matched the control at both 512 and 2048 pixels. In two 512-pixel pairs, the
later seven renders took 1,238/1,241 ms for control and 1,197/1,217 ms for the
trial. At 2048 pixels they took 1,259/1,333 ms and 1,346/1,333 ms. First-render
times overlapped at both sizes. The larger-page result did not establish a gain,
so the code trial was removed. Scratch payloads and CSVs are under
`C:/Users/steve/kp-bench-render/jbig2-intacc-trial-20261002`.
