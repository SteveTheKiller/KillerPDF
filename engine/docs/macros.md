# Reusable macros and batch execution

`PdfMacro` stores an ordered list of typed operations and string settings.
`PdfMacroRunner` passes each step and the current bytes to a host-supplied
callback. The runner does not implement a general save pipeline or execute
scripts. The host decides which operations it supports and how their inputs,
outputs, validation, and external actions are handled.

## Create and serialize a workflow

Use a feature-specific step factory where available so its settings use the
expected schema. This macro exports the first page as text:

```csharp
using KillerPdf.Engine.Documents;

static PdfMacro CreateTextExportMacro()
{
    return new PdfMacro("First-page text",
    [
        PdfStructuredExportMacro.Step(PdfStructuredExportFormat.PlainText, [0])
    ]);
}
```

Save `macro.ToJson(indented: true)` and reload it with `PdfMacro.FromJson(json)`.
The current schema version is 1. Names must be nonempty, at least one step is
required, and operation values must be defined. Deserialization validates this
model; it does not prove every setting or operation sequence is executable.
Validate settings through the matching executor and keep configuration
dictionaries stable while a run is active.

`Duplicate`, `MoveStep`, `InsertStep`, `ReplaceStep`, and `RemoveStep` return new
macros. Step indices are zero-based; the last step cannot be removed. These
copy helpers copy settings dictionaries. The constructor accepts existing
steps, so do not assume every caller-supplied settings dictionary was deeply
frozen.

`CreateStarter` supplies editable Archival, Sharing, Scanning, and Privacy
sequences. Their names describe intended workflows, not guaranteed output
properties. For example, Archival requires the host to implement OCR,
validation, and saving; creating that starter does not create a PDF/A file.

## Preview names and collisions

```csharp
using KillerPdf.Engine.Documents;

static PdfMacroPreview PreviewTextExport(PdfMacro macro, bool outputExists)
{
    return macro.Preview(
        [new PdfMacroPreviewFile("input.pdf", "output.txt", outputExists)],
        PdfMacroOverwriteBehavior.Error);
}
```

The host supplies the names and `OutputExists`; preview performs no filesystem
inspection. Output names must be unique case-insensitively. Error sets `CanRun`
false when an output exists, Skip excludes collisions from `FilesToProcess`,
and Replace retains them. No files are written, reserved, or deleted.

`Run` does not accept or enforce this preview. The host must honor `CanRun`, map
`FilesToProcess` to the selected input bytes, and check destination state again
when saving. Preview names are labels, not validated filesystem destinations.

## Supply an operation callback

```csharp
using KillerPdf.Engine.Documents;

static PdfMacroRunReport RunTextExports(byte[][] sources,
    CancellationToken cancellationToken = default)
{
    var macro = new PdfMacro("First-page text",
        [PdfStructuredExportMacro.Step(PdfStructuredExportFormat.PlainText, [0])]);
    return PdfMacroRunner.RunReport(macro,
        sources.Select(source => (ReadOnlyMemory<byte>)source),
        (step, data, token) => step.Operation switch
        {
            PdfMacroOperation.Export => PdfStructuredExportMacro.Execute(step, data, token),
            _ => throw new NotSupportedException("This host supports structured export only.")
        }, cancellationToken);
}
```

Each callback receives the previous step's bytes and returns the next step's
bytes. Export can produce text or Office data, so a later step cannot assume
its input is still PDF. The runner does not validate this transition or insert
validation automatically. Use an explicit dispatcher and reject unsupported
operations or settings.

Feature-specific helpers include `PdfStructuredExportMacro`, `PdfNavigationMacro`,
`PdfAttachmentMacro`, `PdfCollectionMacro`, `PdfLayerMacro`,
`PdfPageFurnitureMacro`, and `PdfImpositionMacro`. Their typed factories and
executors define the supported settings. Individual helpers can reopen input
without a password callback; a generic callback must not assume all helpers
share the same authentication, review, or output contract. See
[exporting](exporting.md), [navigation](navigation.md), [attachments](attachments.md),
and [layers](layers.md) for those feature boundaries.

## Interpret outcomes and cancellation

Inputs run sequentially. The runner copies each source before its first step
and copies successful output into `PdfMacroFileResult.Data`. A callback can
still perform external side effects; byte copying does not undo them.
`Succeeded` means all callbacks completed with data, not that the result passed
PDF validation or retained visual fidelity.

Ordinary step exceptions become an `Error` with the failed step index and
operation, and processing continues with the next input. Fatal memory and
process-level exceptions are not contained. Cancellation between inputs leaves
the remaining inputs unprocessed. Cancellation during a step, when the supplied
token is canceled, records a canceled input and stops.

The token is checked before each step and passed to the callback. A callback
that does not observe it can delay cancellation. There is no built-in per-file
timeout, process isolation, concurrency, or whole-run memory limit.
`RunReport` materializes the supplied input list and retains successful output
buffers. Size batches accordingly.

Reports count succeeded, failed, canceled, and unprocessed inputs.
`ToJson(indented: true)` omits document data but includes error messages and
failure locations. It is an outcome summary, not a restorable checkpoint with
output payloads or input fingerprints.

## Resume an interrupted batch

`ResumeReport(macro, inputs, previous, operation, cancellationToken)` preserves
the previous contiguous result prefix. It retries a canceled final input from
the first step and continues with unprocessed inputs. It does not retry earlier
failed inputs or resume partway through a file.

The method checks input count and result indices, not input hashes, macro
identity, callback implementation, or destination state. The host must retain
and verify those identities before resuming. A callback that saves externally
must also handle replay of a partially processed canceled input. Keep the
original report and successful output data, since the JSON summary omits them.

## Pass contextual values between steps

`RunContextual` starts a fresh, case-sensitive value dictionary for each input.
A setting whose entire value is `${name}` is replaced from that dictionary.
Partial-string interpolation is not supported. Missing names fail that input.
The callback receives the resolved step and current values and returns a
`PdfMacroOperationResult` containing bytes and optional new values. New values
replace matching names for subsequent steps within that input only.

Value names allow ASCII letters, digits, underscores, hyphens, and periods,
with a maximum length of 128. Values cannot be null or exceed 1,000,000
characters. These limits do not validate an operation's meaning or bound the
total number of settings. Context substitution is data handling, not code or
shell execution; the host remains responsible for how each resolved value is
used.
