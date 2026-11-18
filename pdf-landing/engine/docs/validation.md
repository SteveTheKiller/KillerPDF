# Validation

Validation is part of the engine design, not a label added after writing. Use structural inspection before editing, round-trip validation for deterministic rewrites, and independent tools for standards claims.

## Inspect unfamiliar input

```csharp
using KillerPdf.Engine.Diagnostics;

PdfInspectionReport report = PdfDocumentInspector.Inspect(source);

if (!report.IsStructurallyValid)
{
    foreach (PdfDiagnostic diagnostic in report.Diagnostics)
        Console.WriteLine($"{diagnostic.Severity}: {diagnostic.Message}");
}
```

Inspection is bounded and non-throwing for structural failures. It checks the header, revision chain, cross-reference data, indirect objects, catalog, object limits, and authentication state.

## Validate a deterministic rewrite

```csharp
using KillerPdf.Engine.Validation;

PdfRoundTripResult result = PdfRoundTripValidator.Validate(source);
if (!result.Succeeded)
    throw new InvalidOperationException(result.FailureMessage);

File.WriteAllBytes("validated.pdf", result.RewrittenBytes!);
```

The round-trip validator inspects the source, rewrites it, reopens the result, writes it a second time, and compares the two outputs. Use `ValidateAuthenticated` for password-protected input.

## Run preflight and report checks

```csharp
using KillerPdf.Engine.Diagnostics;

PdfPreflightReport report = PdfPreflightRunner.Run(
    source, PdfPreflightProfile.PrintProduction);

if (!report.Passed || !report.Complete)
    Console.WriteLine(report.ToText());
```

A preflight profile selects checks for structure, metadata, language, tagging, forms, links, page boxes, output intents, images, fonts, transparency, color and ink limits, layers, and declared PDF/A, PDF/UA, and PDF/X boundaries. Reports identify source objects and checks that could not run. `PdfAccessibilityInspector` and `PdfPrintProductionReport` produce focused reports, and correction plans preview a change before you apply it. A clean report is not certification.

## Check PDF/UA-1 with the Matterhorn Protocol

```csharp
using KillerPdf.Engine.Diagnostics;

PdfUaReport report = PdfUaValidator.Validate(document);

Console.WriteLine(report.Verdict);
foreach (PdfUaFailure failure in report.Failures)
    Console.WriteLine($"{failure.Condition} {failure.Clause} page {failure.PageIndex}: {failure.Message}");
```

`PdfUaValidator` checks a document against PDF/UA-1 (ISO 14289-1) and returns one result for each of the 31 Matterhorn Protocol 1.1 checkpoints. A checkpoint is `Passed`, `Failed`, `Incomplete`, or `NeedsHumanReview`. Each failure carries its Matterhorn condition ID, such as 06-003 for a missing title, the ISO clause, and the page and object when they apply. Conditions a person must judge, and machine conditions the validator does not check yet, are listed by ID on each checkpoint.

The verdict is `Failed`, `Incomplete`, or `PassedMachineChecks`. It never claims full conformance, because part of the protocol needs a person. A PDF/UA-2 document is checked against the same checkpoints, and `DeclaredPart` records which part it claims. `ToText` and `ToJson` give the same report shapes as the accessibility inspector. Preflight runs this validator on any document whose XMP metadata declares PDF/UA-1.

## Understand what this proves

| Check | What it establishes |
| --- | --- |
| Structural inspection | Required PDF structure can be resolved within explicit limits |
| Reopen after writing | The engine can parse the output it produced |
| Second deterministic write | Equivalent input produces identical normalized output |
| Independent validation | A separate implementation accepts the claimed structure or profile |

> A successful engine round trip does not replace veraPDF, qpdf, a signature verifier, or application-specific acceptance tests.

## Validation

For exact source and test counts, see the Overview. Corpus results identify the tested build and inputs. qpdf, veraPDF and OpenSSL provide separate structural, standards and signature checks.

[Overview](index.html) | [Corpus](../corpus.html)

## Validate the profile you claim

Use veraPDF for PDF/A and PDF/UA profiles, qpdf for general structural checks, and a cryptographic implementation independent of the engine for signature interoperability. Keep the exact validator version and profile in your build or release record.
