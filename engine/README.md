# The KillerPDF.Engine

The KillerPDF.Engine is an independent .NET library for reading, rendering, editing, redacting, signing and writing PDF files. It's the engine behind [KillerPDF](https://killerpdf.net), and it has no UI dependencies, so you can use it in your own applications.

It also handles forms (including XFA), OCR, export to Office, HTML and Markdown, PDF/A and PDF/UA validation, ICC color management, and reading EPUB and DjVu files.

## Install

The engine targets .NET 10.

```powershell
dotnet add package KillerPdf.Engine
```

JPEG 2000 decoding uses OpenJPEG 2.5.4, which the package bundles for Windows x64, Linux x64, macOS x64 and macOS ARM64. On Windows it needs the Microsoft Visual C++ runtime.

## Quick start

Create a PDF:

```csharp
using KillerPdf.Engine.Authoring;

byte[] pdf = new PdfDocumentBuilder()
    .SetMetadata(new PdfDocumentMetadata { Title = "Hello", Language = "en-US" })
    .AddBlankPage(612, 792)
    .Build();

File.WriteAllBytes("hello.pdf", pdf);
```

Extract the words from a page:

```csharp
using KillerPdf.Engine.Documents;

PdfDocument document = PdfDocument.Open(File.ReadAllBytes("input.pdf"), string.Empty);
PdfPageContent page = new PdfPageContentReader(document).Read(0);
foreach (PdfExtractedWord word in page.Words)
    Console.WriteLine($"{word.Text}: {word.BoundingBox}");
```

## Documentation

- Engine documentation: https://killerpdf.net/engine/
- Changelog: https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/CHANGELOG.md
- Source: https://github.com/SteveTheKiller/KillerPDF

## License

The KillerPDF.Engine is licensed under the [Mozilla Public License 2.0](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/LICENSE). Bundled font metrics and character maps keep their original licenses, listed in [THIRD-PARTY-NOTICES.txt](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/THIRD-PARTY-NOTICES.txt), which is also included in the package.
