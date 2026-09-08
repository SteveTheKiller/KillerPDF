# Soft-mask graphics-state correction

Creating a graphics soft mask now resets inherited fill and stroke opacity to
one and blend mode to Normal. The page's transparency settings apply when
painting with the resulting mask. Settings explicitly applied inside the mask
remain effective. This follows the transparency-group and soft-mask definitions
in [Adobe PDF Reference 1.6, sections 7.5.4 and 7.5.5](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.6.pdf).

Eight regression cases failed before the fix and pass afterward. They cover
alpha and luminosity masks, isolated and non-isolated groups, and internal
opacity. Previously a half-opacity object could be painted at quarter opacity;
inherited Multiply blending could make luminosity-masked content disappear.

All 3,252 engine tests and 341 app tests pass. The app build has zero warnings
or errors. The 40-file Broad sample renders all 74 pages at 2048 pixels without
failures. Compared with the uniform-alpha build, 72 outputs are pixel-identical.
The two changed pages are GWG168_Softmasks_Vector_part1_X4 page 1 and
Ghent_PDF-Output-Test-V50_ALL_X4 page 2.

Both changed pages were visually inspected. The change corrects the dark
bevel-and-emboss shadow in the vector soft-mask panel. In the standalone patch,
changed pixels are confined to bounds (1736, 229, 1919, 410). Mean absolute RGB
difference from the embedded reference drops from 8.548 to 1.505 over the
interior rectangles (1740, 232, 1914, 402) and (1740, 532, 1914, 702).
This local comparison is not a full-page color-fidelity score.

Logs and outputs are under
`C:/Users/steve/killerpdf-benchmark/cmyk-compositing-20260907/soft-mask-state-final`;
the preceding images are in `uniform-alpha-3-After`.
After app DLL SHA-256:
`DCFB8D95FBD6953CCBAFD88A5A8724FD7AF8E8FC161439C8714BF6E35009AD2D`.

This correctness check does not establish a new speed or memory result.
ICC fidelity, remaining isolated-group outlines, difficult-page speed, and
whole-app memory parity remain open development work.
