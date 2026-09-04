using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Presentation settings applied by a conforming viewer when a PDF opens.</summary>
public sealed record PdfInitialView
{
    /// <summary>Gets the requested page arrangement, or null when the viewer chooses its default.</summary>
    public PdfPageLayout? PageLayout { get; init; }
    /// <summary>Gets the requested navigation panel, or null when the viewer chooses its default.</summary>
    public PdfPageMode? PageMode { get; init; }
    /// <summary>Gets the document viewer preferences.</summary>
    public PdfViewerPreferences ViewerPreferences { get; init; } = new();
    /// <summary>Gets the zero-based page opened by the document action, if it targets a page directly.</summary>
    public int? PageIndex { get; init; }
    /// <summary>Gets the opening destination, if the document action targets a page directly.</summary>
    public PdfDestination? Destination { get; init; }
    /// <summary>Gets the named opening destination, if one is used.</summary>
    public string? NamedDestination { get; init; }

    /// <summary>Applies the complete initial-view selection in one incremental revision.</summary>
    public byte[] Apply(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (ViewerPreferences is null)
            throw new InvalidOperationException("Viewer preferences are required.");
        if (NamedDestination is not null
            && (PageIndex is not null || Destination is not null))
            throw new InvalidOperationException(
                "A named opening destination cannot be combined with a page destination.");
        if ((PageIndex is null) != (Destination is null))
            throw new InvalidOperationException(
                "An opening page and destination must be supplied together.");

        var editor = new PdfIncrementalPageEditor(document);
        if (PageLayout is PdfPageLayout layout) editor.SetPageLayout(layout);
        else editor.ClearPageLayout();
        if (PageMode is PdfPageMode mode) editor.SetPageMode(mode);
        else editor.ClearPageMode();
        editor.SetViewerPreferences(ViewerPreferences);
        if (NamedDestination is not null) editor.SetNamedOpenAction(NamedDestination);
        else if (PageIndex is int pageIndex)
            editor.SetOpenAction(pageIndex, Destination!);
        else editor.ClearOpenAction();
        return editor.Build();
    }

    internal static PdfInitialView Read(PdfDocument document, PdfPageTree tree)
    {
        PdfDictionary catalog = tree.Catalog;
        PdfPageLayout? layout = ReadEnum<PdfPageLayout>(document, catalog, "PageLayout",
            ("SinglePage", PdfPageLayout.SinglePage), ("OneColumn", PdfPageLayout.OneColumn),
            ("TwoColumnLeft", PdfPageLayout.TwoColumnLeft), ("TwoColumnRight", PdfPageLayout.TwoColumnRight),
            ("TwoPageLeft", PdfPageLayout.TwoPageLeft), ("TwoPageRight", PdfPageLayout.TwoPageRight));
        PdfPageMode? mode = ReadEnum<PdfPageMode>(document, catalog, "PageMode",
            ("UseNone", PdfPageMode.UseNone), ("UseOutlines", PdfPageMode.UseOutlines),
            ("UseThumbs", PdfPageMode.UseThumbs), ("FullScreen", PdfPageMode.FullScreen),
            ("UseOC", PdfPageMode.UseOptionalContent), ("UseAttachments", PdfPageMode.UseAttachments));
        PdfViewerPreferences preferences = ReadPreferences(document, catalog);
        (int? pageIndex, PdfDestination? destination, string? namedDestination) =
            ReadOpenAction(document, tree);
        return new PdfInitialView
        {
            PageLayout = layout,
            PageMode = mode,
            ViewerPreferences = preferences,
            PageIndex = pageIndex,
            Destination = destination,
            NamedDestination = namedDestination
        };
    }

    private static PdfViewerPreferences ReadPreferences(PdfDocument document, PdfDictionary catalog)
    {
        if (!TryValue(document, catalog, "ViewerPreferences", out PdfObject? value)) return new();
        PdfDictionary dictionary = value as PdfDictionary
            ?? throw new InvalidOperationException("The catalog /ViewerPreferences value is not a dictionary.");
        return new PdfViewerPreferences
        {
            HideToolbar = Boolean(document, dictionary, "HideToolbar"),
            HideMenuBar = Boolean(document, dictionary, "HideMenubar"),
            HideWindowUi = Boolean(document, dictionary, "HideWindowUI"),
            FitWindow = Boolean(document, dictionary, "FitWindow"),
            CenterWindow = Boolean(document, dictionary, "CenterWindow"),
            DisplayDocumentTitle = Boolean(document, dictionary, "DisplayDocTitle"),
            PickTrayByPdfSize = Boolean(document, dictionary, "PickTrayByPDFSize"),
            ReadingDirection = Name(document, dictionary, "Direction") == "R2L"
                ? PdfReadingDirection.RightToLeft : PdfReadingDirection.LeftToRight,
            PrintScaling = Name(document, dictionary, "PrintScaling") == "None"
                ? PdfPrintScaling.None : PdfPrintScaling.ApplicationDefault,
            Duplex = Name(document, dictionary, "Duplex") switch
            {
                "Simplex" => PdfDuplexMode.Simplex,
                "DuplexFlipShortEdge" => PdfDuplexMode.DuplexFlipShortEdge,
                "DuplexFlipLongEdge" => PdfDuplexMode.DuplexFlipLongEdge,
                _ => PdfDuplexMode.Default
            }
        };
    }

    private static (int?, PdfDestination?, string?) ReadOpenAction(PdfDocument document, PdfPageTree tree)
    {
        if (!TryValue(document, tree.Catalog, "OpenAction", out PdfObject? value)) return (null, null, null);
        if (value is PdfString text)
            return (null, null, PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "The catalog /OpenAction value"));
        if (value is PdfName name) return (null, null, name.ValueAsLatin1());
        if (value is not PdfArray array || array.Count < 2) return (null, null, null);
        PdfIndirectReference? pageReference = array[0] as PdfIndirectReference;
        PdfName? kindName = Resolve(document, array[1]) as PdfName;
        if (pageReference is null || kindName is null) return (null, null, null);
        int pageIndex = tree.Pages.ToList().FindIndex(page =>
            page.Reference.ObjectNumber == pageReference.ObjectNumber
            && page.Reference.Generation == pageReference.Generation);
        if (pageIndex < 0) return (null, null, null);
        double? Number(int index) => index >= array.Count ? null : Resolve(document, array[index]) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            PdfNull => null,
            _ => null
        };
        PdfDestination? destination = kindName.ValueAsLatin1() switch
        {
            "XYZ" => PdfDestination.At(Number(2), Number(3), Number(4)),
            "Fit" => PdfDestination.FitPage(),
            "FitH" => PdfDestination.FitWidth(Number(2)),
            "FitV" => PdfDestination.FitHeight(Number(2)),
            "FitR" when Number(2) is double left && Number(3) is double bottom
                && Number(4) is double right && Number(5) is double top
                => PdfDestination.FitRectangle(left, bottom, right, top),
            "FitB" => PdfDestination.FitBoundingBox(),
            "FitBH" => PdfDestination.FitBoundingBoxWidth(Number(2)),
            "FitBV" => PdfDestination.FitBoundingBoxHeight(Number(2)),
            _ => null
        };
        return (pageIndex, destination, null);
    }

    private static T? ReadEnum<T>(PdfDocument document, PdfDictionary dictionary, string key,
        params (string Name, T Value)[] values) where T : struct
    {
        string? name = Name(document, dictionary, key);
        foreach ((string candidate, T result) in values)
            if (name == candidate) return result;
        return null;
    }

    private static bool Boolean(PdfDocument document, PdfDictionary dictionary, string key) =>
        TryValue(document, dictionary, key, out PdfObject? value) && value is PdfBoolean boolean && boolean.Value;

    private static string? Name(PdfDocument document, PdfDictionary dictionary, string key) =>
        TryValue(document, dictionary, key, out PdfObject? value) && value is PdfName name
            ? name.ValueAsLatin1() : null;

    private static bool TryValue(PdfDocument document, PdfDictionary dictionary, string key, out PdfObject? value)
    {
        if (!dictionary.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)), out value)) return false;
        value = Resolve(document, value);
        return true;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An initial-view reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }
}
