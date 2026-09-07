# Data merge and template generation

Data merge maps caller-supplied records into an existing PDF template. It can
fill AcroForm values, replace supported page-text placeholders, and append
caller-resolved images. It returns independent results; it does not load a
spreadsheet, choose a filesystem destination, or save generated files.

## Expand one string

```csharp
using KillerPdf.Engine.Documents;

static string ExpandGreeting()
{
    return PdfDataMerge.Expand("Hello, {{name}}.",
        new Dictionary<string, string?> { ["name"] = "Ada" });
}
```

The result is `Hello, Ada.`. Placeholders use double braces and trim whitespace
around the field name. Record lookup follows the supplied dictionary's comparer.
Missing or null values use Error, Empty, or KeepPlaceholder behavior; Error is
the default. An empty string is a supplied value. Empty or unclosed placeholders
throw. Expansion is a single pass and does not execute expressions.

`RunBatch(records, generate)` invokes a custom generator per record and captures
ordinary failures. The generator is responsible for creating and validating
PDF bytes; a non-null byte array alone is not proof of a valid PDF. This basic
runner has no cancellation parameter or process isolation.

## Define a reusable mapping

```csharp
using KillerPdf.Engine.Documents;

static PdfDataMergeProfile CreateRecipientProfile()
{
    return new PdfDataMergeProfile("Recipients",
        [new PdfDataMergeFieldMapping("name", "recipient")],
        outputFileNameTemplate: "letter-{{id}}.pdf");
}
```

This maps the record's `name` value to the template's `recipient` form field.
`profile.Map(record)` returns form data, text replacements, mapped image
references, and the expanded output name without changing a PDF.

Mappings can supply default values, per-mapping inclusion conditions, and
Text, Number, or Date conversion. Number conversion uses decimal parsing;
date conversion uses `DateTimeOffset` parsing. The default culture is invariant,
or a mapping can specify a culture and format string. Use explicit input dates
and offsets where reproducibility matters. Text mappings reject format strings.

Record-level and mapping-level conditions compare values ordinally. Check
`profile.Includes(record)` to decide record inclusion. `Map` and
`PreviewFormRecord` do not apply the record-level filter themselves; the batch
runner does. Target names must be unique case-insensitively, including targets
of different kinds.

Save configuration with `ToJson()` and read it with
`PdfDataMergeProfile.FromJson(json)`. Version 1 includes mappings and defaults,
but no source records. Defaults and filename templates can still contain
sensitive application data. `ToMacroStep` and `FromMacroStep` store or retrieve
the same configuration; a host must supply records and execute the merge.

## Preview a record against a template

```csharp
using KillerPdf.Engine.Documents;

static PdfDataMergePreview PreviewRecipient(PdfDocument template,
    IReadOnlyDictionary<string, string?> record)
{
    var profile = new PdfDataMergeProfile("Recipients",
        [new PdfDataMergeFieldMapping("name", "recipient")], "letter-{{id}}.pdf");
    return PdfDataMerge.PreviewFormRecord(template, record, profile);
}
```

The preview reports form-field matches, placeholder occurrence counts, image
placement matches, output name, and an optional error. `CanGenerate` means
these implemented checks found no blocked target. It does not validate image
resolution, final visual fit, collisions between generated filenames, or every
writer constraint. Preview does not call an image resolver or retain generated
PDF bytes.

Output names are expanded from original record values, not automatically from
formatted mapping results. Mapping rejects characters reported invalid by the
host platform. The library does not reserve destinations or enforce the host's
save policy. Validate the final name and destination before writing a file.

## Generate editable or flattened PDFs

```csharp
using KillerPdf.Engine.Documents;

static IReadOnlyList<PdfDataMergeDocumentResult> GenerateRecipients(
    PdfDocument template, IEnumerable<IReadOnlyDictionary<string, string?>> records,
    CancellationToken cancellationToken = default)
{
    var profile = new PdfDataMergeProfile("Recipients",
        [new PdfDataMergeFieldMapping("name", "recipient")], "letter-{{id}}.pdf");
    return PdfDataMerge.RunFormBatch(template, records, profile,
        PdfDataMergeOutputMode.Editable, cancellationToken);
}
```

Every included record starts from the same template. Form matches must pass the
importer's preview before values are applied. Editable is the default; Flattened
also paints supported widget appearances into page content and removes fields.
Reopen and inspect values and appearances, especially before flattening. See
[forms](forms.md) for appearance and import limitations.

Successful filenames are reserved within the batch case-insensitively. A later
duplicate becomes a record error. Failed records do not reserve their names.
Results include zero-based `RecordIndex`, optional `OutputFileName`, generated
`Data`, `Error`, and `Skipped`. A profile-excluded record is skipped; a successful
record has data, no error, and is not skipped.

## Page-text and image mappings

Set a field mapping's `TargetKind` to `TextPlaceholder` to replace
`{{TargetField}}` through the supported Latin-1 content transformation. This is
not general Unicode search or paragraph reflow. Placeholders split across text
operands or embedded-font encodings need separate handling. Every mapped
placeholder must be found; inspect the output for clipping and changed spacing.

Image mappings supply a record field, zero-based page index, and fixed X, Y,
width, and height in PDF points. The image-resolver overload passes the mapped
string to `Func<string, PdfImage>`. The host decides what that reference means
and supplies the image; the engine does not fetch URLs or open image paths by
itself. Preview checks page existence and rectangle fit, not the referenced
image. Applied images are appended page content and retain content-editing
guards. Review nonzero page origins, rotation, overlap, and intended aspect ratio.

## Cancellation, reports, and combining output

The form batch checks cancellation between records and within supported text
and image work. Cancellation during a record can appear as that record's Error;
there is no separate canceled flag. Remaining records are not returned after
the token is observed. Keep the original input count to distinguish records
never reached. Image callbacks and form-writing steps may delay cancellation.

`PdfDataMergeBatchReport.Create(results)` summarizes only returned results.
Its `TotalRecords` is not necessarily the number originally supplied.
`ToText()` and `ToJson()` omit PDF payloads and record dictionaries but retain
output names and error messages. Batches run in process and retain successful
output buffers; ordinary failure containment does not provide a timeout or
whole-job memory ceiling.

`CombineSuccessful(results)` sorts successful records by `RecordIndex`, combines
their PDFs, and returns `Document` and `IncludedRecordIndices`. It rejects a
batch with no successes and uses the normal document-import rules. Editable
outputs from the same form template normally have duplicate field names and
cannot be combined directly. Use reviewed flattened outputs when editability
is not required, or supply documents with distinct valid field identities.
The combiner does not rename fields or turn failed or skipped records into
blank pages. Combining, flattening, text
replacement, and image placement can reopen PDFs without a password callback;
these are not general encrypted-template workflows.

Keep the template and original records, save separate outputs, and verify
generated values, included record indices, page counts, and rendered content.
See [editing](editing.md), [rendering](rendering.md), and [macros](macros.md)
for the surrounding application workflow.
