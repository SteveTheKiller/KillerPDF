# Editing PDFs

Use the incremental editors to change an existing document while retaining its original byte prefix and adding one bounded revision.

## Why incremental editing matters

An incremental update appends changed objects, a new cross-reference section, and a new trailer. The original bytes remain intact. This is important for preservation-sensitive workflows, audit trails, and signed revision analysis.

Use a deterministic full rewrite when you intentionally want a normalized document instead.

## Change pages

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

PdfDocument document = PdfDocument.Open(File.ReadAllBytes("input.pdf"));

byte[] updated = new PdfIncrementalPageEditor(document)
    .RotateClockwise(0)
    .MovePage(2, 0)
    .AddBlankPage(612, 792)
    .Build();

File.WriteAllBytes("updated.pdf", updated);
```

Page operations include insertion, import, removal, reordering, rotation, page boxes, user units, transitions, thumbnails, content replacement, and content appending.

## Copy a page from another PDF

`AddImportedPage` copies the selected page and its dependent object graph into the destination document. If either document contains an AcroForm, the editor also merges the form field tree, renames colliding resource names in `/DR`, and rewrites affected `/DA` strings.

```csharp
byte[] destinationBytes = File.ReadAllBytes("destination.pdf");
byte[] sourceBytes = File.ReadAllBytes("source.pdf");

PdfDocument destination = PdfDocument.Open(destinationBytes);
PdfDocument source = PdfDocument.Open(sourceBytes);

byte[] updated = new PdfIncrementalPageEditor(destination)
    .AddImportedPage(source, sourcePageIndex: 0)
    .Build();

File.WriteAllBytes("combined.pdf", updated);
```

Page indexes are zero-based. Duplicate fully qualified form field names throw an exception instead of being renamed automatically.

## Insert an image at a page position

Use `PdfContentStreamBuilder` to place an image directly into a page's content. PDF coordinates start at the lower-left corner of the page.

```csharp
using KillerPdf.Engine.Authoring;

PdfPageInformation page = PdfPageInformation.Read(document)[0];
PdfImage image = PdfImage.FromJpeg(File.ReadAllBytes("photo.jpg"));

var content = new PdfContentStreamBuilder()
    .DrawImage(image, x: 72, y: 500, width: 180, height: 120);

byte[] updated = new PdfIncrementalPageEditor(document)
    .AppendPageContent(0, page.Width, page.Height, content)
    .Build();

File.WriteAllBytes("with-image.pdf", updated);
```

For an image that should remain an annotation, use `PdfIncrementalAnnotationEditor.AddImageStamp` with the same page, position, and dimensions.

## Move, resize, or remove a placed image

`PdfPageContentReader.ReadImagePlacements` returns one handle for each image occurrence on a page. `PdfImagePlacementEditor` then moves, resizes, or removes the occurrence in a detached incremental revision. It preserves shared resources and unrelated content.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

var reader = new PdfPageContentReader(document);
PdfImagePlacementSelection image = reader.ReadImagePlacements(0)[0];

if (image.CanSetBounds)
{
    var target = new PdfContentBounds(72, 400, 252, 520);
    byte[] moved = PdfImagePlacementEditor.SetBounds(document, image, target);
}

if (image.CanRemove)
{
    byte[] without = PdfImagePlacementEditor.Remove(document, image);
}
```

A handle belongs to the `PdfDocument` instance that read it, and the editor throws `ArgumentException` for any other instance. `SetBounds` takes the new rectangle as a `PdfContentBounds` in unrotated, crop-relative PDF points, and it needs a placement that is axis-aligned, unclipped, and valid. `CanSetBounds` and `CanRemove` report whether an occurrence is supported, and `BoundsUnsupportedReason` and `UnsupportedReason` give a stable reason when it is not. Removal honors the document's modification, certification, and structure restrictions.

## Extract an image as a detached asset

`PdfImagePlacementExtractor.Extract` copies one original image into a `PdfImageAsset`, an immutable single-image resource that does not belong to the source document. It keeps the image's pixels, an RGB ICC profile and rendering intent, and a matching soft mask.

```csharp
PdfImageAsset asset = PdfImagePlacementExtractor.Extract(document, image);
Console.WriteLine($"{asset.Width} x {asset.Height}");

byte[] assetPage = new PdfDocumentBuilder().AddPage(200, 150,
    new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 200, 150)).Build();

byte[] pasted = new PdfIncrementalPageEditor(document)
    .AppendPageContent(0, 612, 792, new PdfContentStreamBuilder().DrawImage(asset, 10, 10, 100, 100))
    .Build();
```

`PdfContentStreamBuilder.DrawImage` places an asset on a new page or appends it to an existing page, and `PdfImageAsset.ToPdfBytes` returns it as a PDF with a page the size of the image. The extractor handles one raw or Flate image, or a JPEG with direct DeviceRGB color, with eight bits per component. An image with a color space the asset cannot carry, a property it does not preserve such as a Decode array or an image mask, or a JPEG with a soft mask throws `NotSupportedException`. An asset, its soft mask, and its encoded data are limited to 5 MiB. A document opened with a user password must allow content copying, or the extractor throws `InvalidOperationException`.

## Select and extract pixels from a render

`PdfRasterSelection` and `PdfRasterExtraction` work on a rendered float frame, so they can pick up content that is not a placed image, such as a drawing or part of a scan. A selection is an immutable mask of whole pixels with a top-left origin, and each pixel is either selected or not.

```csharp
using KillerPdf.Engine.Rendering;

var renderer = new PdfPageRenderer(document);
PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(612, 792));

PdfRasterSelection area = PdfRasterSelection.SelectRectangle(
    frame.Width, frame.Height, left: 100, top: 100, selectionWidth: 300, selectionHeight: 200);
PdfRasterSelection color = PdfRasterSelection.SelectConnected(frame, seedX: 120, seedY: 130, tolerance: 0.05f);

PdfRasterSelection both = area.Add(color);
PdfRasterSelection withoutColor = area.Subtract(color);

if (PdfRasterExtraction.AllowsExtraction(document))
{
    PdfRasterExtractionResult result = PdfRasterExtraction.Extract(frame, both, featherRadius: 2);
    Console.WriteLine($"{result.Width} x {result.Height} at {result.Left}, {result.Top}");

    var (red, green, blue) = PdfRasterExtraction.SampleFillColor(frame, both);
    PdfImageAsset fill = PdfRasterExtraction.CreateFill(both, result, red, green, blue);
}
```

`SelectConnected` selects the four-connected region of visible pixels whose RGB values are within `tolerance` of the seed pixel. The tolerance is the largest allowed difference in any channel, it is inclusive, and it uses the frame's own channel encoding. A seed pixel with zero alpha selects nothing. `SelectRectangle` selects a rectangle inside the frame. `Add` and `Subtract` return a new selection with the same dimensions, so you can refine a selection and leave transparent holes. Each operation takes a cancellation token and an optional `maximumWorkingBytes` limit of at most 256 MiB, which is also the default, and it throws `ArgumentException` when the work would not fit.

`Extract` copies the selected pixels into a transparent `PdfImageAsset` that covers the selection's bounding box. `PdfRasterExtractionResult.Left`, `Top`, `Width`, and `Height` give that rectangle in frame pixels. A `featherRadius` from 0 to `PdfRasterExtraction.MaximumFeatherRadius` (32) softens the edge of the mask, and 0 keeps hard edges. Pixels come from the frame's own sRGB output and never from a monitor or proof view, and a transparent source pixel stays transparent. An empty selection, or one with dimensions different from the frame, throws `ArgumentException`. Like an extracted page picture, the result is limited to `PdfRasterExtraction.MaximumPixels` (1,310,720 pixels) and 5 MiB of compressed data, and a larger area throws `NotSupportedException`.

To cut an area instead of copying it, `SampleFillColor` takes the mean sRGB color of the opaque pixels a fixed distance outside the selection (`DefaultFillRingDistance`, 3 pixels), and `CreateFill` builds a solid image in the selection's shape that covers the cut area. `Extract` does not check the document's permissions itself, so call `AllowsExtraction` first. It returns false when the document was opened with a user password that does not allow content copying.

## Update document features

`PdfIncrementalPageEditor` also changes metadata, output intents, page layout, viewer preferences, open actions, named destinations, page labels, attachments, bookmarks, and AcroForm fields.

```csharp
byte[] updated = new PdfIncrementalPageEditor(document)
    .SetTextFieldValue("Customer.Name", "Ada Lovelace")
    .SetCheckBoxValue("Terms.Accepted", true)
    .Build();
```

## Edit annotations

Use `PdfIncrementalAnnotationEditor` for page annotations and links:

```csharp
var editor = new PdfIncrementalAnnotationEditor(document);

byte[] updated = editor
    .AddTextNote(0, 72, 700, "Reviewed")
    .Build();
```

The annotation editor supports text notes, text markup, free text, lines, shapes, ink, image stamps, redaction marks, file attachments, and links.

## Remove, rewrite, and optimize

Some edits change content rather than add a revision on top of it:

| Task | Start with |
| --- | --- |
| Permanently remove text, images, annotations, and fields under reviewed areas, then verify the result | `PdfRedactionSearch` and `PdfContentRedaction` |
| Rewrite a line of page text in place | `PdfTextLineEditor` |
| Preview and apply lossless optimization or selective removal | `PdfOptimizer` |
| Flatten form fields | `PdfFormFlattener` |
| Export to HTML, Markdown, JSON, Word, Excel, or PowerPoint formats | `PdfStructuredExport` |

Redaction and optimization write a new full rewrite, so they invalidate existing signatures unless you plan for that. Incremental edits keep earlier revisions in the file, so they are not a way to erase data.

## Import safely

Page imports copy the complete dependent object graph with collision handling. The editor refuses imports that depend on unsupported global state rather than producing a document with silently damaged forms, navigation, optional content, or accessibility structure.

## Write to a new destination

Keep the source and output paths separate until `Build` succeeds. Replace the original file only after your application has completed its own validation and durable write procedure.
