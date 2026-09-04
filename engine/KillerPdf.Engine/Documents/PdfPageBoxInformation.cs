using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Describes one rectangular PDF page boundary in points.</summary>
public sealed record PdfPageBoxBounds(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Gets the box width.</summary>
    public double Width => Right - Left;
    /// <summary>Gets the box height.</summary>
    public double Height => Top - Bottom;
}

/// <summary>Describes the complete effective box model for one PDF page.</summary>
public sealed record PdfPageBoxInformation
{
    /// <summary>Gets the zero-based page index.</summary>
    public required int PageIndex { get; init; }
    /// <summary>Gets the required physical page boundary.</summary>
    public required PdfPageBoxBounds MediaBox { get; init; }
    /// <summary>Gets the visible page boundary, defaulting to the media box.</summary>
    public required PdfPageBoxBounds CropBox { get; init; }
    /// <summary>Gets the bleed boundary, defaulting to the crop box.</summary>
    public required PdfPageBoxBounds BleedBox { get; init; }
    /// <summary>Gets the intended finished-page boundary, defaulting to the crop box.</summary>
    public required PdfPageBoxBounds TrimBox { get; init; }
    /// <summary>Gets the meaningful-content boundary, defaulting to the crop box.</summary>
    public required PdfPageBoxBounds ArtBox { get; init; }
    /// <summary>Gets whether the page explicitly declares a crop box.</summary>
    public bool HasExplicitCropBox { get; init; }
    /// <summary>Gets whether the page explicitly declares a bleed box.</summary>
    public bool HasExplicitBleedBox { get; init; }
    /// <summary>Gets whether the page explicitly declares a trim box.</summary>
    public bool HasExplicitTrimBox { get; init; }
    /// <summary>Gets whether the page explicitly declares an art box.</summary>
    public bool HasExplicitArtBox { get; init; }

    /// <summary>Reads the effective box model for every page.</summary>
    public static IReadOnlyList<PdfPageBoxInformation> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        var result = new PdfPageBoxInformation[tree.Pages.Count];
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            PdfPageBoxBounds media = ReadInherited(document, page, "MediaBox")
                ?? throw new InvalidOperationException(
                    $"Page {page.Index + 1} has no effective media box.");
            PdfPageBoxBounds crop = ReadInherited(document, page, "CropBox") ?? media;
            PdfPageBoxBounds bleed = ReadDirect(document, page, "BleedBox") ?? crop;
            PdfPageBoxBounds trim = ReadDirect(document, page, "TrimBox") ?? crop;
            PdfPageBoxBounds art = ReadDirect(document, page, "ArtBox") ?? crop;
            result[page.Index] = new PdfPageBoxInformation
            {
                PageIndex = page.Index,
                MediaBox = media,
                CropBox = crop,
                BleedBox = bleed,
                TrimBox = trim,
                ArtBox = art,
                HasExplicitCropBox = page.Dictionary.ContainsKey(Name("CropBox")),
                HasExplicitBleedBox = page.Dictionary.ContainsKey(Name("BleedBox")),
                HasExplicitTrimBox = page.Dictionary.ContainsKey(Name("TrimBox")),
                HasExplicitArtBox = page.Dictionary.ContainsKey(Name("ArtBox"))
            };
        }
        return Array.AsReadOnly(result);
    }

    private static PdfPageBoxBounds? ReadInherited(
        PdfDocument document, PdfPageTreeEntry page, string key) =>
        page.InheritedValues.TryGetValue(Name(key), out PdfObject? value)
            ? ReadBox(document, value, page.Index, key) : null;

    private static PdfPageBoxBounds? ReadDirect(
        PdfDocument document, PdfPageTreeEntry page, string key) =>
        page.Dictionary.TryGetValue(Name(key), out PdfObject? value)
            ? ReadBox(document, value, page.Index, key) : null;

    private static PdfPageBoxBounds ReadBox(
        PdfDocument document, PdfObject value, int pageIndex, string key)
    {
        PdfArray array = Resolve(document, value, pageIndex, key) as PdfArray
            ?? throw new InvalidOperationException(
                $"Page {pageIndex + 1} /{key} is not an array.");
        if (array.Count != 4)
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} /{key} does not contain four coordinates.");
        double left = Number(document, array[0], pageIndex, key);
        double bottom = Number(document, array[1], pageIndex, key);
        double right = Number(document, array[2], pageIndex, key);
        double top = Number(document, array[3], pageIndex, key);
        if (right <= left || top <= bottom)
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} /{key} has zero or negative size.");
        return new PdfPageBoxBounds(left, bottom, right, top);
    }

    private static double Number(PdfDocument document, PdfObject value,
        int pageIndex, string key) => Resolve(document, value, pageIndex, key) switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real when double.IsFinite(real.Value) => real.Value,
        _ => throw new InvalidOperationException(
            $"Page {pageIndex + 1} /{key} contains a nonnumeric coordinate.")
    };

    private static PdfObject Resolve(PdfDocument document, PdfObject value,
        int pageIndex, string key)
    {
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException(
                    $"Page {pageIndex + 1} /{key} has an invalid reference chain.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
