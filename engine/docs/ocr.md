# OCR integration and review

The engine can render PDF pages, preprocess pixels, recognize words with local
models, and return reviewable text and bounds. Recognition is separate from
writing searchable text. The host selects models, languages, rendering settings,
review policy, and output destinations. These examples target the 1.9 source.

## Load models and recognize a page

Install the required `.kpocr` files in a host-selected directory before calling
this example. For `eng`, the loader expects `eng.kpocr`. Model files are inputs
to the library; this API does not download or train them automatically.
Open and authenticate the document before constructing the recognizer.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

static PdfOcrPageRecognition RecognizeEnglish(PdfDocument document,
    string modelDirectory, int pageIndex, int pixelWidth, int pixelHeight,
    CancellationToken cancellationToken)
{
    if (!PdfOcrRecognitionModelFiles.TryCreateCatalog(modelDirectory, "eng",
        out PdfOcrRecognitionModelCatalog? models))
        throw new InvalidOperationException("The English OCR model could not be loaded.");
    var recognizer = new PdfOcrPageRecognizer(document, models!);
    return recognizer.Recognize(pageIndex,
        new PdfRenderOptions(pixelWidth, pixelHeight),
        new PdfOcrOptions(["eng"]), cancellationToken);
}
```

Supply dimensions matching the displayed page's aspect ratio. Larger renders
can retain smaller text but increase work and memory. Page indexes start at zero.
Reuse the loaded catalog and recognizer when processing more pages of the same
immutable document. Keep the returned diagnostics with the review results.

`TryCreateCatalog` accepts a plus-separated language expression, such as
`eng+spa`, and requires every requested model. `PdfOcrOptions.Languages` takes
separate identifiers, such as `["eng", "spa"]`. The catalog selects and combines
requested language models. A missing or invalid model fails loading rather than
silently substituting another language. Serialized recognition models are limited
to 256 MiB each and validated before use.

Optional `.kplm` language-context files can be loaded through
`TryLoadLanguageCombined` and passed to the recognizer's language-model overload.
Character context can resolve ambiguities, but it cannot establish that a guessed
word matches the source image.

## Options and coordinates

`PdfOcrOptions` defaults to searchable-image output, deskewing, orientation
correction, and page-segment detection. Background removal and noise removal
default to false. Select preprocessing against representative inputs; aggressive
cleanup can remove thin characters or punctuation.

| Result | Coordinates |
| --- | --- |
| `PdfOcrResult.Words` from a raster provider | Pixel bounds with a top-left origin |
| `PdfOcrPageRecognition.Review.Words` | PDF bounds mapped back through page geometry and preprocessing |

`PixelWidth` and `PixelHeight` record the rendered dimensions. Word and mean
confidence values range from zero to one. Confidence is an estimate, not a
measured probability that a particular word is correct. Inspect text, placement,
reading order, and diagnostics together.

The output-mode setting describes the requested workflow. Calling `Recognize`
returns recognition data; it does not write a PDF or make page text editable.
Choose the appropriate output writer after review.

## Review and persist corrections

```csharp
using KillerPdf.Engine.Documents;

static PdfOcrReview CorrectWord(PdfOcrReview review, string wordId,
    string replacement)
{
    return review.Correct(wordId, replacement);
}
```

Reviews are immutable. Assign the returned review after `Correct`, `ReplaceAll`,
`Ignore`, or `AcceptConfidentWords`. `GetLowConfidenceWords(threshold)` selects
pending words at or below the threshold. `AcceptConfidentWords(threshold)` accepts
pending words at or above it. Set thresholds from validation data and the host's
review policy rather than using them as universal accuracy guarantees.

`Ignore` means accept a word without correction. It retains that word in text
exports and searchable output. `WriteSearchableText` does not automatically omit
pending words, so complete the host's review decision before writing.

`ExportJson` and `ImportJson` preserve review state. Retain a document identity
alongside the review so results are not applied to another file or page order.
`ExportText(pageCount)` retains page boundaries. `CreateAccuracyReport` summarizes
confidence and review status; its name does not mean recognition was compared
against ground-truth text.

## Write reviewed searchable text

Supply an embeddable TrueType font that covers the reviewed characters. This
writer appends invisible text over the existing pages. It retains the source
page content and does not convert OCR words into visible editable replacements.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;

static byte[] WriteReviewedText(PdfDocument document, PdfOcrReview acceptedReview,
    TrueTypeFont font)
{
    return acceptedReview.WriteSearchableText(document, font);
}
```

Reopen the returned bytes, extract the added text, and compare rendered output
with the source. Validate word placement on rotated and cropped pages as part
of the host's integration. Existing text remains, so the host should avoid adding
a duplicate OCR layer to pages that already have adequate searchable text.
Tagged-document, authentication, and editing guards still apply.

For raster-provider results, `PdfOcrSearchableTextWriter.Write` accepts one
`PdfOcrPixelPage` per PDF page and a per-word font resolver. Pixel bounds must fit
the corresponding rendered page. A null font skips that word; inspect
`WrittenWords` rather than assuming every recognition was written. If no words
can be written, handle that case before requesting an empty incremental update.

## Select a local provider

`IPdfOcrRasterProvider` accepts BGRA pixels, width, height, and row stride.
Pass the actual stride; it need not equal `width * 4`. Keep the pixel memory
available for the duration of recognition. The contract supports cancellation
and an optional character whitelist.

`PdfEngineOcrProvider` wraps a recognition-model catalog and optional language
context. `PdfOnnxOcrProvider` wraps a host-supplied session factory; each call
creates and disposes an isolated session. ONNX model descriptions and runtime
sessions are separate from `.kpocr` models.

`PdfOcrProviderSelector.Select` considers installed languages and supported
options. Automatic selection chooses the highest priority, breaking ties by
provider ID. Explicit selection requires that provider to be installed and
capable; it does not silently fall back. Provider selection itself does not retry
a failed recognition call. The desktop app's Tesseract fallback is a host policy,
not an automatic dependency of the engine page recognizer.

## Validate and train models

`PdfOcrModelTrainer` creates training samples and models and evaluates labeled
samples. `PdfOcrTrainingPartition` supports document-level holdout assignment.
Keep related pages and derivatives in the same partition to avoid testing on
variants of training material.

Evaluate held-out character and word accuracy, confusion pairs, confidence
calibration, word boxes, reading order, and form layout. The engine exposes
text, word-box, reading-order, and form metrics for these separate checks.
Track model hashes, renderer build, preprocessing, languages, and corpus identity.
Rendering changes can change model inputs even when the same documents are used.

Do not replace shipped models based only on training accuracy or a passing
integration example. Compare candidate models with the current models and host
fallback on held-out data before changing the selected model pack.
For expensive batches, use per-file isolation and timeouts as well as cancellation.
Keep failed, canceled, and unprocessed pages distinct from successful recognition.
