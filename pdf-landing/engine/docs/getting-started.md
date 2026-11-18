# Getting started

Install The KillerPDF.Engine, create a PDF 2.0 document, and reopen it in less than five minutes.

## Requirements

- .NET 10 SDK
- A .NET console, desktop, web, or service project
- Windows, Linux, or macOS for the engine itself

JPEG 2000 images decode with OpenJPEG. The package bundles OpenJPEG 2.5.4 for Windows x64, where it needs the Microsoft Visual C++ runtime. On other platforms, JPEG 2000 decoding needs a compatible OpenJPEG library that the .NET native loader can find as `openjp2`. Without one, decoding a JPEG 2000 stream throws `PdfFilterException`.

The library is UI-free. Your application chooses its own renderer, interface, storage, and deployment model.

## Install from NuGet

Run this in your project directory:

```powershell
dotnet add package KillerPdf.Engine
```

When working from a KillerPDF repository checkout, use a project reference instead:

```xml
<ProjectReference Include="path\to\KillerPDF\engine\KillerPdf.Engine\KillerPdf.Engine.csproj" />
```

## Create your first document

```csharp
using KillerPdf.Engine.Authoring;

byte[] pdf = new PdfDocumentBuilder()
    .SetMetadata(new PdfDocumentMetadata
    {
        Title = "Hello from The KillerPDF.Engine",
        Author = "Example application",
        Language = "en-US"
    })
    .AddBlankPage(612, 792)
    .Build();

File.WriteAllBytes("hello.pdf", pdf);
```

Page dimensions use PDF points. A US Letter page is 612 by 792 points.

## Open and inspect it

```csharp
using KillerPdf.Engine.Documents;

byte[] source = File.ReadAllBytes("hello.pdf");
PdfDocument document = PdfDocument.Open(source);
PdfDocumentInformation information = PdfDocumentInformation.Read(document);

Console.WriteLine(information.Title);
Console.WriteLine($"{information.PageCount} page(s)");
Console.WriteLine($"PDF {information.Version}");
```

## Rewrite it deterministically

```csharp
using KillerPdf.Engine.Writing;

byte[] rewritten = PdfDocumentWriter.Write(document);
File.WriteAllBytes("hello-rewritten.pdf", rewritten);
```

Use a deterministic rewrite when reproducible output and a normalized object graph matter. Use the incremental editors when preserving the original byte prefix matters.

## Choose the next guide

- [Reading PDFs](reading-pdfs.html) covers metadata, pages, links, bookmarks, and forms.
- [Creating PDFs](creating-pdfs.html) covers pages, content streams, metadata, and modern standards.
- [Editing PDFs](editing-pdfs.html) covers byte-preserving changes to existing files.
- [Validation](validation.html) covers structural inspection and independent validators.
