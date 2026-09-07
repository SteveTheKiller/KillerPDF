# AcroForm values and interchange

This guide covers the 1.9 development API for reading widgets and exchanging
field values. The engine supplies PDF data and editing operations; the host owns
the form UI, file selection, review, and save destination.

The examples below use unencrypted, unsigned PDFs. Consult the
[security guide](security.md) before designing a protected-document workflow.
The form importer currently reopens intermediate output without a password
callback, so these examples are not an authenticated import recipe.

## Read, export, preview, and apply

Use fully qualified field names when matching values. A field can have multiple
widgets across pages, so widget count is not necessarily field count.
`ReadPage` uses a zero-based page index and exposes inherited field state,
current and default values, choice export/display pairs, widget geometry, and
page rotation. Keep those values separate from your UI's screen coordinates.

The following functions can live in a host service. Add the namespace imports
and use normal .NET implicit usings:

```csharp
using KillerPdf.Engine.Documents;

static IReadOnlyList<PdfFormWidgetInfo> ReadWidgets(byte[] pdf)
{
    PdfDocument document = PdfDocument.Open(pdf);
    return Enumerable.Range(0, PdfDocumentInformation.Read(document).PageCount)
        .SelectMany(page => PdfFormWidgetReader.ReadPage(document, page)).ToArray();
}

static byte[] ExportValues(byte[] pdf)
{
    PdfFormDataSet data = PdfFormDataExporter.Export(
        PdfDocument.Open(pdf), includeAnnotations: false);
    return PdfXfdfFormData.Write(data);
}

static byte[] ImportReviewedValues(byte[] pdf, byte[] xfdf)
{
    PdfDocument document = PdfDocument.Open(pdf);
    PdfFormDataSet incoming = PdfXfdfFormData.Read(xfdf);
    if (incoming.ContainsJavaScript || incoming.ContainsSignature
        || incoming.ContainsIncrementalDifferences)
        throw new InvalidOperationException("Review active or signed interchange data separately.");
    var fieldsOnly = new PdfFormDataSet { Fields = incoming.Fields };
    IReadOnlyList<PdfFormDataMatch> matches = PdfFormDataImporter.Preview(document, fieldsOnly);
    if (matches.Count == 0 || matches.Any(match => match.Status != PdfFormDataMatchStatus.Matched))
        throw new InvalidOperationException("Every supplied field must match an editable field.");
    return PdfFormDataImporter.Apply(document, fieldsOnly);
}
```

`ExportValues` deliberately exports field data without annotations or a source
file path. By default, the exporter excludes fields marked NoExport and returns
one entry per distinct field name. Set `includeNoExportFields: true` only when
the host intends to include them. NoExport is an export flag, not an encryption
boundary or a rule preventing later field edits.

The import example copies only fields into a fresh data set. It does not use an
incoming source path to choose a PDF or import incoming annotations. The host
supplies the destination PDF explicitly and reviews the requested values before
calling the function. The example rejects every nonmatching field instead of
allowing partial application.

## Understand the preview

`PdfFormDataImporter.Preview` reports one result per supplied field:

| Status | Meaning |
| --- | --- |
| Matched | The field name and supplied value can be applied. |
| Unmatched | No destination widget has this name. |
| ReadOnly | The destination field is marked read-only. |
| Incompatible | The field type cannot accept these imported values. |
| InvalidValue | The value fails the destination field's constraints. |

Matching uses ordinal, case-sensitive names. Duplicate or empty input names
throw. The preview reports required and NoExport flags, but it is not a complete
form submission validator. The host still decides which required fields must be
filled and which changes the user accepts.

`CreateReport` provides aggregate counts without field values. Use the preview
for individual decisions and the report for a summary that does not expose
entered data.

## Saving and flattening

The default `Apply` overload retains editable form fields. It applies matched
text, choice, checkbox, and radio-button values; it skips other preview statuses.
If neither field values nor annotations can be applied, it throws. The example
checks all statuses first to avoid silently skipping requested values.

Field updates use an incremental revision that preserves the original byte
prefix. Imported annotations can add another revision. Select
`PdfFormDataImportOutputMode.Flattened` only when the host intends to paint
field appearances into page content and remove the fields. Review the rendered
result and retain the original file if future form editing matters. A preserved
byte prefix does not by itself establish signature validity.

## Interchange boundaries

`PdfXfdfFormData.Read` disables XML external resolution and prohibits DTDs,
with a 16 MiB character limit. Parsing identifies script elements without
executing them. The host should inspect interchange flags and choose the
supported data it intends to apply.

`PdfFdfFormData` provides the related FDF reader and writer.
`PdfFormDataSet.SelectFields` selects field names but retains annotations;
selecting fields alone is not a field-only export. The exporter option used above
avoids that ambiguity. Annotation import, source-PDF resolution, XFA conversion,
form layout changes, and action inspection are separate APIs and are not covered
by these examples.

The three functions were compiled and executed against the current engine.
Validation covered widget counts, NoExport filtering, XFDF round-trip values,
updated field values, preservation of the original PDF prefix, and rejection of
an unknown destination field.

