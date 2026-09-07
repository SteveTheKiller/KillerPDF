# Attachments and PDF portfolios

This guide covers the 1.9 development API for embedded files, page attachment
icons, extraction, editing, comparison, and portfolio metadata. Use the project
reference in the [engine README](../README.md).

## Embed a file and place its icon

Add a page and register the attachment before placing an icon that refers to it.
Page indexes are zero-based; annotation coordinates and size are PDF points.

```csharp
using KillerPdf.Engine.Authoring;

static byte[] CreateAttachmentSample()
{
    return new PdfDocumentBuilder()
        .AddBlankPage(200, 200)
        .AddAttachment("notes.txt", "Example attachment"u8.ToArray(),
            mimeType: "text/plain", description: "Sample notes")
        .AddFileAttachmentAnnotation(0, 20, 20, 18, "notes.txt",
            contents: "Read the sample notes")
        .Build();
}
```

`AddAttachment` copies the supplied bytes. It accepts description, associated-file
relationship, creation date, and modification date. Names must be portable plain
file names and unique within the builder, ignoring case. MIME types require valid
type/subtype tokens. These declarations describe the payload; they do not convert it.

An embedded file can exist without a page icon. An icon references a file
specification and does not itself display or execute the payload. Registering an
attachment does not automatically create a portfolio presentation.

## Read metadata, payloads, and placements

`PdfAttachmentReader.Read(document)` reads the document's embedded-files name tree.
Each `PdfAttachmentInfo` contains `Data`, file name, MIME type, relationship,
description, dates, portfolio values, and source object identifiers when indirect.
`ReadPageAnnotations(document, pageIndex)` reads page placements and their targets,
including attachment specifications not registered in the document name tree.

`DeclaredSize` and `DeclaredChecksum` preserve supplied metadata. `SizeMatches`
and `ChecksumMatches` are nullable when the corresponding metadata is absent.
The checksum comparison uses the PDF embedded-file MD5 field; it is not proof of
authorship or trust. Flags for unsafe names, executable extensions/signatures,
and encrypted PDF/ZIP content are inspection hints, not a complete content scan.

Authenticate encrypted attachments before reading them, even if page content is
readable without authentication. Malformed trees, file specifications, payloads,
or filters can reject reading. Decoding is bounded to 64 MiB per attachment in
this API, but the returned collection retains all decoded payloads, so total
memory can be larger. `ToText(document)` and `ToJson(document, indented: true)`
omit payload bytes from their reports but still read and decode attachments.

## Extract into a selected directory

Choose an existing output directory, inspect the attachment, then call `Extract`.
The following example creates the host-selected directory and refuses to overwrite
an existing file:

```csharp
using KillerPdf.Engine.Documents;

static string ExtractSampleAttachment(byte[] source, string outputDirectory)
{
    PdfDocument document = PdfDocument.Open(source);
    PdfAttachmentInfo attachment = PdfAttachmentReader.Read(document)
        .Single(item => item.FileName == "notes.txt");
    Directory.CreateDirectory(outputDirectory);
    return PdfAttachmentReader.Extract(attachment, outputDirectory);
}
```

`GetSafeExtractionPath` validates a plain file name and confines the computed path
to the selected directory. `Extract` uses exclusive file creation by default;
`overwrite: true` explicitly replaces a destination. The host owns directory
selection and filesystem permissions. The API writes bytes and does not launch them.

`ExtractAll` validates names and duplicate destinations before writing, and checks
existing files when overwrite is disabled. This prevents partial output from
those validation failures. It is not a filesystem transaction: a later I/O error
can leave files already written. Extraction does not create the directory itself.

## Edit and compare embedded files

Use `PdfIncrementalPageEditor` for document-level attachment registration and
payload edits, then call `Build` and reopen the returned bytes:

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

static byte[] ReplaceSampleAttachment(byte[] source)
{
    PdfDocument document = PdfDocument.Open(source);
    byte[] updated = new PdfIncrementalPageEditor(document)
        .ReplaceAttachment("notes.txt", "Revised attachment"u8.ToArray())
        .Build();
    PdfDocument reopened = PdfDocument.Open(updated);
    PdfAttachmentInfo attachment = PdfAttachmentReader.Read(reopened)
        .Single(item => item.FileName == "notes.txt");
    if (!attachment.Data.Span.SequenceEqual("Revised attachment"u8))
        throw new InvalidOperationException("Attachment replacement did not persist.");
    return updated;
}
```

Other document-level methods include `AddAttachment`, `RemoveAttachment`,
`RenameAttachment`, `SetAttachmentDescription`, and `SetAttachmentClassification`.
Use `PdfIncrementalAnnotationEditor` to add or change page attachment placements,
their targets, and icons. Inspect both document registrations and page placements
after an edit; they are distinct reference surfaces.

Replacement, renaming, description, and classification edits preserve the
registered file specification's indirect identity and portfolio values. Existing
page icons sharing that specification receive the updated payload and metadata.
Independent file specifications, including inline page targets, remain separate
even when their names match.

`PdfAttachmentComparison.Compare(original, changed)` reports added, removed,
payload, metadata, and placement differences through `Changes` and `HasChanges`.
Payload and metadata changes are reported for each affected page annotation,
including attachments that have no document-level registration. Each page change
identifies its page and annotation index.
`PdfAttachmentMacro` creates typed audit, removal, rename, description, and
classification steps and executes them against supplied bytes without external
actions. Review macro results and reopen outputs before saving.

Incremental edits preserve earlier revisions, which can still contain replaced
or removed payloads. Removing an attachment registration is not secure erasure.
See the [editing guide](editing.md) for preservation and save boundaries.

## Portfolio metadata

`PdfCollectionReader.Read` returns portfolio presentation, initial document,
schema fields, sort rules, and folders, or `null` when no collection is declared.
It also provides text and JSON reports. Per-file collection values appear on
`PdfAttachmentInfo.CollectionValues`.

`PdfCollectionEditor.SetPresentation`, `SetSchema`, `SetItemValues`, and `SetFolders`
return updated bytes for the corresponding metadata. `Clear` removes collection
presentation metadata. These operations do not extract files or run an initial
document; the host decides how to display the portfolio and open selected content.

See [PdfAttachmentReader](../KillerPdf.Engine/Documents/PdfAttachmentReader.cs),
[PdfAttachmentComparison](../KillerPdf.Engine/Documents/PdfAttachmentComparison.cs),
[PdfCollectionReader](../KillerPdf.Engine/Documents/PdfCollectionReader.cs), and
[PdfCollectionEditor](../KillerPdf.Engine/Editing/PdfCollectionEditor.cs).
