# Image opacity correctness checkpoint

September 7, 2026. Ordinary images now apply nonstroking graphics-state opacity,
including images with their own transparency masks. Previously only stencil
images applied this opacity. RGB and grayscale images retain direct source
sampling without an additional converted image buffer.

Six new cases failed before the change: zero and half opacity in grayscale,
RGB, and CMYK. Three opaque controls passed. All nine now pass, together with
two cases checking that half opacity multiplies a 128/255 image soft mask on
white and transparent backgrounds. Stroke opacity stays at one in these cases
to distinguish the two graphics-state parameters.

The full engine suite passes 3,199 tests and the app suite passes 341 tests.
The Release configuration builds with zero warnings or errors. A real-app
2048-pixel run on the existing 40-file difficult sample completed all 74 pages.
All decoded RGBA outputs exactly match the first conserved-engine acceptance
run recorded in the memory-parity checkpoint. This sample does not expose the
new opacity cases; the targeted tests supply that coverage.

Output PNGs and the per-page log remain in
`C:\Users\steve\killerpdf-benchmark\image-opacity-20260907`.
This is a correctness check, not a new performance or memory comparison.
Remaining color, compositing, and difficult-page speed work is still open.

## Non-isolated group opacity

A subsequent fix applies outer opacity once to completed non-isolated groups
under normal blending. Previously an internal opacity setting could replace the
outer opacity. Two additional regressions verify overlapping objects on white
and transparent backgrounds. Both failed before the change and pass afterward.
Full suites pass 3,201 engine tests and 341 app tests; the app builds with zero
warnings or errors.

The same 74-page app comparison changes only Ghent suite page 2. Its zero-opacity
patches lose the erroneous crosses, as intended and as shown by PDFium. The
changed pixel bounds are (662, 441) through (732, 928), with 4,632 changed pixels
in the 1542 by 2048 engine image. PDFium renders this page one pixel narrower,
and CMYK and blend-mode differences remain elsewhere. This is not whole-page
visual parity. The other 73 pages are exactly unchanged. PNGs and the per-page
log remain in `C:\Users\steve\killerpdf-benchmark\group-opacity-20260907`.
