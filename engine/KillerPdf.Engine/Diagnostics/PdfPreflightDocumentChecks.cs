using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Diagnostics;

internal static class PdfPreflightDocumentChecks
{
    private static readonly PdfName MediaBoxName = Name("MediaBox");
    private static readonly PdfName CropBoxName = Name("CropBox");
    private static readonly PdfName OutputIntentsName = Name("OutputIntents");

    internal static IReadOnlyList<PdfPreflightFinding> CheckPageBoxes(PdfDocument document)
    {
        var findings = new List<PdfPreflightFinding>();
        PdfPageTree tree;
        try
        {
            tree = PdfPageTree.Read(document);
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            return [Error("PageBoxes.InvalidPageTree", error.Message)];
        }
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            try
            {
                PdfBox media = Box(document, page, MediaBoxName, required: true)
                    ?? throw new InvalidOperationException("A page has no media box.");
                PdfBox crop = Box(document, page, CropBoxName, required: false) ?? media;
                if (crop.Left < media.Left || crop.Bottom < media.Bottom
                    || crop.Right > media.Right || crop.Top > media.Top)
                    findings.Add(Error("PageBoxes.CropOutsideMediaBox",
                        "The crop box extends outside the media box.", page.Index,
                        page.Reference.ObjectNumber));
            }
            catch (Exception error) when (IsDocumentFailure(error))
            {
                findings.Add(Error("PageBoxes.InvalidBox", error.Message,
                    page.Index, page.Reference.ObjectNumber));
            }
        }
        return Array.AsReadOnly(findings.ToArray());
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckOutputIntent(PdfDocument document)
    {
        try
        {
            PdfPageTree tree = PdfPageTree.Read(document);
            if (!tree.Catalog.TryGetValue(OutputIntentsName, out PdfObject? value))
                return [Error("OutputIntent.Missing", "The document has no output intent.")];
            PdfArray intents = Resolve(document, value) as PdfArray
                ?? throw new InvalidOperationException("The catalog output intents value is not an array.");
            if (intents.Count == 0)
                return [Error("OutputIntent.Empty", "The document has no output intent.")];
            var findings = new List<PdfPreflightFinding>();
            foreach (PdfObject item in intents)
            {
                PdfIndirectReference? reference = item as PdfIndirectReference;
                PdfDictionary intent = Resolve(document, item) as PdfDictionary
                    ?? throw new InvalidOperationException("An output intent is not a dictionary.");
                if (!intent.TryGetValue(Name("DestOutputProfile"), out PdfObject? profileValue)
                    || Resolve(document, profileValue) is not PdfStream profile
                    || !profile.Dictionary.ContainsKey(Name("N")))
                    findings.Add(Error("OutputIntent.MissingProfile",
                        "An output intent has no usable destination ICC profile.",
                        objectNumber: reference?.ObjectNumber));
            }
            return Array.AsReadOnly(findings.ToArray());
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            return [Error("OutputIntent.Invalid", error.Message)];
        }
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckImageResolution(
        PdfDocument document, double minimumDpi)
    {
        var findings = new List<PdfPreflightFinding>();
        IReadOnlyList<PdfPageContentBatchResult> pages = PdfPageContentBatch.Read(document);
        foreach (PdfPageContentBatchResult page in pages)
        {
            if (!page.Succeeded)
            {
                findings.Add(Error("ImageResolution.PageUnreadable",
                    page.Error ?? "The page content could not be read.", page.PageIndex));
                continue;
            }
            foreach (PdfExtractedImage image in page.Content!.Images)
            {
                if (image.HorizontalDpi is not double horizontal
                    || image.VerticalDpi is not double vertical)
                {
                    findings.Add(Warning("ImageResolution.Unknown",
                        "An image's effective resolution could not be determined.", page.PageIndex));
                    continue;
                }
                if (horizontal < minimumDpi || vertical < minimumDpi)
                    findings.Add(Warning("ImageResolution.BelowMinimum",
                        $"An image is {horizontal:0.#} by {vertical:0.#} DPI; the minimum is {minimumDpi:0.#} DPI.",
                        page.PageIndex));
            }
        }
        return Array.AsReadOnly(findings.ToArray());
    }

    private static PdfBox? Box(PdfDocument document, PdfPageTreeEntry page,
        PdfName name, bool required)
    {
        if (!page.InheritedValues.TryGetValue(name, out PdfObject? value))
        {
            if (required) throw new InvalidOperationException("A page has no media box.");
            return null;
        }
        PdfArray array = Resolve(document, value) as PdfArray
            ?? throw new InvalidOperationException("A page box is not an array.");
        if (array.Count != 4) throw new InvalidOperationException("A page box must have four coordinates.");
        double left = Number(document, array[0]);
        double bottom = Number(document, array[1]);
        double right = Number(document, array[2]);
        double top = Number(document, array[3]);
        if (right <= left || top <= bottom)
            throw new InvalidOperationException("A page box has zero or negative size.");
        return new PdfBox(left, bottom, right, top);
    }

    private static double Number(PdfDocument document, PdfObject value) =>
        Resolve(document, value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real when double.IsFinite(real.Value) => real.Value,
            _ => throw new InvalidOperationException("A page-box coordinate is not a finite number.")
        };

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A preflight reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static bool IsDocumentFailure(Exception error) =>
        error is ArgumentException or InvalidOperationException or FormatException
            or NotSupportedException or OverflowException;

    private static PdfPreflightFinding Error(string code, string message,
        int? pageIndex = null, int? objectNumber = null) =>
        new(code, PdfDiagnosticSeverity.Error, message, pageIndex, objectNumber);

    private static PdfPreflightFinding Warning(string code, string message,
        int? pageIndex = null, int? objectNumber = null) =>
        new(code, PdfDiagnosticSeverity.Warning, message, pageIndex, objectNumber);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private sealed record PdfBox(double Left, double Bottom, double Right, double Top);
}
