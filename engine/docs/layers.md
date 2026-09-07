# PDF layers and optional content

This guide covers the 1.9 development API for optional-content groups (OCGs),
their saved configuration, content assignment, and bounded flattening. Use the
project reference in the [engine README](../README.md). These APIs require .NET 10
and do not require the Windows app.

## Create content on layers

Create a `PdfOptionalContentGroup`, wrap its drawing commands with
`BeginOptionalContent` and `EndMarkedContent`, and pass the content builder to
`AddPage`. Reuse the same group instance for content belonging to the same layer.
The document builder registers the referenced groups and writes their resources.

```csharp
using KillerPdf.Engine.Authoring;

static byte[] CreateLayerSample()
{
    var notes = new PdfOptionalContentGroup("Notes", initiallyVisible: false,
        visibleWhenPrinting: false, visibleWhenExporting: true);
    var content = new PdfContentStreamBuilder()
        .SetFillRgb(0, 0, 1).Rectangle(10, 10, 30, 30).Fill()
        .BeginOptionalContent(notes)
        .SaveState().SetFillRgb(1, 0, 0).Rectangle(60, 10, 30, 30).Fill()
        .RestoreState().EndMarkedContent();
    return new PdfDocumentBuilder().AddPage(100, 60, content).Build();
}
```

The blue rectangle is ordinary content. The red rectangle belongs to a hidden
layer. Marked-content boundaries are not graphics-state boundaries; use
`SaveState` and `RestoreState` when layer drawing changes state that should not
affect following content. Close each marked-content sequence.

## Inspect and change saved visibility

`PdfOptionalContentReader.Read(document)` returns registered `Groups` and default
and alternate `Configurations`. `ToText()` and `ToJson(indented: true)` produce
inspection reports. Authenticate encrypted documents before reading layers.
A document without `/OCProperties` returns empty groups and configurations;
malformed properties can throw rather than being reported as an empty layer list.

Each group includes its object number, generation, name, initial visibility,
locked state, and nullable print/export preferences. Names are display labels;
use the object identity from the current document when editing. Imported PDFs
can contain duplicate names, so do not silently choose the first matching label.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

static byte[] SetLayerVisibility(byte[] source, string name, bool visible)
{
    PdfDocument document = PdfDocument.Open(source);
    PdfOptionalContentGroupInfo group = PdfOptionalContentReader.Read(document)
        .Groups.Single(item => item.Name == name);
    return PdfOptionalContentEditor.SetInitialVisibility(
        document, group.ObjectNumber, visible);
}
```

This example intentionally rejects missing or ambiguous names. For an interactive
host, present the reader's groups and keep the selected object identity instead.
Editors return new PDF bytes; they do not mutate the opened document or save a
file. Reopen returned bytes before the next operation, and recreate the renderer
to use the changed saved state.

`PdfPageRenderer` takes its hidden group set from the default initial visibility
when it is constructed. It does not expose a separate live visibility-set option.
The reader exposes alternate configurations, print/export preferences, and locks
as metadata; the host must decide how to apply those policies. A saved lock is
not encryption or an access-control boundary.

## Edit layer definitions and assignments

The following methods are on `PdfOptionalContentEditor`. Page indexes are zero-based;
layer and annotation identifiers are source object numbers, not list positions.

| Operation | API and behavior |
| --- | --- |
| Register a new layer | `AddGroup` requires a nonempty, unique name and accepts initial, locked, print, and export settings. |
| Rename or copy a definition | `RenameGroup`; `DuplicateGroup` copies settings but does not copy content assignments. |
| Change usage preferences | `SetPrintVisibility` and `SetExportVisibility` accept `null` to clear a preference. |
| Change the default configuration | `SetInitialVisibility`, `SetLocked`, `SetDefaultBaseState`, and `SetDefaultConfigurationMetadata`. |
| Change display order | `SetDisplayOrder` accepts a flat order; `SetDisplayOrderTree` accepts `PdfOptionalContentOrderItem.Layer` and `.Folder` entries. |
| Assign page content | `SetPageContentGroup` wraps the whole page; `SetPageInstructionRangeGroup` accepts a complete top-level instruction range that does not split marked content. |
| Assign an annotation | `SetAnnotationGroup` assigns a registered group or clears the annotation's assignment. |
| Merge definitions | `MergeGroups` reassigns supported references and unregisters the source; unsupported active references reject the operation. |
| Remove an unused definition | `RemoveUnusedGroup` rejects a group still referenced by content, annotations, or other objects. |

Use the current reader and raw instruction list to select ranges. Adding a layer
does not assign existing content to it. Changing display order does not reorder
the page's painting commands. Review returned output before saving it, especially
when edits affect signatures, encryption, or tagged-document structure.

## Flatten supported layer content

Flattening selects visible content and removes optional-content wrappers and
unused definitions. Without an explicit visible set, it uses saved initial states.
An explicit empty set hides all registered groups while preserving ordinary content.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

static byte[] FlattenInitiallyVisibleLayers(byte[] source)
{
    PdfDocument document = PdfDocument.Open(source);
    byte[] flattened = PdfOptionalContentEditor.FlattenPageContent(document);
    PdfDocument reopened = PdfDocument.Open(flattened);
    if (PdfOptionalContentReader.Read(reopened).Groups.Count != 0)
        throw new InvalidOperationException("Layer definitions remain after flattening.");
    return flattened;
}
```

`FlattenPageContent` rejects tagged documents and unsupported nested optional
content. Direct optional-content properties, unregistered groups, malformed
marked-content sequences, and unsupported active references can also fail.
Do not replace an exception with a blanket successful-flattening report.

These editors use incremental updates. Hidden content can remain in earlier
revisions of the returned bytes, even after it disappears from the current page.
Layer hiding and flattening are not permanent redaction. See the
[editing guide](editing.md) for revision and preservation boundaries.

The sample's default and flattened renderings should both show only the blue
rectangle. Toggling Notes on should also show the red rectangle. Verify pixels
and diagnostics in addition to checking that layer definitions were removed.

See [PdfOptionalContentReader](../KillerPdf.Engine/Documents/PdfOptionalContentReader.cs),
[PdfOptionalContentEditor](../KillerPdf.Engine/Editing/PdfOptionalContentEditor.cs),
and the [rendering guide](rendering.md) for the implementation and related limits.
