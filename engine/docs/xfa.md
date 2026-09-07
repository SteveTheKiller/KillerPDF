# XFA inspection, data, and conversion

XFA packets, dataset values, layout plans, and standard PDF appearances are
different representations. Reading or updating a dataset does not by itself
reflow a form or repaint its pages. The engine exposes these operations
separately so a host can inspect results before saving or converting a document.

Use an authenticated `PdfDocument` for protected inputs and consult the
[security guide](security.md). The examples below assume unencrypted PDFs.

## Inspect packets and values

```csharp
using KillerPdf.Engine.Documents;

static PdfFormDataSet? ReadXfaValues(byte[] pdf)
{
    PdfXfaInfo? info = PdfXfaReader.Read(PdfDocument.Open(pdf));
    return info is null ? null : PdfXfaDatasets.Read(info);
}

static string? InspectXfaCompatibility(byte[] pdf)
{
    PdfXfaInfo? info = PdfXfaReader.Read(PdfDocument.Open(pdf));
    return info is null ? null : PdfXfaCompatibility.Analyze(info).ToJson(true);
}
```

`PdfXfaReader.Read` returns null when the document has no XFA entry. Otherwise,
`Packets` preserves source order and decoded bytes. `IsPacketArray` distinguishes
named packet pairs from a single combined XDP stream. `FormType` reflects a
separate config packet's declaration; a combined stream can initially report
Unknown even when its XML contains a config element.

The reader does not execute form scripts. `ContainsScript` is a textual
detection hint, not a complete script inventory or a security verdict.
`PdfXfaDatasets.Read` copies that hint into `ContainsJavaScript`, including when
the source uses FormCalc. Inspect template behaviors for language details.

Dataset field names are dot-separated XML local-name paths. Repeated leaf
elements become ordered entries in a field's `Values` list. Namespace identity,
attributes, and arbitrary XML structure are not represented by this flattened
data model. Do not treat a read/write cycle of `PdfFormDataSet` as a lossless XML
round trip.

## Change one occurrence or replace datasets

This self-contained example changes the second repeated value in memory:

```csharp
using KillerPdf.Engine.Documents;

static PdfXfaInfo ChangeRepeatedExample()
{
    var data = new PdfFormDataSet
    {
        Fields = [new PdfFormDataField
        {
            Name = "order.item",
            Values = ["First", "Second"]
        }]
    };
    var info = new PdfXfaInfo
    {
        IsPacketArray = true,
        Packets = [new PdfXfaPacket("datasets", PdfXfaDatasets.Write(data))]
    };
    return PdfXfaDatasets.SetValue(info, "order.item", 1, "Reviewed second");
}

static byte[] SaveReviewedOccurrence(
    PdfDocument document, string fieldName, int occurrenceIndex, string value)
{
    return PdfXfaDocumentEditor.SetValue(document, fieldName, occurrenceIndex, value);
}
```

Occurrence indices are zero-based. `SetValue` requires an existing leaf and
preserves unrelated XML content and other packets, though the modified XML is
serialized again. `PdfXfaDatasets.SetValue` returns packet data only;
`PdfXfaDocumentEditor.SetValue` returns an incremental PDF revision.

Use `PdfXfaDatasets.Replace` or `PdfXfaDocumentEditor.ReplaceDatasets` only when
replacing the whole dataset is intended. They rebuild dataset XML from field
paths and values. Names must be unique, nonempty, valid qualified XML paths;
a field cannot simultaneously be a value and the parent of another field.

Supported combined XDP streams can have their dataset element updated while
other elements remain present. Document editing preserves the original PDF byte
prefix. Unencrypted output is reopened internally to compare dataset values;
encrypted output skips that internal reopen, so authenticate and verify it in
the host. Dataset edits do not synchronize existing AcroForm widget values,
saved appearance streams, or rendered page content.

## Review behavior and layout results

| API | Result and boundary |
| --- | --- |
| `PdfXfaTemplate.Read` | Fields, bindings, controls, appearance metadata, and declared behaviors. Requires a named template packet. |
| `PdfXfaCalculationEngine.Evaluate` | Restricted FormCalc calculations in template order; later calculations can use earlier results. Returns statuses and values without writing packets. |
| `PdfXfaValidationEngine.Evaluate` | Per-expression Passed, Rejected, or an unsupported/missing/failed result. It is not a complete form submission validator. |
| `PdfXfaEventEngine.Evaluate` | Evaluates the caller-selected activity against supplied data without mutating it. It is not a browser event loop. |
| `PdfXfaFormatter.Format` | Supported picture formatting with explicit missing/unsupported/invalid results. |
| `PdfXfaStaticLayout.Plan` | Positioned field placements plus unsupported flowed field paths. Rejects a Dynamic declaration. |
| `PdfXfaFlowLayout.Plan` | Bounded flow and pagination using explicit page dimensions, margins, and repeated data. |
| `PdfXfaLocales.Read`, `PdfXfaImages.Read` | Locale and image information for supported template content. |

JavaScript is not executed by these behavior APIs. Unsupported languages,
missing expressions, failed calculations, and rejected validations must remain
visible to the host. An empty result list can mean there were no matching
behaviors; it does not prove complete form correctness. Calculations use
single-valued, finite numeric input with invariant parsing, not arbitrary XFA
objects or a complete FormCalc runtime.

Both layout plans use zero-based pages and top-left coordinates in PDF points.
Convert a placement to PDF bottom-left coordinates with
`pageHeight - placement.Y - placement.Height`. Plans do not render pixels or
automatically modify the document.

## Convert to a standard PDF

`PdfXfaAcroFormConverter.Convert(document, mode)` removes active XFA and creates
standard PDF output while preserving source pages. The default mode is
`PdfXfaConversionMode.Editable`; `Flattened` paints generated widget appearances
into page content and removes those fields. Dynamic conversion can add pages.
Neither mode is a complete reproduction of an arbitrary XFA viewer.

The converter handles supported text, numeric/date/password, check, choice, and
unsigned signature controls. Images and barcodes require Flattened mode.
Unsupported controls, out-of-page static placements, invalid data, and populated
signature fields can prevent conversion. Inspect calculations and formatting
first: conversion applies successful results, while unsupported or failed
behavior results are not a guarantee of rejection. It does not execute general
JavaScript or preserve interactive XFA behavior.

`PdfXfaCompatibility.Analyze` reports known unsupported constructs without
changing data. Its current combined-XDP finding is conservative: dataset edits
and conversion can accept supported combined streams even when the report flags
them. Conversely, `IsSupported` does not prove every expression, binding, page,
or conversion mode is supported. Review findings, behavior results, and the
actual output together.

Conversion can reopen intermediate PDFs without a password callback. This is
not a general authenticated conversion recipe. For the unencrypted workflow,
keep the original, convert to a separate output, reopen it, compare field values
and page counts, and render every affected page. See the [form guide](forms.md)
for standard widgets and [rendering](rendering.md) for image comparison.

## Resource and preservation limits

Each decoded XFA packet is limited to 64 MiB. XML readers prohibit DTD processing
and external resolution; config detection limits XML to 16 Mi characters, while
the dataset and layout readers use 64 Mi-character limits. Calculations and
matching events are limited to 10,000 each, and flow layout to 10,000 placements.
These are component bounds, not a whole-document memory or latency guarantee.
Batch hosts should isolate costly files and retain failure details.

Removing active XFA through an incremental revision does not erase its original
bytes. Conversion and dataset editing do not establish signature validity,
secure sanitization, accessibility, or standards conformance by themselves.
