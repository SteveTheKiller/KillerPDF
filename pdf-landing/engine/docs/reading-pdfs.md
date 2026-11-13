# Reading PDFs

Open documents from memory, inspect unfamiliar input, and read high-level document features without exposing parser internals to the rest of your application.

## Open a document

```csharp
using KillerPdf.Engine.Documents;

byte[] source = File.ReadAllBytes("input.pdf");
PdfDocument document = PdfDocument.Open(source);
```

For an encrypted document, pass the user or owner password:

```csharp
PdfDocument document = PdfDocument.Open(source, password);
```

The document keeps its own copy of the supplied bytes, so your buffer can be reused after the call. Use `PdfDocument.OpenWithCompatibilityRecovery` when a viewer needs bounded recovery from a damaged file. Strict parsing remains the default.

## Inspect before opening

Use the non-throwing structural inspector when a file came from an unfamiliar or untrusted source:

```csharp
using KillerPdf.Engine.Diagnostics;

PdfInspectionReport report = PdfDocumentInspector.Inspect(source);
if (!report.IsStructurallyValid)
{
    foreach (PdfDiagnostic diagnostic in report.Diagnostics)
        Console.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
}
```

Encrypted input reports that authentication is required. Use `InspectAuthenticated` when your application has a password.

## Read document information

```csharp
PdfDocumentInformation information = PdfDocumentInformation.Read(document);

Console.WriteLine(information.Title);
Console.WriteLine(information.Author);
Console.WriteLine(information.Language);
Console.WriteLine(information.PageCount);
```

## Read page geometry

```csharp
IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);

for (int index = 0; index < pages.Count; index++)
{
    PdfPageInformation page = pages[index];
    Console.WriteLine($"Page {index + 1}: {page.Width} x {page.Height}, {page.Rotation} degrees");
}
```

The effective geometry accounts for inherited page boxes and rotation.

## Read interactive features

| Feature | Reader |
| --- | --- |
| Bookmarks | `PdfBookmarkReader` |
| Links | `PdfLinkReader` |
| Form widgets | `PdfFormWidgetReader` |
| Signatures | `PdfSignatureReader` |
| Attachments | `PdfAttachmentReader` |
| Comments | `PdfCommentReader` |
| Layers | `PdfOptionalContentReader` |

These readers return stable, high-level records with decoded text and normalized geometry. Your application does not need to traverse raw dictionaries for common tasks.

## Read form fields

Form widgets are read one page at a time. Each result includes the fully qualified field name, field type, current value, flags, options, and normalized page coordinates.

```csharp
IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);

for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
{
    foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, pageIndex))
    {
        Console.WriteLine($"{widget.FieldName}: {widget.Value}");
        Console.WriteLine($"  Type: {widget.FieldKind}");
        Console.WriteLine($"  Rectangle: {widget.Left}, {widget.Bottom}, {widget.Right}, {widget.Top}");
    }
}
```

An empty page returns an empty list. Page indexes are zero-based.

## Extract text and render pages

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

var reader = new PdfPageContentReader(document);
PdfPageContent content = reader.Read(0);
foreach (PdfExtractedWord word in content.Words)
    Console.WriteLine($"{word.Text}: {word.BoundingBox}");

var renderer = new PdfPageRenderer(document);
PdfRenderedPage image = renderer.Render(0, new PdfRenderOptions(1024, 768));
```

Extraction returns words, text runs, lines, glyph bounds, fonts, images, paths, and shadings in unrotated, crop-relative PDF points. Rendering returns tightly packed BGRA32 pixels with a top-left origin, and the caller chooses how to display or encode them. Both report diagnostics for any recovery they applied.

## Read EPUB and DjVu documents

EPUB and DjVu support is in development. The engine reads both formats with its own readers and does not convert them to PDF unless you ask. `PdfReadSnapshot` presents either format through one surface for page count, page geometry, text, outline, and rendering.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;

TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes("regular.ttf"));
var options = new EpubLayoutOptions(PageWidth: 794, PageHeight: 1123, DefaultFont: font);

PdfReadSnapshot book = PdfReadSnapshot.OpenEpub(File.ReadAllBytes("book.epub"), options);
PdfReadSnapshot scan = PdfReadSnapshot.OpenDjvu(File.ReadAllBytes("scan.djvu"));

Console.WriteLine($"{book.Format}: {book.PageCount} pages");
Console.WriteLine($"{scan.Format}: {scan.PageCount} pages");

PdfReadPageGeometry geometry = scan.GetPageGeometry(0);
Console.WriteLine($"{geometry.Width} x {geometry.Height} {geometry.Unit} at {geometry.DpiX} dpi");
```

EPUB is reflowable, so layout needs a page size and a fallback TrueType font that your application supplies. `EpubLayoutOptions` takes the page size in CSS pixels at 96 per inch, and every page of one book shares that geometry. A DjVu page is a scan, so each page reports its own size in pixels and its recorded resolution. `PdfReadSnapshot.OpenEpub` lays out the whole book before it returns. The snapshot keeps its own copy of the bytes, and the `Owned` variants take your array instead of copying it.

For metadata, the reading order, or raw chapter text without a layout, open the format directly:

```csharp
using KillerPdf.Engine.Formats.Djvu;

EpubDocument epub = EpubDocument.Open(File.ReadAllBytes("book.epub"));
Console.WriteLine($"{epub.Metadata.Title} by {epub.Metadata.Creator}");
foreach (EpubSpineItem item in epub.Spine)
    Console.WriteLine(item.Item.Href);
string firstChapter = epub.ReadText(0);

DjvuDocument djvu = DjvuDocument.Open(File.ReadAllBytes("scan.djvu"));
foreach (DjvuDiagnostic notice in djvu.Diagnostics)
    Console.WriteLine($"{notice.Code}: {notice.Message}");
```

`EpubDocument` accepts EPUB 2 and EPUB 3 packages. `DjvuDocument.Pages` lists each page's size, resolution, orientation, and bundled component name. Opening a DjVu document does not decode any page image.

## Open a long EPUB progressively

`PdfReadSnapshot.OpenEpubProgressive` lays out the first chapter in reading order and returns while a background thread paginates the rest of the book. `PageCount` grows by whole chapters, pages already counted never change, and the finished snapshot has the same pages that `OpenEpub` lays out.

```csharp
PdfReadSnapshot book = PdfReadSnapshot.OpenEpubProgressive(bytes, options);
try
{
    bool exists = await book.WaitForPageAsync(40);

    string target = "EPUB/chapter12.xhtml#start";
    await book.WaitForNavigationTargetAsync(target);
    PdfReadNavigationDestination place = book.ResolveNavigationTarget(target);

    await book.Pagination;
    Console.WriteLine($"{book.PageCount} pages");
}
finally
{
    book.ReleaseLayout();
}
```

`IsPaginating` and `PaginationProgress` report how far pagination has come. `WaitForPageAsync` returns false when pagination ended before it reached the page. `ResolveNavigationTarget` throws `InvalidOperationException` for a target whose chapter is not paginated yet, so wait with `WaitForNavigationTargetAsync` first. `Pagination` completes when every page is known, faults with the error that stopped pagination, and is canceled when `ReleaseLayout` runs first. Call `ReleaseLayout` when the book closes so queued layout work stops. Page text stays readable afterward, but a page that would need layout again is refused. The token you pass to the open call cancels only the first chapter.

## Read text, outlines, and links in EPUB and DjVu

```csharp
PdfReadTextPage text = book.ReadTextPage(0);
Console.WriteLine(text.Text);
foreach (PdfReadTextRegion region in text.Regions)
    Console.WriteLine($"{region.Start}, {region.Length}: {region.Bounds}");

foreach (PdfReadNavigationEntry entry in book.ReadNavigation())
    Console.WriteLine($"{entry.Label}: {entry.Target}");

foreach (PdfReadLinkRegion link in book.ReadLinkRegions(0))
    Console.WriteLine($"{link.Target}: {link.Bounds}");
```

Text regions map a UTF-16 range of the page text to bounds in points, with the origin at the lower left, before the page's intrinsic rotation. `ReadPageText` returns a page's text without its regions. For EPUB it reads from the book's page index and never lays the chapter out again.

| Capability | EPUB | DjVu |
| --- | --- | --- |
| Metadata | Title, identifier, language, and creator | Page size, resolution, and orientation |
| Page text | Yes, with regions | Yes, with regions when the page has located text |
| Outline | EPUB 3 navigation document, with the NCX as a fallback, or the EPUB 2 NCX | Bookmarks stored in the document |
| Link regions | Yes, for linked text | No. `ReadLinkRegions` throws `NotSupportedException` |

`ReadNavigation` returns each outline entry's label, an opaque target string, and its children. Nothing is fetched or executed. `ResolveNavigationTarget` turns a target into a page index and a position:

- An EPUB target is a package-relative chapter path with an optional fragment, such as `EPUB/chapter12.xhtml#start`. Targets and fragments are case-sensitive.
- A DjVu target must begin with `#` and name a page by component ID, component name, component title, one-based page number, or a relative offset such as `+2` or `-1`. A relative offset needs the `currentPageIndex` argument. External resources and viewer commands throw `NotSupportedException`.

A DjVu destination always has a left and top of zero. A DjVu page can carry link and highlight annotations, but the engine does not read them as link regions and they do not change the rendered pixels.

## Render native pages and regions

```csharp
using KillerPdf.Engine.Rendering;

var render = new PdfRenderOptions(794, 1123);
PdfRenderedFrame frame = book.RenderPage(0, render);
byte[] bgra = frame.ToSrgbBgra32();

PdfRenderedFrame visible = book.RenderPageRegion(0, render, left: 100, top: 200, width: 256, height: 256);
PdfRenderedFrame natural = scan.RenderPage(0);
```

Native rendering returns a `PdfRenderedFrame`, the same float32 frame type as the precision renderer for PDF. `RenderPage(pageIndex)` draws a page at its natural size with any intrinsic rotation applied. The overload that takes `PdfRenderOptions` draws it at an exact pixel size. EPUB text is not reflowed for that size, and its glyph outlines are scaled.

`RenderPageRegion` draws the pixel rectangle at `left` and `top` of a page drawn at the full size in the options. It returns exactly that crop, and only the region counts against the storage limits. A viewer can use it to draw the visible part of a page sharply at any zoom. A DjVu page is decoded once into its own compact form, and only the requested pixels are resampled from it, so 300 and 600 dpi scans render whole, resized, and by region.

Native renderers accept `PdfRenderOptions.Proof` and apply it as described in the proofing section below. They refuse `SeparationPlate` with `NotSupportedException`.

## Convert EPUB and DjVu to PDF

Conversion is always explicit. Opening a document never produces a PDF.

```csharp
byte[] epubPdf = epub.ConvertToPdf(new EpubPdfConversionOptions(font)
{
    PageWidthPoints = 612,
    PageHeightPoints = 792,
    MarginPoints = 54,
    IsolateChapterFailures = true,
    DiagnosticReported = notice => Console.WriteLine($"{notice.Reason}: {notice.Message}")
});

var notices = new List<DjvuDiagnostic>();
byte[] djvuPdf = djvu.ConvertToPdf(notices);
foreach (DjvuDiagnostic notice in notices)
    Console.WriteLine($"{notice.PageIndex}: {notice.Code}");
```

EPUB conversion takes paper size, margin, and fallback font size in points, and defaults to A4 paper. It keeps selectable text in the fonts the layout used, local and web links, PNG and JPEG images, solid backgrounds, and a bookmark hierarchy from the book's outline. Paper must be 3 to 14,400 points on each side, and `EpubPdfConversionOptions.FitsPdfPageLimit` checks that before you convert. `DiagnosticReported` receives each chapter, resource, or navigation item the conversion left out.

DjVu conversion handles bitonal, grayscale, color photo, and compound pages. It writes located page text as an invisible, selectable layer and local bookmarks as PDF bookmarks. Color photo pages convert without loss unless you set `DjvuPdfConversionOptions.JpegEncoder` to a `PdfJpegEncoder` from `KillerPdf.Engine.Writing`, and `JpegQuality` (1 through 100, default 98) then applies. DjVu conversion does not write links.

## What the native readers ignore, replace, or refuse

**EPUB** ignores some content, replaces some with a notice, and refuses the rest:

- CSS declarations and rules the reader does not implement are ignored. `PdfReadSnapshot.IgnoredDeclarations` counts them for each chapter.
- A single image or SVG drawing that the reader cannot use is left out, and the chapter still lays out. The omission appears in `PdfReadSnapshot.ContentDiagnostics` with an `EpubOmissionReason` and the item's package path. Only PNG and JPEG images draw. A PNG or JPEG with a color profile other than sRGB, an image that is too large or damaged, and an SVG that is more than a static cover are left out. A link to a target that is not in the book stays inert.
- A character that no available font maps draws the font's missing-glyph box and adds a content diagnostic.
- A chapter whose content is refused ends layout and conversion with an exception. Set `IsolateChapterFailures` in `EpubLayoutOptions` or `EpubPdfConversionOptions` to replace that chapter with one page that states `UnavailableChapterLabel`, the chapter's path, and the reason, and the rest of the book lays out. `PdfReadSnapshot.ChapterDiagnostics` lists each replaced chapter. The reasons include encryption, scripting, text that needs shaping or bidirectional processing the reader does not implement, layout constructs it does not implement, safety limits, and invalid markup.
- Package problems always refuse the book: a first ZIP entry that is not an uncompressed `mimetype` entry holding `application/epub+zip`, a missing container or package document, a package version other than 2 or 3, unsafe or ambiguous resource paths, and any exceeded package limit.
- A package file larger than 8 MiB expanded, or one that cannot be read, is left out, and conversion reports it through `DiagnosticReported`. A chapter file in that state is refused like any other refused chapter.

**DjVu** reports what it cannot do through stable notice codes:

- `DjvuDocument.Diagnostics` and `PdfReadSnapshot.DjvuDiagnostics` list notices from opening, such as `ExternalComponentsUnsupported`, `IncludedComponentUnsupported`, `PageVersionUnsupported`, `ImageDecodingUnsupported`, and `UnrecognizedChunkSkipped`. Components outside a bundled document are never fetched.
- Text that is not valid UTF-8 reads with one replacement character for each unreadable sequence, and a `TextEncodingInvalid` notice names the page. `PdfReadTextPage.Diagnostics` carries it.
- A page the renderer cannot draw throws `NotSupportedException`. This covers pages that use JPEG layers, striped MMR masks, or a page version newer than 0.26. Embedded thumbnails are not decoded.
- During conversion, a page that cannot be converted becomes a 612 by 792 point page that states `Page N could not be converted` and the reason, and a `PageNotConverted` notice with the page index is added. Page positions stay aligned with the source.
- During conversion, page text that cannot be placed is left out with a `TextNotConverted` notice and the page image is kept. Characters the text layer cannot represent are written as replacement characters with a `TextCharactersReplaced` notice. A bookmark that names no page is left out with a `BookmarkNotConverted` notice, and its children move up a level.
- A container with no local pages, such as an indirect multi-file document, makes `PdfReadSnapshot.OpenDjvu` throw `NotSupportedException`. A file that is not a DjVu container, or whose structure is invalid, throws `InvalidDataException`.

Full format coverage remains in progress for both readers. Treat each diagnostic as a statement about the source file, and check the diagnostics before you rely on a conversion.

## Native format limits

The readers enforce these limits in code and refuse or report input that exceeds them. An EPUB resource larger than 8 MiB expanded is left out instead of refusing the book.

| Limit | EPUB | DjVu |
| --- | --- | --- |
| Input size | 128 MiB package, and 256 MiB expanded in total | 64 MiB |
| Container entries | 4,096 ZIP entries, 4,096 manifest items, and 4,096 spine items | Not applicable |
| Page size | 32,768 CSS pixels on a side, and at most 4,194,304 pixels | 100 million pixels per page |
| Pages | 10,000 per book | 4,096 per document |
| Text | 16,000,000 laid-out characters per book | 4 MiB and 65,536 located zones per page |
| Outline | 16,384 entries, 32 levels deep | 1,024 bookmarks, 32 levels deep |
| Navigation target | 4,096 characters | 4,096 characters |

`EpubLayoutOptions.FitsPageLimits` checks a page size, margin, and font size before you lay out. `EpubLayoutOptions.TableFlowWorkLimit` sets the per-chapter table work budget from 1 through 16,000,000, and the default is 1,000,000. A DjVu page whose resolution is outside 25 to 6,000 dpi is read as 300 dpi. `PdfReadSnapshot.OpenDjvu` refuses input over 64 MiB, and `DjvuDocument.RenderPage` at natural size is limited to a 128 MiB float frame, which is 8,388,608 pixels. Render a larger page with a `PdfRenderOptions` size or by region instead.

## Render with float32 precision and color management

`PdfPageRenderer.RenderFrame` and `RenderFrameRegion` render a page into a `PdfRenderedFrame`: tightly packed float32 RGBA with straight alpha and a top-left origin. RGB channels are relative to document white at one, with no implied absolute luminance, and finite values outside zero through one are kept until you convert the frame for output.

```csharp
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;

var renderer = new PdfPageRenderer(document);
PdfRenderedFrame frame = renderer.RenderFrame(0, new PdfRenderOptions(1024, 768));

byte[] srgb = frame.ToSrgbBgra32();

PdfColorProfile monitor = PdfColorProfile.Load(File.ReadAllBytes("monitor.icc"));
byte[] display = frame.ToDisplayBgra32(monitor);

Half[] half = frame.ToPremultipliedLinearHalfRgba();
```

A frame converts to sRGB bytes, to bytes in an RGB monitor profile, or to premultiplied linear half-precision values. `ToDisplayBgra32` needs a monitor profile with an actual relative colorimetric reverse transform. Its output channels are monitor device values, not sRGB, so use it only when your application owns monitor conversion.

The float renderer does not approximate. A page that uses a feature it cannot draw with full precision throws `NotSupportedException` instead of returning quantized pixels, so catch that exception and fall back to `PdfPageRenderer.Render` when a bounded BGRA32 image is enough.

Color in float frames follows these rules:

- Untagged DeviceCMYK converts through the render's working CMYK profile. Tagged CMYK, a resource `DefaultCMYK`, and a usable document output intent take priority over it.
- Float frames and printer proofs simulate overprint. A page of unprofiled DeviceCMYK fills and strokes keeps native ink plates for overprint modes 0 and 1, opacity, and proofing. A printer proof of any other overprint page composes its inks, spot plates included, and converts the composed ink. A float render that is not a proof refuses other overprinted paint.

## Proof for a printer

Soft proofing shows how a page looks on a printer by converting each color through the printer's ICC profile. Set `PdfRenderOptions.Proof` and render a float frame.

```csharp
using KillerPdf.Engine.Authoring;

PdfColorProfile printer = PdfColorProfile.Load(File.ReadAllBytes("printer.icc"));
var settings = new PdfDocumentProofSettings(
    enabled: true,
    printerProfile: printer,
    options: new PdfProofOptions
    {
        RenderingIntent = PdfRenderingIntent.RelativeColorimetric,
        BlackPointCompensation = true,
        SimulatePaper = false,
        SimulateBlack = false
    },
    gamutWarnings: true);

var proofOptions = new PdfRenderOptions(1024, 768) { Proof = settings };
PdfRenderedFrame proof = renderer.RenderFrame(0, proofOptions);
```

`PdfProofOptions` selects the conversion:

- `RenderingIntent` is `Perceptual`, `RelativeColorimetric`, `Saturation`, or `AbsoluteColorimetric`. The default is `RelativeColorimetric`.
- `BlackPointCompensation` defaults to true.
- `SimulatePaper` simulates the printer's paper. It defaults to false.
- `SimulateBlack` preserves the printer's black instead of normalizing it. It defaults to false. With `SimulatePaper` on and `SimulateBlack` off, the proof keeps the paper tint and maps printer black to display black. That is a viewing aid and not an ICC rendering intent.

A proof request that the engine cannot honor throws `NotSupportedException`. The printer profile must be an output (`prtr`) profile with actual colorimetric tables in both directions. The selected intent needs an actual inverse table, not a fallback to another intent. Absolute colorimetric and paper simulation also need a profile with a valid media white point. Proofing is refused when `ForPrinting` is set, and byte rendering does not apply it. EPUB and DjVu frames from `PdfReadSnapshot` accept the same proof settings.

## Warn about out-of-gamut colors

With `gamutWarnings` on, the frame carries a separate `PdfGamutWarningMask` that classifies each pixel as `Inside`, `Boundary`, `Outside`, or `NotEvaluated` without changing the color pixels. A fully transparent pixel is not evaluated.

```csharp
PdfGamutWarningMask? mask = proof.PrinterGamutWarnings;
Console.WriteLine(mask?.WarningPixelCount);

PdfRenderedFrame marked = proof.WithGamutWarningsMarked();
byte[] pixels = marked.ToSrgbBgra32();
```

`WithGamutWarningsMarked` returns a copy with the `Outside` pixels painted flat neutral gray (sRGB 128 of 255). It keeps alpha, every other pixel, and the mask, and it returns the same frame when nothing is outside. The marked copy is for viewing and must not be used as output. The check measures the printer tables' own round-trip error, so a table-based CMYK profile does not warn about colors it can print, and it follows the proof's black-point compensation. `PdfPrinterGamutCheck` classifies individual D50 XYZ colors against a printer profile.

`PdfProofPreset` captures a proof's choices as a named, immutable preset that refers to the profile by its SHA256 content digest. It does not contain the profile. `ToJson` and `FromJson` save and load it, and `ResolveProfile` combines a preset with a profile you load, which must match the digest. `PdfLogicalDocument.Proof` holds the proof settings of a document, and `PdfDocumentProofView.ComparisonOverride` lets one view compare with different settings.

## Choose the working CMYK profile

Untagged DeviceCMYK needs an assumed profile. The engine bundles `CGATS001Compat-v2-micro`, a CC0 profile for CGATS TR 001 (North American web printing). `PdfCmykViewingDefault.LoadProfile` returns an owned copy of it. This is an assumption for viewing and is not a printer profile selection.

```csharp
PdfColorProfile workingCmyk = PdfColorProfile.Load(File.ReadAllBytes("working-cmyk.icc"));
var options = new PdfRenderOptions(1024, 768) { WorkingCmykProfile = workingCmyk };
PdfRenderedFrame frame = renderer.RenderFrame(0, options);

Console.WriteLine(frame.AssumedCmykProfileSha256);
foreach (string note in frame.Diagnostics)
    Console.WriteLine(note);
```

The replacement must be a CMYK profile with a perceptual or relative colorimetric forward table, or the property setter throws. The renderer uses the perceptual table when the profile has one. A frame reports the SHA256 of the profile it assumed in `AssumedCmykProfileSha256`, which is null when the page painted no untagged CMYK, and describes the assumption in `Diagnostics`.

`PdfColorProfile.Load` reads ICC version 2 and 4 input, display, output, and color-space profiles of up to 32 MiB without touching external resources. `GetTransformSelection` reports which table an intent and direction will use, and whether it falls back to another intent. `ToXyz`, `FromXyz`, and `ConvertTo` convert color components through D50 XYZ.

## Know the color limits

HDR output and physical color accuracy are still in progress. The engine does not claim either. A float frame is relative to document white, and its numbers are not measured luminance. `ToPremultipliedLinearHalfRgba` copies a linear frame to half-precision values for a host that presents them. It does not make a render HDR.

## Know the boundary

KillerPDF 1.9 uses The KillerPDF.Engine for rendering, text extraction, and OCR. Tesseract remains available in the desktop app as a fallback OCR provider. See the developer guide for current engine APIs.
