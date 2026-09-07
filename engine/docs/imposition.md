# Imposition plans and sheet PDFs

Imposition arranges source pages on output sheets. The planner computes page
order and placement; the exporter creates a new PDF containing those sheet
sides. It does not send a print job or configure a physical printer.

## Define and preview a grid

All dimensions, margins, gutters, overlap, and creep use PDF points. Source,
sheet, and slot indices are zero-based. Null source entries represent blanks.

```csharp
using KillerPdf.Engine.Documents;

static PdfImpositionPreset CreateTwoUpPreset()
{
    return new PdfImpositionPreset("Two-up landscape", columns: 2, rows: 1,
        sheetWidth: 792, sheetHeight: 612, margin: 18, gutter: 12);
}
```

`preset.Plan(pageCount)` produces sequential N-up sides. Slots run left to
right, then top to bottom; unused final slots are blank. With duplex enabled,
consecutive sides share a sheet index and alternate Front and Back. An odd
side count does not add an extra blank back side automatically.

`preset.Place(side, sourcePageBounds)` fits nonblank source rectangles into the
grid and centers them within cells. It can scale up as well as down. With
`RotateToFit`, a 90-degree placement is chosen only when it gives a larger
scale. This is placement rotation, not a report of the source page's rotation.
Returned placements contain slot and source indices, sheet bounds, scale, and
rotation.

`PreviewJson(bounds, indented: true)` and `PreviewText(bounds)` report the
preset's sequential N-up plan and placements. Supply one actual selected source
box per source page; these methods do not open a PDF or look up the preset's
`SourceBox` for you. They do not preview a booklet or manual plan automatically.
Use `Place` on each side of such a plan instead.

Save reusable settings with `ToJson()` and load them with
`PdfImpositionPreset.FromJson(json)`. Version 1 validates grid dimensions,
sheet size, enum values, margins, gutters, and available grid area.

## Export the planned sides

This example uses the same two-up preset and its default options:

```csharp
using KillerPdf.Engine.Documents;

static byte[] BuildTwoUp(PdfDocument source)
{
    var preset = new PdfImpositionPreset("Two-up landscape", 2, 1,
        792, 612, margin: 18, gutter: 12);
    int pageCount = PdfPageBoxInformation.Read(source).Count;
    return PdfImpositionExporter.Build(source, preset.Plan(pageCount),
        preset.Columns, preset.Rows, preset.SheetWidth, preset.SheetHeight,
        margin: preset.Margin, gutter: preset.Gutter,
        rotateToFit: preset.RotateToFit);
}
```

`Build` writes one PDF page per supplied side in the supplied order. It imports
page content and resources into newly authored sheet pages. Source annotations
are not copied, including interactive widgets and links. This is a new sheet
document, not an incremental revision of the source. Do not infer that source
metadata, tags, signatures, or interactive behavior are preserved.

`SourceBox` selects Crop, Bleed, Trim, or Art bounds through
`PdfPageBoxInformation`. Nonzero box origins participate in the placement
transform. Review cropped and rotated source pages in the generated output.
At least one side is required for export, even though a zero-page planner can
return an empty list. Use a separate destination and retain the source.

The exporter infers duplex intent from whether any supplied side is Back and
writes the corresponding long-edge or short-edge viewer preference. This is
not proof that a viewer or printer will apply that setting. Review the sheet
order and orientation with the intended printing workflow.

## Select a page-order plan

| Planner | Behavior |
| --- | --- |
| `PlanNUp` | Sequential pages with trailing blank slots. |
| `PlanStepAndRepeat` | Repeats one selected source page for a requested copy count. |
| `PlanCutStack` | Simplex sheets whose cut piles stack into source order. |
| `PlanManual` | Caller-defined source indices, repeats, and explicit null blanks. |
| `PlanBooklet` | Two-slot saddle-stitched order, padded to a multiple of four source pages. |
| `PlanBookletSignatures` | Independent booklet groups with a positive multiple-of-four maximum signature size. |

For eight source pages, `PlanBooklet(8)` yields slot pairs `[7, 0]`, `[1, 6]`,
`[5, 2]`, and `[3, 4]` across two front/back sheet pairs. These are zero-based
source indices. The last signature can contain padding blanks. Feed booklet
sides to a two-slot grid; the typed booklet macro requires exactly two slots
and duplex output. A two-column, one-row grid places each pair side by side.

`ApplyCreep` shifts columns away from the fold using
`side.CreepDepth * creepPerSheet`; it requires more than one column. Booklet
plans populate nesting depth, while ordinary sequential plans leave it zero.
The shift does not enlarge the sheet or reserve extra margin. Review clipping
and trim allowances for the requested offset.

## Tile a poster at full size

```csharp
using KillerPdf.Engine.Documents;

static byte[] BuildFirstPagePoster(PdfDocument source)
{
    return PdfImpositionExporter.BuildPoster(source, sourcePageIndex: 0,
        sheetWidth: 612, sheetHeight: 792, margin: 18, overlap: 12);
}
```

Poster output tiles the selected source box at scale one onto simplex sheets.
`PlanPosterTiles` can preview its regions from source and usable tile dimensions.
Tiles run top to bottom, left to right. Overlap must be nonnegative and smaller
than both usable tile dimensions. The exporter applies the source box origin
and margins and clips each imported region. It does not copy annotations.

## Marks, macros, and verification

The exporter can add crop marks, registration targets, fold marks, process-color
bars, and sheet information. These are authored graphics; their presence does
not validate a printing standard or a printer's color behavior. Reserve enough
space for marks and inspect the actual output bounds. Standalone mark planners
return geometry without writing a PDF.

`PdfImpositionMacro` has typed factories for N-up, booklet, repeat, cut-stack,
manual-sequence, and poster steps. Its executor applies the saved settings and
returns PDF bytes. Macro execution has its own authentication and cancellation
boundaries; see [macros](macros.md). The direct planner and exporter methods
shown here have no cancellation parameter or whole-job memory limit.

Before accepting output, reopen it, verify side count, page order, dimensions,
blanks, and source-page coverage, and render every affected sheet. Use the
[rendering guide](rendering.md) for comparison and [editing](editing.md) for
import and preservation boundaries.
