# KillerPDF.Engine

KillerPDF.Engine is a .NET library for reading, rendering, creating, editing, redacting, signing and validating PDF files. It's the engine behind [KillerPDF](https://killerpdf.net), and it has no UI dependencies, so you can drop it into a web service, a console tool or your own desktop app.

It's written from scratch in C#, with no wrapper around a native PDF library. It also opens EPUB and DjVu files, does OCR, and exports to Word, Excel, PowerPoint, HTML and Markdown.

## Why KillerPDF.Engine

Most free .NET PDF libraries do one job. PdfPig reads text, and its renderer is a separate add-on that's still early. QuestPDF and PDFsharp create documents but don't render them. PDFtoImage and Docnet render through Google's native PDFium code. If you need to read, render, edit and sign the same file, you usually end up stitching several of them together.

The libraries that do everything either cost money or come with strings attached. iText is AGPL unless you buy a commercial license, and its redaction add-on, pdfSweep, is licensed separately. QuestPDF's current releases are source-available and free only for companies under $1M in annual revenue. Aspose and Syncfusion are commercial products.

KillerPDF.Engine does all of it in one package under the Mozilla Public License 2.0. You can use it for free in commercial and closed-source software. The only condition is that if you distribute changes to the engine's own files, you share those changes.

It also does a few things the others don't:

- **Real redaction.** It removes the text, images and drawings under the box, then reads the file back to check that nothing survived. It doesn't just paint a black rectangle over text that's still there.
- **Built-in OCR.** It runs its own recognition models, so there's no Tesseract install and no cloud service.
- **Safe with hostile files.** Parsing has hard limits, so a damaged or malicious file stops with a clear diagnostic instead of hanging your app or being silently rewritten.
- **Edits that keep the original.** Incremental saves leave the original bytes untouched, which keeps existing signatures verifiable.
- **Current standards.** It supports PDF 2.0, PDF/A-4 and PDF/UA-2 out of the box.
- **Proven in a real app.** KillerPDF uses it for every page it opens, renders, edits and saves.

## Features

**Reading**

- Parses the whole file format, including classic and compressed cross-reference tables, object streams and incremental revisions
- Text with word, line and letter positions, font details and image placements, including nested forms and inline images
- Table and layout extraction, and search across page text, annotations and form values

**Rendering**

- Renders pages to BGRA pixels with clipping, both fill rules, transparency, forms, images, annotations and form fields
- Float32 rendering, ICC color management, monitor profiles and a bundled CMYK profile
- Printer proofing with all four rendering intents, black-point compensation, paper simulation and gamut warnings
- Color separations

**Creating**

- PDF 2.0 documents with text, standard and embedded TrueType fonts, images, color spaces, shadings, patterns and transparency
- Bookmarks, named destinations, links, page labels, transitions, viewer preferences, layers and attachments
- Tagged PDF, PDF/UA-2, PDF/A-4, PDF/A-4e and PDF/A-4f

**Editing**

- Incremental updates that keep the original bytes, or clean deterministic rewrites
- Insert, import, remove, reorder, rotate, crop and resize pages, and set page boxes and labels
- Edit paragraphs and lines of text in place
- Move, resize, delete and extract placed images, and select and extract pixels from a rendered page
- Headers, footers, Bates numbering, n-up and booklet imposition, measurements, macros and structural comparison

**Annotations and forms**

- Notes, replies, popups, text markup, shapes, stamps, free text and file attachments
- AcroForm text fields, check boxes, radio buttons, choice lists, buttons and signature fields
- Field detection, data merge and flattening
- FDF and XFDF import and export
- XFA inspection, editing, calculation, validation and conversion to AcroForm

**Security**

- Content-level redaction with sensitive-data search and a read-back check
- RC4, AES-128, AES-256 and AES-GCM passwords, certificate encryption and crypt filters
- Detached CMS signatures, certification, field locks, seed values and RFC 3161 timestamps
- Signature verification with trust and revocation checks, PAdES evidence and signed-revision analysis

**Conversion and checks**

- OCR with its own recognition models, with results in PDF coordinates
- Export to plain text, HTML, Markdown, JSON, Word, Excel, PowerPoint and EPUB, with a report of anything that couldn't carry over
- EPUB and DjVu reading, rendering, text, outlines and conversion to PDF (in development)
- Preflight, accessibility and print-production reports with previewed fixes
- Repair plans, lossless optimization and recovery of damaged files

## Install

The engine targets .NET 10.

```powershell
dotnet add package KillerPdf.Engine
```

JPEG 2000 images are decoded with OpenJPEG 2.5.4, which the package bundles for Windows x64, Linux x64, macOS x64 and macOS ARM64. On Windows it needs the Microsoft Visual C++ runtime.

## Quick start

```csharp
using KillerPdf.Engine.Authoring;

var content = new PdfContentStreamBuilder()
    .BeginText()
    .SetFont(PdfStandardFont.HelveticaBold, 20)
    .MoveText(72, 712)
    .ShowLatin1Text("Hello from KillerPDF.Engine")
    .EndText();

byte[] pdf = new PdfDocumentBuilder()
    .SetMetadata(new PdfDocumentMetadata { Title = "Hello", Language = "en-US" })
    .AddPage(612, 792, content)
    .Build();

File.WriteAllBytes("hello.pdf", pdf);
```

## Read text

Every page gives you its words, lines and letters, with positions in PDF points. Page numbers start at zero.

```csharp
using KillerPdf.Engine.Documents;

PdfDocument document = PdfDocument.Open(File.ReadAllBytes("input.pdf"));
var reader = new PdfPageContentReader(document);

for (int i = 0; i < reader.PageCount; i++)
    foreach (PdfExtractedWord word in reader.Read(i).Words)
        Console.WriteLine($"{word.Text}: {word.BoundingBox}");
```

Password-protected files open with `PdfDocument.Open(bytes, password)`.

## Render pages

The renderer draws a page to BGRA pixels, including annotations and form fields if you want them.

```csharp
using KillerPdf.Engine.Rendering;

var renderer = new PdfPageRenderer(document);
var options = new PdfRenderOptions(1024, 768, includeAnnotations: true, includeFormFields: true);
PdfRenderedPage page = renderer.Render(0, options, CancellationToken.None);
// page.Width, page.Height and page.Pixels
```

It supports ICC color management, monitor profiles and printer proofing.

## Create documents

`PdfDocumentBuilder` builds new PDF 2.0 files. It covers pages, text, embedded TrueType fonts, images, shapes, bookmarks, links, form fields, attachments and tagged structure.

```csharp
using KillerPdf.Engine.Fonts;

TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes("fonts/Example.ttf"));
var content = new PdfContentStreamBuilder()
    .BeginText()
    .SetFont(font, 18)
    .MoveText(72, 720)
    .ShowUnicodeText("Temperature: 23 °C")
    .EndText();

byte[] pdf = new PdfDocumentBuilder().AddPage(612, 792, content).Build();
```

## Edit pages

`PdfIncrementalPageEditor` moves, rotates, removes and imports pages. It saves as an incremental update, so the original bytes stay intact.

```csharp
using KillerPdf.Engine.Editing;

byte[] edited = new PdfIncrementalPageEditor(document)
    .MovePage(0, 2)
    .RotateClockwise(2)
    .AddImportedPage(otherDocument, 0)
    .Build();
```

You can also add text or drawings on top of an existing page with `AppendPageContent`.

## Bookmarks and links

```csharp
byte[] pdf = new PdfDocumentBuilder()
    .AddBlankPage().AddBlankPage()
    .AddBookmark("Overview", 0)
    .AddPageLink(0, 36, 700, 120, 24, 1, destination: PdfDestination.FitPage())
    .AddUriLink(0, 36, 660, 120, 24, "https://example.com/")
    .Build();
```

`PdfBookmarkReader` and `PdfLinkReader` read them back from existing files.

## Comments and annotations

```csharp
byte[] reviewed = new PdfIncrementalAnnotationEditor(document)
    .AddTextNote(0, 72, 680, "Check the heading", name: "heading-review")
    .AddTextNote(0, 100, 680, "Fixed", name: "reply", inReplyTo: "heading-review")
    .Build();
```

`PdfCommentReader` lists every comment in a file, and `PdfCommentEditor` changes or removes them.

## Forms

The engine reads and fills AcroForm fields, imports and exports FDF and XFDF, and flattens forms. XFA forms can be inspected, edited and converted to AcroForm.

```csharp
PdfFormDataSet values = PdfFormDataExporter.Export(document, includeAnnotations: false);
byte[] xfdf = PdfXfdfFormData.Write(values);
```

## Redaction

Redaction actually removes the covered text, images and drawings from the file instead of just painting a black box over them. Then it reads the result back to make sure nothing survived.

```csharp
PdfRedactionReview review = PdfRedactionSearch.Find(pages, new PdfRedactionSearchOptions
{
    Kind = PdfRedactionSearchKind.RegularExpression,
    Query = @"\bACCT-\d{6}\b",
    ReasonCode = "ACCOUNT"
}, cancellationToken);
```

You review the matches, then `PdfContentRedaction.Apply` removes them.

## Passwords and encryption

The engine supports RC4, AES-128, AES-256 and AES-GCM passwords, as well as certificate encryption.

```csharp
using KillerPdf.Engine.Security;

byte[] pdf = new PdfDocumentBuilder()
    .SetPasswordEncryption(new PdfPasswordEncryptionOptions
    {
        UserPassword = "open-me",
        OwnerPassword = "owner",
        AllowContentCopying = false
    })
    .AddBlankPage(612, 792)
    .Build();
```

## Digital signatures

You supply the CMS signature, from a certificate, a smart card or a cloud key, and the engine places it in the file.

```csharp
using System.Security.Cryptography.Pkcs;
using KillerPdf.Engine.Signing;

byte[] signed = PdfDetachedSignatureWriter.Sign(document, data =>
{
    var cms = new SignedCms(new ContentInfo(data.ToArray()), detached: true);
    cms.ComputeSignature(new CmsSigner(certificate));
    return cms.Encode();
}, new PdfSignatureOptions { Reason = "Approved" });
```

`PdfSignatureInspection.Inspect` checks existing signatures for integrity, trust, revocation and later changes. It also supports timestamps, certification and field locks.

## OCR

The engine has its own OCR. It needs no Tesseract install, and its results come back in PDF coordinates so you can make scanned pages searchable.

```csharp
PdfOcrRecognitionModelFiles.TryCreateCatalog(modelDirectory, "eng", out var models);
var recognizer = new PdfOcrPageRecognizer(document, models!);
PdfOcrPageRecognition result = recognizer.Recognize(0,
    new PdfRenderOptions(2550, 3300), new PdfOcrOptions(["eng"]), cancellationToken);
```

## Export

```csharp
string markdown = PdfStructuredExport.ToMarkdown(document);
string html = PdfStructuredExport.ToHtml(document);
byte[] docx = PdfStructuredExport.ToDocx(document);
```

The engine also exports plain text, JSON, Excel and PowerPoint, and every export can report what it couldn't carry over. Tables can be pulled out on their own with `PdfTableExtractor`.

## Accessibility and standards

The engine creates tagged PDF and supports PDF/A-4 and PDF/UA-2. `PdfAccessibilityInspector` checks an existing file, and `PdfPreflightRunner` runs preflight profiles with previewed corrections.

## Repair and optimize

`PdfOptimizer` builds a plan that shows exactly what it will compress or remove before you apply it. Damaged files with broken cross-references, page trees or streams can be recovered.

## EPUB and DjVu

```csharp
using KillerPdf.Engine.Formats.Djvu;

byte[] pdf = DjvuDocument.Open(File.ReadAllBytes("scan.djvu")).ConvertToPdf();
```

EPUB works the same way through `EpubDocument`. Both formats can also be rendered and read directly without converting. This support is still in development.

## Documentation

- Engine documentation: https://killerpdf.net/engine/
- Changelog: https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/CHANGELOG.md
- Source: https://github.com/SteveTheKiller/KillerPDF

## License

KillerPDF.Engine is licensed under the [Mozilla Public License 2.0](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/LICENSE). Bundled font metrics and character maps keep their original licenses, which are listed in [THIRD-PARTY-NOTICES.txt](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/THIRD-PARTY-NOTICES.txt).
