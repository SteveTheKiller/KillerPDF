# Structured text and Office export

`PdfStructuredExport` converts page extraction into editable representations.
It does not reproduce the complete rendered page. Use [rendering](rendering.md)
for pixel output, [reading](reading.md) for extraction geometry, and
[editing](editing.md) for saving PDF documents.

## Extract and review a ruled table

`PdfTableExtractor` is a separate, bounded workflow for one complete rectangular
grid on one page. Select the whole table, including its outer borders, using
unrotated, crop-relative PDF points with a bottom-left origin:

```csharp
using KillerPdf.Engine.Documents;

static byte[] ExportReviewedTable(PdfDocument document, PdfContentBounds region,
    CancellationToken cancellationToken = default)
{
    PdfTableExtractionResult result = PdfTableExtractor.Extract(
        document, 0, region, cancellationToken);
    if (result.Table is not { } table)
        throw new NotSupportedException($"Table extraction: {result.Failure}");

    // Apply a correction accepted in the preview before exporting.
    table = table.WithCellText(0, 0, "0012");
    return PdfTableExport.Export(table, PdfTableExportFormat.Xlsx, cancellationToken);
}
```

`RowCount`, `ColumnCount`, and row-major `Cells` include empty cells. Rows run
from top to bottom. `GetCell(row, column)` uses zero-based indices. Each cell
retains `OriginalText`, original `Letters` and their geometry, and its `Bounds`.
`WithCellText` returns an immutable corrected copy; it changes neither the
original table nor the PDF. Reconstructed multiline cell text uses LF line breaks.

The first slice accepts axis-aligned stroked rules and filled rectangular rules
up to two points thick. Divider coverage must be complete, with a 0.75-point
alignment and gap tolerance. It supports at most 100 rows, 50 columns, and 5,000
cells. It does not infer unruled, merged, multi-page, or image-only tables.
Rotated page metadata is supported because coordinates remain unrotated;
rotated or right-to-left text inside cells is unsupported. Text that crosses a
cell boundary is rejected rather than silently assigned to an uncertain cell.
Explicit nonzero character or word spacing is unsupported, including spacing
set by the combined text-show operator. The glyph model does not retain that
spacing or text-show boundaries, so it cannot safely distinguish tracking gaps
from word separators in an identifier.

`Failure` distinguishes missing rules, incomplete grids, scanned regions,
unsupported text direction, limits, and uncertain content. Source extraction
diagnostics cause `UncertainContent` and remain available in `Diagnostics`.
Visibility checks are deliberately conservative: the extracted path model does
not retain per-path opacity and clipping state. Pages using clipping, extended
graphics states, unsupported XObjects, optional content, resource color spaces, white paint,
or invisible text are rejected, even when that state occurs outside the selection.
Isolated image placements wholly outside the table selection do not block extraction.
Explicit stroke widths greater than two points and page content transforms that
can enlarge stroke extents are also rejected because the path model does not
retain the painted stroke footprint or paint order. Translation, quarter-turn
rotation, and transforms that do not enlarge either axis remain supported.
Intersecting images, shading, and filled shapes other than thin rules are also
unsupported. These checks are not a substitute for reviewing the selected page
and editable preview.

Extraction also limits the already-extracted page to 100,000 glyphs, 100,000
paths, and 100,000 path segments, and the selection to two million text
characters. Those limits apply after the page reader's own bounded parsing;
they are not a process-level memory ceiling. Extraction and export check
cancellation during their grid, text, and output loops.

XLSX writes every cell as an explicit string with a text number format, so
identifiers such as `0012` survive and formula-like text stays text. Literal
Office XML escape sequences and control characters are encoded without
changing their meaning. Both formats reject cells longer than 32,767 UTF-16
code units, malformed surrogate sequences, and tables exceeding two million
text characters instead of truncating them or producing an oversized workbook.

CSV uses UTF-8 with a BOM, quotes every cell, and doubles embedded quotes.
Values beginning with `=`, `+`, `-`, or `@` receive a leading apostrophe, including
after leading whitespace, control or format characters, or Unicode compatibility
forms. This intentionally changes those CSV field values to prevent formula
evaluation. CSV cannot declare column types: import its columns as text to
preserve identifiers and date-like values. Prefer XLSX when opening directly in
a spreadsheet application. Do not strip the protective apostrophes before an
automatic spreadsheet import.

`PdfTableExport.Export` returns complete bytes and never writes a destination.
The host should write atomically only after review and successful export, and
honor cancellation before replacing any existing output. The existing
`PdfStructuredExportFormat.Spreadsheet` Page/Line/Text behavior is unchanged.

## Inspect representation losses first

Open and authenticate the source, select a format and page indices, and inspect
the known losses before deciding whether to export:

```csharp
using KillerPdf.Engine.Documents;

static PdfStructuredExportReport InspectFirstPageExport(byte[] source)
{
    PdfDocument document = PdfDocument.Open(source);
    return PdfStructuredExport.InspectLosses(
        document, PdfStructuredExportFormat.WordDocument, [0]);
}
```

Findings include a code, zero-based page index, count, and explanation. The
current checks report omitted vector paths, missing image data, text direction
not preserved outside JSON, and links omitted outside HTML and JSON.
`ToText()` and `ToJson(indented: true)` export the report.

`IsLossless` only means that this set of checks found nothing. It does not
establish exact layout, typography, shading, transparency, annotations, form
behavior, tagged structure, or semantic equivalence. Export is not blocked
automatically by findings. Retain the source and inspect the result.

## Choose a representation

| Format | Current output |
| --- | --- |
| `PlainText` | Extracted lines, with form feeds between selected pages. |
| `Html` | Page sections, text paragraphs and font spans, empty image figures, and a separate page-link list. |
| `Markdown` | Page headings, escaped text, and image placeholders with empty targets. |
| `Json` | Page dimensions, line/run text and bounds, direction, image placements, links, and extraction diagnostics. |
| `WordDocument` | DOCX paragraphs, editable text and complete ruled tables, embedded pictures, and page breaks. |
| `Spreadsheet` | XLSX with one sheet containing Page, Line, and Text columns; it does not reconstruct PDF tables. |
| `Presentation` | PPTX with one slide per selected page, positioned editable text, complete ruled tables, and embedded pictures. |
| `Epub` | Reflowable EPUB 3 book with chapters, headings, paragraphs, a table of contents, and PNG pictures. |

Word and PowerPoint embed supported images as PNG pictures. Unsupported images
retain placeholders and appear in the loss report. Clipping, external graphics
states, stencil masks and placements outside the page require page-image mode.
General vector paths are not
converted to editable shapes. JSON's geometry is
unrotated, crop-relative PDF points with a bottom-left origin. Its exported
`page` and link `destinationPage` values are one-based; annotation indices and
the loss report's `PageIndex` remain zero-based. The JSON output is an array of
page records, not the loss-report schema.

For Word and PowerPoint, pass `officeMode: PdfOfficeExportMode.PageImages` to
`Export`, `ToDocx`, or `ToPptx` to render each selected page as a picture instead.
This explicit mode retains page appearance but removes separate text and cell
editability. `InspectLosses` accepts the same mode and reports that tradeoff.
Incomplete page renders stop export. Rendering is bounded to twice the PDF point
dimensions and a maximum edge of 4096 pixels. Individual editable-mode pictures
use a maximum edge of 2048 pixels.

The default remains `PdfOfficeExportMode.Editable`, including older saved macros.
Batch `Run` and `RunReport`, and `PdfStructuredExportMacro.Step`, accept the same
option. Only complete axis-aligned ruled tables are reconstructed; merged,
unruled, scanned, and uncertain tables remain outside this slice. Editable export
reconstructs content and does not guarantee the original page layout or fonts.

The desktop command line accepts `--export input.pdf output.docx --layout images`
for page pictures, or `--layout editable` for editable text, paragraphs, pictures
and supported ruled tables. The layout option applies to DOCX and PPTX output;
other formats reject `images`. `--pages` and `--report` remain available.

## Export selected pages

```csharp
using KillerPdf.Engine.Documents;

static byte[] ExportPages(byte[] source, PdfStructuredExportFormat format,
    int[] pages, CancellationToken cancellationToken = default)
{
    PdfDocument document = PdfDocument.Open(source);
    return PdfStructuredExport.Export(document, format, pages,
        cancellationToken: cancellationToken);
}
```

`Export` returns UTF-8 bytes for text formats, ZIP-based Office document
bytes for DOCX, XLSX, and PPTX, and an EPUB package for `Epub`. The convenience methods `ToPlainText`, `ToHtml`,
`ToMarkdown`, and `ToJson` return strings; `ToDocx`, `ToXlsx`, and `ToPptx` return
bytes. The library does not choose or write a destination file.

Page selections are zero-based, unique, and within the document. Null selects
all pages; a supplied list preserves its order. An empty list exports an empty
representation. Extraction materializes the selected pages before formatting.
Cancellation is checked during page extraction, but not continuously through
every subsequent string or ZIP operation. Limit batch size and output memory
in the host rather than assuming a whole-export resource ceiling.

These examples open unencrypted PDFs. For ordinary direct export of a protected
PDF, supply an authenticated `PdfDocument` through the
[reading workflow](reading.md). Batch, macro, and OCR-assisted paths reopen
bytes without a password callback and are not general authenticated recipes.

## Export an EPUB book

`PdfStructuredExportFormat.Epub`, or `KillerPdf.Engine.Formats.Epub.PdfEpubExport.Write(document,
pageIndices, cancellationToken)` directly, rebuilds the text flow of the selected
pages as a reflowable EPUB 3 book. Batch, macro, and loss-report paths accept the
same format.

A tagged PDF's structure tree supplies the reading order, headings, and
paragraphs, and content tagged directly under a grouping element still reads as
a paragraph. A page where the tree reaches under half of the text, such as a
page inserted from an untagged file, is read from its layout and placed in page
order. A tree that reaches almost none of the text is ignored.

An untagged document is read from its layout. A vertical gutter that narrow
lines on both sides respect splits the page into columns, read left before
right, while lines crossing the gutter, such as titles, divide the page into
bands read top to bottom. Lines join into paragraphs, and words broken across
lines by a hyphen are mended. Short paragraphs set clearly larger than the body
size become headings ranked by size, while larger sentences that end in
punctuation stay paragraphs. A paragraph continues across a column or page break
only when the sentence plainly runs on. Running headers, footers, and page
numbers in the top or bottom tenth of the page that repeat from page to page are
dropped, including section running heads that repeat only within their section.

Each picture is rendered and stored once as PNG however often it appears, and it
is placed beside the text at its position on the page. A picture repeated in the
page margins, such as a logo, is treated as running furniture and dropped. A
page whose content uses an external graphics state or a clip has all of its
pictures left out, as does any picture placed partly off the page; the loss
report lists these.

Chapters start at each top-level heading, and the navigation document lists the
headings as a nested table of contents. The title comes from the document
information, then the first heading, then `Untitled`. The author and
modification date come from the document information, and the language is
normalized to a BCP 47 tag, or `und` when it is missing or invalid. ZIP entries use a fixed timestamp.

## Supply export font names

HTML, DOCX, and PPTX accept `PdfStructuredExportFontSubstitutions` through their
convenience methods or the `fontSubstitutions` argument on `Export`:

```csharp
using KillerPdf.Engine.Documents;

static byte[] ExportWithFontMap(byte[] source)
{
    var fonts = new PdfStructuredExportFontSubstitutions(
        new Dictionary<string, string> { ["Helvetica"] = "Arial" },
        fallbackFont: "Arial");
    return PdfStructuredExport.Export(PdfDocument.Open(source),
        PdfStructuredExportFormat.WordDocument, fontSubstitutions: fonts);
}
```

The map matches source names case-insensitively. The fallback applies when the
source name is absent, not to every unmapped name. These are output font names;
the export does not embed font files or install fonts. The consuming application
can substitute unavailable fonts and change layout.

## Reviewed OCR and batch outcomes

`Export` accepts an optional `PdfOcrReview` paired with a `TrueTypeFont`. It writes
the reviewed searchable text to an intermediate PDF and exports its extraction.
It does not perform recognition itself. Both OCR arguments must be supplied
together. Inspect the effective document's losses when evaluating an
OCR-assisted output, since a report on the original does not describe the added
text. See [OCR](ocr.md) for review and searchable-text behavior.

```csharp
using KillerPdf.Engine.Documents;

static PdfStructuredExportBatchReport ExportBatch(byte[] first, byte[] second,
    CancellationToken cancellationToken = default)
{
    PdfStructuredExportBatchItem[] inputs =
    [
        new("first.pdf", first, [0]),
        new("second.pdf", second, [0])
    ];
    return PdfStructuredExportBatchRunner.RunReport(inputs,
        PdfStructuredExportFormat.Json, cancellationToken);
}
```

Batch items retain copies of source bytes. Output names come from the source
base name and format extension; duplicate output names are rejected
case-insensitively before processing. Each reached document returns generated
`Data` and a `LossReport`, an `Error`, or `WasCanceled`. `Succeeded` means export
completed, even when representation losses exist.

`RunReport` counts successful, failed, canceled, and unprocessed inputs. A token
canceled before an item starts leaves it unprocessed; cancellation during an
item records that item as canceled and stops. Ordinary item failures are
contained, while fatal memory or process-level failures are not. This is
sequential in-process execution, not process isolation or a per-file timeout.
The JSON summary omits source and output payloads but includes names and error
messages. The in-memory results still retain the source and generated bytes.

`PdfStructuredExportMacro.Step(format, pageIndices)` creates a typed export step;
`Execute(step, source, cancellationToken)` returns its output without writing
files or launching applications. It does not include a loss report, font map,
OCR review, or password callback. Inspect losses separately and retain the
selected pages and format with the output for later comparison.
