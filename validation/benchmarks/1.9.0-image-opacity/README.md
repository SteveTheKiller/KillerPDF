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
