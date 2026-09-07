# Structured text and Office export

`PdfStructuredExport` converts page extraction into editable representations.
It does not reproduce the complete rendered page. Use [rendering](rendering.md)
for pixel output, [reading](reading.md) for extraction geometry, and
[editing](editing.md) for saving PDF documents.

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
| `WordDocument` | DOCX paragraphs and editable text runs, page breaks, and textual image placeholders. |
| `Spreadsheet` | XLSX with one sheet containing Page, Line, and Text columns; it does not reconstruct PDF tables. |
| `Presentation` | PPTX with one slide per selected page, positioned editable text, and image placeholders. |

No format embeds the original image data or vector paths. JSON's geometry is
unrotated, crop-relative PDF points with a bottom-left origin. Its exported
`page` and link `destinationPage` values are one-based; annotation indices and
the loss report's `PageIndex` remain zero-based. The JSON output is an array of
page records, not the loss-report schema.

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

`Export` returns UTF-8 bytes for text formats and ZIP-based Office document
bytes for DOCX, XLSX, and PPTX. The convenience methods `ToPlainText`, `ToHtml`,
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
