# Headers, footers, and Bates numbering

The page-furniture APIs format repeated text, plan placement, write decorative
artifacts, and inspect or replace recognized marks. Visible numbering is
independent of PDF page labels. See [navigation](navigation.md) for logical
labels and [macros](macros.md) for host execution.

## Format text with explicit values

```csharp
using KillerPdf.Engine.Documents;

static string FormatFooter()
{
    return PdfPageFurnitureFormatter.Format("Page {page} of {pages} | {date}",
        new PdfPageFurnitureContext
        {
            PageNumber = 2, TotalPages = 8, Date = new DateOnly(2026, 9, 7)
        });
}
```

The result is `Page 2 of 8 | 2026-09-07`. Built-in tokens are `{page}`, `{pages}`,
`{label}`, `{filename}`, `{title}`, `{author}`, and `{date}`. Custom token names
are case-sensitive; built-in values take precedence over matching custom names.
Null values expand to empty text. Unknown, empty, or unclosed tokens throw.
Two opening braces emit a literal opening brace; this is not a general template
or expression language.

`CreateContexts(document, date, fileName, title, author, customTokens)` creates
one context per physical page and reads logical page labels. Supply the other
metadata explicitly; the method does not read title or author automatically.
The page-numbering macro does read those values before creating its contexts.

`PageNumber` is one-based and must be within `TotalPages`. Number formats include
decimal, Roman, and alphabetic sequences. Roman numbering supports 1 through
3999. The date is supplied by the caller, making repeated formatting stable.
The template is limited to 1,000,000 characters; this is not a bound on all
expanded token data or a complete document's memory use.

## Write a reviewed mark

This example assumes an unrotated page with an origin of zero and a reviewed,
clear footer area:

```csharp
using KillerPdf.Engine.Documents;

static byte[] AddFooter(PdfDocument document)
{
    return PdfPageFurnitureWriter.Apply(document,
        [new PdfPageFurnitureMark(0, "Reviewed copy", X: 36, Baseline: 18)]);
}
```

Marks use zero-based page indices, PDF-point positions, a text baseline, font
size, optional RGB color, opacity, rotation, and a standard PDF font. The writer
uses Latin-1 text; it is not a general Unicode font-layout API. It validates
basic values and appends artifact content, but does not automatically check
page fit or overlap. Use these marks for decorative furniture, not meaningful
content that belongs in a tagged reading order.

`PdfPageFurniturePlacementPlanner.Plan` accepts page dimensions, measured text
width and height, margins, edge, alignment, and optional occupied rectangles.
It returns placement bounds and intersecting rectangles. Supply all rectangles
in the same coordinate system. The test uses rectangle overlap, not visible
pixel coverage. It does not account for a rotated mark automatically.

## Use the page-numbering macro

```csharp
using KillerPdf.Engine.Documents;

static byte[] NumberPages(byte[] source)
{
    var step = PdfPageFurnitureMacro.NumberPagesStep(new PdfPageNumberMacroOptions
    {
        Date = new DateOnly(2026, 9, 7),
        Template = "{page} / {pages}",
        Edge = PdfPageFurnitureEdge.Footer,
        Alignment = PdfPageFurnitureAlignment.Center
    });
    return PdfPageFurnitureMacro.Execute(step, source).ToArray();
}
```

The macro formats selected pages, estimates width from character count and font
size, checks extracted content rectangles, and writes the marks. Null page
selection means all pages; physical numbering does not restart for a selection.
By default, detected collisions prevent writing. `AllowCollisions` should reflect
a reviewed placement decision. The estimate is not exact font measurement or a
complete visual collision check; inspect nonzero crop origins and rotated text
in particular. An empty selection or a template producing empty text cannot be
applied through this writer.

Macro execution opens bytes without a password callback. Direct writer calls
accept a `PdfDocument`, while replacement can reopen bytes internally. These
examples are not a general encrypted-document workflow. See
[security](security.md) and [editing](editing.md) for authentication and saving.

## Inspect, remove, or replace existing marks

`PdfPageFurnitureReport.Inspect(document)` reads the versioned artifact metadata
used by this feature. `ToText` and `ToJson` export text and placement details.
The marker is a format convention, not authenticated proof of who created it.
Reports do not identify arbitrary headers, footers, or ordinary PDF artifacts.

`PdfPageFurnitureEditor.RemoveAll(document)` removes blocks using the recognized
marker prefix, preserving other page instructions. It returns unchanged source
bytes when none are found and rejects an unclosed marked block. Removal
recognition uses the marker prefix, while the report also decodes its metadata;
a malformed marker can therefore be removable without appearing in the report.
`ReplaceAll(document, marks)` removes recognized marks and applies the supplied
set. An empty set removes them without adding replacements.

These edits retain the applicable page-editing guards. Appending decorative
artifacts and replacing existing page content have different structure
constraints; do not assume every tagged PDF can use every removal path.
Incremental removal does not erase the earlier bytes.

## Plan continuous Bates values

```csharp
using KillerPdf.Engine.Documents;

static IReadOnlyList<PdfBatesNumber> PlanBatesBatch()
{
    return PdfBatesNumbering.Plan([2, 1], new PdfBatesNumberingOptions
    {
        StartNumber = 100, DigitCount = 6, Prefix = "DOC-"
    });
}
```

This assigns `DOC-000100` and `DOC-000101` to the first document and
`DOC-000102` to the second. Ordering comes from the supplied document/page
sequence. Start values are nonnegative; padding supports 1 through 18 digits
and does not truncate larger values. Planning checks numeric range overflow.

`ApplyBatch(documents, options, createMark)` uses a caller-supplied placement
factory for each planned value. The factory must retain its assigned page
index; use the planned `Text` to retain the numbering. It does not provide
per-file failure reports or collision detection itself.
`ApplyNamedBatch` additionally returns deterministic output names and rejects
case-insensitive name collisions. Names must be plain file names; the default
suffix is `_bates`. The methods return bytes and do not write files.

For the configured macro path, use `BatesBatchStep` with `ExecuteBatesBatch`.
Run it once over the ordered batch to keep numbering continuous. The ordinary
per-document macro runner does not turn it into a cross-document counter.
The direct Bates planner and writer have no cancellation parameter. Retain
the planned mapping and review every output's values, placement, page count,
and source content before saving.
